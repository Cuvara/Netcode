using System;
using System.Collections;
using System.Collections.Generic;
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
    /// A graceful leave delivers its disconnect frame before the transport is closed: written by
    /// the write loop, then flushed to an acknowledgement, then closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Leave</c> used to queue the frame and close in the same call. Closing cancelled the
    /// write loop before it ran, so the frame was never written at all, and the server saw the
    /// client vanish: both rpg-factory testlab S08G runs ended on its KCP dead-link timeout. The
    /// transport half (a written frame survives a lost datagram only if the session stays up for
    /// the flush) is <c>KcpCloseFlushTests</c>, headless.
    /// </para>
    /// <para>
    /// Coroutines, bounded by time, for the reason <c>WireConnectionDispatchTests</c> gives: the
    /// write loop resumes on Unity's synchronization context, which only the Editor pumps.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class WireConnectionLeaveTests
    {
        private static readonly JsonWireCodec Codec = new JsonWireCodec();

        /// <summary>Records writes and the close, in order. Cannot tell when a write is delivered, as TCP cannot.</summary>
        private class RecordingTransport : ITransport
        {
            private readonly UniTaskCompletionSource<byte[]> _parked = new UniTaskCompletionSource<byte[]>();
            public readonly List<string> Events = new List<string>();

            public string RemoteEndPoint => "recording";
            public bool IsConnected => true;
            public UniTask ConnectAsync(string host, int port, CancellationToken ct) => UniTask.CompletedTask;
            public UniTask<byte[]> ReadFrameAsync(CancellationToken ct) => _parked.Task;

            public UniTask WriteFrameAsync(byte[] body, CancellationToken ct)
            {
                Events.Add("write:" + Codec.DecodeBody(body).Type);
                return UniTask.CompletedTask;
            }

            public void Close()
            {
                if (!Events.Contains("close")) Events.Add("close");
            }

            public void Dispose() { }
        }

        /// <summary>Also records the flush that waits for the acknowledgement, as KCP does.</summary>
        private sealed class FlushingTransport : RecordingTransport, IFlushableTransport
        {
            public bool Acknowledge = true;

            public async UniTask<bool> FlushAsync(CancellationToken ct)
            {
                Events.Add("flush");

                // An acknowledgement takes a little time on a real link.
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
                Events.Add(Acknowledge ? "acked" : "timeout");
                return Acknowledge;
            }
        }

        private sealed class SilentLog : INetLog
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception exception = null) { }
        }

        private static WireConnection Started(RecordingTransport transport)
        {
            var conn = new WireConnection("test", transport, Codec, new NetworkSettings(), new SilentLog());
            conn.Start();
            return conn;
        }

        private static IEnumerator UntilClosed(RecordingTransport transport)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!transport.Events.Contains("close") && deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator ALeave_WritesTheDisconnect_BeforeTheTransportCloses()
        {
            var transport = new RecordingTransport();
            var conn = Started(transport);

            conn.Leave("bye");
            yield return UntilClosed(transport);

            TestContext.WriteLine(string.Join(" -> ", transport.Events));
            Assert.That(transport.Events, Is.EqualTo(new[] { "write:" + MsgType.Disconnect, "close" }),
                "the disconnect frame reaches the transport, and only then is it closed");
            Assert.That(conn.CloseInfo?.Cause, Is.EqualTo(DisconnectCause.LocalClose));
        }

        [UnityTest]
        public IEnumerator ALeave_WaitsForTheDisconnectToBeAcknowledged_ThenCloses()
        {
            var transport = new FlushingTransport();
            var conn = Started(transport);

            conn.Leave("bye");
            yield return UntilClosed(transport);

            TestContext.WriteLine(string.Join(" -> ", transport.Events));
            Assert.That(transport.Events, Is.EqualTo(new[] { "write:" + MsgType.Disconnect, "flush", "acked", "close" }),
                "written, flushed to an acknowledgement, and only then closed");
            Assert.That(conn.LeaveAcknowledged, Is.True);
        }

        [UnityTest]
        public IEnumerator FramesQueuedBeforeTheLeave_GoFirst_AndNothingFollowsTheDisconnect()
        {
            var transport = new FlushingTransport();
            var conn = Started(transport);

            conn.Send(MsgType.Resync, new ResyncRequest());
            conn.Leave("bye");
            conn.Send(MsgType.Resync, new ResyncRequest()); // after the goodbye: dropped
            yield return UntilClosed(transport);

            TestContext.WriteLine(string.Join(" -> ", transport.Events));
            Assert.That(transport.Events,
                Is.EqualTo(new[] { "write:" + MsgType.Resync, "write:" + MsgType.Disconnect, "flush", "acked", "close" }));
        }

        [UnityTest]
        public IEnumerator AnUnacknowledgedLeave_StillCloses()
        {
            var transport = new FlushingTransport { Acknowledge = false };
            var conn = Started(transport);

            conn.Leave("bye");
            yield return UntilClosed(transport);

            Assert.That(transport.Events, Is.EqualTo(new[] { "write:" + MsgType.Disconnect, "flush", "timeout", "close" }));
            Assert.That(conn.LeaveAcknowledged, Is.False);
        }

        [Test]
        public void ALeaveBeforeStart_ClosesAtOnce()
        {
            var transport = new RecordingTransport();
            var conn = new WireConnection("test", transport, Codec, new NetworkSettings(), new SilentLog());

            conn.Leave("bye");

            Assert.That(transport.Events, Is.EqualTo(new[] { "close" }), "no running loop, nothing to deliver");
        }
    }
}
