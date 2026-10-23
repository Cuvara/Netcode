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
    /// A remote entity that stops - or is teleported (a respawn) and then stays - must be
    /// rendered where the server says it is, not where the last motion would have carried it.
    /// </summary>
    /// <remarks>
    /// <para><b>Found by the rpg-factory testlab</b> (scenario S05, three rendered clients):
    /// after a remote player respawned from x = -14.7 to x = 0 and stood still, an observer
    /// rendered it at x = +5.1 for ten seconds while its received position was (0, 0); and
    /// every enemy that stopped at its target was drawn a constant 0.125 units past it
    /// (2.5 u/s x the 50 ms extrapolation cap).</para>
    ///
    /// <para>The binder deliberately pushes no sample for an entity whose position did not
    /// change (a delta may withhold a moving entity, see WithheldEntityDoesNotFreezeTests), so
    /// once an entity stops, the render clock runs past its newest sample for good and the
    /// evaluator extrapolates the last segment - capped, but held at the cap forever.</para>
    ///
    /// <para>Same harness as <see cref="WithheldEntityDoesNotFreezeTests"/>: 15 Hz snapshots,
    /// 4 server ticks apart, 5 ms render frames, a keyframe every 30 snapshots carrying every
    /// entity (the server's default keyframe interval), empty deltas in between for an entity
    /// that does not change.</para>
    /// </remarks>
    public class StoppedEntityConvergesTests
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
            public void SetState(string id, float x, float y, int hp, int maxHp) =>
                Positions[id] = new[] { x, y };
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

            public readonly List<(double Ms, double X)> Rendered = new List<(double, double)>();

            public Stream(InterpolationConfig config) => _binder = new WorldViewBinder(_view, null, _clock, config);

            /// <summary>Snapshot k at k * interval: the entity at x if it changed, else an empty delta.</summary>
            public void Snapshot(int k, double x, bool changed)
            {
                FramesUntil(k * IntervalMs);
                _clock.NowMs = k * IntervalMs;
                long tick = (k + 1) * 4;
                bool keyframe = _snapshots++ % KeyframeEvery == 0;
                var entities = changed || keyframe
                    ? new[] { new ResolvedEntity(RemoteId, "enemy", (float)x, 0f, 100, 100) }
                    : new ResolvedEntity[0];
                _world.Apply(new ResolvedSnapshot(tick, 0L, keyframe, entities, new string[0]));
                _binder.Tick(_world, LocalId);
            }

            public void FramesUntil(double endMs)
            {
                for (var t = _next; t < endMs; t += FrameMs)
                {
                    _clock.NowMs = t;
                    _binder.Tick(_world, LocalId);
                    if (_view.Positions.TryGetValue(RemoteId, out var p))
                    {
                        Rendered.Add((t, p[0]));
                    }

                    _next = t + FrameMs;
                }
            }
        }

        private static InterpolationConfig Config => InterpolationConfig.Default;

        [Test]
        public void AnEntityThatStops_IsRenderedAtItsStopPosition_NotPastIt()
        {
            var s = new Stream(Config);
            const double step = 2.5 / 15.0; // an enemy at 2.5 u/s
            int k = 0;
            for (; k < 40; k++)
            {
                s.Snapshot(k, k * step, changed: true);
            }

            double stop = (k - 1) * step;
            for (int end = k + 45; k < end; k++)
            {
                s.Snapshot(k, stop, changed: false); // three seconds standing still
            }

            s.FramesUntil(k * IntervalMs);
            double stopMs = 40 * IntervalMs;
            double last = s.Rendered[s.Rendered.Count - 1].X;
            double worstPast = 0;
            foreach (var (ms, x) in s.Rendered)
            {
                if (ms > stopMs + 1000.0)
                {
                    worstPast = Math.Max(worstPast, x - stop);
                }
            }

            TestContext.WriteLine($"stop {stop:F4}, last rendered {last:F4}, worst past the stop after 1 s {worstPast:F4}");
            Assert.That(last, Is.EqualTo(stop).Within(1e-3),
                $"the entity stopped at {stop:F4} and is still rendered at {last:F4} three seconds later");
            Assert.That(worstPast, Is.LessThan(1e-3),
                "a second after stopping the entity must not be drawn past where it stopped");
        }

        [Test]
        public void AnEntityTeleportedThenStill_IsNeverDrawnBeyondTheDestination()
        {
            // The guard is opt-in (the evaluator cannot know the game's speeds); a game that
            // moves at 5 u/s sets it a few times above that.
            var config = Config;
            config.MaxExtrapolationSpeed = 15.0;
            var s = new Stream(config);
            const double step = 5.0 / 15.0; // a player at 5 u/s walking toward -x
            int k = 0;
            for (; k < 44; k++)
            {
                s.Snapshot(k, -k * step, changed: true);
            }

            double dead = -(k - 1) * step;
            for (int end = k + 45; k < end; k++)
            {
                s.Snapshot(k, dead, changed: false); // dead, three seconds
            }

            int respawn = k;
            s.Snapshot(k++, 0.0, changed: true); // respawn at the origin
            for (int end = k + 60; k < end; k++)
            {
                s.Snapshot(k, 0.0, changed: false);
            }

            s.FramesUntil(k * IntervalMs);
            double respawnMs = respawn * IntervalMs;
            double beyond = 0;
            double last = s.Rendered[s.Rendered.Count - 1].X;
            foreach (var (ms, x) in s.Rendered)
            {
                if (ms >= respawnMs)
                {
                    beyond = Math.Max(beyond, x);
                }
            }

            TestContext.WriteLine($"teleport {dead:F2} -> 0, furthest drawn beyond 0: {beyond:F4}, last {last:F4}");
            Assert.That(beyond, Is.LessThan(0.05),
                $"after a teleport to 0 the entity was drawn at x = {beyond:F3}, past the destination; " +
                "a teleport is not motion and must not be extrapolated");
            Assert.That(last, Is.EqualTo(0.0).Within(1e-3));
        }
    }
}
