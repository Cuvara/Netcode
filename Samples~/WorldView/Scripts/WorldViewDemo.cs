using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Json;
using Cuvara.Netcode.Transport;
using Cuvara.Netcode.View;
using UnityEngine;

namespace Samples.WorldView
{
    /// <summary>
    /// Connects one client and RENDERS the world, so two of these — a player build and
    /// the Editor — can be looked at rather than read about.
    /// </summary>
    /// <remarks>
    /// Everything the netcode side needs is three calls on <see cref="WorldViewBinder"/>:
    /// construct with a view, <c>Tick</c> each frame, and optionally
    /// <c>NoteRemovedIds</c> from a snapshot handler. Swapping the GameObject view for a
    /// DOTS one later touches nothing in this file except the constructor argument.
    /// </remarks>
    public sealed class WorldViewDemo : MonoBehaviour
    {
        [Header("Gateway")]
        [SerializeField] private string gatewayHost = "127.0.0.1";
        [SerializeField] private int gatewayPort = 8000;
        [SerializeField] private string mapId = "map_01";

        [Header("Run")]
        [SerializeField] private float runSeconds = 75f;
        [SerializeField] private int inputRateHz = 15;

        [Tooltip("Seconds after the peer appears before the screenshot is taken, so both " +
                 "capsules are on screen and have moved.")]
        [SerializeField] private float screenshotAfterPeerSeconds = 6f;

        private NetworkClient _client;
        private WorldViewBinder _binder;
        private GameObjectEntityView _view;
        private CancellationTokenSource _cts;
        private string _role;
        private string _reportPath;
        private DateTime? _peerSeenAt;
        private bool _shotTaken;

        private void Start()
        {
            Application.runInBackground = true;

            _role = Application.isEditor ? "editor" : "player";
            _reportPath = Path.Combine(Path.GetTempPath(), $"netcode-view-{_role}.txt");

            SetUpCamera();

            _view = new GameObjectEntityView();
            _binder = new WorldViewBinder(_view);

            _cts = new CancellationTokenSource();
            RunAsync(_cts.Token).Forget();
        }

        /// <summary>
        /// Top-down camera so the server's 2D plane is what the screenshot shows. Built in
        /// code so the sample scene needs nothing configured by hand.
        /// </summary>
        private void SetUpCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = go.AddComponent<Camera>();
            }

