using System;
using Google.Protobuf;
using Cuvara.Netcode.Protocol;
using Msg = Cuvara.Netcode.Protocol.Messages;
using Pb = RpgMmo.Wire.V1;

namespace Cuvara.Netcode.Codec
{
    /// <summary>
    /// Protobuf codec, generated from <c>backend/shared/proto/wire.proto</c>. The
    /// default encoding on the wire, and 81% smaller than the JSON path once entity-id
    /// interning and the entity-type enum are in play.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two behaviours ride along with this encoding that JSON never exercises, and
    /// both present as gameplay bugs rather than codec bugs when wrong:
    /// entity-id interning (an id arrives once, later frames carry only a handle) and
    /// the entity-type enum with its <c>type_name</c> fallback. Interning is resolved a
    /// layer up, in <c>Snapshot/SnapshotResolver</c>; the type rule is handled here,
    /// because it is purely a question of how the field is read.
    /// </para>
    /// <para>
    /// <b>The payload is Protobuf inside Protobuf.</b> <c>Envelope.payload</c> is a
    /// bytes field carrying the encoding of the inner message — not JSON nested in a
    /// Protobuf envelope.
    /// </para>
    /// </remarks>
    public sealed class ProtobufWireCodec : IWireCodec
    {
        public WireEncoding Encoding => WireEncoding.Protobuf;

        public byte[] EncodeBody(MsgType type, IWireMessage payload)
        {
            // A type of 0 must never reach the wire. proto3 omits field 1 when it is
            // zero, so a type-0 Envelope would not begin with 0x08 — the tag byte the
            // peer sniffs to tell Protobuf from JSON's '{' (0x7B). The frame would be
            // parsed as the wrong encoding entirely, which is the most confusing
            // failure this protocol can produce, and this assert is the cheapest place
            // to catch it.
            if (type == MsgType.Unspecified)
            {
                throw new WireCodecException(
                    "refusing to encode a frame with type 0: proto3 omits a zero field 1, so the " +
                    "body would not start with 0x08 and the peer would sniff it as JSON rather " +
                    "than Protobuf");
            }

            var envelope = new Pb.Envelope { Type = (uint)type };

            var inner = EncodePayload(payload);
            if (inner != null)
            {
                envelope.Payload = ByteString.CopyFrom(inner);
            }

            return envelope.ToByteArray();
        }

        public WireFrame DecodeBody(byte[] body)
        {
            if (body == null || body.Length == 0)
            {
                throw new WireCodecException("empty frame body");
            }

            Pb.Envelope envelope;
            try
            {
                envelope = Pb.Envelope.Parser.ParseFrom(body);
            }
            catch (InvalidProtocolBufferException ex)
            {
                throw new WireCodecException("malformed Protobuf envelope: " + ex.Message, ex);
            }

            // Rejecting 0 fails closed rather than half-parsing. A body starting 0x12 is
            // valid Protobuf carrying only field 2, leaving Type at its default of 0 —
            // arbitrary bytes can decode "successfully" into exactly that.
            if (envelope.Type == 0)
            {
                throw new WireCodecException(
                    "Protobuf envelope carries type 0, which is not a wire type; the body is not " +
                    "a frame this protocol produced");
            }

            var type = (MsgType)envelope.Type;
            return new WireFrame(type, DecodePayload(type, envelope.Payload), WireEncoding.Protobuf);
        }

        private static byte[] EncodePayload(IWireMessage payload)
        {
            switch (payload)
            {
                case null:
                    return null;

                case Msg.ResyncRequest _:
                    // Empty message; the server ignores the body entirely.
                    return new Pb.ResyncRequest().ToByteArray();

                case Msg.AuthRequest m:
                    return new Pb.AuthRequest { Token = m.Token ?? string.Empty }.ToByteArray();

                case Msg.EnterWorldRequest m:
                    return new Pb.EnterWorldRequest { MapId = m.MapId ?? string.Empty }.ToByteArray();

                case Msg.JoinTokenRequest m:
                    return new Pb.JoinTokenRequest { Token = m.Token ?? string.Empty }.ToByteArray();

                case Msg.InputMessage m:
                    return new Pb.InputMessage
                    {
                        Tick = (ulong)Math.Max(0L, m.Tick),
                        MoveX = m.MoveX,
                        MoveY = m.MoveY,
                        AttackTargetId = m.AttackTargetId ?? string.Empty,
                    }.ToByteArray();

                case Msg.DisconnectMessage m:
                    // Sent by the client on a clean Leave, on BOTH connections.
                    return new Pb.DisconnectMessage { Reason = m.Reason ?? string.Empty }.ToByteArray();

                case Msg.PingMessage m:
                    return new Pb.PingMessage { Timestamp = m.Timestamp }.ToByteArray();

                case Msg.PongMessage m:
                    return new Pb.PongMessage
                    {
                        Timestamp = m.Timestamp,
                        ServerTime = m.ServerTime,
                    }.ToByteArray();

                default:
                    throw new WireCodecException(
                        "no Protobuf encoding for payload type " + payload.GetType().Name);
            }
        }

