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
        /// The contribution is the floor less the part of the wait's range never sampled, so it
        /// is bounded by the evidence rather than by a rounding rule.
        /// </summary>
        [TestCase(0.0)]
        [TestCase(0.0083)]     // half a base tick
        [TestCase(0.0167)]     // one base tick
        [TestCase(0.0333)]     // two base ticks
        public void TheContributionIsTheFloorLessItsOwnUncertainty(double uplinkPlusAge)
        {
            var e = Drive(uplinkPlusAge, seconds: 60.0, snapshotPeriod: SnapshotPeriod * 1.03);

            Assert.That(e.HasEstimate, Is.True, "precondition");
            Assert.That(e.ConservativeFloorTicks, Is.LessThanOrEqualTo(e.FloorTicks),
                "biased low, never high: an over-lead is the defect this exists to remove, "
                + "arriving from the other side.");

            double expected = Math.Max(0.0, (e.FloorSeconds - e.UnsweptSeconds) * BaseHz);
            Assert.That(e.ConservativeFloorTicks, Is.EqualTo((float)expected).Within(1e-4),
                "the bias is the measured unswept remainder and nothing else — no tuned "
                + "fraction, no rounding rule, so it shrinks to zero as the sweep completes.");

            Assert.That(e.ConservativeFloorTicks,
                Is.LessThanOrEqualTo((float)(uplinkPlusAge * BaseHz) + 0.5f),
                "and it must not exceed the true constant by more than the residual "
                + "uncertainty, or it is over-leading on evidence it does not have.");
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
        /// PINS A KNOWN, DELIBERATELY UNFIXED DEFECT, with the measurement that sizes it.
        /// <c>AckIntervalSeconds</c> is the cadence every sweep requirement is scaled by, and
        /// taking it as the smallest gap between arrivals reads the cadence LESS the full
        /// arrival jitter — here 50.0 ms against a true 66.7 ms, a quarter low.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why a minimum is the wrong statistic here specifically.</b> Everywhere else in
        /// this class a minimum is right because the quantity can only be inflated — a wait is
        /// the constant plus something non-negative. A GAP is not that quantity. One arrival
        /// late and the next on time shortens the gap between them by the whole of the first
        /// arrival's delay, so the gap distribution straddles the cadence rather than sitting
        /// above it, and its minimum is biased low by the jitter range rather than converging
        /// on the cadence.
        /// </para>
        /// <para>
        /// <b>The direction, which is what bounds the risk and why this can wait.</b> A
        /// cadence that reads low makes <see cref="AckLatencyEstimator.SweptEnough"/>'s span
        /// requirement and <c>OccupiedBuckets</c>' bucket width smaller, so the guard admits
        /// data it should refuse. It is LENIENT, never strict, and therefore cannot produce an
        /// over-lead on its own — asserted below rather than assumed, because that assertion
        /// is the whole reason this is a recorded defect and not an incident.
        /// </para>
        /// <para>
        /// <b>Why it is not fixed here.</b> The obvious correction — take the smallest mean of
        /// two ADJACENT gaps, which telescope so that a single arrival's delay cancels exactly
        /// — was implemented and measured rather than reasoned about. It fixes this case, and
        /// on the drop case below it reads <b>99.999 ms against a true 66.667 ms</b>, because
        /// at one snapshot in three lost no adjacent pair of gaps is free of a drop and every
        /// pair mean is inflated by the missing arrival. That is 50% HIGH: it converts a
        /// lenient guard into a strict one, which is the single direction this term is not
        /// allowed to be wrong in. A correct fix needs a robust statistic over a ring of
        /// gaps — the same minimum-to-percentile move this class has already made twice — and
        /// that is a larger change than this was recorded as, deserving its own measurement
        /// rather than being folded in behind one.
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

            Assert.That(e.AckIntervalSeconds, Is.EqualTo(Cadence - Jitter).Within(1e-6),
                "THE MEASUREMENT. The reading is the cadence less the WHOLE jitter range, not "
                + "part of it, because the minimum finds the one adjacent pair where a "
                + "maximally late arrival is followed by an on-time one. 50.0 ms against a "
                + "66.7 ms cadence — 25% low — and every requirement scaled by it is weakened "
                + "in the same proportion. Pinned so that a change to the statistic has to "
                + "come here and say what it did.");
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
    }
}
