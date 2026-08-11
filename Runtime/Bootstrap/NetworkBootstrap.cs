using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Connection;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.Transport;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;
using UnityEngine;
using VContainer;

namespace Cuvara.Netcode.Bootstrap
{
    /// <summary>
    /// Press Play and watch the whole core flow run against a local backend:
    /// gateway auth, enter world, dial the assigned game server, join, then input up
    /// and snapshots down. Every step is logged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A development harness, not gameplay: it draws nothing, spawns nothing, and
    /// implements no rules. Its only job is to prove the two-hop handshake works
    /// end to end and that snapshots merge through <c>Shared.GameLogic</c>.
    /// </para>
    /// <para>
    /// It prefers an injected <see cref="NetworkClient"/> — put
    /// <c>RegisterNetworking()</c> in <c>GameLifetimeScope</c> and the container owns
    /// the connection, which is what a real scene should do. With no container in the
    /// scene it constructs its own and says so, so a bare scene still runs.
    /// </para>
    /// </remarks>
    [AddComponentMenu("RPG MMO/Network Bootstrap")]
    public sealed class NetworkBootstrap : MonoBehaviour
    {
        [Tooltip("Address, credentials and map. Leave empty to use the built-in localhost defaults.")]
        [SerializeField] private NetworkBootstrapConfig config;

        private NetworkClient _injected;
        private NetworkClient _client;
        private NetworkClient _owned;
        private CancellationTokenSource _lifetime;
        private long _inputTick;
        private int _snapshotsSeen;

        /// <summary>Optional. Present when a VContainer scope registered the networking layer.</summary>
        [Inject]
        public void Construct(NetworkClient client) => _injected = client;

        /// <summary>The live client, or null before <see cref="Connect"/> has run.</summary>
        public NetworkClient Client => _client;

        private void Start()
        {
            // A server-authoritative client must keep running while unfocused. Both
            // the input loop and the heartbeat are driven from the Unity player loop,
            // and the player loop does not tick in an unfocused Editor or player
            // unless this is set — frameCount simply stops advancing. The socket read
            // threads keep delivering snapshots throughout, so the session looks
            // healthy from the outside while no input and no pong is being sent, and
            // the server drops the connection 30 s later with no visible cause.
            //
            // Project-wide "Run In Background" is off (ProjectSettings), which is a
            // reasonable default for a single-player game and the wrong one for this.
            // Set here rather than flipped project-wide, because that decision belongs
            // to whoever owns the shipping player settings.
            Application.runInBackground = true;

            if (config == null)
            {
                config = ScriptableObject.CreateInstance<NetworkBootstrapConfig>();
                Debug.Log("[bootstrap] no config asset assigned — using localhost defaults " +
                          "(Assets > Create > RPG MMO > Network Bootstrap Config to make one)");
            }

            if (config.ConnectOnStart)
            {
                Connect();
            }
        }

        /// <summary>Runs the flow. Safe to call once; a second call is ignored.</summary>
        public void Connect()
        {
            if (_lifetime != null)
            {
                return;
            }

            _lifetime = new CancellationTokenSource();
            RunAsync(_lifetime.Token).Forget();
        }

        private async UniTaskVoid RunAsync(CancellationToken cancellationToken)
        {
            var settings = new NetworkSettings
            {
                GatewayHost = config.GatewayHost,
                GatewayPort = config.GatewayPort
            };

            if (_injected != null)
            {
                _client = _injected;
                Debug.Log("[bootstrap] using the NetworkClient from the container");
            }
            else
            {
                _owned = new NetworkClient(
                    settings, new DefaultTransportFactory(), new JsonWireCodec(), new UnityNetLog());
                _client = _owned;
                Debug.Log("[bootstrap] no container found — constructed a NetworkClient locally. " +
                          "Register RegisterNetworking() in GameLifetimeScope for the real wiring.");
            }

            _client.StateChanged += OnStateChanged;
            _client.SnapshotReceived += OnSnapshot;
            _client.SessionClosed += OnSessionClosed;
            _client.GatewayClosed += OnGatewayClosed;

            Debug.Log($"[bootstrap] step 1/5 — minting a development JWT for '{config.UserId}' " +
                      "(HS256, shared secret; in production Nakama issues this)");
            string jwt;
            try
            {
                jwt = DevJwt.Sign(config.UserId, config.JwtSecret, TimeSpan.FromSeconds(config.JwtLifetimeSeconds));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[bootstrap] could not mint a token: {ex.Message}");
                return;
            }

            Debug.Log($"[bootstrap] step 2/5 — connecting to gateway {config.GatewayHost}:{config.GatewayPort}, " +
                      $"map '{config.MapId}'");
            try
            {
                await _client.ConnectAsync(jwt, config.MapId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[bootstrap] connect failed: {ex.Message}. " +
                               $"Is the gateway listening on {config.GatewayHost}:{config.GatewayPort} " +
                               "and is its JWT_SECRET the same as the one in the config asset?");
                return;
            }

            Debug.Log($"[bootstrap] step 4/5 — in world as '{_client.UserId}'. " +
                      "Gateway connection kept open so an eviction can still reach us.");

            await SendInputLoopAsync(cancellationToken);
        }

