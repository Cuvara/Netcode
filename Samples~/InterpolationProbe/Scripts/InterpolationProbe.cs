using System.Collections.Generic;
using Cuvara.Netcode.Interpolation;
using Cuvara.Netcode.View;
using UnityEngine;
using UnityEngine.UIElements;

namespace Cuvara.Netcode.Samples.InterpolationProbe
{
    /// <summary>
    /// Drives a synthetic snapshot stream into the interpolation core and draws the result,
    /// beside the same stream drawn by the algorithm netcode 0.19.0 replaced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this scene exists at all for a feature that already shipped.</b> The free-
    /// running render clock landed in 0.19.0 with a changelog entry and a documentation
    /// section, and with four tests that assert the numbers. What none of those give anyone
    /// is the thing the change was actually about: how the motion <i>looks</i>. "Stepped
    /// backwards 0.2000 units" is a sentence; a dot visibly snapping back is the defect. A
    /// scene is the only artefact that carries that, so the feature was short one until
    /// this sample was added.
    /// </para>
    /// <para>
    /// <b>No server and no network.</b> <see cref="SyntheticSnapshotStream"/> plays the
    /// server: it produces one entity's position on a 15 Hz tick, delays each snapshot, and
    /// perturbs whichever one a button asks it to. The scenarios are the ones
    /// <c>Tests/Editor/RemoteInterpolationContinuityTests.cs</c> constructs — an early
    /// arrival, a late arrival past the old clamp, a dropped snapshot, and a clean periodic
    /// control — because a scene that invented different ones would be demonstrating
    /// something the suite does not defend.
    /// </para>
    /// <para>
    /// <b>The orange dot is not production code.</b> It is rendered by
    /// <see cref="ObsoleteResetOnArrivalInterpolator"/>, a sample-only copy of the
    /// pre-0.19.0 algorithm; read the banner at the top of that file. The green dot is the
    /// real thing — <see cref="InterpolationClock"/> and
    /// <see cref="SnapshotInterpolation.Evaluate{TBuffer}"/>, through the same
    /// <see cref="EntitySampleRing"/> the production <c>WorldViewBinder</c> uses.
    /// </para>
    /// <para>
    /// UI is UXML/UI Toolkit, like every sample scene in this package. There is no
    /// <c>OnGUI</c> and no uGUI canvas here on purpose: sample scenes are where a reader
    /// learns the intended pattern.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(UIDocument))]
    public sealed class InterpolationProbe : MonoBehaviour
    {
        /// <summary>A step below this is float noise on a position of order 1, not motion.</summary>
        private const double NoiseUnits = 1e-4;

        /// <summary>
        /// Top of the jitter slider, in milliseconds. Deliberately past the 100 ms
        /// <see cref="InterpolationConfig.TargetDelay"/>: a slider that stopped below the
        /// buffer's own depth could never show the buffer running out, which is half of
        /// what there is to learn here.
        /// </summary>
        private const float JitterMaxMs = 150f;

        [Header("Stream")]
        [Tooltip("Arrival jitter, plus or minus, in milliseconds. The slider writes this.")]
        [Range(0f, JitterMaxMs)]
        public float jitterMs;

        [Tooltip("Draw the pre-0.19.0 algorithm's dot beside the current one.")]
        public bool showObsoleteTrack = true;

        [Header("Markers")]
        public Color currentColor = new Color(0.36f, 0.85f, 0.55f);
        public Color obsoleteColor = new Color(0.95f, 0.60f, 0.25f);
        public Color truthColor = new Color(0.45f, 0.50f, 0.58f);

        private readonly SyntheticSnapshotStream _stream = new SyntheticSnapshotStream();
        private readonly List<SyntheticSnapshotStream.Packet> _arrivals =
            new List<SyntheticSnapshotStream.Packet>();

        private readonly ObsoleteResetOnArrivalInterpolator _obsolete =
            new ObsoleteResetOnArrivalInterpolator();

        private readonly MotionStats _currentStats = new MotionStats();
        private readonly MotionStats _obsoleteStats = new MotionStats();

        private InterpolationConfig _config;
        private InterpolationClock _clock;
        private EntitySampleRing _ring;

        private Transform _currentMarker;
        private Transform _obsoleteMarker;
        private Transform _truthMarker;

        private double _now;
        private bool _paused;
        private double _nextUiRefresh;

        private DropdownField _repeatField;
        private Slider _jitterSlider;
        private Label _jitterValue;
        private Toggle _pauseToggle;
        private Toggle _obsoleteToggle;
        private Label _streamLine;
        private Label _clockLine;
        private Label _verdict;

        private TrackUi _currentUi;
        private TrackUi _obsoleteUi;

        /// <summary>The four labels and the bar that report one track.</summary>
        private struct TrackUi
        {
            public Label Step;
            public Label Max;
            public Label Backwards;
            public Label Delay;
            public VisualElement Bar;
        }

