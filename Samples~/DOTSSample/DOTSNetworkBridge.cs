using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Transport;
using Cuvara.Netcode.View;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Networking;

namespace DOTSSample
{
    /// <summary>
    /// Bridges the netcode layer to the DOTS scene: authenticates via Nakama, connects
    /// through the gateway to a game server, and reconciles replicated entities as ECS
    /// entities via <see cref="DOTSEntityView"/>.
    /// </summary>
    /// <remarks>
    /// Follows the same pattern as the WorldView sample: <see cref="WorldViewBinder"/>
    /// does the reconciliation, and this MonoBehaviour drives the connection lifecycle
    /// and calls <c>Tick</c> every frame.
    /// </remarks>
    public sealed class DOTSNetworkBridge : MonoBehaviour
    {
        [Header("Gateway")]
        [SerializeField] private string gatewayHost = "127.0.0.1";
        [SerializeField] private int gatewayPort = 8000;
        [SerializeField] private string mapId = "map_01";

        [Header("Input")]
        [SerializeField] private int inputRateHz = 15;

        [Header("Run")]
        [SerializeField] private float runSeconds = 300f;

        private NetworkClient _client;
        private WorldViewBinder _binder;
        private DOTSEntityView _view;
        private CancellationTokenSource _cts;
        private long _inputTick;
        private string _pendingAttackTarget = "";

        // --- Status for OnGUI ---
        private string _status = "Initializing...";
        private string _userId = "";
        private int _snapshotCount;
        private int _entityCount;
        private readonly List<DOTSEntityView.EntityLabel> _labelCache = new List<DOTSEntityView.EntityLabel>();
        private GUIStyle _labelStyle;
        private GUIStyle _localLabelStyle;
        private GUIStyle _fpsStyle;
        private Texture2D _bgTex;
        private Texture2D _hpBarTex;
        private Texture2D _hpBgTex;

        // --- Cached GUI strings (rebuilt only when values change) ---
        private Camera _cachedCamera;
        private int _cachedCameraFrame = -1;
        private string _cachedStatusText;
        private string _prevStatus;
        private NetworkClientState _prevState;
        private string _cachedUserText;
        private string _cachedEntityText;
        private int _prevEntityCount = -1;
        private int _prevSnapshotCount = -1;
        private string _cachedRttText;
        private int _prevRttMs = -1;
        private long _prevWorldTick = -1;
        private string _cachedFpsText;
        private string _cachedFpsStatsText;
        private string _cachedFpsRttText;
        // Own dirty-flag for the top-right RTT label. It must NOT share '_prevRttMs'
        // with the HUD label above: that field is advanced by the HUD's own cache check
        // earlier in the same OnGUI pass, so a shared flag reads as "unchanged" every
        // frame and freezes this label on its first sample.
        private int _prevFpsRttMs = -1;
        private readonly GUIContent _sharedContent = new GUIContent();

        // --- Per-entity label cache (rebuilt on entity set change) ---
        private readonly Dictionary<string, string> _entityLabelTextCache = new Dictionary<string, string>();

        // --- Combat stats cache ---
        private string _cachedCombatText;
        private int _prevKills = -1;
        private EntityQuery _combatStatsQuery;

        // --- Attack event bridge (ECS → server) ---
        private EntityQuery _attackRequestQuery;
        private float _attackDebugTimer;

        // --- FPS tracking ---
        private const int FpsSampleCount = 60;
        private readonly float[] _frameTimes = new float[FpsSampleCount];
        private int _frameIndex;
        private bool _frameBufferFull;
        private float _fpsUpdateTimer;
        private float _displayFps;
        private float _displayMs;
        private float _displayMin;
        private float _displayMax;
        private float _displayAvg;

        // --- Server status panel (bottom-left) ---
        [Header("Server Status")]
        [SerializeField] private string gameServerStatusUrl = "http://127.0.0.1:9101/status";
        [SerializeField] private string nakamaHealthUrl = "http://127.0.0.1:7350/healthcheck";
        [SerializeField] private float statusPollInterval = 4f;

        [Header("Nakama API")]
        [SerializeField] private string nakamaBaseUrl = "http://127.0.0.1:7350";
        [SerializeField] private string leaderboardId = "kills_alltime";

        [Tooltip("Maps offered at startup. One entry connects to it straight away; " +
                 "two or more draw the map selector and wait for a click.")]
        [SerializeField] private string[] availableMaps = { "map_01", "map_02" };

        private GUIStyle _serverPanelStyle;
        private GUIStyle _mapButtonStyle;

