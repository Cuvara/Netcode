using NUnit.Framework;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The wire protocol version handshake, client side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The number names what the schema MEANS, not its shape or its encoding. Shape is
    /// self-describing and encoding is sniffed from byte 0; neither catches two peers
    /// that parse every byte and then disagree about what a field means. Before this
    /// field existed, client and server agreed by convention alone — a skewed build
    /// connected, parsed everything, and was confidently wrong about the world.
    /// </para>
    /// <para>
    /// The server side of each hop is what refuses; this fixture covers the three things
    /// the CLIENT is responsible for: sending its version on both hops, reading a
    /// server's back over both encodings, and never retrying a refusal that no retry can
    /// change.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class ProtocolVersionTests
    {
        // ---- The constant itself -------------------------------------------------

        /// <summary>
        /// Version numbering starts at 1 so "absent" and "version zero" cannot collide —
        /// the trap documented at length on <c>EntitySnapshot.speed</c>. proto3 elides a
        /// zero, so a 0 on the wire has exactly one meaning: nobody advertised.
        /// </summary>
        [Test]
        public void VersionNumberingStartsAtOne()
        {
            Assert.That(WireProtocolVersion.Unversioned, Is.EqualTo(0u));
            Assert.That(WireProtocolVersion.Current, Is.GreaterThanOrEqualTo(1u),
                "a version of 0 would be indistinguishable from an elided proto3 field");
        }

        /// <summary>
        /// Pinned deliberately. This constant is mirrored by hand in three languages
        /// (Go <c>shared/messages.WireProtocolVersion</c>, C# server
        /// <c>WireProtocol.ProtocolVersion</c>, here); no language can be authoritative
        /// for the others, so each side pins it and a change here is a change that must
        /// be made in all three. If this assertion fails, the other two need the same
        /// edit and the API doc's version table needs a new row.
        /// </summary>
        [Test]
        public void CurrentVersionIsPinned()
        {
            Assert.That(WireProtocolVersion.Current, Is.EqualTo(1u),
                "mirror any bump in shared/messages/messages.go and GameServer/Net/WireProtocol.cs");
        }

        [Test]
        public void MatchingServerIsCompatible()
        {
            Assert.That(WireProtocolVersion.IsCompatible(WireProtocolVersion.Current), Is.True);
            Assert.That(WireProtocolVersion.IsUnversioned(WireProtocolVersion.Current), Is.False);
        }

        /// <summary>
        /// An unversioned server is admitted, matching the servers' own shipping default
        /// of admitting unversioned peers. Refusing here would make the client the
        /// strictest party in the system and lock a working fleet out of itself; the
        /// migration is driven from the server's --min-protocol-version, in one place.
        /// </summary>
        [Test]
        public void UnversionedServerIsCompatibleButFlagged()
        {
            Assert.That(WireProtocolVersion.IsCompatible(WireProtocolVersion.Unversioned), Is.True,
                "the client must not be stricter than the servers' own default");
            Assert.That(WireProtocolVersion.IsUnversioned(WireProtocolVersion.Unversioned), Is.True,
                "'nobody checked' must stay distinguishable from 'checked and agreed'");
        }

        /// <summary>
        /// A server one version AHEAD is incompatible just as firmly as one behind. This
        /// client cannot know what a later version changed, so admitting it would be the
        /// guess the whole mechanism exists to prevent.
        /// </summary>
        [Test]
        public void DifferentServerVersionIsIncompatibleInBothDirections()
        {
            Assert.That(WireProtocolVersion.IsCompatible(WireProtocolVersion.Current + 1), Is.False);
            Assert.That(WireProtocolVersion.IsCompatible(9999u), Is.False);
        }

        // ---- The client sends its version by default -----------------------------

        /// <summary>
        /// Both handshake messages advertise without the caller having to remember. A
        /// client that silently sent 0 would be admitted on trust everywhere and the
        /// whole mechanism would be inert.
        /// </summary>
        [Test]
        public void HandshakeRequestsAdvertiseByDefault()
        {
            Assert.That(new AuthRequest().ProtocolVersion, Is.EqualTo(WireProtocolVersion.Current));
            Assert.That(new JoinTokenRequest().ProtocolVersion, Is.EqualTo(WireProtocolVersion.Current));
        }

        // ---- Round-trip over both encodings --------------------------------------

        // The client models only server->client payloads on the decode side (a request
        // it never receives), so a request cannot be round-tripped through this codec.
        // These assert on the ENCODED BYTES instead, which is the stronger claim anyway:
        // what matters is that the field lands where the servers actually look for it.
        // That the servers then read it correctly is proven on their side, by
        // shared/messages TestProtocolVersionRoundTripsBothEncodings (Go) and
        // ProtocolVersionHandshakeTests (C#), both of which decode real client frames.

        /// <summary>
        /// The JSON body carries the exact snake_case key the servers read. This is the
        /// legacy encoding, and its field names are matched literally on both servers
        /// (Go struct tags, and a hand-written Utf8JsonReader in the C# game server), so
        /// a typo here is a field that silently never arrives.
        /// </summary>
        [Test]
        public void JsonRequestsCarryTheVersionKey()
        {
            var codec = new JsonWireCodec();

            var auth = System.Text.Encoding.UTF8.GetString(
                codec.EncodeBody(MsgType.Auth, new AuthRequest { Token = "jwt" }));
            Assert.That(auth, Does.Contain("\"protocol_version\":1"));

            var join = System.Text.Encoding.UTF8.GetString(
                codec.EncodeBody(MsgType.JoinToken, new JoinTokenRequest { Token = "jt" }));
            Assert.That(join, Does.Contain("\"protocol_version\":1"));
        }

        /// <summary>
        /// Zero is omitted from the JSON body, so the two encodings agree: absent means
        /// "did not advertise" in both. Writing an explicit 0 here would make a JSON
        /// client look unversioned-but-present while a Protobuf one looked simply absent
        /// — two spellings of one state, which is how a second convention starts.
        /// </summary>
        [Test]
        public void JsonOmitsAnUnadvertisedVersion()
        {
            var codec = new JsonWireCodec();
            var body = System.Text.Encoding.UTF8.GetString(codec.EncodeBody(
                MsgType.JoinToken,
                new JoinTokenRequest { Token = "jt", ProtocolVersion = WireProtocolVersion.Unversioned }));

            Assert.That(body, Does.Not.Contain("protocol_version"));
        }

        /// <summary>
        /// Protobuf actually emits the field, and elides it at zero. The length
        /// difference is the observable proof of both halves without needing the
        /// generated types in this assembly.
        /// </summary>
        [Test]
        public void ProtobufEmitsTheVersionAndElidesZero()
        {
            var codec = new ProtobufWireCodec();

            var advertised = codec.EncodeBody(
                MsgType.JoinToken,
                new JoinTokenRequest { Token = "jt", ProtocolVersion = WireProtocolVersion.Current });
            var silent = codec.EncodeBody(
                MsgType.JoinToken,
                new JoinTokenRequest { Token = "jt", ProtocolVersion = WireProtocolVersion.Unversioned });

            Assert.That(advertised.Length, Is.GreaterThan(silent.Length),
                "an advertised version must actually reach the wire");
            Assert.That(silent.Length, Is.LessThan(advertised.Length),
                "proto3 must elide the zero, so absent and 'version zero' stay one state");
        }

        /// <summary>
        /// A server-to-client envelope, hand-built. The codec's <c>EncodeBody</c> only
        /// covers client-to-server payloads — the client never sends a join response —
        /// so an inbound test frame has to be written the way the servers write it.
        /// (Same helper shape as WireConnectionDispatchTests.)
        /// </summary>
        private static byte[] Inbound(MsgType type, string payloadJson) =>
            System.Text.Encoding.UTF8.GetBytes(
                "{\"type\":" + (int)type + ",\"payload\":" + payloadJson + "}");

        /// <summary>
        /// A server that predates the field replies without one. Over Protobuf that is
        /// byte-identical to an explicit 0, which is exactly why 0 is reserved — and the
        /// JSON encoding omits it for the same reason, so both spell "nobody advertised"
        /// the same way.
        /// </summary>
        [Test]
        public void AbsentServerVersionDecodesAsUnversioned()
        {
            var codec = new JsonWireCodec();
            var frame = codec.DecodeBody(Inbound(
                MsgType.JoinTokenResp,
                "{\"ok\":true,\"user_id\":\"u-42\",\"tick_rate\":60}"));

            var decoded = (JoinTokenResponse)frame.Payload;

            Assert.That(decoded.ProtocolVersion, Is.EqualTo(WireProtocolVersion.Unversioned));
            Assert.That(WireProtocolVersion.IsUnversioned(decoded.ProtocolVersion), Is.True);
            Assert.That(decoded.TickRate, Is.EqualTo(60u), "the other fields must be unaffected");
        }

        /// <summary>A server's advertised version reaches the client on both hops.</summary>
        [Test]
        public void ServerVersionIsReadBack()
        {
            var codec = new JsonWireCodec();

            var join = codec.DecodeBody(Inbound(
                MsgType.JoinTokenResp,
                "{\"ok\":true,\"user_id\":\"u-42\",\"tick_rate\":60,\"protocol_version\":1}"));
            Assert.That(((JoinTokenResponse)join.Payload).ProtocolVersion, Is.EqualTo(1u));

            // Carried on a REJECTION too, unlike tick_rate: a client refused for a
            // mismatch has to be told which version it failed against, or the refusal is
            // as opaque as the parse error it replaces.
            var auth = codec.DecodeBody(Inbound(
                MsgType.AuthResp,
                "{\"ok\":false,\"error\":\"protocol_version_mismatch\",\"protocol_version\":1}"));
            var authResp = (AuthResponse)auth.Payload;
            Assert.That(authResp.ProtocolVersion, Is.EqualTo(1u));
            Assert.That(authResp.Error, Is.EqualTo(KickReasons.ProtocolVersionMismatch));
        }

        // ---- A refusal must never be retried -------------------------------------

        /// <summary>
        /// The reason string is the servers' own machine-readable token. Both hops use
        /// it, and clients branch on this exact value.
        /// </summary>
        [Test]
        public void MismatchReasonMatchesTheServers()
        {
            Assert.That(KickReasons.ProtocolVersionMismatch, Is.EqualTo("protocol_version_mismatch"));
        }

        /// <summary>
        /// The behaviour that actually protects a player from a confusing session: a
        /// version refusal is permanent. Treating it as transient burns the whole
        /// reconnect budget against a wall and then reports "could not join", burying the
        /// one message that said what was really wrong.
        /// </summary>
        [Test]
        public void VersionMismatchIsAPermanentFailure()
        {
            Assert.That(
                ReconnectPolicy.IsPermanentServerError(KickReasons.ProtocolVersionMismatch),
                Is.True,
                "no number of retries turns this client into a different build");

            Assert.That(
                ReconnectPolicy.IsPermanentFailure(
                    new NetworkException("refused", KickReasons.ProtocolVersionMismatch)),
                Is.True);
        }

        /// <summary>
        /// A transient refusal must stay transient — the permanent set must not have
        /// grown to swallow the errors reconnect exists for.
        /// </summary>
        [Test]
        public void OrdinaryTransientErrorsStayRetryable()
        {
            Assert.That(ReconnectPolicy.IsPermanentServerError("server is starting, retry shortly"), Is.False);
            Assert.That(ReconnectPolicy.IsPermanentServerError(""), Is.False);
        }
    }
}
