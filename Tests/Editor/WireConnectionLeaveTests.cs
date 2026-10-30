using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    /// A graceful leave delivers its disconnect frame before the transport is closed: written
    /// straight to the transport on the calling thread (after anything already in flight), then
    /// flushed to an acknowledgement, then closed.
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

            public virtual UniTask WriteFrameAsync(byte[] body, CancellationToken ct)
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

                // An acknowledgement takes a little time on a real link. Task.Yield, not a
                // player-loop yield: an edit-mode Editor pumps only the synchronization context.
                await Task.Yield();
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

        // Netcode #182: the player loop never runs again after Application.Quit. Plain [Test]s with
        // no pump or yield: whatever is recorded here was written on the calling thread.

        [Test]
        public void ALeave_WritesTheDisconnect_BeforeItReturns_WithoutAnyPump()
        {
            var transport = new RecordingTransport();
            var conn = Started(transport);

            conn.Leave("bye");

            Assert.That(transport.Events, Does.Contain("write:" + MsgType.Disconnect),
                "the disconnect is on the transport synchronously, not queued for the write loop");
        }

        [Test]
        public void ALeave_FlushesInTheSameCall_WithoutAnyPump()
        {
            var transport = new FlushingTransport();
            var conn = Started(transport);

            conn.Leave("bye");

            Assert.That(transport.Events, Is.EqualTo(new[] { "write:" + MsgType.Disconnect, "flush" }),
                "written, and the acknowledgement wait has begun, all before the first suspension");
        }

        [Test]
        public void FramesQueuedBeforeTheLeave_AreWrittenFirst_WithoutAnyPump()
        {
            var transport = new RecordingTransport();
            var conn = Started(transport);

            conn.Send(MsgType.Resync, new ResyncRequest());
            conn.Leave("bye");
            conn.Send(MsgType.Resync, new ResyncRequest()); // after the goodbye: dropped

            Assert.That(transport.Events,
                Is.EqualTo(new[] { "write:" + MsgType.Resync, "write:" + MsgType.Disconnect, "close" }));
        }

        [Test]
        public void ASecondLeave_SendsNoSecondDisconnect()
        {
            var transport = new RecordingTransport();
            var conn = Started(transport);

            conn.Leave("bye");
            conn.Leave("again");

            Assert.That(transport.Events, Is.EqualTo(new[] { "write:" + MsgType.Disconnect, "close" }));
        }

        /// <summary>
        /// Holds the first write open until released, and counts writes in flight. TCP behaves so:
        /// a write completes on a later player-loop frame, from one shared buffer.
        /// </summary>
        private sealed class SlowFirstWriteTransport : RecordingTransport
        {
            private readonly UniTaskCompletionSource _firstWrite = new UniTaskCompletionSource();
            private bool _firstStarted;
            public int InFlight;
            public int MaxInFlight;

            public bool FirstWriteStarted => _firstStarted;
            public void CompleteFirstWrite() => _firstWrite.TrySetResult();

            public override async UniTask WriteFrameAsync(byte[] body, CancellationToken ct)
            {
                Events.Add("write:" + Codec.DecodeBody(body).Type);
                MaxInFlight = Math.Max(MaxInFlight, ++InFlight);
                try
                {
                    if (!_firstStarted)
                    {
                        _firstStarted = true;
                        await _firstWrite.Task;
                    }
                }
                finally
                {
                    InFlight--;
                }
            }
        }

        [UnityTest]
        public IEnumerator ALeave_NeverWritesWhileTheWriteLoopIsMidWrite_AndTheDisconnectFollowsInOrder()
        {
            var transport = new SlowFirstWriteTransport();
            var conn = Started(transport);

            conn.Send(MsgType.Resync, new ResyncRequest());
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!transport.FirstWriteStarted && deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                yield return null;
            }

            Assert.That(transport.FirstWriteStarted, Is.True, "the write loop picked the frame up and is parked mid-write");

            conn.Leave("bye");

            Assert.That(transport.MaxInFlight, Is.EqualTo(1), "the leave must not start a second concurrent write");
            Assert.That(transport.Events, Is.EqualTo(new[] { "write:" + MsgType.Resync }),
                "the disconnect waits for the write in flight");

            transport.CompleteFirstWrite();
            yield return UntilClosed(transport);

            TestContext.WriteLine(string.Join(" -> ", transport.Events));
            Assert.That(transport.Events,
                Is.EqualTo(new[] { "write:" + MsgType.Resync, "write:" + MsgType.Disconnect, "close" }));
            Assert.That(transport.MaxInFlight, Is.EqualTo(1));
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
