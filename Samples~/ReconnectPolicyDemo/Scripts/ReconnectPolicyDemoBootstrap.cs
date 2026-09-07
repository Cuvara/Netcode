using System;
using System.Collections.Generic;
using System.Threading;
using Cuvara.Netcode.Auth;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Connection;
using Cuvara.Netcode.DI;
using Cuvara.Netcode.Transport;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using VContainer;
using VContainer.Unity;

namespace Cuvara.Netcode.Samples.ReconnectPolicyDemo
{
    /// <summary>
    /// The reconnect policy, on screen: a <c>NetworkClient</c> built through
    /// <c>RegisterNetworking()</c> in a VContainer scope, a real session against a live backend,
    /// and three ways to break it — a transport that dies, a heartbeat that starves, and the user
    /// closing — with the client's decision for each shown as it happens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The DI path is the thing under demonstration.</b> Every other sample constructs the
    /// client by hand, which is how <c>RegisterNetworking()</c> shipped for months without anyone
    /// resolving <c>NetworkClient</c> from a scope (fixed in 0.31.1). Here the scope is created in
    /// code (<c>LifetimeScope.Create</c>) so the scene needs no <c>LifetimeScope</c> component,
    /// and the client is <c>Resolve</c>d, never <c>new</c>ed.
    /// </para>
    /// <para>
    /// <b>The demo supplies two dependencies, by two different routes.</b> The
    /// <see cref="ChaosTransportFactory"/> that lets the buttons break a live transport goes in
    /// through <c>RegisterNetworking(transports: …)</c> — the supported route, and the only one
    /// that works: registering <c>ITransportFactory</c> a second time after
    /// <c>RegisterNetworking()</c> makes VContainer fail the <i>whole container build</i> with
    /// <c>Conflict implementation type … FuncInstanceProvider</c>, which is exactly how this
    /// scene died in a player against the live backend on 2026-09-07. The <c>IAuthProvider</c>
    /// is registered separately, after, because <c>RegisterNetworking()</c> does not register
    /// one at all — <c>NetworkClient</c>'s <c>IAuthProvider auth = null</c> constructor default
    /// is not honoured by VContainer, so <c>ConnectAsync(mapId)</c> and every automatic
    /// reconnect need the scope to carry one. A single registration of an interface the package
    /// never registers is safe; a second registration of one it does is not.
    /// </para>
    /// <para>
    /// Log markers are the same the multi-client harness reads from the DOTS sample:
    /// <c>[DOTSNet] Auth OK, user_id=…</c> and <c>[DOTSNet] IN WORLD as …</c>.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ReconnectPolicyDemoBootstrap : MonoBehaviour
    {
        private const string Tag = "[DOTSNet]";
        private const int EventLines = 12;

        [Header("Backend (overridden by -cuvara-* flags / CUVARA_* env)")]
        [SerializeField] private string gatewayHost = "127.0.0.1";
        [SerializeField] private int gatewayPort = 8000;
        [SerializeField] private string mapId = "map_01";

        [Header("Heartbeat")]
        [Tooltip("PingInterval for this demo. Shorter than the 10 s default so a starved heartbeat " +
                 "is declared dead within seconds of pressing the button rather than tens of seconds.")]
        [SerializeField] private float pingIntervalSeconds = 2f;

        [Tooltip("PongTimeout applied when 'Simulate heartbeat timeout' is pressed.")]
        [SerializeField] private float simulatedPongTimeoutSeconds = 3f;

        private Label _backendLine;
        private Label _stateLine;
        private Label _generationLine;
        private Label _attemptLine;
        private Label _budgetLine;
        private Label _causeLine;
        private Label _events;
        private Label _verdict;
        private VisualElement _budgetFill;
        private Button _kill;
        private Button _heartbeat;
        private Button _userClose;
        private Button _connect;

        private BackendCommandLine.Settings _backend;
        private string _deviceId;
        private SampleNakamaAuth _auth;
        private NetworkSettings _settings;
        private ChaosTransportFactory _chaos;
        private LifetimeScope _scope;
        private NetworkClient _client;
        private CancellationTokenSource _cts;
        private readonly List<string> _eventLog = new List<string>();

        private DateTime _reconnectStartedUtc = DateTime.MinValue;
        private DateTime _userClosedUtc = DateTime.MinValue;
        private int _attempt;
        private int _maxAttempts;
        private float _nextDelaySeconds;
        private string _lastCause = "—";
        private bool _connecting;
        private bool _chaosActive;

        private void Awake()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            _backendLine = Require<Label>(root, "backend-line");
            _stateLine = Require<Label>(root, "state-line");
            _generationLine = Require<Label>(root, "generation-line");
            _attemptLine = Require<Label>(root, "attempt-line");
            _budgetLine = Require<Label>(root, "budget-line");
            _causeLine = Require<Label>(root, "cause-line");
            _events = Require<Label>(root, "events");
            _verdict = Require<Label>(root, "verdict");
            _budgetFill = Require<VisualElement>(root, "budget-fill");
            _kill = Require<Button>(root, "kill");
            _heartbeat = Require<Button>(root, "heartbeat");
            _userClose = Require<Button>(root, "user-close");
            _connect = Require<Button>(root, "connect");

            _kill.clicked += KillTransport;
            _heartbeat.clicked += SimulateHeartbeatTimeout;
            _userClose.clicked += UserClose;
            _connect.clicked += ConnectAgain;
        }

