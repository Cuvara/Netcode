namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>client -> gateway (3). Asks the gateway to assign a map server.</summary>
    public sealed class EnterWorldRequest : IWireMessage
    {
        public string MapId { get; set; } = string.Empty;
    }
}
