namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// client -> game server (5). Must be the first frame on the gameplay socket;
    /// the game server rejects anything else with <c>Expected JoinToken message</c>.
    /// </summary>
    public sealed class JoinTokenRequest : IWireMessage
    {
        public string Token { get; set; } = string.Empty;

        /// <summary>
        /// Wire protocol version this client implements. See
        /// <see cref="Cuvara.Netcode.Protocol.WireProtocolVersion"/>.
        /// </summary>
        /// <remarks>
        /// Checked by the game server INDEPENDENTLY of the gateway's check on
        /// <see cref="AuthRequest"/>. Under ADR-3 these are two connections to two
        /// separately deployed processes, and the gateway never carries a snapshot —
        /// so passing the gateway says nothing about whether this client can read
        /// what this game server encodes.
        /// </remarks>
        public uint ProtocolVersion { get; set; } = Cuvara.Netcode.Protocol.WireProtocolVersion.Current;
    }
}
