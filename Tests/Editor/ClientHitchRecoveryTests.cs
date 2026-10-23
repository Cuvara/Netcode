using NUnit.Framework;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// A client stall: one frame of 0.5 s or 2 s (a GC pause, a scene load, a window drag) while
    /// the server keeps ticking. The stall may cost the local player one correction; it must not
    /// cost a stream of them, and the clock must be back on the server's tick line at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect (found by this fixture, predates 0.48.0: v0.20.0, 137feff).</b>
    /// <c>Advance</c> discards frame time beyond the catch-up budget (<c>MaxCatchUpTicks</c>, 15
    /// ticks at 60 Hz), so a stall left the base tick behind the server's by the stall minus
    /// 0.25 s. <c>SteerToServerTick</c> set the clock only beyond two seconds of error and
    /// otherwise steered it by at most 0.5 tick per snapshot, about 7.5 ticks a second. Until
    /// the lag closed the history missed and the ack-offset median trailed the moving clock:
    /// a 0.5 s stall corrected the player by a whole step 24 / 16 times (circle / start-stop),
    /// a 2 s stall 80 / 58 times, with the clock still 30 ticks behind 12 s later.
    /// </para>
    /// <para>
    /// <b>The fix.</b> A stall is a phase event, so the steer jumps the clock onto the server's
    /// tick line: after a clamped frame when the error is outside the steering band, or before
    /// the clamp when the clock is behind by more than the catch-up budget (the stalled frame's
    /// snapshot is handled before that frame's <c>Advance</c>; the jumped time is credited
    /// against that <c>Advance</c>). Pending inputs, holds and history are kept.
    /// </para>
    /// <para>
    /// <b>Why the bound is two steps, not one.</b> One-step corrections at starts and stops
    /// occur WITHOUT any stall, depending only on where the input cadence falls against the
    /// base tick: with no stall, the start-stop run measures 1 to 8 of them in 21 s across
    /// first-send phases 0.25..0.32 s. A stall re-phases the cadence (sending resumes when the
    /// client does), so a one-step correction after it is not evidence about the stall. Two
    /// steps or more is: before the fix every case here had several.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class ClientHitchRecoveryTests
    {
        private const double EventAt = 12.0;

        private static int CorrectionsFrom(PredictionHarness.Result r, double from, float steps)
        {
            int n = 0;
            foreach (var (at, correction, _) in r.CorrectionLog)
            {
                if (at >= from && correction >= r.StepLength * steps * 0.999f) n++;
            }

            return n;
        }

        private static int CorrectionsBetween(PredictionHarness.Result r, double from, double to, float steps)
        {
            int n = 0;
            foreach (var (at, correction, _) in r.CorrectionLog)
            {
                if (at >= from && at < to && correction >= r.StepLength * steps * 0.999f) n++;
            }

            return n;
        }

        [TestCase(PredictionHarness.Motion.Circle, 0.5)]
        [TestCase(PredictionHarness.Motion.Circle, 2.0)]
        [TestCase(PredictionHarness.Motion.StartStop, 0.5)]
        [TestCase(PredictionHarness.Motion.StartStop, 2.0)]
        public void AClientStall_CostsAtMostOneCorrection_AndTheClockIsBackAtOnce(
            PredictionHarness.Motion motion, double stallSeconds)
        {
            PredictionHarness.Config c = ServerStallRecoveryTests.Scenario(motion);
            c.HitchAtSeconds = EventAt;
            c.HitchSeconds = stallSeconds;
            PredictionHarness.Result r = PredictionHarness.Run(c);
            TestContext.WriteLine($"{stallSeconds} s client stall: {r}");
            Assume.That(r.Reconciles, Is.GreaterThan(250), "precondition: the run reconciled");

            int k = 0;
            foreach (var (at, correction, tickError) in r.CorrectionLog)
            {
                if (at < EventAt) continue;
                if (k < 6)
                {
                    TestContext.WriteLine($"  after stall #{k}: t={at:F3} correction={correction / r.StepLength:F2} steps tickError={tickError}");
                }

                // The first reconcile runs on the stalled frame itself, the second already on the
                // jumped clock: from then on, for the two seconds after the stall, the clock is
                // within a tick of the server's line. (Later, the lead target itself moves when the
                // staleness fit settles -- ordinary steering, the same in a run with no stall.)
                if (k >= 1 && at < EventAt + stallSeconds + 2.0)
                {
                    Assert.That(tickError, Is.InRange(-1L, 1L),
                        $"snapshot #{k} after the stall (t={at:F3}): the clock is {tickError} ticks off. {r}");
                }

                k++;
            }

            int large = CorrectionsFrom(r, EventAt, 2f);
            Assert.That(large, Is.LessThanOrEqualTo(1),
                $"after one {stallSeconds} s stall the reconcile corrected by two steps or more {large} times. {r}");
            Assert.That(CorrectionsFrom(r, EventAt + stallSeconds + 1.0, 2f), Is.Zero,
                "a second after the stall nothing of it is left to correct. " + r);
            Assert.That(r.StallResyncs, Is.EqualTo(1), "one stall, one clock jump. " + r);
            Assert.That(r.FinalTickError, Is.InRange(-2L, 2L), r.ToString());
        }

        /// <summary>
        /// The same stalls in the shape the live client sees them: the backlog is delivered oldest
        /// first, one snapshot every 10 ms, not handed over whole in the stall-end frame, and the
        /// frame time reaching the predictor is capped as Unity caps <c>Time.deltaTime</c>.
        /// </summary>
        /// <remarks>
        /// The test above passed while the live client corrected the player 6 to 15 times for one
        /// 2 s stall, every correction a multiple of 16 ticks (rpg-factory testlab S13, runs
        /// 20261022T194438Z-S13-vdqw and 20261022T195013Z-S13-q3rr). The harness applied only the
        /// newest of the arrived snapshots, so the first steer after the stall saw a current tick;
        /// live, it saw the oldest snapshot of the backlog, and every later one was four ticks
        /// newer while the clock had moved by less than one.
        /// </remarks>
        [TestCase(PredictionHarness.Motion.Circle, 0.5, 1.0 / 120.0)]
        [TestCase(PredictionHarness.Motion.Circle, 2.0, 1.0 / 120.0)]
        [TestCase(PredictionHarness.Motion.StartStop, 0.5, 1.0 / 120.0)]
        [TestCase(PredictionHarness.Motion.StartStop, 2.0, 1.0 / 120.0)]
        [TestCase(PredictionHarness.Motion.Circle, 2.0, 1.0 / 500.0)]
        public void AClientStall_WithTheBacklogDeliveredAtTheTransportsPace_CostsAtMostOneCorrection(
            PredictionHarness.Motion motion, double stallSeconds, double frameSeconds)
        {
            PredictionHarness.Config c = ServerStallRecoveryTests.Scenario(motion);
            c.FrameSeconds = frameSeconds;
            c.HitchAtSeconds = EventAt;
            c.HitchSeconds = stallSeconds;
            c.BacklogSpacingSeconds = 0.010;
            c.MaxFrameDeltaSeconds = 1.0 / 3.0;
            PredictionHarness.Result r = PredictionHarness.Run(c);
            TestContext.WriteLine($"{stallSeconds} s client stall, paced backlog, {frameSeconds * 1000.0:F1} ms frames: {r}");
            Assume.That(r.Reconciles, Is.GreaterThan(250), "precondition: the run reconciled");

            // The backlog is the stall's snapshots (15 a second) at 10 ms each; give it that and
            // two snapshot intervals before the clock is judged.
            double drained = EventAt + stallSeconds + stallSeconds * 15.0 * 0.010 + 0.15;
            int k = 0;
            foreach (var (at, correction, tickError) in r.CorrectionLog)
            {
                if (at < EventAt) continue;
                if (k++ < 15 || correction >= r.StepLength * 0.999f)
                {
                    TestContext.WriteLine($"  after stall: t={at:F3} correction={correction / r.StepLength:F2} steps tickError={tickError}");
                }

                if (at >= drained && at < drained + 2.0)
                {
                    Assert.That(tickError, Is.InRange(-1L, 1L),
                        $"t={at:F3}: once the backlog is in, the clock is within a tick of the server's line. {r}");
                }
            }

            // Judged over the stall's reach (until two seconds after the backlog), not to the end of
            // the run. Later, AckLatencyEstimator's ladder test can refuse its floor and accept it
            // again, which moves the lead target by its whole floor (4 ticks here) and costs a
            // correction of about two steps. That happens with no stall at all: in 3 of 12 seeds
            // of this scenario without a stall the lead target changed 2 to 18 times. With the
            // default seed it happens 7 s after this stall, at 21.2 s, because the stall changed
            // which acknowledgements were sampled. It is not the stall's correction, and it is
            // reported separately rather than being fixed here.
            int large = CorrectionsBetween(r, EventAt, drained + 2.0, 2f);
            Assert.That(large, Is.LessThanOrEqualTo(1),
                $"after one {stallSeconds} s stall with a paced backlog the reconcile corrected by two steps or more {large} times. {r}");
            Assert.That(CorrectionsBetween(r, drained + 1.0, drained + 2.0, 2f), Is.Zero,
                "a second after the backlog nothing of the stall is left to correct. " + r);
            Assert.That(r.StallResyncs, Is.EqualTo(1), "one stall, one clock jump. " + r);

            // No end-of-run tick error check, unlike the test above: with the default seed the lead
            // target is still flapping by the acknowledgement floor at the end of the run (see
            // above), and the clock follows the target. The clock is judged where the stall
            // reaches it, in the loop above.
        }

        /// <summary>
        /// The stall in the client's own frame order: the stall-end frame samples and records its
        /// input first (<c>DotsWorldBridge.Update</c>), and only then is the frame's snapshot
        /// steered on and reconciled (<c>LocalPredictionSystem</c>). Both delivery shapes: the
        /// whole backlog read in the stall-end frame (the transport since 0.48.0), and paced.
        /// </summary>
        /// <remarks>
        /// The tests above record the input after the snapshot, so the jump always came first and
        /// the stall-end input landed on the new tick line. Live, it is recorded on the old one, a
        /// whole stall behind (rpg-factory testlab S13, run 20261022T222006Z-S13-uwrf): the first
        /// reconcile replayed that input from its old tick on top of the server's position, which
        /// moved the player 14 steps too far, and the next snapshot took 12 of them back. That made
        /// two local corrections for one stall.
        /// </remarks>
        [TestCase(PredictionHarness.Motion.Circle, 0.5, false, 1.0 / 120.0)]
        [TestCase(PredictionHarness.Motion.Circle, 2.0, false, 1.0 / 120.0)]
        [TestCase(PredictionHarness.Motion.StartStop, 0.5, false, 1.0 / 120.0)]
        [TestCase(PredictionHarness.Motion.StartStop, 2.0, false, 1.0 / 120.0)]
        [TestCase(PredictionHarness.Motion.Circle, 2.0, false, 1.0 / 500.0)]
        [TestCase(PredictionHarness.Motion.Circle, 2.0, true, 1.0 / 120.0)]
        [TestCase(PredictionHarness.Motion.StartStop, 2.0, true, 1.0 / 120.0)]
        public void AClientStall_WithTheInputSampledBeforeTheSnapshot_CostsAtMostOneCorrection(
            PredictionHarness.Motion motion, double stallSeconds, bool pacedBacklog, double frameSeconds) =>
            RunClientOrderStall(motion, stallSeconds, pacedBacklog, frameSeconds, deltaLags: false);

        /// <summary>
        /// The client's order with Unity's frame timing as well: <c>Time.deltaTime</c> reports the
        /// previous frame, so the stall reaches <c>Advance</c> one frame after the stall-end frame
        /// has recorded its input and applied the backlog.
        /// </summary>
        /// <remarks>
        /// Live on 461be3a every stall cost two corrections (rpg-factory testlab S13, runs
        /// 20261023T010311Z-S13-3zhl, -j8xw, -9zy5). In the stall frame the clock jumped onto the
        /// server's line, and the jump credit was spent on that frame's 1.3 ms deltaTime. The next
        /// frame then advanced the capped 0.333 s (15 ticks clamped): the player stepped about 1.25 u
        /// forward, the clock ran 15 ticks ahead, and the next snapshot jumped it back and corrected
        /// the player back.
        /// </remarks>
        [TestCase(PredictionHarness.Motion.Circle, 0.5, false, 1.0 / 120.0)]
        [TestCase(PredictionHarness.Motion.Circle, 2.0, false, 1.0 / 120.0)]
        [TestCase(PredictionHarness.Motion.StartStop, 0.5, false, 1.0 / 120.0)]
        [TestCase(PredictionHarness.Motion.StartStop, 2.0, false, 1.0 / 120.0)]
        [TestCase(PredictionHarness.Motion.Circle, 2.0, false, 1.0 / 500.0)]
        [TestCase(PredictionHarness.Motion.Circle, 2.0, true, 1.0 / 120.0)]
        [TestCase(PredictionHarness.Motion.StartStop, 0.5, true, 1.0 / 120.0)]
        public void AClientStall_WithUnitysLateDeltaTime_CostsAtMostOneCorrection(
            PredictionHarness.Motion motion, double stallSeconds, bool pacedBacklog, double frameSeconds) =>
            RunClientOrderStall(motion, stallSeconds, pacedBacklog, frameSeconds, deltaLags: true);

        private static void RunClientOrderStall(
            PredictionHarness.Motion motion, double stallSeconds, bool pacedBacklog, double frameSeconds, bool deltaLags)
        {
            PredictionHarness.Config c = ServerStallRecoveryTests.Scenario(motion);
            c.DeltaTimeLagsOneFrame = deltaLags;
            c.FrameSeconds = frameSeconds;
            c.HitchAtSeconds = EventAt;
            c.HitchSeconds = stallSeconds;
            c.InputBeforeSnapshot = true;
            c.MaxFrameDeltaSeconds = 1.0 / 3.0;
            if (pacedBacklog) c.BacklogSpacingSeconds = 0.010;
            PredictionHarness.Result r = PredictionHarness.Run(c);
            TestContext.WriteLine($"{stallSeconds} s stall, input first, deltaLags={deltaLags}, paced={pacedBacklog}, {frameSeconds * 1000.0:F1} ms frames: {r}");
            Assume.That(r.Reconciles, Is.GreaterThan(250), "precondition: the run reconciled");

            double drained = EventAt + stallSeconds + (pacedBacklog ? stallSeconds * 15.0 * 0.010 : 0.0) + 0.15;
            foreach (var (at, correction, tickError) in r.CorrectionLog)
            {
                if (at < EventAt || at > drained + 2.0) continue;
                if (correction >= r.StepLength * 0.999f || at < drained)
                {
                    TestContext.WriteLine($"  t={at:F3} correction={correction / r.StepLength:F2} steps tickError={tickError}");
                }
            }

            // Over the stall's reach, for the reason the paced-backlog test above gives.
            int large = CorrectionsBetween(r, EventAt, drained + 2.0, 2f);
            Assert.That(large, Is.LessThanOrEqualTo(1),
                $"one {stallSeconds} s stall, input sampled first: {large} corrections of two steps or more. {r}");
            Assert.That(CorrectionsBetween(r, drained + 1.0, drained + 2.0, 2f), Is.Zero,
                "a second after the stall nothing of it is left to correct. " + r);
            Assert.That(r.StallResyncs, Is.EqualTo(1), "one stall, one clock jump. " + r);
        }

        [TestCase(PredictionHarness.Motion.Circle)]
        [TestCase(PredictionHarness.Motion.StartStop)]
        public void OrdinaryJitter_NeverJumpsTheClock(PredictionHarness.Motion motion)
        {
            // Uneven frames (120 fps +/- 7 ms), 20 ms network jitter each way and 10 ms send
            // jitter: no frame is long enough to clamp, and nothing may be read as a stall.
            PredictionHarness.Config c = ServerStallRecoveryTests.Scenario(motion);
            c.FrameJitterSeconds = 0.007;
            c.NetworkJitterSeconds = 0.020;
            c.SendJitterSeconds = 0.010;
            PredictionHarness.Result r = PredictionHarness.Run(c);
            TestContext.WriteLine(r.ToString());
            Assume.That(r.Reconciles, Is.GreaterThan(250), "precondition: the run reconciled");

            Assert.That(r.ClampedFrames, Is.Zero, r.ToString());
            Assert.That(r.StallResyncs, Is.Zero, "jitter is steered, never jumped. " + r);
            Assert.That(r.HardResyncs, Is.Zero, r.ToString());
        }
    }
}
