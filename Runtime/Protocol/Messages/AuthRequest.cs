namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>client -> gateway (1). Authenticates the connection with a JWT.</summary>
    public sealed class AuthRequest : IWireMessage
    {
        /// <summary>JWT issued by the meta backend, verified locally by the gateway.</summary>
        public string Token { get; set; } = string.Empty;
    }
}