        private static IWireMessage DecodePayload(MsgType type, ByteString payload)
        {
            var bytes = payload ?? ByteString.Empty;

            try
            {
                switch (type)
                {
                    case MsgType.AuthResp:
                    {
                        var m = Pb.AuthResponse.Parser.ParseFrom(bytes);
                        return new Msg.AuthResponse { Ok = m.Ok, UserId = m.UserId, Error = m.Error };
                    }

                    case MsgType.EnterWorldResp:
                    {
                        var m = Pb.EnterWorldResponse.Parser.ParseFrom(bytes);
                        return new Msg.EnterWorldResponse
                        {
                            ServerAddr = m.ServerAddr,
                            JoinToken = m.JoinToken,
                            Transport = m.Transport,
                            Error = m.Error,
                        };
                    }

                    case MsgType.JoinTokenResp:
                    {
                        var m = Pb.JoinTokenResponse.Parser.ParseFrom(bytes);
                        return new Msg.JoinTokenResponse { Ok = m.Ok, UserId = m.UserId, Error = m.Error };
                    }

                    case MsgType.Snapshot:
                        return DecodeSnapshot(Pb.SnapshotMessage.Parser.ParseFrom(bytes));

                    case MsgType.Disconnect:
                    {
                        var m = Pb.DisconnectMessage.Parser.ParseFrom(bytes);
                        return new Msg.DisconnectMessage { Reason = m.Reason };
                    }

                    case MsgType.Kick:
                    {
                        var m = Pb.KickMessage.Parser.ParseFrom(bytes);
                        return new Msg.KickMessage { Reason = m.Reason };
                    }

                    case MsgType.Ping:
                    {
                        var m = Pb.PingMessage.Parser.ParseFrom(bytes);
                        return new Msg.PingMessage { Timestamp = m.Timestamp };
                    }

                    case MsgType.Pong:
                    {
                        var m = Pb.PongMessage.Parser.ParseFrom(bytes);
                        return new Msg.PongMessage { Timestamp = m.Timestamp, ServerTime = m.ServerTime };
                    }

                    case MsgType.Resync:
                        return new Msg.ResyncRequest();

                    default:
                        // A type this client does not consume is not an error — the
                        // connection layer ignores frames it has no handler for.
                        return null;
                }
            }
            catch (InvalidProtocolBufferException ex)
            {
                throw new WireCodecException(
                    $"malformed Protobuf payload for {type}: {ex.Message}", ex);
            }
        }

        private static Msg.SnapshotMessage DecodeSnapshot(Pb.SnapshotMessage m)
        {
            var snapshot = new Msg.SnapshotMessage
            {
                // ulong on the wire, long here. Ticks are counters, not durations, and
                // will not approach the point where this narrows.
                Tick = (long)m.Tick,
                AckTick = (long)m.AckTick,
                Full = m.Full,
            };

            foreach (var e in m.Entities)
            {
                snapshot.Entities.Add(new Msg.EntitySnapshot
                {
                    // May be empty: on a delta the id is interned away and only the
                    // handle identifies the entity. SnapshotResolver resolves it.
                    Id = e.Id,
                    Type = ReadEntityType(e),
                    X = e.X,
                    Y = e.Y,
                    Hp = e.Hp,
                    MaxHp = e.MaxHp,
                    Handle = e.Handle,
                    Speed = e.Speed,
                });
            }

            foreach (var id in m.Removed)
            {
                // Entity IDs, never handles — do not route these through the handle table.
                snapshot.Removed.Add(id);
            }

            return snapshot;
        }

        /// <summary>
        /// Reads the entity kind, preferring the enum and falling back to
        /// <c>type_name</c>.
        /// </summary>
        /// <remarks>
        /// The writer never sets both — an enum value costs two bytes and the string
        /// would be pure waste beside it — so <c>ENTITY_TYPE_UNSPECIFIED</c> (0) means
        /// literally "see <c>type_name</c>", not "unknown". Reading only the enum makes
        /// every kind the schema does not yet know come back as unspecified; reading
        /// only <c>type_name</c> makes every kind it does know come back empty. Both
        /// halves are required. The names must match the server's
        /// <c>EntityTypes</c> spelling, because the resolved string is what gameplay
        /// compares against.
        /// </remarks>
        private static string ReadEntityType(Pb.EntitySnapshot e)
        {
            switch (e.Type)
            {
                case Pb.EntityType.Player: return "player";
                case Pb.EntityType.Mob: return "mob";
                case Pb.EntityType.Npc: return "npc";
                case Pb.EntityType.Item: return "item";
                case Pb.EntityType.Projectile: return "projectile";

                case Pb.EntityType.Unspecified:
                default:
                    // Either a kind added to the simulation ahead of the schema, or one
                    // deliberately kept out of the enum. Both travel as type_name.
                    return e.TypeName ?? string.Empty;
            }
        }
    }
}
