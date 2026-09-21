using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.World;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The entity counters on <see cref="WorldState"/> (#161).
    /// </summary>
    /// <remarks>
    /// Every other client-side counter measures frames, so a snapshot carrying one entity
    /// and one carrying eight are indistinguishable and a client rendering 1 of 8 reports a
    /// perfectly healthy line. These exist so that "snapshots arriving at 15/s" can be
    /// distinguished from "snapshots arriving at 15/s carrying two entities", which is a
    /// completely different statement and the only actionable one.
    /// </remarks>
    public class WorldStateEntityCounterTests
    {
        private static ResolvedEntity E(string id, float x) =>
            new ResolvedEntity(id, "mob", x, 0f, 100, 100);

        private static ResolvedSnapshot Snap(long tick, bool full, IReadOnlyList<ResolvedEntity> ents,
                                             string[] removed = null) =>
            new ResolvedSnapshot(tick, 0L, full, ents, removed ?? new string[0]);

        [Test]
        public void AKeyframeReportsItsOwnSize_AndTheVisibleCountAgreesWithIt()
        {
            var world = new WorldState();
            world.Apply(Snap(1, true, new[] { E("a", 1), E("b", 2), E("c", 3) }));

            Assert.AreEqual(3, world.LastAppliedEntityCount);
            Assert.IsTrue(world.LastAppliedWasKeyframe);
            // The comparison the counter exists to make: on a keyframe these must agree.
            Assert.AreEqual(world.Count, world.LastAppliedEntityCount);
            Assert.AreEqual(3, world.EntitiesApplied);
        }

        /// <summary>
        /// The arm that stops the counter being read as "entities visible". On a delta it is
        /// the number that CHANGED, which is normally far below the visible count and is not
        /// a fault — reporting it as if it were would manufacture the alarm this is meant to
        /// make possible.
        /// </summary>
        [Test]
        public void ADeltaReportsWhatChanged_NotWhatIsVisible()
        {
            var world = new WorldState();
            world.Apply(Snap(1, true, new[] { E("a", 1), E("b", 2), E("c", 3) }));

            world.Apply(Snap(2, false, new[] { E("a", 9) }));

            Assert.AreEqual(1, world.LastAppliedEntityCount);
            Assert.IsFalse(world.LastAppliedWasKeyframe);
            Assert.AreEqual(3, world.Count, "a delta must not drop entities it did not mention");
            Assert.AreEqual(4, world.EntitiesApplied, "the running total accumulates across snapshots");
        }

        [Test]
        public void RemovalsAreCountedSeparatelyFromEntities()
        {
            var world = new WorldState();
            world.Apply(Snap(1, true, new[] { E("a", 1), E("b", 2) }));
            world.Apply(Snap(2, false, new[] { E("a", 5) }, new[] { "b" }));

            Assert.AreEqual(1, world.LastAppliedRemovedCount);
            Assert.AreEqual(1, world.LastAppliedEntityCount);
            Assert.AreEqual(1, world.Count);
        }

        /// <summary>
        /// An empty snapshot must report zero rather than leaving the previous value in
        /// place. A stale count is worse than no count: it reads as "entities are arriving"
        /// during exactly the silence the counter was added to reveal.
        /// </summary>
        [Test]
        public void AnEmptySnapshotReportsZero_RatherThanHoldingTheLastValue()
        {
            var world = new WorldState();
            world.Apply(Snap(1, true, new[] { E("a", 1), E("b", 2) }));
            Assert.AreEqual(2, world.LastAppliedEntityCount);

            world.Apply(Snap(2, false, new ResolvedEntity[0]));

            Assert.AreEqual(0, world.LastAppliedEntityCount);
            Assert.AreEqual(2, world.EntitiesApplied, "the total must not move on an empty snapshot");
        }
    }
}
