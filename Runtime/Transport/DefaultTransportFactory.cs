namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// Produces the transports this client implements.
    /// </summary>
    public sealed class DefaultTransportFactory : ITransportFactory
    {
        private readonly string _transportKey;
        private readonly TlsOptions _tls;

        /// <summary>
        /// Creates the factory.
        /// </summary>
        /// <param name="transportKey">
        /// Passed to <see cref="KcpTransport"/> for per-session encryption; empty or null
        /// means plaintext (the dev default).
        /// </param>
        /// <param name="tls">
        /// Settings for <see cref="TransportKind.TcpTls"/>. Only the gateway hop asks for
        /// that kind, so this never affects a game-server link.
        /// </param>
        public DefaultTransportFactory(string transportKey = null, TlsOptions tls = null)
        {
            _transportKey = transportKey;
            _tls = tls;
        }

        public ITransport Create(TransportKind kind)
        {
            switch (kind)
            {
                case TransportKind.Tcp:
                    return new TcpTransport();

                case TransportKind.TcpTls:
                    // Refuse rather than quietly hand back a plaintext transport. A caller
                    // that asked for TLS and got cleartext is the downgrade this hop
                    // exists to prevent, and it would look identical to success.
                    if (_tls == null)
                    {
                        throw new TransportException(
                            "TransportKind.TcpTls was requested but this factory has no TlsOptions; " +
                            "construct DefaultTransportFactory with them, or ask for TransportKind.Tcp");
                    }

                    return new TcpTransport(_tls);

                case TransportKind.Kcp:
                    return new KcpTransport(_transportKey);

                default:
                    throw new TransportException($"unsupported transport {kind}");
            }
        }
    }
}
