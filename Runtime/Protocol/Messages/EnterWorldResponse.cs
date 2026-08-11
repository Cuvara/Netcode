namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// gateway -> client (4). Carries the game server to dial directly; the
    /// gateway is a redirector and never sees gameplay traffic (ADR-3).
    /// </summary>
    public sealed class EnterWorldResponse : IWireMessage
    {
        /// <summary><c>host:port</c> of the assigned game server.</summary>
        public string ServerAddr { get; set; } = string.Empty;

        /// <summary>
        /// Single-use, 30-second join token pinned to that one server. Replaying
        /// it is rejected with <c>Token already used</c>; a retry needs a fresh
        /// <c>enter_world</c>.
        /// </summary>
        public string JoinToken { get; set; } = string.Empty;

        /// <summary>
        /// Realtime transport the <b>game server</b> speaks: <c>tcp</c> or
        /// <c>kcp</c>. Empty means <c>tcp</c>. It is unrelated to the transport
        /// used to reach the gateway — the two are configured independently.
        /// </summary>
        public string Transport { get; set; } = string.Empty;

        /// <summary>Gateway error string, from the closed set. Empty on success.</summary>
        public string Error { get; set; } = string.Empty;
    }
}
