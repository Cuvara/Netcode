using System;
using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Interpolation;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using Cuvara.Netcode.World;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Remote interpolation through the binder in the two cases the existing fixtures leave open:
    /// an entity that never moves, and a teleport (respawn) after which the entity keeps moving.
    /// </summary>
    /// <remarks>
    /// Same stream as <see cref="StoppedEntityConvergesTests"/>: 15 Hz snapshots four ticks apart,
    /// 5 ms frames, a keyframe every 30 snapshots carrying every entity and deltas that omit an
    /// entity whose position did not change.
    /// </remarks>
    [TestFixture]
    public sealed class InterpolationScenarioTests
    {
        private const string LocalId = "local-user";
        private const string RemoteId = "remote-user";
        private const double IntervalMs = 1000.0 / 15.0;
        private const double FrameMs = 5.0;
        private const int KeyframeEvery = 30;

        private sealed class RecordingView : IEntityView
        {
            public readonly Dictionary<string, float[]> Positions = new Dictionary<string, float[]>();
            public void Spawn(string id, bool isLocal, string type) { }
            public void Despawn(string id) => Positions.Remove(id);
            public void SetState(string id, float x, float y, int hp, int maxHp) => Positions[id] = new[] { x, y };
        }

        private sealed class ManualClock : IViewClock
        {
            public double NowMs { get; set; }
        }

        private sealed class Stream
        {
            private readonly ManualClock _clock = new ManualClock();
            private readonly RecordingView _view = new RecordingView();
            private readonly WorldState _world = new WorldState();
            private readonly WorldViewBinder _binder;
            private double _next = FrameMs;
            private int _snapshots;
            private (double X, double Y) _last = (double.NaN, double.NaN);

            public readonly List<(double Ms, double X, double Y)> Rendered = new List<(double, double, double)>();

            public Stream(InterpolationConfig config) => _binder = new WorldViewBinder(_view, null, _clock, config);

            public void Snapshot(int k, double x, double y)
            {
                FramesUntil(k * IntervalMs);
                _clock.NowMs = k * IntervalMs;
                bool keyframe = _snapshots++ % KeyframeEvery == 0;
                bool changed = x != _last.X || y != _last.Y;
                _last = (x, y);
                var entities = changed || keyframe
                    ? new[] { new ResolvedEntity(RemoteId, "player", (float)x, (float)y, 100, 100) }
                    : new ResolvedEntity[0];
                _world.Apply(new ResolvedSnapshot((k + 1) * 4, 0L, keyframe, entities, new string[0]));
                _binder.Tick(_world, LocalId);
            }

            public void FramesUntil(double endMs)
            {
                for (var t = _next; t < endMs; t += FrameMs)
                {
                    _clock.NowMs = t;
                    _binder.Tick(_world, LocalId);
                    if (_view.Positions.TryGetValue(RemoteId, out var p)) Rendered.Add((t, p[0], p[1]));
                    _next = t + FrameMs;
                }
            }
        }

        [Test]
        public void AnEntityAtRest_IsDrawnExactlyWhereItStands_EveryFrame()
        {
            var s = new Stream(InterpolationConfig.Default);
            int k = 0;
            for (; k < 90; k++) s.Snapshot(k, 3.0, -2.0); // six seconds, three keyframes
            s.FramesUntil(k * IntervalMs + 1000.0); // and a second with nothing at all

            Assume.That(s.Rendered.Count, Is.GreaterThan(1000));
            foreach (var (ms, x, y) in s.Rendered)
            {
                Assert.That(x, Is.EqualTo(3.0).Within(1e-6), $"at {ms} ms");
                Assert.That(y, Is.EqualTo(-2.0).Within(1e-6), $"at {ms} ms");
            }
        }

        [Test]
        public void ATeleportFollowedByMotion_IsNeverDrawnPastTheNewPath()
        {
            // Walking toward -x at 5 u/s, respawned at the origin, standing there for a second
            // (the deltas omit it, so the render clock runs past its newest sample and the
            // teleport is the segment that would be extrapolated), then walking toward +y. After
            // the teleport the entity may be drawn on the old path, on the segment the teleport
            // spans, or on the new path; never beyond where the new path has got to, and never
            // carried along the teleport's own direction past the origin.
            var config = InterpolationConfig.Default;
            config.MaxExtrapolationSpeed = 15.0; // a game moving at 5 u/s sets it a few times above
            var s = new Stream(config);
            const double step = 5.0 / 15.0;
            int k = 0;
            for (; k < 30; k++) s.Snapshot(k, -10.0 - k * step, 0.0);

            int respawn = k;
            for (int i = 0; i < 15; i++, k++) s.Snapshot(k, 0.0, 0.0);      // a second at the origin
            for (int i = 0; i < 45; i++, k++) s.Snapshot(k, 0.0, i * step); // then +y
            s.FramesUntil(k * IntervalMs + 500.0);

            double newestY = 44 * step;
            double respawnMs = respawn * IntervalMs;
            double worstPastOrigin = 0, worstAhead = 0;
            foreach (var (ms, x, y) in s.Rendered)
            {
                if (ms < respawnMs) continue;
                worstPastOrigin = Math.Max(worstPastOrigin, x);   // the teleport moved +x to 0
                worstAhead = Math.Max(worstAhead, y - newestY);
            }

            var last = s.Rendered[s.Rendered.Count - 1];
            TestContext.WriteLine($"past origin {worstPastOrigin:F4}, ahead of newest {worstAhead:F4}, last ({last.X:F3}, {last.Y:F3})");
            Assert.That(worstPastOrigin, Is.LessThan(1e-3),
                "the teleport's velocity must not carry the entity past where it landed");
            Assert.That(worstAhead, Is.LessThanOrEqualTo(5.0 * config.MaxExtrapolation + 1e-3),
                "beyond the newest sample by at most the extrapolation cap");
            Assert.That(last.X, Is.EqualTo(0.0).Within(1e-3));
            Assert.That(last.Y, Is.EqualTo(newestY).Within(1e-3), "and it settles onto the newest sample");
        }
    }
}
