using System.Text;
using Google.Protobuf;
using NUnit.Framework;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.World;
using Pb = RpgMmo.Wire.V1;
using SimAction = Shared.GameLogic.Components.EntityAction;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The gameplay-v2 wire additions on their way from bytes to the types a view layer
    /// consumes: the event channel (<c>SnapshotMessage.events</c>), the ability fields on
    /// <c>InputMessage</c>, and the <c>action_seq</c> retrigger counter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every one of these fails the same silent way if it is wrong: the server sends the
    /// field, the codec decodes the frame without error, and the value arrives as its type
    /// default. Nothing throws, nothing logs, and the feature simply does nothing. So each
    /// test asserts on the value that reached the consumer, never on the fact that a decode
    /// succeeded.
    /// </para>
    /// <para>
    /// Both encodings are exercised for each field. That asymmetry is not hypothetical: when
    /// <c>action_seq</c> first reached the backend's Protobuf writer alone, the Protobuf
    /// byte-identity fixture failed and the JSON one PASSED — which read as good news and
    /// was the bug.
    /// </para>
    /// </remarks>
    public class GameEventAndActionSeqTests
    {
        // ── Ability input, outbound ──────────────────────────────────────────────

        private static InputMessage AbilityInput() => new InputMessage
        {
            Tick = 41,
            MoveX = 1f,
            MoveY = 0f,
            AttackTargetId = "mob_3",
            AbilityId = 7,
            AbilityTargetId = "mob_9",
            AimX = 12.5f,
            AimY = -3.25f,
        };

        [Test]
        public void ProtobufInput_CarriesEveryAbilityField()
        {
            byte[] body = new ProtobufWireCodec().EncodeBody(MsgType.Input, AbilityInput());

            var decoded = Pb.InputMessage.Parser.ParseFrom(
                Pb.Envelope.Parser.ParseFrom(body).Payload);

            Assert.That(decoded.AbilityId, Is.EqualTo(7u));
            Assert.That(decoded.AbilityTargetId, Is.EqualTo("mob_9"));
            Assert.That(decoded.AimX, Is.EqualTo(12.5f));
            Assert.That(decoded.AimY, Is.EqualTo(-3.25f));
            // The pre-existing fields must still be there: an additive change that displaced
            // one of them would be a far worse outcome than the new fields not working.
            Assert.That(decoded.AttackTargetId, Is.EqualTo("mob_3"));
            Assert.That(decoded.MoveX, Is.EqualTo(1f));
        }

        [Test]
        public void JsonInput_OmitsAbilityFieldsWhenUnset()
        {
            var plain = new InputMessage { Tick = 1, MoveX = 0f, MoveY = 0f };

            string json = Encoding.UTF8.GetString(
                new JsonWireCodec().EncodeBody(MsgType.Input, plain));

            // Absence is how proto3 spells "not sent", and the two encodings must say the
            // same thing the same way — an explicit "ability_id":0 would decode identically
            // but put bytes on the wire the encoding this mirrors does not.
            Assert.That(json, Does.Not.Contain("ability_id"));
            Assert.That(json, Does.Not.Contain("ability_target_id"));
            Assert.That(json, Does.Not.Contain("aim_x"));
        }

        [Test]
        public void JsonInput_WritesAbilityFieldsWhenSet()
        {
            string json = Encoding.UTF8.GetString(
                new JsonWireCodec().EncodeBody(MsgType.Input, AbilityInput()));

            Assert.That(json, Does.Contain("\"ability_id\""));
            Assert.That(json, Does.Contain("mob_9"));
            Assert.That(json, Does.Contain("\"aim_x\""));
        }

        // ── action_seq, inbound ──────────────────────────────────────────────────

        private static byte[] ProtoSnapshotBody(Pb.SnapshotMessage snapshot) =>
            new Pb.Envelope
            {
                Type = (uint)MsgType.Snapshot,
                Payload = Google.Protobuf.ByteString.CopyFrom(snapshot.ToByteArray()),
            }.ToByteArray();

        private static Pb.EntitySnapshot PbEntity(
            string id, uint handle, uint actionSeq = 0,
            Pb.EntityAction action = Pb.EntityAction.Idle) => new Pb.EntitySnapshot
        {
            Id = id,
            Handle = handle,
            Type = Pb.EntityType.Player,
            X = 1f, Y = 2f, Hp = 50, MaxHp = 100, Speed = 4f,
            FacingBrad = 1,
            Action = action,
            ActionSeq = actionSeq,
        };

        [Test]
        public void ProtobufSnapshot_CarriesActionSeqThroughToTheResolvedEntity()
        {
            var pb = new Pb.SnapshotMessage { Tick = 10, Full = true };
            pb.Entities.Add(PbEntity("e1", 1, actionSeq: 9, action: Pb.EntityAction.Attacking));

            var frame = new ProtobufWireCodec().DecodeBody(ProtoSnapshotBody(pb));
            var snapshot = (SnapshotMessage)frame.Payload;

            Assert.That(snapshot.Entities[0].ActionSeq, Is.EqualTo(9u));

            Assert.That(new SnapshotResolver().TryResolve(snapshot, out var resolved), Is.True);
            Assert.That(resolved.Entities[0].ActionSeq, Is.EqualTo(9u),
                "the counter decoded but was dropped at handle resolution — the entity would " +
                "still render, it would just never retrigger an animation");
            Assert.That(resolved.Entities[0].Action, Is.EqualTo(SimAction.Attacking));
        }

        [Test]
        public void JsonSnapshot_CarriesActionSeq()
        {
            const string json = "{\"tick\":5,\"full\":true,\"entities\":[" +
                "{\"id\":\"e1\",\"type\":\"player\",\"x\":1,\"y\":2,\"hp\":50,\"max_hp\":100," +
                "\"speed\":4,\"facing_brad\":1,\"action\":3,\"action_seq\":4}]}";

            var frame = new JsonWireCodec().DecodeBody(Envelope(MsgType.Snapshot, json));
            var snapshot = (SnapshotMessage)frame.Payload;

            Assert.That(snapshot.Entities[0].ActionSeq, Is.EqualTo(4u));
        }

        [Test]
        public void AnAbsentActionSeq_DecodesAsZeroMeaningNotSent()
        {
            var pb = new Pb.SnapshotMessage { Tick = 1, Full = true };
            pb.Entities.Add(PbEntity("e1", 1));

            var frame = new ProtobufWireCodec().DecodeBody(ProtoSnapshotBody(pb));
            var snapshot = (SnapshotMessage)frame.Payload;

            // Zero, not 1: a consumer must be able to tell "this server sends no counter"
            // from "this counter is at its first value", or an old server retriggers every
            // animation on every snapshot.
            Assert.That(snapshot.Entities[0].ActionSeq, Is.EqualTo(0u));
        }

        // ── The event channel, inbound ───────────────────────────────────────────

        /// <summary>Frames a JSON payload the way the decoder expects, as the servers write it.</summary>
        private static byte[] Envelope(MsgType type, string json) =>
            Encoding.UTF8.GetBytes("{\"type\":" + (int)type + ",\"payload\":" + json + "}");

        private static Pb.GameEvent PbEvent(
            Pb.GameEventType type, uint source, uint target, int amount = 0,
            uint abilityId = 0, uint flags = 0) => new Pb.GameEvent
        {
            Type = type, Source = source, Target = target,
            Amount = amount, AbilityId = abilityId, Flags = flags,
        };

        [Test]
        public void ProtobufSnapshot_DecodesEvents()
        {
            var pb = new Pb.SnapshotMessage { Tick = 3, Full = true };
            pb.Entities.Add(PbEntity("attacker", 1));
            pb.Entities.Add(PbEntity("victim", 2));
            pb.Events.Add(PbEvent(Pb.GameEventType.Damage, 1, 2, amount: 25, abilityId: 7, flags: 1));

            var frame = new ProtobufWireCodec().DecodeBody(ProtoSnapshotBody(pb));
            var snapshot = (SnapshotMessage)frame.Payload;

            Assert.That(snapshot.Events, Has.Count.EqualTo(1));
            Assert.That(snapshot.Events[0].Type, Is.EqualTo(GameEventType.Damage));
            Assert.That(snapshot.Events[0].Amount, Is.EqualTo(25));
            Assert.That(snapshot.Events[0].AbilityId, Is.EqualTo(7u));
            Assert.That(snapshot.Events[0].Flags, Is.EqualTo(GameEventFlags.Critical));
        }

        [Test]
        public void TheResolverTurnsEventHandlesIntoEntityIds()
        {
            var pb = new Pb.SnapshotMessage { Tick = 3, Full = true };
            pb.Entities.Add(PbEntity("attacker", 1));
            pb.Entities.Add(PbEntity("victim", 2));
            pb.Events.Add(PbEvent(Pb.GameEventType.Damage, 1, 2, amount: 25));

            var snapshot = (SnapshotMessage)new ProtobufWireCodec()
                .DecodeBody(ProtoSnapshotBody(pb)).Payload;

            var resolver = new SnapshotResolver();
            Assert.That(resolver.TryResolve(snapshot, out var resolved), Is.True);

            Assert.That(resolved.Events, Has.Count.EqualTo(1));
            // The bindings this snapshot introduces are the ones the event uses. Resolving
            // events before those bindings landed would report both participants unresolved
            // — which is exactly the spawn-then-immediately-take-damage case.
            Assert.That(resolved.Events[0].SourceId, Is.EqualTo("attacker"));
            Assert.That(resolved.Events[0].TargetId, Is.EqualTo("victim"));
            Assert.That(resolver.UnresolvedEventParticipants, Is.Zero);
        }

        [Test]
        public void AnEventMayNameAnEntityNotInThisSnapshot()
        {
            var resolver = new SnapshotResolver();

            var keyframe = new Pb.SnapshotMessage { Tick = 1, Full = true };
            keyframe.Entities.Add(PbEntity("attacker", 1));
            keyframe.Entities.Add(PbEntity("victim", 2));
            var first = (SnapshotMessage)new ProtobufWireCodec()
                .DecodeBody(ProtoSnapshotBody(keyframe)).Payload;
            Assert.That(resolver.TryResolve(first, out _), Is.True);

            // A delta carrying only the victim, plus an event naming the attacker — who did
            // not move and so is legitimately absent from the entity list.
            var delta = new Pb.SnapshotMessage { Tick = 2, Full = false };
            delta.Entities.Add(new Pb.EntitySnapshot { Handle = 2, Hp = 25, MaxHp = 100 });
            delta.Events.Add(PbEvent(Pb.GameEventType.Damage, 1, 2, amount: 25));
            var second = (SnapshotMessage)new ProtobufWireCodec()
                .DecodeBody(ProtoSnapshotBody(delta)).Payload;

            Assert.That(resolver.TryResolve(second, out var resolved), Is.True);
            Assert.That(resolved.Events[0].SourceId, Is.EqualTo("attacker"));
            Assert.That(resolver.UnresolvedEventParticipants, Is.Zero);
        }

        /// <summary>
        /// A player who can see the victim but not the attacker still gets the number. The
        /// alternative is a health bar that drops with no explanation.
        /// </summary>
        [Test]
        public void AZeroHandleResolvesToNoSource_AndIsNotCountedAsUnresolved()
        {
            var pb = new Pb.SnapshotMessage { Tick = 1, Full = true };
            pb.Entities.Add(PbEntity("victim", 2));
            pb.Events.Add(PbEvent(Pb.GameEventType.Damage, source: 0, target: 2, amount: 9));

            var snapshot = (SnapshotMessage)new ProtobufWireCodec()
                .DecodeBody(ProtoSnapshotBody(pb)).Payload;

            var resolver = new SnapshotResolver();
            Assert.That(resolver.TryResolve(snapshot, out var resolved), Is.True);

            Assert.That(resolved.Events[0].HasSource, Is.False);
            Assert.That(resolved.Events[0].HasTarget, Is.True);
            Assert.That(resolved.Events[0].Amount, Is.EqualTo(9));
            // Zero is "no such participant", not a failure to resolve one.
            Assert.That(resolver.UnresolvedEventParticipants, Is.Zero);
        }

        /// <summary>
        /// The asymmetry with an entity: an unresolvable event participant is reported as
        /// absent and counted, and the snapshot still resolves. Escalating to a resync would
        /// spend a keyframe for every observer to repair a floating number.
        /// </summary>
        [Test]
        public void AnUnknownEventHandle_IsCountedButDoesNotFailTheSnapshot()
        {
            var pb = new Pb.SnapshotMessage { Tick = 1, Full = true };
            pb.Entities.Add(PbEntity("victim", 2));
            pb.Events.Add(PbEvent(Pb.GameEventType.Damage, source: 77, target: 2, amount: 9));

            var snapshot = (SnapshotMessage)new ProtobufWireCodec()
                .DecodeBody(ProtoSnapshotBody(pb)).Payload;

            var resolver = new SnapshotResolver();
            Assert.That(resolver.TryResolve(snapshot, out var resolved), Is.True,
                "an event must never abort a snapshot the entities themselves resolved");
            Assert.That(resolved.Events[0].HasSource, Is.False);
            Assert.That(resolved.Events[0].TargetId, Is.EqualTo("victim"));
            Assert.That(resolver.UnresolvedEventParticipants, Is.EqualTo(1));
            // And the entity-level counter is untouched: the two disagreements are different.
            Assert.That(resolver.UnresolvedCount, Is.Zero);
        }

        [Test]
        public void JsonEvents_AreAddressedByIdRatherThanHandle()
        {
            const string json = "{\"tick\":5,\"full\":true,\"entities\":[" +
                "{\"id\":\"victim\",\"type\":\"mob\",\"x\":0,\"y\":0,\"hp\":10,\"max_hp\":20,\"speed\":0}]," +
                "\"events\":[{\"type\":1,\"source_id\":\"attacker\",\"target_id\":\"victim\"," +
                "\"amount\":12,\"ability_id\":3,\"flags\":2}]}";

            var snapshot = (SnapshotMessage)new JsonWireCodec()
                .DecodeBody(Envelope(MsgType.Snapshot, json)).Payload;

            Assert.That(snapshot.Events, Has.Count.EqualTo(1));
            Assert.That(snapshot.Events[0].SourceId, Is.EqualTo("attacker"));

            var resolver = new SnapshotResolver();
            Assert.That(resolver.TryResolve(snapshot, out var resolved), Is.True);

            // No handle table on this encoding; the ids are the only names, and they pass
            // straight through.
            Assert.That(resolved.Events[0].SourceId, Is.EqualTo("attacker"));
            Assert.That(resolved.Events[0].TargetId, Is.EqualTo("victim"));
            Assert.That(resolved.Events[0].Amount, Is.EqualTo(12));
            Assert.That(resolved.Events[0].Flags, Is.EqualTo(GameEventFlags.Immune));
            Assert.That(resolver.UnresolvedEventParticipants, Is.Zero);
        }

        [Test]
        public void ASnapshotWithNoEvents_ResolvesToAnEmptyListRatherThanNull()
        {
            var pb = new Pb.SnapshotMessage { Tick = 1, Full = true };
            pb.Entities.Add(PbEntity("e1", 1));

            var snapshot = (SnapshotMessage)new ProtobufWireCodec()
                .DecodeBody(ProtoSnapshotBody(pb)).Payload;

            Assert.That(new SnapshotResolver().TryResolve(snapshot, out var resolved), Is.True);
            Assert.That(resolved.Events, Is.Not.Null);
            Assert.That(resolved.Events, Is.Empty);
        }

        /// <summary>
        /// An unrecognised type reaches the consumer rather than being swallowed by the
        /// codec. This layer decodes; deciding what to ignore belongs to whatever draws it,
        /// and a debug overlay should be able to show "the server sent a type we do not know".
        /// </summary>
        [Test]
        public void AnUnrecognisedEventType_IsCarriedThroughRatherThanDropped()
        {
            var pb = new Pb.SnapshotMessage { Tick = 1, Full = true };
            pb.Entities.Add(PbEntity("e1", 1));
            pb.Events.Add(new Pb.GameEvent { Type = (Pb.GameEventType)99, Target = 1 });

            var snapshot = (SnapshotMessage)new ProtobufWireCodec()
                .DecodeBody(ProtoSnapshotBody(pb)).Payload;

            Assert.That(new SnapshotResolver().TryResolve(snapshot, out var resolved), Is.True);
            Assert.That(resolved.Events, Has.Count.EqualTo(1));
            Assert.That((int)resolved.Events[0].Type, Is.EqualTo(99));
        }

        // ── The merge, which is where a decoded field most quietly disappears ────

        /// <summary>
        /// <c>action_seq</c> has to survive one more hop than the resolver: the merge into
        /// <see cref="WorldState"/>, which is what a view actually reads.
        /// </summary>
        /// <remarks>
        /// This test exists because that hop was in fact missing when the field was first
        /// wired through. The codec decoded it, the resolver carried it, every test above
        /// passed — and the value was dropped converting <c>ResolvedEntity</c> to
        /// <c>EntitySnapshotData</c>, so no view ever saw it. The symptom would have been an
        /// entity that renders perfectly, carries the right action, and never animates a
        /// second swing.
        /// </remarks>
        [Test]
        public void ActionSeqSurvivesTheMergeIntoWorldState()
        {
            var pb = new Pb.SnapshotMessage { Tick = 1, Full = true };
            pb.Entities.Add(PbEntity("e1", 1, actionSeq: 12, action: Pb.EntityAction.Attacking));

            var snapshot = (SnapshotMessage)new ProtobufWireCodec()
                .DecodeBody(ProtoSnapshotBody(pb)).Payload;

            Assert.That(new SnapshotResolver().TryResolve(snapshot, out var resolved), Is.True);

            var world = new WorldState();
            world.Apply(resolved);

            Assert.That(world.TryGet("e1", out var merged), Is.True);
            Assert.That(merged.ActionSeq, Is.EqualTo(12u),
                "decoded and resolved but dropped at the merge — no view would ever see it");
        }

        /// <summary>
        /// A repeated attack changes nothing else about the entity. If the merge or anything
        /// upstream of it dropped the counter, this is the case that would look identical to
        /// a single attack.
        /// </summary>
        [Test]
        public void ARepeatedAttackChangesOnlyTheCounter_AndTheChangeIsVisible()
        {
            var world = new WorldState();
            var resolver = new SnapshotResolver();

            uint Merge(uint seq)
            {
                var pb = new Pb.SnapshotMessage { Tick = seq, Full = true };
                pb.Entities.Add(PbEntity("e1", 1, actionSeq: seq, action: Pb.EntityAction.Attacking));
                var msg = (SnapshotMessage)new ProtobufWireCodec()
                    .DecodeBody(ProtoSnapshotBody(pb)).Payload;
                resolver.TryResolve(msg, out var r);
                world.Apply(r);
                world.TryGet("e1", out var e);
                return e.ActionSeq;
            }

            uint first = Merge(5);
            uint second = Merge(6);

            Assert.That(first, Is.EqualTo(5u));
            Assert.That(second, Is.EqualTo(6u));
            // Inequality is the retrigger test a consumer must use — not greater-than.
            Assert.That(second, Is.Not.EqualTo(first));
        }

        [Test]
        public void ResetClearsTheEventCounterToo()
        {
            var pb = new Pb.SnapshotMessage { Tick = 1, Full = true };
            pb.Entities.Add(PbEntity("victim", 2));
            pb.Events.Add(PbEvent(Pb.GameEventType.Damage, 77, 2));

            var snapshot = (SnapshotMessage)new ProtobufWireCodec()
                .DecodeBody(ProtoSnapshotBody(pb)).Payload;

            var resolver = new SnapshotResolver();
            resolver.TryResolve(snapshot, out _);
            Assert.That(resolver.UnresolvedEventParticipants, Is.EqualTo(1));

            resolver.Reset();

            Assert.That(resolver.UnresolvedEventParticipants, Is.Zero);
        }
    }
}
