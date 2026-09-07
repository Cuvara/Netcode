using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;
using Cuvara.Netcode.Auth;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Connection;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The recovery policy and the operation-generation guard of
    /// <see cref="NetworkClient"/>, driven through a scripted backend: which closes
    /// come back, which stay down, what the budget does, and that a cancelled or
    /// superseded connect can never flip state or leak a socket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every await in these flows completes synchronously — scripted transports
    /// answer from queues, the delay seam is a no-op — so nothing here waits on the
    /// editor tick and nothing here flakes in headless CI. The [UnityTest] wait
    /// loops are wall-clock deadlines that are never reached on a passing run.
    /// </para>
    /// <para>
    /// Companion to <c>NetworkClientRetryTests</c>, which pins the join-retry loop
    /// and the original server_shutdown reconnect; this file pins what 0.31.0 added.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class NetworkClientRecoveryTests
    {
        // ── scripted backend ─────────────────────────────────────────────

        private sealed class RespondingTransport : ITransport
        {
            private readonly Func<byte, List<byte[]>> _respond;
            private readonly Queue<byte[]> _inbound = new Queue<byte[]>();
            private UniTaskCompletionSource<byte[]> _parked;

            public RespondingTransport(Func<byte, List<byte[]>> respond) => _respond = respond;

            public string RemoteEndPoint => "scripted";
            public bool IsConnected => !Closed;
            public bool Closed { get; private set; }
            public bool Disposed { get; private set; }
            public readonly List<byte> Written = new List<byte>();

            /// <summary>When set, the responder's answers are held until <see cref="Release"/>.</summary>
            public bool Hold;
            private readonly Queue<byte[]> _held = new Queue<byte[]>();

            public UniTask ConnectAsync(string host, int port, CancellationToken ct) => UniTask.CompletedTask;

            public UniTask<byte[]> ReadFrameAsync(CancellationToken ct)
            {
                if (_inbound.Count > 0)
                {
                    return UniTask.FromResult(_inbound.Dequeue());
                }

                _parked = new UniTaskCompletionSource<byte[]>();
                var parked = _parked;
                ct.Register(() => parked.TrySetCanceled(ct));
                return parked.Task;
            }

            public UniTask WriteFrameAsync(byte[] body, CancellationToken ct)
            {
                var type = ReadType(body);
                Written.Add(type);
                var responses = _respond(type);
                if (responses != null)
                {
                    foreach (var r in responses)
                    {
                        if (Hold) _held.Enqueue(r); else Deliver(r);
                    }
                }

                return UniTask.CompletedTask;
            }

            /// <summary>Lets held answers through — the "slow server" finally replies.</summary>
            public void Release()
            {
                Hold = false;
                while (_held.Count > 0) Deliver(_held.Dequeue());
            }

            /// <summary>Push a server-initiated frame (a disconnect, a kick).</summary>
            public void Deliver(byte[] frame)
            {
                var parked = _parked;
                if (parked != null)
                {
                    _parked = null;
                    parked.TrySetResult(frame);
                }
                else
                {
                    _inbound.Enqueue(frame);
                }
            }

            /// <summary>The peer half-closes: the parked read returns a clean EOF.</summary>
            public void Eof() => Deliver(null);

            /// <summary>The socket dies: the parked read throws.</summary>
            public void Fail(Exception ex)
            {
                var parked = _parked;
                _parked = null;
                parked?.TrySetException(ex);
            }

            public void Close() => Closed = true;
            public void Dispose() { Closed = true; Disposed = true; }

            private static byte ReadType(byte[] body)
            {
                var json = Encoding.UTF8.GetString(body);
                var marker = "\"type\":";
                var at = json.IndexOf(marker, StringComparison.Ordinal);
                Assert.That(at, Is.GreaterThanOrEqualTo(0), "client frame carries no type: " + json);
                var start = at + marker.Length;
                var end = start;
                while (end < json.Length && char.IsDigit(json[end])) end++;
                return byte.Parse(json.Substring(start, end - start));
            }
        }

        private sealed class ScriptedFactory : ITransportFactory
        {
            private readonly List<Func<RespondingTransport>> _script = new List<Func<RespondingTransport>>();
            public readonly List<RespondingTransport> Created = new List<RespondingTransport>();

            public ScriptedFactory Then(Func<byte, List<byte[]>> responder)
            {
                _script.Add(() => new RespondingTransport(responder));
                return this;
            }

            public ITransport Create(TransportKind kind)
            {
                Assert.That(Created.Count, Is.LessThan(_script.Count),
                    $"the flow dialed more connections ({Created.Count + 1}) than the script expected ({_script.Count})");
                var t = _script[Created.Count]();
                Created.Add(t);
                return t;
            }
        }

        private static byte[] Frame(MsgType type, string payloadJson) =>
            Encoding.UTF8.GetBytes("{\"type\":" + (int)type + ",\"payload\":" + payloadJson + "}");

        private static byte[] AuthOk() => Frame(MsgType.AuthResp, "{\"ok\":true,\"user_id\":\"u1\"}");
        private static byte[] AuthErr(string error) => Frame(MsgType.AuthResp, "{\"ok\":false,\"error\":\"" + error + "\"}");
        private static byte[] AssignOk() => Frame(MsgType.EnterWorldResp,
            "{\"server_addr\":\"127.0.0.1:9000\",\"join_token\":\"tok\",\"transport\":\"tcp\"}");
        private static byte[] JoinOk() => Frame(MsgType.JoinTokenResp, "{\"ok\":true,\"user_id\":\"u1\",\"tick_rate\":60}");
        private static byte[] Kick(string reason) => Frame(MsgType.Kick, "{\"reason\":\"" + reason + "\"}");
        private static byte[] Bye(string reason) => Frame(MsgType.Disconnect, "{\"reason\":\"" + reason + "\"}");

        private static Func<byte, List<byte[]>> GatewayOk() => type =>
        {
            switch ((MsgType)type)
            {
                case MsgType.Auth: return new List<byte[]> { AuthOk() };
                case MsgType.EnterWorld: return new List<byte[]> { AssignOk() };
                default: return null;
            }
        };

        private static Func<byte, List<byte[]>> GatewayAuthRefuses(string error, Action onAuth = null) => type =>
        {
            if ((MsgType)type != MsgType.Auth) return null;
            onAuth?.Invoke();
            return new List<byte[]> { AuthErr(error) };
        };

        private static Func<byte, List<byte[]>> GameServerJoinOk() => type =>
            (MsgType)type == MsgType.JoinToken ? new List<byte[]> { JoinOk() } : null;

        private sealed class FakeClock
        {
            public long NowMs;
            public long Read() => NowMs;
        }

        private static NetworkSettings FastSettings(FakeClock clock = null) => new NetworkSettings
        {
            JoinAttempts = 1,
            JoinRetryDelay = TimeSpan.Zero,
            RetryJitter = TimeSpan.Zero,
            ReconnectDelay = TimeSpan.FromSeconds(1),
            ReconnectMaxDelay = TimeSpan.FromSeconds(8),
            ReconnectBudget = TimeSpan.FromSeconds(25),
            ReconnectAttempts = 8,
            DelayScheduler = (delay, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return UniTask.CompletedTask;
            },
            // Heartbeats never fire: the loops park on this forever, so a test
            // never depends on the editor tick.
            HeartbeatScheduler = (delay, ct) => new UniTaskCompletionSource().Task,
            MonotonicClock = clock != null ? (Func<long>)clock.Read : MonotonicClockDefault.NowMs,
        };

        private sealed class RecordingLog : INetLog
        {
            public readonly List<string> Lines = new List<string>();
            public void Info(string message) { lock (Lines) Lines.Add("I " + message); }
            public void Warn(string message) { lock (Lines) Lines.Add("W " + message); }
            public void Error(string message, Exception exception = null)
            {
                lock (Lines) Lines.Add("E " + message + (exception == null ? "" : " :: " + exception.Message));
            }
            public override string ToString() { lock (Lines) return string.Join("\n", Lines); }
        }

        private sealed class CountingAuth : IAuthProvider
        {
            public int Calls;
            public UniTask<string> GetJwtAsync(CancellationToken ct)
            {
                Calls++;
                return UniTask.FromResult("jwt" + Calls);
            }
        }

        private sealed class Probe
        {
            public readonly List<int> Attempts = new List<int>();
            public readonly List<ReconnectionProgress> Progress = new List<ReconnectionProgress>();
            public readonly List<ConnectionStateChangedEvent> Transitions = new List<ConnectionStateChangedEvent>();
            public readonly List<DisconnectInfo> SessionCloses = new List<DisconnectInfo>();
            public readonly List<DisconnectInfo> GatewayCloses = new List<DisconnectInfo>();
            public int Reconnected;
            public Exception Failed;

            public Probe(NetworkClient client)
            {
                client.ReconnectAttemptStarted += a => Attempts.Add(a);
                client.ReconnectProgress += p => Progress.Add(p);
                client.ConnectionStateChanged += e => Transitions.Add(e);
                client.SessionClosed += i => SessionCloses.Add(i);
                client.GatewayClosed += i => GatewayCloses.Add(i);
                client.Reconnected += () => Reconnected++;
                client.ReconnectFailed += ex => Failed = ex;
            }
        }

        private static DateTime Deadline(double seconds = 10) => DateTime.UtcNow.AddSeconds(seconds);

        private static async UniTask<Exception> Connect(NetworkClient client, string mapId, CancellationToken ct = default)
        {
            try
            {
                await client.ConnectAsync(mapId, ct);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        /// <summary>Gateway #1 + game server #1, landed in world.</summary>
        private static IEnumerator LandInWorld(NetworkClient client, RecordingLog log)
        {
            Exception failure = null;
            var done = false;
            Connect(client, "map_01").ContinueWith(ex => { done = true; failure = ex; }).Forget();
            for (var t = Deadline(); !done && DateTime.UtcNow < t;) yield return null;
            Assert.That(done, Is.True, "initial connect never finished\n" + log);
            Assert.That(failure, Is.Null, failure?.ToString() + "\n" + log);
            Assert.That(client.State, Is.EqualTo(NetworkClientState.InWorld));
        }

        // ── which closes come back ───────────────────────────────────────

        [UnityTest]
        public IEnumerator PeerClosed_ReconnectsAtOnce_ThroughAFreshCredential()
        {
            var factory = new ScriptedFactory()
                .Then(GatewayOk()).Then(GameServerJoinOk())   // first login
                .Then(GatewayOk()).Then(GameServerJoinOk());  // the reconnect
            var auth = new CountingAuth();
            var log = new RecordingLog();
            var client = new NetworkClient(FastSettings(), factory, new JsonWireCodec(), log, auth);
            var probe = new Probe(client);

            yield return LandInWorld(client, log);
            var firstSession = client.Session;

            // NAT expiry, Wi-Fi hand-off: the socket just ends.
            factory.Created[1].Eof();

            for (var t = Deadline(); probe.Reconnected == 0 && probe.Failed == null && DateTime.UtcNow < t;)
                yield return null;

            Assert.That(probe.Failed, Is.Null, probe.Failed?.ToString() + "\n" + log);
            Assert.That(probe.Reconnected, Is.EqualTo(1), log.ToString());
            Assert.That(probe.SessionCloses.Count, Is.EqualTo(1));
            Assert.That(probe.SessionCloses[0].Cause, Is.EqualTo(DisconnectCause.PeerClosed));
            Assert.That(probe.Attempts, Is.EqualTo(new[] { 1 }), "one healthy round suffices");
            Assert.That(probe.Progress[0].DelaySeconds, Is.EqualTo(0f), "a plain drop retries immediately");
            Assert.That(auth.Calls, Is.EqualTo(2), "the reconnect must refresh the credential: the join token was spent");
            Assert.That(factory.Created.Count, Is.EqualTo(4), "gateway + game server, twice");
            Assert.That(client.State, Is.EqualTo(NetworkClientState.InWorld));
            Assert.That(client.Session, Is.Not.SameAs(firstSession));
            Assert.That(factory.Created[0].Disposed, Is.True, "the old gateway socket is gone");
            Assert.That(factory.Created[1].Disposed, Is.True, "the old game socket is gone");
            Assert.That(probe.Transitions, Has.Some.Matches<ConnectionStateChangedEvent>(
                e => e.Current == NetworkClientState.Reconnecting));
            client.Dispose();
        }

        [UnityTest]
        public IEnumerator TransportError_Reconnects()
        {
            var factory = new ScriptedFactory()
                .Then(GatewayOk()).Then(GameServerJoinOk())
                .Then(GatewayOk()).Then(GameServerJoinOk());
            var auth = new CountingAuth();
            var log = new RecordingLog();
            var client = new NetworkClient(FastSettings(), factory, new JsonWireCodec(), log, auth);
            var probe = new Probe(client);

            yield return LandInWorld(client, log);
            factory.Created[1].Fail(new System.IO.IOException("connection reset by peer"));

            for (var t = Deadline(); probe.Reconnected == 0 && probe.Failed == null && DateTime.UtcNow < t;)
                yield return null;

            Assert.That(probe.SessionCloses[0].Cause, Is.EqualTo(DisconnectCause.TransportError));
            Assert.That(probe.Reconnected, Is.EqualTo(1), log.ToString());
            Assert.That(client.State, Is.EqualTo(NetworkClientState.InWorld));
            client.Dispose();
        }

        [UnityTest]
        public IEnumerator DuplicateLoginKick_NeverReconnects()
        {
            var factory = new ScriptedFactory().Then(GatewayOk()).Then(GameServerJoinOk());
            var auth = new CountingAuth();
            var log = new RecordingLog();
            var client = new NetworkClient(FastSettings(), factory, new JsonWireCodec(), log, auth);
            var probe = new Probe(client);

            yield return LandInWorld(client, log);

            // The game server's ADR-20 eviction: kick, then the paired disconnect.
            factory.Created[1].Deliver(Kick(KickReasons.DuplicateLogin));
            factory.Created[1].Deliver(Bye(KickReasons.DuplicateLogin));

            for (var t = Deadline(0.5); DateTime.UtcNow < t;) yield return null;

            Assert.That(probe.SessionCloses.Count, Is.EqualTo(1), "kick + disconnect is ONE event");
            Assert.That(probe.SessionCloses[0].Cause, Is.EqualTo(DisconnectCause.Kicked));
            Assert.That(probe.Attempts, Is.Empty, "an eviction must never be retried\n" + log);
            Assert.That(auth.Calls, Is.EqualTo(1));
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Ended));
            Assert.That(client.IsReconnecting, Is.False);
            client.Dispose();
        }

        [UnityTest]
        public IEnumerator GatewayEviction_MakesTheFollowingSessionDrop_Terminal()
        {
            var factory = new ScriptedFactory().Then(GatewayOk()).Then(GameServerJoinOk());
            var auth = new CountingAuth();
            var log = new RecordingLog();
            var client = new NetworkClient(FastSettings(), factory, new JsonWireCodec(), log, auth);
            var probe = new Probe(client);

            yield return LandInWorld(client, log);

            // The gateway evicts us (this account logged in elsewhere). The game
            // server's kick may arrive as a plain close from an older build — that
            // close would normally reconnect, and must not.
            factory.Created[0].Deliver(Kick(KickReasons.DuplicateLogin));
            factory.Created[0].Deliver(Bye(KickReasons.DuplicateLogin));
            Assert.That(probe.GatewayCloses.Count, Is.EqualTo(1));
            Assert.That(probe.GatewayCloses[0].Cause, Is.EqualTo(DisconnectCause.Kicked));
            Assert.That(client.State, Is.EqualTo(NetworkClientState.InWorld), "the gateway is not in the gameplay path");

            factory.Created[1].Eof();
            for (var t = Deadline(0.5); DateTime.UtcNow < t;) yield return null;

            Assert.That(probe.Attempts, Is.Empty, "after an eviction the session close is terminal\n" + log);
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Ended));
            client.Dispose();
        }

        [UnityTest]
        public IEnumerator GatewayLinkLoss_LeavesTheSessionAlone_AndIsNotRetriedInPlace()
        {
            var factory = new ScriptedFactory().Then(GatewayOk()).Then(GameServerJoinOk());
            var auth = new CountingAuth();
            var log = new RecordingLog();
            var client = new NetworkClient(FastSettings(), factory, new JsonWireCodec(), log, auth);
            var probe = new Probe(client);

            yield return LandInWorld(client, log);
            factory.Created[0].Eof();

            for (var t = Deadline(0.5); DateTime.UtcNow < t;) yield return null;

            Assert.That(probe.GatewayCloses.Count, Is.EqualTo(1));
            Assert.That(probe.SessionCloses, Is.Empty);
            Assert.That(client.State, Is.EqualTo(NetworkClientState.InWorld));
            // Re-authenticating now would supersede our own login (the gateway
            // publishes session_superseded for the live jti) and get the game
            // session kicked. So: no dial.
            Assert.That(factory.Created.Count, Is.EqualTo(2));
            Assert.That(auth.Calls, Is.EqualTo(1));
            client.Dispose();
        }

        [UnityTest]
        public IEnumerator ConnectionLossReconnect_CanBeTurnedOff_WithoutTouchingShutdownReconnect()
        {
            var factory = new ScriptedFactory().Then(GatewayOk()).Then(GameServerJoinOk());
            var settings = FastSettings();
            settings.ReconnectOnConnectionLoss = false;
            var client = new NetworkClient(settings, factory, new JsonWireCodec(), new RecordingLog(), new CountingAuth());
            var probe = new Probe(client);
            var log = new RecordingLog();

            yield return LandInWorld(client, log);
            factory.Created[1].Eof();
            for (var t = Deadline(0.3); DateTime.UtcNow < t;) yield return null;

            Assert.That(probe.Attempts, Is.Empty);
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Ended));
            client.Dispose();
        }

        // ── the budget, and what happens after it ────────────────────────

        [UnityTest]
        public IEnumerator BudgetExhausted_GivesUp_WithTheAttemptCount_AndStaysEnded()
        {
            var clock = new FakeClock();
            var factory = new ScriptedFactory().Then(GatewayOk()).Then(GameServerJoinOk());
            // Every reconnect round: the gateway is up but its store is not. Ten
            // seconds of the budget go by per round.
            for (var i = 0; i < 8; i++)
            {
                factory.Then(GatewayAuthRefuses("session creation failed", () => clock.NowMs += 10_000));
            }

            var auth = new CountingAuth();
            var log = new RecordingLog();
            var client = new NetworkClient(FastSettings(clock), factory, new JsonWireCodec(), log, auth);
            var probe = new Probe(client);

            yield return LandInWorld(client, log);
            factory.Created[1].Eof();

            for (var t = Deadline(); probe.Failed == null && probe.Reconnected == 0 && DateTime.UtcNow < t;)
                yield return null;

            // Rounds start at t=0 (pause 0), t=10+1, t=20+2; the fourth would start
            // past 25 s and is not started.
            Assert.That(probe.Failed, Is.InstanceOf<ReconnectExhaustedException>(), log.ToString());
            var exhausted = (ReconnectExhaustedException)probe.Failed;
            Assert.That(exhausted.Attempts, Is.EqualTo(3), log.ToString());
            Assert.That(exhausted.Permanent, Is.False);
            Assert.That(exhausted.InnerException, Is.InstanceOf<NetworkException>());
            Assert.That(probe.Progress.Count, Is.EqualTo(3));
            Assert.That(probe.Progress[1].DelaySeconds, Is.EqualTo(1f));
            Assert.That(probe.Progress[2].DelaySeconds, Is.EqualTo(2f));
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Ended));
            Assert.That(client.IsReconnecting, Is.False);
            Assert.That(factory.Created.Count, Is.EqualTo(5), "2 for the login, 3 gateway dials for the rounds");
            for (var i = 2; i < 5; i++)
            {
                Assert.That(factory.Created[i].Disposed, Is.True, $"round socket {i} leaked");
            }

            client.Dispose();
        }

        [UnityTest]
        public IEnumerator PermanentRefusal_StopsAfterOneRound_WithTheRealError()
        {
            var factory = new ScriptedFactory()
                .Then(GatewayOk()).Then(GameServerJoinOk())
                .Then(GatewayAuthRefuses("invalid token"));
            var auth = new CountingAuth();
            var log = new RecordingLog();
            var client = new NetworkClient(FastSettings(), factory, new JsonWireCodec(), log, auth);
            var probe = new Probe(client);

            yield return LandInWorld(client, log);
            factory.Created[1].Eof();

            for (var t = Deadline(); probe.Failed == null && probe.Reconnected == 0 && DateTime.UtcNow < t;)
                yield return null;

            Assert.That(probe.Failed, Is.InstanceOf<ReconnectExhaustedException>(), log.ToString());
            var exhausted = (ReconnectExhaustedException)probe.Failed;
            Assert.That(exhausted.Permanent, Is.True);
            Assert.That(exhausted.Attempts, Is.EqualTo(1));
            Assert.That(((NetworkException)exhausted.InnerException).ServerError, Is.EqualTo("invalid token"));
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Ended));
            Assert.That(factory.Created.Count, Is.EqualTo(3));
            client.Dispose();
        }

        [UnityTest]
        public IEnumerator ServerShutdown_WaitsABackoffRoundFirst()
        {
            var factory = new ScriptedFactory()
                .Then(GatewayOk()).Then(GameServerJoinOk())
                .Then(GatewayOk()).Then(GameServerJoinOk());
            var log = new RecordingLog();
            var client = new NetworkClient(FastSettings(), factory, new JsonWireCodec(), log, new CountingAuth());
            var probe = new Probe(client);

            yield return LandInWorld(client, log);
            factory.Created[1].Deliver(Bye(KickReasons.ServerShutdown));

            for (var t = Deadline(); probe.Reconnected == 0 && probe.Failed == null && DateTime.UtcNow < t;)
                yield return null;

            Assert.That(probe.Reconnected, Is.EqualTo(1), log.ToString());
            Assert.That(probe.Progress[0].DelaySeconds, Is.EqualTo(1f), "a drain waits out one pause: no storm");
            client.Dispose();
        }

        // ── cancellation and the operation generation ────────────────────

        [UnityTest]
        public IEnumerator CancelDuringAuth_ClosesTheGateway_AndLeavesStateDisconnected()
        {
            // A gateway that never answers: auth parks on the read.
            var factory = new ScriptedFactory().Then(type => null);
            var log = new RecordingLog();
            var client = new NetworkClient(FastSettings(), factory, new JsonWireCodec(), log, new CountingAuth());
            var probe = new Probe(client);

            var cts = new CancellationTokenSource();
            Exception failure = null;
            var done = false;
            Connect(client, "map_01", cts.Token).ContinueWith(ex => { done = true; failure = ex; }).Forget();
            Assert.That(done, Is.False, "the gateway never answers auth: the read must park");
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Authenticating));
            Assert.That(factory.Created.Count, Is.EqualTo(1));

            cts.Cancel();
            for (var t = Deadline(); !done && DateTime.UtcNow < t;) yield return null;

            Assert.That(done, Is.True, "cancel did not complete the connect\n" + log);
            Assert.That(failure, Is.InstanceOf<OperationCanceledException>(), failure?.ToString());
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Disconnected));
            Assert.That(client.Session, Is.Null);
            foreach (var t in factory.Created)
            {
                Assert.That(t.Disposed, Is.True, "a cancelled connect must close what it dialed");
            }

            client.Dispose();
        }

        [UnityTest]
        public IEnumerator StaleAuthCompletion_AfterDisconnect_IsDiscarded()
        {
            var log = new RecordingLog();
            Exception failure = null;
            var done = false;
            // Hold the gateway's answers so the flow parks inside AuthenticateAsync.
            var slowGateway = new RespondingTransport(GatewayOk()) { Hold = true };
            var lazy = new LazyFactory(slowGateway);
            var client = new NetworkClient(FastSettings(), lazy, new JsonWireCodec(), log, new CountingAuth());
            var probe = new Probe(client);

            Connect(client, "map_01").ContinueWith(ex => { done = true; failure = ex; }).Forget();
            Assert.That(done, Is.False, "the auth reply is held; the connect must be parked");
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Authenticating));

            // The user backs out. Then the slow gateway finally answers.
            client.Disconnect();
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Ended));
            slowGateway.Release();

            for (var t = Deadline(); !done && DateTime.UtcNow < t;) yield return null;

            Assert.That(done, Is.True, log.ToString());
            Assert.That(failure, Is.InstanceOf<OperationCanceledException>(),
                "a completion from a superseded operation must not be applied: " + failure);
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Ended), "the stale flow flipped state");
            Assert.That(client.Session, Is.Null);
            Assert.That(lazy.Created.Count, Is.EqualTo(1), "the stale flow went on to dial the game server");
            Assert.That(slowGateway.Disposed, Is.True, "the stale gateway socket leaked");
            client.Dispose();
        }

        [UnityTest]
        public IEnumerator ANewerConnect_SupersedesTheOlderOne_AndOwnsTheState()
        {
            var log = new RecordingLog();
            var slowGateway = new RespondingTransport(GatewayOk()) { Hold = true };
            var lazy = new LazyFactory(slowGateway,
                new RespondingTransport(GatewayOk()), new RespondingTransport(GameServerJoinOk()));
            var client = new NetworkClient(FastSettings(), lazy, new JsonWireCodec(), log, new CountingAuth());
            var probe = new Probe(client);

            Exception first = null, second = null;
            var firstDone = false;
            var secondDone = false;
            Connect(client, "map_01").ContinueWith(ex => { firstDone = true; first = ex; }).Forget();
            Assert.That(firstDone, Is.False);

            Connect(client, "map_02").ContinueWith(ex => { secondDone = true; second = ex; }).Forget();
            for (var t = Deadline(); !secondDone && DateTime.UtcNow < t;) yield return null;
            Assert.That(second, Is.Null, second?.ToString() + "\n" + log);
            Assert.That(client.State, Is.EqualTo(NetworkClientState.InWorld));
            Assert.That(client.CurrentMapId, Is.EqualTo("map_02"));
            var session = client.Session;

            // The first gateway answers late.
            slowGateway.Release();
            for (var t = Deadline(); !firstDone && DateTime.UtcNow < t;) yield return null;

            Assert.That(first, Is.InstanceOf<OperationCanceledException>(), first?.ToString());
            Assert.That(client.State, Is.EqualTo(NetworkClientState.InWorld), "the stale flow disturbed the live session");
            Assert.That(client.Session, Is.SameAs(session));
            Assert.That(client.CurrentMapId, Is.EqualTo("map_02"));
            Assert.That(slowGateway.Disposed, Is.True);
            Assert.That(lazy.Created.Count, Is.EqualTo(3), "the stale flow must not dial further");
            client.Dispose();
        }

        [UnityTest]
        public IEnumerator DisconnectDuringTheBackoffPause_StopsTheLoop_Silently()
        {
            var factory = new ScriptedFactory()
                .Then(GatewayOk()).Then(GameServerJoinOk())
                .Then(GatewayOk()).Then(GameServerJoinOk());
            var settings = FastSettings();
            UniTaskCompletionSource parkedPause = null;
            settings.DelayScheduler = (delay, ct) =>
            {
                parkedPause = new UniTaskCompletionSource();
                var p = parkedPause;
                ct.Register(() => p.TrySetCanceled(ct));
                return p.Task;
            };
            var log = new RecordingLog();
            var client = new NetworkClient(settings, factory, new JsonWireCodec(), log, new CountingAuth());
            var probe = new Probe(client);

            yield return LandInWorld(client, log);
            factory.Created[1].Deliver(Bye(KickReasons.ServerShutdown)); // delay-first: parks on the pause
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Reconnecting));
            Assert.That(client.IsReconnecting, Is.True);
            Assert.That(parkedPause, Is.Not.Null);

            client.Disconnect();
            for (var t = Deadline(0.3); DateTime.UtcNow < t;) yield return null;

            Assert.That(probe.Attempts, Is.Empty, "a user disconnect must cancel the pending round");
            Assert.That(probe.Failed, Is.Null, "a user disconnect is not a reconnect failure");
            Assert.That(client.IsReconnecting, Is.False);
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Ended));
            Assert.That(factory.Created.Count, Is.EqualTo(2));
            client.Dispose();
        }

        [Test]
        public void ConnectWithAnEmptyJwt_IsRejectedLocally()
        {
            var client = new NetworkClient(FastSettings(), new ScriptedFactory(), new JsonWireCodec(), new RecordingLog());
            Assert.Throws<ArgumentException>(() => client.ConnectAsync("", "map_01", CancellationToken.None).Forget());
        }

        /// <summary>Hands out pre-built transports in order.</summary>
        private sealed class LazyFactory : ITransportFactory
        {
            private readonly RespondingTransport[] _transports;
            public readonly List<RespondingTransport> Created = new List<RespondingTransport>();

            public LazyFactory(params RespondingTransport[] transports) => _transports = transports;

            public ITransport Create(TransportKind kind)
            {
                Assert.That(Created.Count, Is.LessThan(_transports.Length), "dialed more than scripted");
                var t = _transports[Created.Count];
                Created.Add(t);
                return t;
            }
        }
    }
}
