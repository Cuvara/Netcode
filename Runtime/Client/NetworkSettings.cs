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
        /// Budget for one <c>enter_world</c> round trip, separate from
        /// <see cref="ConnectTimeout"/> and deliberately larger than the gateway's
        /// own handler window (18 s against a cold map — it may allocate a server
        /// and wait for it to register before answering).
        /// </summary>
        /// <remarks>
        /// This existed as a bug before it existed as a setting: enter_world used
        /// to run under <see cref="ConnectTimeout"/>'s 10 s, so a cold-map first
        /// join was cancelled client-side at 10 s while succeeding server-side at
        /// ~12 — and the cancellation escaped the join-retry loop, aborting the
        /// whole connect (#54, server side rpg-mmo-server#235).
        /// </remarks>
        public TimeSpan EnterWorldTimeout { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>
        /// How many times to retry the join. Each attempt re-runs
        /// <c>enter_world</c>, because a join token is single-use with a 30 s TTL
        /// and pinned to one server: replaying one is rejected with
        /// <c>Token already used</c>.
        /// </summary>
        public int JoinAttempts { get; set; } = 3;

        /// <summary>Pause between join attempts, before jitter.</summary>
        public TimeSpan JoinRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Upper bound of the random extra added to every retry and reconnect
        /// pause. A fixed delay synchronises a storm: after a server restart every
        /// client observes the close in the same instant, and identical pauses
        /// bring them all back in the same instant too — the reconnect wave the
        /// jitter exists to spread.
        /// </summary>
        public TimeSpan RetryJitter { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Reconnect automatically when the session ends with the server's
        /// <c>server_shutdown</c> reason. Requires an <c>IAuthProvider</c> — the
        /// reconnect needs a fresh (or cached-and-still-valid) credential, and only
        /// a provider can answer that without a cold re-auth.
        /// </summary>
        /// <remarks>
        /// The server holds the entity for 30 s after a disconnect precisely so a
        /// client can come back into its own body; until this flag existed nothing
        /// in the package consumed that window (#54).
        /// </remarks>
        public bool ReconnectOnServerShutdown { get; set; } = true;

        /// <summary>Reconnect rounds before giving up and surfacing the failure.</summary>
        public int ReconnectAttempts { get; set; } = 5;

        /// <summary>
        /// Base pause before each reconnect round, before jitter. Grows linearly
        /// with the round number (2 s, 4 s, 6 s…), so five rounds span ~30 s —
        /// the entity-hold window.
        /// </summary>
        public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

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
