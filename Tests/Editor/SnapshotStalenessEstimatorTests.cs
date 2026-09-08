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
        /// A reading is available long before the RATE fit lands, and it is the real age.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The gap this closes, measured.</b> A rate is a slope and cannot be fitted over a
        /// short baseline, so <see cref="SnapshotStalenessEstimator.IsUsable"/> cannot turn
        /// true early — the test above pins that deliberately. But epoch one only sets an
        /// anchor and two consecutive two-second epochs cannot span the four seconds a fit
        /// needs, so against a 15 Hz snapshot stream the first fit lands <b>8.2 s after
        /// join</b>, and for those eight seconds the caller had no reading at all and fell
        /// back to a derived one snapshot interval.
        /// </para>
        /// <para>
        /// On localhost that fallback is <b>4 base ticks against a real age of 0.06</b>, and
        /// a four-tick error in the steering target is a four-tick error in what a tick number
        /// means on the two sides — which the reconcile reports as the <b>0.3333-unit,
        /// 4.00-step</b> correction this fixture's own remarks describe, at every start and
        /// every stop, for the first eight seconds of every session. Live, 36 corrections in
        /// 162 reconciles with both sides agreeing on 60 Hz and every other counter clean.
        /// </para>
        /// <para>
        /// The age does not need the rate: over a few seconds the envelope's slope is one to
        /// within a few hundred ppm, which is 0.02 base ticks over ten seconds against the
        /// four ticks it replaces. So the reading is offered from
        /// <see cref="SnapshotStalenessEstimator.MinimumProvisionalSamples"/> onward, flagged
        /// as provisional rather than fitted.
        /// </para>
        /// </remarks>
        [TestCase(0.0)]
        [TestCase(1.0)]
        [TestCase(3.0)]
        public void TheAgeIsReadableBeforeTheRateFitLands(double extraTicks)
        {
            var e = new SnapshotStalenessEstimator();
            long tick = 1000;
            double now = ClockOffset + tick / (double)BaseHz + 0.010;

            // A steady route, so the floor is established after a couple of samples.
            for (var i = 0; i < SnapshotStalenessEstimator.MinimumProvisionalSamples; i++)
            {
                e.Sample(tick, now, BaseHz);
                tick += SnapshotEvery;
                now += Interval;
            }

            Assert.That(e.HasEstimate, Is.True,
                "a reading must be available within a fifth of a second of joining, or the " +
                "caller spends the whole warm-up steering on a derived number.");
            Assert.That(e.IsUsable, Is.False,
                "and it must NOT claim to be a fitted line: a rate over this baseline would " +
                "be noise, which is what the test above exists to keep true.");

            // Hold one snapshot for a known extra span. That span IS the age.
            e.Sample(tick, now + extraTicks / BaseHz, BaseHz);

            Assert.That(e.StalenessTicks, Is.EqualTo((float)extraTicks).Within(0.05f),
                "the provisional reading is the height above the running floor, so a snapshot " +
                "held for n ticks must read as n ticks.");
        }

        /// <summary>
        /// The provisional reading may drift upward when the two clocks' rates differ, and
        /// never downward — which is what makes clamping it from above safe.
        /// </summary>
        /// <remarks>
        /// This is the property <see cref="WorldViewBinderLeadTests"/> relies on: the caller
        /// takes <c>min(provisional, derived)</c>, so below the derived figure the reading is
        /// evidence and above it it is drift. The 1.103 ratio here is a synthetic one, driven
        /// directly, and exercises the fit at a 10% difference; it was once believed to be this
        /// machine's real ratio, which it is not — see
        /// <see cref="SnapshotStalenessEstimator.MinimumSkew"/>'s remarks, where an unfitted
        /// rate is at its most dangerous.
        /// </remarks>
        [Test]
        public void AnUnfittedRateDriftsTheProvisionalReadingUpwardOnly()
        {
            var e = new SnapshotStalenessEstimator();
            long tick = 1000;
            double t0 = ClockOffset + tick / (double)BaseHz;
            const double skew = 1.103;

            float lowest = float.MaxValue;
            float highest = 0f;

            // Stay inside the warm-up: no fit may land, or this measures the fitted line.
            for (var i = 0; i < 20; i++)
            {
                double now = t0 + skew * (i * Interval);
                e.Sample(tick, now, BaseHz);
                tick += SnapshotEvery;

                if (!e.HasEstimate) continue;
                if (e.StalenessTicks < lowest) lowest = e.StalenessTicks;
                if (e.StalenessTicks > highest) highest = e.StalenessTicks;
            }

            Assert.That(e.IsUsable, Is.False, "precondition: still inside the warm-up");
            Assert.That(lowest, Is.GreaterThanOrEqualTo(0f),
                "a snapshot cannot be read before it was produced");
            Assert.That(highest, Is.GreaterThan(1f),
                "a 10% rate difference must show as a growing reading rather than be absorbed " +
                "silently -- if it did not, clamping from above would be pointless and the " +
                "caller could simply believe the provisional number.");
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

            Assert.That(e.FitsRefused, Is.GreaterThan(0),
                "the refusal was silent. SkewPpm reads 0 without a fit, which is exactly what " +
                "two clocks that agree look like, so a bound set too tightly disables the " +
                "measurement for a whole session and reports nothing.");

            Assert.That(e.RefusedSkewPpm, Is.LessThan(-100_000),
                $"the refused rate read {e.RefusedSkewPpm:F0} ppm. The number that was rejected " +
                "has to be visible, or a bound that is wrong cannot be seen to be wrong.");
        }

        /// <summary>
        /// A real machine's clock difference is fitted, not refused.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The bounds were originally 0.90 and 1.10 and every fit was refused: <c>IsUsable</c>
        /// stayed false for a whole session and the steering fell back to the derived figure
        /// with nothing reporting it. That was attributed to the machine sitting at about
        /// <b>1.103</b> — the Windows performance counter running fast against the Linux clock
        /// the server ticks on — and the bounds were widened to admit it.
        /// </para>
        /// <para>
        /// <b>The 1.103 attribution has since been falsified.</b> The same machine, idle,
        /// measures <b>220 ppm</b> — a ratio of 1.0002 — and only reads 90 000 ppm under load,
        /// which is a delay-floor step being absorbed as rate rather than a clock difference.
        /// See <c>SnapshotStalenessEstimator.MinimumSkew</c>. The ratios below are therefore
        /// SYNTHETIC: they exercise the band at values a machine could in principle sit at, and
        /// 1.103 is kept because the band still has to admit it, not because anything measured
        /// it.
        /// </para>
        /// </remarks>
        [TestCase(1.103)]
        [TestCase(0.90)]
        [TestCase(1.25)]
        public void AnOrdinaryMachinesClockDifferenceIsFittedRatherThanRefused(double rate)
        {
            var e = new SnapshotStalenessEstimator();

            long tick = 1000;
            double now = ClockOffset + tick / (double)BaseHz;

            for (var i = 0; i < 15 * 30; i++)
            {
                e.Sample(tick, now, BaseHz);
                tick += SnapshotEvery;
                now += Interval * rate;
            }

            Assert.That(e.IsUsable, Is.True,
                $"a clock ratio of {rate:F3} was refused ({e.FitsRefused} refusals, last " +
                $"reading {e.RefusedSkewPpm:F0} ppm). Between two ordinary machines and a tick " +
                "rate that is simply wrong there is an order of magnitude; a bound at the edge " +
                "of the first rejects reality and reports nothing.");

            Assert.That(e.FitsRefused, Is.Zero);
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

        /// <summary>
        /// Run a link at a fixed clock ratio for <paramref name="seconds"/>, optionally raising
        /// the DELAY FLOOR partway through — the failure the envelope fit cannot see by itself.
        /// </summary>
        private static SnapshotStalenessEstimator Run(
            double seconds, double rate = 1.0, double floorStep = 0.0, double stepAt = 0.0)
        {
            var e = new SnapshotStalenessEstimator();
            long tick = 1000;
            double t0 = ClockOffset + tick / (double)BaseHz;
            double elapsed = 0;

            while (elapsed < seconds)
            {
                double floor = (stepAt > 0 && elapsed >= stepAt) ? floorStep : 0.0;
                e.Sample(tick, t0 + rate * elapsed + 0.002 + floor, BaseHz);
                tick += SnapshotEvery;
                elapsed += Interval;
            }

            return e;
        }

        /// <summary>
        /// A delay floor that rises between the two anchors is not a rate, and must not reach a
        /// clock as one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the assumption the whole fit rests on, and it went unchecked for three
        /// releases.</b> A line through two best-case samples is a rate only if the MINIMUM
        /// ACHIEVABLE DELAY is the same at both. When it is not — a starved frame loop, a
        /// machine that got busy — the later anchor sits above the true line and the slope
        /// absorbs the displacement as rate. The estimator documented the assumption in its own
        /// remarks and then fed the result straight to <c>SetClockRateScale</c>.
        /// </para>
        /// <para>
        /// Live, on one machine minutes apart: idle it read <b>220 ppm</b>, and inside a loaded
        /// test suite <b>90 636 ppm</b> — a number that cannot be a crystal ratio, because
        /// crystal ratios do not move 90 000 ppm in ten minutes. The client then ran its
        /// base-tick clock 8.3% slow on purpose, sat at a three-tick standing error, and
        /// corrected by three whole steps at every transition. Over the 4 s minimum baseline
        /// that slope is a delay-floor step of 362 ms, which is an ordinary hitch.
        /// </para>
        /// <para>
        /// What separates the two is baseline. A rate is constant and reads the same over any
        /// span; a step fakes <c>step / baseline</c> and decays as the baseline grows. So the
        /// fit may still land — the AGE it reports is still worth having — but
        /// <see cref="SnapshotStalenessEstimator.RateCorroborated"/> must not.
        /// </para>
        /// </remarks>
        [Test]
        public void ADelayFloorStepIsNotARateAndMustNotCorroborate()
        {
            // Two clocks that genuinely agree, with a 300 ms delay floor arriving at 6 s.
            var e = Run(seconds: 30.0, rate: 1.0, floorStep: 0.300, stepAt: 6.0);

            Assert.That(e.IsUsable, Is.True,
                "precondition: the fit still lands — this is not about refusing to measure");

            Assert.That(e.RateCorroborated, Is.False,
                "the slope here is a 300 ms displacement divided by whatever baseline it was "
                + "measured over, so it reads differently every time the baseline grows. A rate "
                + "does not do that, and nothing that does may reach a clock.");
        }

        /// <summary>
        /// A real rate difference reads the same over every baseline, so it corroborates and is
        /// believed — the guard must not have bought safety by refusing to measure at all.
        /// </summary>
        [TestCase(1.0002)]     // the measured truth on the development machine: two crystals
        [TestCase(1.005)]      // half a percent, an unusual but real pair
        public void ARealRateDifferenceCorroboratesAndIsBelieved(double rate)
        {
            var e = Run(seconds: 30.0, rate: rate);

            Assert.That(e.IsUsable, Is.True, "precondition: a line must be fitted");
            Assert.That(e.RateCorroborated, Is.True,
                "a constant rate reads the same over a 4 s baseline and an 8 s one, so "
                + "successive fits agree and the reading is evidence. Refusing this would "
                + "reintroduce the failure the 0.90/1.10 bounds produced, by another door.");

            Assert.That(e.SkewPpm, Is.EqualTo((rate - 1.0) * 1e6).Within(CorroborationPpmTolerance),
                "and the rate it corroborated on must be the real one");
        }

        private const double CorroborationPpmTolerance = 1500.0;

        /// <summary>
        /// A rate beyond one percent is counted, because it is either a remarkable machine or a
        /// measurement taken across something that moved, and both are worth seeing.
        /// </summary>
        [Test]
        public void AnExtraordinaryRateIsCountedRatherThanPassingSilently()
        {
            var e = Run(seconds: 30.0, rate: 1.05);

            Assert.That(e.FitsExtraordinary, Is.GreaterThan(0),
                "SkewPpm's remarks have always said tens of thousands is not skew and is worth "
                + "an error rather than a correction. Nothing enforced it and the correction "
                + "was issued anyway; now at least it is visible.");
        }

        /// <summary>
        /// A refused slope must not reach the AGE either. It is the same fit, and the age is the
        /// height above it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Gating the clock alone was half a fix, and the counter said so.</b> The age is
        /// <c>y - (offset + skew * x)</c>, so the slope <see cref="SnapshotStalenessEstimator.RateCorroborated"/>
        /// refuses is the same slope the residual is measured against — refusing it for the
        /// clock left it steering the lead by the other route, undiminished, and growing with
        /// the distance from the anchor.
        /// </para>
        /// <para>
        /// Measured live on a run where the rate was correctly refused: a 51 225 ppm fit over a
        /// 6.1 s baseline displaces the line by 0.31 s — <b>18.7 base ticks</b> — and the age
        /// read <b>5.24</b> against a true idle age of 0.06, taking the lead to 6 where healthy
        /// runs sat at 1. The correction stayed at three whole steps with every rate counter
        /// reading clean.
        /// </para>
        /// </remarks>
        [Test]
        public void ARefusedSlopeDoesNotReachTheAgeEither()
        {
            // Two clocks that genuinely agree, with a 300 ms delay floor arriving at 6 s: the
            // fit lands, the slope is a displacement over a baseline, and nothing may believe it.
            var e = Run(seconds: 40.0, rate: 1.0, floorStep: 0.300, stepAt: 6.0);

            Assert.That(e.IsUsable, Is.True, "precondition: a line was fitted");
            Assert.That(e.RateCorroborated, Is.False, "precondition: and its slope was refused");

            Assert.That(e.AgeIsFitted, Is.False,
                "the age must fall back to the unit-rate provisional reading, which carries no "
                + "slope and so cannot accumulate with the baseline. Measuring it against the "
                + "refused line is how the slope kept steering the lead after the clock stopped "
                + "listening to it.");

            // The true age here is the 300 ms step above the pre-step floor: 18 base ticks. What
            // must NOT happen is the reading running away with the distance from the anchor.
            Assert.That(e.StalenessTicks, Is.LessThan(60f),
                "the provisional reading is a height above a running floor at unit rate. It is "
                + "bounded by what the route actually did; a slope-derived one is bounded by "
                + "nothing but the baseline, and the caller's ceiling.");
        }
    }
}