        // --- Auth ---
        private string _nakamaSessionToken;

        // --- Economy (gold) ---
        private int _goldDisplay;
        private int _goldServer;
        private int _goldOptimistic;
        private string _cachedGoldText;
        private int _prevGoldDisplay = -1;
        private int _prevLocalKills;

        // --- Leaderboard ---
        private string _cachedLeaderboardPanel = "<b>Leaderboard</b>\n\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\n(waiting...)";
        private string _prevLeaderboardRaw;
        private LeaderboardRecord[] _leaderboardRecords = Array.Empty<LeaderboardRecord>();

        [Serializable]
        private struct LeaderboardRecord
        {
            public string username;
            public string score;
            public string rank;
            public string owner_id;
        }

        [Serializable]
        private struct LeaderboardResponse
        {
            public LeaderboardRecord[] records;
        }

        [Serializable]
        private struct AccountResponse
        {
            public string wallet;
        }

        [Serializable]
        private struct WalletData
        {
            public int gold;
        }

        // --- Map selector ---
        private bool _mapSelected;
        private string _cachedMapText;

        // Cached poll results
        private bool _nakamaOk;
        private bool _gameServerOk;
        private int _gsTickRate;
        private int _gsPlayers;
        private int _gsEnemies;
        private string _gsRedis = "unknown";
        private string _gsPostgres = "unknown";
        private int _gsUptimeSeconds;

        // Cached display strings (rebuilt only on change)
        private string _cachedServerPanel;
        private bool _prevNakamaOk;
        private bool _prevGameServerOk;
        private int _prevGsTickRate = -1;
        private int _prevGsPlayers = -1;
        private int _prevGsEnemies = -1;
        private string _prevGsRedis;
        private string _prevGsPostgres;
        private int _prevGsUptime = -1;
        private bool _prevGatewayOk;

        [Serializable]
        private struct GameServerStatus
        {
            public bool ok;
            public int tick_rate;
            public int players_online;
            public int enemies_alive;
            public string redis;
            public string postgres;
            public int uptime_seconds;
        }

        private void Start()
        {
            Application.runInBackground = true;

            _view = new DOTSEntityView();
            _binder = new WorldViewBinder(_view);

            _cts = new CancellationTokenSource();
            // Connection starts when a map is selected (see the OnGUI map selector).
            // With a single map there is nothing to choose, so connect to it directly —
            // taking the id from the list rather than from 'mapId', or configuring one
            // map would silently connect to whatever 'mapId' happened to hold.
            if (availableMaps == null || availableMaps.Length == 0)
            {
                StartConnection(mapId);
            }
            else if (availableMaps.Length == 1)
            {
                StartConnection(availableMaps[0]);
            }
            PollServerStatusAsync(_cts.Token).Forget();
        }

        /// <summary>
        /// Replaces the map set offered at startup.
        /// </summary>
        /// <remarks>
        /// For callers that add this component from script — <see cref="DOTSSceneSetup"/>
        /// does — because a component added at runtime can only ever carry its field
        /// initializers, never a scene's inspector values. Call it in the same frame the
        /// component is added: <c>Start</c> reads <c>availableMaps</c> to decide between
        /// connecting directly and drawing the selector, and it runs after the frame's
        /// <c>Awake</c> pass. A null or empty array is ignored, so a caller that has
        /// nothing to say leaves the inspector-authored value alone.
        /// </remarks>
        public void ConfigureMaps(string[] maps)
        {
            if (maps == null || maps.Length == 0)
                return;
            availableMaps = maps;
        }

        private void StartConnection(string selectedMap)
        {
            mapId = selectedMap;
            _cachedMapText = "Map: " + selectedMap;
            _mapSelected = true;
            _status = "Authenticating...";
            RunAsync(_cts.Token).Forget();
        }

