using Google.Protobuf;
using NUnit.Framework;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.World;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;
using Pb = RpgMmo.Wire.V1;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Protocol version 3 entity state from the decoded wire message to the merged world:
    /// handle resolution of the new entity references (<c>owner</c>, a status's
    /// <c>source</c>), and the hand-off to Shared.GameLogic's merger through the
    /// <c>EntitySnapshotData</c> v3 constructor.
    /// </summary>
    /// <remarks>
    /// The fields are asserted where a consumer reads them -- on <see cref="ResolvedEntity"/>
    /// and on <see cref="WorldState"/> -- because the failure being guarded is the familiar one:
    /// decoded correctly, then dropped by an adapter, arriving as zero with every layer green.
    /// </remarks>
    public class SnapshotV3ResolveTests
    {
        private static SnapshotMessage Decode(Pb.SnapshotMessage pb) =>
            (SnapshotMessage)new ProtobufWireCodec().DecodeBody(new Pb.Envelope
            {
                Type = (uint)MsgType.Snapshot,
                Payload = ByteString.CopyFrom(pb.ToByteArray()),
            }.ToByteArray()).Payload;

        private static Pb.EntitySnapshot Caster() => new Pb.EntitySnapshot
        {
            Id = "player_1", Handle = 1, Type = Pb.EntityType.Player, X = 1f, Y = 1f, Hp = 10, MaxHp = 10,
        };

        private static Pb.EntitySnapshot Projectile(uint ownerHandle) => new Pb.EntitySnapshot
        {
            Id = "proj_1", Handle = 2, Type = Pb.EntityType.Projectile,
            X = 2f, Y = 3f, Z = 1.5f, VelX = 20f, VelY = 0f, VelZ = -1f,
            Owner = ownerHandle, SpawnSeq = 4,
        };

        [Test]
        public void OwnerHandleResolvesToTheCastersId_EvenWhenBothAreIntroducedTogether()
        {
            // The projectile is listed BEFORE its caster: resolution must wait for every
            // binding in the snapshot, not just the ones seen so far.
            var pb = new Pb.SnapshotMessage { Tick = 10, Full = true };
            pb.Entities.Add(Projectile(ownerHandle: 1));
            pb.Entities.Add(Caster());

            var resolver = new SnapshotResolver();
            Assert.That(resolver.TryResolve(Decode(pb), out var resolved), Is.True);

            var projectile = resolved.Entities[0];
            Assert.That(projectile.Id, Is.EqualTo("proj_1"));
            Assert.That(projectile.OwnerId, Is.EqualTo("player_1"));
            Assert.That(projectile.SpawnSeq, Is.EqualTo(4u));
            Assert.That(projectile.Z, Is.EqualTo(1.5f));
            Assert.That(projectile.VelX, Is.EqualTo(20f));
            Assert.That(projectile.VelZ, Is.EqualTo(-1f));
            Assert.That(projectile.HasVersion3Fields, Is.True);
            Assert.That(resolver.UnresolvedEntityReferences, Is.Zero);
        }

        [Test]
        public void AnUnknownOwnerHandleIsNull_CountedAndDoesNotAbortTheSnapshot()
        {
            var pb = new Pb.SnapshotMessage { Tick = 10, Full = true };
            pb.Entities.Add(Projectile(ownerHandle: 99));

            var resolver = new SnapshotResolver();
            Assert.That(resolver.TryResolve(Decode(pb), out var resolved), Is.True,
                "a reference to ANOTHER entity is not a reason to discard this one");

            Assert.That(resolved.Entities[0].OwnerId, Is.Null);
            Assert.That(resolver.UnresolvedEntityReferences, Is.EqualTo(1));
            Assert.That(resolver.UnresolvedCount, Is.Zero, "no resync for a dangling owner");
        }

        [Test]
        public void JsonOwnerIdIsUsedWhenThereIsNoHandle()
        {
            var message = new SnapshotMessage { Tick = 3, Full = true };
            message.Entities.Add(new EntitySnapshot { Id = "proj_9", Type = "projectile", OwnerId = "player_7", SpawnSeq = 2 });

            Assert.That(new SnapshotResolver().TryResolve(message, out var resolved), Is.True);
            Assert.That(resolved.Entities[0].OwnerId, Is.EqualTo("player_7"));
        }

        [Test]
        public void StatsAndStatusesResolve_WithTheStatusSourceTurnedIntoAnId()
        {
            var target = Caster();
            target.Stats.Add(new Pb.StatValue { StatId = 1, Value = 50 });
            target.Stats.Add(new Pb.StatValue { StatId = 2, Value = -5 });
            target.Statuses.Add(new Pb.StatusEffect { EffectId = 30, Stacks = 3, ExpiresTick = 500, Source = 5 });
            target.Statuses.Add(new Pb.StatusEffect { EffectId = 31, Stacks = 1 });

            var pb = new Pb.SnapshotMessage { Tick = 10, Full = true };
            pb.Entities.Add(target);
            pb.Entities.Add(new Pb.EntitySnapshot { Id = "mob_5", Handle = 5, Type = Pb.EntityType.Mob });

            Assert.That(new SnapshotResolver().TryResolve(Decode(pb), out var resolved), Is.True);
            var e = resolved.Entities[0];

            Assert.That(e.Stats, Is.EqualTo(new[] { new StatValueData(1, 50), new StatValueData(2, -5) }));
            Assert.That(e.Statuses.Length, Is.EqualTo(2));
            Assert.That(e.Statuses[0], Is.EqualTo(new StatusEffectData(30, 3, 500, "mob_5")));
            Assert.That(e.Statuses[1].SourceId, Is.Null, "source 0 means none");
            Assert.That(e.OwnerId, Is.Null);
        }

        [Test]
        public void AVersion2EntityCarriesNoVersion3State()
        {
            var pb = new Pb.SnapshotMessage { Tick = 10, Full = true };
            pb.Entities.Add(Caster());

            Assert.That(new SnapshotResolver().TryResolve(Decode(pb), out var resolved), Is.True);
            var e = resolved.Entities[0];
            Assert.That(e.HasVersion3Fields, Is.False);
            Assert.That(e.Stats, Is.Null);
            Assert.That(e.Statuses, Is.Null);
            Assert.That(e.OwnerId, Is.Null);
        }

        [Test]
        public void ResolvedEventsCarryTheEffectId()
        {
            var pb = new Pb.SnapshotMessage { Tick = 10, Full = true };
            pb.Entities.Add(Caster());
            pb.Events.Add(new Pb.GameEvent { Type = Pb.GameEventType.StatusApplied, Target = 1, Amount = 2, EffectId = 30 });

            Assert.That(new SnapshotResolver().TryResolve(Decode(pb), out var resolved), Is.True);
            Assert.That(resolved.Events[0].Type, Is.EqualTo(Cuvara.Netcode.Protocol.Messages.GameEventType.StatusApplied));
            Assert.That(resolved.Events[0].EffectId, Is.EqualTo(30u));
            Assert.That(resolved.Events[0].TargetId, Is.EqualTo("player_1"));
        }

        // ── into the world ───────────────────────────────────────────────────────

        private static ResolvedEntity Core(string id, uint mask = 0u) =>
            new ResolvedEntity(id, mask == 0u ? "player" : "", 1f, 2f, 10, 10, 5f, 1u,
                EntityAction.Idle, actionSeq: 0u, changedFields: mask);

        private static ResolvedSnapshot Snap(long tick, bool full, params ResolvedEntity[] entities) =>
            new ResolvedSnapshot(tick, 0L, full, entities, new string[0]);

        [Test]
        public void WorldStateCarriesEveryVersion3FieldIntoTheMergedEntity()
        {
            var core = Core("p");
            var full = new ResolvedEntity(in core, 4f, 1f, 2f, 3f, "owner", 6u,
                new[] { new StatValueData(1, 10) }, null,
                new[] { new StatusEffectData(7, 1, 0, null) }, null);

            var world = new WorldState();
            world.Apply(Snap(1, true, full));

            Assert.That(world.TryGet("p", out var e), Is.True);
            Assert.That(e.Z, Is.EqualTo(4f));
            Assert.That(e.VelX, Is.EqualTo(1f));
            Assert.That(e.VelY, Is.EqualTo(2f));
            Assert.That(e.VelZ, Is.EqualTo(3f));
            Assert.That(e.OwnerId, Is.EqualTo("owner"));
            Assert.That(e.SpawnSeq, Is.EqualTo(6u));
            Assert.That(e.Stats, Is.EqualTo(new[] { new StatValueData(1, 10) }));
            Assert.That(e.Statuses, Is.EqualTo(new[] { new StatusEffectData(7, 1, 0, null) }));
            // And the protocol 2 core is untouched by the v3 constructor.
            Assert.That(e.X, Is.EqualTo(1f));
            Assert.That(e.Hp, Is.EqualTo(10));
            Assert.That(e.Speed, Is.EqualTo(5f));
        }

        /// <summary>
        /// A delta with only the Z bit set changes the height and keeps everything else,
        /// including the stat block a version 3 delta did not mention. The merge rule is
        /// SnapshotMerger's; this asserts the adapter hands it the mask and the fields.
        /// </summary>
        [Test]
        public void ADeltaWithTheZBitMovesHeightAndKeepsTheStatBlock()
        {
            var core = Core("p");
            var world = new WorldState();
            world.Apply(Snap(1, true, new ResolvedEntity(in core, 1f, 0f, 0f, 0f, null, 0u,
                new[] { new StatValueData(1, 10), new StatValueData(2, 20) }, null, null, null)));

            var deltaCore = Core("p", SnapshotFieldBits.Z);
            world.Apply(Snap(2, false, new ResolvedEntity(in deltaCore, 7f, 0f, 0f, 0f, null, 0u, null, null, null, null)));

            Assert.That(world.TryGet("p", out var e), Is.True);
            Assert.That(e.Z, Is.EqualTo(7f));
            Assert.That(e.Stats, Is.EqualTo(new[] { new StatValueData(1, 10), new StatValueData(2, 20) }));
            Assert.That(e.Hp, Is.EqualTo(10), "an unflagged protocol 2 field is kept too");
        }

        [Test]
        public void AStatsDeltaUpsertsAndRemovesById()
        {
            var core = Core("p");
            var world = new WorldState();
            world.Apply(Snap(1, true, new ResolvedEntity(in core, 0f, 0f, 0f, 0f, null, 0u,
                new[] { new StatValueData(1, 10), new StatValueData(2, 20) }, null, null, null)));

            var deltaCore = Core("p", SnapshotFieldBits.Stats);
            world.Apply(Snap(2, false, new ResolvedEntity(in deltaCore, 0f, 0f, 0f, 0f, null, 0u,
                new[] { new StatValueData(2, 25), new StatValueData(3, 30) }, new[] { 1u }, null, null)));

            Assert.That(world.TryGet("p", out var e), Is.True);
            Assert.That(e.Stats, Is.EqualTo(new[] { new StatValueData(2, 25), new StatValueData(3, 30) }));
        }

        [Test]
        public void FromBytesToTheWorld_TheProjectileArrivesWithItsOwnerAndVelocity()
        {
            var pb = new Pb.SnapshotMessage { Tick = 10, Full = true };
            pb.Entities.Add(Caster());
            pb.Entities.Add(Projectile(ownerHandle: 1));

            var resolver = new SnapshotResolver();
            Assert.That(resolver.TryResolve(Decode(pb), out var resolved), Is.True);
            var world = new WorldState();
            world.Apply(resolved);

            Assert.That(world.TryGet("proj_1", out var p), Is.True);
            Assert.That(p.Type, Is.EqualTo("projectile"));
            Assert.That(p.OwnerId, Is.EqualTo("player_1"));
            Assert.That(p.SpawnSeq, Is.EqualTo(4u));
            Assert.That(p.Z, Is.EqualTo(1.5f));
            Assert.That(p.VelX, Is.EqualTo(20f));
        }
    }
}
