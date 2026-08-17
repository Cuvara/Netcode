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
        public void ResetForgetsEverything()
        {
            var e = Run(simHz: 60, sendHz: 15, seconds: 2.0);
            e.Reset();

            Assert.That(e.HasEstimate, Is.False);
            Assert.That(e.Samples, Is.Zero);
        }
    }
}