        private void Start()
        {
            try
            {
                Boot();
            }
            catch (Exception ex)
            {
                // A scope that does not build leaves _client null and every later frame
                // throwing NullReferenceException out of Update(), which buries the real
                // cause. Say it once, loudly, in the log and on the panel, and stop.
                Debug.LogError($"{Tag} FATAL: the demo scope failed to start: {ex}");
                Fail(ex);
            }
        }

        /// <summary>
        /// Everything <see cref="Start"/> does, so a failure anywhere in it lands in one
        /// catch rather than half-initialising the scene.
        /// </summary>
        private void Boot()
        {
            _backend = BackendCommandLine.Resolve(gatewayHost, gatewayPort, mapId, "http://127.0.0.1:9101/status");
            _deviceId = BackendCommandLine.ResolveDeviceId(_backend, $"reconnect-{(Application.isEditor ? "editor" : "player")}");
            _auth = new SampleNakamaAuth(_backend.NakamaScheme, _backend.NakamaHost, _backend.NakamaPort, _backend.NakamaServerKey);

            _settings = new NetworkSettings
            {
                GatewayHost = _backend.GatewayHost,
                GatewayPort = _backend.GatewayPort,
                PingInterval = TimeSpan.FromSeconds(Mathf.Max(0.5f, pingIntervalSeconds)),
            };
            _chaos = new ChaosTransportFactory(new DefaultTransportFactory());

            // The scope. RegisterNetworking registers NetworkSettings, the log, the codec, the
            // transport factory and NetworkClient — exactly one registration of each, which is
            // why the chaos factory is handed to it as a parameter instead of being registered
            // again afterwards (a second ITransportFactory registration does not override it,
            // it fails the container build). IAuthProvider is the demo's own because the package
            // registers none.
            var device = _deviceId;
            var auth = _auth;
            var chaos = _chaos;
            var settings = _settings;
            _scope = LifetimeScope.Create(builder =>
            {
                builder.RegisterNetworking(settings, transports: chaos);
                builder.Register<IAuthProvider>(
                    _ => new DelegateAuthProvider(ct => auth.GetGatewayTokenAsync(device, ct)),
                    Lifetime.Singleton);
            }, "ReconnectPolicyDemoScope");

            _client = _scope.Container.Resolve<NetworkClient>();
            var resolvedFactory = _scope.Container.Resolve<ITransportFactory>();
            var chaosActive = ReferenceEquals(resolvedFactory, _chaos);
            _chaosActive = chaosActive;
            if (!chaosActive)
            {
                Debug.LogWarning($"{Tag} the scope resolved {resolvedFactory.GetType().Name} rather than the demo's ChaosTransportFactory; the break buttons are disabled.");
            }

            _backendLine.text =
                $"gateway {_backend.GatewayHost}:{_backend.GatewayPort}  nakama {_backend.NakamaBaseUrl}  map {_backend.MapId}\n" +
                $"device {_deviceId}\nclient via RegisterNetworking(): {(_client != null ? "resolved" : "MISSING")}  " +
                $"transport factory: {resolvedFactory.GetType().Name}{(chaosActive ? "" : " (not the demo's)")}";
            Debug.Log($"{Tag} backend gateway={_backend.GatewayHost}:{_backend.GatewayPort} nakama={_backend.NakamaBaseUrl} map={_backend.MapId} device={_deviceId}");

            _client.StateChanged += OnStateChanged;
            _client.SessionClosed += OnSessionClosed;
            _client.GatewayClosed += info => Note($"gateway closed: {info}");
            _client.ReconnectAttemptStarted += OnReconnectAttemptStarted;
            _client.ReconnectProgress += OnReconnectProgress;
            _client.Reconnected += OnReconnected;
            _client.ReconnectFailed += OnReconnectFailed;

            _cts = new CancellationTokenSource();
            ConnectAsync(_cts.Token).Forget();
        }

        /// <summary>
        /// Leaves the panel showing why the demo did not start, with every button dead.
        /// </summary>
        private void Fail(Exception ex)
        {
            _chaosActive = false;
            _client = null;

            // A half-built scope owns whatever it did create; drop it rather than leave it
            // alive behind a dead panel. OnDestroy tolerates the null.
            if (_scope != null)
            {
                _scope.Dispose();
                _scope = null;
            }

            if (_backendLine != null)
            {
                _backendLine.text = "the demo scope did not build — see the log";
            }

            Note($"FATAL: {ex.GetType().Name}: {ex.Message}");
            SetVerdict($"Demo failed to start: {ex.Message}", ok: false);

            _kill?.SetEnabled(false);
            _heartbeat?.SetEnabled(false);
            _userClose?.SetEnabled(false);
            _connect?.SetEnabled(false);
        }

        private async UniTaskVoid ConnectAsync(CancellationToken ct)
        {
            if (_connecting) return;
            _connecting = true;
            try
            {
                Debug.Log($"{Tag} Authenticating device={_deviceId}");
                await _auth.GetGatewayTokenAsync(_deviceId, ct);
                Debug.Log($"{Tag} Auth OK, user_id={_auth.UserId}");
                Note($"auth ok user_id={_auth.UserId}");

                // The provider overload: the registered IAuthProvider answers from its cached
                // gateway JWT, and every automatic reconnect goes through the same provider.
                await _client.ConnectAsync(_backend.MapId, ct);
                Debug.Log($"{Tag} IN WORLD as {_client.UserId}");
                Note($"in world as {_client.UserId} (generation {_client.Generation})");
                SetVerdict("Connected. Press a button to break it.", ok: true);
            }
            catch (OperationCanceledException)
            {
                Debug.Log($"{Tag} Cancelled");
            }
            catch (Exception ex)
            {
                Debug.LogError($"{Tag} FATAL: {ex}");
                Note($"connect failed: {ex.Message}");
                SetVerdict($"Connect failed: {ex.Message}", ok: false);
            }
            finally
            {
                _connecting = false;
            }
        }

        private void Update()
        {
            if (_client == null) return;

            _stateLine.text = $"state {_client.State}  user {(string.IsNullOrEmpty(_client.UserId) ? "—" : _client.UserId)}  " +
                              $"rtt {(_client.Session != null ? _client.Session.RoundTripMs : 0)} ms  reconnecting {_client.IsReconnecting}";
            _generationLine.text = $"operation generation {_client.Generation}   PongTimeout {_settings.PongTimeout.TotalSeconds:0.#} s   PingInterval {_settings.PingInterval.TotalSeconds:0.#} s";

            var budget = (float)_settings.ReconnectBudget.TotalSeconds;
            if (_reconnectStartedUtc != DateTime.MinValue)
            {
                var elapsed = (float)(DateTime.UtcNow - _reconnectStartedUtc).TotalSeconds;
                var fraction = budget > 0f ? Mathf.Clamp01(elapsed / budget) : 0f;
                _budgetFill.style.width = Length.Percent(fraction * 100f);
                _budgetLine.text = $"elapsed {elapsed:0.0} s of the {budget:0} s budget";
                _budgetFill.EnableInClassList("cuvara-probe__bar-fill--hot", fraction > 0.75f);
            }
            else
            {
                _budgetFill.style.width = Length.Percent(0f);
                _budgetLine.text = $"budget {budget:0} s (idle)";
            }

            _attemptLine.text = _maxAttempts > 0
                ? $"attempt {_attempt}/{_maxAttempts}   next in {_nextDelaySeconds:0.0} s"
                : "attempt —";
            _causeLine.text = $"last close: {_lastCause}";

            _kill.SetEnabled(_chaosActive && _client.State == NetworkClientState.InWorld);
            _heartbeat.SetEnabled(_chaosActive && _client.State == NetworkClientState.InWorld);
            _userClose.SetEnabled(_client.State != NetworkClientState.Disconnected && _client.State != NetworkClientState.Ended);
            _connect.SetEnabled(!_connecting && (_client.State == NetworkClientState.Disconnected || _client.State == NetworkClientState.Ended));
        }

        // ---- buttons ----

        private void KillTransport()
        {
            if (_chaos == null) return;
            var killed = _chaos.KillNewest();
            Note(killed
                ? $"KILL: closed the {_chaos.Newest?.Kind} transport under the client — expect PeerClosed/TransportError, then a reconnect"
                : "kill: nothing connected");
            Debug.Log($"{Tag} chaos: transport killed={killed}");
        }

        private void SimulateHeartbeatTimeout()
        {
            if (_chaos == null || _settings == null) return;
            _settings.PongTimeout = TimeSpan.FromSeconds(Mathf.Max(0.5f, simulatedPongTimeoutSeconds));
            _settings.PingInterval = TimeSpan.FromSeconds(1);
            var blackholed = _chaos.BlackholeNewest();
            Note(blackholed
                ? $"HEARTBEAT: PongTimeout={_settings.PongTimeout.TotalSeconds:0.#} s, reads blackholed — expect HeartbeatTimeout within ~{_settings.PongTimeout.TotalSeconds + pingIntervalSeconds:0} s, then a reconnect"
                : "heartbeat: nothing connected");
            Debug.Log($"{Tag} chaos: heartbeat starved={blackholed} pongTimeout={_settings.PongTimeout.TotalSeconds:0.#}s");
        }

        private void UserClose()
        {
            if (_client == null) return;
            _userClosedUtc = DateTime.UtcNow;
            _reconnectStartedUtc = DateTime.MinValue;
            _attempt = 0;
            _maxAttempts = 0;
            _client.Disconnect();
            Note("USER CLOSE: Disconnect() — the policy must NOT reconnect");
            Debug.Log($"{Tag} user close (generation {_client.Generation})");
            SetVerdict("User closed. Watching 5 s for a reconnect that must not happen…", ok: true);
        }

        private void ConnectAgain()
        {
            if (_client == null || _cts == null) return;
            _userClosedUtc = DateTime.MinValue;
            _reconnectStartedUtc = DateTime.MinValue;
            _attempt = 0;
            _maxAttempts = 0;
            _lastCause = "—";
            Note("connect again: a new operation, a new generation");
            ConnectAsync(_cts.Token).Forget();
        }

        // ---- client events ----

        private void OnStateChanged(NetworkClientState state)
        {
            Debug.Log($"{Tag} State -> {state}");
            Note($"state -> {state} (generation {_client.Generation})");

            if (state == NetworkClientState.InWorld && _reconnectStartedUtc != DateTime.MinValue)
            {
                _reconnectStartedUtc = DateTime.MinValue;
            }
        }

        private void OnSessionClosed(DisconnectInfo info)
        {
            _lastCause = info.ToString();
            var decision = ReconnectPolicy.ForSessionClose(info);
            Note($"session closed: {info.Cause} → policy {decision}");
            Debug.Log($"{Tag} Session closed: {info} → {decision}");

            if (decision == ReconnectDecision.Reconnect)
            {
                _reconnectStartedUtc = DateTime.UtcNow;
                _attempt = 0;
                _maxAttempts = _settings.ReconnectAttempts;
            }
            else if (_userClosedUtc != DateTime.MinValue)
            {
                SetVerdict("User close: policy said Never. Correct.", ok: true);
            }
        }

        private void OnReconnectAttemptStarted(int attempt)
        {
            _attempt = attempt;
            if (_userClosedUtc != DateTime.MinValue && (DateTime.UtcNow - _userClosedUtc).TotalSeconds < 5)
            {
                SetVerdict("A reconnect attempt started after a user close — policy violation.", ok: false);
                Debug.LogError($"{Tag} reconnect attempt {attempt} after user close");
            }

            Note($"reconnect attempt {attempt} started");
            Debug.Log($"{Tag} Reconnect attempt {attempt}");
        }

        private void OnReconnectProgress(ReconnectionProgress progress)
        {
            _attempt = progress.Attempt;
            _maxAttempts = progress.MaxAttempts;
            _nextDelaySeconds = progress.DelaySeconds;
            Note($"progress: {progress}");
        }

        private void OnReconnected()
        {
            var elapsed = _reconnectStartedUtc == DateTime.MinValue ? 0.0 : (DateTime.UtcNow - _reconnectStartedUtc).TotalSeconds;
            _reconnectStartedUtc = DateTime.MinValue;
            _maxAttempts = 0;
            Note($"RECONNECTED as {_client.UserId} after {elapsed:0.0} s (generation {_client.Generation})");
            Debug.Log($"{Tag} Reconnected as {_client.UserId} after {elapsed:0.0}s");
            Debug.Log($"{Tag} IN WORLD as {_client.UserId}");
            SetVerdict($"Reconnected in {elapsed:0.0} s. Break it again, or close as the user.", ok: true);
        }

        private void OnReconnectFailed(Exception exception)
        {
            _reconnectStartedUtc = DateTime.MinValue;
            var exhausted = exception as ReconnectExhaustedException;
            var detail = exhausted != null
                ? $"gave up after {exhausted.Attempts} attempt(s), {exhausted.Elapsed.TotalSeconds:0.0} s{(exhausted.Permanent ? ", permanent" : "")}"
                : exception?.Message;
            Note($"RECONNECT FAILED: {detail}");
            Debug.LogWarning($"{Tag} Reconnect gave up: {detail}");
            SetVerdict($"Reconnect failed: {detail}", ok: false);
        }

        // ---- plumbing ----

        private void Note(string line)
        {
            _eventLog.Add($"{DateTime.UtcNow:HH:mm:ss.f}  {line}");
            while (_eventLog.Count > EventLines) _eventLog.RemoveAt(0);
            // Null when Awake itself failed to bind the UXML: the log still gets the line.
            if (_events != null) _events.text = string.Join("\n", _eventLog);
        }

        private void SetVerdict(string text, bool ok)
        {
            if (_verdict == null) return;
            _verdict.text = text;
            _verdict.EnableInClassList("cuvara-probe__verdict--ok", ok);
            _verdict.EnableInClassList("cuvara-probe__verdict--bad", !ok);
        }

        private static T Require<T>(VisualElement root, string name) where T : VisualElement
        {
            var element = root.Q<T>(name);
            if (element == null)
            {
                throw new InvalidOperationException($"ReconnectPolicyDemoView.uxml has no {typeof(T).Name} named '{name}'.");
            }

            return element;
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();

            if (_client != null)
            {
                _client.StateChanged -= OnStateChanged;
                _client.SessionClosed -= OnSessionClosed;
                _client.ReconnectAttemptStarted -= OnReconnectAttemptStarted;
                _client.ReconnectProgress -= OnReconnectProgress;
                _client.Reconnected -= OnReconnected;
                _client.ReconnectFailed -= OnReconnectFailed;
            }

            // The scope owns the client (IDisposable, container-created): disposing it disconnects.
            if (_scope != null) _scope.Dispose();
        }
    }
}
