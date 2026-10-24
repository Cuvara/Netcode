using System.Text;
using Google.Protobuf;
using NUnit.Framework;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;
using Shared.GameLogic.Systems;
using Pb = RpgMmo.Wire.V1;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Speed presence through both codecs (Unity EditMode only: the generated protobuf types
    /// are a plugin the headless project does not compile). Absent decodes as NaN, a present
    /// value as itself, and <c>changed_fields</c> bit 0x0020 governs on a partial in BOTH
    /// encodings: a JSON partial with <c>"speed":0</c> and the bit clear is "unchanged".
    /// </summary>
    public class SnapshotSpeedCodecTests
    {
        private const string Id = "e1";
        private const uint XY = SnapshotFieldBits.X | SnapshotFieldBits.Y;

        private static byte[] ProtoBody(Pb.SnapshotMessage snapshot) =>
            new Pb.Envelope
            {
                Type = (uint)MsgType.Snapshot,
                Payload = ByteString.CopyFrom(snapshot.ToByteArray()),
            }.ToByteArray();

        private static EntitySnapshot DecodeProto(Pb.EntitySnapshot e)
        {
            var pb = new Pb.SnapshotMessage { Tick = 1 };
            pb.Entities.Add(e);
            return ((SnapshotMessage)new ProtobufWireCodec().DecodeBody(ProtoBody(pb)).Payload).Entities[0];
        }

        private static EntitySnapshot DecodeJson(string entityJson)
        {
            var body = Encoding.UTF8.GetBytes(
                "{\"type\":" + (int)MsgType.Snapshot + ",\"payload\":{\"tick\":1,\"entities\":[" +
                entityJson + "]}}");
            return ((SnapshotMessage)new JsonWireCodec().DecodeBody(body).Payload).Entities[0];
        }

        [Test]
        public void SpeedBitConstantMatchesTheSharedFieldBit()
        {
            Assert.AreEqual(SnapshotFieldBits.Speed, EntitySnapshot.SpeedFieldBit);
        }

        // ── Protobuf ──

        [Test]
        public void Proto_PresentZeroDecodesAsZero()
        {
            var e = DecodeProto(new Pb.EntitySnapshot { Id = Id, Speed = 0f });
            Assert.AreEqual(0f, e.Speed);
        }

        [Test]
        public void Proto_PresentPositiveDecodesAsItself()
        {
            Assert.AreEqual(4.5f, DecodeProto(new Pb.EntitySnapshot { Id = Id, Speed = 4.5f }).Speed);
        }

        [Test]
        public void Proto_AbsentSpeedDecodesAsNaN()
        {
            Assert.IsTrue(float.IsNaN(DecodeProto(new Pb.EntitySnapshot { Id = Id }).Speed));
        }

        [Test]
        public void Proto_PartialWithoutSpeedBitIsNaNEvenIfPresent()
        {
            var e = DecodeProto(new Pb.EntitySnapshot { Id = Id, Speed = 0f, ChangedFields = XY });
            Assert.IsTrue(float.IsNaN(e.Speed), "bit 0x0020 clear: the value is not this partial's to give");
        }

        [Test]
        public void Proto_PartialWithSpeedBitAppliesAZero()
        {
            var e = DecodeProto(new Pb.EntitySnapshot
            {
                Id = Id, Speed = 0f, ChangedFields = XY | SnapshotFieldBits.Speed,
            });
            Assert.AreEqual(0f, e.Speed);
        }

        [Test]
        public void Proto_PooledDecodeDoesNotCarryTheLastSpeedOver()
        {
            var codec = ProtobufWireCodec.CreatePooled();
            var first = new Pb.SnapshotMessage { Tick = 1 };
            first.Entities.Add(new Pb.EntitySnapshot { Id = Id, Speed = 6f });
            codec.DecodeBody(ProtoBody(first));
            var second = new Pb.SnapshotMessage { Tick = 2 };
            second.Entities.Add(new Pb.EntitySnapshot { Id = Id });

            var e = ((SnapshotMessage)codec.DecodeBody(ProtoBody(second)).Payload).Entities[0];

            Assert.IsTrue(float.IsNaN(e.Speed), "a pooled entity must not keep the previous speed");
        }

        // ── JSON ──

        [Test]
        public void Json_PresentZeroDecodesAsZero()
        {
            Assert.AreEqual(0f, DecodeJson("{\"id\":\"e1\",\"speed\":0}").Speed);
        }

        [Test]
        public void Json_PresentPositiveDecodesAsItself()
        {
            Assert.AreEqual(4.5f, DecodeJson("{\"id\":\"e1\",\"speed\":4.5}").Speed);
        }

        [Test]
        public void Json_AbsentKeyDecodesAsNaN()
        {
            Assert.IsTrue(float.IsNaN(DecodeJson("{\"id\":\"e1\"}").Speed));
        }

        [Test]
        public void Json_PartialWithZeroSpeedAndNoBitIsNaN()
        {
            var e = DecodeJson("{\"id\":\"e1\",\"speed\":0,\"changed_fields\":" + XY + "}");
            Assert.IsTrue(float.IsNaN(e.Speed), "a JSON partial with speed 0 and no bit must not read as immobile");
        }

        [Test]
        public void Json_PartialWithSpeedBitAppliesAZero()
        {
            var mask = XY | SnapshotFieldBits.Speed;
            Assert.AreEqual(0f, DecodeJson("{\"id\":\"e1\",\"speed\":0,\"changed_fields\":" + mask + "}").Speed);
        }

        [Test]
        public void Json_ZeroMaskKeyframeKeepsAPresentZero()
        {
            Assert.AreEqual(0f, DecodeJson("{\"id\":\"e1\",\"speed\":0,\"changed_fields\":0}").Speed);
        }
    }
}
