using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using Cuvara.Netcode.World;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// A snapshot that does not mention an entity must not be rendered as "it stopped".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bug.</b> <see cref="WorldViewBinder"/> iterates every entity the client
    /// holds, not the entities the snapshot carried — a delta names only what changed and
    /// the merged world keeps the rest at its last value. It then pushed an interpolation
    /// sample for all of them, so an entity the delta omitted got a manufactured sample:
    /// new tick, old position. That is a positive assertion that the entity was still
    /// there at that tick, so the evaluator interpolated between two identical points,
    /// rendered it frozen for the interval, and jumped when the real update landed.
    /// </para>
    /// <para>
    /// <b>Why it only started mattering.</b> While "absent from a delta" could only mean
    /// "unchanged", a duplicate sample was true. The server's replication schedule
    /// (rpg-mmo-server ADR-27) made absence mean "unchanged OR withheld", and nothing on
    /// this side was told — the classic shape where a wire convention changes meaning and
    /// every test on both sides keeps passing because each side is self-consistent. Three
    /// people playing saw mobs walk in visible steps while players moved smoothly.
    /// </para>
    /// <para>
    /// <b>Two arms.</b> "The entity kept moving" proves nothing on its own — it is also
    /// true of a stream with no gap in it at all. <see cref="Uninterrupted"/> establishes
    /// what a continuous stream renders, and the withheld run is then compared against it.
    /// </para>
    /// </remarks>
    public class WithheldEntityDoesNotFreezeTests
    {
        private const string LocalId = "local-user";
        private const string RemoteId = "remote-user";
        private const double NominalIntervalMs = 1000.0 / 15.0;
        private const double FrameMs = 5.0;

        private sealed class RecordingView : IEntityView
        {
            public readonly Dictionary<string, float[]> Positions = new Dictionary<string, float[]>();

            public void Spawn(string id, bool isLocal, string type) { }
            public void Despawn(string id) => Positions.Remove(id);
            public void SetState(string id, float x, float y, int hp, int maxHp) =>
                Positions[id] = new[] { x, y };
        }

        private sealed class ManualClock : IViewClock
        {
            public double NowMs { get; set; }
        }

        private static ResolvedEntity Entity(string id, float x) =>
            new ResolvedEntity(id, "enemy", x, 0f, 100, 100);

        private static ResolvedSnapshot Keyframe(long tick, params ResolvedEntity[] e) =>
            new ResolvedSnapshot(tick, 0L, true, e, new string[0]);

        /// <summary>A delta that mentions nothing: the shape a withheld entity produces.</summary>
        private static ResolvedSnapshot EmptyDelta(long tick) =>
            new ResolvedSnapshot(tick, 0L, false, new ResolvedEntity[0], new string[0]);

        private sealed class Stream
        {
            private readonly ManualClock _clock = new ManualClock();
            private readonly RecordingView _view = new RecordingView();
            private readonly WorldState _world = new WorldState();
            private readonly WorldViewBinder _binder;
            private readonly List<double> _rendered = new List<double>();

            public Stream() => _binder = new WorldViewBinder(_view, null, _clock);

            public IReadOnlyList<double> Rendered => _rendered;

            public void Arrive(double atMs, long tick, double x)
            {
                _clock.NowMs = atMs;
                _world.Apply(Keyframe(tick, Entity(RemoteId, (float)x)));
                _binder.Tick(_world, LocalId);
            }

            /// <summary>A snapshot lands on time and says nothing about the entity.</summary>
            public void ArriveWithheld(double atMs, long tick)
            {
                _clock.NowMs = atMs;
                _world.Apply(EmptyDelta(tick));
                _binder.Tick(_world, LocalId);
            }

            public void FramesUntil(double endMs)
            {
                for (var t = _next; t < endMs; t += FrameMs)
                {
                    _clock.NowMs = t;
                    _binder.Tick(_world, LocalId);
                    _rendered.Add(_view.Positions[RemoteId][0]);
                    _next = t + FrameMs;
                }
            }

            private double _next = FrameMs;
        }

        /// <summary>
        /// Four arrivals at 15Hz moving one unit each, so the interval and the direction
        /// are both established before the run under test.
        /// </summary>
        private static Stream WarmedUp()
        {
            var s = new Stream();
            for (var k = 0; k < 4; k++)
            {
                var at = k * NominalIntervalMs;
                s.FramesUntil(at);
                s.Arrive(at, (k + 1) * 4, k);
            }

            return s;
        }

        /// <summary>The control: the stream simply continues. This is what "moving" looks like.</summary>
        private static IReadOnlyList<double> Uninterrupted()
        {
            var s = WarmedUp();
            s.FramesUntil(4 * NominalIntervalMs);
            s.Arrive(4 * NominalIntervalMs, 20, 4);
            s.FramesUntil(5 * NominalIntervalMs);
            return s.Rendered;
        }

        [Test]
        public void ASnapshotThatOmitsTheEntityDoesNotRenderItAsStopped()
        {
            var control = Uninterrupted();

            var s = WarmedUp();
            s.FramesUntil(4 * NominalIntervalMs);
            s.ArriveWithheld(4 * NominalIntervalMs, 20);   // on time, carries nothing
            s.FramesUntil(5 * NominalIntervalMs);
            var withheld = s.Rendered;

            var controlSpan = Span(control);
            var withheldSpan = Span(withheld);

            TestContext.WriteLine($"control moved {controlSpan:F4}, withheld moved {withheldSpan:F4}");

            Assert.That(controlSpan, Is.GreaterThan(0.1),
                "the control arm did not move either, so this fixture is measuring nothing " +
                "and the assertion below would pass on a binder that renders everything frozen");

            Assert.That(withheldSpan, Is.GreaterThan(controlSpan * 0.25),
                $"the entity advanced {withheldSpan:F4} across a withheld snapshot against " +
                $"{controlSpan:F4} when the snapshot carried it — it was rendered as having " +
                "stopped, which is the freeze-then-jump a manufactured sample produces");
        }

        /// <summary>
        /// The sustained case, which is the one the server's 133ms band actually produces:
        /// every second snapshot withholds the entity, for as long as it stays in that band.
        /// </summary>
        /// <remarks>
        /// A single gap is covered by extrapolation and disappears. A repeating gap is a
        /// different question, and the arithmetic says so: snapshots are 66.7ms apart, the
        /// entity is carried on every second one, so the real gap is 133ms — against
        /// <see cref="InterpolationConfig.MaxExtrapolation"/> of 50ms. If the render clock
        /// sits far enough behind, the buffered pair still brackets it and nothing is
        /// extrapolated at all; if it does not, the entity extrapolates for 50ms and then
        /// holds. Which of those happens is not something to reason about — it is
        /// <c>TargetDelay</c> against the gap, measured here.
        ///
        /// The assertion is on the WORST rendered interval, not the total: a run can travel
        /// exactly as far as the control while doing it in visible lurches, and the total
        /// is blind to precisely the thing a player sees.
        /// </remarks>
        [Test]
        public void EverySecondSnapshotWithheld_StillRendersAtAnEvenSpeed()
        {
            var control = new Stream();
            for (var k = 0; k < 12; k++)
            {
                var at = k * NominalIntervalMs;
                control.FramesUntil(at);
                control.Arrive(at, (k + 1) * 4, k);
            }

            control.FramesUntil(12 * NominalIntervalMs);

            var alternating = new Stream();
            for (var k = 0; k < 12; k++)
            {
                var at = k * NominalIntervalMs;
                alternating.FramesUntil(at);
                if (k % 2 == 0)
                {
                    alternating.Arrive(at, (k + 1) * 4, k);
                }
                else
                {
                    // On time, carrying nothing — the entity is in a slower band.
                    alternating.ArriveWithheld(at, (k + 1) * 4);
                }
            }

            alternating.FramesUntil(12 * NominalIntervalMs);

            var controlWorst = WorstFrameStep(control.Rendered);
            var controlTypical = TypicalFrameStep(control.Rendered);
            var altWorst = WorstFrameStep(alternating.Rendered);
            var altTypical = TypicalFrameStep(alternating.Rendered);

            TestContext.WriteLine(
                $"control: typical step {controlTypical:F5}, worst {controlWorst:F5}");
            TestContext.WriteLine(
                $"alternating: typical step {altTypical:F5}, worst {altWorst:F5}");

            Assert.That(controlTypical, Is.GreaterThan(0.0),
                "the control arm rendered no motion, so nothing below measures anything");

            // The assertion is on the WORST step, and that direction is load-bearing. The
            // first version of this test asserted the typical step was not too SMALL, and
            // PASSED against the unfixed binder: manufactured samples do not mostly stall,
            // they render half the frames at roughly double speed, so the median goes UP
            // (0.10326 against the control's 0.07268) and a floor never fires. What a
            // player sees is the lurch, and the lurch is the maximum.
            Assert.That(altWorst, Is.LessThan(controlWorst * 1.25),
                $"the alternating arm's worst frame step is {altWorst:F5} against the " +
                $"control's {controlWorst:F5} — the entity is being rendered in lurches, " +
                "which is what a manufactured sample produces once the gap repeats");
        }

        /// <summary>The largest single-frame displacement: the 'jump' of stall-then-jump.</summary>
        private static double WorstFrameStep(IReadOnlyList<double> rendered)
        {
            var worst = 0.0;
            for (var i = rendered.Count / 2; i < rendered.Count; i++)
            {
                var d = rendered[i] - rendered[i - 1];
                if (d > worst) worst = d;
            }

            return worst;
        }

        /// <summary>The median single-frame displacement over the second half of the run.</summary>
        private static double TypicalFrameStep(IReadOnlyList<double> rendered)
        {
            var steps = new List<double>();
            for (var i = rendered.Count / 2; i < rendered.Count; i++)
            {
                steps.Add(rendered[i] - rendered[i - 1]);
            }

            steps.Sort();
            return steps[steps.Count / 2];
        }

        /// <summary>How far the entity travelled over the frames of the final interval.</summary>
        private static double Span(IReadOnlyList<double> rendered)
        {
            var n = rendered.Count;
            var window = (int)(NominalIntervalMs / FrameMs);
            return rendered[n - 1] - rendered[n - window];
        }
    }
}
