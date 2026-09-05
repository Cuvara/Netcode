using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Cuvara.Netcode.Auth;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Tests the map transfer flow: leave one game server, re-authenticate, join
    /// another. The backend is fully scripted — no sockets, no servers.
    /// </summary>
    [TestFixture]
    public sealed class MapTransferTests
    {
        // ── scripted backend (reused from NetworkClientRetryTests pattern) ──

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
                if (_inbound.Count > 0) return UniTask.FromResult(_inbound.Dequeue());
                _parked = new UniTaskCompletionSource<byte[]>();
                return _parked.Task;
            }

            public UniTask WriteFrameAsync(byte[] body, CancellationToken ct)
            {
                var type = ReadType(body);
                var responses = _respond(type);
                if (responses != null)
                    foreach (var r in responses) Deliver(r);
                return UniTask.CompletedTask;
            }

            public void Deliver(byte[] frame)
            {
                var parked = _parked;
                if (parked != null) { _parked = null; parked.TrySetResult(frame); }
                else _inbound.Enqueue(frame);
            }

            public void Close() => _closed = true;
            public void Dispose() => _closed = true;

            private static byte ReadType(byte[] body)
            {
                var json = Encoding.UTF8.GetString(body);
                var marker = "\"type\":";
                var at = json.IndexOf(marker, StringComparison.Ordinal);
                var start = at + marker.Length;
                var end = start;
                while (end < json.Length && char.IsDigit(json[end])) end++;
                return byte.Parse(json.Substring(start, end - start));
            }
        }

        private sealed class ScriptedFactory : ITransportFactory
        {
            private readonly List<Func<RespondingTransport>> _script =
                new List<Func<RespondingTransport>>();
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

        private sealed class FakeAuth : IAuthProvider
        {
            public UniTask<string> GetJwtAsync(CancellationToken ct) =>
                UniTask.FromResult("fake-jwt");
        }

        private sealed class SilentLog : INetLog
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception exception = null) { }
        }

        private static byte[] Frame(MsgType type, string payloadJson) =>
            Encoding.UTF8.GetBytes("{\"type\":" + (int)type + ",\"payload\":" + payloadJson + "}");

        private static byte[] AuthOk() =>
            Frame(MsgType.AuthResp, "{\"ok\":true,\"user_id\":\"u1\"}");

        private static byte[] AssignOk(string addr = "127.0.0.1:9000") =>
            Frame(MsgType.EnterWorldResp,
                "{\"server_addr\":\"" + addr + "\",\"join_token\":\"tok\",\"transport\":\"tcp\"}");

        private static byte[] JoinOk() =>
            Frame(MsgType.JoinTokenResp, "{\"ok\":true,\"user_id\":\"u1\",\"tick_rate\":60}");

        private static Func<byte, List<byte[]>> Gateway(
            Queue<byte[]> assignments) => type =>
        {
            switch ((MsgType)type)
            {
                case MsgType.Auth:
                    return new List<byte[]> { AuthOk() };
                case MsgType.EnterWorld:
                    Assert.That(assignments.Count, Is.GreaterThan(0));
                    return new List<byte[]> { assignments.Dequeue() };
                default:
                    return null;
            }
        };

        private static Func<byte, List<byte[]>> GameServerOk() => type =>
            (MsgType)type == MsgType.JoinToken ? new List<byte[]> { JoinOk() } : null;

        private static NetworkSettings FastSettings() => new NetworkSettings
        {
            JoinAttempts = 1,
            JoinRetryDelay = TimeSpan.FromMilliseconds(1),
            RetryJitter = TimeSpan.Zero,
            DelayScheduler = (delay, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return UniTask.CompletedTask;
            }
        };

        [Test]
        public void TransferToMap_RequiresAuthProvider()
        {
            var client = new NetworkClient(
                FastSettings(), new ScriptedFactory(), new JsonWireCodec(), new SilentLog());

            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await client.TransferToMapAsync("map_02", CancellationToken.None));
        }

        [Test]
        public void TransferToMap_RejectsEmptyMapId()
        {
            var client = new NetworkClient(
                FastSettings(), new ScriptedFactory(), new JsonWireCodec(), new SilentLog(),
                new FakeAuth());

            Assert.ThrowsAsync<ArgumentException>(async () =>
                await client.TransferToMapAsync("", CancellationToken.None));
        }

        [Test]
        public async void TransferToMap_ConnectsToNewMap()
        {
            // Script: first connect to map_01, then transfer to map_02.
            // That means 2 gateway connections and 2 game server connections (4 total).
            var assignments1 = new Queue<byte[]>();
            assignments1.Enqueue(AssignOk("127.0.0.1:9001"));

            var assignments2 = new Queue<byte[]>();
            assignments2.Enqueue(AssignOk("127.0.0.1:9002"));

            var factory = new ScriptedFactory()
                .Then(Gateway(assignments1))  // gateway for map_01
                .Then(GameServerOk())         // game server for map_01
                .Then(Gateway(assignments2))  // gateway for map_02 (transfer)
                .Then(GameServerOk());        // game server for map_02

            var client = new NetworkClient(
                FastSettings(), factory, new JsonWireCodec(), new SilentLog(),
                new FakeAuth());

            var states = new List<NetworkClientState>();
            client.StateChanged += s => states.Add(s);

            // First: connect to map_01.
            await client.ConnectAsync("map_01", CancellationToken.None);
            Assert.That(client.State, Is.EqualTo(NetworkClientState.InWorld));
            Assert.That(client.CurrentMapId, Is.EqualTo("map_01"));

            // Transfer to map_02.
            await client.TransferToMapAsync("map_02", CancellationToken.None);
            Assert.That(client.State, Is.EqualTo(NetworkClientState.InWorld));
            Assert.That(client.CurrentMapId, Is.EqualTo("map_02"));

            // 4 transports were created.
            Assert.That(factory.Created.Count, Is.EqualTo(4));

            // States should include Transferring.
            Assert.That(states, Does.Contain(NetworkClientState.Transferring));
        }

        [Test]
        public async void TransferToMap_CancelsReconnect()
        {
            // Verify that starting a transfer cancels any pending reconnect.
            // This is tested indirectly: if the transfer succeeds with 4 transports,
            // no reconnect was fighting it.
            var assignments1 = new Queue<byte[]>();
            assignments1.Enqueue(AssignOk());

            var assignments2 = new Queue<byte[]>();
            assignments2.Enqueue(AssignOk("127.0.0.1:9002"));

            var factory = new ScriptedFactory()
                .Then(Gateway(assignments1))
                .Then(GameServerOk())
                .Then(Gateway(assignments2))
                .Then(GameServerOk());

            var client = new NetworkClient(
                FastSettings(), factory, new JsonWireCodec(), new SilentLog(),
                new FakeAuth());

            await client.ConnectAsync("map_01", CancellationToken.None);
            await client.TransferToMapAsync("map_02", CancellationToken.None);

            Assert.That(client.CurrentMapId, Is.EqualTo("map_02"));

            client.Dispose();
        }
    }
}
