using System;
using System.Text;
using Cuvara.Netcode.Json;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;

namespace Cuvara.Netcode.Codec
{
    /// <summary>
    /// The legacy JSON encoding: <c>{"type":N,"payload":{...}}</c> with
    /// <c>snake_case</c> members, matching the Go struct tags byte for byte.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only encoding implemented today. Both servers still accept it
    /// and latch per connection, so a JSON client is served correctly by a
    /// Protobuf-default server — but it is the legacy path: it is larger on the
    /// wire and, per the schema, the server never interns entity ids on it.
    /// </para>
    /// <para>
    /// Field presence follows the servers: they omit defaulted fields, and this
    /// codec treats an absent member as its default rather than an error.
    /// </para>
    /// </remarks>
    public sealed class JsonWireCodec : IWireCodec
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public WireEncoding Encoding => WireEncoding.Json;

        public byte[] EncodeBody(MsgType type, IWireMessage payload)
        {
            if (type == MsgType.Unspecified)
            {
                throw new WireCodecException("message type 0 is not a valid wire type");
            }

            var builder = new JsonBuilder();
            builder.BeginObject();
            builder.Number("type", (long)type);
            builder.Raw("payload", WritePayload(payload));
            builder.EndObject();

            return Utf8.GetBytes(builder.ToString());
        }

        public WireFrame DecodeBody(byte[] body)
        {
            if (body == null || body.Length == 0)
            {
                throw new WireCodecException("empty envelope body");
            }

            string text;
            try
            {
                text = Utf8.GetString(body);
            }
            catch (Exception ex)
            {
                throw new WireCodecException("envelope body is not valid UTF-8", ex);
            }

            JsonValue envelope;
            try
            {
                envelope = JsonParser.Parse(text);
            }
            catch (JsonParseException ex)
            {
                throw new WireCodecException("malformed JSON envelope", ex);
            }

            if (envelope.Kind != JsonKind.Object)
            {
                throw new WireCodecException("JSON envelope is not an object");
            }

            var rawType = envelope.GetLong("type");
            if (rawType <= 0 || rawType > byte.MaxValue)
            {
                // Fail closed exactly as both servers do: 0 is not a type, and it
                // is what arbitrary bytes decode to.
                throw new WireCodecException($"invalid message type {rawType}");
            }

            var type = (MsgType)rawType;
            envelope.TryGetMember("payload", out var payload);
            return new WireFrame(type, ReadPayload(type, payload), WireEncoding.Json);
        }

        private static string WritePayload(IWireMessage payload)
        {
            var b = new JsonBuilder();

            switch (payload)
            {
                case null:
                case ResyncRequest _:
                    // Both servers accept an empty object here; the Go decoder also
                    // accepts null, but "{}" is what every current sender emits.
                    return "{}";

                case AuthRequest m:
                    b.BeginObject().String("token", m.Token).EndObject();
                    return b.ToString();

                case EnterWorldRequest m:
                    b.BeginObject().String("map_id", m.MapId).EndObject();
                    return b.ToString();

                case JoinTokenRequest m:
                    b.BeginObject().String("token", m.Token).EndObject();
                    return b.ToString();

                case InputMessage m:
                    b.BeginObject()
                        .Number("tick", m.Tick)
                        .Number("move_x", m.MoveX)
                        .Number("move_y", m.MoveY);
                    if (!string.IsNullOrEmpty(m.AttackTargetId))
                    {
                        b.String("attack_target_id", m.AttackTargetId);
                    }

                    b.EndObject();
                    return b.ToString();

                case PingMessage m:
                    b.BeginObject().Number("timestamp", m.Timestamp).EndObject();
                    return b.ToString();

                case PongMessage m:
                    b.BeginObject()
                        .Number("timestamp", m.Timestamp)
                        .Number("server_time", m.ServerTime)
                        .EndObject();
                    return b.ToString();

                case DisconnectMessage m:
                    b.BeginObject();
                    if (!string.IsNullOrEmpty(m.Reason))
                    {
                        b.String("reason", m.Reason);
                    }

                    b.EndObject();
                    return b.ToString();

                default:
                    throw new WireCodecException(
                        $"no JSON encoder for payload type {payload.GetType().Name}");
            }
        }

        private static IWireMessage ReadPayload(MsgType type, JsonValue payload)
        {
            switch (type)
            {
                case MsgType.AuthResp:
                    return new AuthResponse
                    {
                        Ok = payload.GetBool("ok"),
                        UserId = payload.GetString("user_id"),
                        Error = payload.GetString("error")
                    };

                case MsgType.EnterWorldResp:
                    return new EnterWorldResponse
                    {
                        ServerAddr = payload.GetString("server_addr"),
                        JoinToken = payload.GetString("join_token"),
                        Transport = payload.GetString("transport"),
                        Error = payload.GetString("error")
                    };

                case MsgType.JoinTokenResp:
                    return new JoinTokenResponse
                    {
                        Ok = payload.GetBool("ok"),
                        UserId = payload.GetString("user_id"),
                        Error = payload.GetString("error")
                    };

                case MsgType.Snapshot:
                    return ReadSnapshot(payload);

                case MsgType.Disconnect:
                    return new DisconnectMessage { Reason = payload.GetString("reason") };

                case MsgType.Kick:
                    return new KickMessage { Reason = payload.GetString("reason") };

                case MsgType.Ping:
                    return new PingMessage { Timestamp = payload.GetLong("timestamp") };

                case MsgType.Pong:
                    return new PongMessage
                    {
                        Timestamp = payload.GetLong("timestamp"),
                        ServerTime = payload.GetLong("server_time")
                    };

                default:
                    // A type this client does not model. Not an error: the servers
                    // log and ignore unknown types from us, and we do the same.
                    return null;
            }
        }

        private static SnapshotMessage ReadSnapshot(JsonValue payload)
        {
            var snapshot = new SnapshotMessage
            {
                Tick = payload.GetLong("tick"),
                AckTick = payload.GetLong("ack_tick"),
                Full = payload.GetBool("full")
            };

            foreach (var item in payload.GetArray("entities"))
            {
                snapshot.Entities.Add(new EntitySnapshot
                {
                    Id = item.GetString("id"),
                    Type = item.GetString("type"),
                    X = item.GetFloat("x"),
                    Y = item.GetFloat("y"),
                    Hp = item.GetInt("hp"),
                    MaxHp = item.GetInt("max_hp"),

                    // The JSON encoding never interns; the member is read anyway so
                    // that a server which starts sending it is handled by the same
                    // resolution path rather than silently dropping to id-only.
                    Handle = item.GetUInt("handle")
                });
            }

            foreach (var removed in payload.GetArray("removed"))
            {
                var id = removed.AsString();
                if (!string.IsNullOrEmpty(id))
                {
                    snapshot.Removed.Add(id);
                }
            }

            return snapshot;
        }
    }
}
