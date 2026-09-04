using NUnit.Framework;
using Cuvara.Netcode.Prediction;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Covers recovering the server's base tick rate from snapshot arrivals.
    /// </summary>
    /// <remarks>
    /// The case that matters is the multi-rate one: 60 Hz simulated, 15 Hz sent. Successive
    /// snapshots are then four base ticks apart, and the arithmetic must still yield 60 —
    /// a client that measured 15 here would predict at a quarter rate and be wrong on every
    /// input, which is the defect this exists to detect.
    /// </remarks>
    [TestFixture]
    public sealed class TickRateEstimatorTests
    {
        /// <summary>Feeds a run of snapshots at <paramref name="sendHz"/> from a simulation at <paramref name="simHz"/>.</summary>
        private static TickRateEstimator Run(int simHz, int sendHz, double seconds)
        {
            var e = new TickRateEstimator();
            int ticksPerSnapshot = simHz / sendHz;
            int snapshots = (int)(seconds * sendHz);

            for (var i = 0; i <= snapshots; i++)
            {
                e.Sample(i * (long)ticksPerSnapshot, i / (double)sendHz);
            }

            return e;
        }

        [Test]
        public void MeasuresTheBaseRateNotTheSnapshotRate()
        {
            var e = Run(simHz: 60, sendHz: 15, seconds: 2.0);

            Assert.That(e.HasEstimate, Is.True);
            Assert.That(e.EstimatedHz, Is.EqualTo(60f).Within(0.5f),
                "snapshots arrive at 15 Hz but carry BASE ticks four apart, so the rate " +
                "recovered must be 60 — measuring 15 here is the whole failure");
        }

        [Test]
        public void MeasuresAUniformRate()
        {
            var e = Run(simHz: 15, sendHz: 15, seconds: 2.0);
            Assert.That(e.EstimatedHz, Is.EqualTo(15f).Within(0.5f));
        }

        [Test]
        public void OffersNoEstimateBeforeEnoughHasBeenSeen()
        {
            var e = new TickRateEstimator();
            e.Sample(0, 0.0);
            e.Sample(4, 0.066);

            Assert.That(e.HasEstimate, Is.False,
                "two samples over 66 ms is scheduling noise wearing a number's clothes");
            Assert.That(e.EstimatedHz, Is.Zero, "zero means not measured, never 'stopped'");
        }

        [Test]
        public void IgnoresSnapshotsWhoseTickDoesNotAdvance()
        {
            var e = Run(simHz: 60, sendHz: 15, seconds: 2.0);
            float before = e.EstimatedHz;
            int samples = e.Samples;

            e.Sample(0, 99.0);          // a stale or duplicate delivery

            Assert.That(e.Samples, Is.EqualTo(samples), "carries no timing information");
            Assert.That(e.EstimatedHz, Is.EqualTo(before));
        }

        // ── The cross-check ──

        [Test]
        public void AgreesWithAMatchingAdvertisedRate()
        {
            var e = Run(simHz: 60, sendHz: 15, seconds: 2.0);
            Assert.That(e.Disagrees(60), Is.False);
        }

        [Test]
        public void DisagreesWithTheRateThatCausedTheDefect()
        {
            var e = Run(simHz: 60, sendHz: 15, seconds: 2.0);

            Assert.That(e.Disagrees(15), Is.True,
                "a server simulating at 60 while something claims 15 is the exact " +
                "mismatch that predicts 4x the distance per input and smooths rather " +
                "than snaps");
        }

        [Test]
        public void ReportsNoDisagreementWhenThereIsNothingToCompare()
        {
            var fresh = new TickRateEstimator();
            Assert.That(fresh.Disagrees(60), Is.False, "no estimate yet is not evidence");

            var measured = Run(simHz: 60, sendHz: 15, seconds: 2.0);
            Assert.That(measured.Disagrees(0), Is.False, "nothing advertised is not evidence");
        }

        [Test]
        public void SteadyCadenceIsAdoptedAsTheHoldWindow()
        {
            var e = Run(simHz: 60, sendHz: 15, seconds: 2.0);

            Assert.That(e.SnapshotTickGap, Is.EqualTo(4),
                "snapshots one world tick apart carry base ticks four apart at 60/15, and " +
                "that gap IS the server's hold window");
        }

        [Test]
        public void AJoinKeyframeDoesNotShrinkTheHoldWindow()
        {
            // The first snapshot after joining is emitted when the join is handled, not on
            // a world tick boundary, so the gap to the next scheduled one is the phase --
            // here 1 base tick instead of 4. A running minimum that adopted it would pin
            // the hold window at 1 for the whole session, the predictor would stop
            // integrating the held direction immediately, and the avatar would finish each
            // step early and hold still for the rest of the tick.
            var e = new TickRateEstimator();

            e.Sample(100, 0.000);   // join keyframe, off cadence
            e.Sample(101, 0.005);   // next scheduled snapshot, one tick later
            e.Sample(105, 0.071);
            e.Sample(109, 0.138);
            e.Sample(113, 0.205);

            Assert.That(e.SnapshotTickGap, Is.EqualTo(4),
                "one narrow pair is the join phase, not the cadence. It must not become " +
                "the hold window: understating the window makes the client predict LESS " +
                "than the server, which stays under SmoothingThreshold and so smooths " +
                "instead of snapping — no counter shows it and the avatar freezes " +
                "part-way through every tick.");
        }

        [Test]
        public void ARepeatedNarrowerGapIsAdopted()
        {
            // The guard confirms, it does not veto. A server that genuinely sends every
            // two base ticks must still be believed, or the client would predict motion
            // the server has already stopped -- the failure the minimum exists to avoid.
            var e = new TickRateEstimator();

            e.Sample(100, 0.000);
            e.Sample(104, 0.067);
            e.Sample(106, 0.100);
            e.Sample(108, 0.133);

            Assert.That(e.SnapshotTickGap, Is.EqualTo(2),
                "seen twice, so it is the cadence and not an artefact");
        }

        [Test]
        public void AlternatingGapsAreEachConfirmedIndependently()
        {
            // The previous single-candidate design tracked one gap at a time. Alternating
            // gaps (3, 4, 3, 4) reset the counter on every switch, so neither ever reached
            // the confirmation threshold and the hold window stayed at 0. With per-value
            // counting, each gap accumulates its own count independently.
            var e = new TickRateEstimator();

            e.Sample(100, 0.000);
            e.Sample(103, 0.050);   // gap 3 (first)
            e.Sample(107, 0.117);   // gap 4 (first)
            e.Sample(110, 0.167);   // gap 3 (second — confirmed)
            e.Sample(114, 0.233);   // gap 4 (second — confirmed, but 3 already won)

            Assert.That(e.SnapshotTickGap, Is.EqualTo(3),
                "alternating 3/4 gaps: 3 is confirmed on its second sighting and adopted " +
                "as the minimum, not blocked by 4 resetting a shared counter");
        }

        [Test]
        public void ADroppedSnapshotDoesNotWidenTheHoldWindow()
        {
            // Drops only ever widen a gap, and the minimum is what makes them harmless.
            // Confirming a narrower gap must not have cost that property.
            var e = new TickRateEstimator();

            e.Sample(100, 0.000);
            e.Sample(104, 0.067);
            e.Sample(108, 0.133);
            e.Sample(116, 0.267);   // one snapshot lost: an 8-tick gap
            e.Sample(120, 0.333);

            Assert.That(e.SnapshotTickGap, Is.EqualTo(4), "a loss widens; it never counts");
        }

        [Test]
        public void ResetForgetsEverything()
        {
            var e = Run(simHz: 60, sendHz: 15, seconds: 2.0);
            e.Reset();

            Assert.That(e.HasEstimate, Is.False);
            Assert.That(e.Samples, Is.Zero);
        }
    }
}
