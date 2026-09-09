using System;
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

            // A synthetic 1.103 client/server clock ratio — see MinimumSkew's remarks, which
            // record that this figure was once believed measured and was an artefact. With no fit
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
            // Long enough to CORROBORATE, not merely to fit. The first fit lands at the
            // minimum baseline and only sets the reference; the slope is believed once it has
            // reproduced over a doubled baseline, which needs roughly four times the minimum.
            // Warming only to the first fit leaves the age on the provisional path, where it is
            // clamped to the snapshot gap — correct behaviour, and not what these two pin.
            double until = now + SnapshotStalenessEstimator.MinimumBaselineSeconds * 5;
            while (now < until)
            {
                binder.TickRate.Sample(tick, now);
                binder.Staleness.Sample(tick, now, BaseHz);
                tick += SnapshotEvery;
                now += Interval;
            }

            Assert.That(binder.Staleness.IsUsable, Is.True, "precondition: a line must be fitted");
            Assert.That(binder.Staleness.AgeIsFitted, Is.True,
                "precondition: and its slope must have reproduced, or the age is measured "
                + "against the unit-rate floor instead and this test is pinning the wrong branch");

            binder.TickRate.Sample(tick, now + 6.0 / BaseHz);
            binder.Staleness.Sample(tick, now + 6.0 / BaseHz, BaseHz);

            Assert.That(binder.TargetLeadTicks(), Is.GreaterThan(binder.TickRate.SnapshotTickGap),
                "a FITTED reading is evidence in both directions -- clamping it to the derived " +
                "figure would throw away the measurement on exactly the slow route it is for. " +
                "Only the provisional reading is one-sided.");
        }

        /// <summary>
        /// Feed the binder's acknowledgement estimator a sweeping link with a known constant.
        /// </summary>
        private static void FeedAckFloor(WorldViewBinder binder, double constantSeconds, double seconds)
        {
            // The binder stamps sends from its own clock, so this drives the estimator directly
            // with a synthetic one — the arithmetic under test is the LEAD, not the clock.
            var arrivals = new System.Collections.Generic.Queue<(long Tick, double At)>();
            double now = 0, nextSend = 0, nextSnap = SnapshotPeriod * 0.37;
            double snapPeriod = SnapshotPeriod * 1.03;      // two independent clocks
            long inputTick = 0, accepted = 0;

            while (now < seconds)
            {
                now = Math.Min(nextSend, nextSnap);

                if (now >= nextSend)
                {
                    nextSend = now + SendPeriod;
                    inputTick++;
                    binder.AckLatency.RecordSent(inputTick, now);
                    arrivals.Enqueue((inputTick, now + constantSeconds));
                }

                if (now < nextSnap) continue;

                nextSnap = now + snapPeriod;
                while (arrivals.Count > 0 && arrivals.Peek().At <= now) accepted = arrivals.Dequeue().Tick;
                if (accepted > 0) binder.AckLatency.RecordAck(accepted, now, BaseHz);
            }
        }

        private const double SendPeriod = 1.0 / 15.0;
        private const double SnapshotPeriod = SnapshotEvery / (double)BaseHz;

        [Test]
        public void TheLeadIsTheSnapshotAgePlusTheMeasuredPipelineConstant()
        {
            var binder = NewBinder();
            long tick = 1000;
            double now = ClockOffset + tick / (double)BaseHz + 0.010;
            FeedCadence(binder, ref tick, ref now, 8);

            Assert.That(binder.TargetLeadTicks(), Is.EqualTo(0),
                "precondition: with no acknowledgement floor the lead is the age alone");

            FeedAckFloor(binder, constantSeconds: 2.0 / BaseHz, seconds: 40.0);

            Assert.That(binder.AckLatency.HasEstimate, Is.True, "precondition: a floor was measured");
            Assert.That(binder.TargetLeadTicks(), Is.EqualTo(2).Within(1),
                "the client applies an input at its OWN tick and the server applies it at the " +
                "tick its packet is drained on, so the lead must cover uplink + snapshot age. " +
                "The staleness fit reports only the age ABOVE its envelope floor and this " +
                "reports the constant that envelope absorbed, so the two add without overlap.");
        }

        [Test]
        public void WithNeitherReadingTheLeadIsExactlyWhatItWasBefore()
        {
            var binder = NewBinder();
            long tick = 1000;
            double now = ClockOffset + tick / (double)BaseHz + 0.010;

            // Enough to give TickRateEstimator a window, since the round-trip term is
            // expressed in ticks and needs a measured rate to convert into.
            FeedCadence(binder, ref tick, ref now, 60);

            Assert.That(binder.TickRate.HasEstimate, Is.True, "precondition: a rate is measured");
            Assert.That(binder.AckLatency.HasEstimate, Is.False, "precondition: no floor offered");

            // 100 ms round trip at 60 Hz is 6 ticks; the pre-existing arithmetic adds half.
            binder.RoundTripMs = 100;

            Assert.That(binder.TargetLeadTicks(), Is.EqualTo(3),
                "a consumer that supplies a round trip and never calls NoteInputSent must get " +
                "exactly the behaviour it had before this estimator existed — the fallback is " +
                "not allowed to change under it.");
        }

        [Test]
        public void AMeasuredFloorIsPreferredToTheRoundTripRatherThanAddedToIt()
        {
            var binder = NewBinder();
            long tick = 1000;
            double now = ClockOffset + tick / (double)BaseHz + 0.010;
            FeedCadence(binder, ref tick, ref now, 8);

            binder.RoundTripMs = 100;                       // would contribute 3 ticks
            FeedAckFloor(binder, constantSeconds: 2.0 / BaseHz, seconds: 40.0);

            Assert.That(binder.AckLatency.HasEstimate, Is.True, "precondition");
            Assert.That(binder.TargetLeadTicks(), Is.EqualTo(2).Within(1),
                "the two measure the same thing by different means, so adding them counts the " +
                "pipeline twice. The floor wins because it times the real path end to end — " +
                "the input drain, the server's staged snapshot write, the wire, the wait for a " +
                "frame — while the round trip is a heartbeat through the socket that sees none " +
                "of the staging and is half the wrong quantity besides.");
        }

        /// <summary>
        /// An acknowledgement floor can only ever ADD lead in whole ticks it has evidence for.
        /// </summary>
        /// <remarks>
        /// The floor is a minimum over <c>constant + wait</c> observations, so it sits above
        /// the constant until the wait has swept its whole range — and a sweep that is real but
        /// slow still leaves it high. An over-lead is the original defect arriving from the
        /// other side; an under-lead just leaves residual. Truncating makes an inflated reading
        /// cost accuracy and never correctness.
        /// </remarks>
        /// <summary>
        /// The runaway ceiling must not shrink merely because an acknowledgement floor appeared.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The round-trip term used to be computed only on the branch the floor did not take,
        /// so <c>rttTicks</c> was still zero when the ceiling was built from it. The arrival of
        /// a measurement therefore tightened a clamp that exists to bound a runaway — for a
        /// reason that has nothing to do with runaways — and did so silently, on a change whose
        /// whole safety argument was that it could not touch the steer.
        /// </para>
        /// <para>
        /// This is the second half of the coupling that moved the live clock error when the
        /// estimator was first wired in. It is checked here because a ceiling is invisible
        /// until something hits it, and nothing in an ordinary run does.
        /// </para>
        /// </remarks>
        [Test]
        public void TheCeilingKeepsTheRoundTripWhenAFloorAppears()
        {
            var binder = NewBinder();
            long tick = 1000;
            double now = ClockOffset + tick / (double)BaseHz + 0.010;

            // Long enough to CORROBORATE, not merely to fit. The first fit lands at the
            // minimum baseline and only sets the reference; the slope is believed once it has
            // reproduced over a doubled baseline, which needs roughly four times the minimum.
            // Warming only to the first fit leaves the age on the provisional path, where it is
            // clamped to the snapshot gap — correct behaviour, and not what these two pin.
            double until = now + SnapshotStalenessEstimator.MinimumBaselineSeconds * 5;
            while (now < until)
            {
                binder.TickRate.Sample(tick, now);
                binder.Staleness.Sample(tick, now, BaseHz);
                tick += SnapshotEvery;
                now += Interval;
            }

            Assert.That(binder.Staleness.IsUsable, Is.True, "precondition: a line must be fitted");
            Assert.That(binder.Staleness.AgeIsFitted, Is.True,
                "precondition: and its slope must have reproduced, or the age is measured "
                + "against the unit-rate floor instead and this test is pinning the wrong branch");

            // 100 ms round trip at 60 Hz is 6 ticks.
            binder.RoundTripMs = 100;

            // A floor from a fast route: measured, offered, and contributing ~nothing itself.
            // Exactly the shape that made the deletion invisible.
            FeedAckFloor(binder, constantSeconds: 0.0, seconds: 40.0);
            Assert.That(binder.AckLatency.HasEstimate, Is.True, "precondition: a floor is offered");

            // One snapshot held long past anything the route produces, so the ceiling is what
            // is returned and can be read.
            binder.TickRate.Sample(tick, now + 60.0 / BaseHz);
            binder.Staleness.Sample(tick, now + 60.0 / BaseHz, BaseHz);

            int gap = binder.TickRate.SnapshotTickGap;
            int rttTicks = (int)Math.Round(binder.RoundTripMs * binder.TickRate.EstimatedHz / 1000.0);
            Assert.That(rttTicks, Is.GreaterThan(0),
                "precondition: the round trip must be worth whole ticks, or there is nothing " +
                "for the ceiling to lose");

            Assert.That(binder.TargetLeadTicks(),
                Is.EqualTo(gap * 2 + rttTicks + (int)Math.Ceiling(binder.AckLatency.ConservativeFloorTicks)),
                "the ceiling is two snapshot intervals plus the round trip plus the measured " +
                "floor. Computing the round trip only when there is no floor made a floor " +
                "lower the clamp by the whole round trip here, which is a steer, not a bound.");
        }

        /// <summary>
        /// A provisional age that saturates its own clamp must not be delivered as the clamp
        /// value, because the clamp value is the defect.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>Math.Min(StalenessTicks, gap)</c> reads as a safety ceiling and behaves like one
        /// while the provisional figure is roughly right. It is not a ceiling once the figure
        /// runs away: the provisional reading carries no slope, so an untrustworthy timebase
        /// makes it accumulate, it exceeds <c>gap</c> on every call, and the clamp then returns a
        /// CONSTANT — which is <c>gap</c>, the warm-up fallback the whole of v0.33.0 and v0.34.0
        /// exist to stop steering on.
        /// </para>
        /// <para>
        /// Measured: a provisional age of <b>45.56 base ticks</b> against a true age of 0.09 one
        /// commit earlier on the same box, all three arms steering on a lead of 4, the worst
        /// correction figures of any run in the sequence — <c>37 of 39</c> above one step at
        /// <c>4.00</c> — and <c>reconciles from history 142 hit / 0 missed</c>, so nothing was
        /// missing from the history and the corrections were pure over-lead.
        /// </para>
        /// </remarks>
        [Test]
        public void ASaturatedProvisionalReadingIsRefusedRatherThanDeliveredAsTheClamp()
        {
            var binder = NewBinder();
            long tick = 1000;
            double now = ClockOffset + tick / (double)BaseHz + 0.010;

            // Enough for a provisional reading, and a client clock running fast enough that the
            // unit-rate height runs away: this is the shape a refused fit leaves behind.
            for (var i = 0; i < 40; i++)
            {
                binder.TickRate.Sample(tick, now);
                binder.Staleness.Sample(tick, now, BaseHz);
                tick += SnapshotEvery;
                now += Interval * 1.08;          // 8% fast, the measured artefact's magnitude
            }

            Assert.That(binder.Staleness.HasEstimate, Is.True, "precondition: a provisional reading");
            Assert.That(binder.Staleness.AgeIsFitted, Is.False,
                "precondition: and it must be the provisional one, not a corroborated fit");
            Assert.That(binder.Staleness.StalenessTicks,
                Is.GreaterThan(binder.TickRate.SnapshotTickGap),
                "precondition: the reading must actually saturate, or this pins nothing");

            Assert.That(binder.TargetLeadTicks(), Is.Zero,
                "a saturated clamp is a constant, and that constant is the warm-up fallback. "
                + "An untrustworthy timebase has to produce an UNDER-lead, not the largest lead "
                + "available — the uplink is still covered by the acknowledgement floor, which is "
                + "measured independently of this.");
        }

        [Test]
        public void AnInflatedFloorCannotOverLead()
        {
            var binder = NewBinder();
            long tick = 1000;
            double now = ClockOffset + tick / (double)BaseHz + 0.010;
            FeedCadence(binder, ref tick, ref now, 8);

            // True constant of one tick; the estimator will read it at one-point-something
            // because the wait never quite reaches zero.
            FeedAckFloor(binder, constantSeconds: 1.0 / BaseHz, seconds: 40.0);

            Assert.That(binder.AckLatency.HasEstimate, Is.True, "precondition");
            Assert.That(binder.AckLatency.FloorTicks, Is.GreaterThanOrEqualTo(1f),
                "precondition: the reading is at or above the true constant, never below it");
            Assert.That(binder.TargetLeadTicks(),
                Is.LessThanOrEqualTo((int)Math.Floor(binder.AckLatency.FloorTicks) + 1),
                "the lead must not exceed the whole ticks the floor has evidence for, plus " +
                "whatever the snapshot age legitimately contributes.");
            Assert.That(binder.TargetLeadTicks(), Is.LessThanOrEqualTo(2),
                "a one-tick pipeline must not produce a lead of three because the estimator " +
                "read 1.9 and something rounded up.");
        }

        private sealed class NullView : IEntityView
        {
            public void Spawn(string id, bool isLocal, string type) { }
            public void Despawn(string id) { }
            public void SetState(string id, float x, float y, int hp, int maxHp,
            uint facingBrad, Shared.GameLogic.Components.EntityAction action) { }
        }
    }
}
