using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Auth;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Connection;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.Transport;
using Cuvara.Netcode.World;

namespace Cuvara.Netcode.Client
{
    /// <summary>
    /// Drives the two-hop connection: gateway for auth and map assignment, then the
    /// game server directly for gameplay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two connections have different lifetimes on purpose. The gateway one is
    /// only needed to get a join token, and is kept afterwards solely so an eviction
    /// can be delivered; the game-server one is the session. Losing the gateway
    /// connection does not end the session, and losing the session does not
    /// invalidate the gateway one.
    /// </para>
    /// <para>
    /// A join retry re-runs <c>enter_world</c> rather than re-sending the token.
    /// Join tokens are single-use with a 30 s TTL and are pinned to one server, so a
    /// replay is rejected as already used — which would turn a transient failure
    /// into a permanent one.
    /// </para>
    /// </remarks>
    public sealed class NetworkClient : IDisposable
    {
        private readonly NetworkSettings _settings;
        private readonly ITransportFactory _transports;
        private readonly IWireCodec _codec;
        private readonly INetLog _log;
        private readonly IAuthProvider _auth;

        private GatewayClient _gateway;
        private GameSessionClient _session;

        // Reconnect state. _lastMapId is what a shutdown-triggered reconnect
        // rejoins; _userClosed distinguishes "the user left" from "the server
        // left" so Disconnect()/Dispose() never fight an automatic reconnect.
        private string _lastMapId;
        private volatile bool _userClosed;
        private CancellationTokenSource _reconnectCts;
        private readonly Random _jitter = new Random();

        public NetworkClient(NetworkSettings settings, ITransportFactory transports, IWireCodec codec, INetLog log,
            IAuthProvider auth = null)
        {
            _settings = settings;
            _transports = transports;
            _codec = codec;
            _log = log;
            _auth = auth;
        }

        /// <summary>Every snapshot that resolved, in arrival order.</summary>
        public event Action<ResolvedSnapshot> SnapshotReceived;

        /// <summary>Raised once when the gameplay connection ends.</summary>
        public event Action<DisconnectInfo> SessionClosed;

        /// <summary>
        /// Raised before each automatic reconnect round (1-based attempt number).
        /// Only fires when <see cref="NetworkSettings.ReconnectOnServerShutdown"/>
        /// is on, an <c>IAuthProvider</c> is registered, and the session ended with
        /// the server's <c>server_shutdown</c> reason.
        /// </summary>
        public event Action<int> ReconnectAttemptStarted;

        /// <summary>Raised once when an automatic reconnect lands back in world.</summary>
        public event Action Reconnected;

        /// <summary>
        /// Raised once when every automatic reconnect round failed. The session
        /// stays down; the caller decides what a player sees next.
        /// </summary>
        public event Action<Exception> ReconnectFailed;

        /// <summary>
        /// Raised once when the gateway connection ends. A
        /// <see cref="DisconnectCause.Kicked"/> here is the eviction signal
        /// (<c>duplicate_login</c> today) and means this account is now playing
        /// elsewhere.
        /// </summary>
        public event Action<DisconnectInfo> GatewayClosed;

        /// <summary>
        /// Raised on every state transition, in order. Exists so a caller can narrate
        /// the two-hop handshake without reaching into either hop.
        /// </summary>
        public event Action<NetworkClientState> StateChanged;

        private NetworkClientState _state = NetworkClientState.Disconnected;

        public NetworkClientState State
        {
            get => _state;
            private set
            {
                if (_state == value)
                {
                    return;
                }

                _state = value;
                StateChanged?.Invoke(value);
            }
        }

        /// <summary>The gameplay connection, or null before a successful join.</summary>
        public GameSessionClient Session => _session;

        /// <summary>
        /// Authoritative world state, rebuilt from the snapshot stream by
        /// <c>Shared.GameLogic.Systems.SnapshotMerger</c>. Already merged by the time
        /// <see cref="SnapshotReceived"/> fires, so a subscriber can read either the
        /// delta it was handed or the whole world.
        /// </summary>
        public WorldState World { get; } = new WorldState();

        public string UserId => _session?.UserId ?? _gateway?.UserId ?? string.Empty;

        /// <summary>
        /// The game server's simulation tick rate in Hz, from its join response. Zero
        /// until joined, and zero from a server that does not advertise one — in both
        /// cases the caller must fall back to a configured default rather than treating
        /// zero as a rate.
        /// </summary>
        public uint TickRate => _session?.TickRate ?? 0u;

        /// <summary>
        /// True when an <see cref="IAuthProvider"/> was supplied, so
        /// <see cref="ConnectAsync(string, CancellationToken)"/> can be used.
        /// </summary>
        /// <remarks>
        /// Lets a caller choose the real auth path when one is wired up and fall back to
        /// a development credential when it is not, without provoking an exception to
        /// find out which it is.
        /// </remarks>
        public bool HasAuthProvider => _auth != null;

