using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Cuvara.Netcode.Samples.SendBudgetProbe
{
    /// <summary>
    /// Drives a synthetic crowd through the game server's per-connection downlink budget
    /// and puts the result on screen: bytes per snapshot against the cap, entities
    /// deferred, visible-entity count, and the two numbers that say whether deferral is
    /// safe — how stale the client is, and how many entity handles arrived unbound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this scene exists.</b> The budget shipped with tests that assert
    /// convergence and a design note that argues the priority order. Neither carries the
    /// thing an operator actually needs to judge it: what the trade FEELS like. "45 of 200
    /// entities deferred this tick" is a sentence; a crowd where distant players visibly
    /// update a beat late while the one under your cursor never does is the feature. Dial
    /// the crowd up until the cap bites and the trade is in front of you.
    /// </para>
    /// <para>
    /// <b>No server and no network.</b> <see cref="SyntheticCrowd"/> plays the world and
    /// <see cref="SendBudgetModel"/> plays the server's delta encoder, producing real
    /// <c>RpgMmo.Wire.V1.SnapshotMessage</c> objects whose sizes are measured, not
    /// estimated. <see cref="FakeClient"/> plays the receiving client and applies the
    /// handle rule from <c>wire.proto</c> strictly.
    /// </para>
    /// <para>
    /// <b>What to watch.</b> Push "entities" and "churn" up until "shed/tick" leaves zero.
    /// Bytes/tick flattens against the budget line; "stale" climbs to a plateau and stays
    /// there; "max deferral" rises and then stops, because the scheduler is strictly
    /// oldest-first and the wait is bounded by the size of the dirty set, not by how long
    /// the scene has been running. "handle errors" stays at 0 throughout — that is the
    /// invariant the whole design is built around, and it is on screen precisely because a
    /// check nobody reads is not a check.
    /// </para>
    /// <para>
    /// UI is UXML/UI Toolkit, like every sample scene in this package. There is no
    /// <c>OnGUI</c> and no uGUI canvas here on purpose: sample scenes are where a reader
    /// learns the intended pattern.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(UIDocument))]
    public sealed class SendBudgetProbe : MonoBehaviour
    {
        /// <summary>Snapshots per second. The server broadcasts on the world group at 15 Hz.</summary>
        private const float SnapshotHz = 15f;

        /// <summary>Bars kept in the byte-history strip (~4 s at 15 Hz).</summary>
        private const int HistoryLength = 64;

        [Header("Load")]
        [Tooltip("Entities inside the observer's AOI, including the observer itself.")]
        [Range(1, 400)]
        [SerializeField] private int entityCount = 60;

        [Tooltip("Fraction of the crowd that moves on any given tick.")]
        [Range(0f, 1f)]
        [SerializeField] private float churn = 1f;

        [Tooltip("How far a moving entity travels per tick, in world units.")]
        [Range(0.05f, 2f)]
        [SerializeField] private float speed = 0.4f;

        [Header("Server configuration")]
        [Tooltip("GAMESERVER_MAX_SNAPSHOT_BYTES. 0 disables the budget entirely.")]
        [Range(0, 8192)]
        [SerializeField] private int budgetBytes = 1024;

        [Tooltip("GAMESERVER_KEYFRAME_INTERVAL. Snapshots between keyframes.")]
        [Range(0, 120)]
        [SerializeField] private int keyframeInterval = 30;

        private readonly SyntheticCrowd _crowd = new SyntheticCrowd();
        private readonly SendBudgetModel _model = new SendBudgetModel();
        private readonly FakeClient _client = new FakeClient();
        private readonly int[] _byteHistory = new int[HistoryLength];
        private readonly VisualElement[] _bars = new VisualElement[HistoryLength];

        private float _accumulator;
        private ulong _tick;
        private int _historyHead;
        private long _bytesInWindow;
        private float _windowElapsed;
        private float _bytesPerSecond;
        private int _peakBytes;

        private Label _bytesLabel;
        private Label _rateLabel;
        private Label _visibleLabel;
        private Label _carriedLabel;
        private Label _shedLabel;
        private Label _ageLabel;
        private Label _staleLabel;
        private Label _handleErrorsLabel;
        private Label _budgetLabel;
        private Label _verdictLabel;
        private VisualElement _budgetLine;
        private VisualElement _histogram;

        private SliderInt _entitySlider;
        private Slider _churnSlider;
        private SliderInt _budgetSlider;

        private void OnEnable()
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;

            _bytesLabel = root.Q<Label>("bytes-value");
            _rateLabel = root.Q<Label>("rate-value");
            _visibleLabel = root.Q<Label>("visible-value");
            _carriedLabel = root.Q<Label>("carried-value");
            _shedLabel = root.Q<Label>("shed-value");
            _ageLabel = root.Q<Label>("age-value");
            _staleLabel = root.Q<Label>("stale-value");
            _handleErrorsLabel = root.Q<Label>("handle-errors-value");
            _budgetLabel = root.Q<Label>("budget-value");
            _verdictLabel = root.Q<Label>("verdict");
            _budgetLine = root.Q<VisualElement>("budget-line");
            _histogram = root.Q<VisualElement>("histogram");

            BuildHistogram();

            _entitySlider = root.Q<SliderInt>("entities");
            _churnSlider = root.Q<Slider>("churn");
            _budgetSlider = root.Q<SliderInt>("budget");

            if (_entitySlider != null)
            {
                _entitySlider.value = entityCount;
                _entitySlider.RegisterValueChangedCallback(e => entityCount = e.newValue);
            }
            if (_churnSlider != null)
            {
                _churnSlider.value = churn;
                _churnSlider.RegisterValueChangedCallback(e => churn = e.newValue);
            }
            if (_budgetSlider != null)
            {
                _budgetSlider.value = budgetBytes;
                _budgetSlider.RegisterValueChangedCallback(e =>
                {
                    budgetBytes = e.newValue;
                    _peakBytes = 0;
                });
            }

            Button resync = root.Q<Button>("resync");
            if (resync != null) resync.clicked += () => _model.RequestFull();

            Button reset = root.Q<Button>("reset");
            if (reset != null) reset.clicked += ResetProbe;

            _crowd.Resize(entityCount);
        }

        private void BuildHistogram()
        {
            if (_histogram == null) return;

            _histogram.Clear();
            for (int i = 0; i < HistoryLength; i++)
            {
                var bar = new VisualElement();
                bar.AddToClassList("cuvara-probe__bar");
                _bars[i] = bar;
                _histogram.Add(bar);
            }
        }

        private void ResetProbe()
        {
            _model.Reset();
            _client.Reset();
            Array.Clear(_byteHistory, 0, _byteHistory.Length);
            _historyHead = 0;
            _peakBytes = 0;
            _bytesInWindow = 0;
            _windowElapsed = 0f;
            _bytesPerSecond = 0f;
        }

        private void Update()
        {
            if (_crowd.Count != entityCount) _crowd.Resize(entityCount);

            // Fixed 15 Hz snapshot cadence driven off a render clock that is faster, which
            // is the real relationship: the server simulates and broadcasts on its own
            // tick and the client renders whenever it likes.
            _accumulator += Time.deltaTime;
            float step = 1f / SnapshotHz;
            int steps = 0;
            while (_accumulator >= step && steps < 4)
            {
                _accumulator -= step;
                steps++;
                Step();
            }

            _windowElapsed += Time.deltaTime;
            if (_windowElapsed >= 1f)
            {
                _bytesPerSecond = _bytesInWindow / _windowElapsed;
                _bytesInWindow = 0;
                _windowElapsed = 0f;
            }
        }

        private void Step()
        {
            _tick++;
            _crowd.Step(churn, speed);

            SendBudgetModel.TickResult result = _model.Tick(
                _tick, _crowd.Entities, _crowd.Count, budgetBytes, keyframeInterval,
                _crowd.ObserverX, _crowd.ObserverY, _crowd.SelfIndex, _client);

            _bytesInWindow += result.PayloadBytes;
            if (result.PayloadBytes > _peakBytes) _peakBytes = result.PayloadBytes;

            _byteHistory[_historyHead] = result.PayloadBytes;
            _historyHead = (_historyHead + 1) % HistoryLength;

            Render(result);
        }

        private void Render(in SendBudgetModel.TickResult result)
        {
            int stale = _client.CountStale(_crowd.Entities, _crowd.Count);

            SetText(_bytesLabel, result.PayloadBytes.ToString() + " B" + (result.Full ? "  (keyframe)" : string.Empty));
            SetText(_rateLabel, (_bytesPerSecond / 1024f).ToString("F1") + " KB/s");
            SetText(_visibleLabel, _crowd.Count.ToString());
            SetText(_carriedLabel, result.CarriedCount.ToString());
            SetText(_shedLabel, result.ShedCount.ToString()
                + (result.RemovalsDeferred > 0 ? "  (+" + result.RemovalsDeferred + " despawns)" : string.Empty));
            SetText(_ageLabel, result.MaxShedAge.ToString() + " ticks");
            SetText(_staleLabel, stale.ToString() + " / " + _crowd.Count);
            SetText(_handleErrorsLabel, _client.HandleErrors.ToString());
            SetText(_budgetLabel, budgetBytes == 0 ? "off" : budgetBytes.ToString() + " B");

            if (_handleErrorsLabel != null)
            {
                // The only number on this panel that is ever allowed to be red.
                _handleErrorsLabel.EnableInClassList("cuvara-probe__value--bad", _client.HandleErrors > 0);
            }

            if (_verdictLabel != null)
            {
                _verdictLabel.text = Verdict(in result, stale);
            }

            RenderHistogram();
        }

        private string Verdict(in SendBudgetModel.TickResult result, int stale)
        {
            if (_client.HandleErrors > 0)
            {
                return "A handle arrived with no binding. A real client would have to MsgResync — "
                     + "this is the desync the budget is designed to make impossible, so it is a bug.";
            }
            if (budgetBytes == 0)
            {
                return "Budget off. The snapshot is as big as the visible set makes it — this is what "
                     + "the AOI radius alone bounds, which is area rather than population.";
            }
            if (result.ShedCount == 0)
            {
                return "The budget is not biting: everything that changed fitted. This is the expected "
                     + "reading at any load the server is known to handle.";
            }
            return "Shedding. Bytes are capped, " + stale + " of " + _crowd.Count
                 + " entities are a beat behind, and nothing has been dropped — every deferred entity "
                 + "is re-offered on a later snapshot, oldest first.";
        }

        private void RenderHistogram()
        {
            if (_histogram == null) return;

            // Scale to whatever is largest, so the strip stays readable with the budget off
            // as well as on. The budget line moves with it rather than being fixed, which is
            // why it is drawn as a percentage of the same scale.
            int scale = Math.Max(_peakBytes, budgetBytes > 0 ? budgetBytes : 1);
            if (scale <= 0) scale = 1;

            for (int i = 0; i < HistoryLength; i++)
            {
                int index = (_historyHead + i) % HistoryLength;
                int bytes = _byteHistory[index];
                float fraction = Mathf.Clamp01(bytes / (float)scale);
                _bars[i].style.height = Length.Percent(fraction * 100f);
                _bars[i].EnableInClassList(
                    "cuvara-probe__bar--over", budgetBytes > 0 && bytes > budgetBytes);
            }

            if (_budgetLine != null)
            {
                _budgetLine.style.display = budgetBytes > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                _budgetLine.style.bottom = Length.Percent(
                    Mathf.Clamp01(budgetBytes / (float)scale) * 100f);
            }
        }

        private static void SetText(Label label, string text)
        {
            if (label != null) label.text = text;
        }
    }
}
