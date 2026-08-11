namespace Cuvara.Netcode.Protocol
{
    /// <summary>
    /// Marker for a decoded wire payload. Implemented by every message type in
    /// <c>Scripts.Net.Protocol.Messages</c> so a codec can return one without the
    /// caller having to know the encoding it came from.
    /// </summary>
    public interface IWireMessage
    {
    }
}
