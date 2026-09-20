using NUnit.Framework;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.World;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// A delta that carries a non-zero field mask must leave every unflagged field at its
    /// last known value, rather than resetting it to the proto3 default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the client half of wire protocol version 2 (#158). The server suppresses
    /// individual unchanged fields — 43.4% of a delta's payload was `hp`, `max_hp`, `speed`
    /// and `type` re-sent identical — and marks what it did send in
    /// <c>changed_fields</c>. If this side reads an absent field as zero instead of as
    /// "unchanged", entities collapse to the origin with no health.
    /// </para>
    /// <para>
    /// <b>Two arms, because the dangerous reading passes a one-armed test.</b> A mask of
    /// zero is specified to mean "every field present", which is what a keyframe and a
    /// pre-v2 server both send. So "the entity has the right values" is equally true of a
    /// correct implementation and of one that ignores the mask entirely and applies
    /// everything. The <c>MaskZero</c> arm pins that second behaviour, and the
    /// <c>MaskSet</c> arm is only meaningful next to it: the same delta bytes must produce
    /// DIFFERENT results depending on the mask, or the mask is not being read.
    /// </para>
    /// </remarks>
    public class FieldDeltaMergeTests
    {
        private const string Id = "e1";

        private static ResolvedEntity Full() =>
            new ResolvedEntity(Id, "player", 10f, 20f, 100, 100, 5f, 1024u,
                EntityAction.Attacking, actionSeq: 3u, changedFields: 0u);

        /// <summary>
        /// A delta carrying only new coordinates. Every other field is at its proto3
        /// default — which is exactly what the server sends when it suppresses them.
        /// </summary>
        private static ResolvedEntity PositionOnlyDelta(uint mask) =>
            new ResolvedEntity(Id, type: "", 11f, 21f, 0, 0, 0f, 0u,
                EntityAction.Unspecified, actionSeq: 0u, changedFields: mask);

        private static WorldState Seeded()
        {
            var w = new WorldState();
            w.Apply(new ResolvedSnapshot(1L, 0L, true, new[] { Full() }, new string[0]));
            return w;
        }

        [Test]
        public void MaskSet_UnflaggedFieldsKeepTheirLastKnownValue()
        {
            var w = Seeded();
            uint mask = SnapshotFieldBits.X | SnapshotFieldBits.Y;

            w.Apply(new ResolvedSnapshot(2L, 0L, false, new[] { PositionOnlyDelta(mask) },
                new string[0]));

            Assert.IsTrue(w.TryGet(Id, out var e), "entity vanished from the merged world");

            // What the delta did carry.
            Assert.AreEqual(11f, e.X, "flagged field X was not applied");
            Assert.AreEqual(21f, e.Y, "flagged field Y was not applied");

            // What it did not — the whole point.
            Assert.AreEqual(100, e.Hp, "Hp was reset to the proto3 default instead of kept");
            Assert.AreEqual(100, e.MaxHp, "MaxHp was reset instead of kept");
            Assert.AreEqual(5f, e.Speed, "Speed was reset instead of kept");
            Assert.AreEqual("player", e.Type, "Type was reset instead of kept");
            Assert.AreEqual(1024u, e.FacingBrad, "FacingBrad was reset instead of kept");
            Assert.AreEqual(EntityAction.Attacking, e.Action, "Action was reset instead of kept");
            Assert.AreEqual(3u, e.ActionSeq, "ActionSeq was reset instead of kept");
        }

        [Test]
        public void MaskZero_EveryFieldTakesTheWireValue_IncludingTheDefaults()
        {
            var w = Seeded();

            // Zero mask: a keyframe, or any pre-v2 sender. The SAME bytes as above.
            w.Apply(new ResolvedSnapshot(2L, 0L, false, new[] { PositionOnlyDelta(0u) },
                new string[0]));

            Assert.IsTrue(w.TryGet(Id, out var e));
            Assert.AreEqual(11f, e.X);
            Assert.AreEqual(21f, e.Y);

            // Zero means "all present", so the zeros ARE the update. An implementation that
            // silently treated every delta as partial would keep 100 here and pass the other
            // test while being wrong about keyframes.
            Assert.AreEqual(0, e.Hp, "mask 0 must mean every field present, including defaults");
            Assert.AreEqual(0f, e.Speed, "mask 0 must mean every field present, including defaults");
            Assert.AreEqual(0u, e.ActionSeq, "mask 0 must mean every field present, including defaults");
        }
    }
}
