using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Cuvara.Netcode.View;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Pins the steering target itself: how far ahead of the newest snapshot's tick the
    /// prediction clock is told to run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this fixture exists.</b> The lead was measured by
    /// <see cref="SnapshotStalenessEstimator"/> and, until that estimator had fitted a rate,
    /// derived as one snapshot interval. The estimator's first fit lands <b>8.2 seconds</b>
    /// after join against a 15 Hz snapshot stream — epoch one only sets an anchor, and two
    /// consecutive two-second epochs cannot span the four seconds a fit needs — so the
    /// derived figure was in force for the first eight seconds of every session.
    /// </para>
    /// <para>
    /// On localhost the derived figure is <b>4 base ticks</b> and the real age is <b>0.06</b>.
    /// A four-tick error here is not a four-tick error in a diagnostic: the clock is steered
    /// onto it, so a tick number stops naming the same moment on the two sides, and
    /// <c>LocalMovePredictor.Reconcile</c>'s history path — which indexes the client's own
    /// history by the SERVER's tick number — reports the whole of it as a positional
    /// correction. Reproduced against the live stack in a wall-clock model of the loop:
    /// <b>161 reconciles, 34-47 corrections, max 0.4167 units (5.00 steps)</b> before, and
    /// <b>9-19 corrections, max 0.0833 units (1.00 step)</b> after. The live run this was
    /// diagnosed from read 162 / 36 / 0.3333 (4.00 steps).
    /// </para>
    /// <para>
    /// <b>What is NOT claimed.</b> The residual one-step corrections are irreducible: two
    /// free-running 60 Hz clocks disagree about which tick a motion transition lands on by
    /// plus or minus one, and one step is what that costs. A measurement that expects a
    /// correction to be rare across many start/stop transitions is measuring that
    /// quantisation, not the client.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class WorldViewBinderLeadTests
    {
        private const float BaseHz = 60f;
        private const int SnapshotEvery = 4;                       // 15 Hz on a 60 Hz base
        private const double Interval = SnapshotEvery / (double)BaseHz;
        private const double ClockOffset = 12345.678;

        private static WorldViewBinder NewBinder() => new WorldViewBinder(new NullView(), null);

        /// <summary>Teach the binder the snapshot cadence, so SnapshotTickGap is 4 and not 1.</summary>
        private static void FeedCadence(WorldViewBinder binder, ref long tick, ref double now, int snapshots)
        {
            for (var i = 0; i < snapshots; i++)
            {
                binder.TickRate.Sample(tick, now);
                binder.Staleness.Sample(tick, now, BaseHz);
                tick += SnapshotEvery;
                now += Interval;
            }
        }

        [Test]
        public void TheWarmUpLeadIsTheMeasuredAgeAndNotAWholeSnapshotInterval()
        {
            var binder = NewBinder();
            long tick = 1000;
            double now = ClockOffset + tick / (double)BaseHz + 0.010;

            // A steady localhost route: every snapshot acted on the same tiny span after it
            // was produced, so its real age above the floor is ~0.
            FeedCadence(binder, ref tick, ref now, 8);

            Assert.That(binder.TickRate.SnapshotTickGap, Is.EqualTo(SnapshotEvery),
                "precondition: the cadence must be learned, or the derived fallback is 1 and " +
                "this test cannot tell the two apart");
            Assert.That(binder.Staleness.IsUsable, Is.False,
                "precondition: this is the warm-up window, before any rate has been fitted");
            Assert.That(binder.Staleness.HasEstimate, Is.True,
                "precondition: but a provisional reading must be available");

            Assert.That(binder.TargetLeadTicks(), Is.EqualTo(0),
                "on a steady route the newest snapshot is ~0 ticks old when it is used, so the " +
                "clock must not be steered a whole snapshot interval past the server. Four " +
                "ticks here is 0.3333 world units of correction at every start and every stop.");
        }

        [Test]
        public void AProvisionalReadingIsNeverTrustedPastTheDerivedFigure()
        {
            var binder = NewBinder();
            long tick = 1000;
            double t0 = ClockOffset + tick / (double)BaseHz;

            // The 1.103 client/server clock ratio from MinimumSkew's remarks. With no fit
            // the residual drifts upward without bound; the clamp is what stops the steering
            // target from following it.
            for (var i = 0; i < 40; i++)
            {
                double now = t0 + 1.103 * (i * Interval);
                binder.TickRate.Sample(tick, now);
                binder.Staleness.Sample(tick, now, BaseHz);
                tick += SnapshotEvery;

                Assert.That(binder.TargetLeadTicks(),
                    Is.LessThanOrEqualTo(binder.TickRate.SnapshotTickGap > 0
                        ? binder.TickRate.SnapshotTickGap
                        : 1),
                    "an unfitted rate drifts the provisional reading upward, so it must be " +
                    "clamped by the figure it replaces. Without the clamp this change would " +
                    "be WORSE than the fallback it removes on exactly the machines the " +
                    "estimator's skew bounds were widened for.");
            }
        }

        [Test]
        public void AFittedLineIsBelievedRatherThanClamped()
        {
            var binder = NewBinder();
            long tick = 1000;
            double now = ClockOffset + tick / (double)BaseHz + 0.010;

            // Long enough for a fit, with one snapshot held for six ticks at the end: a real,
            // measured age larger than the derived figure must survive.
            double until = now + SnapshotStalenessEstimator.MinimumBaselineSeconds
                               + SnapshotStalenessEstimator.EpochSeconds * 2;
            while (now < until)
            {
                binder.TickRate.Sample(tick, now);
                binder.Staleness.Sample(tick, now, BaseHz);
                tick += SnapshotEvery;
                now += Interval;
            }

            Assert.That(binder.Staleness.IsUsable, Is.True, "precondition: a line must be fitted");

            binder.TickRate.Sample(tick, now + 6.0 / BaseHz);
            binder.Staleness.Sample(tick, now + 6.0 / BaseHz, BaseHz);

            Assert.That(binder.TargetLeadTicks(), Is.GreaterThan(binder.TickRate.SnapshotTickGap),
                "a FITTED reading is evidence in both directions -- clamping it to the derived " +
                "figure would throw away the measurement on exactly the slow route it is for. " +
                "Only the provisional reading is one-sided.");
        }

        private sealed class NullView : IEntityView
        {
            public void Spawn(string id, bool isLocal, string type) { }
            public void Despawn(string id) { }
            public void SetState(string id, float x, float y, int hp, int maxHp) { }
        }
    }
}
