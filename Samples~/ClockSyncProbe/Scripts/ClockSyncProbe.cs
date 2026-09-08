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
    /// <b>The dial's default is a historical figure, not a measured one, and that
    /// correction is itself worth seeing here.</b> +110,000 ppm was recorded as the measured
    /// ratio between the Windows performance counter and the Linux monotonic clock on this
    /// package's development machine, and it is why the original 0.90/1.10 clamp was widened.
    /// It has since been falsified: the same machine measures <b>220 ppm</b> — a ratio of
    /// 1.0002 — when the Editor is idle, and only reads six figures under load, where a
    /// <i>delay floor that rises between the fit's two anchors</i> is absorbed by the slope.
    /// The dial keeps the value because the clamp must still admit such a ratio and because
    /// the refusal boundary is worth being able to see; it is a synthetic stress case now,
    /// not a machine's fingerprint.
    /// </para>
    /// <para>
    /// <b>Which is what the "Raise delivery floor" button is for.</b> It adds a sustained
    /// delay to every delivery — not the one-off "Stall a frame", a permanent step, which is
    /// what a loaded machine or a server hiccup produces. The two clocks stay in perfect
    /// agreement and the fit reports tens of thousands of ppm anyway, because a line through
    /// two best-case samples is only a rate if the minimum achievable delay was the same at
    /// both. Watch <c>corroborated</c> stay NO and both the clock rate and the age refuse to
    /// follow it: a rate reads the same over any baseline, a floor step fakes
    /// <c>step / baseline</c> and halves when the baseline doubles.
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
        /// The ratio once believed measured on this package's development machine, and the
        /// reason the original clamp was widened. Since falsified — see the class remarks —
        /// and kept as a synthetic stress case at the clamp boundary.
        /// </summary>
        private const float DefaultSkewPpm = 110_000f;

        // ── The two clocks ──
        //
        // clientSeconds is what every real component reads: it plays the role of the
        // monotonic clock the estimator samples with. serverSeconds advances slower or
        // faster by the configured skew — positive ppm means the CLIENT's clock runs fast,
        // so per client second fewer server seconds elapse.
        private double _clientSeconds;
        private double _serverSeconds;
        private long _serverTick = 1000;
        private double _nextSnapshotAtServerSeconds;

        /// <summary>Snapshots in flight: item one is the client time it arrives at.</summary>
        private readonly List<(double deliverAt, long tick, long ackTick)> _pending =
            new List<(double, long, long)>();

        private SnapshotStalenessEstimator _staleness;
        private LocalMovePredictor _predictor;
        private long _lastDeliveredTick;

        /// <summary>
        /// A sustained delay added to every delivery, in seconds. The artefact generator.
        /// </summary>
        /// <remarks>
        /// Distinct from the one-off stall on purpose. A stall is one long frame and the
        /// envelope fit shrugs it off — that is what a lower envelope is FOR. A floor that
        /// rises and stays risen is invisible to the same construction, because the fit's two
        /// anchors then sit at different minimum delays and the line between them tilts. That
        /// tilt is read as clock rate, and before the corroboration gate it was believed:
        /// measured live at 90 636 ppm on a machine whose real ratio is 220.
        /// </remarks>
        private double _deliveryFloorSeconds;

        private float _skewPpm = DefaultSkewPpm;
        private float _jitterMs;
        private bool _paused;

        // One-shot perturbations, applied to the next frame and cleared.
        private float _pendingStallSeconds;
        private double _pendingServerStepSeconds;

        private System.Random _rng = new System.Random(12345);

        // ── The send cadence, and the acknowledgement floor it decides ──
        //
        // This half of the probe exists because the pipeline constant `uplink + snapshot
        // age` is measured by timing an input to the first snapshot that acknowledges it,
        // and that measurement is only valid while the wait term SWEEPS. Sending at exactly
        // the snapshot rate locks the two cadences in phase and the estimator -- correctly --
        // refuses to offer anything at all. Drag the cadence slider to 15 and watch it
        // happen; the histogram collapses to a single bar.
        private const double UplinkSeconds = 0.020;
        private const int SnapshotHz = BaseHz / SnapshotEvery;

        private AckLatencyEstimator _ackLatency;
        private InputSendSchedule _sendSchedule;
        private int _sendHz = InputCadence.RecommendedSendHz(SnapshotHz);
        private long _inputTick;

        /// <summary>Inputs in flight: the client time the SERVER can first see them.</summary>
        private readonly List<(double visibleAt, long tick)> _inFlightInputs =
            new List<(double, long)>();

        /// <summary>
        /// The probe's own copy of the observations, purely so the histogram can show the
        /// distribution the guard is judging.
        /// </summary>
        /// <remarks>
        /// Re-derived here rather than read back out of the estimator, because the estimator
        /// deliberately exposes statistics and not its ring. These are the same numbers it
        /// folds in — send time to the arrival of the acknowledging snapshot — computed at
        /// the same two points.
        /// </remarks>
        private readonly List<double> _observedLatencies = new List<double>();

        /// <summary>Send times, so the histogram can time an acknowledgement the same way.</summary>
        private readonly List<(long tick, double sentAt)> _sentLog =
            new List<(long, double)>();

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
        private SliderInt _sendHzSlider;
        private Label _sendHzValue;
        private Label _cadenceLine;
        private Label _cadenceVerdict;
        private readonly VisualElement[] _phaseBars =
            new VisualElement[AckLatencyEstimator.SweepBuckets];

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
            _sendHzSlider = root.Q<SliderInt>("send-hz");
            _sendHzValue = root.Q<Label>("send-hz-value");
            _cadenceLine = root.Q<Label>("cadence-line");
            _cadenceVerdict = root.Q<Label>("cadence-verdict");

            for (var i = 0; i < _phaseBars.Length; i++)
            {
                _phaseBars[i] = root.Q<VisualElement>($"phase-{i}");
                if (_phaseBars[i] != null) continue;

                Debug.LogError($"[ClockSyncProbe] UXML element 'phase-{i}' not found — the " +
                               "histogram has fewer bars than AckLatencyEstimator.SweepBuckets.");
                enabled = false;
                return;
            }

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
                         (_sendHzSlider, "send-hz"), (_sendHzValue, "send-hz-value"),
                         (_cadenceLine, "cadence-line"), (_cadenceVerdict, "cadence-verdict"),
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

            // Spans the interesting range either side of the snapshot rate, so 15 -- the
            // lock -- is reachable by dragging rather than only describable in a comment.
            _sendHzSlider.lowValue = 8;
            _sendHzSlider.highValue = 20;
            _sendHzSlider.value = _sendHz;
            _sendHzSlider.RegisterValueChangedCallback(e =>
            {
                _sendHz = e.newValue;

                // A cadence change restarts the measurement rather than mixing two phase
                // relationships into one ring. Carrying the old observations across would
                // show a swept histogram for several seconds after dragging to 15, which is
                // precisely the wrong thing for this panel to say.
                _ackLatency.Reset();
                _sendSchedule.Start(_sendHz, _clientSeconds);
                _observedLatencies.Clear();
                _sentLog.Clear();
                _inFlightInputs.Clear();
            });

            root.Q<Button>("stall").clicked += () => _pendingStallSeconds = 0.25f;
            root.Q<Button>("step-clock").clicked += () => _pendingServerStepSeconds = 5.0;
            root.Q<Button>("floor-step").clicked += () =>
                _deliveryFloorSeconds = _deliveryFloorSeconds > 0 ? 0 : 0.30;
            root.Q<Button>("reset").clicked += Reset;
        }

        private void Reset()
        {
            _deliveryFloorSeconds = 0;
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

            _ackLatency = new AckLatencyEstimator();
            _sendSchedule = new InputSendSchedule();
            _sendSchedule.Start(_sendHz, 0.0);
            _inputTick = 0;
            _inFlightInputs.Clear();
            _observedLatencies.Clear();
            _sentLog.Clear();
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

            // ── The client's send loop ──
            //
            // On the PINNED schedule the production loops now use, so the cadence the slider
            // names is the cadence that goes out. Re-deriving the deadline from whenever the
            // frame happened to land is what used to make every nominal rate in (12, 15]
            // arrive as 12 Hz. See InputSendSchedule.
            while (_sendSchedule.SecondsUntilDue(_clientSeconds) <= 0.0)
            {
                _inputTick++;
                _ackLatency.RecordSent(_inputTick, _clientSeconds);

                // The server cannot act on it until the uplink has elapsed.
                _inFlightInputs.Add((_clientSeconds + UplinkSeconds, _inputTick));
                _sentLog.Add((_inputTick, _clientSeconds));
                _sendSchedule.NoteSent(_clientSeconds);
            }

            while (_serverSeconds >= _nextSnapshotAtServerSeconds)
            {
                _serverTick += SnapshotEvery;
                _nextSnapshotAtServerSeconds += SnapshotIntervalServerSeconds;

                // What this snapshot acknowledges: the newest input that had reached the
                // server by the time it was built. Everything older is drained with it,
                // exactly as the real server's monotonic LastInputTick behaves.
                long ackTick = 0;
                for (var i = _inFlightInputs.Count - 1; i >= 0; i--)
                {
                    if (_inFlightInputs[i].visibleAt > _clientSeconds) continue;
                    if (_inFlightInputs[i].tick > ackTick) ackTick = _inFlightInputs[i].tick;
                    _inFlightInputs.RemoveAt(i);
                }

                double jitter = _jitterMs > 0f ? _rng.NextDouble() * _jitterMs / 1000.0 : 0.0;
                _pending.Add((_clientSeconds + jitter + _deliveryFloorSeconds, _serverTick, ackTick));
            }

            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].deliverAt > _clientSeconds)
                {
                    continue;
                }

                long tick = _pending[i].tick;
                long ackTick = _pending[i].ackTick;
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

                // The acknowledgement rides on the snapshot, so it is folded in when the
                // snapshot ARRIVES -- one observation is send -> uplink -> wait for the next
                // snapshot -> age, and only the wait varies. This is the real estimator, on
                // the real call the binder makes.
                if (ackTick > 0)
                {
                    RecordAckObservation(ackTick);
                    _ackLatency.RecordAck(ackTick, _clientSeconds, BaseHz);
                }
            }

            _predictor.Advance(dt);

            RenderReadout();
        }

        /// <summary>
        /// The binder's steering target, minus the round-trip term a synthetic stream does
        /// not have.
        /// </summary>
        /// <remarks>
        /// Gated on <see cref="SnapshotStalenessEstimator.AgeIsFitted"/>, not
        /// <c>IsUsable</c>, exactly as <c>WorldViewBinder.TargetLeadTicks</c> is. IsUsable
        /// says a line was fitted; it does not say the line is trustworthy, and the age is
        /// the height above a line the slope tilts — so an uncorroborated slope steers the
        /// lead through the residual even after the clock has stopped listening to it. Raise
        /// the delivery floor and watch the lead NOT move: that is the difference.
        /// </remarks>
        private int TargetLeadTicks()
        {
            float lead = _staleness.AgeIsFitted ? _staleness.StalenessTicks : SnapshotEvery;
            int ticks = Mathf.RoundToInt(lead);
            return Mathf.Clamp(ticks, 0, SnapshotEvery * 2);
        }

        /// <summary>
        /// The send-cadence half of the panel: what is configured, what the phase actually
        /// does, and whether the estimator will offer a floor because of it.
        /// </summary>
        private void RenderCadenceReadout()
        {
            int occupied = RenderPhaseHistogram();
            int recommended = InputCadence.RecommendedSendHz(SnapshotHz);
            bool locked = _sendHz == SnapshotHz;

            _sendHzValue.text = $"{_sendHz} Hz" + (_sendHz == recommended ? " (recommended)" : string.Empty);

            _cadenceLine.text =
                $"send {_sendHz} Hz vs snapshots {SnapshotHz} Hz | " +
                $"phases {InputCadence.DistinctPhases(_sendHz, SnapshotHz)} | " +
                $"sweep every {InputCadence.SendsPerSweep(_sendHz, SnapshotHz):F1} sends " +
                $"({InputCadence.SweepSeconds(_sendHz, SnapshotHz):F2} s) | " +
                $"buckets {occupied}/{AckLatencyEstimator.SweepBuckets} " +
                $"(needs {AckLatencyEstimator.MinimumOccupiedBuckets}) | " +
                $"samples {_ackLatency.Samples} | superseded {_ackLatency.Superseded} | " +
                $"unswept {_ackLatency.UnsweptSeconds * 1000.0:F1} ms | " +
                $"floor {_ackLatency.FloorTicks:F2} t → lead {_ackLatency.ConservativeFloorTicks:F2} t";

            if (_ackLatency.Samples < AckLatencyEstimator.MinimumSamples)
            {
                _cadenceVerdict.text =
                    "warming up — the floor needs " +
                    $"{AckLatencyEstimator.MinimumSamples} acknowledged observations";
                _cadenceVerdict.EnableInClassList("cuvara-probe__verdict--ok", false);
                _cadenceVerdict.EnableInClassList("cuvara-probe__verdict--bad", false);
                return;
            }

            if (!_ackLatency.SweptEnough)
            {
                _cadenceVerdict.text = locked
                    ? "REFUSED — the send cadence EQUALS the snapshot rate, so every " +
                      "observation carries the same fixed wait. The floor would read high by " +
                      "up to a whole snapshot interval, and an over-lead steers the client " +
                      "past the server. Nothing is offered, and that is correct: this client " +
                      "holds no evidence about its own pipeline constant."
                    : $"REFUSED — {_sendHz} Hz visits only " +
                      $"{InputCadence.DistinctPhases(_sendHz, SnapshotHz)} phase(s) of the " +
                      $"interval, so the wait never sweeps far enough to trust its minimum. " +
                      $"Try {recommended} Hz.";
                _cadenceVerdict.EnableInClassList("cuvara-probe__verdict--ok", false);
                _cadenceVerdict.EnableInClassList("cuvara-probe__verdict--bad", true);
                return;
            }

            // The true constant in this model is the uplink plus the age the snapshot had
            // when it arrived; the uplink half is the part the staleness fit can never see.
            _cadenceVerdict.text =
                $"SWEPT — the wait varied across {occupied} of " +
                $"{AckLatencyEstimator.SweepBuckets} divisions, so the minimum means " +
                $"something. Floor {_ackLatency.FloorSeconds * 1000.0:F1} ms against an " +
                $"injected uplink of {UplinkSeconds * 1000.0:F0} ms plus the snapshot age.";
            _cadenceVerdict.EnableInClassList("cuvara-probe__verdict--ok", true);
            _cadenceVerdict.EnableInClassList("cuvara-probe__verdict--bad", false);
        }

        /// <summary>
        /// Times an acknowledgement exactly as <see cref="AckLatencyEstimator"/> does — the
        /// NEWEST input it covers, never an older one — so the histogram shows the same
        /// distribution the guard is judging.
        /// </summary>
        /// <remarks>
        /// Timing an older input would measure a wait a later input had already earned, and
        /// would stretch the apparent spread by however far the send cadence runs ahead of
        /// the acknowledgement cadence. That is the exact corruption the estimator documents
        /// under <c>Superseded</c>, and a picture drawn from it would make a locked link look
        /// swept — which is the failure this panel exists to make visible.
        /// </remarks>
        private void RecordAckObservation(long ackTick)
        {
            double newestSentAt = 0.0;
            var haveNewest = false;

            for (var i = _sentLog.Count - 1; i >= 0; i--)
            {
                if (_sentLog[i].tick > ackTick) continue;

                if (!haveNewest || _sentLog[i].sentAt > newestSentAt)
                {
                    newestSentAt = _sentLog[i].sentAt;
                    haveNewest = true;
                }

                _sentLog.RemoveAt(i);
            }

            if (!haveNewest) return;

            double latency = _clientSeconds - newestSentAt;
            if (latency <= 0.0) return;

            _observedLatencies.Add(latency);

            // The same memory the estimator keeps, so the picture ages out with the reading.
            const int capacity = 128;
            if (_observedLatencies.Count > capacity)
            {
                _observedLatencies.RemoveRange(0, _observedLatencies.Count - capacity);
            }
        }

        /// <summary>
        /// Fills the eight phase bars, bucketing exactly as
        /// <c>AckLatencyEstimator.OccupiedBuckets</c> does, and returns how many are occupied.
        /// </summary>
        private int RenderPhaseHistogram()
        {
            var counts = new int[AckLatencyEstimator.SweepBuckets];
            double interval = _ackLatency.AckIntervalSeconds;

            if (_observedLatencies.Count > 0 && interval > 0.0)
            {
                double lo = double.MaxValue;
                for (var i = 0; i < _observedLatencies.Count; i++)
                {
                    if (_observedLatencies[i] < lo) lo = _observedLatencies[i];
                }

                double width = interval / AckLatencyEstimator.SweepBuckets;
                for (var i = 0; i < _observedLatencies.Count; i++)
                {
                    var bucket = (int)((_observedLatencies[i] - lo) / width);
                    if (bucket < 0) bucket = 0;
                    if (bucket >= AckLatencyEstimator.SweepBuckets)
                    {
                        bucket = AckLatencyEstimator.SweepBuckets - 1;
                    }

                    counts[bucket]++;
                }
            }

            var peak = 1;
            for (var i = 0; i < counts.Length; i++)
            {
                if (counts[i] > peak) peak = counts[i];
            }

            var occupied = 0;
            for (var i = 0; i < counts.Length; i++)
            {
                if (counts[i] > 0) occupied++;
                if (_phaseBars[i] == null) continue;

                // A floor of 2px so an empty bucket is still drawn -- an absent bar and a
                // zero bar read identically otherwise, and "seven empty buckets" is the
                // whole picture of a lock.
                float share = counts[i] / (float)peak;
                _phaseBars[i].style.height = 2f + share * 38f;
                _phaseBars[i].EnableInClassList(
                    "cuvara-probe__histogram-bar--empty", counts[i] == 0);
            }

            return occupied;
        }

        private void RenderReadout()
        {
            _skewValue.text = $"{_skewPpm / 1000f:+0.0;-0.0} ×10³ ppm " +
                              $"({(1.0 + _skewPpm / 1e6):F4}×)";
            _jitterValue.text = $"{_jitterMs:F0} ms";

            RenderCadenceReadout();

            _fitLine.text =
                $"measured {_staleness.SkewPpm / 1000.0:+0.0;-0.0} ×10³ ppm | " +
                $"staleness {_staleness.StalenessTicks:F2} t | " +
                $"baseline {_staleness.BaselineSeconds:F0} s | " +
                $"fits {_staleness.Fits} | refused {_staleness.FitsRefused} | " +
                $"corroborated {(_staleness.RateCorroborated ? "YES" : "NO")} | " +
                $"age from {(_staleness.AgeIsFitted ? "the fit" : "the unit-rate floor")}" +
                (_deliveryFloorSeconds > 0 ? " | FLOOR +300 ms" : string.Empty) +
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
