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
using UnityEngine;

namespace Samples.NetcodeE2E
{
    /// <summary>
    /// ONE client, reporting what it can see. Two copies of this in two separate OS
    /// processes — a player build and the Editor — are the authoritative test of remote
    /// visibility, because they share no memory at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The in-process two-client harness is a proxy for this: it exercises two
    /// connections, but a pass could in principle come from shared process state rather
    /// than from the peer's entity genuinely arriving over the wire. This shape removes
    /// that doubt by construction.
    /// </para>
    /// <para>
    /// Results are written to a FILE in the temp directory as well as logged, because a
    /// player build's console is not readable from the outside and parsing Player.log is
    /// fragile. The file is the evidence channel.
    /// </para>
    /// <para>
    /// PlayerPrefs is shared between a player build and the Editor under the same
    /// company/product, so a cached Nakama session could be restored by BOTH processes,
    /// making them the same user and turning duplicate-login eviction into what looks
    /// like a visibility failure. This never calls the auth provider or
    /// <c>RestoreSessionAsync</c> — it authenticates with an explicit, per-process,
    /// per-run device id.
    /// </para>
    /// </remarks>
    public sealed class SoloVisibilityProbe : MonoBehaviour
    {
        [SerializeField] private string gatewayHost = "127.0.0.1";
        [SerializeField] private int gatewayPort = 8000;
        [SerializeField] private string mapId = "map_01";
        [SerializeField] private float runSeconds = 75f;
        [SerializeField] private int inputRateHz = 15;

        private NetworkClient _client;
        private CancellationTokenSource _cts;
        private string _role;
        private string _reportPath;
        private DateTime? _peerSeenAt;

        private void Start()
        {
            Application.runInBackground = true;

            // Distinguishes the two processes without any coordination between them.
            _role = Application.isEditor ? "editor" : "player";
            _reportPath = Path.Combine(Path.GetTempPath(), $"netcode-2proc-{_role}.txt");

            _cts = new CancellationTokenSource();
            RunAsync(_cts.Token).Forget();
        }

