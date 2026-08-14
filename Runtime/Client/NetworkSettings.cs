using System;

namespace Cuvara.Netcode.Client
{
    /// <summary>
    /// Connection tuning. Register one instance in the container; nothing in the
    /// networking layer reads global state.
    /// </summary>
    public sealed class NetworkSettings
    {
        /// <summary>Gateway host to dial for the auth + map-assignment hop.</summary>
        public string GatewayHost { get; set; } = "127.0.0.1";

        public int GatewayPort { get; set; } = 8000;

        /// <summary>
        /// Heartbeat cadence. 10 s on both hops, matching <c>pingInterval</c> in the
        /// gateway and <c>Connection.PingInterval</c> in the game server.
        /// </summary>
        public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long a connection may go without a pong before we declare it dead.
        /// 30 s, matching <c>pongTimeout</c> on both servers, so both ends give up
        /// at about the same time.
        /// </summary>
        public TimeSpan PongTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Dial + handshake budget for one connection attempt.</summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How many times to retry the join. Each attempt re-runs
        /// <c>enter_world</c>, because a join token is single-use with a 30 s TTL
        /// and pinned to one server: replaying one is rejected with
        /// <c>Token already used</c>.
        /// </summary>
        public int JoinAttempts { get; set; } = 3;

        /// <summary>Pause between join attempts.</summary>
        public TimeSpan JoinRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Keep the gateway connection open for the whole session.
        /// </summary>
        /// <remarks>
        /// The gateway is not in the gameplay data path (ADR-3), so the connection
        /// is droppable after <c>enter_world</c> — but eviction
        /// (<c>duplicate_login</c>) is only ever pushed there, and the gateway
        /// destroys the session record when the socket closes. Keeping it costs one
        /// idle socket and a ping every 10 s, and it is the only way the client
        /// learns it was displaced by another login.
        /// </remarks>
        public bool KeepGatewayConnection { get; set; } = true;

        /// <summary>
        /// Outbound send queue depth per connection, matching the game server's
        /// bounded channel. When it overflows the oldest frame is dropped: stale
        /// input is worthless, and blocking the caller would stall the frame that
        /// produced it.
        /// </summary>
        public int SendQueueCapacity { get; set; } = 64;
    }
}
