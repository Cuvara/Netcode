using NUnit.Framework;
using Cuvara.Netcode.Interpolation;
using Cuvara.Netcode.View;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Pins the edges of the interpolation core — the clock, the bracketing evaluator and
    /// the ring — that <see cref="RemoteInterpolationContinuityTests"/> cannot reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Those four tests are integration-level: they drive a whole
    /// <see cref="WorldViewBinder"/> over a realistic stream and assert on what a player
    /// would see. That is the right shape for "is the motion smooth", and the wrong shape
    /// for "what happens when the buffer holds one sample", "what happens on the frame the
    /// ring wraps", or "can the render clock ever run backwards". Those are single-call
    /// questions about a pure function, and a stream test that happened to cover one of
    /// them would cover it by accident and stop covering it the day the stream changed.
    /// </para>
    /// <para>
    /// <b>Everything here is a direct call.</b> No binder, no view, no clock reading, no
    /// timeline — the core takes its time as a parameter, which is the whole reason it was
    /// extracted.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class InterpolationCoreTests
    {
        /// <summary>Minimal <see cref="ISampleBuffer"/> over a plain array, for direct calls.</summary>
        private readonly struct ArrayBuffer : ISampleBuffer
        {
            private readonly InterpolationSample[] _samples;

            public ArrayBuffer(params InterpolationSample[] samples)
            {
                _samples = samples;
            }

            public int Length => _samples == null ? 0 : _samples.Length;

            public InterpolationSample this[int index] => _samples[index];
        }

        private static InterpolationSample Sample(long tick, float x, float y = 0f, double receive = 0.0) =>
            new InterpolationSample { Tick = tick, X = x, Y = y, ReceiveTime = receive };

        private static InterpolationConfig Config => InterpolationConfig.Default;

        private const double Nominal = 1.0 / 15.0;

        // ---------------------------------------------------------------- the clock

        [Test]
        public void ClockStartsAFullTargetDelayBehindTheFirstTick()
        {
            var config = Config;
            var clock = new InterpolationClock();
            clock.NoteSnapshot(100, 10.0, 1, config);

            Assert.That(clock.SecondsPerTick, Is.EqualTo(Nominal).Within(1e-9),
                "with a confirmed snapshot tick gap of 1, one tick must be seeded at the " +
                "nominal snapshot interval");
            Assert.That(clock.RenderTick, Is.EqualTo(100.0 - config.TargetDelay / Nominal).Within(1e-9),
                $"the render clock must start {config.TargetDelay * 1000.0:F0} ms behind the " +
                "first tick, so the jitter buffer has something to fill; starting at the tick " +
                "would leave nothing to interpolate toward and force extrapolation from frame one");
        }

        [Test]
        public void ClockSeedsSecondsPerTickFromTheConfirmedSnapshotTickGap()
        {
            var clock = new InterpolationClock();

            // The real deployment: 60 Hz base ticks, a world snapshot every 4 of them.
            clock.NoteSnapshot(1000, 0.0, 4, Config);

            Assert.That(clock.SecondsPerTick, Is.EqualTo(Nominal / 4.0).Within(1e-9),
                "a snapshot every 4 base ticks at the nominal 15 Hz world rate means one " +
                "base tick takes a quarter of the interval; ignoring the gap — which is what " +
                "the interpolator used to do while the predictor was handed it two lines away — " +
                "makes the very first frames render at four times the right rate");
        }

        [Test]
        public void ASkippedTickDoesNotChangeTheSecondsPerTickEstimate()
        {
            var clock = new InterpolationClock();
            clock.NoteSnapshot(1, 0.0, 1, Config);
            clock.NoteSnapshot(2, Nominal, 1, Config);
            var settled = clock.SecondsPerTick;

            // Tick 3's snapshot never arrives; tick 4 turns up at its own natural time.
            clock.NoteSnapshot(4, Nominal * 3.0, 1, Config);

            Assert.That(clock.SecondsPerTick, Is.EqualTo(settled).Within(1e-9),
                $"the estimate moved from {settled * 1000.0:F2} ms to " +
                $"{clock.SecondsPerTick * 1000.0:F2} ms per tick because a snapshot was " +
                "dropped. It must not: the measurement is arrivalGap / tickGap, and a " +
                "doubled gap carries a doubled tick delta, so the ratio is unchanged. " +
                "Averaging arrival intervals instead is what made a dropped packet render " +
                "at 1.54x speed and then freeze");
        }

        [Test]
        public void TheFirstMeasurementReplacesTheSeedRatherThanBeingSmoothedIntoIt()
        {
            var config = Config;
            var clock = new InterpolationClock();

            // Seeded at the nominal rate, but the server is actually running at half of it.
            clock.NoteSnapshot(1, 0.0, 1, config);
            clock.NoteSnapshot(2, Nominal * 2.0, 1, config);

            Assert.That(clock.SecondsPerTick, Is.EqualTo(Nominal * 2.0).Within(1e-9),
                "the seed is a guess from a nominal rate and a tick gap that may not be " +
                "confirmed yet; letting an EMA crawl away from a wrong guess means rendering " +
                "at a wrong rate for seconds after every join");

            // And thereafter it smooths.
            clock.NoteSnapshot(3, Nominal * 3.0, 1, config);
            var expected = Nominal * 2.0 * (1.0 - config.IntervalSmoothing) + Nominal * config.IntervalSmoothing;
            Assert.That(clock.SecondsPerTick, Is.EqualTo(expected).Within(1e-9),
                "after the first measurement the estimate must be smoothed, not replaced, " +
                "or one jittery arrival would set the whole render rate");
        }

        [Test]
        public void AnOutOfOrderSnapshotIsIgnored()
        {
            var clock = new InterpolationClock();
            clock.NoteSnapshot(10, 0.0, 1, Config);
            clock.NoteSnapshot(20, Nominal * 10.0, 1, Config);
            var newest = clock.NewestTick;
            var perTick = clock.SecondsPerTick;

            clock.NoteSnapshot(15, Nominal * 11.0, 1, Config);

            Assert.That(clock.NewestTick, Is.EqualTo(newest),
                "a reordered snapshot must not drag the timeline backwards — its state is " +
                "already superseded, so dropping it costs nothing and honouring it would " +
                "rewind every entity at once");
            Assert.That(clock.SecondsPerTick, Is.EqualTo(perTick).Within(1e-12),
                "and it must not be allowed to poison the rate estimate with a negative gap");
        }

        [Test]
        public void TheRenderClockNeverGoesBackwardsHoweverFarAheadItIs()
        {
            var config = Config;
            var clock = new InterpolationClock();
            clock.NoteSnapshot(1, 0.0, 1, config);

            // Drive it a long way past its target with no new snapshots at all, then keep
            // advancing. This is the pathological case: maximum negative error, sustained.
            var previous = clock.RenderTick;
            for (var i = 0; i < 400; i++)
            {
                clock.Advance(1.0 / 60.0, config);
                Assert.That(clock.RenderTick, Is.GreaterThan(previous),
                    $"the render clock stalled or reversed at frame {i} " +
                    $"({previous:F4} -> {clock.RenderTick:F4} ticks) while {previous - clock.TargetTick(config):F2} " +
                    "ticks ahead of its target. Strict monotonicity is the invariant the " +
                    "whole continuity argument rests on: the rendered position is a monotonic " +
                    "function of this clock along a fixed path, so a clock that can stop can " +
                    "stall the render and a clock that can reverse can pop it");
                previous = clock.RenderTick;
            }
        }

        [Test]
        public void TheRenderClockRateStaysInsideTheConfiguredCap()
        {
            var config = Config;
            var clock = new InterpolationClock();
            clock.NoteSnapshot(1, 0.0, 1, config);

            // Far behind its target: a big positive error asking for a big speed-up.
            clock.RenderTick -= 100.0;

            var before = clock.RenderTick;
            const double dt = 1.0 / 60.0;
            clock.Advance(dt, config);
            var ticks = clock.RenderTick - before;
            var rate = ticks / (dt / clock.SecondsPerTick);

            Assert.That(rate, Is.LessThanOrEqualTo(1.0 + config.MaxClockRateAdjust + 1e-9),
                $"the clock caught up at {rate:F3}x nominal, past the " +
                $"{1.0 + config.MaxClockRateAdjust:F2}x cap. Time dilation above about 10 % " +
                "is visible as another avatar moving unnaturally, and an uncapped catch-up " +
                "is a snap wearing a different name");
        }

        [Test]
        public void AdvanceIgnoresNonPositiveDeltasAndAnEmptyClock()
        {
            var config = Config;
            var empty = new InterpolationClock();
            empty.Advance(1.0, config);
            Assert.That(empty.RenderTick, Is.EqualTo(0.0),
                "nothing may be rendered before a single snapshot has been seen; advancing " +
                "an unseeded clock would invent a timeline out of nothing");

            var clock = new InterpolationClock();
            clock.NoteSnapshot(5, 0.0, 1, config);
            var seeded = clock.RenderTick;
            clock.Advance(0.0, config);
            clock.Advance(-1.0, config);
            Assert.That(clock.RenderTick, Is.EqualTo(seeded),
                "a zero or negative frame delta must be a no-op, not a rewind");
        }

        [Test]
        public void ResetReturnsTheClockToItsUnseededState()
        {
            var clock = new InterpolationClock();
            clock.NoteSnapshot(42, 1.0, 1, Config);
            clock.Advance(0.5, Config);
            clock.Reset();

            Assert.That(clock.HasSamples, Is.False, "a reset clock must not claim to have seen a snapshot");
            Assert.That(clock.NewestTick, Is.EqualTo(0L));
            Assert.That(clock.RenderTick, Is.EqualTo(0.0),
                "a session boundary must not leave the previous session's timeline behind — " +
                "the new session's ticks start somewhere unrelated");
        }

        // ------------------------------------------------------------- the evaluator

        [Test]
        public void AnEmptyBufferRendersNothing()
        {
            var ok = SnapshotInterpolation.EvaluateAt(new ArrayBuffer(), 10.0, Nominal, Config, out _, out _);

            Assert.That(ok, Is.False,
                "with no samples there is no honest position to draw, and returning a " +
                "default of (0, 0) would put the entity at the world origin — a teleport to " +
                "the map corner is worse than not moving it");
        }

        [Test]
        public void ASingleSampleRendersAtThatSample()
        {
            var ok = SnapshotInterpolation.EvaluateAt(
                new ArrayBuffer(Sample(10, 3f, 4f)), 999.0, Nominal, Config, out var x, out var y);

            Assert.That(ok, Is.True);
            Assert.That(x, Is.EqualTo(3f), "one sample is a position, not a direction — there is nothing to extrapolate along");
            Assert.That(y, Is.EqualTo(4f));
        }

        [Test]
        public void ARenderTickBeforeTheOldestSampleHoldsAtTheOldest()
        {
            var ok = SnapshotInterpolation.EvaluateAt(
                new ArrayBuffer(Sample(10, 100f), Sample(11, 200f)), 5.0, Nominal, Config, out var x, out _);

            Assert.That(ok, Is.True);
            Assert.That(x, Is.EqualTo(100f),
                "this is the jitter buffer filling after a join or an area-of-interest " +
                "entry: the entity has no history to have come from, so it holds at its " +
                "first known position rather than being extrapolated backwards into one it " +
                "was never in");
        }

        [Test]
        public void ARenderTickInsideASegmentLerpsByTheTickFraction()
        {
            var ok = SnapshotInterpolation.EvaluateAt(
                new ArrayBuffer(Sample(10, 0f, 0f), Sample(11, 10f, 20f)),
                10.25, Nominal, Config, out var x, out var y);

            Assert.That(ok, Is.True);
            Assert.That(x, Is.EqualTo(2.5f).Within(1e-4f), "a quarter of the way through the tick is a quarter of the way along the segment");
            Assert.That(y, Is.EqualTo(5f).Within(1e-4f));
        }

        [Test]
        public void ASegmentSpanningTwoTicksIsCoveredInTwoTicksWorthOfTime()
        {
            // Tick 11's snapshot was dropped; tick 12 carries twice the distance.
            var buffer = new ArrayBuffer(Sample(10, 0f), Sample(12, 20f));

            SnapshotInterpolation.EvaluateAt(buffer, 11.0, Nominal, Config, out var half, out _);
            SnapshotInterpolation.EvaluateAt(buffer, 12.0, Nominal, Config, out var full, out _);

            Assert.That(half, Is.EqualTo(10f).Within(1e-4f),
                $"one tick into a two-tick segment must be half way along it, and it rendered " +
                $"at {half:F2} of 20. Bracketing by tick is the whole reason a skipped server " +
                "tick no longer doubles the rendered speed: the distance doubled and so did " +
                "the time allowed for it");
            Assert.That(full, Is.EqualTo(20f).Within(1e-4f));
        }

        [Test]
        public void PastTheNewestSampleMotionIsCarriedThenCapped()
        {
            var config = Config;
            var buffer = new ArrayBuffer(Sample(10, 0f), Sample(11, 10f));

            // MaxExtrapolation is 50 ms against a 66.7 ms tick: 0.75 of a tick, 7.5 units.
            SnapshotInterpolation.EvaluateAt(buffer, 11.5, Nominal, config, out var carried, out _);
            SnapshotInterpolation.EvaluateAt(buffer, 99.0, Nominal, config, out var capped, out _);

            Assert.That(carried, Is.EqualTo(15f).Within(1e-3f),
                "half a tick past the newest sample must carry half a segment, so a single " +
                "dropped packet does not read as a freeze");
            Assert.That(capped, Is.EqualTo(10f + 10f * (config.MaxExtrapolation / Nominal)).Within(1e-3f),
                $"extrapolation ran to {capped:F2} instead of stopping at the " +
                $"{config.MaxExtrapolation * 1000.0:F0} ms cap. Past the cap the client is " +
                "inventing motion the server never confirmed, and the further it invents the " +
                "more there is to take back");
        }

        [Test]
        public void ExtrapolationCanBeDisabledOutright()
        {
            var config = Config;
            config.MaxExtrapolation = 0.0;

            SnapshotInterpolation.EvaluateAt(
                new ArrayBuffer(Sample(10, 0f), Sample(11, 10f)), 20.0, Nominal, config, out var x, out _);

            Assert.That(x, Is.EqualTo(10f),
                "zero must mean never extrapolate — hold at the newest confirmed state. " +
                "A deployment that would rather see an entity freeze than see it guess must " +
                "be able to say so without editing the algorithm");
        }

        [Test]
        public void TheEvaluatorFindsTheRightSegmentInADeepBuffer()
        {
            var buffer = new ArrayBuffer(
                Sample(10, 0f), Sample(11, 10f), Sample(12, 20f),
                Sample(13, 30f), Sample(14, 40f), Sample(15, 50f));

            SnapshotInterpolation.EvaluateAt(buffer, 11.5, Nominal, Config, out var x, out _);

            Assert.That(x, Is.EqualTo(15f).Within(1e-4f),
                "with several buffered snapshots the render clock is working through the " +
                "earlier ones — the case a batched TCP read produces — and it must bracket " +
                "the segment the clock is actually in, not the newest pair");
        }

        // ------------------------------------------------------------------ the ring

        [Test]
        public void TheRingKeepsTheNewestSamplesAndDropsTheOldest()
        {
            var ring = new EntitySampleRing(4);
            for (var tick = 1L; tick <= 6L; tick++)
            {
                ring.TryPush(Sample(tick, tick * 10f));
            }

            Assert.That(ring.Length, Is.EqualTo(4), "capacity must be a hard limit, not a hint");
            Assert.That(ring[0].Tick, Is.EqualTo(3L),
                "the oldest retained sample must be the oldest still needed; after six " +
                "arrivals into four slots the first two are gone and index 0 is tick 3");
            Assert.That(ring[3].Tick, Is.EqualTo(6L), "and the newest must still be at the end after wrapping");
            Assert.That(ring[3].X, Is.EqualTo(60f), "wrapping must move the payload with the tick, not just the index");
        }

        [Test]
        public void TheRingStaysOrderedAcrossSeveralWraps()
        {
            var ring = new EntitySampleRing(3);
            for (var tick = 1L; tick <= 20L; tick++)
            {
                ring.TryPush(Sample(tick, tick));
            }

            for (var i = 1; i < ring.Length; i++)
            {
                Assert.That(ring[i].Tick, Is.GreaterThan(ring[i - 1].Tick),
                    $"index {i} holds tick {ring[i].Tick} after index {i - 1} holds " +
                    $"{ring[i - 1].Tick}. The evaluator's bracketing assumes strictly " +
                    "increasing ticks; an out-of-order slot makes it pick a pair spanning no " +
                    "time and render the wrong endpoint, silently");
            }
        }

        [Test]
        public void TheRingRejectsADuplicateOrReorderedTick()
        {
            var ring = new EntitySampleRing(8);
            ring.TryPush(Sample(5, 50f));

            Assert.That(ring.TryPush(Sample(5, 999f)), Is.False, "a duplicate tick must be refused");
            Assert.That(ring.TryPush(Sample(4, 999f)), Is.False, "an older tick must be refused");
            Assert.That(ring.Length, Is.EqualTo(1));
            Assert.That(ring[0].X, Is.EqualTo(50f),
                "and the refusal must leave the held sample alone rather than overwriting it " +
                "with the superseded state");
        }

        [Test]
        public void ClearingARingEmptiesItForReuse()
        {
            var ring = new EntitySampleRing(4);
            ring.TryPush(Sample(1, 1f));
            ring.TryPush(Sample(2, 2f));
            ring.Clear();

            Assert.That(ring.Length, Is.EqualTo(0));
            Assert.That(ring.TryPush(Sample(1, 7f)), Is.True,
                "a pooled ring handed to a new entity must accept that entity's ticks; if " +
                "the previous occupant's newest tick survived Clear, a lower-tick session " +
                "would be silently refused and the entity would never move");
            Assert.That(ring[0].X, Is.EqualTo(7f),
                "and it must not interpolate the new entity from wherever the previous one stood");
        }

        [Test]
        public void ABufferOverAnEmptyRingIsSafeToEvaluate()
        {
            var ok = SnapshotInterpolation.EvaluateAt(
                new EntitySampleBuffer(new EntitySampleRing(4)), 1.0, Nominal, Config, out _, out _);

            Assert.That(ok, Is.False,
                "an entity that has been spawned but whose first snapshot has not been " +
                "pushed yet must report 'nothing to draw', not throw inside a render loop");
        }

        // ---------------------------------------------------------------- the config

        [Test]
        public void ADefaultConstructedConfigIsNormalisedToUsableNumbers()
        {
            var normalised = default(InterpolationConfig).Normalized();
            var defaults = InterpolationConfig.Default;

            Assert.That(normalised.DefaultInterval, Is.EqualTo(defaults.DefaultInterval),
                "a zero interval would divide by zero on the first snapshot");
            Assert.That(normalised.RingCapacity, Is.EqualTo(defaults.RingCapacity),
                "a zero ring capacity would buffer nothing and render every entity at its " +
                "first known position forever");
            Assert.That(normalised.TargetDelay, Is.EqualTo(defaults.TargetDelay));
            Assert.That(normalised.MaxClockRateAdjust, Is.EqualTo(defaults.MaxClockRateAdjust));
        }

        [Test]
        public void NormalisationLeavesADeliberateZeroExtrapolationAlone()
        {
            var config = InterpolationConfig.Default;
            config.MaxExtrapolation = 0.0;

            Assert.That(config.Normalized().MaxExtrapolation, Is.EqualTo(0.0),
                "zero extrapolation is a choice, not an omission — unlike every other field " +
                "here, it has a meaning at zero, and defaulting it would silently overrule a " +
                "deployment that asked for no guessing");
        }
    }
}
