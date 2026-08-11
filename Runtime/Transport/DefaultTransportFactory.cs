namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// Produces the transports this client implements.
    /// </summary>
    /// <remarks>
    /// KCP is deliberately absent rather than silently downgraded to TCP. A game
    /// server that advertises <c>kcp</c> is not listening on TCP at all, so falling
    /// back would produce a connection refused with no explanation of why; failing
    /// here names the missing piece instead.
    /// </remarks>
    public sealed class DefaultTransportFactory : ITransportFactory
    {
        public ITransport Create(TransportKind kind)
        {
            switch (kind)
            {
                case TransportKind.Tcp:
                    return new TcpTransport();

                case TransportKind.Kcp:
                    throw new TransportException(
                        "the assigned game server speaks KCP, which this client does not implement yet");

                default:
                    throw new TransportException($"unsupported transport {kind}");
            }
        }
    }
}
