using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Prediction;
using Cuvara.Netcode.Transport;
using Cuvara.Netcode.View;
using Shared.GameLogic.Components;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cuvara.Netcode.Tests.PlayMode
{
    /// <summary>
    /// Measures the interval client-side prediction removes between submitting an input
    /// and the local avatar visibly moving, against a live backend.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this does NOT measure, stated first so the number is not overclaimed.</b>
    /// It is <b>not</b> keypress-to-visible. A keypress-to-visible figure includes the
    /// keyboard, the OS input stack, and the display pipeline, and measuring it honestly
    /// needs external capture — a high-speed camera or a hardware probe. Nothing running
    /// inside the engine can see those legs, and a number that quietly folded them in
    /// would be a guess wearing a measurement's clothes.
    /// </para>
    /// <para>
    /// What it measures is <b>input-submitted → local avatar moves on screen</b>: from the
    /// frame the client hands an input to the network layer, to the frame the view is told
    /// a changed position for the local entity. That is the whole of the interval
    /// prediction is capable of affecting, and it is the dominant term in the player's
    /// complaint. The legs it excludes are constant between the two configurations, so the
    /// *difference* it reports is unaffected by their absence.
    /// </para>
    ///
    /// <para><b>Method.</b> Each sample:</para>
    /// <list type="number">
    /// <item><description>Hold zero input until the rendered local position is stable, so
    /// any subsequent movement is attributable to one input rather than to the previous
    /// one still arriving.</description></item>
    /// <item><description>Submit one input at tick <c>T</c> with a non-zero direction and
    /// stamp <c>Time.realtimeSinceStartup</c>.</description></item>
    /// <item><description><b>t_visible</b> — the first frame the view is told a local
    /// position differing from the settled one.</description></item>
    /// <item><description><b>t_authoritative</b> — the first snapshot whose
    /// <c>AckTick >= T</c>, i.e. when the server's own answer for that input
    /// lands.</description></item>
    /// </list>
    /// <para>
    /// With prediction, <c>t_visible</c> should be the same frame as the submit. Without
    /// it, <c>t_visible</c> can only be <c>t_authoritative</c>, because nothing else moves
    /// the entity. <b>The difference between the two configurations is what prediction
    /// buys</b>, measured rather than derived.
    /// </para>
    ///
    /// <para><b>Why it cannot run in CI.</b> It needs a gateway, a game server and Nakama.
    /// The CI job runs <c>testMode: EditMode</c> and never executes PlayMode tests, so this
    /// assembly compiles there — which is worth having, it catches breakage — but nothing
    /// here runs. That is a deliberate gap, stated rather than hidden.
    /// </para>
    /// <para>
    /// <b>It does not skip when the backend is down; it fails.</b> A test that quietly
    /// turns green when its dependency is missing is the exact failure this repository has
    /// paid for repeatedly — a suite reporting success while executing nothing. If you run
    /// this without a backend you get a red result naming what was unreachable.
    /// </para>
    /// </remarks>
    [TestFixture]
    [Category("LiveBackend")]
    public sealed class PredictionLatencyMeasurement
    {
        /// <summary>Samples per configuration.</summary>
        /// <remarks>
        /// Enough that the spread is visible rather than one lucky frame. At 15 Hz with a
        /// settle window between samples this is roughly 20 s per configuration.
        /// </remarks>
        private const int Samples = 20;

        /// <summary>
        /// Times each configuration is measured. The median run is reported and the
        /// spread printed beside it.
        /// </summary>
        /// <remarks>
        /// Three, because two cannot distinguish an outlier from a trend and the run
        /// already takes long enough that the backend's own state drifts across it. The
        /// spread matters more than the middle: a metric whose runs disagree by more than
        /// the change being investigated cannot settle the question, and reporting one
        /// number invites exactly that mistake.
        /// </remarks>
        private const int Repeats = 3;

        /// <summary>Frames of zero input before a sample, to settle.</summary>
        private const int SettleTicks = 6;

        /// <summary>Give up on a sample after this long and report it, rather than hang.</summary>
        private const float SampleTimeoutSeconds = 3f;

        private const float MoveEpsilon = 0.001f;

        // How the divergence run forces a disagreement, and the two ways that do NOT work:
        //
        //   A wrong SPEED does not survive. The wire carries per-entity speed (field 9)
        //   and the binder feeds it to SetServerSpeed every snapshot, so the error is
        //   corrected back within one snapshot.
        //
        //   DROPPING the input does not diverge at all, which cost a live run to learn.
        //   An input that is never sent is never acknowledged, so it is never removed
        //   from the pending buffer, so every reconcile replays it on top of the
        //   authoritative position and reproduces the prediction exactly. Correction is
        //   zero because nothing HAS diverged: from the client's side a dropped input is
        //   indistinguishable from one still in flight, which is what it is.
        //
        // What works is sending a DIFFERENT vector than the one predicted. The server
        // acknowledges the tick — so the input leaves the buffer — having moved somewhere
        // else, and the disagreement is real and permanent.

        private sealed class Sample
        {
            public float InputToVisibleMs;
            public float InputToAuthoritativeMs;
            public bool VisibleTimedOut;
            public bool AuthoritativeTimedOut;
        }

        private sealed class Run
        {
            public string Name;
            public List<Sample> Samples = new List<Sample>();
            public int PendingPeak;
            public int ReplayedSteps;
            public int Reconciles;
            public int TickRateInUse;
            public bool TickRateIsFallback;
            public float MeasuredTickRate;
            public bool TickRateDisagrees;

            /// <summary>Distinct positions the view was told for the local entity.</summary>
            /// <remarks>
            /// One means the entity never moved at all. Separates "the server did not
            /// move it" from "the harness did not notice", which otherwise report
            /// identically.
            /// </remarks>
            public int DistinctPositions;

            /// <summary>Times SetState was called for any entity.</summary>
            public int SetStateCalls;

            /// <summary>Wall-clock gaps between consecutive input sends, seconds.</summary>
            /// <remarks>
            /// The server discards inputs that clump into one tick along with the
            /// simulated time they carried (rpg-mmo-server#100), losing up to 46% of
            /// movement at 60/15/5. A client sending unevenly is therefore legitimately
            /// outrun by its own prediction. Measured rather than assumed even, because
            /// "the sends are evenly spaced" is exactly the sort of thing this harness
            /// has now been wrong about twice.
            /// </remarks>
            public readonly System.Collections.Generic.List<float> SendGaps =
                new System.Collections.Generic.List<float>();

            public float SendGapBurstiness
            {
                get
                {
                    if (SendGaps.Count < 2) return float.NaN;
                    float mean = SendGaps.Average();
                    return mean > 0f ? SendGaps.Max() / mean : float.NaN;
                }
            }

            /// <summary>Displacement one accepted input produces on the server.</summary>
            public float ExpectedStep =>
                TickRateInUse > 0 && !float.IsNaN(EffectiveSpeed)
                    ? EffectiveSpeed / TickRateInUse
                    : 0f;
            public int Snaps;
            public int SmoothedCorrections;
            public float MaxCorrection;
            public float EffectiveSpeed;
            public bool Predicting;

            /// <summary>
            /// Per-render-frame movement of the rendered local position, while an input
            /// is in flight. This is the stutter, quantified: at 15 Hz input and a high
            /// frame rate, unsmoothed motion is a long run of exact zeros punctuated by
            /// one whole step, so <see cref="StillFramePercent"/> near 100 and a large
            /// <see cref="MaxFrameDelta"/> are the signature. Smooth motion is a small,
            /// near-constant delta every frame.
            /// </summary>
            public List<float> FrameDeltas = new List<float>();

            /// <summary>Wall-clock length of each sampled frame, for the fps context.</summary>
            public List<float> FrameSeconds = new List<float>();

            public float StillFramePercent =>
                FrameDeltas.Count == 0 ? float.NaN
                    : 100f * FrameDeltas.Count(d => d <= 1e-6f) / FrameDeltas.Count;

            public float MaxFrameDelta => FrameDeltas.Count == 0 ? float.NaN : FrameDeltas.Max();

            public float MeanFrameDelta => FrameDeltas.Count == 0 ? float.NaN : FrameDeltas.Average();

            /// <summary>
            /// Worst frame divided by the average frame — <b>the frame-rate-independent
            /// smoothness number</b>, and the one to quote.
            /// </summary>
            /// <remarks>
            /// <para>
            /// <b>1.0 is perfect</b>: every frame moved the same distance. Unsmoothed
            /// motion puts a whole step on one frame and nothing on the rest, so the ratio
            /// is the number of frames per input interval.
            /// </para>
            /// <para>
            /// <b>Why not quote the raw distance.</b> Two runs of the same build reported a
            /// largest single-frame jump of 0.0149 and 0.0244 world units — a 60% spread
            /// that looks like measurement noise and is not. It is frame rate: those values
            /// imply 336 fps and 205 fps, and a smoothed step necessarily divides into
            /// larger pieces when there are fewer frames to divide it across. The raw
            /// figure is not comparable between runs, or between machines, or between a
            /// developer's Editor and a player's build. This ratio is.
            /// </para>
            /// </remarks>
            public float FrameDeltaBurstiness =>
                FrameDeltas.Count == 0 || MeanFrameDelta <= 0f ? float.NaN
                    : MaxFrameDelta / MeanFrameDelta;

            /// <summary>Observed render rate during the sampled frames, for context.</summary>
            public float ObservedFps { get; set; }

            /// <summary>Standard deviation of the per-frame movement — flat when smooth.</summary>
            public float FrameDeltaStdDev
            {
                get
                {
                    if (FrameDeltas.Count < 2) return float.NaN;
                    float mean = MeanFrameDelta;
                    double sum = FrameDeltas.Sum(d => (d - mean) * (double)(d - mean));
                    return (float)System.Math.Sqrt(sum / (FrameDeltas.Count - 1));
                }
            }
        }

        [UnityTest]
        public IEnumerator InputToVisibleMovement_WithAndWithoutPrediction() => UniTask.ToCoroutine(async () =>
        {
            // Skip, loudly, when there is nothing to measure against.
            //
            // The [Category] attribute is not enough on its own: a consuming project runs
            // the whole PlayMode suite without filtering by category, so the gate does
            // nothing there and this failed their CI with "Cannot connect to destination
            // host". A package cannot rely on a consumer's runner passing the right
            // filter — correctness has to live in the test.
            //
            // Ignore, not silent-pass: an ignored test with a reason is visible in the
            // report and names what is missing. A test that quietly goes green by doing
            // nothing is the failure this repository has spent two days eliminating, and
            // it is not being reintroduced in the one place whose job is honest numbers.
            string unreachable = await FirstUnreachableAsync();
            if (unreachable != null)
            {
                Assert.Ignore(
                    unreachable + ". This measurement needs a live backend and does not " +
                    "run in CI; start the stack and run it locally, or select/exclude it " +
                    "by its 'LiveBackend' category. " + LiveBackendConfig.Describe());
            }

            Debug.Log("[Measure] endpoints: " + LiveBackendConfig.Describe() +
                      "\n[Measure] NOTE: the tickRate above is only a FALLBACK. The rate " +
                      "actually predicted with comes from the server and is reported per run below.");

            // Each configuration is measured Repeats times, interleaved, and the spread
            // is reported.
            //
            // One sample per configuration cannot tell a regression from a noisy metric,
            // and this one is noisy: a run where nothing in the non-predicting path had
            // changed still moved its burstiness by a third. Interleaved rather than
            // batched because the machine and the backend drift over the length of a run,
            // and batching would put that drift entirely into whichever configuration
            // went last.
            var predictedRuns = new List<Run>();
            var unpredictedRuns = new List<Run>();

            for (var r = 0; r < Repeats; r++)
            {
                predictedRuns.Add(await MeasureAsync(predict: true));
                unpredictedRuns.Add(await MeasureAsync(predict: false));
            }

            Run withPrediction = Representative(predictedRuns);
            Run withoutPrediction = Representative(unpredictedRuns);

            ReportSpread("prediction ON", predictedRuns);
            ReportSpread("prediction OFF", unpredictedRuns);

            // A third run whose only purpose is to make the predictor WRONG, so the
            // correction machinery has something to correct. Without it the healthy run's
            // 0.0000 is unfalsifiable: a predictor that never corrects and one that never
            // needs to are indistinguishable.
            //
            // Its TIMINGS are not comparable with the other two and are not used in the
            // comparison — the avatar is being deliberately mispredicted. Only its
            // MaxCorrection is read.
            var diverging = await MeasureAsync(predict: true, forceDivergence: true);

            Report(withPrediction);
            Report(withoutPrediction);
            Report(diverging);
            ReportComparison(withPrediction, withoutPrediction);

            // ---- Guards that make the numbers mean something ----

            Assert.That(withPrediction.Predicting, Is.True,
                "the predictor refused to run, so the 'with prediction' run measured the " +
                "same code path as the other one. Check tick rate and speed in " +
                "LiveBackendConfig.");

            // A predictor that never reconciles is indistinguishable from a perfectly
            // accurate one by position alone: both just look right. These counters are the
            // only evidence that the loop closed at all.
            Assert.That(withPrediction.PendingPeak, Is.GreaterThan(0),
                "no input was ever pending acknowledgement — inputs are not reaching the " +
                "buffer, so nothing was predicted ahead of the server.");
            Assert.That(withPrediction.ReplayedSteps, Is.GreaterThan(0),
                "no input was ever replayed after a reconcile. Prediction ran open-loop: " +
                "it never rewound to an authoritative position, so its agreement with the " +
                "server is untested and this measurement proves nothing about " +
                "reconciliation.");

            // NOT asserted: MaxCorrection > 0 on the healthy run.
            //
            // The first live run failed exactly that assertion, and the assertion was
            // wrong. On localhost, with no loss and Shared.GameLogic bit-exact on both
            // sides, a correction of 0.0000 is the DESIGNED outcome — it is what ADR-10,
            // the FMA-denying split in Integrate and the golden vectors are all for. The
            // same run disproved the assertion's own diagnosis: `replayed steps 3` means
            // Reconcile fired, which is precisely what open-loop cannot do.
            //
            // "Is reconciliation alive?" is answered by ReplayedSteps, asserted above.
            // "Do the two sides disagree?" is what LastCorrection answers, and on a
            // lossless link the healthy answer is no. Conflating them cost a red run.
            //
            // What that assertion was reaching for is covered instead by the deliberate
            // divergence configuration below, and by ReconciliationDivergenceTests in
            // EditMode, which pins all four readings without needing a backend.

            Assert.That(withPrediction.EffectiveSpeed, Is.EqualTo(LiveBackendConfig.PlayerSpeed).Within(0.001f),
                $"the speed replay used ({withPrediction.EffectiveSpeed}) does not match the " +
                $"server's ({LiveBackendConfig.PlayerSpeed}). Either the server's default " +
                "changed or the wire speed is not being adopted — both make every predicted " +
                "step wrong by the ratio.");

            // ---- The other half of the correction guard ----
            //
            // The divergence check below proves corrections CAN happen. Nothing proved they
            // do NOT happen when nothing should diverge, and that missing half let the
            // harness report `corrections smoothed 20` out of 20 samples — every input
            // diverging by 0.25 units, under the snap threshold, invisible — while
            // measuring a 4x tick-rate mismatch it had introduced itself.
            //
            // On localhost with matched rates and bit-exact shared logic, a healthy run
            // should correct essentially never.

            // Smoothing has to earn its place. Predicted motion being LESS even than
            // unpredicted motion is a regression in the one metric closest to what the
            // user reports, whatever the latency numbers say.
            //
            // Gated on the spread: this comparison is only meaningful when the runs of a
            // single configuration agree more closely than the two configurations differ.
            // Asserting through a noisy metric manufactures both regressions and fixes.
            float onSpread = Spread(predictedRuns);
            float offSpread = Spread(unpredictedRuns);
            float worstSpread = Math.Max(onSpread, offSpread);
            float gap = Math.Abs(withPrediction.FrameDeltaBurstiness - withoutPrediction.FrameDeltaBurstiness);

            if (!float.IsNaN(worstSpread) && gap > worstSpread)
            {
                Assert.That(withPrediction.FrameDeltaBurstiness,
                    Is.LessThan(withoutPrediction.FrameDeltaBurstiness),
                    $"predicted motion is LESS even than unpredicted: " +
                    $"{withPrediction.FrameDeltaBurstiness:F2} against " +
                    $"{withoutPrediction.FrameDeltaBurstiness:F2}, a gap of {gap:F2} " +
                    $"against a within-configuration spread of {worstSpread:F2}. The gap " +
                    "is larger than the noise, so this is real. Smoothing that makes " +
                    "motion less even than no smoothing is not earning its place.");
            }
            else
            {
                Debug.LogWarning(
                    $"[Measure] burstiness comparison NOT asserted: ON " +
                    $"{withPrediction.FrameDeltaBurstiness:F2} vs OFF " +
                    $"{withoutPrediction.FrameDeltaBurstiness:F2} differ by {gap:F2}, " +
                    $"within the {worstSpread:F2} spread between runs of one " +
                    "configuration. The metric cannot resolve a difference this small; " +
                    "raise Repeats or reduce what else is running on the machine.");
            }

            // The most direct statement of "the avatar is frozen most of the time", and
            // unlike burstiness it needs no noise floor to interpret: a still frame is
            // either a frame the avatar did not move on or it is not.
            //
            // The number identifies its own cause. At F frames per simulation tick, a
            // rendered position that only changes once per tick leaves (F-1)/F of frames
            // still — 87.5% at 500 fps against 60 Hz, which is what a build measured at
            // 82.7%. Prediction that interpolates within the tick leaves close to none.
            //
            // 25% is chosen well above the few percent a correctly interpolating
            // predictor produces at any sane frame rate, and far below the (F-1)/F a
            // per-tick render produces at every frame rate above the tick rate.
            const float StillFrameBudget = 25f;

            Assert.That(withPrediction.StillFramePercent, Is.LessThan(StillFrameBudget),
                $"{withPrediction.StillFramePercent:F1}% of frames showed no movement at " +
                $"all while an input was in flight. At {withPrediction.ObservedFps:F0} fps " +
                $"against {withPrediction.TickRateInUse} Hz there are " +
                $"{withPrediction.ObservedFps / Math.Max(1, withPrediction.TickRateInUse):F1} " +
                "frames per tick, so a figure near that ratio means the rendered position " +
                "is advancing once per tick rather than once per frame — the avatar is " +
                "teleporting between still poses, which is what a player calls stutter " +
                "and what no correction counter can see.");

            Assert.That(withPrediction.TickRateDisagrees, Is.False,
                $"predicting at {withPrediction.TickRateInUse} Hz while the wire measures " +
                $"{withPrediction.MeasuredTickRate:F1} Hz. Every predicted step is wrong by " +
                "that ratio, and at these magnitudes it smooths rather than snaps — so it " +
                "reads as soft movement, not as an error.");

            Assert.That(withPrediction.Snaps, Is.Zero,
                "a snap in ordinary localhost play means the client and server disagreed by " +
                "more than half a step, which nothing in a healthy configuration should do.");

            int correctionBudget = Samples / 4;
            Assert.That(withPrediction.SmoothedCorrections, Is.LessThanOrEqualTo(correctionBudget),
                $"{withPrediction.SmoothedCorrections} of {Samples} samples needed a " +
                "correction. On localhost with matched rates and the same shared logic on " +
                "both sides, agreement should be the rule and a correction the exception — " +
                "a correction on nearly every input means a systematic disagreement, and " +
                "the last time this fired it was a 4x tick-rate mismatch that no other " +
                "counter showed.");

            var predictedMedian = Median(withPrediction.Samples.Where(s => !s.VisibleTimedOut)
                .Select(s => s.InputToVisibleMs));
            var unpredictedMedian = Median(withoutPrediction.Samples.Where(s => !s.VisibleTimedOut)
                .Select(s => s.InputToVisibleMs));

            Assert.That(predictedMedian, Is.LessThan(unpredictedMedian),
                "prediction did not make the avatar move sooner, which is the only thing " +
                "it exists to do.");

            // The falsifiability check. A predictor fed a speed the server is not using
            // MUST be corrected; if this is zero too, then corrections never happen at
            // all and the healthy run's 0.0000 meant nothing.
            Assert.That(diverging.Reconciles, Is.GreaterThan(0),
                "the divergence run never reconciled at all, so it cannot say anything " +
                "about whether corrections work.");

            Assert.That(diverging.MaxCorrection, Is.GreaterThan(0f),
                "the client predicted a step while sending the server a zero vector, so " +
                "the two MUST disagree by one step once the tick is acknowledged — and " +
                "the predictor still reported no correction. That means reconcile is not " +
                "comparing against the server's position, and the zero correction on the " +
                "healthy run above is meaningless rather than reassuring.");
        });

        /// <summary>
        /// Names the first backend endpoint that cannot be reached, or null when both can.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Cheap and bounded on purpose</b> — a TCP connect with a short timeout, not
        /// the auth flow. The point is to decide whether to run at all, and a probe that
        /// took as long as the thing it guards would be its own problem.
        /// </para>
        /// <para>
        /// <b>Any exception here is treated as "unreachable", never as a failure.</b> A
        /// throw from the probe is the same situation as a refused connection — no
        /// backend — and surfacing it as a test failure would recreate exactly the bug
        /// this method exists to fix.
        /// </para>
        /// </remarks>
        private static async UniTask<string> FirstUnreachableAsync()
        {
            string gateway = await ProbeAsync(
                LiveBackendConfig.GatewayHost, LiveBackendConfig.GatewayPort, "gateway");
            if (gateway != null) return gateway;

            // Nakama is contacted first by the run, so an unreachable one fails earlier
            // and more confusingly than the gateway. Both are checked.
            return await ProbeAsync(
                LiveBackendConfig.NakamaHost, LiveBackendConfig.NakamaPort, "Nakama");
        }

        private const int ProbeTimeoutMs = 1500;

        private static async UniTask<string> ProbeAsync(string host, int port, string what)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    Task connect = client.ConnectAsync(host, port);
                    Task finished = await Task.WhenAny(connect, Task.Delay(ProbeTimeoutMs)).AsUniTask();

                    if (finished != connect)
                    {
                        return $"no {what} at {host}:{port} — connect timed out after {ProbeTimeoutMs} ms";
                    }

                    if (connect.IsFaulted)
                    {
                        // Observed deliberately: an unobserved faulted Task would surface
                        // later as an unrelated error in whatever test runs next.
                        string why = connect.Exception?.GetBaseException().Message ?? "connect failed";
                        return $"no {what} at {host}:{port} — {why}";
                    }

                    return client.Connected
                        ? null
                        : $"no {what} at {host}:{port} — the socket did not open";
                }
            }
            catch (Exception ex)
            {
                return $"no {what} at {host}:{port} — {ex.Message}";
            }
        }

        /// <param name="forceDivergence">
        /// Send a zero vector while predicting a non-zero one, so the server acknowledges
        /// the tick having moved nowhere while the client predicted a step. See the note
        /// on <c>WrongSpeed</c> above for why the two more obvious approaches — a wrong
        /// speed, and dropping the input — do not produce a divergence at all.
        /// </param>
        private static async UniTask<Run> MeasureAsync(bool predict, bool forceDivergence = false)
        {
            var run = new Run
            {
                Name = forceDivergence
                    ? "prediction ON, predicted vector != sent vector (forced divergence)"
                    : predict ? "prediction ON" : "prediction OFF",
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var ct = cts.Token;

            var auth = new NakamaDeviceAuth();
            // Unique device per run: two runs in one session must not share a player
            // entity, or the second inherits the first's position and speed.
            var jwt = await auth.GetGatewayTokenAsync(
                $"measure-{(forceDivergence ? "diverge" : predict ? "on" : "off")}-{DateTime.UtcNow.Ticks}", ct);

            using var client = new NetworkClient(
                new NetworkSettings
                {
                    GatewayHost = LiveBackendConfig.GatewayHost,
                    GatewayPort = LiveBackendConfig.GatewayPort,
                },
                new DefaultTransportFactory(), new ProtobufWireCodec(), new UnityNetLog());

            // CONNECT FIRST. The prediction timestep comes from the server's join
            // response, so the predictor cannot exist until the join has happened.
            //
            // This ordering is the fix for a defect in this file: the harness used to build
            // PredictionSettings from CUVARA_TICK_RATE before connecting, so against the
            // multi-rate backend it measured a client predicting at 15 against a server
            // integrating at 60 — and reported `corrections smoothed 20` out of 20 samples
            // while every other counter read healthy. The instrument had its own copy of
            // the constant the instrument exists to check.
            await client.ConnectAsync(jwt, LiveBackendConfig.MapId, ct);

            string localId = client.UserId;
            Assert.That(localId, Is.Not.Empty, "joined without a user id");

            var settings = PredictionSettings.FromServer(
                client.TickRate,
                fallbackTickRate: LiveBackendConfig.TickRate,
                LiveBackendConfig.PlayerSpeed,
                MapBounds.Default);

            run.TickRateInUse = settings.TickRate;
            run.TickRateIsFallback = settings.TickRateIsFallback;

            LocalMovePredictor predictor = predict ? new LocalMovePredictor(settings) : null;

            var view = new ProbeView { TrackedId = localId };
            var binder = new WorldViewBinder(view, predictor);
            run.Predicting = binder.IsPredicting;

            long tick = 0;
            var lastSendAt = 0f;
            float lastFrameAt = Time.realtimeSinceStartup;

            // The SEND cadence, which is a client choice and deliberately not the server's
            // integration rate. Conflating the two is what produced the defect above.
            float dt = 1f / LiveBackendConfig.TickRate;

            // Let the world arrive and the local entity spawn before measuring.
            await PumpAsync(client, binder, localId, seconds: 1.5f, ct);

            for (int i = 0; i < Samples; i++)
            {
                // --- settle on zero input ---
                for (int s = 0; s < SettleTicks; s++)
                {
                    tick++;
                    client.Session?.SendInput(tick, 0f, 0f, "");
                    predictor?.RecordInput(tick, 0f, 0f);
                    await PumpAsync(client, binder, localId, dt, ct);
                }

                if (!view.TryGet(localId, out var settled))
                {
                    Assert.Fail("the local entity is not in the view — it never spawned, " +
                                "or the id the server gave us is not the one it is sending.");
                }

                // --- one input, then watch ---
                tick++;
                long sampleTick = tick;
                float t0 = Time.realtimeSinceStartup;

                // The divergence run sends a vector the server will act on (nothing)
                // while predicting one it will not. The tick is still SENT, so it is
                // acknowledged and leaves the buffer — which is what makes the
                // disagreement real rather than merely pending.
                if (lastSendAt > 0f)
                {
                    run.SendGaps.Add(t0 - lastSendAt);
                }

                lastSendAt = t0;

                client.Session?.SendInput(sampleTick, forceDivergence ? 0f : 1f, 0f, "");
                predictor?.RecordInput(sampleTick, 1f, 0f);

                var sample = new Sample { VisibleTimedOut = true, AuthoritativeTimedOut = true };
                bool sawVisible = false, sawAuthoritative = false;

                var previousRendered = settled;
                float previousFrameTime = Time.realtimeSinceStartup;

                while (Time.realtimeSinceStartup - t0 < SampleTimeoutSeconds &&
                       !(sawVisible && sawAuthoritative))
                {
                    binder.Tick(client.World, localId);

                    // One reading per render frame: how far the avatar moved since the
                    // last frame. The distribution of these IS the stutter.
                    if (view.TryGet(localId, out var rendered))
                    {
                        run.FrameDeltas.Add((rendered - previousRendered).magnitude);
                        previousRendered = rendered;

                        float nowFrame = Time.realtimeSinceStartup;
                        run.FrameSeconds.Add(nowFrame - previousFrameTime);
                        previousFrameTime = nowFrame;
                    }

                    if (!sawVisible && view.TryGet(localId, out var now) &&
                        (now - settled).sqrMagnitude > MoveEpsilon * MoveEpsilon)
                    {
                        sample.InputToVisibleMs = (Time.realtimeSinceStartup - t0) * 1000f;
                        sample.VisibleTimedOut = false;
                        sawVisible = true;
                    }

                    if (!sawAuthoritative && client.World.AckTick >= sampleTick)
                    {
                        sample.InputToAuthoritativeMs = (Time.realtimeSinceStartup - t0) * 1000f;
                        sample.AuthoritativeTimedOut = false;
                        sawAuthoritative = true;
                    }

                    if (predictor != null)
                    {
                        run.PendingPeak = Math.Max(run.PendingPeak, predictor.PendingCount);
                        run.MaxCorrection = Math.Max(run.MaxCorrection, predictor.LastCorrection);
                    }

                    // The sampling loop is a frame loop too, and it is the one whose
                    // frame deltas become the burstiness figure.
                    float frameNow = Time.realtimeSinceStartup;
                    binder.AdvanceFrame(frameNow - lastFrameAt);
                    lastFrameAt = frameNow;

                    await UniTask.Yield(PlayerLoopTiming.Update, ct);
                }

                run.Samples.Add(sample);
            }

            run.DistinctPositions = view.DistinctTrackedPositions;
            run.SetStateCalls = view.SetStateCalls;

            if (run.FrameSeconds.Count > 0)
            {
                float mean = run.FrameSeconds.Average();
                run.ObservedFps = mean > 0f ? 1f / mean : 0f;
            }

            run.MeasuredTickRate = binder.TickRate.HasEstimate ? binder.TickRate.EstimatedHz : 0f;
            run.TickRateDisagrees = binder.TickRate.Disagrees(run.TickRateInUse);

            if (predictor != null)
            {
                run.ReplayedSteps = predictor.ReplayedSteps;
                run.Reconciles = predictor.Reconciles;
                run.Snaps = predictor.Snaps;
                run.SmoothedCorrections = predictor.SmoothedCorrections;
                run.EffectiveSpeed = predictor.EffectiveSpeed;
            }
            else
            {
                // Deliberately NOT set from LiveBackendConfig. It was, and it printed
                // "effective speed 5" for a run in which nothing moved, which read as
                // evidence that snapshots were arriving and carrying speed. It was the
                // configured constant echoing back. An instrument may print a measured
                // value or say it has none; printing a constant in the slot where a
                // measurement belongs is how a reader is misled by a working tool.
                run.EffectiveSpeed = float.NaN;
            }

            client.Disconnect();
            return run;
        }

        /// <summary>Drives the binder for a wall-clock duration, as a frame loop would.</summary>
        private static async UniTask PumpAsync(
            NetworkClient client, WorldViewBinder binder, string localId, float seconds, CancellationToken ct)
        {
            float until = Time.realtimeSinceStartup + seconds;
            float last = Time.realtimeSinceStartup;

            while (Time.realtimeSinceStartup < until)
            {
                binder.Tick(client.World, localId);

                // Per frame, not per snapshot. Snapshot processing advances prediction
                // once per arriving snapshot, which is the world rate; a client that
                // renders only then shows the avatar still between snapshots and jumps
                // on the frame one lands. That is what the harness was measuring.
                float now = Time.realtimeSinceStartup;
                binder.AdvanceFrame(now - last);
                last = now;

                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }
        }

        // ---- reporting ----

        private static void Report(Run run)
        {
            var visible = run.Samples.Where(s => !s.VisibleTimedOut).Select(s => s.InputToVisibleMs).ToList();
            var auth = run.Samples.Where(s => !s.AuthoritativeTimedOut).Select(s => s.InputToAuthoritativeMs).ToList();

            Debug.Log(
                $"[Measure] === {run.Name} ===\n" +
                $"  samples                  {visible.Count}/{run.Samples.Count} usable" +
                (run.Samples.Count != visible.Count ? "  (timeouts are counted, not dropped)" : "") + "\n" +
                $"  input -> visible move    {Describe(visible)}\n" +
                $"  input -> authoritative   {Describe(auth)}\n" +
                $"  predicting               {run.Predicting}\n" +
                $"  pending peak             {run.PendingPeak}\n" +
                $"  reconciles               {run.Reconciles}\n" +
                $"  replayed steps           {run.ReplayedSteps}   (zero is normal when nothing was pending)\n" +
                $"  corrections smoothed     {run.SmoothedCorrections}\n" +
                $"  corrections snapped      {run.Snaps}\n" +
                $"  max correction           {run.MaxCorrection:F4} world units\n" +
                $"  max correction in steps  {(run.ExpectedStep > 0f ? (run.MaxCorrection / run.ExpectedStep).ToString("F2") : "n/a")}" +
                    CorrectionShapeNote(run) + "\n" +
                $"  distinct positions seen  {run.DistinctPositions}" +
                    (run.DistinctPositions <= 1
                        ? "   <<< the entity NEVER MOVED — server or spawn, not the probe"
                        : "   (the view was told it moved, so the probe can see motion)") + "\n" +
                $"  SetState calls           {run.SetStateCalls}" +
                    (run.SetStateCalls == 0 ? "   <<< no snapshots reached the binder at all" : "") + "\n" +
                $"  send gap burstiness      {run.SendGapBurstiness:F2}" +
                    (run.SendGapBurstiness > 1.5f
                        ? "   <<< sends are CLUMPING; rpg-mmo-server#100 discards clumped inputs"
                        : "   (1.00 is evenly spaced)") + "\n" +
                $"  effective speed          " +
                    (float.IsNaN(run.EffectiveSpeed)
                        ? "not measured (no predictor in this configuration)"
                        : run.EffectiveSpeed.ToString()) + "\n" +
                $"  TICK RATE IN USE         {run.TickRateInUse} Hz" +
                    (run.TickRateIsFallback ? "  <- FALLBACK, server advertised none" : "  (advertised by the server)") + "\n" +
                $"  tick rate measured       {run.MeasuredTickRate:F1} Hz off the wire" +
                    (run.TickRateDisagrees ? "   <<< DISAGREES with the rate in use" : "   (agrees)") + "\n" +
                $"  --- smoothness (per render frame, while moving) ---\n" +
                $"  frames with NO movement  {run.StillFramePercent:F1}%   <- the stutter; " +
                    "high means the avatar teleports once per input and is frozen between\n" +
                $"  largest single-frame jump {run.MaxFrameDelta:F4} world units\n" +
                $"  mean frame delta         {run.MeanFrameDelta:F4}\n" +
                $"  frame delta std dev      {run.FrameDeltaStdDev:F4}\n" +
                $"  BURSTINESS (max/mean)    {run.FrameDeltaBurstiness:F2}   <- 1.00 is perfectly even; " +
                    "THIS is the comparable number\n" +
                $"  expected jump per input  {run.ExpectedStep:F4}   (speed {run.EffectiveSpeed} / {run.TickRateInUse} Hz)" +
                    ExpectedStepNote(run) + "\n" +
                $"  observed frame rate      {run.ObservedFps:F0} fps   (the raw distances above scale with this)");
        }

        private static void ReportComparison(Run on, Run off)
        {
            var onVisible = on.Samples.Where(s => !s.VisibleTimedOut).Select(s => s.InputToVisibleMs).ToList();
            var offVisible = off.Samples.Where(s => !s.VisibleTimedOut).Select(s => s.InputToVisibleMs).ToList();

            Debug.Log(
                "[Measure] === what prediction removed ===\n" +
                $"  median input -> visible, OFF   {Median(offVisible):F1} ms\n" +
                $"  median input -> visible, ON    {Median(onVisible):F1} ms\n" +
                $"  median interval removed        {Median(offVisible) - Median(onVisible):F1} ms\n" +
                "\n" +
                $"  still frames, OFF              {off.StillFramePercent:F1}%\n" +
                $"  still frames, ON               {on.StillFramePercent:F1}%\n" +
                $"  BURSTINESS, OFF                {off.FrameDeltaBurstiness:F2}   (1.00 = perfectly even)\n" +
                $"  BURSTINESS, ON                 {on.FrameDeltaBurstiness:F2}\n" +
                $"  largest frame jump, OFF        {off.MaxFrameDelta:F4}  at {off.ObservedFps:F0} fps\n" +
                $"  largest frame jump, ON         {on.MaxFrameDelta:F4}  at {on.ObservedFps:F0} fps\n" +
                "  (raw distances scale with frame rate and are NOT comparable between runs;\n" +
                "   burstiness is. Two runs of one build gave 0.0149 and 0.0244 purely because\n" +
                "   they rendered at 336 and 205 fps.)\n" +
                "\n" +
                "  This is input-submitted to avatar-moves, measured in-engine.\n" +
                "  It is NOT keypress-to-visible: the keyboard, OS input stack and display\n" +
                "  pipeline are outside the engine and need external capture. Those legs are\n" +
                "  constant between the two runs, so the DIFFERENCE above is unaffected by\n" +
                "  their absence, but the absolute figures are not a player-felt latency.");
        }

        /// <summary>
        /// Flags a largest-frame-jump that is not a whole number of server steps, or is
        /// more than one.
        /// </summary>
        /// <remarks>
        /// With prediction off the avatar moves only when a snapshot lands, so the largest
        /// frame jump should be the displacement the server produced since the previous
        /// snapshot — one step per input it accepted. A jump that is an exact small
        /// multiple of a step says more inputs were applied per snapshot than expected; one
        /// that is not a multiple at all says the step size itself is wrong. Both are worth
        /// seeing in the output rather than derived afterwards: a run reported 0.2500
        /// against an expected 0.0833 and neither of us could account for the factor of
        /// three from the numbers we had printed.
        /// </remarks>
        /// <summary>
        /// Reads the correction as a count of server steps, which is what separates the
        /// two candidate causes of a persistent sub-threshold correction.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A whole number of steps is a phase problem, not a rate problem.</b> An input
        /// is acknowledged when the server has <i>received</i> it, not when it has finished
        /// integrating it: the hold keeps stepping for up to <c>holdTicks - 1</c> base
        /// ticks afterwards. Replay drops an input as soon as it is acknowledged, so the
        /// steps its hold has not taken yet are dropped with it, and the client differs
        /// from the server by exactly that remainder — a whole number of steps, the same
        /// number every time.
        /// </para>
        /// <para>
        /// <b>A fraction is a rate or arithmetic problem</b>, and would point back at the
        /// timestep, the speed, or the movement model rather than at the hold.
        /// </para>
        /// <para>
        /// A measured 0.1667 at 60/15/5 is 2.00 steps exactly, which is the first case.
        /// Printing the ratio rather than the distance is the difference between a number
        /// that needs arithmetic to interpret and one that names its own cause.
        /// </para>
        /// </remarks>
        private static string CorrectionShapeNote(Run run)
        {
            if (run.ExpectedStep <= 0f || run.MaxCorrection <= 0f) return string.Empty;

            float steps = run.MaxCorrection / run.ExpectedStep;
            float nearest = (float)Math.Round(steps);
            bool whole = nearest >= 1f && Math.Abs(steps - nearest) < 0.05f;

            return whole
                ? $"   <<< {nearest:F0} whole steps — a PHASE error (hold remainder dropped " +
                  "at ack), not a rate error"
                : "   (not a whole number of steps — look at the rate or the arithmetic, " +
                  "not the hold)";
        }

        private static string ExpectedStepNote(Run run)
        {
            if (run.ExpectedStep <= 0f || float.IsNaN(run.MaxFrameDelta) || run.MaxFrameDelta <= 0f)
            {
                return string.Empty;
            }

            float ratio = run.MaxFrameDelta / run.ExpectedStep;
            return $"   observed/expected = {ratio:F2}" +
                   (ratio > 1.5f ? "  <<< more movement per frame than one server step" : "");
        }

        /// <summary>
        /// The run whose burstiness is the median, so the reported figures all come from
        /// one coherent run rather than being averaged across runs.
        /// </summary>
        /// <remarks>
        /// Averaging across runs would produce a set of numbers no single run ever
        /// produced, and the relationships between them — visible latency against
        /// authoritative latency, corrections against burstiness — are the point. The
        /// median is chosen on burstiness because that is the metric under investigation.
        /// </remarks>
        /// <summary>
        /// Range of burstiness across runs of one configuration — the resolution floor of
        /// any comparison between configurations.
        /// </summary>
        private static float Spread(List<Run> runs)
        {
            var b = runs.Select(r => r.FrameDeltaBurstiness).Where(x => !float.IsNaN(x)).ToList();
            return b.Count < 2 ? float.NaN : b.Max() - b.Min();
        }

        private static Run Representative(List<Run> runs)
        {
            if (runs.Count == 1) return runs[0];

            var ordered = runs
                .OrderBy(r => float.IsNaN(r.FrameDeltaBurstiness) ? float.MaxValue : r.FrameDeltaBurstiness)
                .ToList();

            return ordered[ordered.Count / 2];
        }

        private static void ReportSpread(string label, List<Run> runs)
        {
            var burst = runs.Select(r => r.FrameDeltaBurstiness).ToList();
            var steps = runs.Select(r => r.ExpectedStep > 0f ? r.MaxCorrection / r.ExpectedStep : float.NaN).ToList();
            var visible = runs.Select(r => Median(r.Samples.Where(x => !x.VisibleTimedOut).Select(x => x.InputToVisibleMs).ToList())).ToList();

            float lo = burst.Min(), hi = burst.Max();
            float mid = burst.Average();
            float relative = mid > 0f ? (hi - lo) / mid : float.NaN;

            Debug.Log(
                $"[Measure] SPREAD over {runs.Count} runs — {label}\n" +
                $"  burstiness        {string.Join(", ", burst.Select(b => b.ToString("F2")))}" +
                $"   range {lo:F2}..{hi:F2}, spread {relative:P0} of mean\n" +
                $"  correction steps  {string.Join(", ", steps.Select(x => x.ToString("F2")))}\n" +
                $"  input->visible ms {string.Join(", ", visible.Select(x => x.ToString("F1")))}\n" +
                (relative > 0.25f
                    ? "  <<< the runs disagree by more than a quarter of their mean. Do not " +
                      "read a change smaller than this spread as a regression or a fix.\n"
                    : "  (runs agree closely; a change larger than this spread is real)\n"));
        }

        private static string Describe(IReadOnlyCollection<float> xs)
        {
            if (xs.Count == 0) return "no usable samples";
            var sorted = xs.OrderBy(x => x).ToList();
            return $"median {Median(sorted):F1} ms   min {sorted.First():F1}   max {sorted.Last():F1}   " +
                   $"p90 {Percentile(sorted, 0.9f):F1}   mean {sorted.Average():F1}";
        }

        private static float Median(IEnumerable<float> xs)
        {
            var s = xs.OrderBy(x => x).ToList();
            if (s.Count == 0) return float.NaN;
            return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) * 0.5f;
        }

        private static float Percentile(List<float> sorted, float p)
        {
            if (sorted.Count == 0) return float.NaN;
            int i = Mathf.Clamp(Mathf.CeilToInt(p * sorted.Count) - 1, 0, sorted.Count - 1);
            return sorted[i];
        }
    }
}