        private void Awake()
        {
            _config = InterpolationConfig.Default.Normalized();
            _ring = new EntitySampleRing(_config.RingCapacity);
            _stream.Reset(0.0);

            _truthMarker = CreateMarker("Server truth (now)", truthColor, 0.32f, 0.02f);
            _obsoleteMarker = CreateMarker("Pre-0.19 reset-on-arrival (SAMPLE ONLY)", obsoleteColor, 0.58f, 0.01f);
            _currentMarker = CreateMarker("Free-running clock (production)", currentColor, 0.46f, 0.0f);
        }

        private void OnEnable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;

            _repeatField = root.Q<DropdownField>("repeat");
            _jitterSlider = root.Q<Slider>("jitter");
            _jitterValue = root.Q<Label>("jitter-value");
            _pauseToggle = root.Q<Toggle>("pause");
            _obsoleteToggle = root.Q<Toggle>("show-obsolete");
            _streamLine = root.Q<Label>("stream-line");
            _clockLine = root.Q<Label>("clock-line");
            _verdict = root.Q<Label>("verdict");

            _currentUi = QueryTrack(root, "current");
            _obsoleteUi = QueryTrack(root, "obsolete");

            // A Q<T> miss returns null and the scene then dies on first interaction with a
            // NullReferenceException pointing at the handler rather than at the renamed
            // element. Naming the actual fault here is worth the branch.
            if (_repeatField == null || _jitterSlider == null || _pauseToggle == null ||
                _streamLine == null || _clockLine == null || _currentUi.Step == null ||
                _obsoleteUi.Step == null)
            {
                Debug.LogError(
                    "[InterpolationProbe] InterpolationProbeView.uxml is missing an expected " +
                    "element. The names in the UXML and the names queried here have to match.");
                enabled = false;
                return;
            }

            // Choices are set here rather than in the UXML so the enum and the dropdown
            // cannot drift apart: a renamed enum member breaks the build instead of
            // silently selecting the wrong perturbation.
            _repeatField.choices = new List<string>
            {
                "Off — clean periodic",
                "Every other snapshot early",
                "Every other snapshot late",
                "Every other snapshot dropped"
            };
            _repeatField.index = 0;
            _repeatField.RegisterValueChangedCallback(_ => _stream.Repeat = RepeatFromIndex(_repeatField.index));

            _jitterSlider.lowValue = 0f;
            _jitterSlider.highValue = JitterMaxMs;
            _jitterSlider.value = jitterMs;
            _jitterSlider.RegisterValueChangedCallback(e =>
            {
                jitterMs = e.newValue;
                _stream.JitterSeconds = jitterMs / 1000.0;
                if (_jitterValue != null) _jitterValue.text = $"± {jitterMs:F0} ms";
            });
            _stream.JitterSeconds = jitterMs / 1000.0;
            if (_jitterValue != null) _jitterValue.text = $"± {jitterMs:F0} ms";

            _pauseToggle.value = false;
            _pauseToggle.RegisterValueChangedCallback(e => _paused = e.newValue);

            if (_obsoleteToggle != null)
            {
                _obsoleteToggle.value = showObsoleteTrack;
                _obsoleteToggle.RegisterValueChangedCallback(e =>
                {
                    showObsoleteTrack = e.newValue;
                    if (_obsoleteMarker != null) _obsoleteMarker.gameObject.SetActive(showObsoleteTrack);
                });
            }

            Bind(root, "inject-early", SyntheticSnapshotStream.Perturbation.Early);
            Bind(root, "inject-late", SyntheticSnapshotStream.Perturbation.Late);
            Bind(root, "inject-skip", SyntheticSnapshotStream.Perturbation.Skip);

            var reset = root.Q<Button>("reset");
            if (reset != null) reset.clicked += ResetAll;

            if (_obsoleteMarker != null) _obsoleteMarker.gameObject.SetActive(showObsoleteTrack);

            ResetAll();
        }

        private void Bind(VisualElement root, string name, SyntheticSnapshotStream.Perturbation kind)
        {
            var button = root.Q<Button>(name);
            if (button != null) button.clicked += () => _stream.Pending = kind;
        }

        private static TrackUi QueryTrack(VisualElement root, string prefix)
        {
            return new TrackUi
            {
                Step = root.Q<Label>(prefix + "-step"),
                Max = root.Q<Label>(prefix + "-max"),
                Backwards = root.Q<Label>(prefix + "-back"),
                Delay = root.Q<Label>(prefix + "-delay"),
                Bar = root.Q<VisualElement>(prefix + "-bar")
            };
        }

