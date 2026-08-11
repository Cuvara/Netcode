namespace Cuvara.Netcode.Connection
{
    /// <summary>Why a connection ended. Reported exactly once per connection.</summary>
    public enum DisconnectCause
    {
        /// <summary>This client closed the connection deliberately.</summary>
        LocalClose = 0,

        /// <summary>The peer half-closed the socket without saying anything first.</summary>
        PeerClosed = 1,

        /// <summary>
        /// The server evicted us: <c>kick</c> followed by the paired
        /// <c>disconnect</c>. Both frames are one event.
        /// </summary>
        Kicked = 2,

        /// <summary>
        /// The server sent an unpaired <c>disconnect</c> — a drain
        /// (<c>server_shutdown</c>), or an eviction from a build that predates
        /// <c>kick</c>.
        /// </summary>
        ServerDisconnect = 3,

        /// <summary>No pong arrived inside the timeout, so we declared the link dead.</summary>
        HeartbeatTimeout = 4,

        /// <summary>The socket failed.</summary>
        TransportError = 5,

        /// <summary>The peer sent something we cannot decode. Not recoverable in place.</summary>
        ProtocolError = 6
    }
}