        /// <summary>
        /// Sends one input per simulation tick for as long as the session lives.
        /// </summary>
        /// <remarks>
        /// The direction is synthetic — a slow circle — purely so positions move and
        /// the snapshot stream is visibly doing something. No integration, clamping,
        /// normalisation or cooldown check happens here: those are the server's rules
        /// and live in <c>Shared.GameLogic</c>. The cadence and the timestep come from
        /// <c>GameConstants</c> / <c>MovementSystem</c> rather than from a number typed
        /// into this file, so client and server cannot drift apart on the tick rate.
        /// </remarks>
        private async UniTask SendInputLoopAsync(CancellationToken cancellationToken)
        {
            var dt = MovementSystem.DeltaTimeForTickRate(config.InputRateHz);
            var period = TimeSpan.FromSeconds(dt);
            var angle = 0f;

            Debug.Log($"[bootstrap] step 5/5 — streaming input at {config.InputRateHz} Hz (dt {dt:F4}s), " +
                      $"logging every {config.SnapshotLogInterval}th snapshot");

            while (!cancellationToken.IsCancellationRequested && _client.Session != null && _client.Session.IsConnected)
            {
                float moveX = 0f, moveY = 0f;
                if (config.SendSyntheticInput)
                {
                    moveX = Mathf.Cos(angle);
                    moveY = Mathf.Sin(angle);
                    angle += dt * 0.5f;
                }

                _inputTick++;
                _client.Session.SendInput(_inputTick, moveX, moveY);

                try
                {
                    await UniTask.Delay(period, DelayType.Realtime, PlayerLoopTiming.Update, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            Debug.Log("[bootstrap] input loop stopped — the session is no longer connected");
        }

        private void OnStateChanged(NetworkClientState state)
        {
            switch (state)
            {
                case NetworkClientState.Authenticating:
                    Debug.Log("[bootstrap]   → auth: sending the JWT to the gateway");
                    break;
                case NetworkClientState.Assigning:
                    Debug.Log($"[bootstrap] step 3/5 — enter_world '{config.MapId}': asking the gateway " +
                              "which game server to dial");
                    break;
                case NetworkClientState.Joining:
                    Debug.Log("[bootstrap]   → dialing the game server directly and spending the join token " +
                              "(single-use, 30 s, pinned to that server)");
                    break;
                case NetworkClientState.InWorld:
                    Debug.Log("[bootstrap]   → join accepted");
                    break;
                case NetworkClientState.Ended:
                    Debug.Log("[bootstrap]   → session ended");
                    break;
                case NetworkClientState.Disconnected:
                    Debug.Log("[bootstrap]   → disconnected");
                    break;
            }
        }

        private void OnSnapshot(ResolvedSnapshot snapshot)
        {
            _snapshotsSeen++;
            if (_snapshotsSeen % config.SnapshotLogInterval != 0)
            {
                return;
            }

            // World is already merged by Shared.GameLogic's SnapshotMerger when this
            // fires, so this reads reconstructed state rather than one delta.
            var world = _client.World;
            var self = string.Empty;
            if (!string.IsNullOrEmpty(_client.UserId) &&
                world.TryGet(_client.UserId, out EntitySnapshotData me))
            {
                self = $", me=({me.X:F2}, {me.Y:F2}) hp {me.Hp}/{me.MaxHp}";
            }

            // Report the newest input tick we have SENT next to the newest the server
            // has ACKED. The gap is the diagnostic: equal-ish means reconciliation has
            // an anchor, while a frozen ack against a climbing send count means our
            // input is not landing, which is invisible if only the ack is logged.
            Debug.Log($"[bootstrap] snapshot #{_snapshotsSeen} tick {snapshot.Tick} " +
                      $"{(snapshot.Full ? "keyframe" : "delta")} sent {_inputTick} ack {snapshot.AckTick} " +
                      $"rtt {_client.Session?.RoundTripMs ?? 0}ms — world has {world.Count} entities " +
                      $"({world.Keyframes} keyframes, {world.Deltas} deltas){self}");
        }

        private void OnSessionClosed(DisconnectInfo info) =>
            Debug.Log($"[bootstrap] gameplay connection closed: {info}");

        private void OnGatewayClosed(DisconnectInfo info)
        {
            if (info.Cause == DisconnectCause.Kicked)
            {
                // The gateway is not in the gameplay path, so this does not end the
                // session by itself — it means this account logged in somewhere else.
                Debug.LogWarning($"[bootstrap] evicted by the gateway: {info.Reason}");
                return;
            }

            Debug.Log($"[bootstrap] gateway connection closed: {info}");
        }

        private void OnDestroy()
        {
            _lifetime?.Cancel();
            _lifetime?.Dispose();
            _lifetime = null;

            if (_client != null)
            {
                _client.StateChanged -= OnStateChanged;
                _client.SnapshotReceived -= OnSnapshot;
                _client.SessionClosed -= OnSessionClosed;
                _client.GatewayClosed -= OnGatewayClosed;
                _client.Disconnect();
            }

            // Only dispose what this component created; a container-owned client
            // outlives the scene on purpose.
            _owned?.Dispose();
            _owned = null;
            _client = null;
        }
    }
}
