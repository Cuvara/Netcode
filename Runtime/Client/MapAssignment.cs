using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Client
{
    /// <summary>
    /// What the gateway hands back from <c>enter_world</c>: where the game server
    /// is, how to reach it, and the one-shot token that gets us in.
    /// </summary>
    public readonly struct MapAssignment
    {
        public MapAssignment(NetworkEndpoint endpoint, string joinToken, TransportKind transport)
        {
            Endpoint = endpoint;
            JoinToken = joinToken;
            Transport = transport;
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
    }
}