            cam.transform.position = new Vector3(0f, 40f, 0f);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            cam.orthographic = true;
            cam.orthographicSize = 14f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.09f, 0.11f, 0.15f);
        }

        /// <summary>
        /// Reconcile every frame. The world is already merged, so this is a cheap
        /// dictionary walk over two entities.
        /// </summary>
        private void Update()
        {
            if (_client == null)
            {
                return;
            }

            _binder.Tick(_client.World, _client.UserId);
        }

        private async UniTaskVoid RunAsync(CancellationToken ct)
        {
            var log = new StringBuilder();
            void Line(string s)
            {
                log.AppendLine(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + s);
                Debug.Log($"[VIEW:{_role}] {s}");
                try { File.WriteAllText(_reportPath, log.ToString()); } catch { }
            }

            try
            {
                var device = $"view-{_role}-{DateTime.UtcNow.Ticks}";
                Line($"role={_role} device={device}");

                // Package-local auth over plain HTTP: no Nakama SDK, nothing outside
                // this package, so the sample compiles for an external consumer.
                var auth = new SampleNakamaAuth();
                var jwt = await auth.GetGatewayTokenAsync(device, ct);
                Line($"USER_ID={auth.UserId}");

                _client = new NetworkClient(
                    new NetworkSettings { GatewayHost = gatewayHost, GatewayPort = gatewayPort },
                    new DefaultTransportFactory(), new ProtobufWireCodec(), new UnityNetLog());

                // Diagnostics only: lets the binder attribute a despawn to an explicit
                // `removed` id rather than to the entity merely ceasing to be listed.
                _client.SnapshotReceived += s => _binder.NoteRemovedIds(s.Removed);

                await _client.ConnectAsync(jwt, mapId, ct);
                Line($"IN_WORLD as {_client.UserId} (local view is the GREEN, larger capsule)");

                var dt = 1f / Math.Max(1, inputRateHz);
                var started = DateTime.UtcNow;
                long tick = 0;
                var lastLog = DateTime.MinValue;

                while (!ct.IsCancellationRequested)
                {
                    var elapsed = (float)(DateTime.UtcNow - started).TotalSeconds;
                    if (elapsed >= runSeconds) break;

                    tick++;

                    // Hold until a peer is visible, then oscillate: keeps both clients
                    // inside the 50-unit AOI regardless of how far apart the two
                    // processes started. See SoloVisibilityProbe for the measurements.
                    if (_client.World.Count >= 2 && !_peerSeenAt.HasValue)
                    {
                        _peerSeenAt = DateTime.UtcNow;
                        Line($"PEER_VISIBLE at t={elapsed:F0}s — views={_view.Count}");
                    }

                    // Opposite PHASE per process, not a different direction. Both stay
                    // bounded near spawn, but they travel opposite ways at any instant, so
                    // they visibly separate and re-converge — relative motion is the thing
                    // worth being able to see, and identical phase left them overlapping.
                    // Phase, unlike a heading difference, cannot accumulate into distance.
                    var phase = Application.isEditor ? 0f : Mathf.PI;
                    var moveX = _peerSeenAt.HasValue
                        ? Mathf.Sin((float)(DateTime.UtcNow - _peerSeenAt.Value).TotalSeconds * 1.5f + phase)
                        : 0f;
                    _client.Session?.SendInput(tick, moveX, 0f);

                    if (_peerSeenAt.HasValue && !_shotTaken &&
                        (DateTime.UtcNow - _peerSeenAt.Value).TotalSeconds >= screenshotAfterPeerSeconds)
                    {
                        _shotTaken = true;
                        var shot = Path.Combine(Path.GetTempPath(), $"netcode-view-{_role}.png");
                        var ok = CaptureCameraPng(shot);
                        Line($"SCREENSHOT ok={ok} -> {shot} (views={_view.Count} world={_client.World.Count})");
                    }

                    if ((DateTime.UtcNow - lastLog).TotalSeconds >= 10)
                    {
                        lastLog = DateTime.UtcNow;
                        Line($"t={elapsed:F0}s world={_client.World.Count} views={_view.Count} " +
                             $"live={_binder.LiveCount} despawn(removed)={_binder.DespawnsFromRemoval} " +
                             $"despawn(absent)={_binder.DespawnsFromAbsence}");
                    }

                    try
                    {
                        await UniTask.Delay(TimeSpan.FromSeconds(dt), DelayType.Realtime,
                            PlayerLoopTiming.Update, ct);
                    }
                    catch (OperationCanceledException) { break; }
                }

                Line($"FINAL world={_client.World.Count} views={_view.Count} " +
                     $"despawn(removed)={_binder.DespawnsFromRemoval} " +
                     $"despawn(absent)={_binder.DespawnsFromAbsence}");
                Line("DONE");
                _client.Disconnect();
            }
            catch (OperationCanceledException) { Line("CANCELLED"); }
            catch (Exception ex) { Line($"FATAL {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>
        /// Renders the camera to a RenderTexture and writes the PNG directly.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT <c>ScreenCapture.CaptureScreenshot</c>. That depends on a
        /// presenting surface, so in the Editor it silently wrote nothing while happily
        /// reporting success in a log line — the player build produced a file and the
        /// Editor did not, from identical code. Rendering explicitly works the same way in
        /// both processes and fails loudly, which is what a piece of test evidence has to
        /// do.
        /// </remarks>
        private static bool CaptureCameraPng(string path)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                return false;
            }

            RenderTexture rt = null;
            Texture2D tex = null;
            var previousTarget = cam.targetTexture;
            var previousActive = RenderTexture.active;

            try
            {
                rt = new RenderTexture(900, 700, 24);
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();

                File.WriteAllBytes(path, tex.EncodeToPNG());
                return File.Exists(path);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VIEW] screenshot failed: {ex.Message}");
                return false;
            }
            finally
            {
                cam.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (tex != null) Destroy(tex);
                if (rt != null) { rt.Release(); Destroy(rt); }
            }
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _binder?.Reset();
            _view?.Clear();
            _client?.Disconnect();
            _client?.Dispose();
            _client = null;
        }
    }
}
