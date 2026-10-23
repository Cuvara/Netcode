using System;
using System.Collections.Generic;
using Cuvara.Netcode.Transport;
using NUnit.Framework;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The measurement the transport never had: <b>given a socket delivering N frames
    /// per second and a read loop that may resume once per player-loop frame, how many
    /// frames reach the consumer?</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is real here and what is modelled.</b> The frame parsing under test is
    /// the production <see cref="FrameBuffer"/>, byte for byte — nothing about framing,
    /// compaction or growth is reimplemented. What is modelled is the <i>scheduler</i>:
    /// <c>Task.AsUniTask()</c> posts its continuation to Unity's
    /// <c>SynchronizationContext</c>, which is drained once per player-loop frame from a
    /// snapshot taken at the start of the drain, so an await costs a whole player-loop
    /// frame even when the bytes are already in the socket buffer. The pump below
    /// reproduces exactly that rule and nothing else: <b>at most one socket read per
    /// tick, unlimited synchronous frame extraction per tick.</b>
    /// </para>
    /// <para>
    /// That rule makes the throughput ceiling arithmetic: a read loop's rate is
    /// <c>playerLoopHz / awaitsPerFrame</c>. It is the whole content of the defect in
    /// Cuvara/IndieRPGMMOAdventure#50 — an exact header read plus an exact body read is
    /// two awaits per frame, so the loop caps at half the player-loop rate: 10.0/s at 20
    /// fps and 5.0/s at 10 fps, with the socket backlog growing without bound below the
    /// knee. Harmless at desktop frame rates, squarely in the way on Android.
    /// </para>
    /// <para>
    /// <see cref="ExactReadStrategy"/> is that defect, kept permanently as a
    /// <b>control</b>. Without it, a green run here would be indistinguishable from a
    /// measurement that cannot fail: the control is what proves this harness detects the
    /// ceiling when the ceiling is present.
    /// </para>
    /// <para>
    /// Plain C#: runs under <c>dotnet test</c> from <c>Tests~/Headless/</c> as well as in
    /// the Editor. What still needs Unity is the awaiting itself — <c>TcpTransport</c>'s
    /// <c>UniTask</c> signatures, TLS handshake and cancellation-by-close — and that is
    /// covered by <c>SealedTransportTests</c> and <c>GatewayTlsTests</c> in the Editor.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class TransportReadPumpTests
    {
        /// <summary>Snapshot-sized bodies, so buffer occupancy is representative.</summary>
        private const int BodySize = 120;

        /// <summary>The game server's world-tick rate, which is what the client must keep up with.</summary>
        private const double ServerHz = 15.0;

        private const double Seconds = 20.0;

        // ------------------------------------------------------------------
        // Harness
        // ------------------------------------------------------------------

        /// <summary>A byte stream with a backlog: whatever the server wrote and the reader has not taken.</summary>
        private sealed class ByteStreamSocket
        {
            private readonly List<byte> _pending = new List<byte>();

            public int Backlog => _pending.Count;

            public void WriteFrame(int index)
            {
                var frame = new byte[WireFraming.HeaderSize + BodySize];
                WireFraming.WriteLength(frame, BodySize);
                frame[WireFraming.HeaderSize + 0] = (byte)(index >> 24);
                frame[WireFraming.HeaderSize + 1] = (byte)(index >> 16);
                frame[WireFraming.HeaderSize + 2] = (byte)(index >> 8);
                frame[WireFraming.HeaderSize + 3] = (byte)index;
                _pending.AddRange(frame);
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                var n = Math.Min(count, _pending.Count);
                if (n <= 0)
                {
                    return 0;
                }

                _pending.CopyTo(0, buffer, offset, n);
                _pending.RemoveRange(0, n);
                return n;
            }
        }

        private static int IndexOf(byte[] body)
        {
            return (body[0] << 24) | (body[1] << 16) | (body[2] << 8) | body[3];
        }

        /// <summary>
        /// One player-loop frame's worth of read-loop progress: everything that can be
        /// produced without touching the socket, then the frame's single await.
        /// </summary>
        private interface IReadStrategy
        {
            void Pump(ByteStreamSocket socket, List<byte[]> sink);
        }

        /// <summary>The shipping reader: production <see cref="FrameBuffer"/>, one read per tick.</summary>
        private sealed class BufferedReadStrategy : IReadStrategy
        {
            private readonly FrameBuffer _buffer = new FrameBuffer();

            public void Pump(ByteStreamSocket socket, List<byte[]> sink)
            {
                // ReadFrameAsync returns synchronously for every frame already in the
                // buffer, and a UniTask that completes synchronously never hops the
                // player loop — so these cost no tick.
                byte[] body;
                while ((body = _buffer.TryTakeFrame()) != null)
                {
                    sink.Add(body);
                }

                // The one await this tick buys.
                var free = _buffer.ReserveForRead();
                var read = socket.Read(free.Array, free.Offset, free.Count);
                if (read > 0)
                {
                    _buffer.Commit(read);
                }
            }
        }

        /// <summary>
        /// The defect, as a control: read exactly the header, then exactly the body. Each
        /// is its own await, so a frame costs two player-loop ticks however many bytes are
        /// already sitting in the socket.
        /// </summary>
        private sealed class ExactReadStrategy : IReadStrategy
        {
            private readonly byte[] _header = new byte[WireFraming.HeaderSize];
            private byte[] _body;
            private byte[] _target;
            private int _have;

            public ExactReadStrategy()
            {
                _target = _header;
            }

            public void Pump(ByteStreamSocket socket, List<byte[]> sink)
            {
                var read = socket.Read(_target, _have, _target.Length - _have);
                _have += read;
                if (_have < _target.Length)
                {
                    return;
                }

                if (ReferenceEquals(_target, _header))
                {
                    var length = WireFraming.ReadLength(_header);
                    if (!WireFraming.IsValidLength(length))
                    {
                        throw new TransportException($"invalid frame length: {length}");
                    }

                    _body = new byte[length];
                    _target = _body;
                    _have = 0;
                    return;
                }

                sink.Add(_body);
                _target = _header;
                _have = 0;
            }
        }

        private sealed class PumpResult
        {
            public int Written;
            public int Delivered;
            public int BacklogAtHalfTime;
            public int BacklogAtEnd;
            public double DeliveredPerSecond => Delivered / Seconds;
        }

        /// <summary>
        /// Runs <paramref name="strategy"/> for <see cref="Seconds"/> of virtual time at
        /// <paramref name="loopHz"/> against a server writing at <see cref="ServerHz"/>,
        /// checking frame order and contents as they arrive.
        /// </summary>
        private static PumpResult Run(IReadStrategy strategy, double loopHz)
        {
            var socket = new ByteStreamSocket();
            var sink = new List<byte[]>();
            var result = new PumpResult();

            var ticks = (int)Math.Round(Seconds * loopHz);
            var dt = 1.0 / loopHz;
            var writePeriod = 1.0 / ServerHz;
            var time = 0.0;
            var nextWrite = 0.0;

            for (var tick = 0; tick < ticks; tick++)
            {
                time += dt;
                while (nextWrite <= time)
                {
                    socket.WriteFrame(result.Written++);
                    nextWrite += writePeriod;
                }

                strategy.Pump(socket, sink);

                if (tick == ticks / 2)
                {
                    result.BacklogAtHalfTime = socket.Backlog;
                }
            }

            result.BacklogAtEnd = socket.Backlog;
            result.Delivered = sink.Count;

            for (var i = 0; i < sink.Count; i++)
            {
                Assert.That(sink[i].Length, Is.EqualTo(BodySize), $"frame {i} came back the wrong size");
                Assert.That(IndexOf(sink[i]), Is.EqualTo(i), $"frame {i} arrived out of order or altered");
            }

            return result;
        }

        // ------------------------------------------------------------------
        // The control: the harness must see the ceiling when the ceiling is there
        // ------------------------------------------------------------------

        /// <summary>
        /// Two awaits per frame caps the read loop at half the player-loop rate. These are
        /// the numbers from the live investigation, reproduced with no socket and no Unity.
        /// </summary>
        /// <remarks>
        /// This test failing means the harness has stopped being able to detect the defect,
        /// which would silently turn every other test in this fixture into a measurement of
        /// nothing.
        /// </remarks>
        [TestCase(60.0, 15.00)] // server-limited: the ceiling (30/s) is above the server
        [TestCase(30.0, 15.00)] // exactly at the knee
        [TestCase(20.0, 10.00)]
        [TestCase(10.0, 5.00)]
        [TestCase(5.0, 2.50)]
        public void ControlTwoAwaitsPerFrameCapsAtHalfTheLoopRate(double loopHz, double expectedPerSecond)
        {
            var result = Run(new ExactReadStrategy(), loopHz);

            Assert.That(result.DeliveredPerSecond, Is.EqualTo(expectedPerSecond).Within(0.25),
                $"an exact-read reader at {loopHz} fps should deliver {expectedPerSecond}/s " +
                $"(min(serverHz, loopHz/2)); it delivered {result.DeliveredPerSecond:0.00}/s");
        }

        /// <summary>Below the knee the defect does not merely slow down — it falls behind for good.</summary>
        [Test]
        public void ControlBacklogGrowsWithoutBoundBelowTheKnee()
        {
            var result = Run(new ExactReadStrategy(), 20.0);

            Assert.That(result.BacklogAtEnd, Is.GreaterThan(result.BacklogAtHalfTime + 10 * BodySize),
                "the control is supposed to fall permanently behind at 20 fps; if it is keeping up, " +
                "the harness is not modelling one-resumption-per-tick and measures nothing");
        }

        // ------------------------------------------------------------------
        // The shipping reader
        // ------------------------------------------------------------------

        /// <summary>
        /// The buffered reader holds the server's full rate all the way down to 5 fps,
        /// because bytes already in hand become frames with no await at all.
        /// </summary>
        /// <remarks>
        /// 5 fps is four times below the old knee and well past any frame rate a shipping
        /// client should see. Reintroducing an await per frame boundary fails this at
        /// every rate below 30 fps.
        /// </remarks>
        [TestCase(60.0)]
        [TestCase(30.0)]
        [TestCase(26.0)]
        [TestCase(20.0)]
        [TestCase(10.0)]
        [TestCase(5.0)]
        public void BufferedReaderKeepsUpWithTheServerAtEveryFrameRate(double loopHz)
        {
            var result = Run(new BufferedReadStrategy(), loopHz);

            // Continuations resume one tick after the read, so at most one tick's
            // production can still be in flight when the clock stops.
            var inFlight = (int)Math.Ceiling(ServerHz / loopHz) + 1;

            Assert.That(result.Delivered, Is.GreaterThanOrEqualTo(result.Written - inFlight),
                $"at {loopHz} fps the reader delivered {result.Delivered} of {result.Written} frames " +
                $"({result.DeliveredPerSecond:0.00}/s against a {ServerHz}/s server) — it is not keeping up");
            Assert.That(result.DeliveredPerSecond, Is.GreaterThanOrEqualTo(ServerHz - 0.25));
        }

        /// <summary>
        /// The backlog is steady rather than growing: keeping up is not the same as
        /// draining slower than the server writes, and only the second one accumulates.
        /// </summary>
        [TestCase(20.0)]
        [TestCase(10.0)]
        [TestCase(5.0)]
        public void BufferedReaderBacklogDoesNotGrow(double loopHz)
        {
            var result = Run(new BufferedReadStrategy(), loopHz);

            Assert.That(result.BacklogAtEnd, Is.LessThanOrEqualTo(result.BacklogAtHalfTime + BodySize),
                $"at {loopHz} fps the socket backlog grew from {result.BacklogAtHalfTime} to " +
                $"{result.BacklogAtEnd} bytes over the second half of the run");
        }

        /// <summary>
        /// The two readers side by side at the frame rate where it mattered. Stated as one
        /// assertion because the defect is a difference, not an absolute number: a change
        /// that slows the buffered reader to the control's rate is the regression.
        /// </summary>
        [Test]
        public void BufferedReaderBeatsTheCeilingTheControlHits()
        {
            const double loopHz = 20.0;
            var buffered = Run(new BufferedReadStrategy(), loopHz);
            var exact = Run(new ExactReadStrategy(), loopHz);

            Assert.That(exact.DeliveredPerSecond, Is.EqualTo(loopHz / 2.0).Within(0.25),
                "the control did not hit the documented playerLoopHz/2 ceiling");
            Assert.That(buffered.DeliveredPerSecond, Is.GreaterThan(exact.DeliveredPerSecond * 1.4),
                $"the buffered reader delivered {buffered.DeliveredPerSecond:0.00}/s against the " +
                $"control's {exact.DeliveredPerSecond:0.00}/s — it is paying an await per frame again");
        }

        /// <summary>
        /// A stalled player loop costs only the frames the stall itself spans: the ceiling
        /// is a function of awaits per frame, not of frame-time jitter. The live
        /// investigation found the same — injected jitter cost nothing while the mean rate
        /// stayed above the knee.
        /// </summary>
        [Test]
        public void BufferedReaderSurvivesAStalledPlayerLoop()
        {
            var socket = new ByteStreamSocket();
            var sink = new List<byte[]>();
            var strategy = new BufferedReadStrategy();

            var time = 0.0;
            var nextWrite = 0.0;
            var written = 0;

            // 300 ticks at a nominal 20 fps, with every tenth tick stalled for 200 ms —
            // four whole server frames arriving between two resumptions.
            for (var tick = 0; tick < 300; tick++)
            {
                time += tick % 10 == 0 ? 0.200 : 1.0 / 20.0;
                while (nextWrite <= time)
                {
                    socket.WriteFrame(written++);
                    nextWrite += 1.0 / ServerHz;
                }

                strategy.Pump(socket, sink);
            }

            Assert.That(sink.Count, Is.GreaterThanOrEqualTo(written - 5),
                $"delivered {sink.Count} of {written} frames across a stalling player loop");
            for (var i = 0; i < sink.Count; i++)
            {
                Assert.That(IndexOf(sink[i]), Is.EqualTo(i), $"frame {i} arrived out of order after a stall");
            }
        }
    }
}
