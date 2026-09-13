using System.Text;
using NUnit.Framework;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// <c>EnterWorldRequest.PartyId</c> on the wire: one message asking for two different
    /// things (ADR-26 decision 1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The property worth pinning is not "the field encodes" — it is that adding the field
    /// changed <b>nothing</b> for a map entry. A client asking for a map must produce the same
    /// bytes it produced before this field existed, in BOTH encodings. Protobuf gives that for
    /// free; JSON does not, and only does here because the encoder omits the field when it is
    /// empty, mirroring the backend's <c>omitempty</c>.
    /// </para>
    /// <para>
    /// Without that, every JSON map entry would start carrying <c>"party_id":""</c> — harmless
    /// to a current server, and a silent divergence from the Go side's bytes for anything that
    /// compares them, which the golden vectors do.
    /// </para>
    /// </remarks>
    public class PartyEntryWireTests
    {
        private const string Content = "dungeon_01";
        private const string Party = "8f14e45fceea167a5a36dedd4bea2543";

        [Test]
        public void DungeonEntry_CarriesThePartyIdThroughProtobuf()
        {
            var codec = new ProtobufWireCodec();

            byte[] body = codec.EncodeBody(
                MsgType.EnterWorld,
                new EnterWorldRequest { MapId = Content, PartyId = Party });

            var envelope = RpgMmo.Wire.V1.Envelope.Parser.ParseFrom(body);
            Assert.AreEqual((uint)MsgType.EnterWorld, envelope.Type);

            var decoded = RpgMmo.Wire.V1.EnterWorldRequest.Parser.ParseFrom(envelope.Payload);
            Assert.AreEqual(Content, decoded.MapId);
            Assert.AreEqual(Party, decoded.PartyId, "the gateway reads this to pick the dungeon path");
        }

        [Test]
        public void MapEntry_SendsNoPartyIdThroughProtobuf()
        {
            var codec = new ProtobufWireCodec();

            byte[] body = codec.EncodeBody(MsgType.EnterWorld, new EnterWorldRequest { MapId = "map_01" });

            var envelope = RpgMmo.Wire.V1.Envelope.Parser.ParseFrom(body);
            var decoded = RpgMmo.Wire.V1.EnterWorldRequest.Parser.ParseFrom(envelope.Payload);

            // Empty, not absent-as-in-null: proto3 has no difference, and the gateway branches
            // on empty. The assertion is that a map entry can never be read as a dungeon one.
            Assert.AreEqual(string.Empty, decoded.PartyId);
        }

        [Test]
        public void MapEntry_ProducesTheSameJsonItProducedBeforeThisFieldExisted()
        {
            var codec = new JsonWireCodec();

            byte[] body = codec.EncodeBody(MsgType.EnterWorld, new EnterWorldRequest { MapId = "map_01" });
            string json = Encoding.UTF8.GetString(body);

            StringAssert.Contains("\"map_id\":\"map_01\"", json);
            StringAssert.DoesNotContain("party_id", json,
                "an empty party id must be OMITTED, not sent as \"\" -- the backend uses omitempty " +
                "and the golden vectors compare bytes across the two implementations");
        }

        [Test]
        public void DungeonEntry_CarriesThePartyIdThroughJson()
        {
            var codec = new JsonWireCodec();

            byte[] body = codec.EncodeBody(
                MsgType.EnterWorld,
                new EnterWorldRequest { MapId = Content, PartyId = Party });
            string json = Encoding.UTF8.GetString(body);

            StringAssert.Contains("\"map_id\":\"" + Content + "\"", json);
            StringAssert.Contains("\"party_id\":\"" + Party + "\"", json);
        }

        /// <summary>
        /// A default-constructed request is a map request. Stated as a test because the whole
        /// design rests on it: every existing caller keeps its old behaviour without being
        /// touched.
        /// </summary>
        [Test]
        public void ANewRequestIsAMapRequest()
        {
            var request = new EnterWorldRequest();

            Assert.AreEqual(string.Empty, request.PartyId);
            Assert.AreEqual(string.Empty, request.MapId);
        }
    }
}
