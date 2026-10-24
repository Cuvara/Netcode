using NUnit.Framework;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.World;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Snapshot speed presence below the codec: the decode rule, NaN passthrough and the
    /// merge. Absent is NaN, a present zero is immobile, and on a field-delta partial
    /// <c>changed_fields</c> bit 0x0020 governs. The codecs are covered by
    /// <c>SnapshotSpeedCodecTests</c>.
    /// </summary>
    public class SnapshotSpeedPresenceTests
    {
        private const string Id = "e1";
        private const uint XY = SnapshotFieldBits.X | SnapshotFieldBits.Y;

        [Test]
        public void SpeedBitConstantMatchesTheSharedFieldBit()
        {
            Assert.AreEqual(SnapshotFieldBits.Speed, EntitySnapshot.SpeedFieldBit);
        }

        [Test]
        public void DecodeSpeed_AbsentIsNaNWhateverTheMask()
        {
            Assert.IsTrue(float.IsNaN(EntitySnapshot.DecodeSpeed(false, 0f, 0u)));
            Assert.IsTrue(float.IsNaN(EntitySnapshot.DecodeSpeed(false, 3f, XY | SnapshotFieldBits.Speed)));
        }

        [Test]
        public void DecodeSpeed_ZeroMaskMeansEveryFieldPresent()
        {
            Assert.AreEqual(0f, EntitySnapshot.DecodeSpeed(true, 0f, 0u));
            Assert.AreEqual(4f, EntitySnapshot.DecodeSpeed(true, 4f, 0u));
        }

        [Test]
        public void DecodeSpeed_PartialWithoutTheBitIsNaNEvenIfPresent()
        {
            Assert.IsTrue(float.IsNaN(EntitySnapshot.DecodeSpeed(true, 0f, XY)));
            Assert.IsTrue(float.IsNaN(EntitySnapshot.DecodeSpeed(true, 4f, XY)));
        }

        [Test]
        public void DecodeSpeed_PartialWithTheBitAppliesZeroAndPositive()
        {
            Assert.AreEqual(0f, EntitySnapshot.DecodeSpeed(true, 0f, XY | SnapshotFieldBits.Speed));
            Assert.AreEqual(4f, EntitySnapshot.DecodeSpeed(true, 4f, XY | SnapshotFieldBits.Speed));
        }

        // ── Passthrough and merge ──

        private static ResolvedEntity Full(float speed) =>
            new ResolvedEntity(Id, "player", 10f, 20f, 100, 100, speed, 1024u,
                EntityAction.Attacking, actionSeq: 0u, changedFields: 0u);

        private static ResolvedEntity Partial(float speed, uint mask) =>
            new ResolvedEntity(Id, type: "", 11f, 21f, 0, 0, speed, 0u,
                EntityAction.Unspecified, actionSeq: 0u, changedFields: mask);

        private static WorldState Seeded(float speed)
        {
            var w = new WorldState();
            w.Apply(new ResolvedSnapshot(1L, 0L, true, new[] { Full(speed) }, new string[0]));
            return w;
        }

        private static EntitySnapshotData Get(WorldState w)
        {
            Assert.IsTrue(w.TryGet(Id, out var e));
            return e;
        }

        [Test]
        public void SpeedlessResolvedEntityIsNaN()
        {
            Assert.IsTrue(float.IsNaN(new ResolvedEntity(Id, "player", 0f, 0f, 1, 1).Speed));
        }

        [Test]
        public void NaNPassesThroughTheWorldOnAFullSnapshot()
        {
            Assert.IsTrue(float.IsNaN(Get(Seeded(float.NaN)).Speed));
        }

        [Test]
        public void PartialWithNaNSpeedAndNoBitKeepsThePreviousSpeed()
        {
            var w = Seeded(5f);
            w.Apply(new ResolvedSnapshot(2L, 0L, false, new[] { Partial(float.NaN, XY) }, new string[0]));
            Assert.AreEqual(5f, Get(w).Speed);
        }

        [Test]
        public void DeltaIntoZeroWithTheBitApplies()
        {
            var w = Seeded(5f);
            w.Apply(new ResolvedSnapshot(2L, 0L, false,
                new[] { Partial(0f, XY | SnapshotFieldBits.Speed) }, new string[0]));
            Assert.AreEqual(0f, Get(w).Speed);
        }

        [Test]
        public void DeltaOutOfZeroWithTheBitApplies()
        {
            var w = Seeded(0f);
            w.Apply(new ResolvedSnapshot(2L, 0L, false,
                new[] { Partial(3f, XY | SnapshotFieldBits.Speed) }, new string[0]));
            Assert.AreEqual(3f, Get(w).Speed);
        }

        [Test]
        public void DecodedJsonPartialWithZeroSpeedAndNoBitDoesNotZeroTheMergedSpeed()
        {
            var w = Seeded(5f);
            var decoded = EntitySnapshot.DecodeSpeed(true, 0f, XY);

            w.Apply(new ResolvedSnapshot(2L, 0L, false, new[] { Partial(decoded, XY) }, new string[0]));

            Assert.AreEqual(5f, Get(w).Speed, "the moving entity must stay moving");
        }
    }
}
