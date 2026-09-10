using System;
using System.Threading;
using Cysharp.Threading.Tasks;

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
        /// Whether to run the sealed-session handshake on the gameplay hop after the join
        /// reply. Must match the game server's <c>--sealed</c> setting.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Defaults to <c>false</c>, matching the server's own default
        /// (<c>SealedRequirement.Disabled</c>), so an existing deployment is unaffected.
        /// </para>
        /// <para>
        /// <b>This is a deployment-wide setting, not a negotiation, and ADR-22 is explicit
        /// that it must not become one.</b> A protocol that can be talked down to cleartext
        /// will be, so neither side offers a fallback and neither side asks the other what it
        /// supports. The cost is that a mismatch is a misconfiguration rather than a
        /// degradation:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// <b>Client on, server off:</b> the server never sends a hello, so the client's read
        /// times out. It is reported as such, with the mismatch named as the likely cause,
        /// rather than being left to hang — which is what this setting existing at all is
        /// worth.
        /// </description></item>
        /// <item><description>
        /// <b>Client off, server on:</b> the server refuses the session and closes. The
        /// client sees a closed connection during the join.
        /// </description></item>
        /// </list>
        /// <para>
        /// A JSON client can never seal — the handshake messages are absent from the JSON
        /// message set on purpose, so key material cannot be rendered into a human-readable
        /// payload — and a server that requires sealing refuses one outright rather than
        /// serving it in the clear.
        /// </para>
        /// </remarks>
        public bool RequireSealedSession { get; set; }

        /// <summary>
        /// How long to wait for the server's sealed hello before giving up.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="ConnectTimeout"/> and deliberately short: the server
        /// answers immediately or not at all, since the hello needs no lookup and no I/O.
        /// A long budget here buys nothing and turns the commonest misconfiguration into a
        /// long silence.
        /// </remarks>
        public TimeSpan SealedHandshakeTimeout { get; set; } = TimeSpan.FromSeconds(5);

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
        /// in the package consumed that window (#54). The first round waits a full
        /// backoff pause: a drain reaches every client at once, and an immediate
        /// retry is a synchronized storm.
        /// </remarks>
        public bool ReconnectOnServerShutdown { get; set; } = true;

        /// <summary>
        /// Reconnect automatically when the gameplay socket dies without anyone
        /// choosing it: <see cref="Connection.DisconnectCause.PeerClosed"/>,
        /// <see cref="Connection.DisconnectCause.HeartbeatTimeout"/> and
        /// <see cref="Connection.DisconnectCause.TransportError"/> — the ordinary
        /// mobile disconnections (NAT expiry, Wi-Fi hand-off, app suspend). The
        /// first round is immediate; backoff starts from the second. Requires an
        /// <c>IAuthProvider</c>. Never applies to a user-initiated close, an
        /// eviction, or a protocol fault — see <see cref="ReconnectPolicy"/>.
        /// </summary>
        public bool ReconnectOnConnectionLoss { get; set; } = true;

        /// <summary>
        /// Upper bound on reconnect rounds. <see cref="ReconnectBudget"/> is the
        /// bound that normally ends the loop; this one exists so a budget set very
        /// large cannot turn into an unbounded retry.
        /// </summary>
        public int ReconnectAttempts { get; set; } = 12;

        /// <summary>
        /// Base pause of the reconnect backoff, before jitter. Doubles every
        /// round (1 s, 2 s, 4 s, 8 s…) up to <see cref="ReconnectMaxDelay"/>.
        /// </summary>
        /// <remarks>
        /// Was 2 s and linear (2, 4, 6…) before 0.31.0. Exponential with a 1 s base
        /// gets a client whose Wi-Fi blipped back in world in ~1 s instead of ~2 s,
        /// and still spreads a restart storm: the pauses of five rounds sum to 23 s
        /// plus jitter, inside the entity hold.
        /// </remarks>
        public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>Cap on a single backoff pause, before jitter.</summary>
        public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(8);

        /// <summary>
        /// Total time the automatic reconnect may keep trying, measured from the
        /// close. A round whose pause would end past the budget is not started;
        /// <see cref="NetworkClient.ReconnectFailed"/> fires with a
        /// <see cref="ReconnectExhaustedException"/> instead.
        /// </summary>
        /// <remarks>
        /// 60 s. The game server holds a dropped player's entity for 30 s — but
        /// measured from when <em>it</em> notices the drop, not from when the client
        /// does. On a client-side network loss the server's own 30 s heartbeat
        /// timeout runs first, so the hold can end up to ~60 s after the client's
        /// close; on a server freeze or restart the hold does not even start until
        /// the server is back. Measured 2026-09-07 against a game server frozen for
        /// 45 s: a 25 s budget gave up four rounds in, seconds before the server
        /// re-registered, while the hold was still ahead of it. 60 s covers both
        /// shapes; past it the hold is gone anyway and a fresh login is the honest
        /// path. A round that is in flight when the budget expires is allowed to
        /// finish — its timeouts (<see cref="ConnectTimeout"/>,
        /// <see cref="EnterWorldTimeout"/>) bound it, not this. After the budget the
        /// session stays <see cref="NetworkClientState.Ended"/>; a later
        /// <see cref="NetworkClient.ConnectAsync(string, System.Threading.CancellationToken)"/>
        /// joins as a fresh login and, if the hold has expired, into a body rebuilt
        /// from persisted state.
        /// </remarks>
        public TimeSpan ReconnectBudget { get; set; } = TimeSpan.FromSeconds(60);

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
        /// How the client pauses between join retries and reconnect rounds. The
        /// default is a realtime delay on the player loop, which is right for a
        /// running game. Tests replace it with a synchronous no-op: the EditMode
        /// runner in headless CI can spin coroutine steps without ever pumping the
        /// editor tick that completes a real <c>UniTask.Delay</c>, so a policy test
        /// that crosses one stalls there forever while passing in an interactive
        /// Editor.
        /// </summary>
        public Func<TimeSpan, CancellationToken, UniTask> DelayScheduler { get; set; } =
            (delay, ct) => UniTask.Delay(delay, DelayType.Realtime, PlayerLoopTiming.Update, ct);

        /// <summary>
        /// How a connection waits between heartbeat pings. Separate from
        /// <see cref="DelayScheduler"/> on purpose: tests make that one complete
        /// synchronously, and a heartbeat loop on a synchronous delay would spin.
        /// A test drives the heartbeat by handing back a task it completes itself.
        /// The default is realtime, so a paused or slowed game still answers the
        /// server's liveness check instead of being dropped at 30 s.
        /// </summary>
        public Func<TimeSpan, CancellationToken, UniTask> HeartbeatScheduler { get; set; } =
            (delay, ct) => UniTask.Delay(delay, DelayType.Realtime, PlayerLoopTiming.Update, ct);

        /// <summary>
        /// Monotonic milliseconds for every elapsed-time decision the package makes:
        /// heartbeat age, round-trip time, reconnect budget. Never wall-clock — a
        /// phone that syncs its clock, or a machine that suspends and corrects on
        /// resume, must not look like 30 s of silence. Wall-clock (UTC) is still used
        /// where the protocol carries a timestamp, and only there.
        /// </summary>
        public Func<long> MonotonicClock { get; set; } = MonotonicClockDefault.NowMs;

        /// <summary>
        /// Outbound send queue depth per connection, matching the game server's
        /// bounded channel. When it overflows the oldest frame is dropped: stale
        /// input is worthless, and blocking the caller would stall the frame that
        /// produced it.
        /// </summary>
        public int SendQueueCapacity { get; set; } = 64;
    }
}
