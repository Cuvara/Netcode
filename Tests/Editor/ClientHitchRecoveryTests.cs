using NUnit.Framework;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// A client stall: one frame of 0.5 s or 2 s (a GC pause, a scene load, a window drag) while
    /// the server keeps ticking. The stall may cost the local player one correction; it must not
    /// cost a stream of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Fails today (declared defect, found by this fixture).</b> <c>Advance</c> discards
    /// frame time beyond the catch-up budget (<c>MaxCatchUpTicks</c>, 15 ticks at 60 Hz), so a
    /// stall leaves the client's base tick behind the server's by the stall minus 0.25 s.
    /// <c>SteerToServerTick</c> sets the clock only beyond two seconds of error
    /// (<c>HardResyncTicks</c>) and otherwise steers it by at most 0.5 tick per snapshot
    /// (<c>MaxSteerTicksPerCall</c>), about 7.5 ticks a second at 15 Hz. Until the lag is
    /// closed the client's ticks are not the server's: the history misses (snapshot ticks lie in
    /// the client's future), the ack-offset median trails the moving clock, and the reconcile
    /// corrects by one or two base steps on most snapshots. Measured here (circle / start-stop):
    /// a 0.5 s stall corrects by a whole step 24 / 16 times, the first two of them 13 and 12
    /// steps; a 2 s stall 80 / 58 times, with the clock still 30 ticks behind 12 s later. The
    /// same run without the stall corrects by a whole step zero times
    /// (<see cref="ServerStallRecoveryTests"/>, control), and a 5 s stall is hard-resynchronised. All of it predates 0.48.0 (v0.20.0, 137feff).
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class ClientHitchRecoveryTests
    {
        private const double EventAt = 12.0;

        [TestCase(PredictionHarness.Motion.Circle, 0.5)]
        [TestCase(PredictionHarness.Motion.Circle, 2.0)]
        [TestCase(PredictionHarness.Motion.StartStop, 0.5)]
        [TestCase(PredictionHarness.Motion.StartStop, 2.0)]
        public void AClientStall_CostsAtMostOneWholeStepCorrection(PredictionHarness.Motion motion, double stallSeconds)
        {
            PredictionHarness.Config c = ServerStallRecoveryTests.Scenario(motion);
            c.HitchAtSeconds = EventAt;
            c.HitchSeconds = stallSeconds;
            PredictionHarness.Result r = PredictionHarness.Run(c);
            TestContext.WriteLine($"{stallSeconds} s client stall: {r}");
            Assume.That(r.Reconciles, Is.GreaterThan(250), "precondition: the run reconciled");

            int afterStall = ServerStallRecoveryTests.StepCorrectionsFrom(r, EventAt);
            Assert.That(afterStall, Is.LessThanOrEqualTo(1),
                $"after one {stallSeconds} s stall the reconcile corrected by a whole step {afterStall} times. {r}");
            Assert.That(ServerStallRecoveryTests.StepCorrectionsFrom(r, EventAt + 3.0), Is.Zero,
                "three seconds after the stall nothing should be left to correct. " + r);
            Assert.That(r.FinalTickError, Is.InRange(-2L, 2L),
                "twelve seconds after the stall the clock is back on the server's tick line. " + r);
        }
    }
}
