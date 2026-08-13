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

        private void Start()
        {
            Application.runInBackground = true;

            _view = new DOTSEntityView();
            _binder = new WorldViewBinder(_view);

            _cts = new CancellationTokenSource();
            RunAsync(_cts.Token).Forget();
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
                        _prevKills = stats.Kills;
                        _cachedCombatText = "Kills: " + stats.Kills;
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
                _cachedUserText = "User: " + (_userId.Length > 12 ? _userId.Substring(0, 12) : _userId) + "...";
                _status = "Connecting to gateway...";
                Debug.Log($"[DOTSNet] Auth OK, user_id={_userId}");

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

            // --- Combat stats (below network HUD) ---
            if (_cachedCombatText != null)
            {
                GUI.color = new Color(1f, 0.6f, 0.2f);
                GUI.Label(new Rect(10, y, w, h), _cachedCombatText);
                y += h;
                GUI.color = Color.white;
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
                    // Reuse RTT value already computed above
                    var rttMs = (int)_client.Session.RoundTripMs;
                    if (_cachedFpsRttText == null || _prevRttMs != rttMs)
                    {
                        _cachedFpsRttText = "RTT: " + rttMs + "ms";
                    }
                    GUI.Label(new Rect(fpsX, fpsY, fpsW, fpsH), _cachedFpsRttText, _fpsStyle);
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
