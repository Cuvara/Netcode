using System;

namespace Cuvara.Netcode.Transport
{
    /// <summary>Parsing for the <c>transport</c> field of <c>enter_world_resp</c>.</summary>
    public static class TransportKinds
    {
        /// <summary>
        /// Maps the wire string onto a transport.
        /// </summary>
        /// <remarks>
        /// <b>Empty means TCP.</b> That is what registry entries written before the
        /// field existed carry, and the field describes the <i>game server</i>, not
        /// the hop that delivered it — the two are configured independently, so
        /// defaulting to "whatever we used to reach the gateway" would be wrong.
        /// </remarks>
        public static TransportKind Parse(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return TransportKind.Tcp;
            }

            if (string.Equals(value, "tcp", StringComparison.OrdinalIgnoreCase))
            {
                return TransportKind.Tcp;
            }

            if (string.Equals(value, "kcp", StringComparison.OrdinalIgnoreCase))
            {
                return TransportKind.Kcp;
            }

            throw new TransportException($"unknown transport '{value}' in enter_world_resp");
        }
    }
}