        private void LeaveRoom()
        {
            Debug.Log("[DOTSNet] Leaving room");

            // Cancel running tasks (RunAsync, economy, leaderboard polls)
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            // Disconnect from game server
            _client?.Disconnect();
            _client?.Dispose();
            _client = null;

            // Drop the presented world. Without this the previous session's entities stay
            // on screen and in the binder's live set, and because this sample mints a new
            // Nakama device id (and so a new user id) on every join, the previous
            // session's avatar would still be flagged as the local player while the new
            // one is too — two "★ YOU" markers. The binder now recovers from that on
            // its own, but recovering is not the same as not causing it: a rejoin should
            // start from an empty world, not from the last one plus a correction.
            _binder?.Reset();

            // Reset state for map selector
            _mapSelected = false;
            _status = "Disconnected";
            _inputTick = 0;
            _pendingAttackTarget = "";
            _snapshotCount = 0;
            _entityCount = 0;
            _cachedMapText = null;
            _cachedStatusText = null;
            _cachedEntityText = null;
            _cachedRttText = null;
            _cachedFpsRttText = null;
            _prevRttMs = -1;
            _prevFpsRttMs = -1;
            _prevWorldTick = -1;
            _cachedCombatText = null;
            _prevKills = -1;
            _prevLocalKills = 0;
            _goldOptimistic = 0;
            _entityLabelTextCache.Clear();

            // Restart server status polling (doesn't need auth)
            PollServerStatusAsync(_cts.Token).Forget();
        }

        private void Update()
        {
            // FPS sampling
            float dt = Time.unscaledDeltaTime;
            _frameTimes[_frameIndex] = dt;
            _frameIndex = (_frameIndex + 1) % FpsSampleCount;
            if (_frameIndex == 0) _frameBufferFull = true;

            _fpsUpdateTimer += dt;
            if (_fpsUpdateTimer >= 0.25f)
            {
                _fpsUpdateTimer = 0f;
                int count = _frameBufferFull ? FpsSampleCount : _frameIndex;
                if (count > 0)
                {
                    float sum = 0f;
                    float min = float.MaxValue;
                    float max = 0f;
                    for (int i = 0; i < count; i++)
                    {
                        float t = _frameTimes[i];
                        sum += t;
                        if (t < min) min = t;
                        if (t > max) max = t;
                    }
                    _displayAvg = count / sum;
                    _displayMin = 1f / max;
                    _displayMax = 1f / min;
                }
                _displayFps = 1f / dt;
                _displayMs = dt * 1000f;

                // Rebuild FPS strings at 4 Hz, not 60
                _cachedFpsText = string.Format("FPS: {0:F0}  ({1:F1}ms)", _displayFps, _displayMs);
                _cachedFpsStatsText = string.Format("Min: {0:F0}  Avg: {1:F0}  Max: {2:F0}", _displayMin, _displayAvg, _displayMax);
            }

            // Combat stats (cached query, no alloc in steady state)
            var dotsWorld = World.DefaultGameObjectInjectionWorld;
            if (dotsWorld != null && dotsWorld.IsCreated)
            {
                if (_combatStatsQuery == default)
                    _combatStatsQuery = dotsWorld.EntityManager.CreateEntityQuery(typeof(CombatStats));

                if (_combatStatsQuery.CalculateEntityCount() > 0)
                {
                    var stats = _combatStatsQuery.GetSingleton<CombatStats>();
                    if (_prevKills != stats.Kills)
                    {
                        // Optimistic gold: +10 per new kill
                        int newKills = stats.Kills - _prevLocalKills;
                        if (newKills > 0)
                            _goldOptimistic += newKills * 10;
                        _prevLocalKills = stats.Kills;

                        _prevKills = stats.Kills;
                        _cachedCombatText = "Kills: " + stats.Kills;
                    }

                    // Gold display: server value + optimistic delta, reconcile on next poll
                    _goldDisplay = _goldServer + _goldOptimistic;
                    if (_prevGoldDisplay != _goldDisplay)
                    {
                        _prevGoldDisplay = _goldDisplay;
                        _cachedGoldText = "Gold: " + _goldDisplay;
                    }
                }
            }

            if (_client == null)
                return;

            // Poll attack requests from ECS → enqueue for next input tick
            if (dotsWorld != null && dotsWorld.IsCreated)
            {
                if (_attackRequestQuery == default)
                    _attackRequestQuery = dotsWorld.EntityManager.CreateEntityQuery(typeof(AttackRequest));

                int attackCount = _attackRequestQuery.CalculateEntityCount();

                _attackDebugTimer -= Time.deltaTime;
                if (_attackDebugTimer <= 0f)
                {
                    _attackDebugTimer = 1f;
                    Debug.Log($"[Debug] AttackRequest count: {attackCount}, pending: '{_pendingAttackTarget}'");
                }

                if (attackCount > 0)
                {
                    var entities = _attackRequestQuery.ToEntityArray(Allocator.Temp);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        var req = dotsWorld.EntityManager.GetComponentData<AttackRequest>(entities[i]);
                        var targetId = req.TargetId.ToString();
                        Debug.Log($"[Debug] AttackRequest consumed: '{targetId}'");
                        if (!string.IsNullOrEmpty(targetId))
                            _pendingAttackTarget = targetId;
                        dotsWorld.EntityManager.DestroyEntity(entities[i]);
                    }
                    entities.Dispose();
                }
            }

