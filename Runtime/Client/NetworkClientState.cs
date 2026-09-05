namespace Cuvara.Netcode.Client
{
    /// <summary>Where <see cref="NetworkClient"/> is in the two-hop handshake.</summary>
    public enum NetworkClientState
    {
        Disconnected = 0,

        /// <summary>Dialling the gateway and sending <c>auth</c>.</summary>
        Authenticating = 1,

        /// <summary>Authenticated; asking the gateway for a map server.</summary>
        Assigning = 2,

        /// <summary>Dialling the game server and spending the join token.</summary>
        Joining = 3,

        /// <summary>Joined. Input goes up and snapshots come down.</summary>
        InWorld = 4,

        /// <summary>Leaving the current map and joining a new one.</summary>
        Transferring = 5,

        /// <summary>The gameplay connection ended. Inspect the reported cause.</summary>
        Ended = 6
    }
}
