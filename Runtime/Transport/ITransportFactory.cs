namespace Cuvara.Netcode.Transport
{
    /// <summary>Creates a transport of the kind a server says it speaks.</summary>
    public interface ITransportFactory
    {
        ITransport Create(TransportKind kind);
    }
}
