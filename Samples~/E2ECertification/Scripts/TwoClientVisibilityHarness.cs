using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Json;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.Transport;
using Nakama;
using Scripts.Nakama;
using UnityEngine;

namespace Samples.NetcodeE2E
{
    /// <summary>
    /// Two independent clients, two Nakama identities, one map — does each see the
    /// other? This is the first test of the multiplayer claim itself; every other
    /// certification here has been single-client, where a world of one entity proves
    /// nothing about remote visibility.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two <see cref="NetworkClient"/> instances in one process is genuinely two
    /// clients to the server: the netcode is per-connection, holds no static state, and
    /// each instance owns its own socket, codec and handle table.
    /// </para>
    /// <para>
    /// Three things this harness deliberately guards against, each of which produces a
    /// FALSE NEGATIVE that looks like a broken server:
    /// </para>
    /// <list type="number">
    /// <item>Area of interest is 50 units. Two clients further apart than that are
    /// mutually invisible and the server is correct to omit them, so both are driven
    /// with near-identical movement and the run is kept short.</item>
    /// <item>Positions are persisted per user, so a reused device id respawns at a
    /// saved position that may be far away. Every run mints BRAND NEW device ids.</item>
    /// <item><see cref="NakamaSessionService"/> caches its session in PlayerPrefs under
    /// one shared key, so going through the auth provider would restore the SAME
    /// session for both clients and silently test one user against itself. Each client
    /// authenticates with an explicit device id instead.</item>
    /// </list>
    /// </remarks>
    public sealed class TwoClientVisibilityHarness : MonoBehaviour
    {
        [Header("Gateway")]
        [SerializeField] private string gatewayHost = "127.0.0.1";
        [SerializeField] private int gatewayPort = 8000;
        [SerializeField] private string mapId = "map_01";

        [Header("Wire")]
        [Tooltip("Protobuf is where interning and the handle table actually do work. " +
                 "With two entities in view, A's snapshots intern two of them.")]
        [SerializeField] private WireEncoding encoding = WireEncoding.Protobuf;

        [Header("Run")]
        [Tooltip("Kept short and with near-identical movement so neither client drifts " +
                 "outside the other's 50-unit AOI, which would read as a failure.")]
        [SerializeField] private float observeSeconds = 25f;

        [SerializeField] private int inputRateHz = 15;

        // --- Results ---
        public static string UserA = "";
        public static string UserB = "";
        public static bool DistinctIdentities;
        public static int WorldCountA;
        public static int WorldCountB;
        public static string WorldDumpA = "";
        public static string WorldDumpB = "";
        public static bool ASeesB;
        public static bool BSeesA;
        public static int EntitiesInSnapshotA;
        public static string PositionTrackA = "";      // B's position as A sees it, over time
        public static string PositionTruthB = "";      // B's position as B sees it, same instants
        public static bool RemotePositionAdvanced;
        public static bool RemovalObservedByA;
        public static int WorldCountAAfterBLeft = -1;
        public static string WorldDumpAAfterBLeft = "";
        public static string FatalError = "";
        public static bool Finished;
        public static int MaxWorldCountA;
        public static int MaxWorldCountB;
        public static bool EverSawEachOther;
        public static string DistanceTrack = "";
        public static float RemovalWaitSeconds = -1f;

        private NetworkClient _a;
        private NetworkClient _b;
        private CancellationTokenSource _cts;

