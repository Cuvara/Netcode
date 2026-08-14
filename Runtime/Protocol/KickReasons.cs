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
    }
}