        private static SyntheticSnapshotStream.Perturbation RepeatFromIndex(int index)
        {
            switch (index)
            {
                case 1: return SyntheticSnapshotStream.Perturbation.Early;
                case 2: return SyntheticSnapshotStream.Perturbation.Late;
                case 3: return SyntheticSnapshotStream.Perturbation.Skip;
                default: return SyntheticSnapshotStream.Perturbation.None;
            }
        }

        private void ResetAll()
        {
            _now = 0.0;
            _stream.Reset(0.0);
            _ring.Clear();
            _clock.Reset();
            _obsolete.Reset();
            _currentStats.Reset();
            _obsoleteStats.Reset();
            _nextUiRefresh = 0.0;
        }

        private void Update()
        {
            if (_paused)
            {
                return;
            }

            double dt = Time.deltaTime;
            if (dt <= 0.0)
            {
                return;
            }

            _now += dt;

            _arrivals.Clear();
            _stream.Pump(_now, _arrivals);

            for (int i = 0; i < _arrivals.Count; i++)
            {
                var packet = _arrivals[i];

                // The production path: the tick places the sample, the receive time is only
                // ever used to measure how long a tick takes. snapshotTickGap is 1 here
                // because this synthetic server publishes every tick.
                _clock.NoteSnapshot(packet.Tick, packet.ArriveAt, 1, _config);
                _ring.TryPush(new InterpolationSample
                {
                    Tick = packet.Tick,
                    ReceiveTime = packet.ArriveAt,
                    X = packet.X,
                    Y = packet.Y
                });

                // The obsolete path, fed the identical packet at the identical instant.
                _obsolete.NoteSnapshot(packet.X, packet.Y, packet.ArriveAt);
            }

            _clock.Advance(dt, _config);

            if (SnapshotInterpolation.Evaluate(new EntitySampleBuffer(_ring), _clock, _config,
                                               out var cx, out var cy))
            {
                _currentMarker.localPosition = new Vector3(cx, cy, 0f);
                _currentStats.Push(cx, cy);
            }

            if (_obsolete.Evaluate(_now, out var ox, out var oy))
            {
                _obsoleteMarker.localPosition = new Vector3(ox, oy, 0.01f);
                _obsoleteStats.Push(ox, oy);
            }

            // Tick 1 is produced at t = 0, so the tick the server is on right now is
            // 1 + elapsed/interval. The grey dot is therefore ahead of both rendered dots
            // by the latency plus the jitter buffer, which is the delay being paid for.
            SyntheticSnapshotStream.PositionAt(1.0 + _now / SyntheticSnapshotStream.TickInterval,
                                               out var tx, out var ty);
            _truthMarker.localPosition = new Vector3(tx, ty, 0.02f);

            // Ten refreshes a second. A label rewritten every frame at 200 fps is a
            // flickering blur that cannot be read, which would defeat the readout.
            if (_now >= _nextUiRefresh)
            {
                _nextUiRefresh = _now + 0.1;
                RefreshUi();
            }
        }

        private void RefreshUi()
        {
            double renderDelayMs = _clock.HasSamples && _clock.SecondsPerTick > 0.0
                ? (_clock.NewestTick - _clock.RenderTick) * _clock.SecondsPerTick * 1000.0
                : 0.0;

            // Where the obsolete algorithm's dot sits relative to the newest sample it
            // holds: at phase 1.0 it is exactly on it, below that it is behind, above it is
            // extrapolating ahead of anything the server ever said.
            double obsoleteDelayMs = (1.0 - _obsolete.LastPhase) * _obsolete.IntervalSeconds * 1000.0;

            Fill(_currentUi, _currentStats, renderDelayMs);
            Fill(_obsoleteUi, _obsoleteStats, obsoleteDelayMs);

            _streamLine.text =
                $"produced {_stream.Produced}   delivered {_stream.Delivered}   " +
                $"dropped {_stream.Dropped}   jitter ±{jitterMs:F0} ms   " +
                $"tick {1000.0 * SyntheticSnapshotStream.TickInterval:F1} ms";

            _clockLine.text = _clock.HasSamples
                ? $"render tick {_clock.RenderTick:F2}   newest {_clock.NewestTick}   " +
                  $"measured {_clock.SecondsPerTick * 1000.0:F2} ms/tick   " +
                  $"target delay {_config.TargetDelay * 1000.0:F0} ms"
                : "waiting for the first snapshot";

            if (_verdict != null)
            {
                bool clean = _currentStats.BackwardsCount == 0;
                _verdict.text = clean
                    ? "no backward step on the production track"
                    : $"{_currentStats.BackwardsCount} backward step(s) on the production track — that is a bug, please report it";
                _verdict.EnableInClassList("cuvara-probe__verdict--bad", !clean);
                _verdict.EnableInClassList("cuvara-probe__verdict--ok", clean);
            }
        }