        private IWireCodec NewCodec() =>
            encoding == WireEncoding.Protobuf
                ? (IWireCodec)new ProtobufWireCodec()
                : new JsonWireCodec();

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
                Debug.Log("[2C] cancelled");
            }
            catch (Exception ex)
            {
                FatalError = ex.GetType().Name + ": " + ex.Message;
                Debug.LogError($"[2C] FATAL {FatalError}");
            }
            finally
            {
                Finished = true;
                Debug.Log("[2C] === FINISHED ===");
            }
        }

        private async UniTask RunFlowAsync(CancellationToken ct)
        {
            // Unique per run so both users spawn fresh rather than at a persisted
            // position from an earlier run, which is how a previous attempt elsewhere
            // ended up with two clients nowhere near each other.
            var tag = DateTime.UtcNow.Ticks.ToString();
            var deviceA = "two-client-A-" + tag;
            var deviceB = "two-client-B-" + tag;
            Debug.Log($"[2C] encoding={encoding} deviceA={deviceA} deviceB={deviceB}");

            var settings = new NakamaSettings();

            var nakamaA = new NakamaSessionService(settings);
            var nakamaB = new NakamaSessionService(settings);

            var sessionA = await nakamaA.AuthenticateDeviceAsync(deviceA, ct);
            var sessionB = await nakamaB.AuthenticateDeviceAsync(deviceB, ct);
            UserA = sessionA.UserId;
            UserB = sessionB.UserId;
            DistinctIdentities = UserA != UserB && !string.IsNullOrEmpty(UserA);
            Debug.Log($"[2C] A={UserA}\n[2C] B={UserB}\n[2C] distinct={DistinctIdentities}");

            if (!DistinctIdentities)
            {
                throw new InvalidOperationException(
                    "both clients authenticated as the same Nakama user; the visibility " +
                    "result would be meaningless");
            }

            var jwtA = await GatewayTokenAsync(nakamaA, sessionA, ct);
            var jwtB = await GatewayTokenAsync(nakamaB, sessionB, ct);

            _a = NewClient("A");
            _b = NewClient("B");

            await _a.ConnectAsync(jwtA, mapId, ct);
            Debug.Log($"[2C] A in world as '{_a.UserId}'");
            await _b.ConnectAsync(jwtB, mapId, ct);
            Debug.Log($"[2C] B in world as '{_b.UserId}'");

            await ObserveAsync(ct);

            // ---- What does each side actually hold? ----
            WorldCountA = _a.World.Count;
            WorldCountB = _b.World.Count;
            WorldDumpA = Dump(_a);
            WorldDumpB = Dump(_b);
            ASeesB = _a.World.TryGet(UserB, out _);
            BSeesA = _b.World.TryGet(UserA, out _);

            Debug.Log($"[2C] === RESULT ===");
            Debug.Log($"[2C] A world count={WorldCountA} : {WorldDumpA}");
            Debug.Log($"[2C] B world count={WorldCountB} : {WorldDumpB}");
            Debug.Log($"[2C] A sees B = {ASeesB}   B sees A = {BSeesA}");

            // ---- Does B's remote position actually advance in A's world? ----
            Debug.Log($"[2C] B as seen by A over time: {PositionTrackA}");
            Debug.Log($"[2C] B as reported by B     : {PositionTruthB}");
            Debug.Log($"[2C] remote position advanced = {RemotePositionAdvanced}");

            // ---- Disconnect B; does A get a removal? ----
            if (ASeesB)
            {
                Debug.Log("[2C] disconnecting B to look for a removal on A");
                _b.Disconnect();

                // Must exceed the server's 30 s entity hold: it keeps a disconnected
                // player's entity so a reconnect can reclaim it, so any wait shorter
                // than 30 s cannot observe a removal and proves nothing.
                var deadline = DateTime.UtcNow.AddSeconds(42);
                while (DateTime.UtcNow < deadline)
                {
                    if (!_a.World.TryGet(UserB, out _))
                    {
                        RemovalObservedByA = true;
                        break;
                    }

                    await Tick(ct);
                }

                RemovalWaitSeconds = (float)(DateTime.UtcNow - (deadline.AddSeconds(-42))).TotalSeconds;
                WorldCountAAfterBLeft = _a.World.Count;
                WorldDumpAAfterBLeft = Dump(_a);
                Debug.Log($"[2C] after B left: removalSeenByA={RemovalObservedByA} " +
                          $"count={WorldCountAAfterBLeft} : {WorldDumpAAfterBLeft}");
            }

            _a.Disconnect();
            await Tick(ct);
        }

        private NetworkClient NewClient(string label)
        {
            var c = new NetworkClient(
                new NetworkSettings { GatewayHost = gatewayHost, GatewayPort = gatewayPort },
                new DefaultTransportFactory(), NewCodec(), new UnityNetLog());

            if (label == "A")
            {
                c.SnapshotReceived += s => EntitiesInSnapshotA = Math.Max(EntitiesInSnapshotA, s.Entities.Count);
            }

            return c;
        }

        /// <summary>
        /// Drives both clients with the SAME direction so they stay inside each other's
        /// 50-unit AOI, sampling the remote position as each side reports it.
        /// </summary>
        private async UniTask ObserveAsync(CancellationToken ct)
        {
            var hz = inputRateHz < 1 ? 15 : inputRateHz;
            var dt = 1f / hz;
            var started = DateTime.UtcNow;
            long tick = 0;
            var seenByA = new List<string>();
            var truthB = new List<string>();
            var lastSample = DateTime.MinValue;
            var dist = new List<string>();

            while (!ct.IsCancellationRequested)
            {
                var elapsed = (float)(DateTime.UtcNow - started).TotalSeconds;
                if (elapsed >= observeSeconds) break;

                tick++;

                // IDENTICAL vectors, not merely similar. Two different headings — even
                // (1,0) vs (0.6,0.3) — separate linearly and crossed the 50-unit AOI at
                // ~20s on the first attempt, which read as "they cannot see each other"
                // when in fact they had drifted out of range. They are distinguishable
                // by user id; they do not need to be distinguishable by position.
                _a.Session?.SendInput(tick, 1f, 0f);
                _b.Session?.SendInput(tick, 1f, 0f);

                if ((DateTime.UtcNow - lastSample).TotalSeconds >= 4)
                {
                    lastSample = DateTime.UtcNow;

                    if (_a.World.TryGet(UserB, out var bAsSeenByA))
                    {
                        seenByA.Add($"{elapsed:F0}s=({bAsSeenByA.X:F2},{bAsSeenByA.Y:F2})");
                    }
                    else
                    {
                        seenByA.Add($"{elapsed:F0}s=ABSENT");
                    }

                    if (_b.World.TryGet(UserB, out var bAsSeenByB))
                    {
                        truthB.Add($"{elapsed:F0}s=({bAsSeenByB.X:F2},{bAsSeenByB.Y:F2})");
                    }

                    // Peak, not final. The first run ended with count 1 on both sides
                    // even though both had held 2 for 20s — end state alone hides that.
                    MaxWorldCountA = Math.Max(MaxWorldCountA, _a.World.Count);
                    MaxWorldCountB = Math.Max(MaxWorldCountB, _b.World.Count);
                    if (_a.World.TryGet(UserB, out _) && _b.World.TryGet(UserA, out _))
                    {
                        EverSawEachOther = true;
                    }

                    // Distance is the only way to tell "cannot see" from "out of range".
                    if (_a.World.TryGet(UserA, out var selfA) &&
                        _b.World.TryGet(UserB, out var selfB))
                    {
                        var d = Mathf.Sqrt(
                            (selfA.X - selfB.X) * (selfA.X - selfB.X) +
                            (selfA.Y - selfB.Y) * (selfA.Y - selfB.Y));
                        dist.Add($"{elapsed:F0}s={d:F1}u" +
                                 $"{(_a.World.TryGet(UserB, out _) ? "/visible" : "/ABSENT")}");
                    }
                }

                await Tick(ct, dt);
            }

            DistanceTrack = string.Join(" ", dist);
            PositionTrackA = string.Join(" ", seenByA);
            PositionTruthB = string.Join(" ", truthB);

            // "Appearing once" is not "being updated": require at least two distinct
            // sampled positions for the remote entity in A's world.
            var distinct = seenByA.Where(s => !s.EndsWith("ABSENT")).Distinct().Count();
            RemotePositionAdvanced = distinct >= 2;
        }

        private static UniTask Tick(CancellationToken ct, float seconds = 0.2f) =>
            UniTask.Delay(TimeSpan.FromSeconds(seconds), DelayType.Realtime,
                PlayerLoopTiming.Update, ct);

        private static string Dump(NetworkClient c)
        {
            var sb = new StringBuilder();
            foreach (var kv in c.World.Entities)
            {
                sb.Append($"[{kv.Key.Substring(0, Math.Min(8, kv.Key.Length))}… ")
                  .Append($"x={kv.Value.X:F2} y={kv.Value.Y:F2} hp={kv.Value.Hp}] ");
            }

            return sb.Length == 0 ? "(empty)" : sb.ToString();
        }

        private static async UniTask<string> GatewayTokenAsync(
            NakamaSessionService nakama, ISession session, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var rpc = await nakama.Client.RpcAsync(session, "gateway_token", "{}");
            var token = JsonParser.Parse(rpc.Payload ?? "{}").GetString("token");
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException(
                    "gateway_token RPC returned no token; payload: " + rpc.Payload);
            }

            return token;
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            _a?.Disconnect();
            _a?.Dispose();
            _b?.Disconnect();
            _b?.Dispose();
            _a = null;
            _b = null;
        }
    }
}
