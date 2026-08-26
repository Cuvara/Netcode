using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Prediction;
using Cuvara.Netcode.Transport;
using Cuvara.Netcode.View;
using Shared.GameLogic.Components;
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
        [Tooltip("Inputs sent per second. Must equal the server's simulation tick rate: " +
                 "the server integrates one step per accepted input at 1/tickRate, and " +
                 "applies only the newest when several land in one tick.")]
        // Defaulted from the shared constant rather than a literal, so this cannot drift
        // from the rate the server actually integrates at. The two are compiled from the
        // same Shared.GameLogic, and a mismatch does not fail — the client is simply
        // wrong by a little on every tick, corrected by every snapshot, which reads to a
        // player as rubber-banding rather than as a misconfiguration.
        //
        // This is a field initializer and it is load-bearing here BECAUSE nothing
        // serializes it: DOTSSceneSetup adds this component at runtime, so the scene
        // carries no DOTSNetworkBridge and no stored value to override it. Author the
        // component into a scene and the serialized number wins instead — at which point
        // this default stops applying and the scene has to be updated too.
        [SerializeField] private int inputRateHz = GameConstants.DefaultTickRate;

        [Tooltip("Take movement from WASD / arrow keys. Off falls back to the scripted " +
                 "sine-wave walk, which is what this sample did before and is still what " +
                 "you want for an unattended soak run.")]
        [SerializeField] private bool useKeyboardInput = false;

        [Header("Prediction")]
        [Tooltip("Fallback movement speed, used before the first snapshot and against a " +
                 "server predating wire.proto field 9. Snapshots now carry per-entity " +
                 "speed and it supersedes this, so a buff or slow is picked up rather " +
                 "than desynced. Should still match the server's " +
                 "ServerDefaults.DefaultPlayerSpeed. Zero disables prediction entirely " +
                 "rather than guessing.")]
        [SerializeField] private float playerSpeed = 5f;

        [Tooltip("Keep the camera over the local player. Off leaves it fixed at the world " +
                 "origin, which is what this sample did before — fine for watching several " +
                 "players at once, wrong the moment you walk anywhere.")]
        [SerializeField] private bool followLocalPlayer = true;

        [Tooltip("How hard the camera pulls toward the local player, per second. Higher is " +
                 "tighter and passes more of the avatar's reconciliation snaps into the view; " +
                 "lower drifts behind during sustained movement.")]
        [SerializeField] private float cameraFollowSharpness = 8f;

        [Header("Run")]
        [Tooltip("Seconds before the client disconnects itself. The end is quiet -- the avatar " +
                 "simply stops and the HUD reads 'Run complete' -- so a value shorter than the " +
                 "session you are actually running looks like a movement or netcode fault.")]
        [SerializeField] private float runSeconds = 3600f;

        private NetworkClient _client;
        private WorldViewBinder _binder;
        private DOTSEntityView _view;
        private LocalMovePredictor _predictor;
        private CancellationTokenSource _cts;
        private long _inputTick;
        private string _pendingAttackTarget = "";

        // Movement sampled on the main thread each frame, consumed by the input loop.
        // Input.GetAxisRaw is main-thread only and the send loop is a UniTask on its own
        // cadence, so the two are deliberately decoupled through these fields.
        private float _moveX;
        private float _moveY;
        private bool _inputFailureReported;
        private bool _tickRateChecked;

        /// <summary>The timestep replay is using, recovered for the cross-check log.</summary>
        private float PredictedDt() =>
            _client != null && _client.TickRate > 0 ? 1f / _client.TickRate : 1f / Mathf.Max(1, inputRateHz);

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
        // Keyed by id, but the locality the text was built with is stored alongside it: the
        // '★ YOU' prefix is derived from IsLocal, so caching on the id alone renders a stale
        // star for any entity whose locality changes under it.
        private readonly Dictionary<string, (bool IsLocal, string Text)> _entityLabelTextCache =
            new Dictionary<string, (bool, string)>();

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

        [Header("Diagnostics")]
        [Tooltip("Per-frame attack bridge counters and per-poll HTTP responses. Off by " +
                 "default: the attack counter alone fired once a second for the whole " +
                 "session, and it only says anything while the attack path or a Nakama " +
                 "endpoint is actually under investigation.")]
        [SerializeField] private bool verboseLogging = false;

        private GUIStyle _serverPanelStyle;
        private GUIStyle _mapButtonStyle;

        // --- Auth ---
        private string _nakamaSessionToken;

        /// <summary>
        /// Kept past the handshake so the session can be renewed. It used to be discarded the
        /// moment the gateway token was in hand, which is why the session token was read once
        /// and then used until it stopped working -- 60 seconds later on a default Nakama, and
        /// silently, because the game connection uses a separate token and keeps running.
        /// </summary>
        private SampleNakamaAuth _nakamaAuth;

        // --- Backend address resolved at startup ---
        // Command line first, then environment, then the field initializers above.
        // See BackendCommandLine; nothing here is read after Start.
        private BackendCommandLine.Settings _backend;

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

            // The backend address is not a constant: the game server is an Agones pod
            // whose port is assigned at scheduling time, and the gateway moves with the
            // cluster. Baking it into a field initializer means a rebuild per target,
            // so it is read from the command line here, once, before anything connects.
            _backend = BackendCommandLine.Resolve(gatewayHost, gatewayPort, mapId, gameServerStatusUrl);
            gatewayHost = _backend.GatewayHost;
            gatewayPort = _backend.GatewayPort;
            if (_backend.NakamaExplicit)
            {
                nakamaBaseUrl = _backend.NakamaBaseUrl;
                nakamaHealthUrl = _backend.NakamaHealthUrl;
            }

            if (_backend.StatusUrlExplicit)
            {
                gameServerStatusUrl = _backend.StatusUrl;
            }

            if (_backend.MapExplicit)
            {
                // Collapse the offered set to the requested map. Two or more entries make
                // Start draw the selector and wait for a click, which is precisely what an
                // unattended launcher cannot supply — the window would sit at a menu and
                // read as "connected nothing".
                mapId = _backend.MapId;
                availableMaps = new[] { _backend.MapId };
            }

            var instanceLabel = string.IsNullOrEmpty(_backend.InstanceLabel)
                ? "1"
                : _backend.InstanceLabel;
            Debug.Log($"[DOTSNet] backend gateway={gatewayHost}:{gatewayPort} " +
                      $"nakama={_backend.NakamaBaseUrl} map={_backend.MapId} " +
                      $"instance={instanceLabel}");

            _view = new DOTSEntityView();

            // Prediction settings are stated, never defaulted. inputRateHz doubles as the
            // server tick rate because the two must match anyway — the server integrates
            // one step per accepted input at 1/tickRate, so a client sending at a
            // different rate predicts a different distance. playerSpeed has to be the
            // server's spawn default (ServerDefaults.DefaultPlayerSpeed); nothing on the
            // wire carries it, which is the weakest joint in this setup and is why the
            // predictor refuses rather than guesses when it is left at zero.
            // Constructed after the join, because the tick rate comes from the server —
            // see StartPrediction. Creating it here with a guessed rate is exactly the
            // defect this release fixes.
            _predictor = null;

            // A predictor that refused is passed anyway: the binder treats a disabled one
            // as no predictor at all, so the fallback is 0.4.0's behaviour rather than a
            // special case anyone has to remember to write.
            _binder = new WorldViewBinder(_view);

            _cts = new CancellationTokenSource();
            // Connection starts when a map is selected (see the OnGUI map selector).
            // With a single map there is nothing to choose, so connect to it directly —
            // taking the id from the list rather than from 'mapId', or configuring one
            // map would silently connect to whatever 'mapId' happened to hold.
            //
            // A map named ON THE COMMAND LINE skips the selector outright. Without this a
            // built player offered more than one map sits on the map-picker forever waiting
            // for a click, which makes it unusable for an automated run against a live
            // server -- the case this sample exists to serve. -cuvara-map is already parsed
            // and carries MapExplicit precisely so a caller can say "this map, no UI".
            if (_backend.MapExplicit)
            {
                StartConnection(_backend.MapId);
            }
            else if (availableMaps == null || availableMaps.Length == 0)
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
            if (_client != null)
            {
                // A second session on one bridge would leave two live clients ticking the
                // same binder, and the older one's entities would never despawn.
                Debug.LogWarning("[DOTSNet] Already connected — leave the room before connecting again.");
                return;
            }

            // Every join authenticates with a fresh device id and therefore gets a fresh
            // Nakama user id, so anything the previous session presented is about to be
            // wrong about which entity is 'you'. Clear it before the first snapshot lands.
            ResetSessionView();

            mapId = selectedMap;
            _cachedMapText = "Map: " + selectedMap;
            _mapSelected = true;
            _status = "Authenticating...";
            RunAsync(_cts.Token).Forget();
        }

        /// <summary>
        /// Drops everything the view and the binder hold for the session that just ended.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Not optional, and not merely tidy.</b> <see cref="WorldViewBinder"/> only calls
        /// <c>Spawn</c> for ids it has not seen, and <c>Spawn</c> is the only place locality is
        /// decided. An entity carried over from the previous session is therefore never
        /// re-evaluated: it keeps the <c>IsLocal</c> it was given when it *was* the local
        /// player, and the next session's own player is spawned local too — two entities
        /// labelled <c>★ YOU</c>, one of them somebody else.
        /// </para>
        /// <para>
        /// The carry-over is real whenever the old entity is still listed in the world when
        /// the new session's first snapshot arrives — the server holds a disconnected
        /// player's entity for ~30 s, so a quick rejoin lands inside that window.
        /// </para>
        /// </remarks>
        private void ResetSessionView()
        {
            _binder?.Reset();
            _tickRateChecked = false;
            _entityLabelTextCache.Clear();
            _labelCache.Clear();
            _entityCount = 0;
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

            // Despawns every presented entity and clears the binder's live set, so the next
            // join starts from an empty world instead of inheriting this one's.
            ResetSessionView();

            // Restart server status polling (doesn't need auth)
            PollServerStatusAsync(_cts.Token).Forget();
        }

        /// <summary>
        /// Builds the predictor once the server has told us its tick rate, and rebinds the
        /// view to it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The tick rate comes from the server, not from a local constant.</b> It is the
        /// cadence the server integrates movement at, so it is the <c>dt</c> prediction must
        /// use — and it was a value shared by convention across two repositories until the
        /// server moved movement to a 60 Hz group while this sample still assumed 15. The
        /// client then predicted four times the distance per input, which at the default
        /// speed is 0.25 world units: <b>under</b> the correction-smoothing threshold, so
        /// it produced no visible snap and simply felt soft and wrong.
        /// </para>
        /// <para>
        /// <c>inputRateHz</c> is deliberately NOT reused for this. It is how often this
        /// client sends, which is a client choice; the integration rate is the server's.
        /// Conflating them is what made the constant look shareable in the first place.
        /// </para>
        /// </remarks>
        private void StartPrediction()
        {
            uint advertised = _client?.TickRate ?? 0u;

            var settings = PredictionSettings.FromServer(
                advertised, fallbackTickRate: inputRateHz, playerSpeed, MapBounds.Default);

            _predictor = new LocalMovePredictor(settings);
            _binder = new WorldViewBinder(_view, _predictor);

            // The protocol permits a fallback only if it is OBSERVABLE. A silent one is
            // behaviourally the code that predated the field.
            if (settings.TickRateIsFallback)
            {
                Debug.LogWarning(
                    $"[DOTSNet] Server advertised no tick rate; predicting at {settings.TickRate}Hz " +
                    "from local configuration. If the server integrates at a different rate, every " +
                    "predicted step is wrong by that ratio — which smooths rather than snaps, so it " +
                    "will feel soft rather than look broken. The measured rate below is the check.");
            }
            else
            {
                Debug.Log($"[DOTSNet] Prediction ON — server tick rate {settings.TickRate}Hz " +
                          $"(advertised), input {inputRateHz}Hz, speed {playerSpeed}");
            }

            if (!_binder.IsPredicting)
            {
                Debug.LogWarning("[DOTSNet] Prediction OFF — settings unusable; rendering server positions");
            }
        }

        /// <summary>
        /// Reads this frame's movement direction into <see cref="_moveX"/> /
        /// <see cref="_moveY"/> for the input loop to pick up.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this sample now has real input at all.</b> It used to send
        /// <c>sin(Time.time * 1.5)</c> / <c>cos(Time.time * 0.8)</c> — an autopilot. That
        /// is the right thing for an unattended soak run and it is kept behind
        /// <see cref="useKeyboardInput"/>, but it makes the question this sample exists to
        /// answer unanswerable: "does moving feel responsive?" has no meaning when nothing
        /// is pressing anything, and keypress-to-visible latency cannot be measured
        /// without a keypress to measure from.
        /// </para>
        /// <para>
        /// Raw axes, not smoothed ones. <c>GetAxis</c> applies its own acceleration curve,
        /// which would put a second, client-only easing in front of a change whose entire
        /// purpose is removing delay — and it would make the vector the client predicts
        /// with differ from the one a player would say they pressed.
        /// </para>
        /// </remarks>
        /// <summary>Angular rate of the scripted walk, radians per second.</summary>
        private const float WalkRadiansPerSecond = 0.6f;

        private float _walkAngle;

        private void SampleMovementInput()
        {
            if (!useKeyboardInput)
            {
                // A CIRCLE, not a constant heading. The diagnostic that stood here sent a
                // constant +x so distance over time would be a straight line; it walks the
                // avatar into the map bound in about a minute and every step after that is
                // clamped to no displacement. The run then looks perfectly smooth while
                // measuring nothing -- held steps read 0/1711 and the prediction path was
                // simply not being exercised.
                //
                // Slow enough that the direction is near-constant between two inputs, so
                // this still tests the held-direction path rather than a new vector every
                // tick, and closed so the avatar stays inside the bounds indefinitely.
                float angle = _walkAngle;
                _walkAngle += WalkRadiansPerSecond * Time.deltaTime;
                _moveX = (float)Math.Cos(angle);
                _moveY = (float)Math.Sin(angle);
                return;
            }

            // Never let an input failure take the bridge down with it. This method is the
            // first statement of Update(), so an exception here stops the connection, the
            // spawn and the render — the sample does not degrade, it dies, and it reads to
            // a user as "nothing works" rather than "input does nothing". That is exactly
            // what happened when this read the legacy API in a project configured for the
            // Input System package.
            try
            {
                ReadKeyboard(out _moveX, out _moveY);
            }
            catch (Exception ex)
            {
                _moveX = 0f;
                _moveY = 0f;

                if (!_inputFailureReported)
                {
                    _inputFailureReported = true;
                    Debug.LogWarning(
                        "[DOTSNet] Keyboard input is unavailable, continuing without it: " +
                        ex.Message + ". The client will still connect and render; the local " +
                        "player just will not move. Set 'useKeyboardInput' false to use the " +
                        "scripted walk instead.");
                }
            }
        }

        /// <summary>
        /// Reads WASD / arrows through whichever input backend this project actually has.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A sample shipped in a package cannot dictate a consumer's Player Settings.</b>
        /// Unity defines <c>ENABLE_INPUT_SYSTEM</c> and <c>ENABLE_LEGACY_INPUT_MANAGER</c>
        /// from the project's active input handling precisely so code can support both, and
        /// under "Input System Package (New)" the legacy <c>UnityEngine.Input</c> class
        /// throws rather than returning zero.
        /// </para>
        /// <para>
        /// The new backend is preferred when both are available, because that is the
        /// configuration a Unity 6 project is most likely to be in and it exercises the
        /// path most consumers will take.
        /// </para>
        /// </remarks>
        private static void ReadKeyboard(out float x, out float y)
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null)
            {
                // No keyboard device — a headless or automated run. Not an error.
                x = 0f;
                y = 0f;
                return;
            }

            x = Axis(keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed,
                     keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed);
            y = Axis(keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed,
                     keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed);
