using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Interpolation;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using Cuvara.Netcode.World;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// <see cref="InterpolationConfig.DeferUntilBracketed"/> — withholding a remote entity
    /// while its buffer is too thin to bracket the render instant, instead of drawing it
    /// frozen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The render clock runs <c>TargetDelay</c> behind, so a newly seen entity holds at its
    /// single sample for that long. It is drawn motionless and then visibly starts moving.
    /// Measured live: 38.2% of the frames in an entity's first 0.25s rendered zero
    /// displacement, against 0.42% once established.
    /// </para>
    /// <para>
    /// <b>Two arms, and the off arm is the one that makes this meaningful.</b> "The entity
    /// never froze" is also true of a test whose entity never moved, or which sampled after
    /// the freeze was over. The off arm establishes that this fixture reproduces the freeze
    /// at all; only then does the on arm's absence of one mean anything.
    /// </para>
    /// </remarks>
    public class DeferUntilBracketedTests
    {
        private const string LocalId = "local-user";
        private const string RemoteId = "remote-user";
        private const double IntervalMs = 1000.0 / 15.0;
        private const double FrameMs = 5.0;

        private sealed class RecordingView : IEntityView
        {
            public readonly Dictionary<string, float[]> Positions = new Dictionary<string, float[]>();
            public readonly List<string> Spawned = new List<string>();

            public void Spawn(string id, bool isLocal, string type) => Spawned.Add(id);
            public void Despawn(string id) => Positions.Remove(id);
            public void SetState(string id, float x, float y, int hp, int maxHp) =>
                Positions[id] = new[] { x, y };
        }

        private sealed class ManualClock : IViewClock
        {
            public double NowMs { get; set; }
        }

        private static ResolvedSnapshot Keyframe(long tick, float x) =>
            new ResolvedSnapshot(tick, 0L, true,
                new[] { new ResolvedEntity(RemoteId, "mob", x, 0f, 100, 100) },
                new string[0]);

        /// <summary>
        /// Drives one entity moving one unit per 15Hz snapshot and records, per rendered
        /// frame, whether it was visible and how far it moved since the previous frame.
        /// </summary>
        private static List<(bool visible, double step)> Run(bool defer, int snapshots)
        {
            var clock = new ManualClock();
            var view = new RecordingView();
            var world = new WorldState();
            var config = InterpolationConfig.Default;
            config.DeferUntilBracketed = defer;
            var binder = new WorldViewBinder(view, null, clock, config);

            var samples = new List<(bool, double)>();
            double? lastX = null;
            double nextFrame = 0;

            for (var k = 0; k < snapshots; k++)
            {
                var at = k * IntervalMs;
                clock.NowMs = at;
                world.Apply(Keyframe((k + 1) * 4, k));
                binder.Tick(world, LocalId);

                for (var t = nextFrame; t < at + IntervalMs; t += FrameMs)
                {
                    clock.NowMs = t;
                    binder.Tick(world, LocalId);

                    if (view.Positions.TryGetValue(RemoteId, out var p))
                    {
                        samples.Add((true, lastX.HasValue ? p[0] - lastX.Value : 0.0));
                        lastX = p[0];
                    }
                    else
                    {
                        samples.Add((false, 0.0));
                    }

                    nextFrame = t + FrameMs;
                }
            }

            return samples;
        }

        [Test]
        public void Off_ReproducesTheFreeze_On_RemovesIt()
        {
            var off = Run(defer: false, snapshots: 6);
            var on = Run(defer: true, snapshots: 6);

            // Frames where the entity was visible but did not move. Skip the very first
            // visible frame of each run: it has no predecessor to have moved from.
            int FrozenVisible(List<(bool visible, double step)> s)
            {
                var n = 0;
                var seen = false;
                foreach (var (visible, step) in s)
                {
                    if (!visible) continue;
                    if (!seen) { seen = true; continue; }
                    if (step == 0.0) n++;
                }

                return n;
            }

            var frozenOff = FrozenVisible(off);
            var frozenOn = FrozenVisible(on);

            TestContext.WriteLine($"off: frozen-visible frames = {frozenOff}");
            TestContext.WriteLine($"on : frozen-visible frames = {frozenOn}");

            Assert.That(frozenOff, Is.GreaterThan(0),
                "the off arm showed no frozen frames, so this fixture does not reproduce " +
                "the artefact and the on arm below proves nothing");

            Assert.That(frozenOn, Is.LessThan(frozenOff),
                $"deferring left {frozenOn} frozen frames against {frozenOff} without it");
        }

        [Test]
        public void On_TheEntityStillAppears_AndDoesSoWithinTheBudget()
        {
            var on = Run(defer: true, snapshots: 6);

            var firstVisible = on.FindIndex(s => s.visible);
            Assert.That(firstVisible, Is.GreaterThanOrEqualTo(0),
                "the entity never appeared at all — deferral must not become a disappearance");

            // HoldDeferBudget is 0.15s; at 5ms frames that is 30 frames, and the entity
            // should appear well inside it once samples are flowing.
            Assert.That(firstVisible * FrameMs, Is.LessThanOrEqualTo(200.0),
                "the entity appeared later than the defer budget allows");
        }

        /// <summary>
        /// The bound that stops the deferral becoming a disappearance: an entity sent once
        /// and never again never acquires a second sample, so the hold condition cannot
        /// clear on its own.
        /// </summary>
        [Test]
        public void On_AnEntitySentOnlyOnce_StillAppearsAfterTheBudget()
        {
            var clock = new ManualClock();
            var view = new RecordingView();
            var world = new WorldState();
            var config = InterpolationConfig.Default;
            config.DeferUntilBracketed = true;
            var binder = new WorldViewBinder(view, null, clock, config);

            clock.NowMs = 0;
            world.Apply(Keyframe(4, 0f));
            binder.Tick(world, LocalId);

            Assert.That(view.Positions.ContainsKey(RemoteId), Is.False,
                "a single sample cannot bracket the render instant, so nothing should show yet");

            // No further snapshots. Only the clock moves.
            for (var t = FrameMs; t <= 400.0; t += FrameMs)
            {
                clock.NowMs = t;
                binder.Tick(world, LocalId);
            }

            Assert.That(view.Positions.ContainsKey(RemoteId), Is.True,
                "an entity that is never sent again must still be rendered once the budget " +
                "expires, or the deferral has silently deleted it");
        }
    }
}
