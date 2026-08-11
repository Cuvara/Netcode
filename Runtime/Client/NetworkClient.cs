using System;
using System.Threading;
using Cysharp.Threading.Tasks;
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

        private GatewayClient _gateway;
        private GameSessionClient _session;

        public NetworkClient(NetworkSettings settings, ITransportFactory transports, IWireCodec codec, INetLog log)
        {
            _settings = settings;
            _transports = transports;
            _codec = codec;
            _log = log;
        }

        /// <summary>Every snapshot that resolved, in arrival order.</summary>
        public event Action<ResolvedSnapshot> SnapshotReceived;

        /// <summary>Raised once when the gameplay connection ends.</summary>
        public event Action<DisconnectInfo> SessionClosed;

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
        /// Runs both hops. Throws <see cref="NetworkException"/> if either server
        /// refuses, after exhausting <see cref="NetworkSettings.JoinAttempts"/>.
        /// </summary>
        public async UniTask ConnectAsync(string jwt, string mapId, CancellationToken cancellationToken)
        {
            Dispose();

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
                State = NetworkClientState.Assigning;
                var assignment = await gateway.EnterWorldAsync(mapId, cancellationToken);

                var session = new GameSessionClient(_settings, _transports, _codec, _log);
                session.SnapshotReceived += OnSnapshot;
                session.Closed += OnSessionClosed;

                try
                {
                    State = NetworkClientState.Joining;
                    await session.JoinAsync(assignment, cancellationToken);

                    _session = session;
                    State = NetworkClientState.InWorld;
                    gateway.StartMonitoring();
                    return;
                }
                catch (NetworkException ex)
                {
                    session.SnapshotReceived -= OnSnapshot;
                    session.Closed -= OnSessionClosed;
                    session.Dispose();
                    lastFailure = ex;
                    _log.Warn($"join attempt {attempt}/{attempts} failed: {ex.Message}");
                }
                catch
                {
                    session.SnapshotReceived -= OnSnapshot;
                    session.Closed -= OnSessionClosed;
                    session.Dispose();
                    throw;
                }

                if (attempt < attempts)
                {
                    await UniTask.Delay(_settings.JoinRetryDelay, DelayType.Realtime, PlayerLoopTiming.Update,
                        cancellationToken);
                }
            }

            State = NetworkClientState.Disconnected;
            throw lastFailure ?? new NetworkException("could not join a game server");
        }

        /// <summary>Leaves the world and drops both connections.</summary>
        public void Disconnect()
        {
            _session?.Leave();
            _gateway?.Close();
            State = NetworkClientState.Ended;
        }

        public void Dispose()
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
