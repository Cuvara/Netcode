using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.Transport;
using Nakama;
using Scripts.Nakama;
using UnityEngine;

namespace Samples.NetcodeE2E
{
    /// <summary>
    /// Client-driven end-to-end certification harness: Nakama device auth, the
    /// gateway_token RPC, the gateway hop, the game server hop, and the
    /// input/snapshot loop — all initiated from inside Unity, no pasted token and
    /// no external tooling.
    /// </summary>
    /// <remarks>
    /// A test harness, not shipping code. It lives under Assets/Samples so that
    /// nothing inside com.cuvara.netcode has to change to run the certification.
    /// </remarks>
    public sealed class NetcodeE2EHarness : MonoBehaviour
    {
        [Header("Nakama")]
        [SerializeField] private string deviceId = "unity-e2e-device-0001";

        [Header("Gateway")]
        [SerializeField] private string gatewayHost = "127.0.0.1";
        [SerializeField] private int gatewayPort = 8000;
        [SerializeField] private string mapId = "map_01";

        [Header("Loop")]
        [SerializeField] private int inputRateHz = 15;
        [SerializeField] private int snapshotLogInterval = 15;

        [Tooltip("Must exceed 60s to exercise the heartbeat: the server pings every 10s " +
                 "and drops a connection after 30s without a pong, and sending input does " +
                 "NOT reset that timer. Any run shorter than 30s passes regardless.")]
        [SerializeField] private float runSeconds = 70f;

        [SerializeField] private float resyncAfterSeconds = 30f;

        [Header("Wire")]
        [Tooltip("Protobuf is the backend default and carries interning plus the entity " +
                 "enum; JSON exercises neither. Both servers mirror the encoding of the " +
                 "first frame per connection, so this needs no server change.")]
        [SerializeField] private WireEncoding encoding = WireEncoding.Protobuf;

        // --- Results, read back through script-execute after the run. ---
        public static string NakamaUserId1 = "";
        public static string NakamaUserId2 = "";
        public static bool SameUserOnReAuth;
        public static string RawSessionTokenResult = "";
        public static string GatewayJwtSource = "";
        public static string AuthedUserId = "";
        public static string ServerAddrRaw = "";
        public static string ServerAddrDialed = "";
        public static bool JoinTokenPresent;
        public static string TransportUsed = "";
        public static int Snapshots;
        public static int Keyframes;
        public static int Deltas;
        public static long LastAckTick;
        public static long LastTick;
        public static int WorldCount;
        public static string WorldDump = "";
        public static bool ResyncRequested;
        public static int KeyframesBeforeResync = -1;
        public static int KeyframesAfterResync = -1;
        public static bool CleanDisconnect;
        public static string FatalError = "";
        public static bool Finished;
        public static string EncodingUsed = "";
        public static float RanSeconds;
        public static long LastRttMs;
        public static bool SurvivedHeartbeatWindow;
        public static bool ReconnectJoined;
        public static float ReconnectGapSeconds = -1f;
        public static string ReconnectUserId = "";
        public static int ReconnectSnapshots;
        public static int ReconnectWorldCount;
        public static string ReconnectWorldDump = "";

        private NetworkClient _client;

        /// <summary>A fresh codec per connection — the encoding is latched per socket.</summary>
        private IWireCodec NewCodec() =>
            encoding == WireEncoding.Protobuf
                ? (IWireCodec)new ProtobufWireCodec()
                : new JsonWireCodec();

        private CancellationTokenSource _cts;
        private long _inputTick;

        private void Start()
        {
            Application.runInBackground = true;
            _cts = new CancellationTokenSource();
            RunAsync(_cts.Token).Forget();
        }

        private async UniTaskVoid RunAsync(CancellationToken ct)
        {
            try
            {
                await RunFlowAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Debug.Log("[E2E] cancelled");
            }
            catch (Exception ex)
            {
                FatalError = ex.GetType().Name + ": " + ex.Message;
                Debug.LogError($"[E2E] FATAL {FatalError}");
            }
            finally
            {
                Finished = true;
                Debug.Log("[E2E] === HARNESS FINISHED ===");
            }
        }

