using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Transport;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Cuvara.Netcode.Samples.PartyDungeonProbe
{
    /// <summary>
    /// Dungeon entry (ADR-26) run against a gateway this scene starts itself, reporting what
    /// the client actually put on the wire — no backend, no Nakama, no configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this scene exists.</b> The backend half of dungeon instancing was proven with a
    /// Go probe that speaks the protocol directly. That proves a server and proves nothing
    /// about whether the shipped client can reach it — and for a while it could not, because
    /// the client's wire had no <c>party_id</c> at all. This scene closes that gap by asking
    /// the real <see cref="NetworkClient"/> to do it and reading the bytes it sent.
    /// </para>
    /// <para>
    /// <b>The case that matters is the reconnect.</b> A dungeon player who drops must come back
    /// into the SAME instance. If the reconnect forgets the party id it asks for a map named
    /// after the dungeon content: that map does not exist, so the rejoin fails — and if such a
    /// map ever did exist, the player would silently reappear in the open world while their
    /// party carried on without them. Nothing in a log distinguishes those two outcomes from a
    /// flaky network, which is exactly why it is checked here rather than assumed.
    /// </para>
    /// <para>
    /// <b>What this does not prove.</b> That a party exists, that Nakama's party RPCs work, or
    /// that your gateway allocates a dungeon. Membership is checked by the gateway against
    /// Nakama, and this scene has neither. It proves the client asks correctly.
    /// </para>
    /// </remarks>
    public sealed class PartyDungeonProbe : MonoBehaviour
    {
        private const string Content = "dungeon_01";
        private const string Party = "8f14e45fceea167a5a36dedd4bea2543";
        private const string Map = "map_01";

        private Label _listenersLine;
        private Label _lastCaseLine;
        private Label _outcomeLine;
        private Label _verdict;
        private ScrollView _log;

        private TcpListener _gateway;
        private int _gatewayPort;
        private CancellationTokenSource _life;
        private bool _busy;

        /// <summary>Every EnterWorld request this scene's fake gateway received, in order.</summary>
        private readonly List<(string MapId, string PartyId)> _seen = new List<(string, string)>();

        private void Start()
        {
            var document = GetComponent<UIDocument>();
            if (document == null || document.rootVisualElement == null)
            {
                Debug.LogError("PartyDungeonProbe needs a UIDocument with PartyDungeonProbeView.uxml assigned.");
                enabled = false;
                return;
            }

            VisualElement root = document.rootVisualElement;
            _listenersLine = root.Q<Label>("listeners-line");
            _lastCaseLine = root.Q<Label>("last-case");
            _outcomeLine = root.Q<Label>("outcome-line");
            _verdict = root.Q<Label>("verdict");
            _log = root.Q<ScrollView>("log");

            root.Q<Button>("map").clicked += () => Run(CaseMapEntrySendsNoParty);
            root.Q<Button>("dungeon").clicked += () => Run(CaseDungeonEntryCarriesTheParty);
            root.Q<Button>("empty-party").clicked += () => Run(CaseEmptyPartyIsRefusedLocally);
            root.Q<Button>("reconnect").clicked += () => Run(CaseReconnectReturnsToTheSameInstance);
            root.Q<Button>("all").clicked += () => Run(RunEveryCase);

            _life = new CancellationTokenSource();

            try
            {
                _gateway = new TcpListener(IPAddress.Loopback, 0);
                _gateway.Start();
                _gatewayPort = ((IPEndPoint)_gateway.LocalEndpoint).Port;
                _listenersLine.text = $"fake gateway on 127.0.0.1:{_gatewayPort}";
            }
            catch (Exception ex)
            {
                Append($"SETUP FAILED: {ex.GetType().Name}: {ex.Message}");
                _verdict.text = "setup failed — no case below has run";
                enabled = false;
                return;
            }

            _verdict.text = "ready — pick a case";
        }

        private void OnDestroy()
        {
            _life?.Cancel();
            _life?.Dispose();
            try { _gateway?.Stop(); } catch (Exception) { /* shutting down */ }
        }

        // ---- the fake gateway ----------------------------------------------------------

        /// <summary>
        /// Answers auth and enter_world for <paramref name="connections"/> dials, recording
        /// every EnterWorld it saw. It answers JSON regardless of what the client sent, which
        /// is enough: this scene reads the REQUEST, and the response only has to let the
        /// client proceed far enough to send one.
        /// </summary>
        private async UniTask ServeGatewayAsync(int connections, CancellationToken ct)
        {
            for (var i = 0; i < connections; i++)
            {
                TcpClient peer = null;
                try
                {
                    peer = await _gateway.AcceptTcpClientAsync().AsUniTask().AttachExternalCancellation(ct);
                    var stream = peer.GetStream();

                    while (!ct.IsCancellationRequested)
                    {
                        byte[] body = await ReadFrameAsync(stream, ct);
                        if (body == null) break;

                        string json = Encoding.UTF8.GetString(body);
                        int type = ReadInt(json, "\"type\":");

                        if (type == (int)MsgType.Auth)
                        {
                            await WriteAsync(stream, "{\"type\":2,\"payload\":{\"ok\":true,\"user_id\":\"probe\"}}", ct);
                            continue;
                        }

                        if (type == (int)MsgType.EnterWorld)
                        {
                            _seen.Add((ReadString(json, "map_id"), ReadString(json, "party_id")));
                            // Refuse the assignment: this scene is about the REQUEST, and a
                            // refusal ends the attempt without needing a fake game server.
                            await WriteAsync(stream,
                                "{\"type\":4,\"payload\":{\"error\":\"probe: request recorded\"}}", ct);
                            continue;
                        }
                    }
                }
                catch (Exception)
                {
                    // A client that hangs up mid-case lands here; the assertions below read
                    // what was recorded, not whether the socket closed politely.
                }
                finally
                {
                    peer?.Close();
                }
            }
        }

        private static async UniTask<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
        {
            var header = new byte[WireFraming.HeaderSize];
            if (!await ReadExactAsync(stream, header, ct)) return null;

            int length = WireFraming.ReadLength(header);
            if (length <= 0 || length > 1 << 20) return null;

            var body = new byte[length];
            return await ReadExactAsync(stream, body, ct) ? body : null;
        }

        private static async UniTask<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer, read, buffer.Length - read, ct).AsUniTask();
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        private static async UniTask WriteAsync(NetworkStream stream, string json, CancellationToken ct)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            var frame = new byte[WireFraming.HeaderSize + body.Length];
            WireFraming.WriteLength(frame, body.Length);
            Buffer.BlockCopy(body, 0, frame, WireFraming.HeaderSize, body.Length);
            await stream.WriteAsync(frame, 0, frame.Length, ct).AsUniTask();
        }

        private static int ReadInt(string json, string marker)
        {
            int at = json.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return -1;
            int start = at + marker.Length, end = start;
            while (end < json.Length && char.IsDigit(json[end])) end++;
            return end > start ? int.Parse(json.Substring(start, end - start)) : -1;
        }

        /// <summary>Returns the field's value, or null when the field is ABSENT.</summary>
        /// <remarks>
        /// Absent and empty must stay distinguishable here: omitting <c>party_id</c> on a map
        /// entry is the behaviour under test, and a reader that turned absence into <c>""</c>
        /// would report a passing case for a client that sent the field every time.
        /// </remarks>
        private static string ReadString(string json, string field)
        {
            string marker = "\"" + field + "\":\"";
            int at = json.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return null;
            int start = at + marker.Length;
            int end = json.IndexOf('"', start);
            return end < 0 ? null : json.Substring(start, end - start);
        }

        // ---- the cases -----------------------------------------------------------------

        private NetworkClient NewClient() => new NetworkClient(
            new NetworkSettings
            {
                GatewayHost = "127.0.0.1",
                GatewayPort = _gatewayPort,
                JoinAttempts = 1,
                ReconnectAttempts = 1,
            },
            new TcpTransportFactory(),
            new JsonWireCodec(),
            new UnityNetworkLog());

        private async UniTask CaseMapEntrySendsNoParty(CancellationToken ct)
        {
            Case("map entry omits party_id");
            _seen.Clear();

            ServeGatewayAsync(1, ct).Forget();
            var client = NewClient();
            try { await client.ConnectAsync("jwt", Map, ct); }
            catch (Exception) { /* the fake gateway refuses the assignment on purpose */ }
            finally { await client.DisposeAsync(); }

            if (_seen.Count != 1) { Fail($"the gateway saw {_seen.Count} enter_world requests, wanted 1"); return; }
            if (_seen[0].PartyId != null)
            {
                Fail($"a map entry sent party_id=\"{_seen[0].PartyId}\" — it must be OMITTED, not empty");
                return;
            }
            Pass($"map_id=\"{_seen[0].MapId}\", no party_id on the wire");
        }

        private async UniTask CaseDungeonEntryCarriesTheParty(CancellationToken ct)
        {
            Case("dungeon entry carries party_id");
            _seen.Clear();

            ServeGatewayAsync(1, ct).Forget();
            var client = NewClient();
            try { await client.ConnectToDungeonAsync(Content, Party, ct); }
            catch (Exception) { /* assignment refused on purpose */ }
            finally { await client.DisposeAsync(); }

            if (_seen.Count != 1) { Fail($"the gateway saw {_seen.Count} enter_world requests, wanted 1"); return; }
            if (_seen[0].PartyId != Party) { Fail($"party_id was \"{_seen[0].PartyId}\", wanted \"{Party}\""); return; }
            if (_seen[0].MapId != Content) { Fail($"map_id was \"{_seen[0].MapId}\", wanted \"{Content}\""); return; }
            Pass($"map_id=\"{Content}\", party_id=\"{Party}\"");
        }

        private UniTask CaseEmptyPartyIsRefusedLocally(CancellationToken ct)
        {
            Case("an empty party id is refused before any socket is opened");
            _seen.Clear();

            var client = NewClient();
            try
            {
                client.ConnectToDungeonAsync(Content, "", ct).Forget();
                Fail("an empty party id was accepted — it must throw rather than fall back to a map entry");
            }
            catch (ArgumentException ex)
            {
                Pass($"refused locally: {ex.Message}");
            }
            catch (Exception ex)
            {
                Fail($"wrong exception: {ex.GetType().Name}: {ex.Message}");
            }

            return UniTask.CompletedTask;
        }

        private async UniTask CaseReconnectReturnsToTheSameInstance(CancellationToken ct)
        {
            Case("a reconnect re-enters the SAME instance");
            _seen.Clear();

            // Two dials: the first attempt and the reconnect the refusal provokes.
            ServeGatewayAsync(2, ct).Forget();

            var client = NewClient();
            try { await client.ConnectToDungeonAsync(Content, Party, ct); }
            catch (Exception) { /* expected */ }

            // Drive one more attempt the way a dropped session would.
            try { await client.ConnectToDungeonAsync(Content, Party, ct); }
            catch (Exception) { /* expected */ }
            finally { await client.DisposeAsync(); }

            if (_seen.Count < 2) { Fail($"only {_seen.Count} enter_world requests reached the gateway, wanted 2"); return; }
            for (var i = 0; i < _seen.Count; i++)
            {
                if (_seen[i].PartyId != Party)
                {
                    Fail($"request #{i + 1} carried party_id=\"{_seen[i].PartyId}\" — a rejoin that forgets the " +
                         "party asks for a map named after the dungeon, and the player leaves their party behind");
                    return;
                }
            }
            Pass($"{_seen.Count} requests, every one carrying party_id=\"{Party}\"");
        }

        private async UniTask RunEveryCase(CancellationToken ct)
        {
            Append("── running every case ──");
            await CaseMapEntrySendsNoParty(ct);
            await CaseDungeonEntryCarriesTheParty(ct);
            await CaseEmptyPartyIsRefusedLocally(ct);
            await CaseReconnectReturnsToTheSameInstance(ct);
            Append("── done ──");
        }

        // ---- reporting -----------------------------------------------------------------

        private void Run(Func<CancellationToken, UniTask> body)
        {
            if (_busy) { Append("a case is already running"); return; }
            RunAsync(body).Forget();
        }

        private async UniTaskVoid RunAsync(Func<CancellationToken, UniTask> body)
        {
            _busy = true;
            try { await body(_life.Token); }
            catch (Exception ex) { Fail($"{ex.GetType().Name}: {ex.Message}"); }
            finally { _busy = false; }
        }

        private void Case(string what)
        {
            _lastCaseLine.text = what;
            _outcomeLine.text = "running…";
            Append($"▶ {what}");
        }

        private void Pass(string detail)
        {
            _outcomeLine.text = "PASS — " + detail;
            _verdict.text = "PASS";
            Append($"  PASS  {detail}");
        }

        private void Fail(string detail)
        {
            _outcomeLine.text = "FAIL — " + detail;
            _verdict.text = "FAIL";
            Append($"  FAIL  {detail}");
        }

        private void Append(string line)
        {
            _log?.Add(new Label(line));
            Debug.Log("[PartyDungeonProbe] " + line);
        }
    }
}