        /// <summary>
        /// Connects using the <see cref="IAuthProvider"/> registered via DI.
        /// Throws <see cref="InvalidOperationException"/> if no provider was injected.
        /// </summary>
        public async UniTask ConnectAsync(string mapId, CancellationToken cancellationToken)
        {
            if (_auth == null)
            {
                throw new InvalidOperationException(
                    "No IAuthProvider registered. Either register one in the container " +
                    "or use the ConnectAsync(jwt, mapId, ct) overload.");
            }

            var jwt = await _auth.GetJwtAsync(cancellationToken);
            await ConnectAsync(jwt, mapId, cancellationToken);
        }

        /// <summary>
        /// Runs both hops. Throws <see cref="NetworkException"/> if either server
        /// refuses, after exhausting <see cref="NetworkSettings.JoinAttempts"/>.
        /// </summary>
        public async UniTask ConnectAsync(string jwt, string mapId, CancellationToken cancellationToken)
        {
            // Tear down connections only — NOT the reconnect machinery. The
            // reconnect loop reaches here itself; full Dispose() would cancel the
            // very token this call is running under.
            TeardownConnections();
            _userClosed = false;

            // Nothing from a previous session survives a new join: entity ids are
            // only meaningful within one game server's world.
            World.Reset();

            var gateway = new GatewayClient(_settings, _transports, _codec, _log);
            _gateway = gateway;
            gateway.Closed += OnGatewayClosed;

            State = NetworkClientState.Authenticating;
            await gateway.AuthenticateAsync(jwt, cancellationToken);

            NetworkException lastFailure = null;
            var attempts = Math.Max(1, _settings.JoinAttempts);

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                GameSessionClient session = null;
                try
                {
                    // Inside the try, so a refused assignment consumes an attempt
                    // like a refused join does. It used to sit outside: the gateway
                    // deliberately types "server is starting, retry shortly" as
                    // retryable and its single-flight allocation ASSUMES the client
                    // retries — yet any enter_world failure aborted the whole
                    // connect with zero of the attempts burned (#54).
                    State = NetworkClientState.Assigning;
                    var assignment = await gateway.EnterWorldAsync(mapId, cancellationToken);

                    session = new GameSessionClient(_settings, _transports, _codec, _log);
                    session.SnapshotReceived += OnSnapshot;
                    session.Closed += OnSessionClosed;

                    State = NetworkClientState.Joining;
                    await session.JoinAsync(assignment, cancellationToken);

                    _session = session;
                    _lastMapId = mapId;
                    State = NetworkClientState.InWorld;
                    gateway.StartMonitoring();
                    return;
                }
                catch (NetworkException ex)
                {
                    DropFailedSession(session);
                    lastFailure = ex;

                    // A precondition refusal (expired session, rate limit, bad
                    // token) cannot be fixed by asking again on this connection:
                    // retrying it burns the remaining attempts against a terminal
                    // answer and hides the real error under "could not join".
                    if (!IsRetryable(ex))
                    {
                        State = NetworkClientState.Disconnected;
                        throw;
                    }
                    _log.Warn($"join attempt {attempt}/{attempts} failed: {ex.Message}");
                }
                catch
                {
                    DropFailedSession(session);
                    throw;
                }

                if (attempt < attempts)
                {
                    await _settings.DelayScheduler(WithJitter(_settings.JoinRetryDelay), cancellationToken);
                }
            }

            State = NetworkClientState.Disconnected;
            throw lastFailure ?? new NetworkException("could not join a game server");
        }

        /// <summary>
        /// Whether asking again can change the answer. The retryable set is the
        /// gateway's, not ours: it types transient assignment refusals with these
        /// exact strings and its allocation flow assumes the client comes back.
        /// Anything unrecognised is treated as retryable too — the server's error
        /// set is the server's to extend, and wrongly retrying a terminal error
        /// costs a few seconds where wrongly aborting a transient one costs the
        /// connect.
        /// </summary>
        private static bool IsRetryable(NetworkException ex)
        {
            switch (ex.ServerError)
            {
                // Preconditions the gateway answers via auth_resp (its exact
                // strings): this connection will keep giving the same answer.
                case "session expired":
                case "rate limited":
                    return false;
                default:
                    return true;
            }
        }

        private void DropFailedSession(GameSessionClient session)
        {
            if (session == null)
            {
                return;
            }
            session.SnapshotReceived -= OnSnapshot;
            session.Closed -= OnSessionClosed;
            session.Dispose();
        }

        private TimeSpan WithJitter(TimeSpan baseDelay)
        {
            var jitterMs = _settings.RetryJitter.TotalMilliseconds;
            if (jitterMs <= 0)
            {
                return baseDelay;
            }
            return baseDelay + TimeSpan.FromMilliseconds(_jitter.NextDouble() * jitterMs);
        }