        private async UniTask RunFlowAsync(CancellationToken ct)
        {
            // ---------- A3: Nakama device auth, from the client ----------
            var settings = new NakamaSettings();
            Debug.Log($"[E2E] A3 — Nakama at {settings.Scheme}://{settings.Host}:{settings.Port} " +
                      $"serverKey='{settings.ServerKey}', deviceId='{deviceId}'");

            var nakama = new NakamaSessionService(settings);
            var session1 = await nakama.AuthenticateDeviceAsync(deviceId, ct);
            NakamaUserId1 = session1.UserId;
            Debug.Log($"[E2E] A3.1 first device auth OK — user_id={session1.UserId} " +
                      $"username={session1.Username} created={session1.Created}");

            // Re-auth with the SAME device id must return the SAME user id.
            var nakama2 = new NakamaSessionService(settings);
            var session2 = await nakama2.AuthenticateDeviceAsync(deviceId, ct);
            NakamaUserId2 = session2.UserId;
            SameUserOnReAuth = session1.UserId == session2.UserId;
            Debug.Log($"[E2E] A3.2 re-auth same deviceId — user_id={session2.UserId} " +
                      $"created={session2.Created} SAME_USER={SameUserOnReAuth}");

            // ---------- A2: does the raw Nakama session token work as a gateway JWT? ----------
            // NakamaAuthProvider returns Session.AuthToken. Test that claim directly.
            Debug.Log("[E2E] A2 — testing whether the RAW Nakama session token is accepted " +
                      "by the gateway (this is what NakamaAuthProvider.GetJwtAsync returns)");
            RawSessionTokenResult = await TryGatewayAuthAsync(session1.AuthToken, ct);
            Debug.Log($"[E2E] A2 RESULT raw-session-token → {RawSessionTokenResult}");

            // ---------- A1: the gateway_token RPC, the documented correct path ----------
            Debug.Log("[E2E] A1 — calling Nakama RPC 'gateway_token' for a gateway-signed JWT");
            var gatewayJwt = await FetchGatewayTokenAsync(nakama, session1, ct);
            GatewayJwtSource = "nakama rpc gateway_token";
            Debug.Log($"[E2E] A1 got gateway JWT via RPC, length={gatewayJwt.Length}, " +
                      $"prefix={gatewayJwt.Substring(0, Math.Min(24, gatewayJwt.Length))}...");

            // ---------- B5: an explicit enter_world, so the assignment can be logged ----------
            // NetworkClient performs enter_world internally and does not surface the
            // response, so one deliberate pass is made here purely to capture it.
            await CaptureAssignmentAsync(gatewayJwt, ct);

            // ---------- B/C: full flow with the RPC-issued token ----------
            EncodingUsed = encoding.ToString();
            Debug.Log($"[E2E] wire encoding = {EncodingUsed}");

            var netSettings = new NetworkSettings { GatewayHost = gatewayHost, GatewayPort = gatewayPort };
            _client = new NetworkClient(
                netSettings, new DefaultTransportFactory(), NewCodec(), new UnityNetLog());
            _client.SnapshotReceived += OnSnapshot;
            _client.SessionClosed += info => Debug.Log($"[E2E] session closed: {info}");
            _client.GatewayClosed += info => Debug.Log($"[E2E] gateway closed: {info}");

            Debug.Log($"[E2E] B4/B5/C6 — ConnectAsync to gateway {gatewayHost}:{gatewayPort}, map '{mapId}'");
            await _client.ConnectAsync(gatewayJwt, mapId, ct);

            AuthedUserId = _client.UserId;
            Debug.Log($"[E2E] B4 PASS auth accepted, in world as user_id='{AuthedUserId}'");

            if (_client.Session != null && _client.Session.IsConnected)
            {
                Debug.Log($"[E2E] C6 PASS join accepted, game session connected " +
                          $"(session user_id='{_client.Session.UserId}')");
            }

            // ---------- C7: input + snapshot loop ----------
            await InputLoopAsync(ct);

            // ---------- C8: world state, read from the live client ----------
            DumpWorld();

            // ---------- C9: clean disconnect ----------
            var firstWorld = WorldDump;
            _client.Disconnect();
            await UniTask.Delay(TimeSpan.FromMilliseconds(400), DelayType.Realtime,
                PlayerLoopTiming.Update, ct);
            CleanDisconnect = true;
            var disconnectedAt = DateTime.UtcNow;
            Debug.Log("[E2E] C9 disconnect requested, no exception raised");

            // ---------- D11: reconnect INSIDE the server's 30 s entity hold ----------
            // Done in the same play session on purpose: a domain reload costs longer
            // than the hold window, so cycling play mode can never test this.
            await UniTask.Delay(TimeSpan.FromSeconds(3), DelayType.Realtime,
                PlayerLoopTiming.Update, ct);

            var gap = (DateTime.UtcNow - disconnectedAt).TotalSeconds;
            Debug.Log($"[E2E] D11 reconnecting {gap:F1}s after disconnect " +
                      $"(server holds the entity for 30s, so this must be < 30)");

            _client.Dispose();
            _client = new NetworkClient(
                netSettings, new DefaultTransportFactory(), NewCodec(), new UnityNetLog());
            _client.SnapshotReceived += OnReconnectSnapshot;

            // A fresh gateway token, exactly as a real client would on resume.
            var jwt2 = await FetchGatewayTokenAsync(nakama, session1, ct);
            await _client.ConnectAsync(jwt2, mapId, ct);
            ReconnectGapSeconds = (float)gap;
            ReconnectJoined = true;
            ReconnectUserId = _client.UserId;
            Debug.Log($"[E2E] D11 rejoined as user_id='{ReconnectUserId}' " +
                      $"sameIdentity={ReconnectUserId == AuthedUserId}");

            // Let a few snapshots land so the world can be compared.
            var until = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < until && _client.Session != null && _client.Session.IsConnected)
            {
                _inputTick++;
                _client.Session.SendInput(_inputTick, 0f, 0f);
                await UniTask.Delay(TimeSpan.FromSeconds(1.0 / 15), DelayType.Realtime,
                    PlayerLoopTiming.Update, ct);
            }

