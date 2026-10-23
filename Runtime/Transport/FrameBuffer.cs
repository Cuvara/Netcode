using System;

namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// The receive-side framing state machine: a growable byte buffer that
    /// <c>[4-byte big-endian length][body]</c> frames are parsed out of, with no
    /// knowledge of sockets, awaits or schedulers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a separate class.</b> Every await in <see cref="TcpTransport"/>
    /// goes through <c>UniTask</c>, which needs <c>UnityEngine</c>, so the whole read
    /// path used to be reachable only from inside the Editor or a built player. The
    /// property that actually governs throughput is not in the awaiting — it is in
    /// <b>how many awaits a frame costs</b>, and that is decided entirely here, by
    /// whether a frame can be produced from bytes already in hand. Splitting the
    /// decision out of the awaiting puts it under <c>dotnet test</c>.
    /// </para>
    /// <para>
    /// <b>The ceiling this shape exists to avoid.</b> <c>Task.AsUniTask()</c> schedules
    /// its continuation on Unity's <c>SynchronizationContext</c>, which is drained once
    /// per player-loop frame from a snapshot taken at the start of the drain. An await
    /// therefore costs a whole player-loop frame even when the bytes are already in the
    /// socket buffer, and the read loop's ceiling is
    /// <c>playerLoopHz / awaitsPerFrame</c>. A reader that does an exact header read and
    /// an exact body read pays two awaits per frame and caps at <b>half the loop rate</b>
    /// — 10.0 frames/s at 20 fps, 5.0 at 10 fps, with the socket backlog growing without
    /// bound below the knee. Harmless on a desktop, squarely in the way on Android where
    /// 30 fps is a normal target.
    /// </para>
    /// <para>
    /// With this buffer, <see cref="TryTakeFrame"/> answers from memory for every frame
    /// that already arrived, so one await can yield many frames and the loop holds the
    /// server's rate far below the old knee. <c>TransportReadPumpTests</c> measures
    /// exactly that, headlessly, against a model of the player loop's drain semantics.
    /// </para>
    /// </remarks>
    public sealed class FrameBuffer
    {
        /// <summary>
        /// 16 KiB holds roughly a hundred snapshot frames, so a client that fell behind
        /// catches up in one player-loop frame instead of one frame per snapshot. It
        /// grows only for a body that does not fit, which the 1 MiB cap bounds.
        /// </summary>
        public const int DefaultCapacity = 16 * 1024;

        private byte[] _buffer;
        private int _start;
        private int _end;

        /// <summary>Creates a buffer of <see cref="DefaultCapacity"/> bytes.</summary>
        public FrameBuffer() : this(DefaultCapacity)
        {
        }

        /// <summary>
        /// Creates a buffer of <paramref name="capacity"/> bytes. Only tests need a size
        /// other than the default; the buffer grows on demand either way.
        /// </summary>
        public FrameBuffer(int capacity)
        {
            if (capacity < WireFraming.HeaderSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    $"a receive buffer smaller than the {WireFraming.HeaderSize}-byte header can never make progress");
            }

            _buffer = new byte[capacity];
        }

        /// <summary>Bytes held that have not yet been parsed into a frame.</summary>
        public int Buffered => _end - _start;

        /// <summary>Current buffer size, which only ever grows.</summary>
        public int Capacity => _buffer.Length;

        /// <summary>
        /// Returns the next complete frame body, or null when more bytes are needed.
        /// Null is "ask the socket", never "end of stream" — the caller distinguishes
        /// those, because only it can see a zero-length read.
        /// </summary>
        /// <exception cref="TransportException">
        /// The length prefix is not a usable length. This is a protocol error and is not
        /// recoverable by reading more: the stream is no longer known to be frame-aligned.
        /// </exception>
        public byte[] TryTakeFrame()
        {
            var buffered = _end - _start;
            if (buffered < WireFraming.HeaderSize)
            {
                return null;
            }

            var length = WireFraming.ReadLength(_buffer, _start);
            if (!WireFraming.IsValidLength(length))
            {
                throw new TransportException($"invalid frame length: {length}");
            }

            if (buffered < WireFraming.HeaderSize + length)
            {
                return null;
            }

            var body = new byte[length];
            Buffer.BlockCopy(_buffer, _start + WireFraming.HeaderSize, body, 0, length);
            _start += WireFraming.HeaderSize + length;
            if (_start == _end)
            {
                _start = 0;
                _end = 0;
            }

            return body;
        }

        /// <summary>
        /// Prepares for one socket read and returns the region to read into: compacting
        /// any partial frame to the front, and growing the buffer first if the frame
        /// being assembled cannot fit in it.
        /// </summary>
        /// <remarks>
        /// The returned segment always has a positive count. That is a property of the
        /// growth rule, not an accident: a buffer full of one incomplete frame is exactly
        /// the case that grows, and a count of zero would come back from the socket as a
        /// zero-length read, which the caller reads as a clean EOF — a hang or a spurious
        /// disconnect rather than a visible error.
        /// </remarks>
        public ArraySegment<byte> ReserveForRead()
        {
            var buffered = _end - _start;

            if (buffered >= WireFraming.HeaderSize)
            {
                var length = WireFraming.ReadLength(_buffer, _start);

                // IsValidLength already bounded this at 1 MiB in TryTakeFrame, so a peer
                // cannot drive it into an unbounded allocation. Re-checked rather than
                // assumed, because this method is public.
                if (WireFraming.IsValidLength(length) && _buffer.Length < WireFraming.HeaderSize + length)
                {
                    Array.Resize(ref _buffer, WireFraming.HeaderSize + length);
                }
            }

            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, buffered);
                _start = 0;
                _end = buffered;
            }

            return new ArraySegment<byte>(_buffer, _end, _buffer.Length - _end);
        }

        /// <summary>
        /// Accepts <paramref name="count"/> bytes written into the segment that
        /// <see cref="ReserveForRead"/> handed out.
        /// </summary>
        public void Commit(int count)
        {
            if (count < 0 || _end + count > _buffer.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count), $"cannot commit {count} bytes into {_buffer.Length - _end} of free space");
            }

            _end += count;
        }
    }
}
