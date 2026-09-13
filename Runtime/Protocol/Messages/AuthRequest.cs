namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>client -> gateway (1). Authenticates the connection with a JWT.</summary>
    public sealed class AuthRequest : IWireMessage
    {
        /// <summary>JWT issued by the meta backend, verified locally by the gateway.</summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>
        /// Wire protocol version this client implements. See
        /// <see cref="Cuvara.Netcode.Protocol.WireProtocolVersion"/>.
        /// </summary>
        /// <remarks>
        /// The gateway refuses a version it cannot serve with
        /// <c>AuthResponse.Error == "protocol_version_mismatch"</c> and closes. Zero
        /// means "not advertised"; this client always sends
        /// <see cref="Cuvara.Netcode.Protocol.WireProtocolVersion.Current"/>.
        /// </remarks>
        public uint ProtocolVersion { get; set; } = Cuvara.Netcode.Protocol.WireProtocolVersion.Current;
    }
}
