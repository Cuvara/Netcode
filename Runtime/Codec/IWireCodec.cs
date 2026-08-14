using Cuvara.Netcode.Protocol;

namespace Cuvara.Netcode.Codec
{
    /// <summary>
    /// Serializes and deserializes frame bodies in one encoding.
    /// </summary>
    /// <remarks>
    /// The interface exists so Protobuf can be added later without touching the
    /// connection, the handshake or the snapshot path: only a second implementation
    /// and a registration are needed. See <c>docs/NETCODE.md</c> for what adding it
    /// requires.
    /// </remarks>
    public interface IWireCodec
    {
        WireEncoding Encoding { get; }

        /// <summary>
        /// Serializes one envelope body — no length prefix. A null payload encodes
        /// as an empty message.
        /// </summary>
        byte[] EncodeBody(MsgType type, IWireMessage payload);

        /// <summary>
        /// Parses one envelope body. Throws <see cref="WireCodecException"/> when
        /// the body is malformed or carries type 0, which is not a wire type in
        /// either encoding and is what arbitrary bytes decode to.
        /// </summary>
        WireFrame DecodeBody(byte[] body);
    }
}
