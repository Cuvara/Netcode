using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.World;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Covers the adapter between the wire-facing <see cref="ResolvedSnapshot"/> and
    /// the shared <c>SnapshotMerger</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. The merge rule itself is the server's code and is tested
    /// on the server; what can break here is the conversion — a dropped removal
    /// list, an off-by-one in the entity copy, a tick that goes backwards. Testing
    /// the merge rule again from this side would just be a second, weaker copy of a
    /// test that already exists.
    /// </remarks>
    [TestFixture]
    public sealed class WorldStateTests
    {
        private static ResolvedSnapshot Keyframe(long tick, params ResolvedEntity[] entities) =>
            new ResolvedSnapshot(tick, 0L, true, entities, new string[0]);

        private static ResolvedSnapshot Delta(long tick, long ackTick,
            IReadOnlyList<ResolvedEntity> entities, IReadOnlyList<string> removed) =>
            new ResolvedSnapshot(tick, ackTick, false, entities, removed);

        private static ResolvedEntity Entity(string id, float x, float y, int hp = 100) =>
            new ResolvedEntity(id, "player", x, y, hp, 100);

        [Test]
        public void KeyframeReplacesTheEntitySet()
        {
            var world = new WorldState();

            world.Apply(Keyframe(1L, Entity("a", 1f, 2f), Entity("b", 3f, 4f)));
            Assert.AreEqual(2, world.Count);

            // A keyframe is the complete AOI set: 'b' is absent, so it must go.
            world.Apply(Keyframe(2L, Entity("a", 5f, 6f)));
            Assert.AreEqual(1, world.Count);
            Assert.IsTrue(world.TryGet("a", out var a));
            Assert.AreEqual(5f, a.X);
            Assert.IsFalse(world.TryGet("b", out _));
            Assert.AreEqual(2, world.Keyframes);
        }

        [Test]
        public void DeltaUpsertsCarriedEntitiesAndAppliesRemovals()
        {
            var world = new WorldState();
            world.Apply(Keyframe(1L, Entity("a", 1f, 1f), Entity("b", 2f, 2f)));

            world.Apply(Delta(2L, 7L,
                new[] { Entity("a", 9f, 9f), Entity("c", 3f, 3f) },
                new[] { "b" }));

            Assert.AreEqual(2, world.Count);
            Assert.IsTrue(world.TryGet("a", out var a));
            Assert.AreEqual(9f, a.X);
            Assert.IsTrue(world.TryGet("c", out _));
            Assert.IsFalse(world.TryGet("b", out _));
            Assert.AreEqual(1, world.Deltas);
            Assert.AreEqual(7L, world.AckTick);
        }

        [Test]
        public void TickAndAckNeverMoveBackwards()
        {
            var world = new WorldState();
            world.Apply(Delta(10L, 5L, new[] { Entity("a", 0f, 0f) }, null));

            // A reordered or ack-less snapshot must not lower either counter — the
            // ack is a prediction anchor and rewinding it would replay accepted input.
            world.Apply(Delta(9L, 0L, new[] { Entity("a", 1f, 1f) }, null));

            Assert.AreEqual(10L, world.Tick);
            Assert.AreEqual(5L, world.AckTick);
        }

        [Test]
        public void EmptyAndNullCollectionsAreAccepted()
        {
            var world = new WorldState();

            world.Apply(Keyframe(1L));
            world.Apply(Delta(2L, 1L, null, null));
            world.Apply(Delta(3L, 2L, new ResolvedEntity[0], new string[0]));

            Assert.AreEqual(0, world.Count);
            Assert.AreEqual(3L, world.Tick);
        }

        [Test]
        public void ResetDropsEverything()
        {
            var world = new WorldState();
            world.Apply(Keyframe(4L, Entity("a", 1f, 1f)));

            world.Reset();

            Assert.AreEqual(0, world.Count);
            Assert.AreEqual(0L, world.Tick);
            Assert.AreEqual(0L, world.AckTick);
            Assert.AreEqual(0, world.Keyframes);
            Assert.AreEqual(0, world.Deltas);
        }
    }
}
