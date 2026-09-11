using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Auth;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Connection;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Protocol;
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
    /// <para>
    /// <b>One operation at a time.</b> Every connect, reconnect round, transfer and
    /// disconnect starts a new <i>operation generation</i>. An async step that
    /// resumes after its generation was superseded — the user cancelled and logged
    /// in again, a reconnect round was overtaken by a transfer — discards its
    /// result and throws <see cref="OperationCanceledException"/> instead of
    /// touching state. Connections are owned by the operation that dials them until
    /// the join lands; a failure or cancel at any step disposes both hops before
    /// the exception leaves, so <see cref="State"/> always matches what is
    /// actually connected.
    /// </para>
    /// <para>
    /// <b>Recovery.</b> When the gameplay socket ends, <see cref="ReconnectPolicy"/>
    /// decides whether to come back (a plain drop, a heartbeat timeout, a server
    /// drain) or stay down (the user left, an eviction, a protocol fault). A
    /// reconnect re-authenticates through the <see cref="IAuthProvider"/> every
    /// round — the old join token was consumed by the original join — with
    /// exponential backoff and jitter inside <see cref="NetworkSettings.ReconnectBudget"/>,
    /// sized to the server's 30 s entity hold.
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

        // Operation generation. Incremented by every public entry point that
        // changes what the client is connected to; captured by each async flow
        // and checked after every await. A stale flow may only dispose what it
        // created itself.
        private int _generation;

        // The reconnect loop's own cancellation, chained to the generation: a new
        // operation cancels it, and it cancels itself when it gives up.
        private CancellationTokenSource _reconnectCts;

        // _lastMapId is what a reconnect rejoins; _userClosed distinguishes "the
        // user left" from "the server left"; _evicted records a gateway
        // duplicate_login so the session close that follows is never retried.
        private string _lastMapId;
        private volatile bool _userClosed;
        private volatile bool _evicted;
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
        /// Raised before each automatic reconnect round (1-based attempt number),
        /// after its backoff pause. Fires only when the policy chose to reconnect
        /// (see <see cref="ReconnectPolicy"/>) and an <c>IAuthProvider</c> is
        /// registered.
        /// </summary>
        public event Action<int> ReconnectAttemptStarted;

        /// <summary>
        /// Raised when a reconnect round is scheduled, before its pause, with the
        /// attempt number, the round cap and the pause length — what a
        /// "Reconnecting… attempt 2/8 (next in 3 s)" overlay binds to.
        /// </summary>
        public event Action<ReconnectionProgress> ReconnectProgress;

        /// <summary>Raised once when an automatic reconnect lands back in world.</summary>
        public event Action Reconnected;

        /// <summary>
        /// Raised once when the automatic reconnect gave up: the budget or round
        /// cap ran out (<see cref="ReconnectExhaustedException"/>), a round hit a
        /// permanent refusal, or the account was evicted meanwhile. The session
        /// stays <see cref="NetworkClientState.Ended"/>; the caller decides what a
        /// player sees next.
        /// </summary>
        public event Action<Exception> ReconnectFailed;

        /// <summary>
        /// Raised once when the gateway connection ends. A
        /// <see cref="DisconnectCause.Kicked"/> here is the eviction signal
        /// (<c>duplicate_login</c> today) and means this account is now playing
        /// elsewhere. Any other cause leaves the session untouched — the gateway
        /// is not in the gameplay path (ADR-3) — and is <i>not</i> retried in
        /// place: re-authenticating while the game session is alive would
        /// supersede our own login and get that session kicked. The gateway link
        /// is re-established by the next connect, reconnect or transfer.
        /// </summary>
        public event Action<DisconnectInfo> GatewayClosed;

        /// <summary>Fired on state change. Carries only the new state (legacy).</summary>
        public event Action<NetworkClientState> StateChanged;

        /// <summary>
        /// Fired on state change with full context: previous state, new state, and reason.
        /// Prefer this over <see cref="StateChanged"/> for new code.
        /// </summary>
        public event Action<ConnectionStateChangedEvent> ConnectionStateChanged;

        private NetworkClientState _state = NetworkClientState.Disconnected;
        private string _lastTransitionReason = "";

        public NetworkClientState State
        {
            get => _state;
            private set
            {
                if (_state == value)
                {
                    _lastTransitionReason = "";
                    return;
                }

                var previous = _state;
                _state = value;
                var reason = _lastTransitionReason;
                _lastTransitionReason = "";
                StateChanged?.Invoke(value);
                ConnectionStateChanged?.Invoke(new ConnectionStateChangedEvent(previous, value, reason));
            }
        }

        private void SetState(NetworkClientState value, string reason)
        {
            _lastTransitionReason = reason ?? "";
            State = value;
        }

        /// <summary>The gameplay connection, or null before a successful join.</summary>
        public GameSessionClient Session => _session;

        /// <summary>True while the automatic reconnect loop is running.</summary>
        public bool IsReconnecting => _reconnectCts != null;

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

        /// <summary>The map id the client is currently on, or was last on.</summary>
        public string CurrentMapId => _lastMapId;

        /// <summary>
        /// The operation generation: incremented by every public entry point that changes what
        /// the client is connected to (<see cref="ConnectAsync(string, CancellationToken)"/>,
        /// <see cref="TransferToMapAsync"/>, <see cref="Disconnect"/>, <see cref="Dispose"/>).
        /// An in-flight flow whose captured generation no longer matches this value is stale and
        /// may only dispose what it created. Read-only diagnostics — a reconnect overlay shows it
        /// so a "state went back to InWorld" can be told apart from "a new session started".
        /// </summary>
        public int Generation => _generation;

        // ─────────────────────────── public operations ───────────────────────────

        /// <summary>
        /// Connects using the <see cref="IAuthProvider"/> registered via DI.
        /// Throws <see cref="InvalidOperationException"/> if no provider was injected.
        /// </summary>
        /// <remarks>
        /// Supersedes any operation in flight — an earlier connect still dialing, a
        /// reconnect loop — which then completes with
        /// <see cref="OperationCanceledException"/> without touching state.
        /// </remarks>
        public UniTask ConnectAsync(string mapId, CancellationToken cancellationToken)
        {
            RequireAuthProvider("the ConnectAsync(jwt, mapId, ct) overload");
            var generation = BeginOperation(userClosed: false);
            return RunConnectAsync(generation, null, mapId, cancellationToken, inReconnect: false);
        }

        /// <summary>
        /// Runs both hops with a caller-supplied JWT. Throws <see cref="NetworkException"/>
        /// if either server refuses, after exhausting <see cref="NetworkSettings.JoinAttempts"/>.
        /// On any failure or cancel both hops are closed before the exception leaves.
        /// </summary>
        public UniTask ConnectAsync(string jwt, string mapId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(jwt))
            {
                throw new ArgumentException("jwt must not be empty", nameof(jwt));
            }

            var generation = BeginOperation(userClosed: false);
            return RunConnectAsync(generation, jwt, mapId, cancellationToken, inReconnect: false);
        }

        /// <summary>
        /// Transfers to a different map. Leaves the current game server cleanly,
        /// re-authenticates through the gateway, and joins the new map's server.
        /// </summary>
        public UniTask TransferToMapAsync(string mapId, CancellationToken cancellationToken)
        {
            RequireAuthProvider("map transfer");
            if (string.IsNullOrEmpty(mapId))
            {
                throw new ArgumentException("mapId must not be empty", nameof(mapId));
            }

            _log.Info($"transferring to map '{mapId}'");
            var generation = BeginOperation(userClosed: false, leavePolitely: true);
            SetState(NetworkClientState.Transferring, "transfer");
            return RunConnectAsync(generation, null, mapId, cancellationToken, inReconnect: false);
        }

        /// <summary>Leaves the world and drops both connections. Never reconnects.</summary>
        public void Disconnect()
        {
            // The user chose to leave: no close that follows from this is the
            // server's doing, so the automatic reconnect must not fire.
            BeginOperation(userClosed: true, leavePolitely: true);
            SetState(NetworkClientState.Ended, "user");
        }

        public void Dispose()
        {
            BeginOperation(userClosed: true);
            SetState(NetworkClientState.Disconnected, "disposed");
        }

        // ─────────────────────────── the connect flow ───────────────────────────

        /// <summary>
        /// Starts a new operation generation: cancels the reconnect loop, drops
        /// whatever is connected, and returns the generation the caller runs under.
        /// Everything that used to be connected is gone when this returns.
        /// </summary>
        private int BeginOperation(bool userClosed, bool leavePolitely = false)
        {
            var generation = ++_generation;
            _userClosed = userClosed;
            CancelReconnect();

            if (leavePolitely)
            {
                // A polite disconnect frame first, so the server saves and releases
                // the entity now rather than at the end of its 30 s hold.
                _session?.Leave();
                _gateway?.Close();
            }

            TeardownConnections();
            return generation;
        }

        private async UniTask RunConnectAsync(int generation, string jwt, string mapId, CancellationToken ct,
            bool inReconnect)
        {
            // Nothing from a previous session survives a new join: entity ids are
            // only meaningful within one game server's world.
            World.Reset();
            _evicted = false;

            GatewayClient gateway = null;
            GameSessionClient session = null;
            var committed = false;

            try
            {
                SetState(NetworkClientState.Authenticating, "");

                if (jwt == null)
                {
                    // Inside the try: a provider that throws or is cancelled must
                    // leave the same canonical state as a refused auth does.
                    jwt = await _auth.GetJwtAsync(ct);
                    Guard(generation, ct);
                }

                gateway = new GatewayClient(_settings, _transports, _codec, _log);
                gateway.Closed += OnGatewayClosed;
                await gateway.AuthenticateAsync(jwt, ct);
                Guard(generation, ct);

                NetworkException lastFailure = null;
                var attempts = Math.Max(1, _settings.JoinAttempts);

                for (var attempt = 1; attempt <= attempts; attempt++)
                {
                    try
                    {
                        // Inside the try, so a refused assignment consumes an attempt
                        // like a refused join does: the gateway types "server is
                        // starting, retry shortly" as retryable and its single-flight
                        // allocation ASSUMES the client retries (#54).
                        SetState(NetworkClientState.Assigning, "");
                        var assignment = await gateway.EnterWorldAsync(mapId, ct);
                        Guard(generation, ct);

                        session = new GameSessionClient(_settings, _transports, _codec, _log);
                        session.SnapshotReceived += OnSnapshot;

                        SetState(NetworkClientState.Joining, "");
                        await session.JoinAsync(assignment, ct);
                        Guard(generation, ct);

                        if (!session.IsConnected)
                        {
                            // The loops started inside JoinAsync and the server ended
                            // the link before we got here. Not a join that succeeded:
                            // one more attempt, through a fresh enter_world.
                            throw new NetworkException(
                                $"game server closed the connection right after the join: {session.CloseInfo}");
                        }

                        // Commit: from here the connections belong to the client, and
                        // only now does a close on this session reach the policy.
                        _gateway = gateway;
                        _session = session;
                        _lastMapId = mapId;
                        committed = true;
                        session.Closed += OnSessionClosed;
                        SetState(NetworkClientState.InWorld, "");
                        gateway.StartMonitoring();
                        return;
                    }
                    catch (NetworkException ex)
                    {
                        DropFailedSession(session);
                        session = null;
                        lastFailure = ex;

                        // A precondition refusal (expired session, rate limit, bad
                        // token) cannot be fixed by asking again on this connection:
                        // retrying it burns the remaining attempts against a terminal
                        // answer and hides the real error under "could not join".
                        if (!IsRetryable(ex))
                        {
                            throw;
                        }

                        _log.Warn($"join attempt {attempt}/{attempts} failed: {ex.Message}");
                    }

                    if (attempt < attempts)
                    {
                        await _settings.DelayScheduler(WithJitter(_settings.JoinRetryDelay), ct);
                        Guard(generation, ct);
                    }
                }

                throw lastFailure ?? new NetworkException("could not join a game server");
            }
            finally
            {
                if (!committed)
                {
                    // Ownership never transferred: whatever this operation dialed is
                    // its own to close, whether it failed, was cancelled, or was
                    // superseded by a newer generation. A superseded flow must not
                    // touch shared state — the newer operation owns that now.
                    DropFailedSession(session);
                    if (gateway != null)
                    {
                        gateway.Closed -= OnGatewayClosed;
                        gateway.Dispose();
                    }

                    if (generation == _generation && !inReconnect)
                    {
                        // A reconnect round leaves the state to its loop, which goes
                        // back to Reconnecting or on to Ended without a flicker.
                        SetState(NetworkClientState.Disconnected, ct.IsCancellationRequested ? "cancelled" : "failed");
                    }
                }
            }
        }

        /// <summary>
        /// After every await: the caller's token first, then the generation. A
        /// stale completion — cancel-then-reconnect, or a newer operation — must
        /// not flip state, so it leaves through the same exception a cancel does.
        /// </summary>
        private void Guard(int generation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (generation != _generation)
            {
                throw new OperationCanceledException("superseded by a newer connect, transfer or disconnect");
            }
        }

        private void RequireAuthProvider(string alternative)
        {
            if (_auth == null)
            {
                throw new InvalidOperationException(
                    $"No IAuthProvider registered. Either register one in the container or use {alternative}.");
            }
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
                // A version refusal from either hop. Not retryable by construction:
                // the client would have to be a different build for the answer to
                // change, so every retry is a guaranteed-identical refusal.
                case KickReasons.ProtocolVersionMismatch:
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
        }

        // ─────────────────────────── session events ───────────────────────────

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
            SetState(NetworkClientState.Ended, info.ToString());
            SessionClosed?.Invoke(info);

            var decision = DecideRecovery(info);
            if (decision == ReconnectDecision.Never)
            {
                return;
            }

            StartReconnect(decision, info);
        }

        /// <summary>
        /// The policy, plus the things the policy is not told: whether the user
        /// left, whether the gateway already reported an eviction, whether a
        /// provider exists to refresh the credential, and the settings toggles.
        /// </summary>
        private ReconnectDecision DecideRecovery(DisconnectInfo info)
        {
            if (_auth == null || _userClosed || _evicted || string.IsNullOrEmpty(_lastMapId))
            {
                return ReconnectDecision.Never;
            }

            var decision = ReconnectPolicy.ForSessionClose(info);

            // ESCALATE, never downgrade. A `require` server refuses a client that did not
            // seal and names the reason; the same client sealing is accepted. Turning our
            // own requirement ON here is safe in the only direction that matters -- there
            // is no path in this class that turns it OFF, so a hostile peer can ask us for
            // more protection and never for less, which is what keeps this from being the
            // negotiated downgrade ADR-22 exists to forbid.
            //
            // Without it, a client whose sealing flag is off simply cannot play on a
            // `require` server, and every operator has to know to pass a flag.
            if (info.Cause == DisconnectCause.Kicked
                && info.Reason == SealedRefusalReason.NoSealedSession
                && !_settings.RequireSealedSession)
            {
                _settings.RequireSealedSession = true;
                _log.Warn(
                    "the game server requires a sealed session and refused this one (" +
                    info.Reason + "); reconnecting WITH sealing. Set " +
                    "NetworkSettings.RequireSealedSession to skip this first refused join.");
            }

            switch (decision)
            {
                case ReconnectDecision.ReconnectAfterDelay:
                    return _settings.ReconnectOnServerShutdown ? decision : ReconnectDecision.Never;
                case ReconnectDecision.Reconnect:
                    return _settings.ReconnectOnConnectionLoss ? decision : ReconnectDecision.Never;
                default:
                    return ReconnectDecision.Never;
            }
        }

        private void StartReconnect(ReconnectDecision decision, DisconnectInfo cause)
        {
            _log.Info($"session ended with {cause}; automatic reconnect armed for '{_lastMapId}'");

            // A reconnect is an operation like any other — it supersedes the dead
            // session's generation — but it is the server's doing, not the user's.
            var generation = BeginOperation(userClosed: false);
            _reconnectCts = new CancellationTokenSource();
            ReconnectLoopAsync(generation, _lastMapId, decision == ReconnectDecision.ReconnectAfterDelay, cause,
                _reconnectCts.Token).Forget();
        }

        private async UniTaskVoid ReconnectLoopAsync(int generation, string mapId, bool delayFirst,
            DisconnectInfo cause, CancellationToken ct)
        {
            var startedMs = _settings.MonotonicClock();
            var budgetMs = (long)_settings.ReconnectBudget.TotalMilliseconds;
            var maxAttempts = Math.Max(1, _settings.ReconnectAttempts);
            var attempts = 0;
            Exception lastFailure = null;

            SetState(NetworkClientState.Reconnecting, cause.ToString());

            try
            {
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    // Round 1 is immediate after a plain drop — the link is most
                    // likely back already — and delayed after a drain, where every
                    // client saw the same close in the same instant.
                    var backoffRound = delayFirst ? attempt : attempt - 1;
                    var pause = WithJitter(ReconnectPolicy.Backoff(backoffRound, _settings.ReconnectDelay,
                        _settings.ReconnectMaxDelay));

                    var elapsedMs = _settings.MonotonicClock() - startedMs;
                    if (elapsedMs + (long)pause.TotalMilliseconds > budgetMs)
                    {
                        break;
                    }

                    ReconnectProgress?.Invoke(new ReconnectionProgress(attempt, maxAttempts, (float)pause.TotalSeconds));
                    if (pause > TimeSpan.Zero)
                    {
                        await _settings.DelayScheduler(pause, ct);
                    }

                    Guard(generation, ct);
                    attempts = attempt;
                    ReconnectAttemptStarted?.Invoke(attempt);
                    _log.Info($"reconnect attempt {attempt}/{maxAttempts} to '{mapId}'");

                    try
                    {
                        // Always through the provider: the join token that got us in
                        // was consumed by that join, and the gateway destroyed its
                        // session record when our socket died. A cached-and-valid
                        // JWT costs the provider nothing; a cold re-auth is its
                        // business, not this loop's.
                        await RunConnectAsync(generation, null, mapId, ct, inReconnect: true);
                        _reconnectCts?.Dispose();
                        _reconnectCts = null;
                        Reconnected?.Invoke();
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastFailure = ex;
                        _log.Warn($"reconnect attempt {attempt}/{maxAttempts} failed: {ex.Message}");

                        if (ReconnectPolicy.IsPermanentFailure(ex))
                        {
                            // The server said no in a way another round cannot
                            // change. Surface the real error now rather than the
                            // budget's worth of identical refusals later.
                            break;
                        }

                        if (_evicted)
                        {
                            break;
                        }

                        SetState(NetworkClientState.Reconnecting, ex.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The user left, or a newer operation superseded this loop. That
                // operation owns the state now.
                return;
            }

            if (generation != _generation)
            {
                return;
            }

            var elapsed = TimeSpan.FromMilliseconds(_settings.MonotonicClock() - startedMs);
            var why = lastFailure != null && ReconnectPolicy.IsPermanentFailure(lastFailure)
                ? $"reconnect refused permanently after {attempts} attempt(s): {lastFailure.Message}"
                : $"reconnect budget of {_settings.ReconnectBudget.TotalSeconds:F0} s exhausted after {attempts} attempt(s)";
            var failure = new ReconnectExhaustedException(why, attempts, elapsed, lastFailure);

            _log.Warn(why);
            _reconnectCts?.Dispose();
            _reconnectCts = null;
            SetState(NetworkClientState.Ended, "reconnect exhausted");
            ReconnectFailed?.Invoke(failure);
        }

        private void OnGatewayClosed(DisconnectInfo info)
        {
            // The gateway is not in the gameplay path, so this does not end the
            // session by itself — an eviction is delivered here, but so is an idle
            // socket simply dying, and the two must not look the same to a caller.
            if (info.Cause == DisconnectCause.Kicked)
            {
                // This account logged in elsewhere. The game server kicks the
                // session next (ADR-20), and that close must never be retried —
                // coming back would evict the newer login in turn.
                _evicted = true;
                _log.Warn($"evicted by the gateway: {info.Reason}; the session that follows will not be retried");
            }

            GatewayClosed?.Invoke(info);
        }
    }
}