        private async UniTaskVoid RunAsync(CancellationToken ct)
        {
            var log = new StringBuilder();
            void Line(string s)
            {
                var stamped = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + s;
                log.AppendLine(stamped);
                Debug.Log($"[SOLO:{_role}] {s}");
                try { File.WriteAllText(_reportPath, log.ToString()); } catch { /* evidence only */ }
            }

            try
            {
                Line($"role={_role} isEditor={Application.isEditor} pid-ish={Time.frameCount}");

                var device = $"solo-{_role}-{DateTime.UtcNow.Ticks}";
                Line($"deviceId={device}");

                // Package-local auth over plain HTTP: no Nakama SDK, nothing outside
                // this package, so the sample compiles for an external consumer.
                var auth = new SampleNakamaAuth();
                var jwt = await auth.GetGatewayTokenAsync(device, ct);
                Line($"USER_ID={auth.UserId}");

                _client = new NetworkClient(
                    new NetworkSettings { GatewayHost = gatewayHost, GatewayPort = gatewayPort },
                    new DefaultTransportFactory(), new ProtobufWireCodec(), new UnityNetLog());

                await _client.ConnectAsync(jwt, mapId, ct);
                Line($"IN_WORLD as {_client.UserId} on map '{mapId}' via Protobuf");

                var dt = 1f / Math.Max(1, inputRateHz);
                var started = DateTime.UtcNow;
                long tick = 0;
                var lastSample = DateTime.MinValue;
                var peak = 0;

                while (!ct.IsCancellationRequested)
                {
                    var elapsed = (float)(DateTime.UtcNow - started).TotalSeconds;
                    if (elapsed >= runSeconds) break;

                    tick++;

                    // BOUNDED OSCILLATION, not a straight line. Two processes cannot
                    // be started at the same instant, and with a constant heading that
                    // start offset becomes distance: a 78 s offset put one client at
                    // x=353 while the other was still at x=0, far outside the 50-unit
                    // AOI, and they never saw each other despite both being correct.
                    // Oscillating keeps each client within ~3 units of spawn forever, so
                    // visibility no longer depends on launching them simultaneously,
                    // while the position still CHANGES over time so "the peer is being
                    // updated" remains observable.
                    // HOLD POSITION until a peer is actually in view, then move.
                    // Two processes cannot join simultaneously — a 17 s gap was measured,
                    // and 78 s in an earlier attempt. Any client that moves during that
                    // window is already displaced when the second one joins: at ~5 u/s a
                    // 17 s gap is ~85 units, past the 50-unit AOI, so the pair never sees
                    // each other and it looks like a visibility failure. Waiting for the
                    // peer removes launch timing from the experiment entirely.
                    var peerPresent = _client.World.Count >= 2;
                    if (peerPresent && !_peerSeenAt.HasValue)
                    {
                        _peerSeenAt = DateTime.UtcNow;
                        Line($"PEER_VISIBLE at t={elapsed:F0}s — releasing hold, moving from here");
                    }

                    var moveX = _peerSeenAt.HasValue
                        ? Mathf.Sin((float)(DateTime.UtcNow - _peerSeenAt.Value).TotalSeconds * 1.5f)
                        : 0f;
                    _client.Session?.SendInput(tick, moveX, 0f);

                    if ((DateTime.UtcNow - lastSample).TotalSeconds >= 5)
                    {
                        lastSample = DateTime.UtcNow;
                        peak = Math.Max(peak, _client.World.Count);
                        Line($"t={elapsed:F0}s count={_client.World.Count} peak={peak} " +
                             $"world={DumpWorld()}");
                    }

                    try
                    {
                        await UniTask.Delay(TimeSpan.FromSeconds(dt), DelayType.Realtime,
                            PlayerLoopTiming.Update, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                // Durations must be measured from the SECOND join, not the local one.
                // The two processes join ~17-29 s apart; holding position removes the
                // DISTANCE that skew causes but not the skew itself, so a local in-world
                // figure overstates the two-player window by exactly that gap. This
                // clock starts when the peer actually appeared.
                var overlap = _peerSeenAt.HasValue
                    ? (DateTime.UtcNow - _peerSeenAt.Value).TotalSeconds
                    : 0d;
                // UPPER BOUND, not the overlap. Measuring from peer appearance fixes the
                // join-skew inflation at the START, but the END cannot be seen locally:
                // a departed peer's entity is HELD for 30 s and keeps appearing, so a
                // client that outlives its peer goes on counting a co-presence that has
                // already ended. Measured: the client that left first reported 62.8 s
                // against a true 63.0 s, while the one that outlived it by 12 s reported
                // 74.9 s — inflated by exactly that tail. The real figure is the MINIMUM
                // across both clients, i.e. second join to first exit, which needs both
                // clients' data. This is the held-entity ghost showing up in a metric.
                Line($"LOCAL_PEER_PRESENT_FOR={overlap:F1}s (upper bound on the two-player " +
                     $"overlap: clocked from peer appearance so join skew is excluded, but a " +
                     $"held entity hides the peer's departure — take the MIN across both clients)");
                Line($"FINAL count={_client.World.Count} peak={peak} world={DumpWorld()}");
                Line($"SELF={_client.UserId}");
                Line("DONE");

                _client.Disconnect();
            }
            catch (OperationCanceledException)
            {
                Line("CANCELLED");
            }
            catch (Exception ex)
            {
                Line($"FATAL {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Full ids, not truncated — the peer's id is the assertion.</summary>
        private string DumpWorld()
        {
            var sb = new StringBuilder();
            foreach (var kv in _client.World.Entities)
            {
                sb.Append($"{{{kv.Key} x={kv.Value.X:F2} y={kv.Value.Y:F2} hp={kv.Value.Hp}}} ");
            }

            return sb.Length == 0 ? "(empty)" : sb.ToString().TrimEnd();
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _client?.Disconnect();
            _client?.Dispose();
            _client = null;
        }
    }
}
