namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// client -> game server (10). Empty payload; promotes this connection's next
    /// snapshot to a keyframe. Cheap but not free — it costs one full AOI
    /// snapshot, so it must not be sent per tick.
    /// </summary>
    public sealed class ResyncRequest : IWireMessage
    {
    }
}
