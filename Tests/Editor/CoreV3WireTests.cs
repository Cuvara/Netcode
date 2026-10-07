using System;
using System.Text;
using Google.Protobuf;
using NUnit.Framework;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Json;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;
using Pb = RpgMmo.Wire.V1;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Wire protocol version 3 (ADR-28..31) through both codecs: every new field, in both
    /// encodings, in the direction it actually travels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inbound fields are built with the GENERATED Protobuf types (the bytes the server's own
    /// encoder produces) or as the JSON the Go struct tags produce, then decoded by this
    /// package's codec. Outbound fields are encoded by this package's codec and read back with
    /// the generated parser, or as JSON text. Neither side of a test is this package checking
    /// itself.
    /// </para>
    /// <para>
    /// Each test asserts the value that arrived, never that a decode "succeeded": the failure
    /// mode of a missed field is a frame that decodes cleanly with the field at its default.
    /// </para>
    /// </remarks>
    public class CoreV3WireTests
    {
        private static byte[] ProtoBody(MsgType type, IMessage payload) =>
            new Pb.Envelope
            {
                Type = (uint)type,
                Payload = ByteString.CopyFrom(payload.ToByteArray()),
            }.ToByteArray();

        private static byte[] JsonBody(MsgType type, string payloadJson) =>
            Encoding.UTF8.GetBytes("{\"type\":" + (int)type + ",\"payload\":" + payloadJson + "}");

        private static JsonValue JsonPayload(byte[] body)
        {
            var envelope = Cuvara.Netcode.Json.JsonParser.Parse(Encoding.UTF8.GetString(body));
            Assert.That(envelope.TryGetMember("payload", out var payload), Is.True);
            return payload;
        }

        private static T ParsePbPayload<T>(byte[] body, MessageParser<T> parser) where T : IMessage<T> =>
            parser.ParseFrom(Pb.Envelope.Parser.ParseFrom(body).Payload);

        // ── MsgType ──────────────────────────────────────────────────────────────

        [Test]
        public void CommandChannelMessageTypesMatchTheGeneratedEnum()
        {
            Assert.That((int)MsgType.Command, Is.EqualTo((int)Pb.MsgType.Command));
            Assert.That((int)MsgType.CommandResult, Is.EqualTo((int)Pb.MsgType.CommandResult));
            Assert.That((int)MsgType.ServerPush, Is.EqualTo((int)Pb.MsgType.ServerPush));
            Assert.That((int)MsgType.Command, Is.EqualTo(32));
            Assert.That((int)MsgType.CommandResult, Is.EqualTo(33));
            Assert.That((int)MsgType.ServerPush, Is.EqualTo(34));
        }

        [Test]
        public void NewGameEventTypesMatchTheGeneratedEnum()
        {
            Assert.That((int)GameEventType.StatusApplied, Is.EqualTo((int)Pb.GameEventType.StatusApplied));
            Assert.That((int)GameEventType.StatusRemoved, Is.EqualTo((int)Pb.GameEventType.StatusRemoved));
            Assert.That((int)GameEventType.ProjectileHit, Is.EqualTo((int)Pb.GameEventType.ProjectileHit));
        }

        // ── EntitySnapshot, inbound ──────────────────────────────────────────────

        private static Pb.EntitySnapshot V3Entity()
        {
            var e = new Pb.EntitySnapshot
            {
                Id = "proj_1",
                Handle = 2,
                Type = Pb.EntityType.Projectile,
                X = 1f, Y = 2f,
                Z = 3.5f,
                VelX = 10f, VelY = -4f, VelZ = 0.25f,
                Owner = 1,
                SpawnSeq = 7,
                ChangedFields = 0,
            };
            e.Stats.Add(new Pb.StatValue { StatId = 3, Value = -42 });
            e.Stats.Add(new Pb.StatValue { StatId = 9, Value = 1000 });
            e.StatsRemoved.Add(4);
            e.Statuses.Add(new Pb.StatusEffect { EffectId = 11, Stacks = 2, ExpiresTick = 123456789012UL, Source = 1 });
            e.StatusesRemoved.Add(12);
            e.StatusesRemoved.Add(13);
            return e;
        }

        private static void AssertV3Entity(EntitySnapshot e, bool interned)
        {
            Assert.That(e.Z, Is.EqualTo(3.5f), "z");
            Assert.That(e.VelX, Is.EqualTo(10f), "vel_x");
            Assert.That(e.VelY, Is.EqualTo(-4f), "vel_y");
            Assert.That(e.VelZ, Is.EqualTo(0.25f), "vel_z");
            Assert.That(e.SpawnSeq, Is.EqualTo(7u), "spawn_seq");
            if (interned)
            {
                Assert.That(e.Owner, Is.EqualTo(1u), "owner handle");
                Assert.That(e.OwnerId, Is.Empty, "owner_id is JSON-only");
            }
            else
            {
                Assert.That(e.Owner, Is.EqualTo(0u), "JSON never interns");
                Assert.That(e.OwnerId, Is.EqualTo("player_1"), "owner_id");
            }

            Assert.That(e.Stats.Count, Is.EqualTo(2));
            Assert.That(e.Stats[0].StatId, Is.EqualTo(3u));
            Assert.That(e.Stats[0].Value, Is.EqualTo(-42), "sint32 must keep its sign");
            Assert.That(e.Stats[1].StatId, Is.EqualTo(9u));
            Assert.That(e.Stats[1].Value, Is.EqualTo(1000));
            Assert.That(e.StatsRemoved, Is.EqualTo(new[] { 4u }));

            Assert.That(e.Statuses.Count, Is.EqualTo(1));
            Assert.That(e.Statuses[0].EffectId, Is.EqualTo(11u));
            Assert.That(e.Statuses[0].Stacks, Is.EqualTo(2u));
            Assert.That(e.Statuses[0].ExpiresTick, Is.EqualTo(123456789012UL));
            Assert.That(e.Statuses[0].Source, Is.EqualTo(interned ? 1u : 0u));
            Assert.That(e.StatusesRemoved, Is.EqualTo(new[] { 12u, 13u }));
        }

        [Test]
        public void ProtobufSnapshot_CarriesEveryVersion3EntityField()
        {
            var pb = new Pb.SnapshotMessage { Tick = 5, Full = true };
            pb.Entities.Add(V3Entity());

            var frame = new ProtobufWireCodec().DecodeBody(ProtoBody(MsgType.Snapshot, pb));
            var snapshot = (SnapshotMessage)frame.Payload;

            Assert.That(snapshot.Entities[0].Type, Is.EqualTo("projectile"));
            AssertV3Entity(snapshot.Entities[0], interned: true);
        }

        [Test]
        public void JsonSnapshot_CarriesEveryVersion3EntityField_WithOwnerIdAsTheJsonTwinOfOwner()
        {
            const string payload =
                "{\"tick\":5,\"full\":true,\"entities\":[{" +
                "\"id\":\"proj_1\",\"type\":\"projectile\",\"x\":1,\"y\":2,\"hp\":0,\"max_hp\":0," +
                "\"z\":3.5,\"vel_x\":10,\"vel_y\":-4,\"vel_z\":0.25," +
                "\"owner_id\":\"player_1\",\"spawn_seq\":7," +
                "\"stats\":[{\"stat_id\":3,\"value\":-42},{\"stat_id\":9,\"value\":1000}]," +
                "\"stats_removed\":[4]," +
                "\"statuses\":[{\"effect_id\":11,\"stacks\":2,\"expires_tick\":123456789012}]," +
                "\"statuses_removed\":[12,13]," +
                "\"changed_fields\":0}]}";

            var frame = new JsonWireCodec().DecodeBody(JsonBody(MsgType.Snapshot, payload));
            var snapshot = (SnapshotMessage)frame.Payload;

            AssertV3Entity(snapshot.Entities[0], interned: false);
        }

        [Test]
        public void JsonSnapshot_ReadsChangedFields()
        {
            const string payload =
                "{\"tick\":5,\"entities\":[{\"id\":\"e\",\"z\":2,\"changed_fields\":512}]}";

            var snapshot = (SnapshotMessage)new JsonWireCodec()
                .DecodeBody(JsonBody(MsgType.Snapshot, payload)).Payload;

            Assert.That(snapshot.Entities[0].ChangedFields, Is.EqualTo(0x0200u));
        }

        [Test]
        public void Version2Snapshot_LeavesEveryVersion3FieldAtNotSent()
        {
            var pb = new Pb.SnapshotMessage { Tick = 5, Full = true };
            pb.Entities.Add(new Pb.EntitySnapshot { Id = "p", Handle = 1, Type = Pb.EntityType.Player, X = 1f });

            var e = ((SnapshotMessage)new ProtobufWireCodec()
                .DecodeBody(ProtoBody(MsgType.Snapshot, pb)).Payload).Entities[0];

            Assert.That(e.Z, Is.EqualTo(0f));
            Assert.That(e.VelX + e.VelY + e.VelZ, Is.EqualTo(0f));
            Assert.That(e.Owner, Is.EqualTo(0u));
            Assert.That(e.OwnerId, Is.Empty);
            Assert.That(e.SpawnSeq, Is.EqualTo(0u));
            Assert.That(e.Stats, Is.Empty);
            Assert.That(e.StatsRemoved, Is.Empty);
            Assert.That(e.Statuses, Is.Empty);
            Assert.That(e.StatusesRemoved, Is.Empty);
        }

        /// <summary>
        /// The pooled codec reuses entity objects; a stat block left over from the previous
        /// snapshot would be handed to whichever entity takes that slot next.
        /// </summary>
        [Test]
        public void PooledCodec_DoesNotLeakOneSnapshotsStatsIntoTheNext()
        {
            var codec = ProtobufWireCodec.CreatePooled();

            var first = new Pb.SnapshotMessage { Tick = 1, Full = true };
            first.Entities.Add(V3Entity());
            codec.DecodeBody(ProtoBody(MsgType.Snapshot, first));

            var second = new Pb.SnapshotMessage { Tick = 2 };
            second.Entities.Add(new Pb.EntitySnapshot { Handle = 2, X = 9f });
            var e = ((SnapshotMessage)codec.DecodeBody(ProtoBody(MsgType.Snapshot, second)).Payload).Entities[0];

            Assert.That(e.Stats, Is.Empty, "stats leaked from the pooled entity");
            Assert.That(e.StatsRemoved, Is.Empty);
            Assert.That(e.Statuses, Is.Empty, "statuses leaked from the pooled entity");
            Assert.That(e.StatusesRemoved, Is.Empty);
            Assert.That(e.Z, Is.EqualTo(0f));
            Assert.That(e.Owner, Is.EqualTo(0u));
            Assert.That(e.SpawnSeq, Is.EqualTo(0u));
        }

        // ── GameEvent, inbound ───────────────────────────────────────────────────

        [Test]
        public void ProtobufEvent_CarriesEffectIdAndTheNewTypes()
        {
            var pb = new Pb.SnapshotMessage { Tick = 5 };
            pb.Events.Add(new Pb.GameEvent { Type = Pb.GameEventType.StatusApplied, Source = 1, Target = 2, Amount = 3, EffectId = 21 });
            pb.Events.Add(new Pb.GameEvent { Type = Pb.GameEventType.StatusRemoved, Target = 2, EffectId = 21 });
            pb.Events.Add(new Pb.GameEvent { Type = Pb.GameEventType.ProjectileHit, Source = 1, Target = 2 });

            var events = ((SnapshotMessage)new ProtobufWireCodec()
                .DecodeBody(ProtoBody(MsgType.Snapshot, pb)).Payload).Events;

            Assert.That(events[0].Type, Is.EqualTo(GameEventType.StatusApplied));
            Assert.That(events[0].EffectId, Is.EqualTo(21u));
            Assert.That(events[0].Amount, Is.EqualTo(3));
            Assert.That(events[1].Type, Is.EqualTo(GameEventType.StatusRemoved));
            Assert.That(events[1].EffectId, Is.EqualTo(21u));
            Assert.That(events[2].Type, Is.EqualTo(GameEventType.ProjectileHit));
            Assert.That(events[2].EffectId, Is.EqualTo(0u));
        }

        [Test]
        public void JsonEvent_CarriesEffectIdAndTheNewTypes()
        {
            const string payload =
                "{\"tick\":5,\"events\":[" +
                "{\"type\":7,\"source_id\":\"a\",\"target_id\":\"b\",\"amount\":3,\"effect_id\":21}," +
                "{\"type\":2,\"target_id\":\"b\",\"amount\":5,\"flags\":4,\"effect_id\":22}," +
                "{\"type\":9,\"source_id\":\"a\",\"target_id\":\"b\"}]}";

            var events = ((SnapshotMessage)new JsonWireCodec()
                .DecodeBody(JsonBody(MsgType.Snapshot, payload)).Payload).Events;

            Assert.That(events[0].Type, Is.EqualTo(GameEventType.StatusApplied));
            Assert.That(events[0].EffectId, Is.EqualTo(21u));
            Assert.That(events[1].Flags, Is.EqualTo(GameEventFlags.Periodic));
            Assert.That(events[1].EffectId, Is.EqualTo(22u), "the periodic source of a heal tick");
            Assert.That(events[2].Type, Is.EqualTo(GameEventType.ProjectileHit));
        }

        // ── InputMessage, outbound ───────────────────────────────────────────────

        private static InputMessage V3Input() => new InputMessage
        {
            Tick = 77,
            MoveX = 0.5f,
            MoveY = -1f,
            AbilityId = 4,
            AimX = 10f,
            AimY = 20f,
            AimZ = 1.75f,
            RenderTick = 9_000_000_123UL,
            RenderAlpha = 0.375f,
            Jump = true,
            SpawnSeq = 5,
        };

        [Test]
        public void ProtobufInput_CarriesEveryVersion3Field()
        {
            var decoded = ParsePbPayload(
                new ProtobufWireCodec().EncodeBody(MsgType.Input, V3Input()), Pb.InputMessage.Parser);

            Assert.That(decoded.AimZ, Is.EqualTo(1.75f));
            Assert.That(decoded.RenderTick, Is.EqualTo(9_000_000_123UL));
            Assert.That(decoded.RenderAlpha, Is.EqualTo(0.375f));
            Assert.That(decoded.Jump, Is.True);
            Assert.That(decoded.SpawnSeq, Is.EqualTo(5u));
            // Additive: the version 2 fields are still where they were.
            Assert.That(decoded.Tick, Is.EqualTo(77UL));
            Assert.That(decoded.AimX, Is.EqualTo(10f));
            Assert.That(decoded.AbilityId, Is.EqualTo(4u));
        }

        [Test]
        public void ProtobufInput_WithNoVersion3Field_IsByteIdenticalToAVersion2Input()
        {
            var v2 = new InputMessage { Tick = 3, MoveX = 1f, MoveY = 0f };
            byte[] body = new ProtobufWireCodec().EncodeBody(MsgType.Input, v2);

            var expected = new Pb.Envelope
            {
                Type = (uint)MsgType.Input,
                Payload = ByteString.CopyFrom(new Pb.InputMessage { Tick = 3, MoveX = 1f }.ToByteArray()),
            }.ToByteArray();

            Assert.That(body, Is.EqualTo(expected));
        }

        [Test]
        public void JsonInput_WritesEveryVersion3FieldUnderItsWireName()
        {
            var p = JsonPayload(new JsonWireCodec().EncodeBody(MsgType.Input, V3Input()));

            Assert.That(p.GetFloat("aim_z"), Is.EqualTo(1.75f));
            Assert.That(p.GetLong("render_tick"), Is.EqualTo(9_000_000_123L));
            Assert.That(p.GetFloat("render_alpha"), Is.EqualTo(0.375f));
            Assert.That(p.GetBool("jump"), Is.True);
            Assert.That(p.GetUInt("spawn_seq"), Is.EqualTo(5u));
        }

        [Test]
        public void JsonInput_OmitsVersion3FieldsWhenUnset()
        {
            string json = Encoding.UTF8.GetString(new JsonWireCodec().EncodeBody(
                MsgType.Input, new InputMessage { Tick = 1 }));

            foreach (var key in new[] { "aim_z", "render_tick", "render_alpha", "jump", "spawn_seq" })
            {
                Assert.That(json, Does.Not.Contain("\"" + key + "\""), key + " must be omitted when zero");
            }
        }

        [Test]
        public void SetRenderTime_SplitsTheInterpolationTickIntoTickAndAlpha()
        {
            var input = new InputMessage().SetRenderTime(1200.25);
            Assert.That(input.RenderTick, Is.EqualTo(1200UL));
            Assert.That(input.RenderAlpha, Is.EqualTo(0.25f).Within(1e-6f));

            // Nothing rendered yet, or garbage: "not sent", so the server does not rewind.
            Assert.That(new InputMessage().SetRenderTime(0.0).RenderTick, Is.EqualTo(0UL));
            Assert.That(new InputMessage().SetRenderTime(double.NaN).RenderTick, Is.EqualTo(0UL));

            // A fraction that rounds to 1.0f in float is folded into the next tick, never sent
            // as an out-of-range alpha.
            var edge = new InputMessage().SetRenderTime(41.99999999999);
            Assert.That(edge.RenderAlpha, Is.LessThan(1f));
            Assert.That(edge.RenderTick == 41UL || edge.RenderTick == 42UL, Is.True);
        }

        // ── Character slots ──────────────────────────────────────────────────────

        [Test]
        public void EnterWorld_CarriesTheCharacterIdInBothEncodings()
        {
            var request = new EnterWorldRequest { MapId = "map_01", CharacterId = "char_b" };

            var pb = ParsePbPayload(
                new ProtobufWireCodec().EncodeBody(MsgType.EnterWorld, request), Pb.EnterWorldRequest.Parser);
            Assert.That(pb.CharacterId, Is.EqualTo("char_b"));
            Assert.That(pb.MapId, Is.EqualTo("map_01"));

            var json = JsonPayload(new JsonWireCodec().EncodeBody(MsgType.EnterWorld, request));
            Assert.That(json.GetString("character_id"), Is.EqualTo("char_b"));
        }

        /// <summary>
        /// The default character produces the bytes a pre-slot client produced, in both
        /// encodings -- the same additive rule party_id follows.
        /// </summary>
        [Test]
        public void EnterWorld_WithTheDefaultCharacter_IsByteIdenticalToAPreSlotClient()
        {
            var request = new EnterWorldRequest { MapId = "map_01" };

            string json = Encoding.UTF8.GetString(new JsonWireCodec().EncodeBody(MsgType.EnterWorld, request));
            Assert.That(json, Is.EqualTo("{\"type\":3,\"payload\":{\"map_id\":\"map_01\"}}"));

            byte[] proto = new ProtobufWireCodec().EncodeBody(MsgType.EnterWorld, request);
            var expected = new Pb.Envelope
            {
                Type = (uint)MsgType.EnterWorld,
                Payload = ByteString.CopyFrom(new Pb.EnterWorldRequest { MapId = "map_01" }.ToByteArray()),
            }.ToByteArray();
            Assert.That(proto, Is.EqualTo(expected));
        }

        [Test]
        public void JoinTokenResponse_SurfacesTheCharacterIdInBothEncodings()
        {
            var pb = (JoinTokenResponse)new ProtobufWireCodec().DecodeBody(ProtoBody(
                MsgType.JoinTokenResp,
                new Pb.JoinTokenResponse { Ok = true, UserId = "u", ProtocolVersion = 3, CharacterId = "char_b" })).Payload;
            Assert.That(pb.CharacterId, Is.EqualTo("char_b"));
            Assert.That(pb.ProtocolVersion, Is.EqualTo(3u));

            var json = (JoinTokenResponse)new JsonWireCodec().DecodeBody(JsonBody(
                MsgType.JoinTokenResp,
                "{\"ok\":true,\"user_id\":\"u\",\"protocol_version\":3,\"character_id\":\"char_b\"}")).Payload;
            Assert.That(json.CharacterId, Is.EqualTo("char_b"));

            var old = (JoinTokenResponse)new JsonWireCodec().DecodeBody(JsonBody(
                MsgType.JoinTokenResp, "{\"ok\":true,\"user_id\":\"u\"}")).Payload;
            Assert.That(old.CharacterId, Is.Empty, "a pre-slot server sends none");
        }

        // ── Command channel ──────────────────────────────────────────────────────

        private static readonly byte[] SomeBytes = { 0x08, 0x96, 0x01, 0x00, 0xFF };

        [Test]
        public void ProtobufCommandRequest_CarriesSeqOpcodeAndOpaquePayload()
        {
            var decoded = ParsePbPayload(
                new ProtobufWireCodec().EncodeBody(MsgType.Command,
                    new CommandRequest { Seq = 1, Opcode = 3, Payload = SomeBytes }),
                Pb.CommandRequest.Parser);

            Assert.That(decoded.Seq, Is.EqualTo(1u));
            Assert.That(decoded.Opcode, Is.EqualTo(3u));
            Assert.That(decoded.Payload.ToByteArray(), Is.EqualTo(SomeBytes));
        }

        [Test]
        public void JsonCommandRequest_CarriesThePayloadAsBase64LikeGoDoes()
        {
            var p = JsonPayload(new JsonWireCodec().EncodeBody(MsgType.Command,
                new CommandRequest { Seq = 2, Opcode = 5, Payload = SomeBytes }));

            Assert.That(p.GetUInt("seq"), Is.EqualTo(2u));
            Assert.That(p.GetUInt("opcode"), Is.EqualTo(5u));
            Assert.That(p.GetString("payload"), Is.EqualTo(Convert.ToBase64String(SomeBytes)));
            Assert.That(p.GetBytes("payload"), Is.EqualTo(SomeBytes));
        }

        [Test]
        public void CommandRequest_RoundTripsThroughBothCodecs()
        {
            foreach (IWireCodec codec in new IWireCodec[] { new ProtobufWireCodec(), new JsonWireCodec() })
            {
                var sent = new CommandRequest { Seq = 9, Opcode = 100, Payload = SomeBytes };
                var back = (CommandRequest)codec.DecodeBody(codec.EncodeBody(MsgType.Command, sent)).Payload;
                Assert.That(back.Seq, Is.EqualTo(9u), codec.Encoding.ToString());
                Assert.That(back.Opcode, Is.EqualTo(100u), codec.Encoding.ToString());
                Assert.That(back.Payload, Is.EqualTo(SomeBytes), codec.Encoding.ToString());
            }
        }

        [Test]
        public void CommandResult_DecodesFromBothEncodings()
        {
            var pb = (CommandResult)new ProtobufWireCodec().DecodeBody(ProtoBody(
                MsgType.CommandResult,
                new Pb.CommandResult { Seq = 4, Ok = false, Error = "rate_limited", Payload = ByteString.CopyFrom(SomeBytes) })).Payload;
            Assert.That(pb.Seq, Is.EqualTo(4u));
            Assert.That(pb.Ok, Is.False);
            Assert.That(pb.Error, Is.EqualTo("rate_limited"));
            Assert.That(pb.Payload, Is.EqualTo(SomeBytes));

            var json = (CommandResult)new JsonWireCodec().DecodeBody(JsonBody(
                MsgType.CommandResult,
                "{\"seq\":4,\"ok\":true,\"payload\":\"" + Convert.ToBase64String(SomeBytes) + "\"}")).Payload;
            Assert.That(json.Seq, Is.EqualTo(4u));
            Assert.That(json.Ok, Is.True);
            Assert.That(json.Error, Is.Empty);
            Assert.That(json.Payload, Is.EqualTo(SomeBytes));
        }

        [Test]
        public void ServerPush_DecodesFromBothEncodings()
        {
            var pb = (ServerPush)new ProtobufWireCodec().DecodeBody(ProtoBody(
                MsgType.ServerPush, new Pb.ServerPush { Opcode = 100, Payload = ByteString.CopyFrom(SomeBytes) })).Payload;
            Assert.That(pb.Opcode, Is.EqualTo(100u));
            Assert.That(pb.Payload, Is.EqualTo(SomeBytes));

            var json = (ServerPush)new JsonWireCodec().DecodeBody(JsonBody(
                MsgType.ServerPush, "{\"opcode\":100}")).Payload;
            Assert.That(json.Opcode, Is.EqualTo(100u));
            Assert.That(json.Payload, Is.Empty, "an omitted payload is empty, never null");
        }

        [Test]
        public void EncodingSniffer_StillTellsTheCommandFramesApart()
        {
            byte[] proto = new ProtobufWireCodec().EncodeBody(MsgType.Command, new CommandRequest { Seq = 1, Opcode = 1 });
            byte[] json = new JsonWireCodec().EncodeBody(MsgType.Command, new CommandRequest { Seq = 1, Opcode = 1 });

            Assert.That(EncodingSniffer.Sniff(proto), Is.EqualTo(WireEncoding.Protobuf));
            Assert.That(EncodingSniffer.Sniff(json), Is.EqualTo(WireEncoding.Json));
        }
    }
}
