using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using System.Collections;
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
    /// Drives <see cref="WireConnection"/> through a scripted <see cref="ITransport"/>:
    /// frame counting, dispatch, heartbeat answering, and the kick/disconnect pairing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> The transport layer had no test coverage outside the Editor
    /// at all — every await in it goes through UniTask, which needs <c>UnityEngine</c>, so
    /// the read/write path could not run under <c>dotnet test</c> and was exercised only by
    /// a live server. That is how a throughput ceiling in <c>TcpTransport</c> (two awaits
    /// per frame, capping the read loop at half the player-loop rate) went unnoticed until
    /// it was hunted for unrelated reasons, and how an 8 % snapshot-rate deficit took a
    /// socket-level probe, a standalone harness and a clock comparison to attribute (#50).
    /// </para>
    /// <para>
    /// <b>What makes these runnable without pumping a player loop.</b> A UniTask that is
    /// already complete continues synchronously, so a transport whose
    /// <c>ReadFrameAsync</c> returns pre-canned frames from a queue is drained entirely
    /// inside <c>Start()</c>; when the queue is empty it hands back a task that never
    /// completes and the read loop parks. Nothing here waits, so nothing here flakes.
    /// </para>
    /// <para>
    /// <b>What this deliberately does not cover.</b> <c>TcpTransport</c> itself — the
    /// buffering, the framing off a real socket, and the player-loop scheduling cost.
    /// Those need either a real socket (Editor-only today) or the larger seam #50
    /// describes. This fixture covers the half that a fake transport can: everything from
    /// "a frame arrived" to the consumer.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class WireConnectionDispatchTests
    {
        /// <summary>
        /// A transport that serves a scripted list of inbound frames and records every
        /// outbound one.
        /// </summary>
        private sealed class ScriptedTransport : ITransport
        {
            private readonly Queue<byte[]> _inbound = new Queue<byte[]>();

            // Handed out when the script is exhausted. Never completed: the read loop
            // parks on it exactly as it would park on a quiet socket.
            private readonly UniTaskCompletionSource<byte[]> _parked =
                new UniTaskCompletionSource<byte[]>();

            public readonly List<byte[]> Written = new List<byte[]>();

            public string RemoteEndPoint => "scripted";

            public bool IsConnected => true;

            public int ReadCalls { get; private set; }

            public void Enqueue(byte[] body) => _inbound.Enqueue(body);

            public UniTask ConnectAsync(string host, int port, CancellationToken ct) =>
                UniTask.CompletedTask;

            public UniTask<byte[]> ReadFrameAsync(CancellationToken ct)
            {
                ReadCalls++;
                return _inbound.Count > 0
                    ? UniTask.FromResult(_inbound.Dequeue())
                    : _parked.Task;
            }

            public UniTask WriteFrameAsync(byte[] body, CancellationToken ct)
            {
                Written.Add(body);
                return UniTask.CompletedTask;
            }

            public void Close() { }

            public void Dispose() { }
        }

        private sealed class SilentLog : INetLog
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception exception = null) { }
        }

        private static readonly JsonWireCodec Codec = new JsonWireCodec();

        /// <summary>
        /// A server-to-client envelope, hand-built. The codec's <c>EncodeBody</c> only
        /// covers client-to-server payloads — the client never sends a snapshot or a kick,
        /// so there is deliberately no encoder for them — which means inbound test frames
        /// have to be written the way the servers write them: <c>{"type":N,"payload":{…}}</c>.
        /// </summary>
        private static byte[] Inbound(MsgType type, string payloadJson) =>
            System.Text.Encoding.UTF8.GetBytes(
                "{\"type\":" + (int)type + ",\"payload\":" + payloadJson + "}");

        private static byte[] Snapshot(long tick) =>
            Inbound(MsgType.Snapshot, "{\"tick\":" + tick + "}");

        private static (WireConnection conn, ScriptedTransport transport) Connect(
            params byte[][] frames)
        {
            var transport = new ScriptedTransport();
            foreach (var f in frames) transport.Enqueue(f);

            var conn = new WireConnection(
                "test", transport, Codec, new NetworkSettings(), new SilentLog());
            return (conn, transport);
        }

        [Test]
        public void EveryDecodedFrameIsCounted_BeforeDispatchDecidesItsFate()
        {
            var (conn, _) = Connect(
                Snapshot(1),
                Snapshot(5),
                Inbound(MsgType.Ping, "{\"timestamp\":42}"));

            var delivered = 0;
            conn.FrameReceived += _ => delivered++;

            conn.Start();

            // Three decoded, but only the snapshots reach the consumer: the ping is
            // handled internally. FramesReceived counting BOTH is the property the
            // snapshot-deficit investigation leaned on — a counter that skipped
            // internally-handled frames would have read low for a reason nobody could
            // name, again.
            Assert.That(conn.FramesReceived, Is.EqualTo(3));
            Assert.That(delivered, Is.EqualTo(2));
        }

        [Test]
        public void AScriptOfManyFramesIsDrainedInOneStart_NoLoopIterationPerFrame()
        {
            const int n = 1000;
            var frames = new byte[n][];
            for (var i = 0; i < n; i++)
                frames[i] = Snapshot(i + 1);

            var (conn, transport) = Connect(frames);
            conn.Start();

            // All thousand arrive synchronously. This is the regression fence for the
            // half-the-player-loop-rate ceiling: a read path that costs a scheduler hop
            // per frame cannot drain a burst inside one call, and this assertion is what
            // would have caught that in fifty lines instead of an investigation.
            Assert.That(conn.FramesReceived, Is.EqualTo(n));

            // One read per frame plus the final parked read. More reads than that means
            // the loop is re-reading; fewer means frames were coalesced or lost.
            Assert.That(transport.ReadCalls, Is.EqualTo(n + 1));
        }

        /// <summary>
        /// The one asynchronous case in this fixture, and why it is a coroutine: the write
        /// loop's semaphore wait goes through <c>Task.AsUniTask()</c>, whose continuation is
        /// posted to Unity's synchronization context even when the task completed
        /// synchronously. A plain [Test] never pumps that context, so the pong sits queued
        /// forever and the assertion reads an empty transport — which is a fact about the
        /// test runner, not about the connection. Yielding lets the Editor pump.
        /// </summary>
        [UnityTest]
        public IEnumerator APingIsAnsweredWithAPongCarryingTheSameTimestamp()
        {
            var (conn, transport) = Connect(
                Inbound(MsgType.Ping, "{\"timestamp\":12345}"));

            conn.Start();

            // Bounded, so a genuinely missing pong fails in frames rather than hanging the
            // suite; generous, because Editor pumping cadence is not a contract.
            for (var i = 0; i < 300 && transport.Written.Count == 0; i++)
            {
                yield return null;
            }

            Assert.That(transport.Written, Has.Count.EqualTo(1),
                "a ping must be answered regardless of session state — the servers time " +
                "out a client whose heartbeat goes quiet");

            var frame = Codec.DecodeBody(transport.Written[0]);
            Assert.That(frame.Type, Is.EqualTo(MsgType.Pong));
            Assert.That(((PongMessage)frame.Payload).Timestamp, Is.EqualTo(12345),
                "the pong echoes the ping's timestamp; that echo is what the server's " +
                "round-trip measurement is built from");
        }

        [Test]
        public void AKickFollowedByItsPairedDisconnect_ClosesOnceWithTheKickCause()
        {
            var (conn, _) = Connect(
                Inbound(MsgType.Kick, "{\"reason\":\"evicted-by-newer-login\"}"),
                Inbound(MsgType.Disconnect, "{\"reason\":\"bye\"}"));

            var closes = new List<DisconnectInfo>();
            conn.Closed += info => closes.Add(info);

            conn.Start();

            // The server sends kick-then-disconnect as one eviction. Reporting both
            // surfaces every eviction twice, and the SECOND report would carry the wrong
            // cause — the generic disconnect instead of the kick the player should see.
            Assert.That(closes, Has.Count.EqualTo(1));
            Assert.That(closes[0].Cause, Is.EqualTo(DisconnectCause.Kicked));
            Assert.That(closes[0].Reason, Is.EqualTo("evicted-by-newer-login"));
        }

        [Test]
        public void AFrameArrivingAfterCloseIsNotDeliveredToTheConsumer()
        {
            var (conn, _) = Connect(
                Inbound(MsgType.Kick, "{\"reason\":\"gone\"}"),
                Snapshot(9));

            var delivered = 0;
            conn.FrameReceived += _ => delivered++;

            conn.Start();

            Assert.That(delivered, Is.Zero,
                "a snapshot decoded after the eviction was handed to a consumer that " +
                "believes the session is over — whatever it does with it is wrong");
        }
    }
}
