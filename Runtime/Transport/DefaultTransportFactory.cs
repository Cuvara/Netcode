namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// Produces the transports this client implements.
    /// </summary>
    public sealed class DefaultTransportFactory : ITransportFactory
    {
        private readonly string _transportKey;

        /// <summary>
        /// Creates the factory. <paramref name="transportKey"/> is passed to
        /// <see cref="KcpTransport"/> for per-session encryption; empty or null
        /// means plaintext (the dev default).
        /// </summary>
        public DefaultTransportFactory(string transportKey = null)
        {
            _transportKey = transportKey;
        }

        public ITransport Create(TransportKind kind)
        {
            switch (kind)
            {
                case TransportKind.Tcp:
                    return new TcpTransport();

                case TransportKind.Kcp:
                    return new KcpTransport(_transportKey);

                default:
                    throw new TransportException($"unsupported transport {kind}");
            }
        }
    }
}
