namespace Cuvara.Netcode.Codec
{
    /// <summary>
    /// Identifies a frame's encoding from its first body byte (ADR-9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Protobuf <c>Envelope</c> always begins <c>0x08</c> — the tag byte for
    /// field 1 (<c>type</c>), which proto3 never elides because the type is always
    /// >= 1 — and a JSON envelope always begins <c>{</c> (<c>0x7B</c>). Those
    /// cannot collide, so there is no negotiation, no version field, and no extra
    /// round trip.
    /// </para>
    /// <para>
    /// Both servers treat "not <c>{</c>" as Protobuf. This client is stricter and
    /// reports anything that is neither marker as <see cref="WireEncoding.Unknown"/>,
    /// because a client has no reason to accept a body it cannot classify and a
    /// clear error beats a Protobuf parse failure three layers down.
    /// </para>
    /// </remarks>
    public static class EncodingSniffer
    {
        /// <summary>First byte of a JSON body.</summary>
        public const byte JsonMarker = (byte)'{';

        /// <summary>First byte of a Protobuf <c>Envelope</c> body.</summary>
        public const byte ProtobufMarker = 0x08;

        public static WireEncoding Sniff(byte[] body)
        {
            if (body == null || body.Length == 0)
            {
                return WireEncoding.Unknown;
            }

            if (body[0] == JsonMarker)
            {
                return WireEncoding.Json;
            }

            return body[0] == ProtobufMarker ? WireEncoding.Protobuf : WireEncoding.Unknown;
        }
    }
}
