using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Shared.GameLogic.Components;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The predictor's call trace: recording changes nothing, costs nothing when off, and a recorded
    /// session replays to exactly the same corrections.
    /// </summary>
    /// <remarks>
    /// The trace exists to replay live sessions headless: three stall fixes passed the synthetic
    /// harness and not the live client (rpg-factory testlab S13), because the harness modelled the
    /// client's frame order and timing instead of recording them. A replay is only evidence if it is
    /// exact, which is what these tests pin, on the harness's own sessions.
    /// </remarks>
    [TestFixture]
    public sealed class PredictorTraceTests
    {
        private const int Hz = 60;
        private const float Speed = 5f;

        /// <summary>A stall in the client's full frame order: the shape the trace is for.</summary>
        private static PredictionHarness.Config StallScenario(PredictionHarness.Motion motion)
        {
            PredictionHarness.Config c = ServerStallRecoveryTests.Scenario(motion);
            c.FrameSeconds = 1.0 / 500.0;
            c.HitchAtSeconds = 12.0;
            c.HitchSeconds = 0.5;
            c.InputBeforeSnapshot = true;
            c.DeltaTimeLagsOneFrame = true;
            c.SnapshotsVisibleNextFrame = true;
            c.MaxFrameDeltaSeconds = 1.0 / 3.0;
            c.DurationSeconds = 16.0;
            return c;
        }

        private static List<PredictorTraceEvent> Record(PredictionHarness.Config c, out PredictionHarness.Result result)
        {
            var events = new List<PredictorTraceEvent>();
            c.Trace = (in PredictorTraceEvent e) => events.Add(e);
            result = PredictionHarness.Run(c);
            return events;
        }

        private static List<PredictorTraceEvent> Results(List<PredictorTraceEvent> events) =>
            events.FindAll(e => e.Call == PredictorCall.ReconcileResult);

        /// <summary>Replays <paramref name="events"/> into a predictor built as the harness builds its own.</summary>
        private static List<PredictorTraceEvent> Replay(IEnumerable<PredictorTraceEvent> events, bool replaySteering)
        {
            var predictor = new LocalMovePredictor(new PredictionSettings(Hz, Speed, MapBounds.Default));
            var replayed = new List<PredictorTraceEvent>();
            predictor.Trace = (in PredictorTraceEvent e) =>
            {
                if (e.Call == PredictorCall.ReconcileResult) replayed.Add(e);
            };
            var replayer = new PredictorTraceReplayer(predictor) { ReplaySteering = replaySteering };
            foreach (var e in events) replayer.Apply(in e);
            return replayed;
        }

        private static void AssertSameResults(List<PredictorTraceEvent> expected, List<PredictorTraceEvent> actual, string what)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Count), what + ": reconcile count");
            for (int i = 0; i < expected.Count; i++)
            {
                PredictorTraceEvent x = expected[i], y = actual[i];
                Assert.That(
                    y.A == x.A && y.B == x.B && y.C == x.C && y.X == x.X && y.Y == x.Y && y.Z == x.Z && y.W == x.W,
                    Is.True,
                    $"{what}: reconcile #{i} differs: base {x.A}/{y.A}, tickError {x.B}/{y.B}, " +
                    $"correction {x.X}/{y.X}, position ({x.Y},{x.Z})/({y.Y},{y.Z})");
            }
        }

        [TestCase(PredictionHarness.Motion.Circle)]
        [TestCase(PredictionHarness.Motion.StartStop)]
        public void ARecordedSession_ReplaysToExactlyTheSameCorrections(PredictionHarness.Motion motion)
        {
            List<PredictorTraceEvent> events = Record(StallScenario(motion), out PredictionHarness.Result r);
            List<PredictorTraceEvent> recorded = Results(events);
            Assume.That(recorded.Count, Is.GreaterThan(100), "precondition: the session reconciled");
            Assume.That(r.StallResyncs, Is.EqualTo(1), "precondition: the session had its stall");

            AssertSameResults(recorded, Replay(events, replaySteering: false), "predictor replay");
            AssertSameResults(recorded, Replay(events, replaySteering: true), "steering replay");
        }

        [Test]
        public void TheNdjsonForm_RoundTripsEveryEventExactly()
        {
            List<PredictorTraceEvent> events = Record(StallScenario(PredictionHarness.Motion.Circle), out _);
            var parsed = new List<PredictorTraceEvent>(events.Count);
            var sb = new StringBuilder();
            foreach (var e in events)
            {
                sb.Clear();
                e.AppendJson(sb);
                Assert.That(PredictorTraceEvent.TryParseJson(sb.ToString(), out PredictorTraceEvent back), Is.True, sb.ToString());
                Assert.That(back.Equals(e), Is.True, "round trip: " + sb);
                parsed.Add(back);
            }

            AssertSameResults(Results(events), Replay(parsed, replaySteering: false), "replay from NDJSON");
        }

        [TestCase(PredictionHarness.Motion.Circle)]
        [TestCase(PredictionHarness.Motion.StartStop)]
        public void Tracing_ChangesNothingThePredictorDoes(PredictionHarness.Motion motion)
        {
            PredictionHarness.Result off = PredictionHarness.Run(StallScenario(motion));
            Record(StallScenario(motion), out PredictionHarness.Result on);

            Assert.That(on.ToString(), Is.EqualTo(off.ToString()));
            Assert.That(on.CorrectionLog.Count, Is.EqualTo(off.CorrectionLog.Count));
            for (int i = 0; i < off.CorrectionLog.Count; i++)
            {
                Assert.That(on.CorrectionLog[i], Is.EqualTo(off.CorrectionLog[i]), $"reconcile #{i}");
            }
        }

#if !UNITY_5_3_OR_NEWER
        // Headless only: GC.GetAllocatedBytesForCurrentThread is exact on .NET; the Editor's runtime
        // may count differently.
        [Test]
        public void Tracing_AllocatesNothing_OffOrOn()
        {
            long Measure(PredictorTraceSink sink)
            {
                var p = new LocalMovePredictor(new PredictionSettings(Hz, Speed, MapBounds.Default));
                p.Trace = sink;
                p.SeedBaseTick(1000);
                p.Reconcile(new Vec2(0f, 0f), 0, 1000, 0);
                long tick = 1, server = 1000;
                for (int warm = 0; warm < 200; warm++) Step(p, ref tick, ref server);

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 2000; i++) Step(p, ref tick, ref server);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }

            void Step(LocalMovePredictor p, ref long tick, ref long server)
            {
                p.RecordInput(tick++, 1f, 0f);
                p.Advance(1f / 120f);
                server += 1;
                p.SteerToServerTick(server, 4);
                p.Reconcile(new Vec2(p.SimulatedPosition.X, p.SimulatedPosition.Y), tick - 2, server, server - 1);
            }

            long off = Measure(null);
            long on = Measure((in PredictorTraceEvent e) => { });
            TestContext.WriteLine($"allocated over 2000 frames: off {off} B, on {on} B");
            Assert.That(on, Is.EqualTo(off), "tracing adds no allocation per call");
            Assert.That(off, Is.Zero, "and with tracing off the predictor's steady state allocates nothing");
        }
#endif
    }
}
