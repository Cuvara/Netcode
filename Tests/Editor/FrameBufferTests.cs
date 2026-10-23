using System;
using System.Collections.Generic;
using Cuvara.Netcode.Transport;
using NUnit.Framework;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The receive-side framing state machine, in isolation from any socket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the half of <see cref="TcpTransport"/> that decides how many awaits a
    /// frame costs, and it was previously unreachable from a test: every path into it
    /// went through a <c>UniTask</c>, and <c>UniTask</c> needs <c>UnityEngine</c>.
    /// </para>
    /// <para>
    /// These tests are plain C# on purpose — they compile and run under
    /// <c>dotnet test</c> from <c>Tests~/Headless/</c> as well as in the Editor, so a
    /// change to the framing rules is caught on every PR rather than only where a Unity
    /// licence is available.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class FrameBufferTests
    {
        private static byte[] Body(int length, byte seed)
        {
            var body = new byte[length];
            for (var i = 0; i < length; i++)
            {
                body[i] = (byte)(seed + i);
            }

            return body;
        }

        private static byte[] Frame(byte[] body)
        {
            var frame = new byte[WireFraming.HeaderSize + body.Length];
            WireFraming.WriteLength(frame, body.Length);
            Buffer.BlockCopy(body, 0, frame, WireFraming.HeaderSize, body.Length);
            return frame;
        }

        /// <summary>Writes <paramref name="bytes"/> into the buffer as one socket read would.</summary>
        private static void Deliver(FrameBuffer buffer, byte[] bytes, int offset, int count)
        {
            var free = buffer.ReserveForRead();
            Assert.That(free.Count, Is.GreaterThan(0),
                "ReserveForRead handed out no room; a zero-length read reads back as a clean EOF");
            var n = Math.Min(count, free.Count);
            Buffer.BlockCopy(bytes, offset, free.Array, free.Offset, n);
            buffer.Commit(n);
        }

        private static void Deliver(FrameBuffer buffer, byte[] bytes)
        {
            Deliver(buffer, bytes, 0, bytes.Length);
        }

        [Test]
        public void EmptyBufferYieldsNoFrame()
        {
            var buffer = new FrameBuffer();
            Assert.That(buffer.TryTakeFrame(), Is.Null);
            Assert.That(buffer.Buffered, Is.Zero);
        }

        [Test]
        public void OneWholeFrameIsReturnedIntact()
        {
            var buffer = new FrameBuffer();
            var body = Body(37, 3);
            Deliver(buffer, Frame(body));

            Assert.That(buffer.TryTakeFrame(), Is.EqualTo(body).AsCollection);
            Assert.That(buffer.TryTakeFrame(), Is.Null);
            Assert.That(buffer.Buffered, Is.Zero);
        }

        /// <summary>
        /// The property the read loop's throughput rests on: bytes already in hand
        /// produce frames with no further reads at all.
        /// </summary>
        /// <remarks>
        /// This is the direct, non-statistical form of what
        /// <see cref="TransportReadPumpTests"/> measures over time. One socket read costs
        /// one player-loop frame; if this ever needs a read per frame again, the read loop
        /// is capped at the player-loop rate divided by the awaits it pays.
        /// </remarks>
        [Test]
        public void ManyFramesInOneReadCostNoFurtherReads()
        {
            const int frameCount = 10;
            var bodies = new List<byte[]>();
            var wire = new List<byte>();
            for (var i = 0; i < frameCount; i++)
            {
                var body = Body(64 + i, (byte)i);
                bodies.Add(body);
                wire.AddRange(Frame(body));
            }

            var buffer = new FrameBuffer();
            Deliver(buffer, wire.ToArray());

            // Exactly one read happened above. Everything below is answered from memory.
            for (var i = 0; i < frameCount; i++)
            {
                var taken = buffer.TryTakeFrame();
                Assert.That(taken, Is.Not.Null, $"frame {i} needed a second socket read");
                Assert.That(taken, Is.EqualTo(bodies[i]).AsCollection, $"frame {i} came back altered");
            }

            Assert.That(buffer.TryTakeFrame(), Is.Null);
            Assert.That(buffer.Buffered, Is.Zero);
        }

        [Test]
        public void FrameSplitAcrossReadsIsReassembled()
        {
            var body = Body(200, 11);
            var frame = Frame(body);
            var buffer = new FrameBuffer();

            for (var offset = 0; offset < frame.Length; offset += 7)
            {
                Assert.That(buffer.TryTakeFrame(), Is.Null, "a frame appeared before all of its bytes arrived");
                Deliver(buffer, frame, offset, Math.Min(7, frame.Length - offset));
            }

            Assert.That(buffer.TryTakeFrame(), Is.EqualTo(body).AsCollection);
        }

        [Test]
        public void HeaderSplitAcrossReadsIsReassembled()
        {
            var body = Body(9, 5);
            var frame = Frame(body);
            var buffer = new FrameBuffer();

            Deliver(buffer, frame, 0, 1);
            Assert.That(buffer.TryTakeFrame(), Is.Null);
            Deliver(buffer, frame, 1, 2);
            Assert.That(buffer.TryTakeFrame(), Is.Null);
            Deliver(buffer, frame, 3, frame.Length - 3);

            Assert.That(buffer.TryTakeFrame(), Is.EqualTo(body).AsCollection);
        }

        /// <summary>
        /// A trailing partial frame must survive the compaction that the next read does,
        /// which is where an off-by-one silently corrupts every following frame rather
        /// than throwing.
        /// </summary>
        [Test]
        public void PartialTailSurvivesCompaction()
        {
            var first = Body(50, 1);
            var second = Body(80, 2);
            var wire = new List<byte>();
            wire.AddRange(Frame(first));
            wire.AddRange(Frame(second));
            var bytes = wire.ToArray();

            // Everything except the last 20 bytes of the second frame.
            var buffer = new FrameBuffer();
            Deliver(buffer, bytes, 0, bytes.Length - 20);

            Assert.That(buffer.TryTakeFrame(), Is.EqualTo(first).AsCollection);
            Assert.That(buffer.TryTakeFrame(), Is.Null);

            Deliver(buffer, bytes, bytes.Length - 20, 20);
            Assert.That(buffer.TryTakeFrame(), Is.EqualTo(second).AsCollection);
        }

        [Test]
        public void BufferGrowsForABodyLargerThanItsCapacity()
        {
            const int capacity = 64;
            var body = Body(500, 7);
            var frame = Frame(body);

            var buffer = new FrameBuffer(capacity);
            Assert.That(buffer.Capacity, Is.EqualTo(capacity));

            var offset = 0;
            while (offset < frame.Length)
            {
                var free = buffer.ReserveForRead();
                Assert.That(free.Count, Is.GreaterThan(0),
                    "the buffer refused to make room for a frame larger than its capacity");
                var n = Math.Min(free.Count, frame.Length - offset);
                Buffer.BlockCopy(frame, offset, free.Array, free.Offset, n);
                buffer.Commit(n);
                offset += n;
            }

            Assert.That(buffer.Capacity, Is.GreaterThanOrEqualTo(WireFraming.HeaderSize + body.Length));
            Assert.That(buffer.TryTakeFrame(), Is.EqualTo(body).AsCollection);
        }

        [Test]
        public void ReserveNeverHandsOutAZeroLengthRegion()
        {
            // A buffer holding one incomplete frame that fills it exactly is the case
            // where a non-growing reader would ask the socket for zero bytes and read
            // the reply as a clean EOF.
            const int capacity = 64;
            var body = Body(capacity * 2, 9);
            var frame = Frame(body);

            var buffer = new FrameBuffer(capacity);
            Deliver(buffer, frame, 0, capacity);
            Assert.That(buffer.Buffered, Is.EqualTo(capacity));

            var free = buffer.ReserveForRead();
            Assert.That(free.Count, Is.GreaterThan(0));
        }

        [Test]
        public void ZeroLengthPrefixIsRejected()
        {
            var buffer = new FrameBuffer();
            Deliver(buffer, new byte[] { 0, 0, 0, 0, 1, 2, 3, 4 });
            Assert.Throws<TransportException>(() => buffer.TryTakeFrame());
        }

        [Test]
        public void OversizedLengthPrefixIsRejected()
        {
            var buffer = new FrameBuffer();
            var header = new byte[WireFraming.HeaderSize];
            WireFraming.WriteLength(header, WireFraming.MaxBodySize + 1);
            Deliver(buffer, header);
            Assert.Throws<TransportException>(() => buffer.TryTakeFrame());
        }

        /// <summary>
        /// A prefix with the high bit set decodes negative rather than becoming a huge
        /// allocation, and must be refused before anything sizes a buffer from it.
        /// </summary>
        [Test]
        public void NegativeLengthPrefixIsRejected()
        {
            var buffer = new FrameBuffer();
            Deliver(buffer, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
            Assert.Throws<TransportException>(() => buffer.TryTakeFrame());

            // And it must not have been used to size a growth either.
            Assert.That(buffer.Capacity, Is.EqualTo(FrameBuffer.DefaultCapacity));
        }

        [Test]
        public void CapacitySmallerThanTheHeaderIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new FrameBuffer(WireFraming.HeaderSize - 1));
        }

        [Test]
        public void CommittingMoreThanWasReservedIsRefused()
        {
            var buffer = new FrameBuffer(64);
            buffer.ReserveForRead();
            Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Commit(65));
            Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Commit(-1));
        }
    }
}