            _binder.Tick(_client.World, _client.UserId);
            _entityCount = _view.Count;
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _client?.Disconnect();
            _client?.Dispose();
        }

        private async UniTaskVoid RunAsync(CancellationToken ct)
        {
            try
            {
                var device = $"dots-{(Application.isEditor ? "editor" : "player")}-{DateTime.UtcNow.Ticks}";
                _status = "Authenticating...";
                Debug.Log($"[DOTSNet] Authenticating device={device}");

                var auth = new SampleNakamaAuth();
                var jwt = await auth.GetGatewayTokenAsync(device, ct);
                _userId = auth.UserId;
                _nakamaSessionToken = auth.SessionToken;
                _cachedUserText = "User: " + (_userId.Length > 12 ? _userId.Substring(0, 12) : _userId) + "...";
                _status = "Connecting to gateway...";
                Debug.Log($"[DOTSNet] Auth OK, user_id={_userId}");

                // Start economy + leaderboard polling now that we have a session token
                PollEconomyAsync(ct).Forget();
                PollLeaderboardAsync(ct).Forget();

                _client = new NetworkClient(
                    new NetworkSettings { GatewayHost = gatewayHost, GatewayPort = gatewayPort },
                    new DefaultTransportFactory(), new ProtobufWireCodec(), new UnityNetLog());

                _client.StateChanged += state =>
                {
                    _status = state.ToString();
                    Debug.Log($"[DOTSNet] State -> {state}");
                };

                _client.SnapshotReceived += s =>
                {
                    _snapshotCount++;
                    _binder.NoteRemovedIds(s.Removed);
                };

                _client.SessionClosed += info =>
                {
                    _status = $"Disconnected: {info}";
                    Debug.Log($"[DOTSNet] Session closed: {info}");
                };

                await _client.ConnectAsync(jwt, mapId, ct);
                _status = "In World";
                Debug.Log($"[DOTSNet] IN WORLD as {_client.UserId}");

                var dt = 1f / Mathf.Max(1f, inputRateHz);
                var started = DateTime.UtcNow;

                while (!ct.IsCancellationRequested)
                {
                    var elapsed = (float)(DateTime.UtcNow - started).TotalSeconds;
                    if (elapsed >= runSeconds) break;

                    _inputTick++;

                    var moveX = Mathf.Sin(Time.time * 1.5f);
                    var moveY = Mathf.Cos(Time.time * 0.8f);
                    var attackTarget = _pendingAttackTarget;
                    _pendingAttackTarget = "";
                    if (!string.IsNullOrEmpty(attackTarget))
                        Debug.Log($"[Attack] Sending attack on {attackTarget} (tick {_inputTick})");
                    _client.Session?.SendInput(_inputTick, moveX, moveY, attackTarget);

                    await UniTask.Delay(TimeSpan.FromSeconds(dt), DelayType.Realtime,
                        PlayerLoopTiming.Update, ct);
                }

                _client.Disconnect();
                _status = "Run complete";
                Debug.Log("[DOTSNet] Run finished");
            }
            catch (OperationCanceledException)
            {
                _status = "Cancelled";
                Debug.Log("[DOTSNet] Cancelled");
            }
            catch (Exception ex)
            {
                _status = $"Error: {ex.Message}";
                Debug.LogError($"[DOTSNet] FATAL: {ex}");
            }
        }

        private async UniTaskVoid PollServerStatusAsync(CancellationToken ct)
        {
            // Wait for initial connection before polling
            await UniTask.Delay(TimeSpan.FromSeconds(2), DelayType.Realtime,
                PlayerLoopTiming.Update, ct);

            while (!ct.IsCancellationRequested)
            {
                // Poll Nakama health
                try
                {
                    using (var req = UnityWebRequest.Get(nakamaHealthUrl))
                    {
                        req.timeout = 3;
                        await req.SendWebRequest().ToUniTask(cancellationToken: ct);
                        _nakamaOk = req.result == UnityWebRequest.Result.Success;
                    }
                }
                catch (Exception)
                {
                    _nakamaOk = false;
                }

                // Poll game server status
                try
                {
                    using (var req = UnityWebRequest.Get(gameServerStatusUrl))
                    {
                        req.timeout = 3;
                        await req.SendWebRequest().ToUniTask(cancellationToken: ct);
                        if (req.result == UnityWebRequest.Result.Success)
                        {
                            _gameServerOk = true;
                            var status = JsonUtility.FromJson<GameServerStatus>(req.downloadHandler.text);
                            _gsTickRate = status.tick_rate;
                            _gsPlayers = status.players_online;
                            _gsEnemies = status.enemies_alive;
                            _gsRedis = status.redis ?? "unknown";
                            _gsPostgres = status.postgres ?? "unknown";
                            _gsUptimeSeconds = status.uptime_seconds;
                        }
                        else
                        {
                            _gameServerOk = false;
                        }
                    }
                }
                catch (Exception)
                {
                    _gameServerOk = false;
                }

                await UniTask.Delay(TimeSpan.FromSeconds(statusPollInterval),
                    DelayType.Realtime, PlayerLoopTiming.Update, ct);
            }
        }