#elif ENABLE_LEGACY_INPUT_MANAGER
            // Raw, not smoothed: GetAxis applies an acceleration curve, which would put a
            // second client-only easing in front of a change whose purpose is removing
            // delay, and would make the predicted vector differ from what the player
            // would say they pressed.
            x = Input.GetAxisRaw("Horizontal");
            y = Input.GetAxisRaw("Vertical");
#else
            // Neither backend is enabled. Nothing to read, and nothing to throw about.
            x = 0f;
            y = 0f;
#endif
        }

#if ENABLE_INPUT_SYSTEM
        private static float Axis(bool positive, bool negative) =>
            (positive ? 1f : 0f) - (negative ? 1f : 0f);
#endif

        /// <summary>
        /// Keeps the camera over the local player.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Without this the camera sits at the world origin for the whole run
        /// (<c>DOTSSceneSetup</c> places it there and nothing moves it), and with
        /// <c>orthographicSize = 12</c> on a 4:3 window that is roughly ±16 units across —
        /// about three seconds of walking at the default speed. Past that your own capsule
        /// leaves the screen while everyone still standing near the origin stays visible,
        /// which reads exactly like "my player disappeared when I moved" and sends the reader
        /// looking for a rendering or netcode fault that is not there.
        /// </para>
        /// <para>
        /// <b>LateUpdate, and damped.</b> LateUpdate so the camera reads positions the frame's
        /// simulation has already written rather than trailing them by a frame.
        /// </para>
        /// <para>
        /// <b>The damping is not a cosmetic choice, and an exact follow was tried first.</b>
        /// The local avatar is predicted, and reconciliation hard-snaps it whenever the
        /// correction exceeds <c>LocalMovePredictor.SmoothingThreshold</c>. A camera locked
        /// rigidly to a position that snaps moves the ENTIRE view on every snap, so every
        /// remote player and every enemy on screen jumps with it. Measured against this
        /// server: walking produced snaps at roughly the input rate, and with a rigid follow
        /// the result was reported as "other players and enemies are jerky and sometimes
        /// disappear" — an artefact of the camera, laid over entities that were being
        /// interpolated correctly. Damping decouples the view from the avatar's corrections.
        /// </para>
        /// <para>
        /// The trade is real and worth stating: damping adds easing of its own, so this is
        /// the wrong setting for judging how smooth remote motion is. Turn
        /// <see cref="followLocalPlayer"/> off for that — a still camera adds nothing at all.
        /// </para>
        /// <para>
        /// Judging REMOTE smoothness is still easier with <see cref="followLocalPlayer"/> off
        /// and the local player left standing still, because then nothing on screen is moving
        /// for a reason other than the thing under observation.
        /// </para>
        /// </remarks>
        private void LateUpdate()
        {
            if (!followLocalPlayer || _view == null || !_view.IsValid) return;

            var cam = GetCamera();
            if (cam == null) return;

            _view.GetEntityLabels(_labelCache);
            for (int i = 0; i < _labelCache.Count; i++)
            {
                if (!_labelCache[i].IsLocal) continue;

                // Height and rotation are left alone: the camera is top-down orthographic and
                // only its ground-plane position should track the player.
                var pos = cam.transform.position;
                var target = new Vector3(_labelCache[i].WorldPos.x, pos.y, _labelCache[i].WorldPos.z);

                // Exponential smoothing, framerate-independent. A plain Lerp with a constant
                // factor eases faster on a 300fps machine than a 60fps one, which would make
                // the sample look different on different hardware for no reason.
                float k = 1f - Mathf.Exp(-cameraFollowSharpness * Time.unscaledDeltaTime);
                cam.transform.position = Vector3.Lerp(pos, target, k);
                return;
            }
        }

        private void Update()
        {
            SampleMovementInput();

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

                if (verboseLogging)
                {
                    _attackDebugTimer -= Time.deltaTime;
                    if (_attackDebugTimer <= 0f)
                    {
                        _attackDebugTimer = 1f;
                        Debug.Log($"[Debug] AttackRequest count: {attackCount}, pending: '{_pendingAttackTarget}'");
                    }
                }

                if (attackCount > 0)
                {
                    var entities = _attackRequestQuery.ToEntityArray(Allocator.Temp);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        var req = dotsWorld.EntityManager.GetComponentData<AttackRequest>(entities[i]);
                        var targetId = req.TargetId.ToString();
                        if (verboseLogging)
                            Debug.Log($"[Debug] AttackRequest consumed: '{targetId}'");
                        if (!string.IsNullOrEmpty(targetId))
                            _pendingAttackTarget = targetId;
                        dotsWorld.EntityManager.DestroyEntity(entities[i]);
                    }
                    entities.Dispose();
                }
            }

            _healthFrames++;

            // The binder steers the prediction clock toward the server's tick and needs the
            // journey time to know how far ahead of the newest snapshot "now" is. Only the
            // session measures it.
            _binder.RoundTripMs = _client.Session?.RoundTripMs ?? 0L;

            _binder.Tick(_client.World, _client.UserId);

            // Per frame, and separately from the snapshot pass above. Snapshot processing
            // advances prediction once per arriving snapshot — the world rate — so a
            // client that renders only from it shows the avatar still between snapshots
            // and jumping on the frame one lands, however fast it is drawing. This is what
            // makes the smoothing observable rather than merely computed.
            _binder.AdvanceFrame(Time.deltaTime);
            _entityCount = _view.Count;

            // Verify the advertised rate against one measured off the wire. The protocol
            // recommends this even when a rate IS advertised, and the reason is that a
            // wrong rate produces no symptom a player can name — it is wrong by a fixed
            // ratio on every input, under the correction threshold, forever.
            if (!_tickRateChecked && _binder.TickRate.HasEstimate)
            {
                _tickRateChecked = true;
                int used = _predictor != null ? (int)Mathf.Round(1f / Mathf.Max(1e-6f, PredictedDt())) : 0;

                if (_binder.TickRate.Disagrees(used))
                {
                    Debug.LogError(
                        $"[DOTSNet] Tick rate mismatch: predicting at {used}Hz but the server's " +
                        $"snapshots measure {_binder.TickRate.EstimatedHz:F1}Hz. Every predicted step " +
                        "is wrong by that ratio. This is the failure that reads as soft, laggy " +
                        "movement rather than as an error.");
                }
                else
                {
                    Debug.Log($"[DOTSNet] Tick rate verified: predicting at {used}Hz, " +
                              $"measured {_binder.TickRate.EstimatedHz:F1}Hz off the wire.");
                }
            }

            LogPredictionHealth();
        }

        /// <summary>
        /// Prints the counters that describe how the local player's motion actually behaved,
        /// once every <see cref="PredictionHealthSeconds"/>.
        /// </summary>
        /// <remarks>
        /// The counters that matter are cheap and already exposed; what was missing was
        /// anything printing them from a running player. "It still feels jerky" and
        /// "Snaps=0, corrections=0" cannot both be acted on without this line, and the
        /// smoothness defects this package has shipped all left every counter clean because
        /// the SIMULATED position was right and only the rendered one was wrong -- so the
        /// render figures are printed next to the reconcile ones deliberately.
        /// </remarks>
        private const float PredictionHealthSeconds = 5f;

        private float _nextPredictionHealthAt;
        private int _lastSnaps;
        private int _lastSmoothed;
        private int _lastReconciles;
        private float _healthWindowStart;

        // Two clocks over the same window. The netcode reads Stopwatch (StopwatchViewClock);
        // this health line reads Time.realtimeSinceStartup. A snapshot rate that looks low
        // is either frames that did not arrive or a second that is not a second, and only
        // measuring both separates them.
        private readonly System.Diagnostics.Stopwatch _healthStopwatch =
            System.Diagnostics.Stopwatch.StartNew();
        private double _healthStopwatchMark;

        /// <summary>Stopwatch reading at the first health line, so totals can be divided by it.</summary>
        private double _firstHealthAt = -1;
        private int _healthFrames;
        private int _snapshotsApplied;
        private int _lastSnapshotsApplied;
        private long _lastFramesRx;

        private void LogPredictionHealth()
        {
            if (_predictor == null || Time.realtimeSinceStartup < _nextPredictionHealthAt)
            {
                return;
            }

            _nextPredictionHealthAt = Time.realtimeSinceStartup + PredictionHealthSeconds;

            float window = Mathf.Max(1e-3f, Time.realtimeSinceStartup - _healthWindowStart);
            _healthWindowStart = Time.realtimeSinceStartup;

            double swNow = _healthStopwatch.Elapsed.TotalSeconds;
            if (_firstHealthAt < 0) _firstHealthAt = swNow;
            double swWindow = swNow - _healthStopwatchMark;
            _healthStopwatchMark = swNow;
            float fps = _healthFrames / window;
            float appliedPerSec = (_snapshotsApplied - _lastSnapshotsApplied) / window;
            long framesRx = _client.Session?.FramesReceived ?? 0L;
            float framesPerSec = (framesRx - _lastFramesRx) / window;
            _lastFramesRx = framesRx;
            _healthFrames = 0;
            _lastSnapshotsApplied = _snapshotsApplied;

            int snaps = _predictor.Snaps - _lastSnaps;
            int smoothed = _predictor.SmoothedCorrections - _lastSmoothed;
            int reconciles = _predictor.Reconciles - _lastReconciles;
            _lastSnaps = _predictor.Snaps;
            _lastSmoothed = _predictor.SmoothedCorrections;
            _lastReconciles = _predictor.Reconciles;

            float step = _predictor.LastCorrection / Mathf.Max(1e-6f, playerSpeed * PredictedDt());

            Debug.Log(
                $"[DOTSNet/health] reconciles={reconciles} smoothed={smoothed} snaps={snaps} " +
                $"lastCorrection={_predictor.LastCorrection:F4} ({step:F2} steps) " +
                $"span={_predictor.EffectiveSmoothingSpan * 1000f:F1}ms " +
                $"stepInterval={_predictor.ObservedStepInterval * 1000f:F1}ms " +
                $"held={_predictor.HeldStepsApplied}/{_predictor.BaseTicksAdvanced} " +
                $"skipExpired={_predictor.SkipExpired} coalesced={_predictor.CoalescedInputs} " +
                $"clientTick={_predictor.BaseTick} serverTick={_binder.LastServerTick} " +
                $"lead={_predictor.BaseTick - _binder.LastServerTick}t " +
                $"fps={fps:F0} snapshotsApplied={appliedPerSec:F1}/s " +
                $"clamped={_predictor.ClampedFrames} discarded={_predictor.DiscardedCatchUpSeconds:F2}s " +
                $"framesRx={framesPerSec:F1}/s rtt={_client.Session?.RoundTripMs ?? 0}ms " +
                $"tickError={_predictor.TickError}t resyncs={_predictor.HardResyncs} " +
                $"pending={_predictor.PendingCount} rejected={_predictor.RejectedInputs} " +
                $"dropped={_predictor.DroppedInputs} nothingHeld={_predictor.SkipNothingHeld} " +
                $"alreadyStepped={_predictor.SkipInputAlreadyStepped} " +
                $"enabled={_predictor.IsEnabled} inputTick={_inputTick} " +
                $"noDisp={_predictor.SkipNoDisplacement} refused={_predictor.SkipRefusedByMovementModel} " +
                $"histHit={_predictor.HistoryHits} histMiss={_predictor.HistoryMisses} " +
                $"staleness={_binder.Staleness.StalenessTicks:F2}t " +
                $"skew={_binder.Staleness.SkewPpm:F0}ppm " +
                $"baseline={_binder.Staleness.BaselineSeconds:F0}s " +
                $"unityWin={window:F3}s swWin={swWindow:F3}s " +
                // Absolute totals as well as per-window rates. A per-window rate is a
                // difference of two counters over a measured interval, and every one of
                // those three can be wrong on its own; a total divided by the time since the
                // first health line cannot be. When the two disagree the bookkeeping is the
                // suspect, not the network.
                $"rxTotal={_client.Session?.FramesReceived ?? 0L} " +
                $"sinceFirst={_healthStopwatch.Elapsed.TotalSeconds - _firstHealthAt:F1}s " +
                $"clockRatio={(swWindow > 0 ? window / swWindow : 0):F4} " +
                $"fits={_binder.Staleness.Fits} " +
                $"stSamples={_binder.Staleness.Samples} " +
                // A refused fit is otherwise invisible: SkewPpm reads 0 without one, which is
                // exactly what two clocks that agree look like. That is how a bound set at
                // 0.90/1.10 disabled the measurement for a whole session and said nothing.
                $"refusedFits={_binder.Staleness.FitsRefused} " +
                $"refusedSkew={_binder.Staleness.RefusedSkewPpm:F0}ppm");
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
                // Per-process identity. Two instances sharing one Nakama user is the
                // failure that looks like success: the second login evicts the first and
                // what is left is a single client in an empty world — indistinguishable
                // from a broken area-of-interest.
                var device = BackendCommandLine.ResolveDeviceId(
                    _backend, $"dots-{(Application.isEditor ? "editor" : "player")}");
                _status = "Authenticating...";
                Debug.Log($"[DOTSNet] Authenticating device={device}");

                var auth = new SampleNakamaAuth(
                    _backend.NakamaScheme, _backend.NakamaHost, _backend.NakamaPort,
                    _backend.NakamaServerKey);
                var jwt = await auth.GetGatewayTokenAsync(device, ct);
                _userId = auth.UserId;
                _nakamaAuth = auth;
                _nakamaSessionToken = auth.SessionToken;
                _cachedUserText = "User: " + (_userId.Length > 12 ? _userId.Substring(0, 12) : _userId) + "..." +
                                  (string.IsNullOrEmpty(_backend.InstanceLabel)
                                      ? string.Empty
                                      : "  [#" + _backend.InstanceLabel + "]");
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
                    _snapshotsApplied++;
                    _binder.NoteRemovedIds(s.Removed);
                };

                _client.SessionClosed += info =>
                {
                    _status = $"Disconnected: {info}";
                    Debug.Log($"[DOTSNet] Session closed: {info}");
                };

                await _client.ConnectAsync(jwt, mapId, ct);
                StartPrediction();
                _status = "In World";
                Debug.Log($"[DOTSNet] IN WORLD as {_client.UserId}");

                var dt = 1f / Mathf.Max(1f, inputRateHz);
                var started = DateTime.UtcNow;

                while (!ct.IsCancellationRequested)
                {
                    var elapsed = (float)(DateTime.UtcNow - started).TotalSeconds;
                    if (elapsed >= runSeconds) break;

                    _inputTick++;

                    var moveX = _moveX;
                    var moveY = _moveY;
                    var attackTarget = _pendingAttackTarget;
                    _pendingAttackTarget = "";
                    if (!string.IsNullOrEmpty(attackTarget))
                        Debug.Log($"[Attack] Sending attack on {attackTarget} (tick {_inputTick})");

                    _client.Session?.SendInput(_inputTick, moveX, moveY, attackTarget);

                    // Recorded immediately after the send, with the same tick and the same
                    // vector — the predictor's whole contract is that it saw exactly what
                    // the server will see. Only the movement half is predicted;
                    // attackTarget is not passed and combat stays server-authoritative.
                    _predictor?.RecordInput(_inputTick, moveX, moveY);

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

            var backoff = new PollBackoff("ServerStatus", statusPollInterval);

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

                // Both URLs answer from the same local backend stack, so they are one
                // health signal here: with neither of them up the panel has nothing new
                // to draw, and asking every statusPollInterval only fills the log of
                // whichever layer times out first.
                if (_nakamaOk || _gameServerOk)
                    backoff.OnSuccess();
                else
                    backoff.OnFailure("Nakama and game server both unreachable");

                await UniTask.Delay(TimeSpan.FromSeconds(backoff.IntervalSeconds),
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

        // Cadence and log volume for one background poll loop. Motivated by a backend
        // whose leaderboard had never been created: every poll answered 404, so the
        // loop produced one warning every 10s and kept asking at full rate for the
        // entire session. Failures widen the interval instead, and only the edges
        // (first failure, recovery) reach the log.
        private sealed class PollBackoff
        {
            private const float MaxIntervalSeconds = 60f;

            private readonly string _tag;
            private readonly float _baseInterval;
            private float _interval;
            private int _consecutiveFailures;

            public PollBackoff(string tag, float baseInterval)
            {
                _tag = tag;
                _baseInterval = baseInterval;
                _interval = baseInterval;
            }

            public float IntervalSeconds => _interval;

            public void OnSuccess()
            {
                if (_consecutiveFailures > 0)
                {
                    Debug.Log($"[{_tag}] Recovered after {_consecutiveFailures} failed poll(s)");
                    _consecutiveFailures = 0;
                }

                _interval = _baseInterval;
            }

            public void OnFailure(string reason)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures == 1)
                {
                    Debug.LogWarning(
                        $"[{_tag}] Poll failing: {reason} — backing off, silent until it recovers");
                }

                _interval = Mathf.Min(_interval * 2f, MaxIntervalSeconds);
            }
        }

        private async UniTaskVoid PollEconomyAsync(CancellationToken ct)
        {
            var backoff = new PollBackoff("Economy", 5f);

            while (!ct.IsCancellationRequested)
            {
                // Renewed before the request rather than after a failure: a refresh on 401
                // still costs one user-visible request per expiry, and on a default Nakama an
                // expiry happens every minute.
                await RenewNakamaSessionAsync(ct);

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

                            backoff.OnSuccess();
                        }
                        else
                        {
                            backoff.OnFailure($"{req.responseCode} {req.error}");
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    backoff.OnFailure(ex.Message);
                }

                await UniTask.Delay(TimeSpan.FromSeconds(backoff.IntervalSeconds), DelayType.Realtime,
                    PlayerLoopTiming.Update, ct);
            }
        }

        private async UniTaskVoid PollLeaderboardAsync(CancellationToken ct)
        {
            // Wait briefly for session token to be set
            await UniTask.Delay(TimeSpan.FromSeconds(1), DelayType.Realtime,
                PlayerLoopTiming.Update, ct);

            var backoff = new PollBackoff("Leaderboard", 10f);

            while (!ct.IsCancellationRequested)
            {
                // Renewed before the request rather than after a failure: a refresh on 401
                // still costs one user-visible request per expiry, and on a default Nakama an
                // expiry happens every minute.
                await RenewNakamaSessionAsync(ct);

                if (string.IsNullOrEmpty(_nakamaSessionToken))
                {
                    if (verboseLogging)
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

                        if (verboseLogging)
                            Debug.Log($"[Leaderboard] Poll response: {req.responseCode} result={req.result}");

                        if (req.result == UnityWebRequest.Result.Success)
                        {
                            var raw = req.downloadHandler.text;
                            if (verboseLogging)
                                Debug.Log($"[Leaderboard] Body: {(raw.Length > 200 ? raw.Substring(0, 200) : raw)}");
                            if (raw != _prevLeaderboardRaw)
                            {
                                _prevLeaderboardRaw = raw;
                                var response = JsonUtility.FromJson<LeaderboardResponse>(raw);
                                _leaderboardRecords = response.records ?? Array.Empty<LeaderboardRecord>();
                                RebuildLeaderboardPanel();
                            }

                            backoff.OnSuccess();
                        }
                        else
                        {
                            backoff.OnFailure($"{req.responseCode} {req.error}");
                            // Show error state but keep panel visible
                            _cachedLeaderboardPanel = "<b>Leaderboard</b>\n\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\n<color=#ff6644>Error " + req.responseCode + "</color>";
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    backoff.OnFailure(ex.Message);
                    _cachedLeaderboardPanel = "<b>Leaderboard</b>\n\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\n<color=#ff6644>Connection error</color>";
                }

                await UniTask.Delay(TimeSpan.FromSeconds(backoff.IntervalSeconds), DelayType.Realtime,
                    PlayerLoopTiming.Update, ct);
            }
        }

        /// <summary>
        /// Keep the Nakama session usable, and report the two ways it can be kept.
        /// </summary>
        /// <remarks>
        /// Cheap when there is nothing to do -- the auth object compares two timestamps. It is
        /// called before each authenticated poll rather than in a timer of its own, so a poll
        /// that is backed off does not keep renewing a session nothing is using.
        /// </remarks>
        private async UniTask RenewNakamaSessionAsync(CancellationToken ct)
        {
            if (_nakamaAuth == null) return;

            int refreshes = _nakamaAuth.Refreshes;
            int reauths = _nakamaAuth.Reauthentications;

            try
            {
                await _nakamaAuth.EnsureSessionAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogWarning("[NakamaAuth] Could not renew the session: " + ex.Message);
                return;
            }

            if (_nakamaAuth.Refreshes != refreshes || _nakamaAuth.Reauthentications != reauths)
            {
                _nakamaSessionToken = _nakamaAuth.SessionToken;

                // Logged on the change, not on every renewal check: a session being renewed on
                // schedule is not news, and a client re-authenticating instead of refreshing
                // is -- it means the refresh token expired too, which is an hour by default.
                if (_nakamaAuth.Reauthentications != reauths)
                    Debug.Log("[NakamaAuth] Re-authenticated from the device id " +
                              $"(refresh unavailable or rejected); total {_nakamaAuth.Reauthentications}");
                else if (verboseLogging)
                    Debug.Log($"[NakamaAuth] Session refreshed; total {_nakamaAuth.Refreshes}");
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

            // --- Prediction (below RTT) ---
            // Deliberately shown even when prediction is OFF. A silently-absent predictor
            // looks exactly like a working one that is doing nothing, and this sample
            // exists so behaviour can be looked at instead of assumed.
            if (_predictor != null)
            {
                y += h;
                if (_binder != null && _binder.IsPredicting)
                {
                    // Snaps is the number worth watching: a steady climb means the client
                    // and the server disagree about speed, tick rate or bounds.
                    GUI.color = _predictor.Snaps > 0 ? new Color(1f, 0.8f, 0.2f) : new Color(0.4f, 1f, 0.6f);
                    GUI.Label(new Rect(10, y, w, h),
                        "Predict: " + _predictor.PendingCount + " pending  err "
                        + _predictor.LastCorrection.ToString("F3") + "  snaps "
                        + _predictor.Snaps);
                }
                else
                {
                    GUI.color = new Color(1f, 0.5f, 0.5f);
                    GUI.Label(new Rect(10, y, w, h), "Predict: OFF (settings unusable)");
                }
                GUI.color = Color.white;
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

                // Cache label text per entity — rebuilt on first sight, and again when locality changes
                if (!_entityLabelTextCache.TryGetValue(label.Id, out var cached)
                    || cached.IsLocal != label.IsLocal)
                {
                    var shortId = label.Id.Length > 8 ? label.Id.Substring(0, 8) : label.Id;
                    cached = (label.IsLocal,
                        label.IsLocal ? ("\u2605 YOU (" + shortId + ")") : shortId);
                    _entityLabelTextCache[label.Id] = cached;
                }

                var displayText = cached.Text;

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
