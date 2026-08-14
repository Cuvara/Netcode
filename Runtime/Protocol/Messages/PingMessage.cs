namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// Either direction (11). The sender fills <see cref="Timestamp"/> with its
    /// own monotonic time in milliseconds; the receiver echoes it back in a
    /// <see cref="PongMessage"/> so the sender can measure RTT.
    /// </summary>
    public sealed class PingMessage : IWireMessage
    {
        public long Timestamp { get; set; }
    }
}
