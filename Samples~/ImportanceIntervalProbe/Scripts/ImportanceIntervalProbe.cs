using UnityEngine;
using UnityEngine.UIElements;

namespace Cuvara.Netcode.Samples.ImportanceIntervalProbe
{
    /// <summary>
    /// Drives <see cref="ImportanceIntervalModel"/> and renders both arms.
    ///
    /// <para>The MonoBehaviour owns the clock and the widgets and nothing else. Everything
    /// that could be wrong about the measurement is in the model, which is plain C# with no
    /// Unity dependency and was run headless against the backend bench's four reference
    /// rows before this scene existed.</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ImportanceIntervalProbe : MonoBehaviour
    {
        [SerializeField] private PopulationShape shape = PopulationShape.Realistic;
        [SerializeField] private int players = 60;
        [SerializeField] private int mobs = 300;
        [SerializeField] private float mobMovingFraction = 0.34f;
        [SerializeField] private float spreadUnits = 260f;
        [SerializeField] private float aoiRadius = 50f;
        [SerializeField] private float nearFraction = 8f;   // score >= 8 -> every tick
        [SerializeField] private float midFraction = 3f;   // score >= 3 -> every 2nd tick
        [SerializeField] private int ticksToRun = 300;

        private readonly ImportanceIntervalModel _model = new ImportanceIntervalModel();
        private bool _running = true;
        private double _accumulator;

        private Label _bytesA, _bytesB, _saving, _staleness, _tiers, _ticks, _verdict, _shapeHint;
        private Button _run;

        private void OnEnable()
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;

            _bytesA = root.Q<Label>("bytes-a");
            _bytesB = root.Q<Label>("bytes-b");
            _saving = root.Q<Label>("saving");
            _staleness = root.Q<Label>("staleness");
            _tiers = root.Q<Label>("tiers");
            _ticks = root.Q<Label>("ticks");
            _verdict = root.Q<Label>("verdict");
            _shapeHint = root.Q<Label>("shape-hint");

            root.Q<Button>("shape-cluster").clicked += () => SetShape(PopulationShape.Cluster);
            root.Q<Button>("shape-spread").clicked += () => SetShape(PopulationShape.Spread);
            root.Q<Button>("shape-realistic").clicked += () => SetShape(PopulationShape.Realistic);

            Bind(root.Q<SliderInt>("players"), players, v => { players = v; Restart(); });
            Bind(root.Q<SliderInt>("mobs"), mobs, v => { mobs = v; Restart(); });
            Bind(root.Q<SliderInt>("aoi"), Mathf.RoundToInt(aoiRadius), v => { aoiRadius = v; Restart(); });
            Bind(root.Q<Slider>("mob-moving"), mobMovingFraction, v => { mobMovingFraction = v; Restart(); });
            Bind(root.Q<Slider>("near"), nearFraction, v => { nearFraction = v; Restart(); });
            Bind(root.Q<Slider>("mid"), midFraction, v => { midFraction = v; Restart(); });

            _run = root.Q<Button>("run");
            _run.clicked += () => { _running = !_running; _run.text = _running ? "Pause" : "Run"; };
            root.Q<Button>("reset").clicked += Restart;

            Restart();
        }

        private static void Bind(SliderInt s, int value, System.Action<int> set)
        {
            if (s == null) return;
            s.value = value;
            s.RegisterValueChangedCallback(e => set(e.newValue));
        }

        private static void Bind(Slider s, float value, System.Action<float> set)
        {
            if (s == null) return;
            s.value = value;
            s.RegisterValueChangedCallback(e => set(e.newValue));
        }

        private void SetShape(PopulationShape s)
        {
            shape = s;
            Restart();
        }

        private void Restart()
        {
            _model.Shape = shape;
            _model.Players = players;
            _model.Mobs = mobs;
            _model.MobMovingFraction = mobMovingFraction;
            _model.SpreadUnits = spreadUnits;
            _model.AoiRadius = aoiRadius;
            // Kept ordered: a near boundary outside the mid one would silently make the
            // mid tier unreachable and the histogram would look like a policy nobody wrote.
            // Top band must not sit below the middle one, or the middle band becomes
            // unreachable and the histogram shows a policy nobody wrote.
            _model.NearFraction = Mathf.Max(nearFraction, midFraction);
            _model.MidFraction = Mathf.Min(nearFraction, midFraction);
            _model.Rebuild();

            _shapeHint.text = shape switch
            {
                PopulationShape.Cluster =>
                    "Everyone on top of everyone — the shape the published 200-player ceiling was measured on. " +
                    "Every entity is a near player, so every entity is tier 1 and there is nothing to demote. " +
                    "Expect 0%, and read that as a fact about the benchmark rather than about the feature.",
                PopulationShape.Spread =>
                    "Players spread over the map, still all players, still all moving. Distance alone starts " +
                    "doing work; entity type still cannot, because everything is a player.",
                _ =>
                    "Spread players plus mobs, most of the mobs idle. The shape a game has — and the shape this " +
                    "game does not have yet: the server's AI is scaffolding, 30 mobs walking to the origin. " +
                    "Treat the number as conditional on this guess.",
            };

            Render();
        }

        private void Update()
        {
            if (!_running || _model.WorldTick >= ticksToRun) return;

            // 15 world ticks a second: the model's tick IS a world tick, which is the rate
            // snapshots ship at. Bounded so an editor stall cannot turn into a burst.
            _accumulator += Time.deltaTime * 15.0;
            int steps = (int)_accumulator;
            if (steps <= 0) return;
            if (steps > 30) steps = 30;
            _accumulator -= steps;

            for (int i = 0; i < steps && _model.WorldTick < ticksToRun; i++) _model.Step();
            Render();
        }

        private void Render()
        {
            _bytesA.text = $"{_model.BytesPerObservationToday:F2} B/entity/snapshot";
            _bytesB.text = $"{_model.BytesPerObservationTiered:F2} B/entity/snapshot";
            _saving.text = $"{_model.SavingPercent:F1}%";
            _staleness.text = $"{_model.StalenessMaxTicks} / {_model.StalenessP99Ticks} ticks " +
                              $"({_model.StalenessMaxTicks * 1000.0 / 15.0:F0} ms worst)";

            long t1 = _model.TierCounts[0], t2 = _model.TierCounts[1], t4 = _model.TierCounts[2];
            long tt = System.Math.Max(1, t1 + t2 + t4);
            _tiers.text = $"{100.0 * t1 / tt:F0} / {100.0 * t2 / tt:F0} / {100.0 * t4 / tt:F0} %";

            _ticks.text = $"{_model.WorldTick} / {ticksToRun}  ({_model.PopulationCount} entities, " +
                          $"{_model.ViewerCount} viewers)";

            _saving.EnableInClassList("cuvara-probe__value--bad", _model.SavingPercent < 5.0);

            _verdict.text = _model.SavingPercent < 1.0
                ? "No saving at all on this population. Every entity is already tier 1 — there is nothing " +
                  "for an interval policy to defer, and no weighting fixes that."
                : $"{_model.SavingPercent:F0}% fewer snapshot bytes, at a worst case of " +
                  $"{_model.StalenessMaxTicks * 1000.0 / 15.0:F0} ms of staleness on the entities it deferred.";
        }
    }
}
