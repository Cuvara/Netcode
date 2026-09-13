using System;
using System.Text;
using Google.Protobuf;
using NUnit.Framework;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Protocol;
using Msg = Cuvara.Netcode.Protocol.Messages;
using Pb = RpgMmo.Wire.V1;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The two ADR-25 fields have to survive BOTH codecs, and a field that decodes to empty
    /// looks exactly like a pre-ADR-25 server — which the client is required to accept. So a
    /// plumbing mistake here does not fail, it silently degrades every session to unverified.
    /// That is the failure these tests exist for.
    /// </summary>
    public class IdentityWireTests
    {
        private static byte[] Key32()
        {
            var k = new byte[32];
            for (int i = 0; i < k.Length; i++) k[i] = (byte)(i + 1);
            return k;
        }

        private static byte[] Sig64()
        {
            var s = new byte[64];
            for (int i = 0; i < s.Length; i++) s[i] = (byte)(255 - i);
            return s;
        }

        [Test]
        public void Protobuf_EnterWorldResponse_CarriesTheIdentityKey()
        {
            byte[] key = Key32();
            var body = new Pb.Envelope
            {
                Type = (uint)MsgType.EnterWorldResp,
                Payload = new Pb.EnterWorldResponse
                {
                    ServerAddr = "10.0.0.5:7019",
                    JoinToken = "t",
                    Transport = "tcp",
                    ServerPublicKey = ByteString.CopyFrom(key),
                }.ToByteString(),
            }.ToByteArray();

            var frame = new ProtobufWireCodec().DecodeBody(body);
            var resp = (Msg.EnterWorldResponse)frame.Payload;

            Assert.AreEqual(key, resp.ServerPublicKey);
        }

        [Test]
        public void Protobuf_SealedServerHello_CarriesTheSignature()
        {
            byte[] sig = Sig64();
            var body = new Pb.Envelope
            {
                Type = (uint)MsgType.SealedServerHello,
                Payload = new Pb.SealedServerHello
                {
                    PublicKey = ByteString.CopyFrom(Key32()),
                    Binding = ByteString.CopyFrom(new byte[32]),
                    ServerSignature = ByteString.CopyFrom(sig),
                }.ToByteString(),
            }.ToByteArray();

            var frame = new ProtobufWireCodec().DecodeBody(body);
            var hello = (Msg.SealedServerHello)frame.Payload;

            Assert.AreEqual(sig, hello.ServerSignature);
        }

        [Test]
        public void Json_EnterWorldResponse_ReadsTheKeyAsBase64()
        {
            // Go's encoding/json renders a []byte as standard-alphabet padded base64. This is
            // the exact shape the gateway emits on the JSON path.
            byte[] key = Key32();
            string json =
                "{\"type\":" + (int)MsgType.EnterWorldResp + ",\"payload\":{" +
                "\"server_addr\":\"10.0.0.5:7019\",\"join_token\":\"t\",\"transport\":\"tcp\"," +
                "\"server_public_key\":\"" + Convert.ToBase64String(key) + "\"}}";

            var frame = new JsonWireCodec().DecodeBody(Encoding.UTF8.GetBytes(json));
            var resp = (Msg.EnterWorldResponse)frame.Payload;

            Assert.AreEqual(key, resp.ServerPublicKey);
        }

        [Test]
        public void Json_AMissingKeyIsEmpty_NotNull()
        {
            // A pre-ADR-25 gateway sends no such field, and the client must treat that as "no
            // key offered" rather than dereferencing null on the join path.
            string json =
                "{\"type\":" + (int)MsgType.EnterWorldResp + ",\"payload\":{" +
                "\"server_addr\":\"10.0.0.5:7019\",\"join_token\":\"t\",\"transport\":\"tcp\"}}";

            var frame = new JsonWireCodec().DecodeBody(Encoding.UTF8.GetBytes(json));
            var resp = (Msg.EnterWorldResponse)frame.Payload;

            Assert.IsNotNull(resp.ServerPublicKey);
            Assert.AreEqual(0, resp.ServerPublicKey.Length);
        }

        [Test]
        public void Json_JunkBase64ArrivesAsNoKey_RatherThanThrowing()
        {
            // These bytes are attacker-chosen. An exception on the join path is a denial of
            // service; "no key" reaches the verifier, which refuses it when identity is
            // required and reports it when it is not.
            string json =
                "{\"type\":" + (int)MsgType.EnterWorldResp + ",\"payload\":{" +
                "\"server_addr\":\"10.0.0.5:7019\",\"join_token\":\"t\",\"transport\":\"tcp\"," +
                "\"server_public_key\":\"!!!! not base64 !!!!\"}}";

            var frame = new JsonWireCodec().DecodeBody(Encoding.UTF8.GetBytes(json));
            var resp = (Msg.EnterWorldResponse)frame.Payload;

            Assert.AreEqual(0, resp.ServerPublicKey.Length);
        }
    }
}
