namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// Either direction (9). Sent by the client to leave politely, and by a
    /// server to announce an eviction or a shutdown.
    /// </summary>
    /// <remarks>
    /// A gateway eviction sends <see cref="KickMessage"/> first and this frame
    /// second, carrying the same reason. They are one event, not two.
    /// </remarks>
    public sealed class DisconnectMessage : IWireMessage
    {
        /// <summary>Machine-readable reason, or empty. Never user-facing text.</summary>
        public string Reason { get; set; } = string.Empty;
    }
}