        private static void Fill(TrackUi ui, MotionStats stats, double delayMs)
        {
            if (ui.Step == null)
            {
                return;
            }

            double median = stats.Median();
            double ratio = median > NoiseUnits ? stats.MaxStep / median : 0.0;

            ui.Step.text = $"{stats.LastStep:F4} u";
            ui.Max.text = median > NoiseUnits
                ? $"{stats.MaxStep:F4} u  ({ratio:F2}× median)"
                : $"{stats.MaxStep:F4} u";
            ui.Backwards.text = stats.BackwardsCount == 0
                ? "none"
                : $"{stats.BackwardsCount}  (worst {stats.WorstBackwards:F4} u)";
            ui.Delay.text = $"{delayMs:F0} ms";

            if (ui.Backwards != null)
            {
                ui.Backwards.EnableInClassList("cuvara-probe__value--bad", stats.BackwardsCount > 0);
            }

            if (ui.Bar != null)
            {
                // Full width at four times the median step, so an ordinary frame sits at a
                // quarter and the 4.3x lurch fills the bar.
                double fill = median > NoiseUnits ? stats.LastStep / (median * 4.0) : 0.0;
                if (fill > 1.0) fill = 1.0;
                ui.Bar.style.width = new StyleLength(Length.Percent((float)(fill * 100.0)));
                ui.Bar.EnableInClassList("cuvara-probe__bar-fill--hot", ratio > 0.0 && stats.LastStep > median * 2.5);
            }
        }

        private Transform CreateMarker(string label, Color color, float scale, float z)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = label;
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * scale;
            go.transform.localPosition = new Vector3(0f, 0f, z);

            var collider = go.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.sharedMaterial = CreateMaterial(color);

            return go.transform;
        }

        /// <summary>
        /// Same fallback chain the DOTS sample uses: URP's Lit where the project has URP,
        /// the built-in Standard shader otherwise. Neither is guaranteed in a consumer
        /// project, which is why both are tried.
        /// </summary>
        private static Material CreateMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Sprites/Default");

            var material = new Material(shader);
            material.color = color;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            return material;
        }

        /// <summary>
        /// Frame-to-frame step statistics for one rendered track: the step, the largest step
        /// seen, and whether any step reversed the direction of travel.
        /// </summary>
        /// <remarks>
        /// Backwards is decided by projecting the step onto the previous direction of
        /// travel rather than by looking at one axis, because the entity travels a circle
        /// and every axis reverses legitimately twice a revolution.
        /// </remarks>
        private sealed class MotionStats
        {
            private const int Window = 240;

            private readonly double[] _steps = new double[Window];
            private readonly double[] _scratch = new double[Window];

            private double _lastX, _lastY;
            private double _dirX, _dirY;
            private bool _hasLast;
            private bool _hasDir;
            private int _count;
            private int _write;

            public double LastStep { get; private set; }
            public double MaxStep { get; private set; }
            public double WorstBackwards { get; private set; }
            public int BackwardsCount { get; private set; }

            public void Reset()
            {
                _hasLast = false;
                _hasDir = false;
                _count = 0;
                _write = 0;
                LastStep = 0.0;
                MaxStep = 0.0;
                WorstBackwards = 0.0;
                BackwardsCount = 0;
            }

            public void Push(double x, double y)
            {
                if (!_hasLast)
                {
                    _lastX = x;
                    _lastY = y;
                    _hasLast = true;
                    return;
                }

                double dx = x - _lastX;
                double dy = y - _lastY;
                _lastX = x;
                _lastY = y;

                double length = System.Math.Sqrt(dx * dx + dy * dy);
                LastStep = length;
                if (length > MaxStep) MaxStep = length;

                _steps[_write] = length;
                _write = (_write + 1) % Window;
                if (_count < Window) _count++;

                if (length <= NoiseUnits)
                {
                    // A stationary frame is not a backwards one, and it must not be allowed
                    // to redefine the direction of travel as zero.
                    return;
                }

                if (_hasDir)
                {
                    double projection = dx * _dirX + dy * _dirY;
                    if (projection < -NoiseUnits)
                    {
                        BackwardsCount++;
                        if (-projection > WorstBackwards) WorstBackwards = -projection;
                    }
                }

                _dirX = dx / length;
                _dirY = dy / length;
                _hasDir = true;
            }

            /// <summary>
            /// Median step over the retained window — the "ordinary frame" every other
            /// number is quoted against, and robust to exactly the outliers being hunted.
            /// </summary>
            public double Median()
            {
                if (_count <= 0)
                {
                    return 0.0;
                }

                System.Array.Copy(_steps, _scratch, _count);
                System.Array.Sort(_scratch, 0, _count);
                return _count % 2 == 1
                    ? _scratch[_count / 2]
                    : 0.5 * (_scratch[_count / 2 - 1] + _scratch[_count / 2]);
            }
        }
    }
}
