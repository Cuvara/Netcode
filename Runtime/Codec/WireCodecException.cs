using System;

namespace Cuvara.Netcode.Codec
{
    /// <summary>
    /// A frame could not be encoded or decoded. Fatal for the connection it
    /// happened on: the peer and this client disagree about the bytes, and there is
    /// no framing-level resynchronisation short of reconnecting.
    /// </summary>
    public sealed class WireCodecException : Exception
    {
        public WireCodecException(string message)
            : base(message)
        {
        }

        public WireCodecException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
