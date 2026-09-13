namespace Cuvara.Netcode.Protocol
{
    /// <summary>
    /// Machine-readable eviction reasons. Only <see cref="DuplicateLogin"/> and
    /// <see cref="ServerShutdown"/> are emitted today; everything else must be
    /// handled generically rather than switched on exhaustively.
    /// </summary>
    public static class KickReasons
    {
        /// <summary>The same user authenticated on another connection. Gateway only.</summary>
        public const string DuplicateLogin = "duplicate_login";

        /// <summary>
        /// The game server is draining. Sent as <c>disconnect{reason}</c> without
        /// a preceding kick frame.
        /// </summary>
        public const string ServerShutdown = "server_shutdown";

        /// <summary>
        /// The peer refused this client for speaking a wire protocol version it
        /// cannot serve, or for advertising none where one is required.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Arrives as <c>AuthResponse.Error</c> from the gateway or
        /// <c>JoinTokenResponse.Error</c> from the game server, never as a kick — the
        /// refusal happens during the handshake, before there is a session to kick.
        /// It is declared here because it belongs to the same closed set of
        /// machine-readable reason strings, and clients branch on it the same way.
        /// </para>
        /// <para>
        /// <b>Never retry it.</b> Nothing the client can do at runtime changes the
        /// answer: it needs a different build. Treating it as transient burns the
        /// whole reconnect budget against a wall and then reports "could not join",
        /// hiding the one error that actually said what was wrong.
        /// </para>
        /// </remarks>
        public const string ProtocolVersionMismatch = "protocol_version_mismatch";
    }
}
