using System;
using System.Collections;
using System.Collections.Generic;
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
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The heartbeat and RTT maths run on the monotonic clock
    /// (<see cref="NetworkSettings.MonotonicClock"/>); the wall clock only ever
    /// fills protocol timestamp fields. A phone that syncs its clock mid-session
    /// must not read as 30 s of silence.
    /// </summary>
    /// <remarks>
    /// Before 0.31.0 both used <c>DateTimeOffset.UtcNow</c>: a +1 h NTP step
    /// between two pings made the age of the last pong 3600 s and the link was
    /// declared dead on the next tick. The heartbeat pause is a seam the test
    /// completes itself; the only thing waited for is the write loop's
    /// player-loop hop that puts the ping on the transport (as in
    /// <c>WireConnectionDispatchTests</c>' pong test), bounded by wall-clock.
    /// </remarks>
    [TestFixture]
    public sealed class WireConnectionClockTests
    {
        private sealed class ParkingTransport : ITransport
        {
            private UniTaskCompletionSource<byte[]> _parked;
            public readonly List<byte[]> Written = new List<byte[]>();
            public string RemoteEndPoint => "scripted";
            public bool IsConnected => true;
            public UniTask ConnectAsync(string host, int port, CancellationToken ct) => UniTask.CompletedTask;

            public UniTask<byte[]> ReadFrameAsync(CancellationToken ct)
            {
                _parked = new UniTaskCompletionSource<byte[]>();
                return _parked.Task;
            }

            public UniTask WriteFrameAsync(byte[] body, CancellationToken ct)
            {
                Written.Add(body);
                return UniTask.CompletedTask;
            }

            public void Deliver(byte[] frame)
            {
                var p = _parked;
                _parked = null;
                p.TrySetResult(frame);
            }

            public void Close() { }
            public void Dispose() { }
        }

        private sealed class RecordingLog : INetLog
        {
            public readonly List<string> Lines = new List<string>();
            public void Info(string message) => Lines.Add("I " + message);
            public void Warn(string message) => Lines.Add("W " + message);
            public void Error(string message, Exception exception = null) =>
                Lines.Add("E " + message + (exception == null ? "" : " :: " + exception));
            public override string ToString() => string.Join("\n", Lines);
        }

        /// <summary>The heartbeat loop parks on this; the test releases one tick at a time.</summary>
        private sealed class HeartbeatGate
        {
            private UniTaskCompletionSource _next = new UniTaskCompletionSource();
            public int Waits;

            public UniTask Wait(TimeSpan delay, CancellationToken ct)
            {
                Waits++;
                return _next.Task;
            }

            public void Tick()
            {
                var current = _next;
                _next = new UniTaskCompletionSource();
                current.TrySetResult();
            }
        }

        private static byte[] Pong(long echoedTimestamp) =>
            Encoding.UTF8.GetBytes("{\"type\":" + (int)MsgType.Pong + ",\"payload\":{\"timestamp\":" +
                                   echoedTimestamp + ",\"server_time\":1}}");

        private static long PingTimestamp(byte[] body)
        {
            var json = Encoding.UTF8.GetString(body);
            var marker = "\"timestamp\":";
            var at = json.IndexOf(marker, StringComparison.Ordinal);
            Assert.That(at, Is.GreaterThanOrEqualTo(0), json);
            var start = at + marker.Length;
            var end = start;
            while (end < json.Length && char.IsDigit(json[end])) end++;
            return long.Parse(json.Substring(start, end - start));
        }

        private static IEnumerator WaitForWrites(ParkingTransport transport, int count, RecordingLog log)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (transport.Written.Count < count && DateTime.UtcNow < deadline) yield return null;
            Assert.That(transport.Written.Count, Is.EqualTo(count), "the ping never reached the transport\n" + log);
        }

        [UnityTest]
        public IEnumerator AWallClockJump_DoesNotDeclareTheLinkDead_AndRttStaysMonotonic()
        {
            long mono = 0;
            long wall = 1_700_000_000_000; // some epoch millisecond
            var gate = new HeartbeatGate();
            var settings = new NetworkSettings
            {
                PongTimeout = TimeSpan.FromSeconds(30),
                MonotonicClock = () => mono,
                HeartbeatScheduler = gate.Wait,
            };
            var transport = new ParkingTransport();
            var log = new RecordingLog();
            var conn = new WireConnection("test", transport, new JsonWireCodec(), settings, log)
            {
                WallClock = () => wall,
            };
            DisconnectInfo? closed = null;
            conn.Closed += info => closed = info;

            conn.Start();
            Assert.That(gate.Waits, Is.EqualTo(1), "the heartbeat loop must be parked on the seam\n" + log + "\n" + closed);

            // Tick 1: a ping goes out stamped with the wall clock.
            mono += 10_000;
            gate.Tick();
            yield return WaitForWrites(transport, 1, log);
            var stamp1 = PingTimestamp(transport.Written[0]);
            Assert.That(stamp1, Is.EqualTo(wall), "the protocol field carries wall-clock time");

            // The pong comes back 40 ms later on the monotonic clock. Meanwhile the
            // wall clock steps forward by an hour (NTP sync).
            mono += 40;
            wall += 3_600_000;
            transport.Deliver(Pong(stamp1));
            Assert.That(conn.RoundTripMs, Is.EqualTo(40), "RTT is monotonic, not wall-clock");
            Assert.That(closed, Is.Null);

            // Tick 2, ten monotonic seconds later: the last pong is 10 s old on the
            // clock that matters. On the wall clock it would look 3600 s old.
            mono += 10_000;
            gate.Tick();
            Assert.That(closed, Is.Null, "a wall-clock step must not read as silence");
            yield return WaitForWrites(transport, 2, log);

            // A stale echo (a pong for a ping we are no longer waiting on) must not
            // corrupt the RTT.
            transport.Deliver(Pong(stamp1));
            Assert.That(conn.RoundTripMs, Is.EqualTo(40));

            // The new ping is answered normally.
            var stamp2 = PingTimestamp(transport.Written[1]);
            Assert.That(stamp2, Is.EqualTo(wall), "the ping after the jump carries the new wall time");
            mono += 25;
            transport.Deliver(Pong(stamp2));
            Assert.That(conn.RoundTripMs, Is.EqualTo(25));

            // And real silence is still caught: 31 monotonic seconds without a pong.
            mono += 31_000;
            gate.Tick();
            Assert.That(closed, Is.Not.Null, "genuine silence must still time out");
            Assert.That(closed.Value.Cause, Is.EqualTo(DisconnectCause.HeartbeatTimeout));
            conn.Dispose();
        }

        [UnityTest]
        public IEnumerator AWallClockStepBackwards_DoesNotResetTheHeartbeatAge()
        {
            long mono = 0;
            long wall = 1_700_000_000_000;
            var gate = new HeartbeatGate();
            var settings = new NetworkSettings
            {
                PongTimeout = TimeSpan.FromSeconds(30),
                MonotonicClock = () => mono,
                HeartbeatScheduler = gate.Wait,
            };
            var transport = new ParkingTransport();
            var log = new RecordingLog();
            var conn = new WireConnection("test", transport, new JsonWireCodec(), settings, log)
            {
                WallClock = () => wall,
            };
            DisconnectInfo? closed = null;
            conn.Closed += info => closed = info;
            conn.Start();
            Assert.That(gate.Waits, Is.EqualTo(1), "the heartbeat loop must be parked on the seam\n" + log + "\n" + closed);

            // No pong ever arrives. The wall clock steps back an hour — on a
            // wall-clock implementation the silence would look negative and the
            // dead link would never be declared.
            wall -= 3_600_000;
            mono += 10_000;
            gate.Tick();
            yield return WaitForWrites(transport, 1, log);
            Assert.That(closed, Is.Null, log.ToString());
            mono += 21_000;
            gate.Tick();
            Assert.That(closed, Is.Not.Null, "31 monotonic seconds of silence is a dead link, whatever the wall clock says");
            Assert.That(closed.Value.Cause, Is.EqualTo(DisconnectCause.HeartbeatTimeout));
            conn.Dispose();
        }

        [Test]
        public void DefaultClocks_AreWiredToTheStopwatch()
        {
            var a = new NetworkSettings().MonotonicClock();
            Thread.Sleep(5);
            var b = new NetworkSettings().MonotonicClock();
            Assert.That(b, Is.GreaterThanOrEqualTo(a + 4));
            Assert.That(a, Is.LessThan(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000),
                "the monotonic clock is process-relative, not an epoch time");
        }
    }
}
