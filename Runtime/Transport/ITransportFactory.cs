namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// Creates a transport of a given kind. The gateway hop asks for
    /// <see cref="TransportKind.Tcp"/> or <see cref="TransportKind.TcpTls"/>; the gameplay
    /// hop asks for <see cref="TransportKind.Kcp"/> and nothing else (realtime gameplay is
    /// KCP/UDP only, enforced by <c>GameSessionClient</c> before this is called).
    /// </summary>
    public interface ITransportFactory
    {
        ITransport Create(TransportKind kind);
    }
}