        private static string FormatUptime(int totalSeconds)
        {
            if (totalSeconds < 60) return totalSeconds + "s";
            int m = totalSeconds / 60;
            int s = totalSeconds % 60;
            if (m < 60) return m + "m " + s.ToString("D2") + "s";
            int h = m / 60;
            m %= 60;
            return h + "h " + m.ToString("D2") + "m";
        }

        private void RebuildServerPanelIfNeeded()
        {
            bool gatewayOk = _client?.State == NetworkClientState.InWorld;

            if (_prevNakamaOk == _nakamaOk &&
                _prevGameServerOk == _gameServerOk &&
                _prevGsTickRate == _gsTickRate &&
                _prevGsPlayers == _gsPlayers &&
                _prevGsEnemies == _gsEnemies &&
                _prevGsRedis == _gsRedis &&
                _prevGsPostgres == _gsPostgres &&
                _prevGsUptime == _gsUptimeSeconds &&
                _prevGatewayOk == gatewayOk &&
                _cachedServerPanel != null)
                return;

            _prevNakamaOk = _nakamaOk;
            _prevGameServerOk = _gameServerOk;
            _prevGsTickRate = _gsTickRate;
            _prevGsPlayers = _gsPlayers;
            _prevGsEnemies = _gsEnemies;
            _prevGsRedis = _gsRedis;
            _prevGsPostgres = _gsPostgres;
            _prevGsUptime = _gsUptimeSeconds;
            _prevGatewayOk = gatewayOk;

            string Dot(bool ok) => ok ? "<color=#44ff44>\u25cf</color>" : "<color=#ff4444>\u25cf</color>";
            string State(bool ok) => ok ? "<color=#44ff44>Connected</color>" : "<color=#ff4444>Disconnected</color>";
            string Svc(string s) => s == "connected"
                ? "<color=#44ff44>Connected</color>"
                : "<color=#ff4444>" + s + "</color>";

            _cachedServerPanel =
                "<b>Server Status</b>\n" +
                "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\n" +
                Dot(_nakamaOk) + " Nakama         " + State(_nakamaOk) + "\n" +
                Dot(gatewayOk) + " Gateway        " + State(gatewayOk) + "\n" +
                Dot(_gameServerOk) + " Game Server    " + State(_gameServerOk) + "\n" +
                (_gameServerOk
                    ? "  Tick Rate      " + _gsTickRate + " Hz\n" +
                      "  Players        " + _gsPlayers + "\n" +
                      "  Enemies        " + _gsEnemies + "\n" +
                      Dot(_gsPostgres == "connected") + " PostgreSQL     " + Svc(_gsPostgres) + "\n" +
                      Dot(_gsRedis == "connected") + " Redis          " + Svc(_gsRedis) + "\n" +
                      "  Uptime         " + FormatUptime(_gsUptimeSeconds)
                    : "  (no data)");
        }

        private async UniTaskVoid PollEconomyAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (string.IsNullOrEmpty(_nakamaSessionToken))
                {
                    await UniTask.Delay(TimeSpan.FromSeconds(2), DelayType.Realtime,
                        PlayerLoopTiming.Update, ct);
                    continue;
                }

