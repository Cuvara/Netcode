using NUnit.Framework;
using Mode = Cuvara.Netcode.Tests.Editor.PredictionHarness.ClientMode;
using Motion = Cuvara.Netcode.Tests.Editor.PredictionHarness.Motion;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Characterises what the prediction does when the server's tick rate STEPS (55 -> 65 ticks per
    /// client second for 6 s, then back), the shape seen in the S09 soak runs 9mre and 3ng5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pins current behaviour; it is not a requirement that snaps happen. A +18% step needs 10 ticks/s
    /// of clock slew; the integral contribution is capped at 0.5 tick per snapshot (7.5 ticks/s at
    /// 15 snapshots/s), while the proportional contribution has its own 0.5 tick-per-call cap.
    /// Those are separate contributions, so 7.5 ticks/s is not the total available correction. The
    /// fitted rate cannot follow a 6 s step. The client clock therefore falls behind by
    /// ~15 ticks (the catch-up budget) and is jumped onto the server's line (<c>StallResyncs</c>),
    /// which shows as one honest snap of 15 ticks x 5/60 = 1.25 units. The server is authoritative
    /// and the error is real, so the snap is the intended outcome. Chrony's default maxslewrate
    /// (83333 ppm, about 8.3%) limits adjustment of wall-clock time; it does not bound or validate
    /// this monotonic scheduler. Linux CLOCK_MONOTONIC ignores discontinuous wall-clock steps but
    /// is frequency-adjusted. The observed VM/host clock-correction explanation (including WSL2)
    /// remains a hypothesis. If a future change lets steering absorb this step, update this test
    /// on purpose, with the anti-desync trade-off stated.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class ServerTickRateStepTests
    {
        private static PredictionHarness.Config Soak(double baseHz, double stepHz, Motion motion)
        {
            var c = PredictionHarness.Config.Default;
            c.ServerHz = baseHz;
            c.SendHz = 15.0;
            c.Mode = Mode.MeasuredSteering;
            c.Motion = motion;
            c.Speed = 5f;
            c.DurationSeconds = 60.0;
            c.WarmupSeconds = 15.0;
            c.UplinkSeconds = 0.010;
            c.DownlinkSeconds = 0.010;
            c.RateStepAtSeconds = 30.0;
            c.RateStepSeconds = stepHz > 0.0 ? 6.0 : 0.0;
            c.RateStepHz = stepHz;
            return c;
        }

        private static PredictionHarness.Result Run(PredictionHarness.Config c)
        {
            PredictionHarness.Result r = PredictionHarness.Run(c);
            TestContext.WriteLine($"{c.ServerHz}->{c.RateStepHz} {c.Motion}: {r}");
            Assume.That(r.Reconciles, Is.GreaterThan(100), "precondition: the run reconciled");
            return r;
        }

        [Test]
        public void Control_FlatRate_HasNoSnap()
        {
            PredictionHarness.Result r = Run(Soak(55.0, 0.0, Motion.Circle));

            Assert.That(r.Snaps, Is.Zero, r.ToString());
            Assert.That(r.HardResyncs, Is.Zero, r.ToString());
            Assert.That(r.MaxCorrection, Is.LessThan(0.5), r.ToString());
        }

        [TestCase(Motion.Circle)]
        [TestCase(Motion.StartStop)]
        public void Plus18PercentStep_ForcesOneClockJumpAndHardSnap(Motion motion)
        {
            PredictionHarness.Result r = Run(Soak(55.0, 65.0, motion));

            Assert.That(r.HardResyncs, Is.EqualTo(1), "one clock jump: " + r);
            Assert.That(r.Snaps, Is.InRange(1, 3), r.ToString());
            Assert.That(r.MaxCorrection, Is.InRange(0.8, 1.3),
                "about 10 to 15 ticks of lag at speed 5 (0.083 units per tick). " + r);
            Assert.That(System.Math.Abs(r.FinalTickError), Is.LessThanOrEqualTo(2),
                "and the clock is back on the server's line afterwards. " + r);
        }

        [TestCase(60.0, 65.0)]   // +8.3% rate step
        [TestCase(55.0, 58.0)]
        public void StepInsideTheSteeringBudget_HasNoSnap(double baseHz, double stepHz)
        {
            PredictionHarness.Result r = Run(Soak(baseHz, stepHz, Motion.Circle));

            Assert.That(r.Snaps, Is.Zero, r.ToString());
            Assert.That(r.HardResyncs, Is.Zero, r.ToString());
        }
    }
}
