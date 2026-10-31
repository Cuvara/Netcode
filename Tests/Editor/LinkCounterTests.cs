using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Connection;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The counters behind <see cref="NetworkClientDiagnostics"/>, driven through a scripted
    /// transport: bytes and frames in and out, send-queue drops, resync requests, unresolved
    /// snapshots, and the zero-allocation read. EditMode only: <c>WireConnection</c> awaits through
    /// UniTask, which needs UnityEngine.
    /// </summary>
    [TestFixture]
    public sealed class LinkCounterTests
    {
        private sealed class ScriptedTransport : ITransport, ITransportDecorator
        {
            private readonly Queue<byte[]> _inbound = new Queue<byte[]>();
            private readonly UniTaskCompletionSource<byte[]> _parked = new UniTaskCompletionSource<byte[]>();
            private bool _closed;

            public readonly List<byte[]> Written = new List<byte[]>();

            public string RemoteEndPoint => "scripted";
            public bool IsConnected => !_closed;

            // A decorator over nothing: lets the unwrap test hand it a chain without a second type.
            public ITransport Inner { get; set; }

            public void Enqueue(byte[] body) => _inbound.Enqueue(body);

            public UniTask ConnectAsync(string host, int port, CancellationToken ct) => UniTask.CompletedTask;

            public UniTask<byte[]> ReadFrameAsync(CancellationToken ct) =>
                _inbound.Count > 0 ? UniTask.FromResult(_inbound.Dequeue()) : _parked.Task;

            public UniTask WriteFrameAsync(byte[] body, CancellationToken ct)
            {
                Written.Add(body);
                return UniTask.CompletedTask;
            }

            public void Close() => _closed = true;
            public void Dispose() => _closed = true;
        }

        private sealed class SilentLog : INetLog
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception exception = null) { }
        }

        private static readonly JsonWireCodec Codec = new JsonWireCodec();

        private static byte[] Inbound(MsgType type, string payloadJson) =>
            Encoding.UTF8.GetBytes("{\"type\":" + (int)type + ",\"payload\":" + payloadJson + "}");

        [Test]
        public void BytesAndFramesReceived_CountEveryBodyTheTransportHandsUp()
        {
            var transport = new ScriptedTransport();
            var a = Inbound(MsgType.Snapshot, "{\"tick\":1}");
            var b = Inbound(MsgType.Snapshot, "{\"tick\":2}");
            var ping = Inbound(MsgType.Ping, "{\"timestamp\":7}");
            transport.Enqueue(a);
            transport.Enqueue(b);
            transport.Enqueue(ping);
            var conn = new WireConnection("test", transport, Codec, new NetworkSettings(), new SilentLog());

            conn.Start();

            Assert.That(conn.FramesReceived, Is.EqualTo(3));
            Assert.That(conn.BytesReceived, Is.EqualTo(a.Length + b.Length + ping.Length));
            conn.Dispose();
        }

        [UnityTest]
        public IEnumerator FramesAndBytesSent_CountWhatTheWriteLoopHandedToTheTransport()
        {
            var transport = new ScriptedTransport();
            transport.Enqueue(Inbound(MsgType.Ping, "{\"timestamp\":12345}"));
            var conn = new WireConnection("test", transport, Codec, new NetworkSettings(), new SilentLog());

            conn.Start();

            // The write loop resumes through the Unity synchronization context; bound by time.
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (conn.FramesSent == 0 && deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                yield return null;
            }

            Assert.That(transport.Written, Has.Count.EqualTo(1));
            Assert.That(conn.FramesSent, Is.EqualTo(1));
            Assert.That(conn.BytesSent, Is.EqualTo(transport.Written[0].Length));
            conn.Dispose();
        }

        [Test]
        public void SendQueueDrops_CountEachOldestFrameDiscardedOnOverflow()
        {
            var transport = new ScriptedTransport();
            var settings = new NetworkSettings { SendQueueCapacity = 2 };
            // Never started: nothing drains the queue, so the third send overflows it.
            var conn = new WireConnection("test", transport, Codec, settings, new SilentLog());

            for (var i = 0; i < 5; i++)
            {
                conn.Send(MsgType.Resync, new ResyncRequest());
            }

            Assert.That(conn.SendQueueDrops, Is.EqualTo(3), "capacity 2, five frames: three oldest are dropped");
            conn.Dispose();
        }

        [Test]
        public void ResyncRequests_CountSentRequests_NotCallsDroppedWhileOneIsOutstanding()
        {
            var transport = new ScriptedTransport();
            var settings = new NetworkSettings();
            var wire = new WireConnection("test", transport, Codec, settings, new SilentLog());
            var session = new GameSessionClient(settings, null, Codec, new SilentLog());
            Field("_connection").SetValue(session, wire);

            Assert.That(session.ResyncRequests, Is.EqualTo(0L));
            session.RequestResync();
            session.RequestResync();
            session.RequestResync();

            Assert.That(session.ResyncRequests, Is.EqualTo(1L));
            session.Dispose();
        }

        [Test]
        public void UnresolvedAndResolvedSnapshots_AreCountedSeparately()
        {
            var transport = new ScriptedTransport();
            var settings = new NetworkSettings();
            var wire = new WireConnection("test", transport, Codec, settings, new SilentLog());
            var session = new GameSessionClient(settings, null, Codec, new SilentLog());
            Field("_connection").SetValue(session, wire);
            var onFrame = typeof(GameSessionClient).GetMethod("OnFrame", BindingFlags.Instance | BindingFlags.NonPublic);

            var good = new SnapshotMessage { Tick = 1, Full = false };
            onFrame.Invoke(session, new object[] { new WireFrame(MsgType.Snapshot, good, WireEncoding.Json) });

            // Handle 7 was never introduced, so the whole snapshot is refused and a keyframe asked for.
            var bad = new SnapshotMessage { Tick = 2, Full = false };
            bad.Entities.Add(new EntitySnapshot { Id = string.Empty, Handle = 7 });
            onFrame.Invoke(session, new object[] { new WireFrame(MsgType.Snapshot, bad, WireEncoding.Json) });

            Assert.That(session.SnapshotsReceived, Is.EqualTo(1L));
            Assert.That(session.UnresolvedSnapshots, Is.EqualTo(1L));
            Assert.That(session.ResyncRequests, Is.EqualTo(1L), "an unresolved snapshot asks for a keyframe");
            session.Dispose();
        }

        [Test]
        public void SessionCountersAreZeroBeforeAJoin()
        {
            var session = new GameSessionClient(new NetworkSettings(), null, Codec, new SilentLog());

            Assert.That(session.BytesReceived + session.BytesSent + session.FramesSent + session.SendQueueDrops, Is.EqualTo(0L));
            Assert.That(session.ResyncRequests + session.UnresolvedSnapshots + session.SnapshotsReceived, Is.EqualTo(0L));
            Assert.That(session.Kcp, Is.Null);
            session.Dispose();
        }

        [Test]
        public void KcpTransportBeforeConnect_ReportsZeroForEveryDiagnosticsField()
        {
            using (var kcp = new KcpTransport())
            {
                Assert.That(kcp.RtoMs, Is.EqualTo(0u));
                Assert.That(kcp.AwakeSilentMs, Is.EqualTo(0L));
                Assert.That(kcp.SilenceFailures, Is.EqualTo(0));
                Assert.That(kcp.PendingSendSegments, Is.EqualTo(0));
                Assert.That(kcp.DatagramsRejected, Is.EqualTo(0L));
                Assert.That(kcp.DatagramsReceived, Is.EqualTo(0L));
            }
        }

        [Test]
        public void Unwrap_FindsTheTransportUnderAChainOfDecorators_AndNullWhenAbsent()
        {
            var bottom = new ScriptedTransport();
            var middle = new ScriptedTransport { Inner = bottom };
            var top = new ScriptedTransport { Inner = middle };

            Assert.That(TransportDecorators.Unwrap<ScriptedTransport>(top), Is.SameAs(top), "the outermost match wins");
            Assert.That(TransportDecorators.Unwrap<KcpTransport>(top), Is.Null);
            Assert.That(TransportDecorators.Unwrap<KcpTransport>(null), Is.Null);
        }

        [Test]
        public void Unwrap_ACycleEndsInsteadOfHanging()
        {
            var a = new ScriptedTransport();
            var b = new ScriptedTransport { Inner = a };
            a.Inner = b;

            Assert.That(TransportDecorators.Unwrap<KcpTransport>(a), Is.Null);
        }

        private sealed class MarkerFactory : ITransportFactory
        {
            public readonly ITransportFactory Inner;
            public MarkerFactory(ITransportFactory inner) => Inner = inner;
            public ITransport Create(TransportKind kind) => Inner.Create(kind);
        }

        [Test]
        public void DecorateTransports_WrapsTheCurrentFactory_AndRejectsNull()
        {
            var original = new MarkerFactory(null);
            var client = new NetworkClient(new NetworkSettings(), original, Codec, new SilentLog());

            client.DecorateTransports(inner => new MarkerFactory(inner));

            Assert.That(client.TransportFactory, Is.TypeOf<MarkerFactory>());
            Assert.That(((MarkerFactory)client.TransportFactory).Inner, Is.SameAs(original));
            Assert.Throws<ArgumentNullException>(() => client.DecorateTransports(null));
            Assert.Throws<ArgumentNullException>(() => client.DecorateTransports(_ => null));
            client.Dispose();
        }

        [Test]
        public void GetDiagnostics_WithoutASession_IsZeroExceptStateGenerationAndAllocatesNothing()
        {
            var client = new NetworkClient(new NetworkSettings(), null, Codec, new SilentLog());

            var d = client.GetDiagnostics();
            Assert.That(d.State, Is.EqualTo(NetworkClientState.Disconnected));
            Assert.That(d.Generation, Is.EqualTo(client.Generation));
            Assert.That(d.ServerProtocolVersion, Is.EqualTo(0u));
            Assert.That(d.RttMs + d.BytesSent + d.BytesReceived + d.FramesReceived + d.FramesSent, Is.EqualTo(0L));
            Assert.That(d.ReconnectAttempts, Is.EqualTo(0));

            long sink = client.GetDiagnostics().FramesReceived;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                sink += client.GetDiagnostics().BytesReceived;
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.EqualTo(0L), "GetDiagnostics must not allocate (sink " + sink + ")");
            client.Dispose();
        }

        private static FieldInfo Field(string name) =>
            typeof(GameSessionClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    }
}
