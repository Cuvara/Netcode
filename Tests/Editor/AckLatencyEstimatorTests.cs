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
            for (var i = 1; i <= AckLatencyEstimator.MinimumSamples - 1; i++)
            {
                e.RecordSent(i, now);
                now += SnapshotPeriod;
                e.RecordAck(i, now + (i % 4) * SnapshotPeriod * 0.3, BaseHz);

                Assert.That(e.HasEstimate, Is.False,
                    "this number ADDS lead, so a floor from too little evidence steers the " +
                    "clock PAST the server — the defect it exists to remove, arriving from " +
                    "the other side. There is deliberately no provisional reading.");
                Assert.That(e.FloorTicks, Is.EqualTo(0f), "and it must read zero, not a guess");
            }

            for (var i = AckLatencyEstimator.MinimumSamples; i <= AckLatencyEstimator.MinimumSamples + 2; i++)
            {
                e.RecordSent(i, now);
                now += SnapshotPeriod;
                e.RecordAck(i, now + (i % 4) * SnapshotPeriod * 0.3, BaseHz);
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
    }
}
