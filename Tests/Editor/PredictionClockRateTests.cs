using System;
using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Shared.GameLogic.Components;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Pins that the prediction clock runs on the SERVER's timebase, not the client
    /// machine's, once the rate difference between the two has been measured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not covered by the steering tests.</b>
    /// <see cref="LocalMovePredictor.SteerToServerTick"/> was a proportional controller with
    /// no integral term, so it removed a phase error and merely <i>drooped</i> against a rate
    /// error: it settled at a standing offset of <c>drift / (gain * snapshotHz)</c> instead
    /// of closing it. It now has a clamped integral term that closes it too; the fitted rate
    /// remains the faster and preferred cure. Every steering test drives it with matched clocks, where that term is
    /// zero and the defect cannot appear.
    /// </para>
    /// <para>
    /// The standing offset is not a diagnostic. <c>Reconcile</c>'s history path compares at
    /// the snapshot's own tick NUMBER, so an offset of n ticks makes the two sides label
    /// different moments with the same number, and the whole of it is reported as position —
    /// a correction at every start and every stop, sized by the offset. Measured live: a
    /// client reading the wire at 55.0 Hz against an advertised 60 (ratio 1.091, the
    /// Windows-performance-counter-against-Linux case in
    /// <see cref="SnapshotStalenessEstimator.MinimumSkew"/>'s remarks) sat at a clock error
    /// of 3 and corrected 2 to 3 steps at every transition, with the lead correct and every
    /// other counter clean.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class PredictionClockRateTests
    {
        private const int BaseHz = 60;
        private const int SnapshotEvery = 4;                      // 15 Hz snapshots
        private const double SnapshotHz = BaseHz / (double)SnapshotEvery;

        /// <summary>The gain in <see cref="LocalMovePredictor.SteerToServerTick"/>.</summary>
        private const double SteerGain = 0.1;

        private static LocalMovePredictor NewPredictor() =>
            new LocalMovePredictor(new PredictionSettings(BaseHz, 5f, MapBounds.Default));

        /// <summary>
        /// Runs a session where the client's clock ticks <paramref name="clockRatio"/> times
        /// faster than the server's, and reports the settled steering error.
        /// </summary>
        private static double SettledTickError(double clockRatio, bool feedTheRateForward)
        {
            var predictor = NewPredictor();
            var staleness = new SnapshotStalenessEstimator();

            double real = 0, nextSnapshot = 0, nextFrame = 0;
            long serverTick = 1000;
            var seeded = false;
            double total = 0;
            var counted = 0;

            while (real < 30.0)
            {
                real = Math.Min(nextSnapshot, nextFrame);

                if (real >= nextFrame)
                {
                    const double frame = 1.0 / 900.0;
                    nextFrame = real + frame;
                    predictor.Advance((float)(frame * clockRatio));   // the client's own clock
                }

                if (real < nextSnapshot) continue;

                serverTick += SnapshotEvery;
                nextSnapshot = real + SnapshotEvery / (double)BaseHz;

                staleness.Sample(serverTick, real * clockRatio, BaseHz);

                if (!seeded) { predictor.SeedBaseTick(serverTick); seeded = true; continue; }

                if (feedTheRateForward && staleness.IsUsable)
                {
                    predictor.SetClockRateScale((float)(1.0 / (1.0 + staleness.SkewPpm / 1e6)));
                }

                predictor.SteerToServerTick(serverTick, 0);

                // Second half only: the first is the estimator warming up, and a reading
                // taken across a transient is a reading of the transient.
                if (real > 15.0) { total += predictor.TickError; counted++; }
            }

            Assert.That(counted, Is.GreaterThan(0), "precondition: the run produced no samples");
            return total / counted;
        }

        [TestCase(1.02)]
        [TestCase(1.05)]
        [TestCase(1.09)]
        [TestCase(1.103)]
        public void WithoutTheRateTheIntegralTermRemovesTheDroopToo(double clockRatio)
        {
            double drift = (clockRatio - 1.0) * BaseHz;              // ticks/s the clock gains
            double droop = drift / (SteerGain * SnapshotHz);

            // This case used to pin that very droop -- 0.8 to 4.1 ticks across these ratios --
            // as the textbook behaviour of a proportional-only loop. The steering now has an
            // integral term (LocalMovePredictor.SteerToServerTick), so with NO rate fed forward
            // the standing error must still settle at zero, inside the integrator's clamp. The
            // droop is kept as the number this is measured against.
            Assert.That(SettledTickError(clockRatio, feedTheRateForward: false),
                Is.EqualTo(0.0).Within(0.5),
                $"a proportional-only loop would sit {droop:F2} ticks off at this ratio; the integral " +
                "term exists to remove exactly that, for the sessions that never corroborate a rate " +
                "to feed forward.");
        }

        [TestCase(1.02)]
        [TestCase(1.05)]
        [TestCase(1.09)]
        [TestCase(1.103)]
        [TestCase(0.92)]
        public void TheFittedRateRemovesTheStandingErrorEntirely(double clockRatio)
        {
            Assert.That(SettledTickError(clockRatio, feedTheRateForward: true),
                Is.EqualTo(0.0).Within(0.5),
                "with the measured rate handed to the clock there is no drift left for the " +
                "phase loop to droop against, so the steering error must settle at zero — " +
                "in BOTH directions, which is why a slow client clock is a case here too.");
        }

        [Test]
        public void MatchedClocksAreUnaffected()
        {
            Assert.That(SettledTickError(1.0, feedTheRateForward: false), Is.EqualTo(0.0).Within(0.5));
            Assert.That(SettledTickError(1.0, feedTheRateForward: true), Is.EqualTo(0.0).Within(0.5));
        }

        [Test]
        public void ARateNoEstimatorWouldHaveFittedIsRefusedRatherThanClamped()
        {
            var predictor = NewPredictor();
            Assert.That(predictor.ClockRateScale, Is.EqualTo(1f), "precondition: no correction yet");

            // 4x is the shape of a client predicting at 60 against a 15 Hz server — the
            // failure the estimator's skew bounds exist to catch, not a clock difference.
            predictor.SetClockRateScale(4f);
            predictor.SetClockRateScale(0.2f);

            Assert.That(predictor.ClockRateScale, Is.EqualTo(1f),
                "a scale outside the estimator's own skew bounds is a bad measurement, and " +
                "steering the simulation onto a CLAMPED bad measurement is worse than not " +
                "steering onto it — the clamped value is still wrong and now looks plausible.");
            Assert.That(predictor.RefusedClockRateScales, Is.EqualTo(2),
                "and the refusal must be counted, or it is indistinguishable from never " +
                "having been offered one.");
        }

        [Test]
        public void NonsenseIsIgnoredWithoutDisturbingAGoodScale()
        {
            var predictor = NewPredictor();
            predictor.SetClockRateScale(0.9f);

            predictor.SetClockRateScale(float.NaN);
            predictor.SetClockRateScale(0f);
            predictor.SetClockRateScale(float.NegativeInfinity);

            Assert.That(predictor.ClockRateScale, Is.EqualTo(0.9f).Within(1e-6f),
                "an unusable value must leave the last good one standing rather than reset " +
                "the clock to the client's own rate.");
        }
    }
}
