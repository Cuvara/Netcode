using NUnit.Framework;
using Cuvara.Netcode.Prediction;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Pins the measurement the prediction clock's steering target is built from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> The target was a formula — one snapshot interval plus the
    /// rounded half round trip — and a formula in whole ticks cannot express an age that is
    /// fractional and set by a client's join phase. Measured with two clients on one machine
    /// against one server, an unlucky phase left one of them with a constant
    /// <b>0.3333-unit, 4.00-step</b> correction on every snapshot while the other sat at
    /// 0.0033; started two seconds apart instead of six, both sat at 0.003–0.017. Same
    /// build, same server, same map. A defect that only shows at some join phases is one no
    /// fixed number can close, which is why this is measured.
    /// </para>
    /// <para>
    /// Every case here drives the estimator with a synthetic clock, so a reading is a
    /// property of the arithmetic and not of the machine the tests ran on.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class SnapshotStalenessEstimatorTests
    {
        private const float BaseHz = 60f;
        private const int SnapshotEvery = 4;          // 15 Hz snapshots against a 60 Hz base
        private const double Interval = SnapshotEvery / (double)BaseHz;

        /// <summary>An offset between the two clocks, to prove nothing depends on them sharing one.</summary>
        private const double ClockOffset = 12345.678;

        /// <summary>
        /// Drive the estimator until it has a line: two epochs' worth of anchors and a
        /// baseline long enough to fit a rate over.
        /// </summary>
        private static SnapshotStalenessEstimator Warm(out long tick, out double now, double delay = 0.010)
        {
            var e = new SnapshotStalenessEstimator();
            tick = 1000;
            now = ClockOffset + tick / (double)BaseHz + delay;

            double until = now + SnapshotStalenessEstimator.MinimumBaselineSeconds
                               + SnapshotStalenessEstimator.EpochSeconds * 2;

            while (now < until)
            {
                e.Sample(tick, now, BaseHz);
                tick += SnapshotEvery;
                now += Interval;
            }

            Assert.That(e.IsUsable, Is.True, "precondition: the estimator must have fitted a line");
            return e;
        }

        [Test]
        public void NothingIsOfferedUntilTheBaselineIsLongEnoughToFitARateOver()
        {
            var e = new SnapshotStalenessEstimator();
            long tick = 1000;
            double now = ClockOffset + tick / (double)BaseHz;

            double until = now + SnapshotStalenessEstimator.MinimumBaselineSeconds;

            while (now < until)
            {
                e.Sample(tick, now, BaseHz);
                tick += SnapshotEvery;
                now += Interval;

                Assert.That(e.IsUsable, Is.False,
                    "a rate is a slope, and a slope over a short baseline is mostly the noise " +
                    "of its two endpoints -- over one second a millisecond of jitter reads as " +
                    "1000 ppm, twenty times a real rate difference. Offering that would steer " +
                    "the clock on the noise.");
            }
        }

        /// <summary>
        /// A link whose delay never varies reads as no staleness above its own floor —
        /// whatever that delay is, and whatever the two clocks' origins are.
        /// </summary>
        /// <remarks>
        /// The one-way delay is inside the floor and cannot be separated from the clock
        /// offset it is mixed with; the caller adds half a measured round trip for it. What
        /// this pins is that a steady link contributes nothing ON TOP, so the reading is the
        /// variable part and only the variable part.
        /// </remarks>
        [TestCase(0.001)]
        [TestCase(0.050)]
        [TestCase(0.400)]
        public void ASteadyLinkReadsAsNothingAboveItsOwnFloor(double delay)
        {
            var e = Warm(out long tick, out double now, delay);

            float staleness = e.Sample(tick, now, BaseHz);

            Assert.That(staleness, Is.EqualTo(0f).Within(0.05f),
                $"a link with a constant {delay * 1000:F0} ms delay reported {staleness:F2} " +
                "ticks of staleness above its own best case. Only the variation is " +
                "measurable here; the constant part is the round trip the caller adds.");
        }

        /// <summary>
        /// A snapshot held back by a slow frame reads as exactly the time it was held.
        /// </summary>
        /// <remarks>
        /// This is the term the formula could not express: the wait for a client frame,
        /// which is set by where that client's loop falls against the server's send cadence
        /// and then holds for the whole session.
        /// </remarks>
        [TestCase(0.5)]
        [TestCase(1.0)]
        [TestCase(2.5)]
        public void ASnapshotHeldForAFrameReadsAsTheTimeItWasHeld(double extraTicks)
        {
            var e = Warm(out long tick, out double now);

            float staleness = e.Sample(tick, now + extraTicks / BaseHz, BaseHz);

            Assert.That(staleness, Is.EqualTo((float)extraTicks).Within(0.1f),
                $"a snapshot acted on {extraTicks:F1} ticks late read as {staleness:F2}. " +
                "The wait for a frame is a real part of how old a snapshot is when the " +
                "client uses it, and it is the part a fixed formula gets wrong.");
        }

        /// <summary>
        /// The floor is not a running minimum: a route that gets permanently slower is
        /// followed, rather than measured forever against a best case it can no longer reach.
        /// </summary>
        /// <remarks>
        /// A running minimum is pinned by the single fastest snapshot of the session. Every
        /// later sample then reads as stale by the whole difference, the steering target
        /// grows to match, and the client sits permanently ahead of the server — the defect
        /// this estimator exists to remove, arriving by another door.
        /// </remarks>
        [Test]
        public void APermanentlySlowerRouteIsFollowedRatherThanMeasuredForever()
        {
            var e = Warm(out long tick, out double now);

            // The route gets 40 ms slower and stays there, for a minute.
            const double worse = 0.040;
            float last = 0f;

            for (var i = 0; i < 15 * 60; i++)
            {
                last = e.Sample(tick, now + worse, BaseHz);
                tick += SnapshotEvery;
                now += Interval;
            }

            float worseTicks = (float)(worse * BaseHz);   // 2.4 ticks

            Assert.That(last, Is.LessThan(worseTicks * 0.5f),
                $"a minute after the route settled 40 ms slower the reading is still " +
                $"{last:F2} ticks, against the {worseTicks:F2} the step was worth. The floor " +
                "is behaving as a running minimum, so the client will steer permanently " +
                "ahead of a server it actually agrees with.");
        }

        /// <summary>
        /// And it recovers slowly enough that ordinary jitter is still measured against the
        /// good case rather than against itself.
        /// </summary>
        [Test]
        public void TheFloorDoesNotChaseJitter()
        {
            var e = Warm(out long tick, out double now);

            // One slow snapshot in every four, for a few seconds.
            for (var i = 0; i < 60; i++)
            {
                double extra = (i % 4 == 0) ? 0.030 : 0.0;
                e.Sample(tick, now + extra, BaseHz);
                tick += SnapshotEvery;
                now += Interval;
            }

            float onTime = e.Sample(tick, now, BaseHz);

            Assert.That(onTime, Is.LessThan(0.5f),
                $"an on-time snapshot read as {onTime:F2} ticks stale after a run of jittery " +
                "ones. The floor has drifted up to meet the jitter, so the baseline is no " +
                "longer the good case and every reading is understated.");
        }

        /// <summary>
        /// A rate difference between the two clocks is <b>measured</b>, not accumulated: the
        /// reading stays flat and the difference shows up in <see cref="SnapshotStalenessEstimator.SkewPpm"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the case the previous design could not pass, and the reason it was
        /// replaced. Fitting an offset alone against a fixed rate cannot see a rate
        /// difference, and a rate difference it cannot see appears as an offset that grows
        /// without bound. Wired to the steering target it made a live client categorically
        /// worse, twice: fed a rate measured off the wire (57.7 Hz for a 60 Hz server) the
        /// reading passed <b>613 ticks</b> with the target following it and snaps at
        /// <b>71 per five-second window</b>; fed the advertised rate it still settled around
        /// <b>205 ticks</b> where two or three was right.
        /// </para>
        /// <para>
        /// Both figures are one defect — a term the model did not have. Solving for it is
        /// what makes this test possible to write, and the parameters run from two ordinary
        /// crystals disagreeing to a tick rate that is simply wrong.
        /// </para>
        /// </remarks>
        [TestCase(1.0005, 500.0)]      // 500 ppm: two crystals
        [TestCase(1.005, 5000.0)]      // half a percent
        [TestCase(1.04, 40000.0)]      // the 57.7 Hz-for-60 Hz case that broke the old design
        public void ARateDifferenceIsMeasuredRatherThanAccumulated(double rate, double expectedPpm)
        {
            var e = new SnapshotStalenessEstimator();

            long tick = 1000;
            double now = ClockOffset + tick / (double)BaseHz;
            float last = 0f;

            // Two minutes of snapshots on a clean link running at the wrong relative rate.
            for (var i = 0; i < 15 * 120; i++)
            {
                last = e.Sample(tick, now, BaseHz);
                tick += SnapshotEvery;
                now += Interval * rate;
            }

            Assert.That(e.IsUsable, Is.True);

            Assert.That(last, Is.LessThan(2f),
                $"two minutes at a {(rate - 1) * 100:F2} % rate difference read as {last:F1} " +
                "ticks of staleness on a link with no jitter at all. The rate is not being " +
                "fitted, so it is accumulating as age -- which is what took the old design " +
                "to 613 ticks and dragged the steering with it.");

            Assert.That(e.SkewPpm, Is.EqualTo(expectedPpm).Within(expectedPpm * 0.1),
                $"the rate difference measured {e.SkewPpm:F0} ppm against {expectedPpm:F0} " +
                "expected. It has to be reported as well as absorbed: tens of thousands of " +
                "ppm is not two clocks drifting, it is a tick rate that does not match what " +
                "the server is running, and it deserves an error rather than a correction.");
        }

        /// <summary>
        /// The rate is fitted over a long baseline, and the baseline is what makes a small
        /// difference measurable at all.
        /// </summary>
        /// <remarks>
        /// A slope over a short baseline is mostly the noise of its two endpoints: over one
        /// second, a millisecond of residual jitter reads as 1000 ppm — twenty times the
        /// difference between two ordinary crystals. The older anchor is therefore kept
        /// rather than rolled forward, so the baseline grows and the estimate tightens.
        /// </remarks>
        [Test]
        public void TheBaselineGrowsSoTheRateEstimateTightens()
        {
            var e = new SnapshotStalenessEstimator();

            long tick = 1000;
            double now = ClockOffset + tick / (double)BaseHz;

            for (var i = 0; i < 15 * 30; i++)
            {
                e.Sample(tick, now, BaseHz);
                tick += SnapshotEvery;
                now += Interval;
            }

            Assert.That(e.BaselineSeconds, Is.GreaterThan(20.0),
                $"after thirty seconds the rate was still being fitted over " +
                $"{e.BaselineSeconds:F1} s. A baseline that does not grow leaves the estimate " +
                "at the noise of two nearby samples forever.");

            Assert.That(e.BaselineSeconds,
                Is.LessThanOrEqualTo(SnapshotStalenessEstimator.MaximumBaselineSeconds));
        }

        /// <summary>
        /// A fit that would imply an impossible rate is refused rather than believed.
        /// </summary>
        /// <remarks>
        /// Two clocks that disagree by more than a few percent are not two clocks with skew —
        /// they are a wrong tick rate or a stepped clock, and a slope fitted through such a
        /// pair would steer the simulation somewhere arbitrary. Refusing keeps a bad fit
        /// merely unhelpful instead of harmful.
        /// </remarks>
        [Test]
        public void AnImpossibleRateIsRefusedRatherThanFitted()
        {
            var e = new SnapshotStalenessEstimator();

            long tick = 1000;
            double now = ClockOffset + tick / (double)BaseHz;

            // The client's clock running at half the server's tick time: not skew.
            for (var i = 0; i < 15 * 30; i++)
            {
                e.Sample(tick, now, BaseHz);
                tick += SnapshotEvery;
                now += Interval * 0.5;
            }

            Assert.That(e.IsUsable, Is.False,
                "a rate half of the server's was accepted as a fit. Steering a simulation on " +
                "that puts it somewhere arbitrary, and the caller has no way to tell a fitted " +
                "line from a fitted absurdity.");
        }

        [Test]
        public void ANegativeReadingIsNeverProduced()
        {
            var e = Warm(out long tick, out double now);

            // Earlier than the floor by a wide margin: a snapshot cannot be read before it
            // was produced, so this is a stale floor, not time running backwards.
            float staleness = e.Sample(tick, now - 1.0, BaseHz);

            Assert.That(staleness, Is.GreaterThanOrEqualTo(0f));
        }

        [Test]
        public void UnusableInputsAreIgnoredRatherThanAveragedIn()
        {
            var e = Warm(out long tick, out double now);
            float before = e.StalenessTicks;

            e.Sample(0, now, BaseHz);          // no tick
            e.Sample(tick, now, 0f);           // no rate: a tick cannot be turned into a time

            Assert.That(e.StalenessTicks, Is.EqualTo(before),
                "a sample that carries no usable rate or tick has nothing to contribute, and " +
                "folding it in at zero would pull the estimate toward a number nobody measured");
        }

        /// <summary>
        /// A reconnect starts over: the floor describes one route to one server.
        /// </summary>
        [Test]
        public void ResetForgetsTheRoute()
        {
            var e = Warm(out long tick, out double now, delay: 0.005);

            e.Reset();

            Assert.That(e.IsUsable, Is.False);
            Assert.That(e.Samples, Is.Zero);
            Assert.That(e.StalenessTicks, Is.EqualTo(0f));

            // A slower route measures against its own best case, not the old one's: the
            // line is a property of one connection, and 200 ms of steady delay on a new one
            // belongs in its offset rather than in its readings.
            var e2 = new SnapshotStalenessEstimator();
            double until = now + SnapshotStalenessEstimator.MinimumBaselineSeconds
                               + SnapshotStalenessEstimator.EpochSeconds * 2;

            while (now < until)
            {
                e2.Sample(tick, now + 0.200, BaseHz);
                tick += SnapshotEvery;
                now += Interval;
            }

            Assert.That(e2.StalenessTicks, Is.LessThan(1f),
                "a fresh estimator on a 200 ms link read it as stale rather than as its own " +
                "baseline, which is what carrying a line across a session boundary does");
        }
    }
}
