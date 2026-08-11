using Cuvara.Netcode.Protocol;

namespace Cuvara.Netcode.Codec
{
    /// <summary>One decoded frame: its type, its payload, and how it was encoded.</summary>
    public readonly struct WireFrame
    {
        public WireFrame(MsgType type, IWireMessage payload, WireEncoding encoding)
        {
            Type = type;
            Payload = payload;
            Encoding = encoding;
        }

        public MsgType Type { get; }

        /// <summary>
        /// The decoded payload, or null for a type this client does not model.
        /// An unknown type is not an error — both servers log and ignore ours, and
        /// we do the same with theirs.
        /// </summary>
        public IWireMessage Payload { get; }

        public WireEncoding Encoding { get; }
    }
}
