using System.Collections.Generic;
using Cuvara.Netcode.Prediction;
using Shared.GameLogic.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace Cuvara.Netcode.Samples.ClockSyncProbe
{
    /// <summary>
    /// Drives a synthetic snapshot-tick stream — no server, no network — through the clock
    /// stack that steers prediction: <see cref="SnapshotStalenessEstimator"/> fitting offset
    /// and rate, and <see cref="LocalMovePredictor.SteerToServerTick"/> following it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this scene exists.</b> The clock work shipped across 0.20.0–0.23.0 with
    /// changelog entries, documentation sections and seventeen tests on the estimator alone.
    /// Every one of those states the behaviour in numbers, and none of them carries the two
    /// facts a person setting a machine up actually needs to see: that a client whose clock
    /// disagrees with the server's by eight percent <i>converges anyway</i>, and what it
    /// looks like when the disagreement is past the clamp and the fit is refused instead.
    /// Both took a live two-machine investigation to learn the first time; this scene makes
    /// them a slider.
    /// </para>
    /// <para>
    /// <b>The dial is real.</b> The default skew, +110,000 ppm, is not an invented stress
    /// case: it is the measured ratio between the Windows performance counter and the Linux
    /// monotonic clock on the machine this package was developed on — the value that sat
    /// just past the original 0.90/1.10 clamp and silently disabled the fit for a whole
    /// session (0.23.0's headline fix). The refusal region on the slider starts where
    /// <see cref="SnapshotStalenessEstimator.MaximumSkew"/> puts it, so the boundary that
    /// was once invisible is a colour change.
    /// </para>
    /// <para>
    /// Everything runs on the real classes. The only synthetic parts are the two clocks —
    /// one advanced by <c>Time.unscaledDeltaTime</c>, the other scaled by the slider — and
    /// the delivery queue that stands in for a network.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ClockSyncProbe : MonoBehaviour
    {
        private const int BaseHz = 60;
        private const int SnapshotEvery = 4;
        private const double SnapshotIntervalServerSeconds = SnapshotEvery / (double)BaseHz;

        /// <summary>
        /// The measured ratio between this package's development machine and its server —
        /// the case that broke the original clamp. See the class remarks.
        /// </summary>
        private const float DefaultSkewPpm = 110_000f;

        // ── The two clocks ──
        //
        // clientSeconds is what every real component reads: it plays the role of the
        // monotonic clock the estimator samples with. serverSeconds advances slower or
        // faster by the configured skew — positive ppm means the CLIENT's clock runs fast,
        // which is what the development machine does, so per client second fewer server
        // seconds elapse.
        private double _clientSeconds;
        private double _serverSeconds;
        private long _serverTick = 1000;
        private double _nextSnapshotAtServerSeconds;

        /// <summary>Snapshots in flight: item one is the client time it arrives at.</summary>
        private readonly List<(double deliverAt, long tick)> _pending =
            new List<(double, long)>();

        private SnapshotStalenessEstimator _staleness;
        private LocalMovePredictor _predictor;
        private long _lastDeliveredTick;

        private float _skewPpm = DefaultSkewPpm;
        private float _jitterMs;
        private bool _paused;

        // One-shot perturbations, applied to the next frame and cleared.
        private float _pendingStallSeconds;
        private double _pendingServerStepSeconds;

        private System.Random _rng = new System.Random(12345);

        // ── UI ──
        private Slider _skewSlider;
        private Label _skewValue;
        private Slider _jitterSlider;
        private Label _jitterValue;
        private Toggle _pauseToggle;
        private Label _fitLine;
        private Label _steerLine;
        private Label _verdict;
        private VisualElement _measuredBar;
        private VisualElement _configuredMark;

        private void Awake()
        {
            Reset();

            var root = GetComponent<UIDocument>().rootVisualElement;

            _skewSlider = root.Q<Slider>("skew");
            _skewValue = root.Q<Label>("skew-value");
            _jitterSlider = root.Q<Slider>("jitter");
            _jitterValue = root.Q<Label>("jitter-value");
            _pauseToggle = root.Q<Toggle>("pause");
            _fitLine = root.Q<Label>("fit-line");
            _steerLine = root.Q<Label>("steer-line");
            _verdict = root.Q<Label>("verdict");
            _measuredBar = root.Q<VisualElement>("measured-bar");
            _configuredMark = root.Q<VisualElement>("configured-mark");

            // A Q<T> miss returns null and the scene then dies on first interaction with a
            // NullReferenceException that names nothing. Failing at startup with the element
            // name is the difference between a five-second fix and a debugging session.
            foreach (var (element, name) in new (object, string)[]
                     {
                         (_skewSlider, "skew"), (_skewValue, "skew-value"),
                         (_jitterSlider, "jitter"), (_jitterValue, "jitter-value"),
                         (_pauseToggle, "pause"), (_fitLine, "fit-line"),
                         (_steerLine, "steer-line"), (_verdict, "verdict"),
                         (_measuredBar, "measured-bar"), (_configuredMark, "configured-mark"),
                     })
            {
                if (element == null)
                {
                    Debug.LogError($"[ClockSyncProbe] UXML element '{name}' not found — " +
                                   "the view and the script have drifted apart.");
                    enabled = false;
                    return;
                }
            }

            _skewSlider.lowValue = -150_000f;
            _skewSlider.highValue = 150_000f;
            _skewSlider.value = _skewPpm;
            _skewSlider.RegisterValueChangedCallback(e => _skewPpm = e.newValue);

            _jitterSlider.lowValue = 0f;
            _jitterSlider.highValue = 100f;
            _jitterSlider.value = _jitterMs;
            _jitterSlider.RegisterValueChangedCallback(e => _jitterMs = e.newValue);

            _pauseToggle.RegisterValueChangedCallback(e => _paused = e.newValue);

            root.Q<Button>("stall").clicked += () => _pendingStallSeconds = 0.25f;
            root.Q<Button>("step-clock").clicked += () => _pendingServerStepSeconds = 5.0;
            root.Q<Button>("reset").clicked += Reset;
        }

        private void Reset()
        {
            _clientSeconds = 0;
            _serverSeconds = 0;
            _serverTick = 1000;
            _nextSnapshotAtServerSeconds = SnapshotIntervalServerSeconds;
            _pending.Clear();
            _lastDeliveredTick = 0;
            _pendingStallSeconds = 0f;
            _pendingServerStepSeconds = 0;
            _rng = new System.Random(12345);

            _staleness = new SnapshotStalenessEstimator();
            _predictor = new LocalMovePredictor(
                new PredictionSettings(BaseHz, 5f, MapBounds.Default));
            _predictor.Reconcile(Vec2.Zero, 0);
        }

        private void Update()
        {
            if (_paused)
            {
                return;
            }

            float dt = Time.unscaledDeltaTime;

            // A stalled frame is one long frame, exactly as a scene load or a debugger
            // produces one: the whole stall arrives as a single deltaTime. What the probe
            // shows is the predictor's catch-up clamp eating it instead of burst-advancing.
            if (_pendingStallSeconds > 0f)
            {
                dt += _pendingStallSeconds;
                _pendingStallSeconds = 0f;
            }

            _clientSeconds += dt;

            // Positive ppm = the client's clock runs fast, so fewer server seconds pass per
            // client second. This is the whole model: t_client = skew * t_server, the same
            // line the estimator fits.
            _serverSeconds += dt / (1.0 + _skewPpm / 1e6);

            // A stepped server clock — a restart on a different tick origin — jumps the
            // stream by whole seconds at once. Past two seconds of error the predictor's
            // steering gives up walking and resynchronises outright; this button shows that
            // HardResyncs is the counter that moves, not Snaps.
            if (_pendingServerStepSeconds > 0)
            {
                _serverSeconds += _pendingServerStepSeconds;
                _serverTick += (long)(_pendingServerStepSeconds * BaseHz);
                _pendingServerStepSeconds = 0;
            }

            while (_serverSeconds >= _nextSnapshotAtServerSeconds)
            {
                _serverTick += SnapshotEvery;
                _nextSnapshotAtServerSeconds += SnapshotIntervalServerSeconds;

                double jitter = _jitterMs > 0f ? _rng.NextDouble() * _jitterMs / 1000.0 : 0.0;
                _pending.Add((_clientSeconds + jitter, _serverTick));
            }

            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].deliverAt > _clientSeconds)
                {
                    continue;
                }

                long tick = _pending[i].tick;
                _pending.RemoveAt(i);

                if (tick <= _lastDeliveredTick)
                {
                    continue;   // jitter reordered it behind a newer one; a real client skips it too
                }

                _lastDeliveredTick = tick;

                // The same three calls WorldViewBinder makes per snapshot, in the same
                // order: sample the age, seed once, steer every time.
                _staleness.Sample(tick, _clientSeconds, BaseHz);
                _predictor.SeedBaseTick(tick);
                _predictor.SteerToServerTick(tick, TargetLeadTicks());
            }

            _predictor.Advance(dt);

            RenderReadout();
        }

        /// <summary>
        /// The binder's steering target, minus the round-trip term a synthetic stream does
        /// not have: the measured staleness when the fit is usable, one snapshot interval
        /// until then.
        /// </summary>
        private int TargetLeadTicks()
        {
            float lead = _staleness.IsUsable ? _staleness.StalenessTicks : SnapshotEvery;
            int ticks = Mathf.RoundToInt(lead);
            return Mathf.Clamp(ticks, 0, SnapshotEvery * 2);
        }

        private void RenderReadout()
        {
            _skewValue.text = $"{_skewPpm / 1000f:+0.0;-0.0} ×10³ ppm " +
                              $"({(1.0 + _skewPpm / 1e6):F4}×)";
            _jitterValue.text = $"{_jitterMs:F0} ms";

            _fitLine.text =
                $"measured {_staleness.SkewPpm / 1000.0:+0.0;-0.0} ×10³ ppm | " +
                $"staleness {_staleness.StalenessTicks:F2} t | " +
                $"baseline {_staleness.BaselineSeconds:F0} s | " +
                $"fits {_staleness.Fits} | refused {_staleness.FitsRefused}" +
                (_staleness.FitsRefused > 0
                    ? $" (last {_staleness.RefusedSkewPpm / 1000.0:+0.0;-0.0} ×10³ ppm)"
                    : string.Empty);

            long lead = _predictor.BaseTick - _lastDeliveredTick;
            _steerLine.text =
                $"predictor tick {_predictor.BaseTick} | snapshot tick {_lastDeliveredTick} | " +
                $"lead {lead} t | tick error {_predictor.TickError} t | " +
                $"hard resyncs {_predictor.HardResyncs} | " +
                $"clamped frames {_predictor.ClampedFrames}";

            // The bar maps the slider's range onto the panel; the mark is the configured
            // value, the fill is the measured one. Convergence is the fill reaching the
            // mark; a refused fit is the fill pinned at zero while the mark sits in the
            // refusal region — which is exactly what an invisible refusal looked like in
            // the field, minus the visibility.
            float half = _skewSlider.highValue;
            float measured = Mathf.Clamp((float)_staleness.SkewPpm, -half, half);
            _measuredBar.style.width = Length.Percent(50f + 50f * measured / half);
            _configuredMark.style.left = Length.Percent(50f + 50f * _skewPpm / half);

            bool pastClamp =
                1.0 + _skewPpm / 1e6 > SnapshotStalenessEstimator.MaximumSkew ||
                1.0 + _skewPpm / 1e6 < SnapshotStalenessEstimator.MinimumSkew;

            if (!_staleness.IsUsable)
            {
                _verdict.text = pastClamp && _staleness.FitsRefused > 0
                    ? "REFUSED — the configured ratio is outside MinimumSkew..MaximumSkew, " +
                      "and the refusal is now a counter instead of a silence"
                    : "warming up — the fit needs two epochs and a baseline";
                _verdict.EnableInClassList("cuvara-probe__verdict--bad", pastClamp);
            }
            else
            {
                double errPpm = System.Math.Abs(_staleness.SkewPpm - _skewPpm);
                _verdict.text = errPpm < 5000
                    ? $"CONVERGED — measured within {errPpm:F0} ppm of the dial; the steering " +
                      "is following a clock it has correctly characterised"
                    : "fitting — the baseline is still growing toward the dialled value";
                _verdict.EnableInClassList("cuvara-probe__verdict--bad", false);
            }
        }
    }
}
