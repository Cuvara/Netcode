using System;
using Google.Protobuf;
using NUnit.Framework;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The two sealed-handshake messages on the wire: Protobuf carries them, JSON refuses
    /// them loudly, and the encoding sniff still works.
    /// </summary>
    public class SealedWireMessageTests
    {
        private static byte[] Filled(int n, byte seed)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = (byte)(seed + i);
            return b;
        }

        /// <summary>The numbers are the wire contract; they are frozen by `wire.proto`.</summary>
        [Test]
        public void MsgTypeValuesMatchTheSchema()
        {
            Assert.AreEqual(16, (int)MsgType.SealedClientHello);
            Assert.AreEqual(17, (int)MsgType.SealedServerHello);
        }

        [Test]
        public void ClientHello_RoundTripsThroughProtobuf()
        {
            var codec = new ProtobufWireCodec();
            byte[] key = Filled(32, 0x40);

            byte[] body = codec.EncodeBody(MsgType.SealedClientHello, new SealedClientHello { PublicKey = key });

            // The client only ever SENDS this one, so the codec has no decoder for it — the
            // assertion that matters is that the bytes are right, which the Protobuf type
            // itself can confirm.
            var envelope = RpgMmo.Wire.V1.Envelope.Parser.ParseFrom(body);
            Assert.AreEqual((uint)MsgType.SealedClientHello, envelope.Type);

            var decoded = RpgMmo.Wire.V1.SealedClientHello.Parser.ParseFrom(envelope.Payload);
            CollectionAssert.AreEqual(key, decoded.PublicKey.ToByteArray());
        }

        [Test]
        public void ServerHello_RoundTripsThroughProtobuf()
        {
            var codec = new ProtobufWireCodec();
            byte[] key = Filled(32, 0x10);
            byte[] binding = Filled(32, 0x90);

            var wire = new RpgMmo.Wire.V1.Envelope
            {
                Type = (uint)MsgType.SealedServerHello,
                Payload = Google.Protobuf.ByteString.CopyFrom(
                    new RpgMmo.Wire.V1.SealedServerHello
                    {
                        PublicKey = Google.Protobuf.ByteString.CopyFrom(key),
                        Binding = Google.Protobuf.ByteString.CopyFrom(binding),
                    }.ToByteArray()),
            };

            WireFrame frame = codec.DecodeBody(wire.ToByteArray());

            Assert.AreEqual(MsgType.SealedServerHello, frame.Type);
            var hello = frame.Payload as SealedServerHello;
            Assert.IsNotNull(hello);
            CollectionAssert.AreEqual(key, hello.PublicKey);
            CollectionAssert.AreEqual(binding, hello.Binding);
            Assert.AreEqual(string.Empty, hello.Error);
        }

        [Test]
        public void ServerHello_CarriesTheServersRefusal()
        {
            var codec = new ProtobufWireCodec();
            var wire = new RpgMmo.Wire.V1.Envelope
            {
                Type = (uint)MsgType.SealedServerHello,
                Payload = Google.Protobuf.ByteString.CopyFrom(
                    new RpgMmo.Wire.V1.SealedServerHello { Error = "not_configured" }.ToByteArray()),
            };

            var hello = (SealedServerHello)codec.DecodeBody(wire.ToByteArray()).Payload;

            Assert.AreEqual("not_configured", hello.Error);
            Assert.AreEqual(0, hello.PublicKey.Length, "a refusal carries no key");
        }

        /// <summary>
        /// The JSON codec must refuse to encode a hello, and refuse LOUDLY.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Absent from the JSON message set is the point: key material must never be
        /// renderable into a human-readable payload, and JSON is the encoding someone is most
        /// likely to paste into an issue.
        /// </para>
        /// <para>
        /// A silent no-op would be worse than the leak it prevents. It would put an
        /// empty-bodied Envelope on the wire and the server would refuse the handshake for a
        /// reason that names nothing — a JSON client failing to connect with no cause
        /// anywhere. The throw is what turns that into a message naming the type.
        /// </para>
        /// </remarks>
        [Test]
        public void JsonCodec_RefusesToEncodeAHello_Loudly()
        {
            var codec = new JsonWireCodec();

            var ex = Assert.Throws<WireCodecException>(
                () => codec.EncodeBody(MsgType.SealedClientHello, new SealedClientHello { PublicKey = Filled(32, 1) }));

            StringAssert.Contains("SealedClientHello", ex.Message,
                "the message must name the type, or a JSON client fails to connect with no cause anywhere");
        }

        /// <summary>
        /// A sealed hello still begins with 0x08, so the peer's encoding sniff is unaffected.
        /// </summary>
        /// <remarks>
        /// The first body byte distinguishes Protobuf (<c>0x08</c>, Envelope field 1) from
        /// JSON (<c>0x7B</c>, <c>{</c>) — and, once a session is sealed, from a sealed frame
        /// (<c>0xC1</c>). These three must stay mutually exclusive; the hellos are ordinary
        /// Envelopes and travel before any key exists, so they must sniff as Protobuf.
        /// </remarks>
        [Test]
        public void AHelloIsStillSniffedAsProtobuf()
        {
            var codec = new ProtobufWireCodec();
            byte[] body = codec.EncodeBody(MsgType.SealedClientHello, new SealedClientHello { PublicKey = Filled(32, 7) });

            Assert.AreEqual(0x08, body[0]);
            Assert.AreNotEqual(0x7B, body[0]);
            Assert.AreNotEqual(Cuvara.Netcode.Crypto.SealedFrame.Marker, body[0]);
        }

        /// <summary>
        /// `EnterWorldResponse.session_key` is gone from the schema, and the client must not
        /// have grown a property for it.
        /// </summary>
        /// <remarks>
        /// It was the per-session key of the scheme ADR-22 supersedes — delivered to the
        /// client in the clear over the same plaintext transport it was meant to protect.
        /// ADR-22 records field 5 as reserved and NOT reused. A regeneration that quietly
        /// brought it back would compile and pass every other test.
        /// </remarks>
        [Test]
        public void SessionKeyIsGoneFromTheGeneratedSchema()
        {
            Type t = typeof(RpgMmo.Wire.V1.EnterWorldResponse);

            // Prove the lookup works before trusting its null. GetProperty returns null for
            // ANY name that is not there, a misspelling included, so an assertion that only
            // checks for null passes just as happily against a typo — the same shape of
            // vacuous test this suite has caught twice already.
            Assert.IsNotNull(t.GetProperty("JoinToken"), "reflection lookup is not finding real properties");

            Assert.IsNull(t.GetProperty("SessionKey"),
                "field 5 is reserved by ADR-22 and must not be reused");
        }
    }
}
