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

        /// <summary>How far the entity travelled over the frames of the final interval.</summary>
        private static double Span(IReadOnlyList<double> rendered)
        {
            var n = rendered.Count;
            var window = (int)(NominalIntervalMs / FrameMs);
            return rendered[n - 1] - rendered[n - window];
        }
    }
}
