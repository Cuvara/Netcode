using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Client
{
    /// <summary>
    /// What the gateway hands back from <c>enter_world</c>: where the game server
    /// is, how to reach it, and the one-shot token that gets us in.
    /// </summary>
    public readonly struct MapAssignment
    {
        /// <param name="serverIdentityKey">
        /// The game server's Ed25519 identity public key (ADR-25), or null from a gateway that
        /// predates it.
        /// </param>
        /// <param name="identityKeyHopAuthenticated">
        /// Whether the gateway connection this assignment arrived over was authenticated.
        /// </param>
        public MapAssignment(
            NetworkEndpoint endpoint,
            string joinToken,
            TransportKind transport,
            byte[] serverIdentityKey = null,
            bool identityKeyHopAuthenticated = false)
        {
            Endpoint = endpoint;
            JoinToken = joinToken;
            Transport = transport;
            ServerIdentityKey = serverIdentityKey;
            IdentityKeyHopAuthenticated = identityKeyHopAuthenticated;
        }

        public NetworkEndpoint Endpoint { get; }

        /// <summary>
        /// Single-use, 30-second, pinned to <see cref="Endpoint"/>'s server. Not
        /// reusable for a second attempt — a retry needs a fresh <c>enter_world</c>.
        /// </summary>
        public string JoinToken { get; }

        /// <summary>
        /// The transport the <b>game server</b> speaks, which is unrelated to the
        /// one used to reach the gateway.
        /// </summary>
        public TransportKind Transport { get; }

        /// <summary>
        /// The game server's Ed25519 identity public key (ADR-25), or null/empty from a gateway
        /// that predates it.
        /// </summary>
        public byte[] ServerIdentityKey { get; }

        /// <summary>
        /// Whether <see cref="ServerIdentityKey"/> arrived over an authenticated hop.
        /// </summary>
        /// <remarks>
        /// <b>The key and this flag travel together because the key is worthless without it.</b>
        /// The identity key arrives on the gateway hop; over plaintext an active attacker
        /// substitutes it and the signature check then passes against the attacker's own key.
        /// Anything that hands the key onward without this flag has dropped the only thing that
        /// makes the signature mean something.
        /// </remarks>
        public bool IdentityKeyHopAuthenticated { get; }
    }
}
