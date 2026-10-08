namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// Produces the transports this client implements: TCP (optionally TLS) for the
    /// <b>gateway hop</b>, KCP/UDP for the <b>gameplay hop</b>.
    /// </summary>
    /// <remarks>
    /// The two hops never share a transport. <see cref="CreateGateway"/> builds only TCP or
    /// TCP+TLS; <see cref="CreateGameplay"/> builds only <see cref="KcpTransport"/>. TCP is
    /// not a gameplay option — <c>GameSessionClient</c> refuses any kind but
    /// <see cref="TransportKind.Kcp"/> before it asks a factory for anything.
    /// </remarks>
    public sealed class DefaultTransportFactory : ITransportFactory
    {
        private readonly string _transportKey;
        private readonly TlsOptions _tls;

        /// <summary>
        /// Creates the factory.
        /// </summary>
        /// <param name="transportKey">
        /// The KCP transport key (<c>NetworkSettings.TransportKey</c>, 64 hex characters) for
        /// the gameplay hop's datagram encryption; it must equal the game server's
        /// <c>TRANSPORT_KEY</c>. Empty or null means plaintext datagrams (the dev default).
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

        /// <summary>True when a transport key was supplied for the gameplay hop.</summary>
        public bool HasTransportKey => !string.IsNullOrWhiteSpace(_transportKey);

        public ITransport Create(TransportKind kind)
        {
            switch (kind)
            {
                case TransportKind.Tcp:
                    return CreateGateway(useTls: false);

                case TransportKind.TcpTls:
                    return CreateGateway(useTls: true);

                case TransportKind.Kcp:
                    return CreateGameplay();

                default:
                    throw new TransportException($"unsupported transport {kind}");
            }
        }

        /// <summary>The gateway hop (auth + map assignment): TCP, or TCP+TLS (ADR-23).</summary>
        public ITransport CreateGateway(bool useTls)
        {
            if (!useTls)
            {
                return new TcpTransport();
            }

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
        }

        /// <summary>
        /// The gameplay hop: always <see cref="KcpTransport"/>.
        /// </summary>
        /// <exception cref="System.NotSupportedException">On WebGL, which has no UDP.</exception>
        public ITransport CreateGameplay() => new KcpTransport(_transportKey);
    }
}