        /// <summary>
        /// Transfers to a different map. Leaves the current game server cleanly,
        /// re-authenticates through the gateway, and joins the new map's server.
        /// </summary>
        public async UniTask TransferToMapAsync(string mapId, CancellationToken cancellationToken)
        {
            if (_auth == null)
                throw new InvalidOperationException("map transfer requires an IAuthProvider");
            if (string.IsNullOrEmpty(mapId))
                throw new ArgumentException("mapId must not be empty", nameof(mapId));

            _log.Info($"transferring to map '{mapId}'");
            State = NetworkClientState.Transferring;
            CancelReconnect();
            _session?.Leave();
            await ConnectAsync(mapId, cancellationToken);
        }

        /// <summary>The map id the client is currently on, or was last on.</summary>
        public string CurrentMapId => _lastMapId;

        /// <summary>Leaves the world and drops both connections.</summary>
        public void Disconnect()
        {
            // The user chose to leave: no close that follows from this is the
            // server's doing, so the automatic reconnect must not fire.
            _userClosed = true;
            CancelReconnect();
            _session?.Leave();
            _gateway?.Close();
            State = NetworkClientState.Ended;
        }

        public void Dispose()
        {
            _userClosed = true;
            CancelReconnect();
            TeardownConnections();
        }

        private void CancelReconnect()
        {
            var cts = _reconnectCts;
            _reconnectCts = null;
            if (cts != null)
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        private void TeardownConnections()
        {
            var session = _session;
            _session = null;
            if (session != null)
            {
                session.SnapshotReceived -= OnSnapshot;
                session.Closed -= OnSessionClosed;
                session.Dispose();
            }

            var gateway = _gateway;
            _gateway = null;
            if (gateway != null)
            {
                gateway.Closed -= OnGatewayClosed;
                gateway.Dispose();
            }

            State = NetworkClientState.Disconnected;
        }

        private void OnSnapshot(ResolvedSnapshot snapshot)
        {
            // Merge before publishing: the shared merger is the single definition of
            // how a keyframe/delta stream becomes world state (ADR-10), and a
            // subscriber that reads World during the callback must see this snapshot
            // already applied.
            World.Apply(snapshot);
            SnapshotReceived?.Invoke(snapshot);
        }

        private void OnSessionClosed(DisconnectInfo info)
        {
            State = NetworkClientState.Ended;
            SessionClosed?.Invoke(info);

            // server_shutdown is the one close that PROMISES a comeback is worth
            // trying: the server said "I am going away, reconnect elsewhere", and
            // the backend holds the entity for 30 s for exactly this. Everything
            // else — kicks, protocol errors, plain drops — stays with the caller.
            if (_settings.ReconnectOnServerShutdown
                && _auth != null
                && !_userClosed
                && info.Reason == Protocol.KickReasons.ServerShutdown
                && !string.IsNullOrEmpty(_lastMapId))
            {
                StartReconnect();
            }
        }

        private void StartReconnect()
        {
            _log.Info($"session ended with server_shutdown; automatic reconnect armed for '{_lastMapId}'");
            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            _reconnectCts = new CancellationTokenSource();
            ReconnectLoopAsync(_lastMapId, _reconnectCts.Token).Forget();
        }

        private async UniTaskVoid ReconnectLoopAsync(string mapId, CancellationToken ct)
        {
            var attempts = Math.Max(1, _settings.ReconnectAttempts);
            Exception lastFailure = null;

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    // Linear-plus-jitter, sized so the default five rounds span the
                    // server's 30 s entity hold. Delay FIRST: the shutdown that
                    // triggered this reaches every client in the same instant, and
                    // an immediate retry is a synchronized storm at a gateway that
                    // is likely still allocating the replacement server.
                    var pause = TimeSpan.FromTicks(_settings.ReconnectDelay.Ticks * attempt);
                    await _settings.DelayScheduler(WithJitter(pause), ct);

                    ReconnectAttemptStarted?.Invoke(attempt);
                    _log.Info($"reconnect attempt {attempt}/{attempts} to '{mapId}'");

                    // The provider answers with its cached credential when it is
                    // still valid — a reconnect should cost zero auth traffic in
                    // the common case. Cold re-auth is the provider's fallback,
                    // not this loop's business.
                    await ConnectAsync(mapId, ct);

                    Reconnected?.Invoke();
                    return;
                }
                catch (OperationCanceledException)
                {
                    return; // user left, or a newer reconnect superseded this one
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                    _log.Warn($"reconnect attempt {attempt}/{attempts} failed: {ex.Message}");
                }
            }

            ReconnectFailed?.Invoke(lastFailure);
        }

        private void OnGatewayClosed(DisconnectInfo info)
        {
            // The gateway is not in the gameplay path, so this does not end the
            // session by itself — an eviction is delivered here, but so is an idle
            // socket simply dying, and the two must not look the same to a caller.
            GatewayClosed?.Invoke(info);
        }
    }
}
