using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Shared.GameLogic.Components;
using Mode = Cuvara.Netcode.Tests.Editor.PredictionHarness.ClientMode;
using Motion = Cuvara.Netcode.Tests.Editor.PredictionHarness.Motion;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Reconciling against the history entry the snapshot actually describes
    /// (<c>ack_applied_tick</c>), measured end to end on <see cref="PredictionHarness"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> A snapshot at server tick T was compared with the client's history at
    /// client tick T. The server applies an input on the tick that DRAINS it, not on the tick the
    /// client recorded it on, so the two tick lines are offset by roughly the client's lead minus
    /// one plus clock skew, and every tick of that offset came back as a correction: a fraction
    /// of a step per reconcile on a curve, a whole step at a start or a stop. Three smaller
    /// defects sat beside it and are pinned here too: the history entry of an input tick was
    /// recorded before the input stepped it, a new tick dropped the unshown part of the step on
    /// screen, and the clock steering had no integral term.
    /// </para>
    /// <para>
    /// <b>Why whole-run statistics rather than single cases.</b> Each of these is invisible in one
    /// reconcile and obvious in a distribution: the offset is a constant that shows up only as
    /// "every correction is a little wrong", and the render jump only on the frames where a tick
    /// boundary falls. The harness is deterministic (seeded), so the thresholds are exact
    /// properties of the code, not flaky ones.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class AckAppliedTickReconcileTests
    {
        private static PredictionHarness.Config Aligned(Motion motion)
        {
            var c = PredictionHarness.Config.Default;
            c.ServerHz = 60.0;
            c.SendHz = 15.0;
            c.Motion = motion;
            return c;
        }

        /// <summary>The live configuration that exposed the defect: 59.6 Hz server, 13 Hz sends with jitter.</summary>
        private static PredictionHarness.Config Live(Motion motion, Mode mode)
        {
            var c = PredictionHarness.Config.Default;
            c.ServerHz = 59.6;
            c.SendHz = 13.0;
            c.SendJitterSeconds = 0.008;
            c.NetworkJitterSeconds = 0.004;
            c.UplinkSeconds = 0.010;
            c.DownlinkSeconds = 0.010;
            c.LeadTicks = 6;
            c.Motion = motion;
            c.Mode = mode;
            c.DurationSeconds = 55.0;
            return c;
        }

        private static PredictionHarness.Result Run(PredictionHarness.Config c)
        {
            PredictionHarness.Result r = PredictionHarness.Run(c);
            TestContext.WriteLine($"{c.Mode} {c.Motion} {c.ServerHz}Hz send {c.SendHz}Hz: {r}");
            Assume.That(r.Reconciles, Is.GreaterThan(100), "precondition: the run reconciled");
            return r;
        }

        [TestCase(Motion.Circle)]
        [TestCase(Motion.StartStop)]
        public void AlignedClocksAndNoJitter_CorrectNothing(Motion motion)
        {
            PredictionHarness.Result r = Run(Aligned(motion));

            Assert.That(r.NonzeroCorrections, Is.Zero,
                "with the server at exactly 60 Hz, no jitter and the history compared at the entry the " +
                "snapshot describes, client and server took the same steps on the same ticks: any " +
                $"correction at all is a comparison error, not a disagreement. {r}");
        }

        [Test]
        public void LiveCircle_CorrectsLessThanTwoThousandthsOfAUnitOnAverage()
        {
            PredictionHarness.Result r = Run(Live(Motion.Circle, Mode.AckApplied));

            Assert.That(r.MeanCorrection, Is.LessThan(0.002), r.ToString());
            Assert.That(r.CorrectionsAtLeastOneStep, Is.Zero,
                "a whole step of correction on a steady curve is the offset defect itself. " + r);
        }

        [Test]
        public void LiveStartStop_NeverCorrectsMoreThanOneStep()
        {
            PredictionHarness.Result r = Run(Live(Motion.StartStop, Mode.AckApplied));

            Assert.That(r.MaxCorrection, Is.LessThanOrEqualTo(r.StepLength * 1.0001f),
                "starts and stops are where a mis-aligned comparison costs a whole step per tick of " +
                "offset; with ack_applied_tick the comparison is aligned and at most the one tick of " +
                $"drain quantisation remains. {r}");
        }

        [Test]
        public void TheFieldIsWhatRemovesTheError_WithoutItTheSameRunCorrectsMore()
        {
            // The control arm. Without it the assertions above are also satisfied by a harness
            // that never moves the player.
            PredictionHarness.Result with = Run(Live(Motion.Circle, Mode.AckApplied));
            PredictionHarness.Result without = Run(Live(Motion.Circle, Mode.SnapshotTickOnly));

            Assert.That(without.MeanCorrection, Is.GreaterThan(with.MeanCorrection * 2.0),
                $"with: {with} / without: {without}");
        }

        [TestCase(Motion.Circle)]
        [TestCase(Motion.StartStop)]
        public void RenderedMotion_NeverJumpsMoreThanHalfAgainANormalFrame(Motion motion)
        {
            PredictionHarness.Config c = Live(motion, Mode.AckApplied);
            c.FrameSeconds = 0.001;
            c.DurationSeconds = 20.0;
            PredictionHarness.Result r = Run(c);

            Assert.That(r.MovingFrames, Is.GreaterThan(1000), "precondition: frames were measured");
            Assert.That(r.MaxFrameMotionRatio, Is.LessThanOrEqualTo(1.5),
                "a frame that moves the avatar much further than the frame before it is a visible " +
                "hitch, whatever the simulated position does. A new tick replacing the step on " +
                $"screen used to drop its unshown remainder in one frame. {r}");
        }

        [Test]
        public void MeasuredSteering_KeepsTheClockOnTheServersAndTheCorrectionsSmall()
        {
            // The path WorldViewBinder and the DOTS LocalPredictionSystem now share: the lead is
            // MEASURED (staleness, acknowledgement floor) rather than fixed, so it moves while
            // the estimators warm up, and every move of the clock on a moving body is a real
            // one-step disagreement with the server. Bounded, not zero.
            PredictionHarness.Result circle = Run(Live(Motion.Circle, Mode.MeasuredSteering));
            PredictionHarness.Result startStop = Run(Live(Motion.StartStop, Mode.MeasuredSteering));

            foreach (PredictionHarness.Result r in new[] { circle, startStop })
            {
                Assert.That(r.HardResyncs, Is.Zero, r.ToString());
                Assert.That(r.MaxAckTickOffset - r.MinAckTickOffset, Is.LessThanOrEqualTo(4),
                    $"a steered clock keeps the client/server tick offset within the jitter. {r}");
                Assert.That(System.Math.Abs(r.FinalClientLeadTicks), Is.LessThanOrEqualTo(8),
                    $"the clock stays near the server's instead of running away. {r}");
                Assert.That(r.MaxCorrection, Is.LessThanOrEqualTo(r.StepLength * 2.01f), r.ToString());
                Assert.That(r.MeanCorrection, Is.LessThan(0.003), r.ToString());
            }
        }

        [Test]
        public void TheOldDotsPath_IsTheControl_ItsClockRunsAway()
        {
            // What LocalPredictionSystem did before: two-argument Reconcile on a new ack, no
            // steering. Pinned so the improvement above is measured against the real baseline.
            // Measured: the client clock 22 ticks ahead of the server's after 55 s, a mean
            // correction of 0.030 and up to three steps at a start or stop.
            PredictionHarness.Result old = Run(Live(Motion.StartStop, Mode.TwoArgumentNoSteering));
            PredictionHarness.Result now = Run(Live(Motion.StartStop, Mode.MeasuredSteering));

            Assert.That(old.FinalClientLeadTicks, Is.GreaterThan(15),
                $"precondition: without steering the clock drifts against a 59.6 Hz server. {old}");
            Assert.That(now.MaxCorrection, Is.LessThan(old.MaxCorrection), $"old: {old} / new: {now}");
            Assert.That(now.MeanCorrection, Is.LessThan(old.MeanCorrection / 5.0), $"old: {old} / new: {now}");
        }

        // ── Unit-level pins for the three smaller defects ──

        private static LocalMovePredictor NewPredictor() =>
            new LocalMovePredictor(new PredictionSettings(60, 5f, MapBounds.Default));

        [Test]
        public void TheOffsetIsTheAckedInputsBaseTickMinusTheTickThatAppliedIt()
        {
            var p = NewPredictor();
            p.SeedBaseTick(1000);
            p.Reconcile(new Vec2(0f, 0f), 0, 1000, 0);

            // Client base tick 1000: record input 1. The server applies it on its tick 995 (the
            // client leads by 5); the snapshot at 995 acks it.
            p.RecordInput(1, 1f, 0f);
            for (int i = 0; i < 8; i++) p.Advance(1f / 60f);

            p.Reconcile(new Vec2(5f / 60f, 0f), ackTick: 1, serverBaseTick: 995, ackAppliedTick: 995);

            Assert.That(p.AckTickOffset, Is.EqualTo(5));
            Assert.That(p.AckOffsetSamples, Is.EqualTo(1));
            Assert.That(p.LastCorrection, Is.EqualTo(0f),
                "the snapshot at server 995 is the client's history at 1000, where it took exactly " +
                "the one step the server took");
        }

        [Test]
        public void WithoutTheField_TheComparisonIsAtTheSnapshotTick_AsBefore()
        {
            var p = NewPredictor();
            p.SeedBaseTick(1000);
            p.Reconcile(new Vec2(0f, 0f), 0, 1000);
            p.RecordInput(1, 1f, 0f);
            for (int i = 0; i < 8; i++) p.Advance(1f / 60f);

            p.Reconcile(new Vec2(5f / 60f, 0f), 1, 995, 0);

            Assert.That(p.AckOffsetSamples, Is.Zero);
            Assert.That(p.AckTickOffset, Is.Zero);
        }

        [Test]
        public void AnInputTicksHistoryEntryIncludesTheInputsStep()
        {
            var p = NewPredictor();
            p.SeedBaseTick(1000);
            p.Reconcile(new Vec2(0f, 0f), 0, 1000);

            // Start moving on tick 1000 (no Advance in between: the entry for 1000 was written
            // before the input). The server, applying it on the same tick, is one step east.
            p.RecordInput(1, 1f, 0f);
            p.Reconcile(new Vec2(5f / 60f, 0f), 1, 1000, 1000);

            Assert.That(p.HistoryHits, Is.EqualTo(1), "precondition: compared against the history");
            Assert.That(p.LastCorrection, Is.EqualTo(0f),
                "the history entry of the input's own tick must be taken AFTER the input stepped it, " +
                "or every start reads as a one-step error");
        }

        [Test]
        public void TheIntegralTermClosesASlowServersDroop()
        {
            // 59.6 Hz server against a 60 Hz client clock, steered every fourth server tick.
            var p = NewPredictor();
            long serverTick = 1000;
            p.SeedBaseTick(serverTick);
            double server = 0, client = 0;
            double sumError = 0;
            int counted = 0;
            while (server < 60.0)
            {
                server += 4.0 / 59.6;
                serverTick += 4;
                while (client < server)
                {
                    client += 1.0 / 600.0;
                    p.Advance(1f / 600f);
                }

                p.SteerToServerTick(serverTick, 0);
                if (server > 30.0)
                {
                    sumError += p.TickError;
                    counted++;
                }
            }

            double mean = sumError / counted;
            Assert.That(System.Math.Abs(mean), Is.LessThan(0.5),
                $"a proportional-only loop droops about a tick against a 0.7% rate difference; with " +
                $"the integral term the mean steady-state tick error is {mean:F3}");
            Assert.That(p.SteerIntegralTicks, Is.GreaterThan(0f),
                "the integrator learned that the client clock runs fast");
        }
    }
}
