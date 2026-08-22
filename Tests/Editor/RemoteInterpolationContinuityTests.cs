using System;
using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using Cuvara.Netcode.World;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Covers the one property of remote interpolation that the existing
    /// <see cref="WorldViewBinderTests"/> cannot reach: that the rendered motion is
    /// <i>continuous</i> — that between two consecutive rendered frames a remote entity
    /// never steps backwards along its path, never stalls and then jumps, and never
    /// renders at a speed the server never moved it at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why these tests could not be written before.</b> The interpolation factor is
    /// <c>(now − arrival) / measuredInterval</c>. Continuity is a property of that curve
    /// <i>between</i> arrivals, so a test has to sample it at instants of its own
    /// choosing. Until <see cref="IViewClock"/> existed the clock was a private
    /// self-starting <c>Stopwatch</c>, so the only instant a test could sample was the one
    /// it executed at — which, immediately after handing the binder a snapshot, is
    /// <c>t ≈ 0</c>. That is the single point on the curve where continuity is trivially
    /// true no matter how discontinuous the curve is everywhere else, and it is why
    /// <see cref="WorldViewBinderTests"/> asserts <c>Is.LessThan(1f)</c> and nothing
    /// sharper. Its own fixture remark says so.
    /// </para>
    /// <para>
    /// <b>Arrival time and frame time are placed independently.</b>
    /// <see cref="Stream.Arrive"/> sets the clock and then delivers a snapshot;
    /// <see cref="Stream.Frame"/> sets the clock and delivers nothing. The binder stamps
    /// an arrival only on the pass that carries a new world tick and computes the render
    /// phase on every pass, so the two calls put arrivals and frames at chosen, unrelated
    /// instants on one timeline. This matters because the behaviour under test is
    /// precisely the <i>relationship</i> between the two: a single knob that advanced
    /// both together could not express an early or a late arrival at all.
    /// </para>
    /// <para>
    /// <b>The scenario.</b> One remote entity travelling in a straight line along +X at a
    /// constant server speed of one unit per world tick, so "rendered speed" is just
    /// <c>|Δx|</c> per unit of time and every irregularity in the rendered motion is an
    /// artefact of the interpolator rather than of the entity. Arrivals at the package's
    /// own world rate, 15 Hz — <see cref="NominalIntervalMs"/>, 66.7 ms — and frames every
    /// <see cref="FrameMs"/> = 5 ms, i.e. 200 fps, which puts ~13 frames inside each
    /// interval so the frame-delta series has the resolution to show a discontinuity.
    /// Each scenario warms up with four perfectly periodic arrivals before it perturbs
    /// anything, so the binder's interval EMA (α = 0.3, sanity-bounded to
    /// 1 ms &lt; measured &lt; 500 ms) has settled on 66.7 ms and the numbers a
    /// perturbation produces are attributable to the perturbation.
    /// </para>
    /// <para>
    /// <b>Three of these four are expected to fail on the current implementation</b>, and
    /// that is the point of writing them before the fix rather than after: a suite that
    /// went straight from "does not exist" to "green" would never have shown anyone the
    /// defect. <see cref="APerfectlyPeriodicStreamRendersSmoothly"/> is the control and
    /// must pass today — without it a failing suite is indistinguishable from a broken
    /// harness.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class RemoteInterpolationContinuityTests
    {
        private sealed class RecordingView : IEntityView
        {
            public readonly Dictionary<string, float[]> Positions = new Dictionary<string, float[]>();
            public readonly Dictionary<string, string> Types = new Dictionary<string, string>();
            public readonly List<string> SpawnedLocal = new List<string>();
            public readonly List<string> Despawned = new List<string>();

            public void Spawn(string id, bool isLocal, string type)
            {
                Types[id] = type;
                if (isLocal)
                {
                    SpawnedLocal.Add(id);
                }
            }

            public void Despawn(string id)
            {
                Despawned.Add(id);
                Positions.Remove(id);
            }

            public readonly Dictionary<string, int> Hp = new Dictionary<string, int>();

            public int SetStateCalls { get; private set; }

            public void SetState(string id, float x, float y, int hp, int maxHp)
            {
                SetStateCalls++;
                Positions[id] = new[] { x, y };
                Hp[id] = hp;
            }
        }

        /// <summary>A clock the test sets directly. Production uses <see cref="StopwatchViewClock"/>.</summary>
        private sealed class ManualClock : IViewClock
        {
            public double NowMs { get; set; }
        }

        private const string LocalId = "local-user";
        private const string RemoteId = "remote-user";

        /// <summary>The package's world rate, 15 Hz — the binder's own default interval.</summary>
        private const double NominalIntervalMs = 1000.0 / 15.0;

        /// <summary>5 ms per rendered frame: 200 fps, ~13 frames per snapshot interval.</summary>
        private const double FrameMs = 5.0;

        /// <summary>
        /// A step smaller than this is not motion, it is float noise in a position of
        /// order 1. Used only to keep an exactly-stationary frame from reading as a
        /// backwards one.
        /// </summary>
        private const double NoiseUnits = 1e-4;

        private static ResolvedSnapshot Keyframe(long tick, params ResolvedEntity[] entities) =>
            new ResolvedSnapshot(tick, 0L, true, entities, new string[0]);

        private static ResolvedEntity Entity(string id, float x, float y, string type = "player") =>
            new ResolvedEntity(id, type, x, y, 100, 100);

        /// <summary>One rendered frame: when it happened and where the entity was drawn.</summary>
        private struct Sample
        {
            public double TimeMs;
            public double X;
            public bool CarriedASnapshot;
        }

        /// <summary>
        /// Drives a binder over a timeline the test writes, recording where the remote
        /// entity was rendered on every frame.
        /// </summary>
        private sealed class Stream
        {
            private readonly ManualClock _clock = new ManualClock();
            private readonly RecordingView _view = new RecordingView();
            private readonly WorldState _world = new WorldState();
            private readonly WorldViewBinder _binder;
            private readonly List<Sample> _samples = new List<Sample>();
            private double _nextFrameMs = FrameMs;

            public Stream()
            {
                _binder = new WorldViewBinder(_view, null, _clock);
            }

            public IReadOnlyList<Sample> Samples => _samples;

            /// <summary>
            /// A snapshot arrives at <paramref name="atMs"/>. The clock is set first, so
            /// this is the instant the binder stamps as the arrival.
            /// </summary>
            public void Arrive(double atMs, long tick, double x)
            {
                _clock.NowMs = atMs;
                _world.Apply(Keyframe(tick, Entity(RemoteId, (float)x, 0f)));
                _binder.Tick(_world, LocalId);
                Record(atMs, true);

                // A frame landing on the same instant as an arrival would be a duplicate
                // sample with a zero-length step, not a second rendered frame.
                while (_nextFrameMs <= atMs)
                {
                    _nextFrameMs += FrameMs;
                }
            }

            /// <summary>
            /// A frame is rendered at <paramref name="atMs"/> with no snapshot. Only the
            /// clock moved, which is what makes frame time independent of arrival time.
            /// </summary>
            public void Frame(double atMs)
            {
                _clock.NowMs = atMs;
                _binder.Tick(_world, LocalId);
                Record(atMs, false);
            }

            /// <summary>Renders every 5 ms frame strictly before <paramref name="endMs"/>.</summary>
            public void FramesUntil(double endMs)
            {
                while (_nextFrameMs < endMs)
                {
                    Frame(_nextFrameMs);
                    _nextFrameMs += FrameMs;
                }
            }

            private void Record(double atMs, bool carriedASnapshot)
            {
                _samples.Add(new Sample
                {
                    TimeMs = atMs,
                    X = _view.Positions[RemoteId][0],
                    CarriedASnapshot = carriedASnapshot
                });
            }
        }

        /// <summary>
        /// Four perfectly periodic arrivals at 0, 66.7, 133.3 and 200 ms carrying ticks
        /// 1..4 at x = 0..3, with frames throughout. Leaves the stream positioned on the
        /// arrival at <c>3 × NominalIntervalMs</c>, with the interval EMA settled.
        /// </summary>
        private static Stream WarmedUp()
        {
            var stream = new Stream();
            for (var k = 0; k < 4; k++)
            {
                var at = k * NominalIntervalMs;
                stream.FramesUntil(at);
                stream.Arrive(at, k + 1, k);
            }

            return stream;
        }

        /// <summary>
        /// Frame-to-frame displacements, paired with the two instants they span, for
        /// every consecutive pair whose earlier frame is at or after
        /// <paramref name="fromMs"/> and before <paramref name="toMs"/>.
        /// </summary>
        private static List<(double Delta, double FromMs, double ToMs)> Steps(
            IReadOnlyList<Sample> samples, double fromMs, double toMs)
        {
            var steps = new List<(double Delta, double FromMs, double ToMs)>();
            for (var i = 1; i < samples.Count; i++)
            {
                var a = samples[i - 1];
                if (a.TimeMs < fromMs - 1e-6 || a.TimeMs >= toMs - 1e-6)
                {
                    continue;
                }

                steps.Add((samples[i].X - a.X, a.TimeMs, samples[i].TimeMs));
            }

            return steps;
        }

        private static double Median(List<(double Delta, double FromMs, double ToMs)> steps)
        {
            var values = new List<double>();
            for (var i = 0; i < steps.Count; i++)
            {
                values.Add(steps[i].Delta);
            }

            values.Sort();
            return values.Count % 2 == 1
                ? values[values.Count / 2]
                : 0.5 * (values[values.Count / 2 - 1] + values[values.Count / 2]);
        }

        /// <summary>
        /// Mean rendered speed in units per millisecond over the frames in
        /// <c>[fromMs, toMs)</c> — total displacement over elapsed time, which is the
        /// stable statistic. The instantaneous peak is what a player actually sees, but it
        /// is one frame wide and therefore noisier to assert on.
        /// </summary>
        private static double MeanSpeed(IReadOnlyList<Sample> samples, double fromMs, double toMs)
        {
            Sample? first = null;
            Sample last = default;
            for (var i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                if (s.TimeMs < fromMs - 1e-6 || s.TimeMs >= toMs - 1e-6)
                {
                    continue;
                }

                if (first == null)
                {
                    first = s;
                }

                last = s;
            }

            Assert.That(first, Is.Not.Null, "the measurement window contained no frames");
            var span = last.TimeMs - first.Value.TimeMs;
            Assert.That(span, Is.GreaterThan(0.0), "the measurement window spanned no time");
            return (last.X - first.Value.X) / span;
        }

        /// <summary>How many frames immediately before <paramref name="index"/> did not move at all.</summary>
        private static int StallLengthBefore(
            List<(double Delta, double FromMs, double ToMs)> steps, int index)
        {
            var n = 0;
            for (var i = index - 1; i >= 0 && Math.Abs(steps[i].Delta) <= NoiseUnits; i--)
            {
                n++;
            }

            return n;
        }

        // -------------------------------------------------------------------------
        // 1. Early arrival.
        //
        // EXPECTED TO FAIL on the current implementation, at
        //     Runtime/View/WorldViewBinder.cs:  _lastSnapshotTimeMs = nowMs;
        // read three lines later by
        //     double elapsed = nowMs - _lastSnapshotTimeMs;
        // Both run in the same Tick call, in that order, so on any pass carrying a new
        // snapshot `elapsed` is exactly 0 and t is exactly 0: the render phase is
        // discarded and restarted on every arrival, whatever the previous frame drew.
        //
        // WHAT WILL MAKE IT PASS (stage 3): a free-running render clock, so the phase
        // continues across an arrival instead of being reset by it.
        // -------------------------------------------------------------------------
        [Test]
        public void RenderedMotionStaysContinuousWhenASnapshotArrivesEarly()
        {
            var stream = WarmedUp();

            // Ordinary jitter, no packet loss: the next snapshot arrives at 75 % of the
            // interval. The frame before it had already rendered ~72 % of the way along
            // the previous segment.
            var early = 3 * NominalIntervalMs + 50.0;
            stream.FramesUntil(early);
            stream.Arrive(early, 5, 4);

            var after = early + NominalIntervalMs;
            stream.FramesUntil(after);
            stream.Arrive(after, 6, 5);
            stream.FramesUntil(after + NominalIntervalMs);

            var warmUp = Steps(stream.Samples, NominalIntervalMs, 3 * NominalIntervalMs);
            var median = Median(warmUp);
            var steps = Steps(stream.Samples, NominalIntervalMs, double.MaxValue);

            // Upper bound as well as lower, deliberately. An early arrival lurches
            // FORWARD — the shift to From = B, To = C with t reset to 0 renders B, which
            // is ahead of the A + 0.72·(B−A) the previous frame drew — so a test that
            // only asserted "never moves backwards" would pass here and leave the real
            // early-arrival discontinuity uncovered.
            //
            // 2.5x the median forward step allows for the EMA momentarily undershooting
            // the interval and for float noise; the defect produces a single step of
            // roughly 4.3x the median, so there is clear margin either side of the bound.
            var ceiling = 2.5 * median;
            for (var i = 0; i < steps.Count; i++)
            {
                var (delta, startMs, endMs) = steps[i];

                Assert.That(delta, Is.GreaterThanOrEqualTo(-NoiseUnits),
                    $"the rendered position stepped BACKWARDS by {-delta:F4} units between " +
                    $"the frames at {startMs:F1} ms and {endMs:F1} ms, while the entity was moving " +
                    $"forwards at a constant server speed throughout — on screen that is a " +
                    $"visible pop back. The median forward step over the periodic warm-up " +
                    $"was {median:F4} units.");

                Assert.That(delta, Is.LessThanOrEqualTo(ceiling),
                    $"the rendered position jumped FORWARD {delta:F4} units in a single " +
                    $"frame between {startMs:F1} ms and {endMs:F1} ms — {delta / median:F1}x the " +
                    $"median frame step of {median:F4} units, i.e. the entity covered about " +
                    $"{delta / median:F1} frames' worth of travel in one frame. That is a " +
                    $"visible lurch, and it is what the snapshot arriving " +
                    $"{NominalIntervalMs - 50.0:F1} ms early did to an interpolation phase " +
                    $"that restarts at 0 on every arrival.");
            }
        }

        // -------------------------------------------------------------------------
        // 2. Late arrival.
        //
        // EXPECTED TO FAIL on the current implementation. Same arrival stamp as test 1,
        // plus the extrapolation clamp
        //     Runtime/View/WorldViewBinder.cs:  if (t > 1.2f) t = 1.2f;
        // t runs to the cap, the entity freezes at B + 0.2·(B−A), and when the snapshot
        // finally lands t resets to 0 with From = B, so it renders at B — a step BACKWARDS
        // of 0.2 of a segment, bounded by the cap rather than by how late the snapshot was.
        //
        // WHAT WILL MAKE IT PASS (stage 3): a free-running render clock, so the phase
        // carries across the arrival and the extrapolated distance is absorbed rather than
        // undone.
        // -------------------------------------------------------------------------
        [Test]
        public void RenderedMotionStaysContinuousWhenASnapshotArrivesLate()
        {
            var stream = WarmedUp();

            // 95 ms is ~1.42x the interval — past the t = 1.2 cap, so the stall is entered
            // before the snapshot lands.
            var late = 3 * NominalIntervalMs + 95.0;
            stream.FramesUntil(late);
            stream.Arrive(late, 5, 4);

            var after = late + NominalIntervalMs;
            stream.FramesUntil(after);
            stream.Arrive(after, 6, 5);
            stream.FramesUntil(after + NominalIntervalMs);

            var warmUp = Steps(stream.Samples, NominalIntervalMs, 3 * NominalIntervalMs);
            var median = Median(warmUp);
            var steps = Steps(stream.Samples, NominalIntervalMs, double.MaxValue);

            var ceiling = 2.5 * median;
            for (var i = 0; i < steps.Count; i++)
            {
                var (delta, startMs, endMs) = steps[i];

                Assert.That(delta, Is.GreaterThanOrEqualTo(-NoiseUnits),
                    $"the rendered position stepped BACKWARDS by {-delta:F4} units between " +
                    $"the frames at {startMs:F1} ms and {endMs:F1} ms, after standing still for " +
                    $"{StallLengthBefore(steps, i)} frames — the stall-then-jump a player " +
                    $"reads as rubber-banding. The backward step is " +
                    $"{-delta / median:F1}x the median forward step of {median:F4} units, " +
                    $"and it is the extrapolated distance being undone rather than absorbed " +
                    $"when the late snapshot resets the interpolation phase to 0.");

                Assert.That(delta, Is.LessThanOrEqualTo(ceiling),
                    $"the rendered position jumped FORWARD {delta:F4} units in a single " +
                    $"frame between {startMs:F1} ms and {endMs:F1} ms — {delta / median:F1}x the " +
                    $"median frame step of {median:F4} units. A snapshot arriving 95 ms " +
                    $"after the previous one, against a measured interval of " +
                    $"{NominalIntervalMs:F1} ms, must not be paid for in one frame.");
            }
        }

        // -------------------------------------------------------------------------
        // 3. A skipped server tick.
        //
        // EXPECTED TO FAIL on the current implementation, at the interval EMA
        //     Runtime/View/WorldViewBinder.cs:
        //         double measured = nowMs - _lastSnapshotTimeMs;
        //         if (measured > 1.0 && measured < 500.0)
        //             _snapshotIntervalMs = _snapshotIntervalMs * 0.7 + measured * 0.3;
        // A doubled arrival gap moves the divisor only 30 % of the way — from 66.7 ms to
        // ~86.7 ms — while the numerator, the position delta, has genuinely doubled. Twice
        // the distance divided by a barely-changed interval renders at ~1.54x speed until
        // the t = 1.2 cap, then stalls.
        //
        // WHAT WILL MAKE IT PASS (stage 3): a free-running render clock driven by the
        // snapshots' own tick spacing, so a two-tick gap is rendered over two ticks' worth
        // of time rather than over one measured arrival interval.
        // -------------------------------------------------------------------------
        [Test]
        public void ASkippedServerTickDoesNotDoubleTheRenderedSpeed()
        {
            var stream = WarmedUp();

            // Tick 5's snapshot never arrives. Tick 6 turns up at its own natural time,
            // two intervals after tick 4, carrying twice the position delta. The entity
            // did not speed up: it covered 2 units in 2 ticks, exactly as before.
            var skipped = 3 * NominalIntervalMs + 2 * NominalIntervalMs;
            stream.FramesUntil(skipped);
            stream.Arrive(skipped, 6, 5);

            var next = skipped + NominalIntervalMs;
            stream.FramesUntil(next);
            stream.Arrive(next, 7, 6);
            stream.FramesUntil(next + NominalIntervalMs);

            // Reference: one ordinary warm-up interval, measured the same way.
            var reference = MeanSpeed(stream.Samples, 2 * NominalIntervalMs, 3 * NominalIntervalMs);

            // The interval in which the doubled position delta is actually rendered.
            var observed = MeanSpeed(stream.Samples, skipped, next);

            // ±35 % is far outside the couple-of-percent wobble the EMA and float error
            // produce on a periodic stream, and far inside what the defect produces
            // (~1.54x). Asserted on the mean rather than the peak because the mean is the
            // stabler statistic; the instantaneous peak, near 3x before the stall, is what
            // a player actually sees.
            var ratio = observed / reference;
            Assert.That(ratio, Is.InRange(0.65, 1.35),
                $"a remote entity whose server tick was skipped rendered at " +
                $"{ratio:F2}x its ordinary speed ({observed * 1000.0:F2} units/s against " +
                $"{reference * 1000.0:F2} units/s over the previous interval). The entity " +
                $"never changed speed on the server — it covered two units in two ticks, " +
                $"the same one unit per tick as always. What changed is that the binder " +
                $"divided a doubled position delta by an interval its EMA had moved only " +
                $"30 % of the way, so a dropped packet reads on screen as the entity " +
                $"sprinting and then freezing.");
        }

        // -------------------------------------------------------------------------
        // 4. The control.
        //
        // EXPECTED TO PASS on the current implementation, and it must keep passing after
        // stage 3. Without it a failing suite is indistinguishable from a broken harness:
        // if the clock seam, the frame pump or the sampling were wrong, this would fail
        // too, and the three failures above would prove nothing.
        //
        // Tighter than tests 1-3 (1.5x rather than 2.5x) on purpose. On a perfectly
        // periodic stream the only irregularity left is the frame that straddles an
        // arrival — 5 ms does not divide 66.7 ms evenly — which is bounded by one frame's
        // worth of phase error, so a bound that loose would make the control toothless.
        // -------------------------------------------------------------------------
        [Test]
        public void APerfectlyPeriodicStreamRendersSmoothly()
        {
            var stream = new Stream();
            for (var k = 0; k < 6; k++)
            {
                var at = k * NominalIntervalMs;
                stream.FramesUntil(at);
                stream.Arrive(at, k + 1, k);
            }

            stream.FramesUntil(6 * NominalIntervalMs);

            var warmUp = Steps(stream.Samples, NominalIntervalMs, 3 * NominalIntervalMs);
            var median = Median(warmUp);
            var steps = Steps(stream.Samples, NominalIntervalMs, double.MaxValue);

            var ceiling = 1.5 * median;
            for (var i = 0; i < steps.Count; i++)
            {
                var (delta, startMs, endMs) = steps[i];

                Assert.That(delta, Is.GreaterThanOrEqualTo(-NoiseUnits),
                    $"the rendered position stepped backwards by {-delta:F4} units between " +
                    $"the frames at {startMs:F1} ms and {endMs:F1} ms on a stream with no jitter " +
                    $"at all. Nothing in the interpolator should be able to do that here — " +
                    $"if this fails, the harness or the clock seam is wrong, not the " +
                    $"interpolator, and the other three tests in this fixture prove nothing.");

                Assert.That(delta, Is.LessThanOrEqualTo(ceiling),
                    $"the rendered position jumped {delta:F4} units in a single frame " +
                    $"between {startMs:F1} ms and {endMs:F1} ms — {delta / median:F1}x the median " +
                    $"step of {median:F4} units — on arrivals spaced exactly " +
                    $"{NominalIntervalMs:F1} ms apart. This is the control: a perfectly " +
                    $"periodic stream is the one case the current interpolator is expected " +
                    $"to render smoothly, so a failure here means the harness is broken.");
            }
        }
    }
}
