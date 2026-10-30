using System;

namespace Cuvara.Netcode.Transport
{
    /// <summary>A transport-level failure: dial, framing, or a broken link.</summary>
    public class TransportException : Exception
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

    /// <summary>A TLS certificate did not match the certificate explicitly pinned by the client.</summary>
    public sealed class TlsPinMismatchException : TransportException
    {
        public TlsPinMismatchException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
