namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// game server -> client (6). On failure the game server closes the socket
    /// straight after this frame.
    /// </summary>
    public sealed class JoinTokenResponse : IWireMessage
    {
        public bool Ok { get; set; }

        public string UserId { get; set; } = string.Empty;

        /// <summary>
        /// <c>Invalid or expired token</c>, <c>Token is for a different server</c>,
        /// <c>Token already used</c>, or <c>Expected JoinToken message</c>.
        /// </summary>
        public string Error { get; set; } = string.Empty;
    }
}
