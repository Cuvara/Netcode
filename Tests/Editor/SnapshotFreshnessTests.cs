using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Protocol.Messages;
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
            Assert.That(world.Deltas, Is.EqualTo(1), "the repeat must not be merged");
            Assert.That(world.StaleSnapshotsDropped, Is.EqualTo(1));
        }

        [Test]
        public void ASecondSnapshotAtTheMergedTick_DoesNotOverwriteItsEntityState()
        {
            // The server stages at most one snapshot per connection per world tick, so a second
            // one at the tick already merged is a replay, and a replay carries an older view.
            var world = new WorldState();
            world.Apply(Keyframe(100, At(1f)));
            world.Apply(Delta(104, At(3f))); // the entity stops here; later deltas omit it

            world.Apply(Delta(104, At(2f)));

            world.Apply(Delta(108));
            Assert.That(world.Tick, Is.EqualTo(108L));
            Assert.That(world.Entities[RemoteId].X, Is.EqualTo(3f),
                "tick 104 was already merged; a second snapshot at tick 104 must not replace it");
            Assert.That(world.StaleSnapshotsDropped, Is.EqualTo(1));
        }

        [Test]
        public void ARepeatedKeyframeAtTheMergedTick_IsDroppedAndCounted()
        {
            var world = new WorldState();
            world.Apply(Keyframe(100, At(1f)));

            world.Apply(Keyframe(100, At(5f)));

            Assert.That(world.Entities[RemoteId].X, Is.EqualTo(1f));
            Assert.That(world.Keyframes, Is.EqualTo(1));
            Assert.That(world.StaleSnapshotsDropped, Is.EqualTo(1));
        }

        [Test]
        public void TheFirstSnapshot_IsAppliedEvenAtTickZero()
        {
            // "Already merged" is tracked, not inferred from Tick > 0: a fresh world, and a
            // reset one, start at tick zero and must still take a first snapshot there.
            var world = new WorldState();
            world.Apply(Keyframe(0, At(1f)));
            Assert.That(world.Entities[RemoteId].X, Is.EqualTo(1f));

            world.Reset();
            world.Apply(Keyframe(0, At(4f)));

            Assert.That(world.Entities[RemoteId].X, Is.EqualTo(4f));
            Assert.That(world.StaleSnapshotsDropped, Is.Zero);
        }

        private static SnapshotMessage WireSnapshot(long tick, bool full, params EntitySnapshot[] entities)
        {
            var m = new SnapshotMessage { Tick = tick, AckTick = 0, Full = full };
            foreach (var e in entities) m.Entities.Add(e);
            return m;
        }

        private static EntitySnapshot Introduce(uint handle, float x) =>
            new EntitySnapshot { Id = RemoteId, Handle = handle, Type = "player", X = x, Hp = 100, MaxHp = 100 };

        private static EntitySnapshot Interned(uint handle, float x) =>
            new EntitySnapshot { Id = string.Empty, Handle = handle, Type = "player", X = x, Hp = 100, MaxHp = 100 };

        // GameSessionClient's order: resolve, and merge only what resolved.
        private static bool Receive(SnapshotResolver resolver, WorldState world, SnapshotMessage message)
        {
            if (!resolver.TryResolve(message, out var resolved)) return false;
            world.Apply(resolved);
            return true;
        }

        [TestCase(108L)]
        [TestCase(112L)]
        public void TheResyncKeyframe_AfterAnAbortedSnapshot_IsApplied(long keyframeTick)
        {
            // An unknown handle aborts the whole snapshot before it reaches the world, so the
            // world's tick stays at the last merged one and the keyframe answering the resync is
            // newer than it. The server sends that keyframe at a later tick; the aborted tick is
            // covered too, so the guard cannot reject the repair on either.
            var resolver = new SnapshotResolver();
            var world = new WorldState();
            Assert.That(Receive(resolver, world, WireSnapshot(100, true, Introduce(1, 1f))), Is.True);
            Assert.That(Receive(resolver, world, WireSnapshot(104, false, Interned(1, 2f))), Is.True);

            Assert.That(Receive(resolver, world, WireSnapshot(108, false, Interned(7, 9f))), Is.False,
                "handle 7 was never introduced, so this snapshot must abort");
            Assert.That(world.Tick, Is.EqualTo(104L));

            Assert.That(Receive(resolver, world, WireSnapshot(keyframeTick, true, Introduce(1, 3f))), Is.True);

            Assert.That(world.Tick, Is.EqualTo(keyframeTick));
            Assert.That(world.Entities[RemoteId].X, Is.EqualTo(3f));
            Assert.That(world.StaleSnapshotsDropped, Is.Zero);
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

        [Test]
        public void AnOlderSnapshot_IsCountedAsDropped()
        {
            var world = new WorldState();
            world.Apply(Keyframe(100, At(1f)));
            world.Apply(Delta(108, At(3f)));

            world.Apply(Delta(104, At(2f)));

            Assert.That(world.StaleSnapshotsDropped, Is.EqualTo(1));
        }

        [Test]
        public void AfterAReset_ANewSessionOnALowerTickLine_IsApplied()
        {
            // The guard must not freeze a reconnect to a server whose ticks restart lower:
            // Reset returns the tick to zero, so the new session's first keyframe is newer.
            var world = new WorldState();
            world.Apply(Keyframe(100_000, At(1f)));
            world.Apply(Delta(100_004, At(2f)));

            world.Reset();
            world.Apply(Keyframe(500, At(7f)));
            world.Apply(Delta(504, At(8f)));

            Assert.That(world.Tick, Is.EqualTo(504L));
            Assert.That(world.Entities[RemoteId].X, Is.EqualTo(8f));
            Assert.That(world.StaleSnapshotsDropped, Is.Zero);
        }

        [Test]
        public void WithoutAReset_ANewSessionOnALowerTickLine_IsDroppedAndCounted()
        {
            // The contract the guard relies on, pinned: a new session goes into a reset world.
            // NetworkClient resets its World at the start of every connect and reconnect.
            var world = new WorldState();
            world.Apply(Keyframe(100_000, At(1f)));

            world.Apply(Keyframe(500, At(7f)));

            Assert.That(world.Entities[RemoteId].X, Is.EqualTo(1f));
            Assert.That(world.StaleSnapshotsDropped, Is.EqualTo(1));
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
