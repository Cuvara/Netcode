using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using Cuvara.Netcode.World;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// A snapshot older than one already applied, or a repeat of one, must change nothing: not the
    /// world's tick, not an entity's state, not what is drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gameplay transport is ordered (KCP), so the live client does not see a reordered
    /// snapshot today; the tick checks downstream (<c>InterpolationRing.Accepts</c>, the binder's
    /// rate gate) exist for the day it does. <see cref="WorldState"/> sits upstream of all of
    /// them, and what it merges is what every later pass reads.
    /// </para>
    /// <para>
    /// The damaging case is an entity that STOPS: once it stands still the server's deltas omit
    /// it, so whatever the world holds for it is what is drawn until the next keyframe (30
    /// snapshots, two seconds). A stale snapshot merged after the stop leaves it drawn at an old
    /// position for that whole time.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class SnapshotFreshnessTests
    {
        private const string LocalId = "local-user";
        private const string RemoteId = "remote-user";

        private static ResolvedSnapshot Delta(long tick, params ResolvedEntity[] entities) =>
            new ResolvedSnapshot(tick, 0L, false, entities, new string[0]);

        private static ResolvedSnapshot Keyframe(long tick, params ResolvedEntity[] entities) =>
            new ResolvedSnapshot(tick, 0L, true, entities, new string[0]);

        private static ResolvedEntity At(float x) => new ResolvedEntity(RemoteId, "player", x, 0f, 100, 100);

        [Test]
        public void ADuplicateSnapshot_ChangesNothing()
        {
            var world = new WorldState();
            world.Apply(Keyframe(100, At(1f)));
            world.Apply(Delta(104, At(2f)));

            world.Apply(Delta(104, At(2f)));

            Assert.That(world.Tick, Is.EqualTo(104L));
            Assert.That(world.Entities[RemoteId].X, Is.EqualTo(2f));
        }

        [Test]
        public void AnOlderSnapshot_DoesNotMoveTheTickBack()
        {
            var world = new WorldState();
            world.Apply(Keyframe(100, At(1f)));
            world.Apply(Delta(108, At(3f)));

            world.Apply(Delta(104, At(2f)));

            Assert.That(world.Tick, Is.EqualTo(108L));
        }

        [Test]
        public void AnOlderSnapshot_DoesNotOverwriteANewerEntityState()
        {
            var world = new WorldState();
            world.Apply(Keyframe(100, At(1f)));
            world.Apply(Delta(108, At(3f))); // the entity stops here; later deltas omit it

            world.Apply(Delta(104, At(2f))); // reordered: produced before the one above

            world.Apply(Delta(112));
            world.Apply(Delta(116));

            Assert.That(world.Entities[RemoteId].X, Is.EqualTo(3f),
                "the entity's newest state is tick 108's; tick 104's arrived late and must not replace it");
        }

        private sealed class RecordingView : IEntityView
        {
            public readonly Dictionary<string, float> X = new Dictionary<string, float>();
            public void Spawn(string id, bool isLocal, string type) { }
            public void Despawn(string id) => X.Remove(id);
            public void SetState(string id, float x, float y, int hp, int maxHp) => X[id] = x;
        }

        private sealed class ManualClock : IViewClock
        {
            public double NowMs { get; set; }
        }

        [Test]
        public void AnOlderSnapshot_DoesNotDrawAStoppedEntityAtAnOldPosition()
        {
            // End to end, 15 Hz snapshots four ticks apart and 5 ms frames: the entity walks to
            // x = 3 and stops, a snapshot from x = 2 arrives late, and the deltas that follow omit
            // the entity because it is standing still.
            var clock = new ManualClock();
            var view = new RecordingView();
            var world = new WorldState();
            var binder = new WorldViewBinder(view, null, clock);
            const double intervalMs = 1000.0 / 15.0;

            void Frames(double untilMs)
            {
                for (; clock.NowMs < untilMs; clock.NowMs += 5.0) binder.Tick(world, LocalId);
            }

            world.Apply(Keyframe(4, At(0f)));
            binder.Tick(world, LocalId);
            int k = 1;
            for (; k <= 12; k++)
            {
                Frames(k * intervalMs);
                world.Apply(Delta(4 + 4 * k, At(0.25f * k))); // walks to x = 3 at k = 12
                binder.Tick(world, LocalId);
            }

            Frames(k * intervalMs);
            world.Apply(Delta(4 + 4 * 8, At(2f))); // tick 36, already superseded by tick 52
            binder.Tick(world, LocalId);

            for (int end = k + 20; k < end; k++)
            {
                Frames(k * intervalMs);
                world.Apply(Delta(4 + 4 * k)); // standing still: the entity is omitted
                binder.Tick(world, LocalId);
            }

            Frames(k * intervalMs);
            Assert.That(view.X[RemoteId], Is.EqualTo(3f).Within(1e-3),
                "the entity stopped at x = 3; a late snapshot from x = 2 must not be what is drawn");
        }
    }
}
