using System;
using NUnit.Framework;
using Cuvara.Netcode.Prediction;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Pins the measurement of the pipeline constant the staleness fit cannot see.
    /// </summary>
    /// <remarks>
    /// The client applies an input at its own base tick; the server applies it at the tick
    /// its packet is drained on, and reports it on a snapshot already old when it is read. So
    /// the steering target must be <c>uplink + snapshot age</c>, and both terms are constants
    /// that a lower-envelope fit absorbs by construction. Every case here drives the estimator
    /// with a synthetic clock, so a reading is a property of the arithmetic.
    /// </remarks>
    [TestFixture]
    public sealed class AckLatencyEstimatorTests
    {
        private const float BaseHz = 60f;
        private const double SendPeriod = 1.0 / 15.0;
        private const double SnapshotPeriod = 4.0 / BaseHz;      // 15 Hz on a 60 Hz base
        private const double ClockOffset = 9876.5;

        /// <summary>
        /// Drives a session where the true pipeline constant is <paramref name="uplinkPlusAge"/>
        /// seconds and acknowledgements arrive on a snapshot cadence that drifts against the
        /// send cadence, which is what lets the wait term sweep through zero.
        /// </summary>
        private static AckLatencyEstimator Drive(
            double uplinkPlusAge, double seconds, double snapshotPeriod = SnapshotPeriod)
        {
            var e = new AckLatencyEstimator();
            var arrivals = new System.Collections.Generic.Queue<(long Tick, double At)>();
            double now = ClockOffset;
            double nextSend = now, nextSnapshot = now + snapshotPeriod * 0.37;
            long inputTick = 0, accepted = 0;
            double end = now + seconds;

            while (now < end)
            {
                now = Math.Min(nextSend, nextSnapshot);

                if (now >= nextSend)
                {
                    nextSend = now + SendPeriod;
                    inputTick++;
                    e.RecordSent(inputTick, now);
                    // The server accepts it uplinkPlusAge later; the snapshot that carries the
                    // acknowledgement is whichever one is produced after that instant.
                    arrivals.Enqueue((inputTick, now + uplinkPlusAge));
                }

                if (now < nextSnapshot) continue;

                nextSnapshot = now + snapshotPeriod;
                while (arrivals.Count > 0 && arrivals.Peek().At <= now)
                {
                    accepted = arrivals.Dequeue().Tick;
                }

                if (accepted > 0) e.RecordAck(accepted, now, BaseHz);
            }

            return e;
        }

        [TestCase(0.0)]
        [TestCase(0.0167)]     // one base tick
        [TestCase(0.0333)]     // two base ticks
        public void TheFloorConvergesOnTheRealConstant(double uplinkPlusAge)
        {
            // Snapshot cadence 3% off the send cadence: two independent clocks, which is what
            // makes the wait term sweep through zero. See SweptEnough.
            var e = Drive(uplinkPlusAge, seconds: 40.0, snapshotPeriod: SnapshotPeriod * 1.03);

            Assert.That(e.HasEstimate, Is.True, "a sweeping link must produce a floor");
            Assert.That(e.FloorSeconds, Is.EqualTo(uplinkPlusAge).Within(SnapshotPeriod * 0.25),
                "one observation is uplink + wait-for-the-next-snapshot + age, and the wait is " +
                "the only term that varies. Its minimum must land on the constant, not on the " +
                "mean, which would be the constant plus half a snapshot interval.");
            Assert.That(e.FloorTicks, Is.EqualTo((float)(uplinkPlusAge * BaseHz)).Within(1.5f));
        }

        /// <summary>
        /// A link whose send and snapshot cadences never drift apart offers NO floor, rather
        /// than a floor inflated by the fixed wait.
        /// </summary>
        /// <remarks>
        /// This is the failure mode that makes the sweep check load-bearing rather than
        /// decorative, and it is not hypothetical: a client sending at the world rate is sending
        /// at exactly the snapshot rate, so a perfectly matched pair of clocks would sit here.
        /// The floor would then read the constant plus a fixed wait of up to a whole snapshot
        /// interval, the lead would be too large by that much, and an over-lead is the original
        /// defect arriving from the other side. Offering nothing keeps the caller on whatever it
        /// used before, which is the one safe answer.
        /// </remarks>
        [Test]
        public void APhaseLockedLinkOffersNothingRatherThanAnInflatedFloor()
        {
            var e = Drive(0.0167, seconds: 40.0, snapshotPeriod: SendPeriod);

            Assert.That(e.Samples, Is.GreaterThan(AckLatencyEstimator.MinimumSamples),
                "precondition: the link must have produced plenty of observations");
            Assert.That(e.SweptEnough, Is.False,
                "the wait never varied, so nothing here is evidence about where the floor is");
            Assert.That(e.HasEstimate, Is.False,
                "and a floor must not be offered on that evidence");
            Assert.That(e.FloorTicks, Is.EqualTo(0f));
        }

        [Test]
        public void NothingIsOfferedBeforeTheFloorMeansAnything()
        {
            var e = new AckLatencyEstimator();
            double now = ClockOffset;

            // Latencies that do sweep, so only the sample count is under test here.
            //
            // Acknowledgements on the snapshot cadence, the SEND time moving. Stamping the acks
            // at `now + latency` instead would let the latency spread shrink the measured
            // snapshot interval -- the estimator takes it as the smallest gap between
            // acknowledgements -- and with it the sweep requirement that interval scales.
            // Up to MinimumSweepSamples, not MinimumSamples: a sweep verdict below that count
            // is read from the extremes and is not evidence of anything. See
            // TheFirstVerdictIsNotTakenFromTheExtremes.
            for (var i = 1; i <= AckLatencyEstimator.MinimumSweepSamples - 1; i++)
            {
                now += SnapshotPeriod;
                e.RecordSent(i, now - (i % 4) * SnapshotPeriod * 0.3);
                e.RecordAck(i, now, BaseHz);

                Assert.That(e.HasEstimate, Is.False,
                    "this number ADDS lead, so a floor from too little evidence steers the " +
                    "clock PAST the server — the defect it exists to remove, arriving from " +
                    "the other side. There is deliberately no provisional reading.");
                Assert.That(e.FloorTicks, Is.EqualTo(0f), "and it must read zero, not a guess");
            }

            for (var i = AckLatencyEstimator.MinimumSweepSamples;
                 i <= AckLatencyEstimator.MinimumSweepSamples + 4;
                 i++)
            {
                now += SnapshotPeriod;
                e.RecordSent(i, now - (i % 4) * SnapshotPeriod * 0.3);
                e.RecordAck(i, now, BaseHz);
            }

            Assert.That(e.HasEstimate, Is.True, "and it must arrive as soon as it does mean something");
        }

        [Test]
        public void AStallIsRefusedRatherThanAllowedToSetTheFloor()
        {
            var e = new AckLatencyEstimator();
            double now = ClockOffset;

            e.RecordSent(1, now);
            e.RecordAck(1, now + AckLatencyEstimator.MaximumFloorSeconds + 0.5, BaseHz);

            Assert.That(e.Samples, Is.EqualTo(0), "a stall is not a floor");
            Assert.That(e.Refused, Is.EqualTo(1),
                "and the refusal must be counted, or a connection that only ever stalls is " +
                "indistinguishable from one that was never measured");
        }

        [Test]
        public void AnAcknowledgementBeforeItsSendIsRefused()
        {
            var e = new AckLatencyEstimator();
            e.RecordSent(1, ClockOffset);
            e.RecordAck(1, ClockOffset - 0.05, BaseHz);

            Assert.That(e.Samples, Is.EqualTo(0));
            Assert.That(e.Refused, Is.EqualTo(1),
                "an acknowledgement cannot precede its send, so this is a clock that moved " +
                "and not a fast route — and folding it in would set the floor negative");
        }

        [Test]
        public void UnusableInputsAreIgnored()
        {
            var e = new AckLatencyEstimator();
            e.RecordSent(0, ClockOffset);
            e.RecordSent(1, double.NaN);
            e.RecordAck(0, ClockOffset, BaseHz);
            e.RecordAck(1, double.PositiveInfinity, BaseHz);
            e.RecordAck(1, ClockOffset, 0f);

            Assert.That(e.Samples, Is.EqualTo(0));
            Assert.That(e.HasEstimate, Is.False);
        }

        [Test]
        public void AFloorThatWorsensIsFollowedRatherThanRememberedForever()
        {
            // A route that doubles its constant must be tracked, or the lead stays short on a
            // connection that has genuinely changed.
            var e = Drive(0.0167, seconds: 20.0, snapshotPeriod: SnapshotPeriod * 1.03);
            Assert.That(e.FloorSeconds, Is.EqualTo(0.0167).Within(SnapshotPeriod * 0.25));

            double now = ClockOffset + 12.0;
            long tick = 100000;
            for (var i = 0; i < 400; i++)
            {
                tick++;
                e.RecordSent(tick, now);
                now += SendPeriod;
                e.RecordAck(tick, now + 0.0500, BaseHz);
            }

            Assert.That(e.FloorSeconds, Is.GreaterThan(0.0167 * 1.5),
                "the epoch memory is five to ten seconds precisely so a worse route is " +
                "followed. A permanent minimum would hold the lead down at a figure the " +
                "connection stopped producing.");
        }

        [Test]
        public void ResetForgetsTheRoute()
        {
            var e = Drive(0.0333, seconds: 40.0, snapshotPeriod: SnapshotPeriod * 1.03);
            Assert.That(e.HasEstimate, Is.True, "precondition");

            e.Reset();

            Assert.That(e.HasEstimate, Is.False);
            Assert.That(e.FloorTicks, Is.EqualTo(0f));
            Assert.That(e.Samples, Is.EqualTo(0));
            Assert.That(e.Refused, Is.EqualTo(0));
        }

        /// <summary>
        /// A phase-locked link whose acknowledgements retire several inputs at once must still
        /// be refused, and the retired-but-not-newest inputs are why it once was not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the failure the retire-everything loop actually caused, and it is NOT the
        /// one it was suspected of. Timing every input an acknowledgement covers cannot pull
        /// the floor down: an older input waited for an acknowledgement a later input had
        /// already earned, so its interval is LARGER, and a minimum is monotone — larger values
        /// never move it. The suspicion that stale send times dragged the minimum down is
        /// refuted by that alone.
        /// </para>
        /// <para>
        /// What they do corrupt is the SPAN, and the span is the entire evidence
        /// <see cref="AckLatencyEstimator.SweptEnough"/> rests on. Here the send cadence is four
        /// times the acknowledgement cadence and locked to it, so every acknowledgement retires
        /// four inputs whose send times are three send periods apart. Folding all four in
        /// stretches the observed maximum by that spread — which has nothing to do with the wait
        /// term — and the guard reads "swept" on a link where the wait never varied at all. A
        /// guard fed values from outside the quantity it guards is not a guard.
        /// </para>
        /// </remarks>
        [Test]
        public void SupersededObservationsDoNotStretchTheSweep()
        {
            var e = new AckLatencyEstimator();
            double now = ClockOffset;
            const double constant = 0.0100;
            long tick = 0;

            // Four sends per acknowledgement, exactly in phase: the wait is the same every
            // time, so there is no sweep and nothing may be offered.
            for (var snapshot = 0; snapshot < 200; snapshot++)
            {
                for (var k = 0; k < 4; k++)
                {
                    tick++;
                    e.RecordSent(tick, now + k * (SnapshotPeriod / 4.0));
                }

                now += SnapshotPeriod;
                e.RecordAck(tick, now + constant, BaseHz);
            }

            Assert.That(e.Superseded, Is.GreaterThan(0),
                "precondition: acknowledgements must actually be covering more than one input, "
                + "or this case is not exercising the loop it is about");

            Assert.That(e.SweptEnough, Is.False,
                "the wait term is identical on every observation here — the cadences are locked "
                + "at 4:1 — so nothing about this link is evidence that the minimum is near the "
                + "constant. It read swept only because the superseded inputs' send times "
                + "stretched the span by three send periods.");

            Assert.That(e.HasEstimate, Is.False,
                "and with no sweep there must be no floor, because a floor here reads high by a "
                + "fixed wait and an over-lead is the original defect from the other side.");
        }

        /// <summary>
        /// A constant smaller than one base tick must survive into the contribution, because
        /// truncating it to a whole tick is how this term stayed open.
        /// </summary>
        /// <remarks>
        /// The lead was fed <c>Math.Floor(FloorTicks)</c>, which is zero for every localhost
        /// link ever measured here — 0.14 and 0.68 base ticks were the two live readings. So the
        /// estimator contributed nothing in exactly the regime it exists for. And the deficit is
        /// not proportionally small: the tick label is an integer, so a lead 0.68 ticks short
        /// carries the wrong tick number for most of every tick and the reconcile returns a
        /// whole step for it.
        /// </remarks>
        [Test]
        public void AFractionOfABaseTickSurvivesInsteadOfTruncatingToNothing()
        {
            // 0.3 of a base tick: far below anything Math.Floor can carry.
            var e = Drive(0.3 / BaseHz, seconds: 60.0, snapshotPeriod: SnapshotPeriod * 1.03);

            Assert.That(e.HasEstimate, Is.True, "precondition: the sweep must have been verified");
            Assert.That(Math.Floor(e.FloorTicks), Is.EqualTo(0.0),
                "precondition: this is the regime truncation discards entirely");

            Assert.That(e.ConservativeFloorTicks, Is.GreaterThan(0f),
                "a sub-tick constant is still a whole step of correction, because the tick "
                + "label it shifts is an integer. Truncation made the estimator a no-op on "
                + "every fast link, which is every link it was measured on.");

            Assert.That(e.ConservativeFloorTicks, Is.LessThanOrEqualTo(e.FloorTicks),
                "the contribution is the floor biased LOW and may never exceed it");
        }

        /// <summary>
        /// The contribution is the floor less the quantile's OWN construction bias, so it lands
        /// on the pipeline constant instead of a tenth of a snapshot interval above it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the discriminating test for the floor-statistic change, and it replaces
        /// the one that pinned <c>FloorSeconds - UnsweptSeconds</c>.</b> That term subtracted
        /// the unsampled remainder of the wait's range — about <c>S / phases</c> — from a floor
        /// inflated by <c>0.1 · S</c>. Those are unrelated quantities and they nearly cancelled
        /// only because the shipped cadences visit 13 and 23 phases, either side of the 10 that
        /// would make it exact; the remainder <c>S · (0.1 − 1/phases)</c> changes SIGN below ten
        /// phases. A term that is correct for its current inputs is not a working term.
        /// </para>
        /// <para>
        /// <b>Every number here is derived from the run, not chosen.</b> The bias asserted is
        /// <c>FloorPercentile · S</c> with <c>S</c> the estimator's own measured snapshot
        /// interval, and the tolerance is HALF that bias — so the test separates the corrected
        /// reading from the uncorrected one by construction, at any snapshot rate, rather than
        /// by a constant that could be widened until a run passed. Against the uncorrected term
        /// the second assertion fails by the full <c>0.1 · S</c>.
        /// </para>
        /// </remarks>
        [TestCase(0.0)]
        [TestCase(0.0083)]     // half a base tick
        [TestCase(0.0167)]     // one base tick
        [TestCase(0.0333)]     // two base ticks
        public void TheContributionRemovesTheQuantilesOwnConstructionBias(double uplinkPlusAge)
        {
            var e = Drive(uplinkPlusAge, seconds: 60.0, snapshotPeriod: SnapshotPeriod * 1.03);

            Assert.That(e.HasEstimate, Is.True, "precondition: the sweep must have been verified");

            double intervalTicks = e.AckIntervalSeconds * BaseHz;
            Assert.That(intervalTicks, Is.GreaterThan(1.0),
                "precondition: the snapshot interval must have been measured, since the bias "
                + "under test is a fraction of it");

            double trueTicks = uplinkPlusAge * BaseHz;
            double bias = AckLatencyEstimator.FloorPercentile * intervalTicks;
            double tolerance = bias * 0.5;

            // THE DEFECT ITSELF, read off the real estimator rather than assumed.
            Assert.That(e.FloorTicks - trueTicks, Is.EqualTo(bias).Within(tolerance),
                "one observation is `constant + wait` and the wait sweeps a snapshot interval, "
                + "so the tenth percentile sits 0.1 * S above the constant BY CONSTRUCTION — on "
                + "every clean run, contamination or not. That is the inflation, measured.");

            Assert.That(e.ConservativeFloorTicks, Is.EqualTo((float)trueTicks).Within(tolerance),
                "and the contribution must have that bias removed, landing on the pipeline "
                + "constant. `FloorSeconds - UnsweptSeconds` does not: on a fully swept link "
                + "the unswept remainder goes to zero and the whole 0.1 * S inflation survives "
                + "into the lead, which is the over-lead direction this estimator exists to "
                + "avoid.");

            Assert.That(e.FloorTicks - e.ConservativeFloorTicks, Is.EqualTo(bias).Within(tolerance),
                "and WHAT IS REMOVED must be that bias, independently of the constant — this is "
                + "the claim stated directly rather than through the answer. `UnsweptSeconds` "
                + "removes about S/phases instead, which is a different quantity that merely "
                + "resembles it at the two cadences this package ships.");

            Assert.That(e.ConservativeFloorTicks, Is.LessThanOrEqualTo(e.FloorTicks),
                "biased low, never high: an over-lead is the defect this exists to remove, "
                + "arriving from the other side.");
        }

        /// <summary>
        /// What was subtracted is reported, and it is the fitted slope's tenth — so a reader can
        /// check the correction rather than infer it from two printed floors.
        /// </summary>
        [Test]
        public void TheSubtractedBiasIsReportedAndIsTheLaddersOwnSlope()
        {
            var e = Drive(0.0167, seconds: 60.0, snapshotPeriod: SnapshotPeriod * 1.03);

            Assert.That(e.HasEstimate, Is.True, "precondition");
            Assert.That(e.FloorCorrectionApplied, Is.True,
                "a cleanly swept link must produce a straight ladder and an applied correction");
            Assert.That(e.FloorCorrectionRefusals, Is.EqualTo(0),
                "and must never have fallen back on the way there");

            Assert.That(e.FloorTicks - e.ConservativeFloorTicks,
                Is.EqualTo(e.FloorBiasTicks).Within(1e-3f),
                "what the floor lost must equal what is reported as subtracted, or the reported "
                + "figure is decoration");

            Assert.That(e.FloorBiasTicks,
                Is.EqualTo((float)(AckLatencyEstimator.FloorPercentile * e.LadderSlopeTicks))
                    .Within(1e-3f),
                "the bias is the run's OWN slope times the percentile — self-correcting, rather "
                + "than an assumed snapshot interval");

            Assert.That(e.LadderSlopeTicks,
                Is.EqualTo((float)(e.AckIntervalSeconds * BaseHz)).Within(e.AckIntervalSeconds * BaseHz * 0.25),
                "and on a swept link that slope IS the snapshot interval, which is the check "
                + "that the line describes the quantity it is supposed to");
        }

        /// <summary>
        /// A distribution the affine sweep model does not describe is REFUSED, visibly, rather
        /// than corrected by a slope that means nothing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The bias subtracted above exists only because the quantiles are affine in <c>q</c>.
        /// Where they are not, there is no <c>0.1 · S</c> to remove and a fitted slope is a
        /// number from a falsified model. <b>A fallback is another claim about the same
        /// quantity</b>, and the two available here were the raw percentile — which is the known
        /// over-lead bias, the defect — and nothing. Nothing wins, because an under-lead merely
        /// leaves residual in place.
        /// </para>
        /// <para>
        /// What this test really pins is that the refusal is NOT SILENT. A contribution of zero
        /// reads identically to "the link is instant" in every other counter, so
        /// <c>FloorCorrectionRefusals</c> and <c>FloorCorrectionApplied</c> have to move.
        /// </para>
        /// </remarks>
        [Test]
        public void ALadderThatIsNotStraightRefusesTheCorrectionVisibly()
        {
            // A BIMODAL distribution: two swept bands with a gap between them. Each band on
            // its own is affine in q, and the pair is not — the ladder steps across the gap
            // instead of rising through it, which is exactly the model being false rather than
            // the data being noisy. Both bands sweep a whole snapshot interval, so the sweep
            // guard is satisfied and a floor IS offered; the refusal under test therefore comes
            // from the ladder's shape and from nothing else.
            //
            // This is not a hypothetical shape. A bimodal-under-load regime is the one the
            // percentile's original justification of record appealed to.
            var latencies = new System.Collections.Generic.List<double>();
            for (var i = 0; i < 160; i++)
            {
                double phase = (i % 40) / 40.0;
                latencies.Add(i % 2 == 0
                    ? 0.0050 + phase * SnapshotPeriod                        // the near band
                    : 0.0050 + SnapshotPeriod * 3.0 + phase * SnapshotPeriod); // the far band
            }

            var e = DriveDistribution(latencies.ToArray());

            Assert.That(e.HasEstimate, Is.True,
                "precondition: a floor IS offered here, so the refusal under test is about the "
                + "ladder's shape and not about the sweep guard");
            Assert.That(e.FloorTicks, Is.GreaterThan(0f), "precondition");

            Assert.That(e.FloorCorrectionApplied, Is.False,
                "the points are not on a line, so the model that predicts the bias does not "
                + "describe this distribution and there is nothing to subtract");
            Assert.That(e.FloorCorrectionRefusals, Is.GreaterThan(0),
                "and the substitution must be COUNTED. An invisible fallback is how three "
                + "defects reached this package this month.");
            Assert.That(e.ConservativeFloorTicks, Is.EqualTo(0f),
                "refused, not repaired: a distribution the model does not describe is not "
                + "handed to a statistic chosen to survive it.");
            Assert.That(e.FloorBiasTicks, Is.EqualTo(0f),
                "and nothing may be reported as subtracted when nothing was");
        }

        /// <summary>
        /// An acknowledgement naming a tick this client has never sent belongs to a previous
        /// session, and must not be allowed to set the floor.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the defect that made the estimator read a fifth of the observed minimum in a
        /// full suite while passing when run alone, and it is a real client condition rather
        /// than a test artefact: a reconnect onto a server that has not yet reaped the previous
        /// player keeps that player's <c>LastInputTick</c>, so the fresh session — numbering
        /// from 1 — has every input satisfy <c>tick &lt;= ackTick</c> the moment it is sent.
        /// Each is then retired by the very next snapshot and timed at the wait for one client
        /// frame, and the minimum filter holds that for its whole epoch memory.
        /// </para>
        /// <para>
        /// Live, that produced a floor of 0.17 base ticks against an input-to-acknowledgement
        /// distribution whose MINIMUM was 1.39 — a reading below anything the route ever did,
        /// which is the opposite of the inflation the sweep guard was built for.
        /// </para>
        /// </remarks>
        [Test]
        public void AnAcknowledgementFromAPreviousSessionCannotSetTheFloor()
        {
            var e = new AckLatencyEstimator();
            double now = ClockOffset;

            // The server still holds the old session's newest input tick.
            const long stale = 5000;
            const double realConstant = 0.0250;

            for (var i = 0; i < 300; i++)
            {
                long tick = i + 1;                      // the fresh session numbers from 1
                e.RecordSent(tick, now);
                now += SendPeriod;

                // A client frame later the next snapshot lands, carrying an acknowledgement
                // from the session before this one, jittered over a realistic frame wait.
                // Unguarded these are 1-9 ms observations against a real pipeline of 25.
                //
                // The assertion that discriminates below is Samples, not HasEstimate. In this
                // synthetic shape the sweep guard happens to refuse the floor as well, because
                // a frame wait does not span half a snapshot interval — but that is an
                // accident of the shape and it did NOT hold live, where a floor of 0.17 base
                // ticks was offered against an observed minimum of 1.39. The invariant worth
                // pinning is that these are not observations at all.
                e.RecordAck(stale, now + 0.0010 + (i % 9) * 0.0010, BaseHz);
            }

            Assert.That(e.HasEstimate, Is.False,
                "not one of these intervals is an input-to-acknowledgement time — the "
                + "acknowledgement was already past the tick before it was stamped — so there "
                + "is nothing here to offer a floor from.");

            Assert.That(e.Samples, Is.Zero,
                "and none of them may be counted as an observation either, or the sample "
                + "threshold is satisfied by evidence about a connection that ended.");

            Assert.That(e.AckAheadOfSend, Is.GreaterThan(0),
                "and the condition is counted rather than silently swallowed: a client seeing "
                + "this past its first seconds is talking to a server that thinks it is "
                + "someone else, which is worth a counter.");

            // Once the client's own numbering catches up, the estimator works normally.
            long t = stale;
            var arrivals = new System.Collections.Generic.Queue<(long Tick, double At)>();
            double nextSend = now, nextSnap = now + SnapshotPeriod * 0.37;
            double snapPeriod = SnapshotPeriod * 1.03;
            long accepted = 0;
            double end = now + 60.0;

            while (now < end)
            {
                now = Math.Min(nextSend, nextSnap);
                if (now >= nextSend)
                {
                    nextSend = now + SendPeriod;
                    t++;
                    e.RecordSent(t, now);
                    arrivals.Enqueue((t, now + realConstant));
                }

                if (now < nextSnap) continue;
                nextSnap = now + snapPeriod;
                while (arrivals.Count > 0 && arrivals.Peek().At <= now) accepted = arrivals.Dequeue().Tick;
                if (accepted > 0) e.RecordAck(accepted, now, BaseHz);
            }

            Assert.That(e.HasEstimate, Is.True,
                "the guard must drop the stale acknowledgements, not the connection");
            Assert.That(e.FloorSeconds, Is.EqualTo(realConstant).Within(SnapshotPeriod * 0.5),
                "and the floor that follows must be the real constant, not the 1 ms the stale "
                + "acknowledgements would have pinned it to for the rest of the epoch.");
        }

        /// <summary>
        /// Feed a distribution directly, one observation per acknowledgement, so a shape
        /// measured on a live run can be replayed exactly.
        /// </summary>
        private static AckLatencyEstimator DriveDistribution(double[] latencies)
        {
            var e = new AckLatencyEstimator();
            double now = ClockOffset;
            long tick = 0;

            foreach (double latency in latencies)
            {
                tick++;

                // ACKNOWLEDGEMENTS ARRIVE ON THE SNAPSHOT CADENCE; the SEND time is what moves.
                // That is the real shape and it matters here: the estimator measures the
                // snapshot interval as the smallest gap between acknowledgements, so stamping
                // the acks at `now + latency` would let the latency spread shrink the measured
                // interval and, with it, the sweep requirement itself.
                now += SnapshotPeriod;
                e.RecordSent(tick, now - latency);
                e.RecordAck(tick, now, BaseHz);
            }

            return e;
        }

        /// <summary>
        /// A tight distribution with a handful of outliers must be REFUSED, not floored — and
        /// the sweep guard is what has to refuse it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This replays a distribution measured live inside a loaded suite: a body at 23–33 ms,
        /// a tail to 74, and three observations near 3 ms where an input happened to arrive
        /// immediately before a gather. The extremum reported <b>0.17 base ticks</b> against an
        /// observed minimum of 1.39, and the response at the time was to take a tenth-percentile
        /// quantile instead.
        /// </para>
        /// <para>
        /// <b>That was the wrong layer, and this test now pins the right one.</b> A distribution
        /// this tight is one whose wait term never swept, so no floor should be offered from it
        /// at any percentile — the minimum is unrepresentative and the quantile is merely less
        /// obviously so. The guard failed to refuse it because its span was <c>max - min</c>,
        /// which three outliers satisfy. Measured between quantiles, the span is about 10 ms
        /// against a 33 ms requirement and the whole distribution is correctly refused.
        /// </para>
        /// </remarks>
        [Test]
        public void ATightDistributionWithOutliersIsRefusedRatherThanFloored()
        {
            var latencies = new System.Collections.Generic.List<double>();
            for (var i = 0; i < 140; i++)
            {
                if (i % 47 == 0) latencies.Add(0.0028);              // caught a gather: 3 of 140
                else if (i % 11 == 0) latencies.Add(0.0325 + (i % 5) * 0.0084);   // the tail, to 74
                else latencies.Add(0.0231 + (i % 7) * 0.0014);      // the body, 23-31
            }

            var e = DriveDistribution(latencies.ToArray());

            Assert.That(e.SweptEnough, Is.False,
                "the wait term never varied here — the body spans 8 ms of a 67 ms interval — so "
                + "nothing about this link is evidence that its minimum is near the constant. "
                + "Reading a span from max minus min let three outliers certify it as swept.");

            Assert.That(e.HasEstimate, Is.False,
                "and with no sweep there must be no floor at any percentile. Choosing a more "
                + "robust statistic here treats the symptom: the guard above it is admitting "
                + "data it should refuse.");
        }

        /// <summary>
        /// On a genuinely swept link the floor still lands on the constant.
        /// </summary>
        [TestCase(0.0)]
        [TestCase(0.0167)]
        [TestCase(0.0333)]
        public void OnASweptLinkTheFloorStillFindsTheConstant(double uplinkPlusAge)
        {
            var e = Drive(uplinkPlusAge, seconds: 60.0, snapshotPeriod: SnapshotPeriod * 1.03);

            Assert.That(e.HasEstimate, Is.True, "precondition");
            Assert.That(e.FloorSeconds, Is.EqualTo(uplinkPlusAge).Within(SnapshotPeriod * 0.25),
                "with the wait genuinely sweeping, the smallest observations sit on the "
                + "constant. That is the case the minimum filter was always right for, and the "
                + "only case a floor is now offered in at all.");
        }

        /// <summary>
        /// The live phase lock, with its one outlier: a span taken from the extremes certifies
        /// it, a span taken between quantiles refuses it.
        /// </summary>
        /// <remarks>
        /// Measured on a client sending at 15 Hz into a 15 Hz snapshot stream — the case this
        /// class's remarks warn about by name: <c>min 5.5 ms, median 58.8, p90 62.2</c>. The
        /// wait is pinned near its maximum on all but one observation. <c>max - min</c> reads
        /// 57 ms against a 33 ms requirement and passes; the floor then lands on the locked mode
        /// at <b>3.33 base ticks</b> while the harness's own observed minimum is <b>0.33</b> —
        /// ten times high, straight into the steering lead, which reached 8 and pushed the
        /// reconcile's compare point past the retained history (39 hits against 120 misses,
        /// where a healthy run had 138 against 3).
        /// </remarks>
        [Test]
        public void APhaseLockWithOneOutlierCannotCertifyItselfAsSwept()
        {
            var latencies = new System.Collections.Generic.List<double>();
            for (var i = 0; i < 140; i++)
            {
                // One observation in forty caught a gather; the rest are pinned near the top of
                // the interval, which is what a phase lock looks like from here.
                latencies.Add(i % 40 == 0 ? 0.0055 : 0.0555 + (i % 9) * 0.0008);
            }

            var e = DriveDistribution(latencies.ToArray());

            Assert.That(e.SweptEnough, Is.False,
                "one observation cannot be the evidence that a wait term varied. Between the "
                + "tenth and ninetieth percentiles this data spans about 7 ms of a 67 ms "
                + "interval, which is a lock, not a sweep.");

            Assert.That(e.HasEstimate, Is.False,
                "so no floor is offered, and the lead keeps the round-trip fallback rather than "
                + "taking a reading ten times the observed minimum.");
        }

        /// <summary>
        /// The guard's FIRST verdict must not be its weakest one: a distribution that is
        /// refused once there are enough observations must not be certified while there are
        /// few.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>Quantile</c> truncates <c>q * n</c> to an index, so at <c>n = 8</c> the tenth
        /// percentile is index 0 and the ninetieth is index 7 — the minimum and the maximum.
        /// The span half of the guard is therefore <c>max - min</c> in that window, which is
        /// precisely the statistic the guard was rewritten to stop being, and the occupancy
        /// half does not cover for it: three buckets is a low bar for a body that is merely
        /// narrow rather than locked.
        /// </para>
        /// <para>
        /// This is the gather-catch shape the class already documents, widened to a body of
        /// 30-44 ms so that it occupies three buckets of a 67 ms interval. It reads a span of
        /// 39 ms at <c>n = 8</c> and 14 ms once the quantiles are interior — one is above the
        /// 33 ms requirement and the other is well below it, from the same distribution. The
        /// floor it would have offered in the window is taken at index 0 as well:
        /// <b>0.20 base ticks against a body minimum of 1.81</b>.
        /// </para>
        /// </remarks>
        [Test]
        public void TheFirstVerdictIsNotTakenFromTheExtremes()
        {
            var latencies = new System.Collections.Generic.List<double>();
            for (var i = 0; i < 140; i++)
            {
                // One in forty caught a gather; the rest are a narrow body that never swept.
                latencies.Add(i % 40 == 0 ? 0.0034 : 0.0301 + (i % 9) * 0.0018);
            }

            var all = latencies.ToArray();

            Assert.That(DriveDistribution(all).SweptEnough, Is.False,
                "precondition: with the quantiles interior this distribution spans about 14 ms "
                + "of a 67 ms interval and is correctly refused.");

            for (var n = AckLatencyEstimator.MinimumSamples;
                 n < AckLatencyEstimator.MinimumSweepSamples + 4;
                 n++)
            {
                var prefix = new double[n];
                Array.Copy(all, prefix, n);
                var e = DriveDistribution(prefix);

                Assert.That(e.SweptEnough, Is.False,
                    $"at n = {n} the same refused distribution certified itself as swept. A "
                    + "guard whose first verdict is its weakest one is worse than no guard: it "
                    + "passes in exactly the window — the first fraction of a second after a "
                    + "join or a reset — where nothing else has evidence to contradict it.");

                Assert.That(e.HasEstimate, Is.False,
                    $"and at n = {n} a floor followed from it. It is taken at index 0 there too, "
                    + "so it is the minimum of the gather catches rather than of the pipeline, "
                    + "and WorldViewBinder.TargetLeadTicks would let it DISPLACE the round-trip "
                    + "fallback rather than merely add to it.");
            }
        }

        /// <summary>
        /// The sample floor for a span verdict is derived from the quantiles, not written down,
        /// so changing either constant cannot silently reopen the window.
        /// </summary>
        [Test]
        public void TheSweepFloorIsWhereBothQuantilesBecomeInterior()
        {
            int n = AckLatencyEstimator.MinimumSweepSamples;

            Assert.That(n, Is.GreaterThanOrEqualTo(AckLatencyEstimator.MinimumSamples),
                "a sweep verdict can never be offered on fewer observations than a floor needs.");

            Assert.That((int)(AckLatencyEstimator.SweepLowQuantile * n), Is.GreaterThan(0),
                "at the floor the low quantile must not be the minimum.");

            Assert.That((int)(AckLatencyEstimator.SweepHighQuantile * n), Is.LessThan(n - 1),
                "at the floor the high quantile must not be the maximum. Note this is 11 for "
                + "0.10/0.90 and not 10: (int)(0.9 * 10) is 9, which is still the last index "
                + "of ten.");

            Assert.That((int)(AckLatencyEstimator.SweepLowQuantile * (n - 1)) == 0
                        || (int)(AckLatencyEstimator.SweepHighQuantile * (n - 1)) >= n - 2,
                Is.True,
                "and it must be the SMALLEST such count — one fewer observation must still put "
                + "a quantile on an extremum, or the floor is costing evidence for nothing.");
        }

        /// <summary>
        /// Feeds acknowledgements on a fixed <paramref name="cadence"/> whose ARRIVALS are
        /// delayed by a bounded, non-negative jitter, and optionally drops some snapshots.
        /// </summary>
        /// <remarks>
        /// A jitter that only ever delays is the physical case: an arrival is observed on a
        /// render frame, so it can be seen late and never early. The delays cycle
        /// deterministically rather than randomly, so a reading is a property of the
        /// arithmetic and not of a seed.
        /// </remarks>
        private static AckLatencyEstimator DriveArrivals(
            double cadence, double jitter, int count, int dropEvery = 0)
        {
            var e = new AckLatencyEstimator();
            long tick = 0;

            for (var i = 0; i < count; i++)
            {
                tick++;
                // Delays ramp 0 -> jitter over four arrivals and then reset, so every cycle
                // contains the pair the defect is made of: one arrival maximally late followed
                // by one on time, which shortens the gap between them by the whole jitter
                // range. A pattern that never puts the extremes adjacent never shortens a gap
                // by more than part of the range and understates the defect it is measuring.
                double delay = jitter * (i % 4) / 3.0;
                double at = ClockOffset + i * cadence + delay;

                e.RecordSent(tick, at - 0.005);
                if (dropEvery > 0 && i % dropEvery == dropEvery - 1) continue;
                e.RecordAck(tick, at, BaseHz);
            }

            return e;
        }

        /// <summary>
        /// The arrival-jitter case that sized the old defect, now pinning what replaced it:
        /// the reading is the cadence less the jitter <b>over the averaging window</b> —
        /// 63.889 ms against a true 66.667, where the old minimum read 50.000.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why a minimum was the wrong statistic here specifically.</b> Everywhere else in
        /// this class a minimum is right because the quantity can only be inflated — a wait is
        /// the constant plus something non-negative. A GAP is not that quantity. One arrival
        /// late and the next on time shortens the gap between them by the whole of the first
        /// arrival's delay, so the gap distribution straddles the cadence rather than sitting
        /// above it, and its minimum was biased low by the whole jitter range: <b>50.000 ms
        /// against 66.667, a quarter low</b>, weakening every requirement scaled by it in the
        /// same proportion.
        /// </para>
        /// <para>
        /// <b>The direction is the safety property, and it is asserted rather than assumed.</b>
        /// A cadence that reads low makes <see cref="AckLatencyEstimator.SweptEnough"/>'s span
        /// requirement and <c>OccupiedBuckets</c>' bucket width smaller, so the guard admits
        /// data it should refuse: LENIENT, never strict, and therefore unable to produce an
        /// over-lead on its own. That assertion is unchanged and outlives the statistic.
        /// </para>
        /// <para>
        /// <b>What the reading is now.</b> Adjacent gaps telescope, so the mean of
        /// <see cref="AckLatencyEstimator.IntervalWindowMax"/> consecutive single-interval gaps
        /// carries only <c>1/K</c> of the arrival jitter, and the observed jitter spread over
        /// the same window is subtracted to keep the result on the lenient side of an otherwise
        /// unbiased estimate. The residue is exactly that subtraction: the delay pattern here
        /// spans <c>4·jitter/3</c> between its widest and narrowest gap, so the reading is
        /// <c>cadence − (4·jitter/3)/8</c>, and it is written that way below rather than as a
        /// number, so that changing the window length moves the expectation with it instead of
        /// falsifying a constant somebody would then edit.
        /// </para>
        /// <para>
        /// <b>Two statistics were rejected, both by measurement.</b> The smallest mean of two
        /// ADJACENT gaps telescopes correctly and provably never reads below the old minimum;
        /// at one snapshot in three lost it reads 99.999 ms against 66.667 — 50% HIGH — because
        /// no adjacent pair is then free of a drop. A raw low PERCENTILE of the gaps fails the
        /// other way on this very fixture, which
        /// <see cref="TheRejectedPercentileOfGapsWouldReadStrict"/> pins: three gaps sit above
        /// the cadence for every one below, so the tenth percentile is still the minimum and
        /// the twenty-fifth reads 8.3% HIGH. <b>A percentile of gaps is not the same move as a
        /// percentile of waits</b>, because a wait cannot fall below the constant and a gap can.
        /// </para>
        /// </remarks>
        [Test]
        public void TheSnapshotIntervalReadsLowByTheArrivalJitter()
        {
            const double Cadence = 4.0 / BaseHz;       // 66.7 ms, the 15 Hz snapshot rate
            const double Jitter = 1.0 / BaseHz;        // one 60 fps frame

            var e = DriveArrivals(Cadence, Jitter, count: 120);

            Assert.That(e.AckIntervalSeconds, Is.LessThanOrEqualTo(Cadence + 1e-9),
                "THE SAFETY PROPERTY. The estimate must stay on the lenient side of the true "
                + "cadence: reading it HIGH would tighten every requirement scaled by it and "
                + "could produce an over-lead. Any future fix must keep this assertion.");

            // The widest gap this delay pattern produces less the narrowest, which is what the
            // statistic subtracts a window's share of.
            const double GapSpread = 4.0 * Jitter / 3.0;

            Assert.That(
                e.AckIntervalSeconds,
                Is.EqualTo(Cadence - GapSpread / AckLatencyEstimator.IntervalWindowMax)
                    .Within(1e-9),
                "THE MEASUREMENT, replacing the one this test was written to pin. The reading "
                + "is no longer the cadence less the WHOLE jitter range (50.000 ms, 25% low) "
                + "but the cadence less the jitter over the averaging window: 63.889 ms "
                + "against 66.667, 4.2% low. The residual is the leniency subtraction, not "
                + "the estimator failing to telescope — the window mean itself is exact here.");

            Assert.That(e.AckIntervalWindow, Is.EqualTo(AckLatencyEstimator.IntervalWindowMax),
                "and it used a full window: with no loss there is a drop-free run the whole "
                + "length of the ring, so nothing here rests on the fallback.");
        }

        /// <summary>
        /// The case that disqualified the obvious fix, kept as the standing requirement any
        /// replacement statistic must meet: on a jitter-free cadence the estimate is exactly
        /// the cadence, with or without dropped snapshots.
        /// </summary>
        /// <remarks>
        /// A dropped snapshot doubles one gap. The present minimum ignores it, which is the
        /// one thing the present minimum gets right. The adjacent-pair mean does not: at
        /// <c>dropEvery = 3</c> every pair contains a drop and the reading goes 50% HIGH. This
        /// fixture exists so that the next attempt at the jitter bias is measured against loss
        /// before it is believed, rather than after.
        /// </remarks>
        [TestCase(0)]
        [TestCase(5)]
        [TestCase(3)]
        public void AnIdealCadenceIsMeasuredExactly_DropsOrNot(int dropEvery)
        {
            const double Cadence = 4.0 / BaseHz;

            var e = DriveArrivals(Cadence, jitter: 0.0, count: 120, dropEvery: dropEvery);

            Assert.That(e.AckIntervalSeconds, Is.EqualTo(Cadence).Within(1e-9),
                "with no jitter there is nothing to correct, so the reading must not move — "
                + "including when one snapshot in " + dropEvery + " is lost.");
        }

        /// <summary>
        /// Jitter and loss TOGETHER, which neither pinned case covered and which is where the
        /// previous attempt was actually wrong: the reading stays lenient at every loss rate,
        /// and degrades to the old minimum rather than past it when no window can be formed.
        /// </summary>
        /// <remarks>
        /// The window needs a run of consecutive delivered snapshots. At one in five lost the
        /// longest run is three, so the reading averages three gaps and lands 13.9% low; at one
        /// in three lost the arrivals alternate and there is no run at all, so it falls back to
        /// the minimum and reads exactly what it read before this change — 25% low, and SAID to
        /// be, by <see cref="AckLatencyEstimator.AckIntervalWindow"/>. <b>A fallback is a second
        /// claim about the same quantity; it is reported rather than taken silently.</b>
        /// </remarks>
        [TestCase(0, 8, -4.2)]
        [TestCase(5, 3, -13.9)]
        [TestCase(3, 1, -25.0)]
        public void TheIntervalStaysLenientWhenJitterAndLossArriveTogether(
            int dropEvery, int expectedWindow, double expectedErrorPercent)
        {
            const double Cadence = 4.0 / BaseHz;
            const double Jitter = 1.0 / BaseHz;

            var e = DriveArrivals(Cadence, Jitter, count: 120, dropEvery: dropEvery);

            Assert.That(e.AckIntervalSeconds, Is.LessThanOrEqualTo(Cadence + 1e-9),
                "THE SAFETY PROPERTY, under loss. The attempt this replaces failed exactly "
                + "here: its adjacent-pair mean read 50% HIGH at one snapshot in three.");

            Assert.That(e.AckIntervalWindow, Is.EqualTo(expectedWindow),
                "the window length is the reading's own account of how much evidence it had — "
                + "1 means no two snapshots in a row survived and the reading is the bare "
                + "minimum this change was made to stop relying on.");

            Assert.That(
                100.0 * (e.AckIntervalSeconds - Cadence) / Cadence,
                Is.EqualTo(expectedErrorPercent).Within(0.05),
                "measured, not derived: the error the window length buys at this loss rate.");
        }

        /// <summary>
        /// The fallback keeps a count, because the window length alone is instantaneous state
        /// and a session that fell back for thirty seconds and then recovered leaves no trace
        /// in it.
        /// </summary>
        /// <remarks>
        /// The same shape as <c>AckAheadOfSend</c> before it was deliberately induced: a
        /// reading nobody has watched act is indistinguishable from one that cannot. A handful
        /// at the start of any session is normal — a window cannot be formed before there are
        /// gaps to form it from — so the count is only evidence when it keeps climbing, and
        /// both halves are asserted here rather than only the interesting one.
        /// </remarks>
        [Test]
        public void TheFallbackToTheMinimumIsCountedAndNotOnlyReportedInstantaneously()
        {
            const double Cadence = 4.0 / BaseHz;
            const double Jitter = 1.0 / BaseHz;

            // One in three lost: the arrivals alternate, no two in a row, so EVERY
            // acknowledgement past the first reads the fallback.
            var always = DriveArrivals(Cadence, Jitter, count: 120, dropEvery: 3);

            Assert.That(always.AckIntervalWindow, Is.EqualTo(1));
            Assert.That(always.AckIntervalFallbacks, Is.GreaterThan(50),
                "a link that never delivers two snapshots in a row never leaves the fallback, "
                + "and the count is the only place that is visible after the fact.");

            // A clean link forms a window as soon as it has two gaps, so the count stops at
            // the one acknowledgement that had only a single gap to work with.
            var clean = DriveArrivals(Cadence, Jitter, count: 120);

            Assert.That(clean.AckIntervalWindow, Is.EqualTo(AckLatencyEstimator.IntervalWindowMax));
            Assert.That(clean.AckIntervalFallbacks, Is.EqualTo(1),
                "the start of a session is not a defect: the first gap cannot be averaged with "
                + "anything. A count that STAYS at one is the healthy reading, which is what "
                + "makes a climbing one worth looking at.");

            Assert.That(new AckLatencyEstimator().AckIntervalFallbacks, Is.Zero);

            clean.Reset();
            Assert.That(clean.AckIntervalFallbacks, Is.Zero,
                "and it describes one connection, like everything else here.");
        }

        /// <summary>
        /// The candidate the record recommended, falsified on the fixture it was recommended
        /// for: a raw low percentile of the GAPS reads STRICT, which is the one direction this
        /// term may not be wrong in.
        /// </summary>
        /// <remarks>
        /// <para>
        /// "The same minimum-to-percentile move this class has already made twice" is the note
        /// that was left for whoever fixed this, and it does not transfer. <b>A percentile is
        /// right for the observations because a wait is the constant plus something
        /// non-negative, so the distribution sits ABOVE the quantity and a low percentile
        /// approaches it from the safe side. A gap straddles the cadence instead</b>, and this
        /// delay pattern is not symmetric about it: three gaps of <c>cadence + jitter/3</c> for
        /// every one of <c>cadence − jitter</c>. So the tenth percentile is still the minimum
        /// and buys nothing, and every percentile above the twenty-fifth is above the cadence.
        /// </para>
        /// <para>
        /// Computed here from the same arrivals rather than taken on trust, so that the reason
        /// the shipped statistic is not a percentile is a measurement in the suite rather than
        /// a sentence in a changelog.
        /// </para>
        /// </remarks>
        [Test]
        public void TheRejectedPercentileOfGapsWouldReadStrict()
        {
            const double Cadence = 4.0 / BaseHz;
            const double Jitter = 1.0 / BaseHz;

            var gaps = new System.Collections.Generic.List<double>();
            double previous = double.NaN;
            for (var i = 0; i < 120; i++)
            {
                double at = ClockOffset + i * Cadence + Jitter * (i % 4) / 3.0;
                if (i > 0) gaps.Add(at - previous);
                previous = at;
            }

            gaps.Sort();
            // Nearest rank, the same index rule Quantile() uses.
            double q10 = gaps[(int)(0.10 * gaps.Count)];
            double q25 = gaps[(int)(0.25 * gaps.Count)];

            Assert.That(q10, Is.EqualTo(Cadence - Jitter).Within(1e-9),
                "the tenth percentile of the gaps IS the minimum on this pattern — the "
                + "recommended move buys nothing at the quantile low enough to be safe.");

            Assert.That(q25, Is.GreaterThan(Cadence),
                "and the next one up is already STRICT: 72.222 ms against a 66.667 cadence, "
                + "8.3% high. This is why the shipped statistic averages a window instead.");
        }

        /// <summary>
        /// The limit of the whole approach, stated as a test rather than left to be
        /// rediscovered: when periodic loss is phase-locked to the arrival jitter so that NO
        /// observed gap spans exactly one cadence interval, both this statistic and the minimum
        /// it replaced read about twice the cadence.
        /// </summary>
        /// <remarks>
        /// At one snapshot in two, with the dropped arrival always the on-time one, every
        /// surviving gap covers two intervals. Nothing in this class can tell that from a
        /// genuinely halved snapshot rate — the arrivals are identical — so this is a property
        /// of the LINK, not of the statistic, and it is the forbidden direction for both.
        /// It is pinned so that the leniency property is understood as conditional on the link
        /// delivering two snapshots in a row somewhere in the ring, which is the condition the
        /// old minimum silently depended on too.
        /// </remarks>
        [Test]
        public void PhaseLockedLossDefeatsThisStatisticAndTheOneItReplaced()
        {
            const double Cadence = 4.0 / BaseHz;
            const double Jitter = 1.0 / BaseHz;

            var e = DriveArrivals(Cadence, Jitter, count: 120, dropEvery: 2);

            Assert.That(e.AckIntervalSeconds, Is.GreaterThan(1.8 * Cadence),
                "NOT A PASSING GRADE — a pinned failure. Every gap spans two intervals, so the "
                + "reading is about two cadences (130.6 ms) where the minimum read 122.2. Both "
                + "are strict, and the difference between them is not the point: the point is "
                + "that the quantity is absent from the arrivals and no statistic over them "
                + "recovers it.");

            // The milder face of the same lock, and the one a real link could plausibly meet:
            // at one in four the dropped arrival is always the maximally late one, so every
            // surviving gap is cadence + jitter/3 and there is no short gap anywhere. Both
            // statistics read 72.222 ms -- IDENTICALLY, because with no gap below the cadence
            // a window mean and a minimum are the same number.
            var mild = DriveArrivals(Cadence, Jitter, count: 120, dropEvery: 4);

            Assert.That(mild.AckIntervalSeconds, Is.EqualTo(Cadence + Jitter / 3.0).Within(1e-9),
                "8.3% strict, and exactly what the minimum it replaced reads on the same "
                + "arrivals. Pinned so that a future reader meeting this number does not "
                + "attribute it to the windowing.");
        }

        /// <summary>
        /// The leniency property across jitter shapes, jitter ranges and loss rates rather than
        /// on one pattern — asserted CONDITIONALLY, on the only condition under which it can
        /// hold: that the link delivered a gap spanning one interval at all.
        /// </summary>
        /// <remarks>
        /// The larger sweep this is the runnable part of covered 43 200 arms (four jitter
        /// ranges, six jitter shapes, six loss rates, 300 seeds). In none of them did the
        /// windowed reading cross the cadence where the old minimum had not, and in none was it
        /// FURTHER from the cadence than the old minimum; it was closer in 51.5% and further in
        /// none. <b>Without the jitter-spread subtraction the same sweep reads up to 7.1%
        /// high</b>, which is what makes that term load-bearing rather than decorative.
        /// </remarks>
        [Test]
        public void TheIntervalIsNeverStricterThanTheMinimumItReplaced()
        {
            const double Cadence = 4.0 / BaseHz;

            int arms = 0, closer = 0;
            foreach (double jitter in new[] { Cadence / 8.0, Cadence / 4.0, Cadence / 2.0 })
            foreach (double loss in new[] { 0.0, 0.1, 0.2, 0.33 })
            foreach (int shape in new[] { 0, 1, 2 })
            for (var seed = 0; seed < 12; seed++)
            {
                var rng = new Random(seed * 31 + shape);
                var e = new AckLatencyEstimator();
                double previous = double.NaN, minGap = double.MaxValue;
                long tick = 0;

                for (var i = 0; i < 300; i++)
                {
                    double delay = shape switch
                    {
                        0 => jitter * rng.NextDouble(),                  // uniform
                        1 => rng.NextDouble() < 0.05 ? jitter : 0.0,     // rare late arrival
                        _ => rng.NextDouble() < 0.5 ? jitter : 0.0,      // bimodal
                    };
                    double at = ClockOffset + i * Cadence + delay;
                    tick++;
                    e.RecordSent(tick, at - 0.005);
                    if (i > 0 && rng.NextDouble() < loss) continue;
                    e.RecordAck(tick, at, BaseHz);
                    if (!double.IsNaN(previous) && at - previous < minGap) minGap = at - previous;
                    previous = at;
                }

                arms++;
                string arm = $"jitter={jitter:F4} loss={loss} shape={shape} seed={seed}";

                if (minGap <= Cadence + 1e-9)
                {
                    Assert.That(e.AckIntervalSeconds, Is.LessThanOrEqualTo(Cadence + 1e-9),
                        "THE SAFETY PROPERTY, on an arm where the minimum was lenient and the "
                        + "reading therefore has no excuse not to be: " + arm);
                }

                Assert.That(
                    Math.Abs(e.AckIntervalSeconds - Cadence),
                    Is.LessThanOrEqualTo(Math.Abs(minGap - Cadence) + 1e-9),
                    "and never further from the cadence than the statistic it replaced: " + arm);

                if (Math.Abs(e.AckIntervalSeconds - Cadence) < Math.Abs(minGap - Cadence)) closer++;
            }

            Assert.That(closer, Is.GreaterThan(arms / 2),
                "a change that is never worse but also never better is not a fix; it must be "
                + "closer to the true cadence on most arms. Closer on " + closer + " of "
                + arms + ".");
        }
    }
}
