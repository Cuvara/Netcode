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
