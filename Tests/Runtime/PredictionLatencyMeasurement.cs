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

        /// <summary>Frames of zero input before a sample, to settle.</summary>
        private const int SettleTicks = 6;

        /// <summary>Give up on a sample after this long and report it, rather than hang.</summary>
        private const float SampleTimeoutSeconds = 3f;

        private const float MoveEpsilon = 0.001f;

        // How the divergence run forces a disagreement, and why not by a wrong speed:
        // the wire carries per-entity speed now (field 9) and the binder feeds it to
        // SetServerSpeed on every snapshot, so a deliberately wrong PredictionSettings
        // speed is corrected back to the server's within one snapshot and no divergence
        // survives. Dropping an input instead cannot be undone that way — the client
        // predicts a step the server never takes, and the next snapshot must correct it.

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

            public float StillFramePercent =>
                FrameDeltas.Count == 0 ? float.NaN
                    : 100f * FrameDeltas.Count(d => d <= 1e-6f) / FrameDeltas.Count;

            public float MaxFrameDelta => FrameDeltas.Count == 0 ? float.NaN : FrameDeltas.Max();

            public float MeanFrameDelta => FrameDeltas.Count == 0 ? float.NaN : FrameDeltas.Average();

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

            Debug.Log("[Measure] " + LiveBackendConfig.Describe());

            var withPrediction = await MeasureAsync(predict: true);
            var withoutPrediction = await MeasureAsync(predict: false);

            // A third run whose only purpose is to make the predictor WRONG, so the
            // correction machinery has something to correct. Without it the healthy run's
            // 0.0000 is unfalsifiable: a predictor that never corrects and one that never
            // needs to are indistinguishable.
            //
            // Its TIMINGS are not comparable with the other two and are not used in the
            // comparison — dropping inputs delays acknowledgement by design. Only its
            // MaxCorrection is read.
            var diverging = await MeasureAsync(predict: true, dropSampleInput: true);

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
            Assert.That(diverging.MaxCorrection, Is.GreaterThan(0f),
                "the predictor predicted movement from inputs the server never received, " +
                "and STILL reported no correction. That means reconcile is not comparing " +
                "against the server's position at all, and the zero correction on the " +
                "healthy run above is meaningless rather than reassuring.");
        });

        /// <param name="dropSampleInput">
        /// Predict the sample input locally but never send it, so the server cannot have
        /// applied it. Forces a real disagreement; see the note above on why a wrong speed
        /// no longer works for this.
        /// </param>
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

        private static async UniTask<Run> MeasureAsync(bool predict, bool dropSampleInput = false)
        {
            var run = new Run
            {
                Name = dropSampleInput
                    ? "prediction ON, sample input DROPPED (forced divergence; timings not comparable)"
                    : predict ? "prediction ON" : "prediction OFF",
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var ct = cts.Token;

            var auth = new NakamaDeviceAuth();
            // Unique device per run: two runs in one session must not share a player
            // entity, or the second inherits the first's position and speed.
            var jwt = await auth.GetGatewayTokenAsync(
                $"measure-{(dropSampleInput ? "drop" : predict ? "on" : "off")}-{DateTime.UtcNow.Ticks}", ct);

            using var client = new NetworkClient(
                new NetworkSettings
                {
                    GatewayHost = LiveBackendConfig.GatewayHost,
                    GatewayPort = LiveBackendConfig.GatewayPort,
                },
                new DefaultTransportFactory(), new ProtobufWireCodec(), new UnityNetLog());

            LocalMovePredictor predictor = predict
                ? new LocalMovePredictor(new PredictionSettings(
                    LiveBackendConfig.TickRate, LiveBackendConfig.PlayerSpeed, MapBounds.Default))
                : null;

            var view = new ProbeView();
            var binder = new WorldViewBinder(view, predictor);
            run.Predicting = binder.IsPredicting;

            await client.ConnectAsync(jwt, LiveBackendConfig.MapId, ct);

            string localId = client.UserId;
            Assert.That(localId, Is.Not.Empty, "joined without a user id");

            long tick = 0;
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

                // The divergence run predicts this step but never sends it.
                if (!dropSampleInput)
                {
                    client.Session?.SendInput(sampleTick, 1f, 0f, "");
                }
                predictor?.RecordInput(sampleTick, 1f, 0f);

                var sample = new Sample { VisibleTimedOut = true, AuthoritativeTimedOut = true };
                bool sawVisible = false, sawAuthoritative = false;

                var previousRendered = settled;

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

                    await UniTask.Yield(PlayerLoopTiming.Update, ct);
                }

                run.Samples.Add(sample);
            }

            if (predictor != null)
            {
                run.ReplayedSteps = predictor.ReplayedSteps;
                run.Snaps = predictor.Snaps;
                run.SmoothedCorrections = predictor.SmoothedCorrections;
                run.EffectiveSpeed = predictor.EffectiveSpeed;
            }

            client.Disconnect();
            return run;
        }

        /// <summary>Drives the binder for a wall-clock duration, as a frame loop would.</summary>
        private static async UniTask PumpAsync(
            NetworkClient client, WorldViewBinder binder, string localId, float seconds, CancellationToken ct)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                binder.Tick(client.World, localId);
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
                $"  replayed steps           {run.ReplayedSteps}\n" +
                $"  corrections smoothed     {run.SmoothedCorrections}\n" +
                $"  corrections snapped      {run.Snaps}\n" +
                $"  max correction           {run.MaxCorrection:F4} world units\n" +
                $"  effective speed          {run.EffectiveSpeed}\n" +
                $"  --- smoothness (per render frame, while moving) ---\n" +
                $"  frames with NO movement  {run.StillFramePercent:F1}%   <- the stutter; " +
                    "high means the avatar teleports once per input and is frozen between\n" +
                $"  largest single-frame jump {run.MaxFrameDelta:F4} world units\n" +
                $"  mean frame delta         {run.MeanFrameDelta:F4}\n" +
                $"  frame delta std dev      {run.FrameDeltaStdDev:F4}   <- flat when smooth");
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
                $"  largest frame jump, OFF        {off.MaxFrameDelta:F4}\n" +
                $"  largest frame jump, ON         {on.MaxFrameDelta:F4}\n" +
                "\n" +
                "  This is input-submitted to avatar-moves, measured in-engine.\n" +
                "  It is NOT keypress-to-visible: the keyboard, OS input stack and display\n" +
                "  pipeline are outside the engine and need external capture. Those legs are\n" +
                "  constant between the two runs, so the DIFFERENCE above is unaffected by\n" +
                "  their absence, but the absolute figures are not a player-felt latency.");
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
