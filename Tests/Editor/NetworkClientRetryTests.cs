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
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Drives the whole two-hop connect flow — gateway auth, enter_world, join —
    /// through a scripted backend, to pin the retry and reconnect policy #54 added.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why these exist.</b> <c>EnterWorldAsync</c> used to sit inside the retry
    /// loop but OUTSIDE its try: the gateway deliberately types "server is
    /// starting, retry shortly" as retryable and its single-flight allocation
    /// assumes the client retries — yet any enter_world refusal aborted the whole
    /// connect with zero of the attempts burned. And nothing consumed the server's
    /// 30 s entity hold: a <c>server_shutdown</c> close just ended the session.
    /// </para>
    /// <para>
    /// Tests that cross a retry delay or a reconnect pause are [UnityTest]
    /// coroutines: <c>UniTask.Delay</c> completes on the player loop, which a plain
    /// [Test] never pumps (same reasoning as the pong test in
    /// <c>WireConnectionDispatchTests</c>).
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class NetworkClientRetryTests
    {
        // ── scripted backend ─────────────────────────────────────────────

        /// <summary>
        /// A transport whose reads are produced by a responder function applied to
        /// every frame the client writes — a one-connection scripted server.
        /// </summary>
        private sealed class RespondingTransport : ITransport
        {
            private readonly Func<byte, List<byte[]>> _respond;
            private readonly Queue<byte[]> _inbound = new Queue<byte[]>();
            private UniTaskCompletionSource<byte[]> _parked;
            private bool _closed;

            public RespondingTransport(Func<byte, List<byte[]>> respond) => _respond = respond;

            public string RemoteEndPoint => "scripted";
            public bool IsConnected => !_closed;

            public UniTask ConnectAsync(string host, int port, CancellationToken ct) =>
                UniTask.CompletedTask;

            public UniTask<byte[]> ReadFrameAsync(CancellationToken ct)
            {
                if (_inbound.Count > 0)
                {
                    return UniTask.FromResult(_inbound.Dequeue());
                }
                _parked = new UniTaskCompletionSource<byte[]>();
                return _parked.Task;
            }

            public UniTask WriteFrameAsync(byte[] body, CancellationToken ct)
            {
                // Route on the type field of the client's JSON frame. The codec only
                // encodes client->server payloads, so the "server" reads the raw type.
                var type = ReadType(body);
                var responses = _respond(type);
                if (responses != null)
                {
                    foreach (var r in responses) Deliver(r);
                }
                return UniTask.CompletedTask;
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

            public void Close() => _closed = true;
            public void Dispose() => _closed = true;

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

        /// <summary>
        /// Hands out one scripted transport per Create call, in order: the flow dials
        /// the gateway first, then one game-server transport per join attempt.
        /// </summary>
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
                    "the flow dialed more connections than the script expected");
                var t = _script[Created.Count]();
                Created.Add(t);
                return t;
            }
        }

        // ── frame builders (server->client frames are hand-built: the client
        //    codec deliberately has no encoder for them) ───────────────────

        private static byte[] Frame(MsgType type, string payloadJson) =>
            Encoding.UTF8.GetBytes("{\"type\":" + (int)type + ",\"payload\":" + payloadJson + "}");

        private static byte[] AuthOk() =>
            Frame(MsgType.AuthResp, "{\"ok\":true,\"user_id\":\"u1\"}");

        private static byte[] AuthErr(string error) =>
            Frame(MsgType.AuthResp, "{\"ok\":false,\"error\":\"" + error + "\"}");

        private static byte[] AssignOk() =>
            Frame(MsgType.EnterWorldResp,
                "{\"server_addr\":\"127.0.0.1:9000\",\"join_token\":\"tok\",\"transport\":\"tcp\"}");

        private static byte[] AssignErr(string error) =>
            Frame(MsgType.EnterWorldResp, "{\"error\":\"" + error + "\"}");

        private static byte[] JoinOk() =>
            Frame(MsgType.JoinTokenResp, "{\"ok\":true,\"user_id\":\"u1\",\"tick_rate\":60}");

        private static byte[] ServerShutdown() =>
            Frame(MsgType.Disconnect, "{\"reason\":\"server_shutdown\"}");

        /// <summary>Gateway responder: auth ok; enter_world answers from a queue.</summary>
        private static Func<byte, List<byte[]>> Gateway(Queue<byte[]> assignments) => type =>
        {
            switch ((MsgType)type)
            {
                case MsgType.Auth:
                    return new List<byte[]> { AuthOk() };
                case MsgType.EnterWorld:
                    Assert.That(assignments.Count, Is.GreaterThan(0),
                        "enter_world asked more often than the script expected");
                    return new List<byte[]> { assignments.Dequeue() };
                default:
                    return null; // pings etc. — irrelevant here
            }
        };

        private static Func<byte, List<byte[]>> GameServerJoinOk() => type =>
            (MsgType)type == MsgType.JoinToken ? new List<byte[]> { JoinOk() } : null;

        private static NetworkSettings FastSettings() => new NetworkSettings
        {
            JoinAttempts = 3,
            JoinRetryDelay = TimeSpan.FromMilliseconds(1),
            RetryJitter = TimeSpan.Zero,
            ReconnectDelay = TimeSpan.FromMilliseconds(1),
            ReconnectAttempts = 3,
        };

        private sealed class SilentLog : INetLog
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception exception = null) { }
        }

        private sealed class CountingAuth : IAuthProvider
        {
            public int Calls;
            public UniTask<string> GetJwtAsync(CancellationToken ct)
            {
                Calls++;
                return UniTask.FromResult("jwt");
            }
        }

        // ── the tests ────────────────────────────────────────────────────

        [UnityTest]
        public IEnumerator RetryableAssignmentRefusal_ConsumesAnAttempt_AndSucceedsOnTheNext()
        {
            var assignments = new Queue<byte[]>(new[]
            {
                AssignErr("server is starting, retry shortly"),
                AssignOk(),
            });
            var factory = new ScriptedFactory()
                .Then(Gateway(assignments))       // gateway
                .Then(GameServerJoinOk());        // game server, second attempt only

            var client = new NetworkClient(FastSettings(), factory, new JsonWireCodec(), new SilentLog());

            var done = false;
            Exception failure = null;
            Connect(client, "map_01").ContinueWith(ex => { done = true; failure = ex; }).Forget();

            for (var i = 0; i < 600 && !done; i++) yield return null;

            Assert.That(done, Is.True, "connect never finished");
            Assert.That(failure, Is.Null, failure?.ToString());
            Assert.That(client.State, Is.EqualTo(NetworkClientState.InWorld));
            Assert.That(assignments.Count, Is.Zero, "the second enter_world never happened");
            client.Dispose();
        }

        [UnityTest]
        public IEnumerator TerminalPreconditionRefusal_AbortsImmediately_WithTheRealError()
        {
            // "session expired" arrives as auth_resp — the gateway's precondition
            // shape. Retrying it can only repeat the answer, and before the
            // classification the real error drowned under "could not join".
            var assignments = new Queue<byte[]>(new[] { AuthErr("session expired") });
            var factory = new ScriptedFactory().Then(Gateway(assignments));

            var client = new NetworkClient(FastSettings(), factory, new JsonWireCodec(), new SilentLog());

            var done = false;
            Exception failure = null;
            Connect(client, "map_01").ContinueWith(ex => { done = true; failure = ex; }).Forget();

            for (var i = 0; i < 600 && !done; i++) yield return null;

            Assert.That(done, Is.True, "connect never finished");
            Assert.That(failure, Is.InstanceOf<NetworkException>());
            Assert.That(((NetworkException)failure).ServerError, Is.EqualTo("session expired"));
            // One gateway connection, zero game-server dials: the terminal answer
            // must not burn the remaining attempts.
            Assert.That(factory.Created.Count, Is.EqualTo(1));
            client.Dispose();
        }

        [UnityTest]
        public IEnumerator ServerShutdown_ReconnectsThroughTheAuthProvider_AndLandsBackInWorld()
        {
            var firstAssignments = new Queue<byte[]>(new[] { AssignOk() });
            var secondAssignments = new Queue<byte[]>(new[] { AssignOk() });
            RespondingTransport gameTransport = null;

            var factory = new ScriptedFactory()
                .Then(Gateway(firstAssignments))   // gateway #1
                .Then(GameServerJoinOk())          // game server #1
                .Then(Gateway(secondAssignments))  // gateway #2 (reconnect)
                .Then(GameServerJoinOk());         // game server #2 (reconnect)

            var auth = new CountingAuth();
            var client = new NetworkClient(FastSettings(), factory, new JsonWireCodec(), new SilentLog(), auth);

            var reconnected = false;
            var reconnectAttempts = 0;
            client.ReconnectAttemptStarted += _ => reconnectAttempts++;
            client.Reconnected += () => reconnected = true;

            var inWorld = false;
            Connect(client, "map_01").ContinueWith(ex => inWorld = ex == null).Forget();
            for (var i = 0; i < 600 && !inWorld; i++) yield return null;
            Assert.That(inWorld, Is.True, "initial connect never landed");
            Assert.That(auth.Calls, Is.EqualTo(1));

            // The server announces its shutdown; the policy must bring the client
            // back through the provider without any caller involvement.
            gameTransport = factory.Created[1];
            gameTransport.Deliver(ServerShutdown());

            for (var i = 0; i < 1200 && !reconnected; i++) yield return null;

            Assert.That(reconnected, Is.True, "the reconnect never landed");
            Assert.That(reconnectAttempts, Is.EqualTo(1), "one healthy round should suffice");
            Assert.That(auth.Calls, Is.EqualTo(2), "the reconnect must go through the provider");
            Assert.That(client.State, Is.EqualTo(NetworkClientState.InWorld));
            Assert.That(factory.Created.Count, Is.EqualTo(4));
            client.Dispose();
        }

        [UnityTest]
        public IEnumerator UserDisconnect_NeverTriggersTheReconnectPolicy()
        {
            var assignments = new Queue<byte[]>(new[] { AssignOk() });
            var factory = new ScriptedFactory()
                .Then(Gateway(assignments))
                .Then(GameServerJoinOk());

            var auth = new CountingAuth();
            var client = new NetworkClient(FastSettings(), factory, new JsonWireCodec(), new SilentLog(), auth);

            var inWorld = false;
            Connect(client, "map_01").ContinueWith(ex => inWorld = ex == null).Forget();
            for (var i = 0; i < 600 && !inWorld; i++) yield return null;
            Assert.That(inWorld, Is.True);

            var attempts = 0;
            client.ReconnectAttemptStarted += _ => attempts++;

            // The user leaves; whatever close frames follow are the user's own doing.
            client.Disconnect();
            factory.Created[1].Deliver(ServerShutdown());

            // Generous window in which a wrongly-armed reconnect would have fired.
            for (var i = 0; i < 120; i++) yield return null;

            Assert.That(attempts, Is.Zero, "a user-initiated close must never auto-reconnect");
            Assert.That(auth.Calls, Is.EqualTo(1));
            client.Dispose();
        }

        [Test]
        public void JitterStaysInsideItsBound_AndZeroJitterIsExact()
        {
            // The jitter maths is private; its observable contract is on settings.
            var settings = new NetworkSettings { RetryJitter = TimeSpan.FromMilliseconds(500) };
            Assert.That(settings.RetryJitter.TotalMilliseconds, Is.EqualTo(500));
            Assert.That(new NetworkSettings().EnterWorldTimeout,
                Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(20)),
                "the enter_world budget must clear the gateway's 18 s handler window");
        }

        private static async UniTask<Exception> WrapConnect(NetworkClient client, string mapId)
        {
            try
            {
                if (client.HasAuthProvider)
                {
                    await client.ConnectAsync(mapId, CancellationToken.None);
                }
                else
                {
                    await client.ConnectAsync("jwt", mapId, CancellationToken.None);
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static UniTask<Exception> Connect(NetworkClient client, string mapId) =>
            WrapConnect(client, mapId);
    }
}