                try
                {
                    var url = nakamaBaseUrl + "/v2/account";
                    using (var req = UnityWebRequest.Get(url))
                    {
                        req.timeout = 5;
                        req.SetRequestHeader("Authorization", "Bearer " + _nakamaSessionToken);
                        await req.SendWebRequest().ToUniTask(cancellationToken: ct);
                        if (req.result == UnityWebRequest.Result.Success)
                        {
                            var account = JsonUtility.FromJson<AccountResponse>(req.downloadHandler.text);
                            if (!string.IsNullOrEmpty(account.wallet))
                            {
                                var wallet = JsonUtility.FromJson<WalletData>(account.wallet);
                                _goldServer = wallet.gold;
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"[Economy] Poll failed: {req.responseCode} {req.error}");
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Economy] Poll exception: {ex.Message}");
                }

                await UniTask.Delay(TimeSpan.FromSeconds(5), DelayType.Realtime,
                    PlayerLoopTiming.Update, ct);
            }
        }

        private async UniTaskVoid PollLeaderboardAsync(CancellationToken ct)
        {
            // Wait briefly for session token to be set
            await UniTask.Delay(TimeSpan.FromSeconds(1), DelayType.Realtime,
                PlayerLoopTiming.Update, ct);

            while (!ct.IsCancellationRequested)
            {
                if (string.IsNullOrEmpty(_nakamaSessionToken))
                {
                    Debug.LogWarning("[Leaderboard] No session token yet, skipping poll");
                    await UniTask.Delay(TimeSpan.FromSeconds(3), DelayType.Realtime,
                        PlayerLoopTiming.Update, ct);
                    continue;
                }

                try
                {
                    var url = nakamaBaseUrl + "/v2/leaderboard/" + leaderboardId + "?limit=10";
                    using (var req = UnityWebRequest.Get(url))
                    {
                        req.timeout = 5;
                        req.SetRequestHeader("Authorization", "Bearer " + _nakamaSessionToken);
                        await req.SendWebRequest().ToUniTask(cancellationToken: ct);

                        Debug.Log($"[Leaderboard] Poll response: {req.responseCode} result={req.result}");

                        if (req.result == UnityWebRequest.Result.Success)
                        {
                            var raw = req.downloadHandler.text;
                            Debug.Log($"[Leaderboard] Body: {(raw.Length > 200 ? raw.Substring(0, 200) : raw)}");
                            if (raw != _prevLeaderboardRaw)
                            {
                                _prevLeaderboardRaw = raw;
                                var response = JsonUtility.FromJson<LeaderboardResponse>(raw);
                                _leaderboardRecords = response.records ?? Array.Empty<LeaderboardRecord>();
                                RebuildLeaderboardPanel();
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"[Leaderboard] Poll failed: {req.responseCode} {req.error}");
                            // Show error state but keep panel visible
                            _cachedLeaderboardPanel = "<b>Leaderboard</b>\n\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\n<color=#ff6644>Error " + req.responseCode + "</color>";
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Leaderboard] Poll exception: {ex.Message}");
                    _cachedLeaderboardPanel = "<b>Leaderboard</b>\n\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\n<color=#ff6644>Connection error</color>";
                }

                await UniTask.Delay(TimeSpan.FromSeconds(10), DelayType.Realtime,
                    PlayerLoopTiming.Update, ct);
            }
        }

        private void RebuildLeaderboardPanel()
        {
            if (_leaderboardRecords.Length == 0)
            {
                _cachedLeaderboardPanel = "<b>Leaderboard</b>\n\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\n(no data)";
                return;
            }

            var sb = new System.Text.StringBuilder(256);
            sb.Append("<b>Leaderboard</b>\n\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\n");

            for (int i = 0; i < _leaderboardRecords.Length; i++)
            {
                var rec = _leaderboardRecords[i];
                var name = string.IsNullOrEmpty(rec.username)
                    ? (rec.owner_id != null && rec.owner_id.Length > 8 ? rec.owner_id.Substring(0, 8) : rec.owner_id ?? "???")
                    : rec.username;
                bool isLocal = rec.owner_id == _userId;
                var line = "#" + rec.rank + "  " + name + "  " + rec.score;
                if (isLocal)
                    sb.Append("<color=#33ccff>").Append(line).Append(" \u2190</color>\n");
                else
                    sb.Append(line).Append("\n");
            }

            _cachedLeaderboardPanel = sb.ToString();
        }

        private void EnsureGuiStyles()
        {
            if (_labelStyle != null) return;

            _bgTex = new Texture2D(1, 1);
            _bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.7f));
            _bgTex.Apply();

            _hpBarTex = new Texture2D(1, 1);
            _hpBarTex.SetPixel(0, 0, new Color(0.2f, 0.9f, 0.2f));
            _hpBarTex.Apply();

            _hpBgTex = new Texture2D(1, 1);
            _hpBgTex.SetPixel(0, 0, new Color(0.3f, 0.1f, 0.1f));
            _hpBgTex.Apply();

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white, background = _bgTex },
                padding = new RectOffset(6, 6, 2, 2)
            };

            _localLabelStyle = new GUIStyle(_labelStyle)
            {
                fontSize = 14,
            };

            _fpsStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white, background = _bgTex },
                padding = new RectOffset(8, 8, 4, 4)
            };

            _serverPanelStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = _bgTex, textColor = Color.white },
                alignment = TextAnchor.UpperLeft,
                fontSize = 13,
                richText = true,
                padding = new RectOffset(10, 10, 8, 8)
            };

            _mapButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                fixedHeight = 50
            };
        }

        private Camera GetCamera()
        {
            int frame = Time.frameCount;
            if (_cachedCameraFrame != frame)
            {
                _cachedCamera = Camera.main;
                _cachedCameraFrame = frame;
            }
            return _cachedCamera;
        }

        private void OnGUI()
        {
            EnsureGuiStyles();

            // --- Map selector (pre-connection) ---
            if (!_mapSelected && availableMaps != null && availableMaps.Length > 1)
            {
                const int btnW = 250;
                int totalH = 40 + availableMaps.Length * 60;
                int startX = (Screen.width - btnW) / 2;
                int startY = (Screen.height - totalH) / 2;

                GUI.color = Color.white;
                GUI.Label(new Rect(startX, startY, btnW, 30), "Select Map:", _fpsStyle);
                startY += 40;

                for (int i = 0; i < availableMaps.Length; i++)
                {
                    if (GUI.Button(new Rect(startX, startY + i * 60, btnW, 50),
                        availableMaps[i], _mapButtonStyle))
                    {
                        StartConnection(availableMaps[i]);
                    }
                }

                return; // Don't draw HUD until connected
            }

            // --- HUD panel (top-left) — strings rebuilt only on value change ---
            var y = 10;
            const int h = 22;
            const int w = 400;

            var currentState = _client?.State ?? NetworkClientState.Disconnected;
            if (_prevStatus != _status || _prevState != currentState)
            {
                _prevStatus = _status;
                _prevState = currentState;
                _cachedStatusText = "Status: " + _status;
            }

            GUI.color = currentState == NetworkClientState.InWorld ? Color.green : Color.yellow;
            GUI.Label(new Rect(10, y, w, h), _cachedStatusText);
            y += h;

            GUI.color = Color.white;
            if (_cachedUserText != null)
            {
                GUI.Label(new Rect(10, y, w, h), _cachedUserText);
                y += h;
            }

            if (_prevEntityCount != _entityCount || _prevSnapshotCount != _snapshotCount)
            {
                _prevEntityCount = _entityCount;
                _prevSnapshotCount = _snapshotCount;
                _cachedEntityText = "Entities: " + _entityCount + "  Snapshots: " + _snapshotCount;
            }
            GUI.Label(new Rect(10, y, w, h), _cachedEntityText ?? "Entities: 0  Snapshots: 0");
            y += h;

            if (_client?.Session != null)
            {
                var rttMs = (int)_client.Session.RoundTripMs;
                long tick = _client.World.Tick;
                if (_prevRttMs != rttMs || _prevWorldTick != tick)
                {
                    _prevRttMs = rttMs;
                    _prevWorldTick = tick;
                    _cachedRttText = "RTT: " + rttMs + "ms  Tick: " + tick;
                }
                GUI.Label(new Rect(10, y, w, h), _cachedRttText);
            }

            // --- Combat stats + gold (below network HUD) ---
            if (_cachedCombatText != null)
            {
                GUI.color = new Color(1f, 0.6f, 0.2f);
                GUI.Label(new Rect(10, y, w, h), _cachedCombatText);
                y += h;
                GUI.color = Color.white;
            }

            if (_cachedGoldText != null)
            {
                GUI.color = new Color(1f, 0.85f, 0.2f);
                GUI.Label(new Rect(10, y, w, h), _cachedGoldText);
                y += h;
                GUI.color = Color.white;
            }

            // Map indicator (cached)
            if (_cachedMapText == null)
                _cachedMapText = "Map: " + mapId;
            GUI.Label(new Rect(10, y, w, h), _cachedMapText);
            y += h;

            // Leave room button (visible when connected)
            if (currentState == NetworkClientState.InWorld)
            {
                y += 4;
                GUI.color = new Color(1f, 0.4f, 0.4f);
                if (GUI.Button(new Rect(10, y, 120, 28), "Leave Room"))
                {
                    LeaveRoom();
                }
                GUI.color = Color.white;
                y += 32;
            }

            // --- FPS counter (top-right) — strings rebuilt at 4 Hz in Update ---
            {
                Color fpsColor;
                if (_displayFps >= 50f) fpsColor = Color.green;
                else if (_displayFps >= 30f) fpsColor = Color.yellow;
                else fpsColor = Color.red;

                const int fpsW = 260;
                const int fpsH = 28;
                int fpsX = Screen.width - fpsW - 10;
                int fpsY = 10;

                GUI.color = fpsColor;
                GUI.Label(new Rect(fpsX, fpsY, fpsW, fpsH), _cachedFpsText ?? "FPS: --", _fpsStyle);
                fpsY += fpsH + 2;

                GUI.color = Color.white;
                GUI.Label(new Rect(fpsX, fpsY, fpsW, fpsH), _cachedFpsStatsText ?? "Min: --  Avg: --  Max: --", _fpsStyle);
                fpsY += fpsH + 2;

                if (_client?.Session != null)
                {
                    // Same source as the HUD label above — one session, one round-trip
                    // measurement — but cached against its own previous value.
                    var rttMs = (int)_client.Session.RoundTripMs;
                    if (_cachedFpsRttText == null || _prevFpsRttMs != rttMs)
                    {
                        _prevFpsRttMs = rttMs;
                        _cachedFpsRttText = "RTT: " + rttMs + "ms";
                    }
                    GUI.Label(new Rect(fpsX, fpsY, fpsW, fpsH), _cachedFpsRttText, _fpsStyle);
                }
            }

            // --- Leaderboard panel (bottom-right, always visible) ---
            {
                const int lbW = 240;
                const int lbH = 260;
                int lbX = Screen.width - lbW - 10;
                int lbY = Screen.height - lbH - 10;
                GUI.color = Color.white;
                GUI.Label(new Rect(lbX, lbY, lbW, lbH), _cachedLeaderboardPanel, _serverPanelStyle);
            }

            // --- Server status panel (bottom-left) ---
            {
                RebuildServerPanelIfNeeded();
                if (_cachedServerPanel != null)
                {
                    const int panelW = 280;
                    const int panelH = 220;
                    int panelX = 10;
                    int panelY = Screen.height - panelH - 10;
                    GUI.color = Color.white;
                    GUI.Label(new Rect(panelX, panelY, panelW, panelH), _cachedServerPanel, _serverPanelStyle);
                }
            }

            // --- Floating entity labels ---
            if (_view == null || !_view.IsValid) return;

            var cam = GetCamera();
            if (cam == null) return;

            _view.GetEntityLabels(_labelCache);

            for (int i = 0; i < _labelCache.Count; i++)
            {
                var label = _labelCache[i];

                var worldPos = new Vector3(label.WorldPos.x, label.WorldPos.y + 1.5f, label.WorldPos.z);
                var screenPos = cam.WorldToScreenPoint(worldPos);

                if (screenPos.z < 0) continue;

                float screenY = Screen.height - screenPos.y;

                // Cache label text per entity — only rebuild on first sight
                if (!_entityLabelTextCache.TryGetValue(label.Id, out var displayText))
                {
                    var shortId = label.Id.Length > 8 ? label.Id.Substring(0, 8) : label.Id;
                    displayText = label.IsLocal ? ("\u2605 YOU (" + shortId + ")") : shortId;
                    _entityLabelTextCache[label.Id] = displayText;
                }

                var style = label.IsLocal ? _localLabelStyle : _labelStyle;
                _sharedContent.text = displayText;
                var textSize = style.CalcSize(_sharedContent);
                float labelW = Mathf.Max(textSize.x + 12f, 80f);
                float labelH = textSize.y + 4f;

                var labelRect = new Rect(
                    screenPos.x - labelW * 0.5f,
                    screenY - labelH,
                    labelW,
                    labelH);

                GUI.color = label.Color;
                GUI.Label(labelRect, displayText, style);

                if (label.MaxHp > 0)
                {
                    const float barH = 4f;
                    float barW = labelW;
                    float barX = labelRect.x;
                    float barY = labelRect.yMax + 2f;
                    float hpRatio = Mathf.Clamp01((float)label.Hp / label.MaxHp);

                    GUI.color = Color.white;
                    GUI.DrawTexture(new Rect(barX, barY, barW, barH), _hpBgTex);
                    GUI.DrawTexture(new Rect(barX, barY, barW * hpRatio, barH), _hpBarTex);
                }
            }

            GUI.color = Color.white;
        }
    }
}
