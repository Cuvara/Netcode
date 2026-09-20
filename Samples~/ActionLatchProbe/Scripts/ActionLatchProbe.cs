using UnityEngine;
using UnityEngine.UIElements;

namespace Cuvara.Netcode.Samples.ActionLatchProbe
{
    /// <summary>
    /// Drives <see cref="ActionLatchModel"/> at the configured critical rate and renders
    /// both arms side by side.
    ///
    /// <para>The MonoBehaviour owns nothing but the clock and the widgets. Everything that
    /// could be wrong about the demonstration lives in the model, which is plain C# and
    /// runs without Unity — so it can be, and was, executed headless before this scene
    /// existed.</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ActionLatchProbe : MonoBehaviour
    {
        [SerializeField] private int criticalHz = 60;
        [SerializeField] private int worldHz = 15;
        [SerializeField] private int attackEveryTicks = 30;
        [SerializeField] private bool autoRun = true;

        private readonly ActionLatchModel _model = new ActionLatchModel();

        private double _accumulator;
        private bool _running;

        private Label _scheduleHint, _verdict;
        private Label _aIssued, _aRendered, _aLost, _aState;
        private Label _bIssued, _bRendered, _bLost, _bState;
        private Button _run;

        private void OnEnable()
        {
            _running = autoRun;

            VisualElement root = GetComponent<UIDocument>().rootVisualElement;

            var critical = root.Q<SliderInt>("critical-hz");
            var world = root.Q<SliderInt>("world-hz");
            var attack = root.Q<SliderInt>("attack-every");

            critical.value = criticalHz;
            world.value = worldHz;
            attack.value = attackEveryTicks;

            critical.RegisterValueChangedCallback(e => { criticalHz = e.newValue; Reconfigure(); });
            world.RegisterValueChangedCallback(e => { worldHz = e.newValue; Reconfigure(); });
            attack.RegisterValueChangedCallback(e => { attackEveryTicks = e.newValue; Reconfigure(); });

            _run = root.Q<Button>("run");
            _run.clicked += () => { _running = !_running; _run.text = _running ? "Pause" : "Run"; };
            _run.text = _running ? "Pause" : "Run";

            // Steps a whole attack interval, because stepping one base tick shows nothing:
            // the interesting event is one tick in thirty.
            root.Q<Button>("step").clicked += () =>
            {
                for (int i = 0; i < attackEveryTicks + WorldEvery(); i++) _model.Step();
                Render();
            };
            root.Q<Button>("reset").clicked += () => { _model.Reset(); Render(); };

            _scheduleHint = root.Q<Label>("schedule-hint");
            _verdict = root.Q<Label>("verdict");
            _aIssued = root.Q<Label>("a-issued");
            _aRendered = root.Q<Label>("a-rendered");
            _aLost = root.Q<Label>("a-lost");
            _aState = root.Q<Label>("a-state");
            _bIssued = root.Q<Label>("b-issued");
            _bRendered = root.Q<Label>("b-rendered");
            _bLost = root.Q<Label>("b-lost");
            _bState = root.Q<Label>("b-state");

            Reconfigure();
        }

        /// <summary>
        /// Base ticks per world tick, clamped to at least 1 and floored like the server's
        /// integer timeline. A world rate that does not divide the critical rate is a
        /// STARTUP FAILURE on the real server (SimulationRates.TryCreate), not something it
        /// rounds — the hint says so rather than the scene silently demonstrating a
        /// configuration the server would refuse to boot with.
        /// </summary>
        private int WorldEvery()
        {
            int every = worldHz <= 0 ? 1 : criticalHz / worldHz;
            return every < 1 ? 1 : every;
        }

        private void Reconfigure()
        {
            int every = WorldEvery();
            _model.Reconfigure(every, attackEveryTicks);

            bool divides = worldHz > 0 && criticalHz % worldHz == 0 && worldHz <= criticalHz;
            _scheduleHint.text = divides
                ? $"WorldEvery = {criticalHz} / {worldHz} = {every}. One base tick in {every} is " +
                  $"observable by any client, so without a latch that is the fraction of " +
                  $"instantaneous actions that can reach one."
                : $"{worldHz} does not divide {criticalHz} exactly. The real server REFUSES TO " +
                  $"START on this (SimulationRates.TryCreate); the probe floors it to " +
                  $"{every} so the sliders stay usable.";

            Render();
        }

        private void Update()
        {
            if (!_running) return;

            _accumulator += Time.deltaTime * criticalHz;
            int steps = (int)_accumulator;
            if (steps <= 0) return;

            // Bounded: a long editor stall must not turn into a thousand-tick catch-up
            // burst inside one frame. Same reasoning as the server's MaxLagTicks.
            if (steps > 240) steps = 240;
            _accumulator -= steps;

            for (int i = 0; i < steps; i++) _model.Step();
            Render();
        }

        private void Render()
        {
            ActionLatchArm a = _model.Arms[0];
            ActionLatchArm b = _model.Arms[1];

            _aIssued.text = a.AttacksIssued.ToString();
            _aRendered.text = a.SwingsRendered.ToString();
            _aLost.text = Lost(a);
            _aState.text = $"{a.LastSampledAction} / {a.LastSampledSeq}";

            _bIssued.text = b.AttacksIssued.ToString();
            _bRendered.text = b.SwingsRendered.ToString();
            _bLost.text = Lost(b);
            _bState.text = $"{b.LastSampledAction} / {b.LastSampledSeq}";

            Mark(_aLost, a.AttacksLost > 0);
            Mark(_bLost, b.AttacksLost > 0);

            _verdict.text = a.AttacksIssued == 0
                ? "Waiting for the first attack."
                : $"Same input, same snapshots ({a.SnapshotsReceived}), same bytes " +
                  $"({a.BytesSent}). Latched: {a.SwingsRendered} of {a.AttacksIssued} swings " +
                  $"reached the client. Unlatched: {b.SwingsRendered} of {b.AttacksIssued}.";
        }

        private static string Lost(ActionLatchArm arm) =>
            arm.AttacksIssued == 0
                ? "—"
                : $"{arm.AttacksLost} ({100.0 * arm.AttacksLost / arm.AttacksIssued:F0}%)";

        private static void Mark(Label label, bool bad) =>
            label.EnableInClassList("cuvara-probe__value--bad", bad);
    }
}
