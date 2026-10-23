using NUnit.Framework;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// A server stall seen from the client: no snapshot for a second, then the held-back ones in
    /// one burst. The local player must not be corrected for it, once or repeatedly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client keeps predicting through the silence (its inputs still reach the server, which
    /// keeps applying them), so when the burst lands every snapshot in it describes ticks the
    /// history still holds, and the newest of them agrees with what the client predicted for
    /// that tick. Nothing in the burst is news about the player's own movement.
    /// </para>
    /// <para>
    /// Delivery is in production order (KCP is ordered), so the burst is head-of-line blocked
    /// exactly as a real stall would be, and the client applies only the newest snapshot that
    /// has arrived each frame, as a binder does.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class ServerStallRecoveryTests
    {
        private const double EventAt = 12.0;

        internal static PredictionHarness.Config Scenario(PredictionHarness.Motion motion)
        {
            var c = PredictionHarness.Config.Default;
            c.SendHz = 13.0;
            c.SendJitterSeconds = 0.002;
            c.UplinkSeconds = 0.030;
            c.DownlinkSeconds = 0.030;
            c.NetworkJitterSeconds = 0.003;
            c.FrameSeconds = 1.0 / 120.0;
            c.DurationSeconds = 24.0;
            c.Mode = PredictionHarness.ClientMode.MeasuredSteering;
            c.Motion = motion;
            c.InOrderDownlink = true;
            return c;
        }

        /// <summary>Counted corrections of at least one base step at or after <paramref name="from"/>.</summary>
        internal static int StepCorrectionsFrom(PredictionHarness.Result r, double from)
        {
            int n = 0;
            foreach (var (at, correction, _) in r.CorrectionLog)
            {
                if (at >= from && correction >= r.StepLength * 0.999f) n++;
            }

            return n;
        }

        private static PredictionHarness.Result Run(PredictionHarness.Config c, string label)
        {
            PredictionHarness.Result r = PredictionHarness.Run(c);
            TestContext.WriteLine($"{label}: {r}");
            Assume.That(r.Reconciles, Is.GreaterThan(250), "precondition: the run reconciled");
            return r;
        }

        [TestCase(PredictionHarness.Motion.Circle)]
        [TestCase(PredictionHarness.Motion.StartStop)]
        public void Control_NoStall_CorrectsByLessThanAStep(PredictionHarness.Motion motion)
        {
            PredictionHarness.Result r = Run(Scenario(motion), "no stall");

            Assert.That(StepCorrectionsFrom(r, EventAt), Is.Zero, r.ToString());
            Assert.That(r.HardResyncs, Is.Zero, r.ToString());
        }

        [TestCase(PredictionHarness.Motion.Circle)]
        [TestCase(PredictionHarness.Motion.StartStop)]
        public void NoSnapshotsForASecondThenABurst_CorrectsNothing(PredictionHarness.Motion motion)
        {
            PredictionHarness.Config c = Scenario(motion);
            c.BurstAtSeconds = EventAt;
            c.BurstSnapshots = 15; // one second of snapshots at 15 Hz
            c.BurstDelaySeconds = 1.0;
            PredictionHarness.Result r = Run(c, "1 s server stall, then a burst");

            Assert.That(StepCorrectionsFrom(r, EventAt), Is.Zero,
                "a second without snapshots followed by all of them at once is no news about the " +
                $"player's own movement; any whole-step correction after it is the stall misread. {r}");
            Assert.That(r.Snaps, Is.Zero, r.ToString());
            Assert.That(r.HardResyncs, Is.Zero, r.ToString());
            Assert.That(r.FinalTickError, Is.InRange(-2L, 2L), "the clock is back on the server's tick line. " + r);
            Assert.That(r.MaxFrameMotionRatio, Is.LessThanOrEqualTo(2.0),
                "the burst must not move the rendered player much further in one frame than in any other. " + r);
        }

        [TestCase(PredictionHarness.Motion.Circle)]
        [TestCase(PredictionHarness.Motion.StartStop)]
        public void AServerTickStallAndItsCatchUp_IsCorrectedOnce_AndNeverAgain(PredictionHarness.Motion motion)
        {
            // The server's tick thread stops for a second and then runs the missed ticks back to
            // back. Every input queued meanwhile drains on the first of them and only the newest
            // moves; the hold then runs out partway through the catch-up, so the server really
            // does end up short of where the client predicted (measured: 39 base steps walking
            // east, 45 on the circle). That is news, and correcting it once is right. Correcting it
            // again on the next snapshot is the history measuring the same error twice.
            PredictionHarness.Config c = Scenario(motion);
            c.ServerStallAtSeconds = EventAt;
            c.ServerStallSeconds = 1.0;
            PredictionHarness.Result r = Run(c, "1 s server tick stall, then its catch-up");
            Assume.That(r.MaxCorrection, Is.GreaterThan(r.StepLength * 10f),
                "precondition: the catch-up moved the server off the client's prediction. " + r);

            Assert.That(StepCorrectionsFrom(r, EventAt), Is.EqualTo(1),
                "one server event, one correction. " + r);
            Assert.That(r.HardResyncs, Is.Zero, r.ToString());
            Assert.That(r.FinalTickError, Is.InRange(-2L, 2L), "the clock is back on the server's tick line. " + r);
        }
    }
}
