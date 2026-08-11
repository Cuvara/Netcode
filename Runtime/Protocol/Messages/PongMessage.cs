namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// Either direction (12). <see cref="Timestamp"/> echoes the probe unchanged;
    /// <see cref="ServerTime"/> is the responder's wall clock in milliseconds
    /// since the Unix epoch.
    /// </summary>
    public sealed class PongMessage : IWireMessage
    {
        public long Timestamp { get; set; }

        public long ServerTime { get; set; }
    }
}
