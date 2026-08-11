using System;

namespace Cuvara.Netcode.Transport
{
    /// <summary>A transport-level failure: dial, framing, or a broken link.</summary>
    public sealed class TransportException : Exception
    {
        public TransportException(string message)
            : base(message)
        {
        }

        public TransportException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
