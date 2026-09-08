using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Prediction;
using Cuvara.Netcode.Transport;
using Cuvara.Netcode.View;
using NUnit.Framework;
using Shared.GameLogic.Components;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cuvara.Netcode.Tests.PlayMode
{
    /// <summary>
    /// Does <see cref="AckLatencyEstimator.AckAheadOfSend"/> ever fire? Induces the one
    /// condition it guards against and reports which of four outcomes happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> `AckAheadOfSend` counts acknowledgements naming an input tick
    /// this session never sent — a reconnect onto a server that has not yet reaped the
    /// previous session's <c>LastInputTick</c>. Left unguarded that condition sets the floor
    /// an order of magnitude below the route, so the guard matters. It has read <b>0 on every
    /// run since it was added</b>, and a guard nobody has watched act is indistinguishable
    /// from one that cannot act. This converts that into a measurement.
    /// </para>
    /// <para>
    /// <b>It is also the only instrument in this package that can see left-tail
    /// contamination at all</b>, which is the second reason to know whether it works. Three
    /// statistical detectors were built for that job and all three failed for one reason:
    /// each was computed from a fit that included the suspect point, so the line followed the
    /// point down and the discrepancy was absorbed by the thing it was measured against. A
    /// counter that is <i>narrower than the question</i> but independent of the fit beats a
    /// shape test that is general but entangled.
    /// </para>
    /// <para>
    /// <b>WHAT THIS TEST CANNOT SEE, stated here because forgetting it has already cost
    /// time.</b> A left-tail contaminant arising from any cause OTHER than this one — a clock
    /// adjustment, a reordered delivery — would move no counter and bend no statistic that
    /// this package computes. There is <b>no detector for it</b>. A clean result here means
    /// "the one cause we can count did not occur"; it does not mean the low tail is clean.
    /// That sentence is in this test's output as well as in this comment, because the last
    /// time it lived only in a retraction, two people independently designed experiments that
    /// assumed the missing detector still existed. <b>A retracted instrument leaves the
    /// question it was built for standing, and the question keeps recruiting designs that
    /// assume an answer exists</b> — so the gap is written where the reader is, not where the
    /// knowledge is.
    /// </para>
    /// </remarks>
    [TestFixture]
    [Category("LiveBackend")]
    public sealed class AckAheadOfSendInduction
    {
        /// <summary>
        /// How long the first session runs before the reconnect.
        /// </summary>
        /// <remarks>
        /// <b>Long on purpose, and it is what makes the outcomes separable.</b> The condition
        /// is that the stale acknowledgement exceeds the new session's highest sent tick, so
        /// the further the first session's final tick is from the handful the second reaches
        /// before its first snapshot, the less ambiguous every reading below becomes. Five
        /// seconds at the recommended cadence is about 65 ticks against a second session that
        /// will be at 1–3, which is not a margin that noise can close.
        /// </remarks>
        private const double FirstSessionSeconds = 5.0;

        /// <summary>How long to watch the second session for its first acknowledgement.</summary>
        private const double AckWaitSeconds = 5.0;

        [UnityTest]
        public IEnumerator TheReconnectGuardFiresWhenTheConditionIsInduced() => UniTask.ToCoroutine(async () =>
        {
            string unreachable = await LiveBackendProbe.FirstUnreachableAsync();
            if (unreachable != null)
            {
                Assert.Ignore(
                    unreachable + ". This test induces a reconnect against a live backend and " +
                    "does not run in CI; start the stack and run it locally, or select/exclude " +
                    "it by its 'LiveBackend' category. " + LiveBackendConfig.Describe());
            }

            Debug.Log("[Reconnect] config provenance: " + LiveBackendConfig.DescribeProvenance());
            Debug.Log("[Reconnect] endpoints: " + LiveBackendConfig.Describe());

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            CancellationToken ct = cts.Token;

            // THE SAME DEVICE ID FOR BOTH SESSIONS. That is the whole induction: the same
            // Nakama user resolves to the same player on the game server, so the second
            // session meets the first session's LastInputTick. The measurement harness next
            // door deliberately uses a UNIQUE id per run for the opposite reason -- so its
            // runs do not inherit each other's entity -- and copying that here would quietly
            // make this test unable to induce anything.
            string deviceId = $"reconnect-guard-{DateTime.UtcNow.Ticks}";
            var auth = new NakamaDeviceAuth();

            // ---- first session: get the server's LastInputTick well clear of zero ----

            long lastTickBeforeDisconnect = await RunFirstSessionAsync(auth, deviceId, ct);

            double disconnectedAt = Time.realtimeSinceStartupAsDouble;

            // ---- second session: fresh numbering, same user ----

            var observation = await RunSecondSessionAsync(auth, deviceId, disconnectedAt, ct);

            // ---- the precondition block, in the run gate's style ----

            // COMPARED AGAINST MAX-SENT, NOT AGAINST THE FIRST POST-RECONNECT INPUT TICK.
            //
            // The obvious form of this line is `FirstAckTick > 1` -- the stale acknowledgement
            // against the new session's first tick. DO NOT SIMPLIFY IT BACK TO THAT. The guard
            // tests `ackTick > _maxSentTick` (AckLatencyEstimator.RecordAck), and _maxSentTick
            // climbs on every send, so the two agree only for the very first send and diverge
            // immediately after.
            //
            // Worked, because the failure is silent and expensive. Suppose the previous session
            // ended at tick 5. The new session sends 1, 2, 3, 4, 5, 6 while waiting for its
            // first snapshot; _maxSentTick reaches 6; the stale ack of 5 arrives; `5 > 6` is
            // false, so the guard correctly does NOT fire. Compared against the first input tick
            // instead, `5 > 1` is true, this test would call the condition induced, find
            // AckAheadOfSend at zero, and report a Runtime/ DEFECT -- manufacturing the exact
            // false alarm that the defect row exists to make credible. A row-2 failure that
            // turned out to be this test's own bug would cost more than never having built it,
            // because the next real one would be read as another false alarm.
            //
            // So the precondition compares the quantity the guard actually tests. This test was
            // built to catch a precondition check that is not checking the precondition; it must
            // not contain one.
            bool sawAck = observation.FirstAckTick > 0;
            bool induced = sawAck && observation.FirstAckTick > observation.MaxSentAtFirstAck;

            Debug.Log(
                "[Reconnect] === induction precondition ===\n" +
                $"  last input tick before disconnect   {lastTickBeforeDisconnect}\n" +
                $"  first input tick after reconnect    1\n" +
                $"  first ack_tick after reconnect      " +
                    (sawAck ? observation.FirstAckTick.ToString() : "none seen") + "\n" +
                $"  max sent tick when it was observed  {observation.MaxSentAtFirstAck}   " +
                    "<<< the quantity the guard actually tests\n" +
                $"  elapsed disconnect -> first ack     {observation.ElapsedMs:F0} ms\n" +
                $"  condition induced                   " +
                    (induced ? "YES" : "NOT INDUCED") + "\n" +
                $"  AckAheadOfSend                      {observation.AckAheadOfSend}\n" +
                $"  inputs sent in second session       {observation.MaxSentAtEnd}\n" +
                "\n" +
                "  NOTE: this test can only see left-tail contamination caused by an\n" +
                "  acknowledgement naming an unsent tick. A contaminant from any other cause\n" +
                "  moves no counter and bends no statistic this package computes — there is no\n" +
                "  detector for it. A clean result here means the one cause we can count did\n" +
                "  not occur; it does not mean the low tail is clean.");

            // ---- the four outcomes ----

            if (!sawAck)
            {
                Assert.Inconclusive(
                    "NOT INDUCED, and not a result: the second session never saw an " +
                    $"acknowledgement at all within {AckWaitSeconds:F0} s. That is a connection " +
                    "or join problem, not evidence about the guard. Nothing below was measured.");
            }

            if (!induced)
            {
                // WHICH KIND of non-induction, because they mean different things and only one
                // of them is evidence.
                bool serverHeldOldState = observation.FirstAckTick == lastTickBeforeDisconnect;

                if (serverHeldOldState)
                {
                    Assert.Inconclusive(
                        "NOT INDUCED — the induction was TOO SLOW, and this says nothing about " +
                        "the guard. The server did still hold the previous session's " +
                        $"LastInputTick ({observation.FirstAckTick}), so the condition existed, but " +
                        $"this client had already sent up to tick {observation.MaxSentAtFirstAck} " +
                        $"by the time the acknowledgement arrived ({observation.ElapsedMs:F0} ms " +
                        "after disconnect), and the guard tests `ackTick > _maxSentTick`. " +
                        "Reconnect faster, or lengthen FirstSessionSeconds so the stale tick is " +
                        "further clear of what the new session reaches. This is a failure of the " +
                        "METHOD, not an absence of the condition.");
                }

                Assert.Inconclusive(
                    "NOT INDUCED — the server had already reaped the previous session. Its first " +
                    $"acknowledgement was {observation.FirstAckTick}, which belongs to this " +
                    $"session's own numbering rather than the previous session's " +
                    $"{lastTickBeforeDisconnect}, so the condition did not exist to be caught. " +
                    "THIS is the reading that bears on whether the condition arises at all: " +
                    "repeated across fast reconnects it is evidence that the guard protects " +
                    "against something this deployment does not produce. One run is not that " +
                    "evidence.");
            }

            // Induced. The guard MUST have fired.
            Assert.That(observation.AckAheadOfSend, Is.GreaterThan(0),
                "GUARD DEFECT, and this is a Runtime/ failure rather than a flaky test. The " +
                $"condition was induced — the server acknowledged tick {observation.FirstAckTick} " +
                $"while this session had sent no further than {observation.MaxSentAtFirstAck} — " +
                "and AckLatencyEstimator.AckAheadOfSend did not move, so the " +
                "`ackTick > _maxSentTick` discard in RecordAck did not run. Two consequences. " +
                "The floor can now be set by inputs retired by an acknowledgement they never " +
                "earned, which reads an order of magnitude below the real route. And the " +
                "left-tail question reopens from a completely different direction, because this " +
                "counter is the ONLY working left-tail instrument in the package — if it does " +
                "not fire, nothing in this codebase can see that contamination at all. Start at " +
                "AckLatencyEstimator.RecordAck and at whether the binder is feeding it this " +
                "session's ticks.");

            Debug.Log(
                "[Reconnect] GUARD FIRES. The condition is real and reachable, and the discard " +
                $"caught {observation.AckAheadOfSend} pending input(s). That is the rate this " +
                "deployment produces under a deliberate fast reconnect — an upper bound on what " +
                "it produces by accident, not a measurement of the accidental rate.");
        });

        private static async UniTask<long> RunFirstSessionAsync(
            NakamaDeviceAuth auth, string deviceId, CancellationToken ct)
        {
            string jwt = await auth.GetGatewayTokenAsync(deviceId, ct);

            using var client = new NetworkClient(
                new NetworkSettings
                {
                    GatewayHost = LiveBackendConfig.GatewayHost,
                    GatewayPort = LiveBackendConfig.GatewayPort,
                },
                new DefaultTransportFactory(), new ProtobufWireCodec(), new UnityNetLog());

            await client.ConnectAsync(jwt, LiveBackendConfig.MapId, ct);
            Assert.That(client.UserId, Is.Not.Empty, "first session joined without a user id");

            var view = new CountingView();
            var binder = new WorldViewBinder(view, BuildPredictor(client));

            var schedule = new InputSendSchedule();
            schedule.Start(LiveBackendConfig.InputSendHz, Time.realtimeSinceStartupAsDouble);

            long tick = 0;
            double until = Time.realtimeSinceStartupAsDouble + FirstSessionSeconds;

            while (Time.realtimeSinceStartupAsDouble < until)
            {
                if (schedule.SecondsUntilDue(Time.realtimeSinceStartupAsDouble) <= 0.0)
                {
                    tick++;
                    client.Session?.SendInput(tick, 1f, 0f, "");
                    binder.NoteInputSent(tick);
                    schedule.NoteSent(Time.realtimeSinceStartupAsDouble);
                }

                binder.Tick(client.World, client.UserId);
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }

            Debug.Log($"[Reconnect] first session sent {tick} inputs as '{client.UserId}', " +
                      $"server acked {client.World.AckTick}");

            // Abrupt, not graceful: a graceful disconnect invites the server to reap the
            // player immediately, which is the one thing that would prevent the condition.
            client.Disconnect();
            return tick;
        }

        private readonly struct Observation
        {
            public readonly long FirstAckTick;
            public readonly long MaxSentAtFirstAck;
            public readonly long MaxSentAtEnd;
            public readonly int AckAheadOfSend;
            public readonly double ElapsedMs;

            public Observation(long firstAck, long maxAtAck, long maxAtEnd, int ahead, double elapsedMs)
            {
                FirstAckTick = firstAck;
                MaxSentAtFirstAck = maxAtAck;
                MaxSentAtEnd = maxAtEnd;
                AckAheadOfSend = ahead;
                ElapsedMs = elapsedMs;
            }
        }

        private static async UniTask<Observation> RunSecondSessionAsync(
            NakamaDeviceAuth auth, string deviceId, double disconnectedAt, CancellationToken ct)
        {
            string jwt = await auth.GetGatewayTokenAsync(deviceId, ct);

            using var client = new NetworkClient(
                new NetworkSettings
                {
                    GatewayHost = LiveBackendConfig.GatewayHost,
                    GatewayPort = LiveBackendConfig.GatewayPort,
                },
                new DefaultTransportFactory(), new ProtobufWireCodec(), new UnityNetLog());

            await client.ConnectAsync(jwt, LiveBackendConfig.MapId, ct);

            // A FRESH binder, so a fresh AckLatencyEstimator whose _maxSentTick starts at 0
            // and whose input numbering starts at 1. That is what a reconnecting client does,
            // and it is the half of the condition this side supplies.
            var view = new CountingView();
            var binder = new WorldViewBinder(view, BuildPredictor(client));

            var schedule = new InputSendSchedule();
            schedule.Start(LiveBackendConfig.InputSendHz, Time.realtimeSinceStartupAsDouble);

            long tick = 0;
            long firstAck = 0, maxSentAtFirstAck = 0;
            double elapsedMs = 0;
            double until = Time.realtimeSinceStartupAsDouble + AckWaitSeconds;

            while (Time.realtimeSinceStartupAsDouble < until)
            {
                if (schedule.SecondsUntilDue(Time.realtimeSinceStartupAsDouble) <= 0.0)
                {
                    tick++;
                    client.Session?.SendInput(tick, 1f, 0f, "");
                    binder.NoteInputSent(tick);
                    schedule.NoteSent(Time.realtimeSinceStartupAsDouble);
                }

                // OBSERVED BEFORE binder.Tick, DELIBERATELY. Tick is what calls RecordAck, so
                // reading the ack and our own max-sent here captures the pair the estimator is
                // about to evaluate. Sampling after Tick would report the state the guard was
                // judged on only by luck, and this test exists to be precise about exactly
                // that comparison.
                long ack = client.World.AckTick;
                if (firstAck == 0 && ack > 0)
                {
                    firstAck = ack;
                    maxSentAtFirstAck = tick;
                    elapsedMs = (Time.realtimeSinceStartupAsDouble - disconnectedAt) * 1000.0;
                }

                binder.Tick(client.World, client.UserId);
                await UniTask.Yield(PlayerLoopTiming.Update, ct);

                // Once the acknowledgement has been folded in there is nothing more to learn;
                // stopping promptly keeps the second session short, which is what keeps
                // max-sent low and the outcomes separable.
                if (firstAck > 0 && binder.AckLatency.AckAheadOfSend > 0) break;
            }

            client.Disconnect();
            return new Observation(
                firstAck, maxSentAtFirstAck, tick, binder.AckLatency.AckAheadOfSend, elapsedMs);
        }

        private static LocalMovePredictor BuildPredictor(NetworkClient client)
        {
            var settings = PredictionSettings.FromServer(
                client.TickRate,
                fallbackTickRate: LiveBackendConfig.FallbackTickRate,
                LiveBackendConfig.PlayerSpeed,
                MapBounds.Default);

            return new LocalMovePredictor(settings);
        }

        /// <summary>A view that records nothing. This test measures a counter, not a picture.</summary>
        private sealed class CountingView : IEntityView
        {
            private readonly HashSet<string> _live = new HashSet<string>();

            public void Spawn(string id, bool isLocal, string type) => _live.Add(id);

            public void Despawn(string id) => _live.Remove(id);

            public void SetState(string id, float x, float y, int hp, int maxHp)
            {
            }
        }
    }
}