            var w2 = _client.World;
            ReconnectWorldCount = w2.Count;
            var sb2 = new System.Text.StringBuilder();
            foreach (var kv in w2.Entities)
            {
                sb2.Append($"[{kv.Key}] x={kv.Value.X:F2} y={kv.Value.Y:F2} hp={kv.Value.Hp}; ");
            }

            ReconnectWorldDump = sb2.ToString();
            Debug.Log($"[E2E] D11 after reconnect — entities={ReconnectWorldCount} " +
                      $"snapshots={ReconnectSnapshots} keyframes={w2.Keyframes}");
            Debug.Log($"[E2E] D11 world BEFORE disconnect: {firstWorld}");
            Debug.Log($"[E2E] D11 world AFTER  reconnect : {ReconnectWorldDump}");

            _client.Disconnect();
            await UniTask.Delay(TimeSpan.FromMilliseconds(300), DelayType.Realtime,
                PlayerLoopTiming.Update, ct);
            Debug.Log("[E2E] D11 second disconnect clean");
        }

        /// <summary>
        /// Auths and runs one enter_world so the gateway's assignment can be logged
        /// verbatim. The join token obtained here is deliberately discarded — it is
        /// single-use, and the real flow mints its own.
        /// </summary>
        private async UniTask CaptureAssignmentAsync(string jwt, CancellationToken ct)
        {
            var settings = new NetworkSettings { GatewayHost = gatewayHost, GatewayPort = gatewayPort };
            using (var probe = new GatewayClient(
                settings, new DefaultTransportFactory(), NewCodec(), new UnityNetLog()))
            {
                await probe.AuthenticateAsync(jwt, ct);
                Debug.Log($"[E2E] B4 auth_resp ok, user_id='{probe.UserId}'");

                var assignment = await probe.EnterWorldAsync(mapId, ct);
                ServerAddrDialed = assignment.Endpoint.ToString();
                TransportUsed = assignment.Transport.ToString();
                JoinTokenPresent = !string.IsNullOrEmpty(assignment.JoinToken);

                Debug.Log($"[E2E] B5 enter_world_resp — server_addr(normalised)='{ServerAddrDialed}' " +
                          $"host='{assignment.Endpoint.Host}' port={assignment.Endpoint.Port} " +
                          $"transport='{TransportUsed}' join_token_present={JoinTokenPresent} " +
                          $"join_token_len={assignment.JoinToken?.Length ?? 0}");
                probe.Close();
            }
        }

