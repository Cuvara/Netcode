namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// server -> client (15). Typed eviction signal, always followed by a
    /// <see cref="DisconnectMessage"/> carrying the same reason and then a FIN.
    /// </summary>
    /// <remarks>
    /// <c>duplicate_login</c> is the only reason emitted today. Any other value
    /// must be handled generically: disconnect and surface a generic message.
    /// </remarks>
    public sealed class KickMessage : IWireMessage
    {
        public string Reason { get; set; } = string.Empty;
    }
}