        /// <summary>
        /// Runs only the gateway auth hop with a candidate token and reports the
        /// outcome as a string, so a rejection is data rather than a thrown test.
        /// </summary>
        private async UniTask<string> TryGatewayAuthAsync(string jwt, CancellationToken ct)
        {
            var settings = new NetworkSettings { GatewayHost = gatewayHost, GatewayPort = gatewayPort };
            using (var probe = new GatewayClient(
                settings, new DefaultTransportFactory(), NewCodec(), new UnityNetLog()))
            {
                try
                {
                    await probe.AuthenticateAsync(jwt, ct);
                    return $"ACCEPTED (user_id={probe.UserId})";
                }
                catch (Exception ex)
                {
                    return $"REJECTED — {ex.GetType().Name}: {ex.Message}";
                }
            }
        }

        /// <summary>
        /// Nakama wraps RPC payloads as a JSON-encoded string, so the result has to
        /// be unwrapped twice: once out of the envelope, once out of the string.
        /// </summary>
        private async UniTask<string> FetchGatewayTokenAsync(
            NakamaSessionService nakama, ISession session, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var rpc = await nakama.Client.RpcAsync(session, "gateway_token", "{}");
            var payload = rpc.Payload ?? string.Empty;
            Debug.Log($"[E2E] A1 raw RPC payload: {payload}");

            // Payload may itself be a quoted JSON string literal.
            if (payload.Length > 1 && payload[0] == '"')
            {
                payload = UnquoteJson(payload);
            }

            var token = ExtractJsonString(payload, "token");
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException(
                    "gateway_token RPC returned no 'token' field. Payload: " + payload);
            }

            return token;
        }

        private static string UnquoteJson(string quoted)
        {
            var inner = quoted.Substring(1, quoted.Length - 2);
            return inner.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        private static string ExtractJsonString(string json, string key)
        {
            var needle = "\"" + key + "\"";
            var at = json.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) return null;

            var colon = json.IndexOf(':', at + needle.Length);
            if (colon < 0) return null;

            var open = json.IndexOf('"', colon + 1);
            if (open < 0) return null;

            var sb = new System.Text.StringBuilder();
            for (var i = open + 1; i < json.Length; i++)
            {
                if (json[i] == '\\' && i + 1 < json.Length) { sb.Append(json[i + 1]); i++; continue; }
                if (json[i] == '"') break;
                sb.Append(json[i]);
            }

            return sb.ToString();
        }

        private async UniTask InputLoopAsync(CancellationToken ct)
        {
            var hz = inputRateHz < 1 ? 15 : inputRateHz;
            var dt = 1f / hz;
            var period = TimeSpan.FromSeconds(dt);
            var angle = 0f;
            var started = DateTime.UtcNow;
            var resyncDone = false;

            Debug.Log($"[E2E] C7 — streaming synthetic input at {hz} Hz for {runSeconds}s");

            while (!ct.IsCancellationRequested &&
                   _client.Session != null && _client.Session.IsConnected)
            {
                var elapsed = (float)(DateTime.UtcNow - started).TotalSeconds;
                if (elapsed >= runSeconds) break;

                // ---------- D10: force a keyframe partway through ----------
                if (!resyncDone && elapsed >= resyncAfterSeconds)
                {
                    resyncDone = true;
                    KeyframesBeforeResync = _client.World.Keyframes;
                    _client.Session.RequestResync();
                    ResyncRequested = true;
                    Debug.Log($"[E2E] D10 RequestResync() sent at {elapsed:F1}s " +
                              $"(keyframes before={KeyframesBeforeResync})");
                }

                _inputTick++;
                _client.Session.SendInput(_inputTick, Mathf.Cos(angle), Mathf.Sin(angle));
                angle += dt * 0.5f;

                try
                {
                    await UniTask.Delay(period, DelayType.Realtime, PlayerLoopTiming.Update, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            RanSeconds = (float)(DateTime.UtcNow - started).TotalSeconds;
            LastRttMs = _client.Session?.RoundTripMs ?? -1L;

            // The server pings every 10s and drops a connection after 30s without a
            // pong; input traffic does not reset that timer. Still connected past 60s
            // is the only evidence that pongs are actually being answered.
            SurvivedHeartbeatWindow =
                RanSeconds > 60f && _client.Session != null && _client.Session.IsConnected;
            Debug.Log($"[E2E] heartbeat: ran {RanSeconds:F1}s, stillConnected=" +
                      $"{_client.Session?.IsConnected} rtt={LastRttMs}ms " +
                      $"survived60s={SurvivedHeartbeatWindow}");

            if (ResyncRequested)
            {
                KeyframesAfterResync = _client.World.Keyframes;
                Debug.Log($"[E2E] D10 keyframes after resync={KeyframesAfterResync} " +
                          $"(delta={KeyframesAfterResync - KeyframesBeforeResync})");
            }
        }

        private void OnSnapshot(ResolvedSnapshot snapshot)
        {
            Snapshots++;
            LastAckTick = snapshot.AckTick;
            LastTick = snapshot.Tick;

            if (Snapshots % Math.Max(1, snapshotLogInterval) != 0) return;

            var world = _client.World;
            Debug.Log($"[E2E] C7 snapshot #{Snapshots} tick {snapshot.Tick} " +
                      $"{(snapshot.Full ? "keyframe" : "delta")} sent {_inputTick} ack {snapshot.AckTick} " +
                      $"rtt {_client.Session?.RoundTripMs ?? 0}ms entities={world.Count} " +
                      $"keyframes={world.Keyframes} deltas={world.Deltas}");
        }

        private void OnReconnectSnapshot(ResolvedSnapshot snapshot)
        {
            ReconnectSnapshots++;
            if (ReconnectSnapshots % 15 != 0) return;
            Debug.Log($"[E2E] D11 post-reconnect snapshot #{ReconnectSnapshots} " +
                      $"tick {snapshot.Tick} {(snapshot.Full ? "keyframe" : "delta")} " +
                      $"entities={_client.World.Count}");
        }

        private void DumpWorld()
        {
            var world = _client.World;
            Keyframes = world.Keyframes;
            Deltas = world.Deltas;
            WorldCount = world.Count;

            var sb = new System.Text.StringBuilder();
            foreach (var kv in world.Entities)
            {
                var e = kv.Value;
                sb.Append($"[{kv.Key}] x={e.X:F2} y={e.Y:F2} hp={e.Hp}/{e.MaxHp}; ");
            }

            WorldDump = sb.ToString();
            Debug.Log($"[E2E] C8 WORLD STATE — entities={WorldCount} tick={world.Tick} " +
                      $"ack={world.AckTick} keyframes={Keyframes} deltas={Deltas}");
            Debug.Log($"[E2E] C8 ENTITIES: {WorldDump}");
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            if (_client != null)
            {
                _client.SnapshotReceived -= OnSnapshot;
                _client.Disconnect();
                _client.Dispose();
                _client = null;
            }
        }
    }
}
