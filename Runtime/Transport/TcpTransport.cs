using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// TCP implementation of <see cref="ITransport"/> with the
    /// <c>[4-byte big-endian length][body]</c> framing both servers use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One reader and one writer at a time: <c>WireConnection</c> runs exactly one
    /// read loop and one write loop, so nothing here locks. Calling
    /// <see cref="ReadFrameAsync"/> twice concurrently would interleave two partial
    /// frames and is a programming error, not a supported mode.
    /// </para>
    /// <para>
    /// Continuations resume on the calling context (the Unity main thread, when the
    /// loops are started from it) — the awaits are on asynchronous socket
    /// operations, so nothing blocks that thread, and events therefore reach
    /// consumers where they can touch the scene.
    /// </para>
    /// <para>
    /// <see cref="System.Net.Sockets"/> is unavailable on WebGL. A WebGL build needs
    /// a WebSocket transport behind this same interface; see <c>docs/NETCODE.md</c>.
    /// </para>
    /// </remarks>
    public sealed class TcpTransport : ITransport
    {
        /// <summary>
        /// Receive buffer. Frames are parsed out of it without touching the socket, so
        /// one await can yield many frames.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this is buffered rather than two exact reads per frame.</b> Every
        /// <c>await</c> here goes through <c>Task.AsUniTask()</c>, which schedules its
        /// continuation with <c>TaskScheduler.FromCurrentSynchronizationContext()</c> —
        /// Unity's context, drained once per player-loop frame from a snapshot taken at
        /// the start of the drain. So an await costs a whole player-loop frame even when
        /// the bytes are already in the socket buffer, and the read loop's ceiling is
        /// <c>playerLoopHz / awaitsPerFrame</c>.
        /// </para>
        /// <para>
        /// Measured, outside Unity, against a 15/s server with the player loop's
        /// snapshot-drain semantics reproduced exactly: with a header read and a body
        /// read the ceiling was <b>exactly half the loop rate</b> — 15.00 frames/s at 30
        /// fps and above, 14.05 at 28 fps, 13.55 at 27 fps, 13.05 at 26 fps, 10.00 at 20
        /// fps, 5.00 at 10 fps, with the socket backlog growing without bound below the
        /// knee. It is a cliff, not a gradient: injected frame-time jitter (10-20% of
        /// frames stalled 30-100 ms) cost nothing while the mean rate stayed above the
        /// knee. With this buffer the same sweep held 15.0 frames/s down to <b>5 fps</b>,
        /// because a UniTask that completes synchronously never hops the player loop.
        /// </para>
        /// </remarks>
        private byte[] _receive = new byte[ReceiveBufferSize];
        private int _receiveStart;
        private int _receiveEnd;

        /// <summary>
        /// 16 KiB holds roughly a hundred snapshot frames, so a client that fell behind
        /// catches up in one player-loop frame instead of one frame per snapshot. It
        /// grows only for a body that does not fit, which the 1 MiB cap bounds.
        /// </summary>
        private const int ReceiveBufferSize = 16 * 1024;

        /// <summary>Reusable write buffer (header + body), grown with headroom and never shrunk.</summary>
        private byte[] _writeBuf = Array.Empty<byte>();

        private TcpClient _client;
        private NetworkStream _stream;
        private int _closed;

        public string RemoteEndPoint { get; private set; } = string.Empty;

        public bool IsConnected => _stream != null && Volatile.Read(ref _closed) == 0;

        public async UniTask ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            if (_client != null)
            {
                throw new TransportException("transport already connected");
            }

            var client = new TcpClient
            {
                // Input goes out once per client tick and is worthless late, so
                // Nagle's delay would be pure added latency.
                NoDelay = true
            };

            try
            {
                await client.ConnectAsync(host, port).AsUniTask().AttachExternalCancellation(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                client.Close();
                throw;
            }
            catch (Exception ex)
            {
                client.Close();
                throw new TransportException($"dial {host}:{port} failed: {ex.Message}", ex);
            }

            _client = client;
            _stream = client.GetStream();
            RemoteEndPoint = host + ":" + port;
        }

        public async UniTask<byte[]> ReadFrameAsync(CancellationToken cancellationToken)
        {
            var stream = RequireStream();

            while (true)
            {
                var buffered = _receiveEnd - _receiveStart;
                if (buffered >= WireFraming.HeaderSize)
                {
                    var length = WireFraming.ReadLength(_receive, _receiveStart);
                    if (!WireFraming.IsValidLength(length))
                    {
                        throw new TransportException($"invalid frame length: {length}");
                    }

                    if (buffered >= WireFraming.HeaderSize + length)
                    {
                        var body = new byte[length];
                        Buffer.BlockCopy(_receive, _receiveStart + WireFraming.HeaderSize, body, 0, length);
                        _receiveStart += WireFraming.HeaderSize + length;
                        if (_receiveStart == _receiveEnd)
                        {
                            _receiveStart = 0;
                            _receiveEnd = 0;
                        }

                        return body;
                    }

                    if (_receive.Length < WireFraming.HeaderSize + length)
                    {
                        // A frame larger than the default buffer. IsValidLength already
                        // bounded it at 1 MiB, so this cannot be driven by a peer into an
                        // unbounded allocation.
                        Array.Resize(ref _receive, WireFraming.HeaderSize + length);
                    }
                }

                if (_receiveStart > 0)
                {
                    Buffer.BlockCopy(_receive, _receiveStart, _receive, 0, buffered);
                    _receiveStart = 0;
                    _receiveEnd = buffered;
                }

                var read = await ReadSomeAsync(stream, _receive, _receiveEnd,
                    _receive.Length - _receiveEnd, cancellationToken);
                if (read == 0)
                {
                    if (buffered == 0)
                    {
                        return null; // clean EOF between frames
                    }

                    throw new TransportException("connection closed mid-frame");
                }

                _receiveEnd += read;
            }
        }

        public async UniTask WriteFrameAsync(byte[] body, CancellationToken cancellationToken)
        {
            if (body == null || body.Length == 0)
            {
                throw new TransportException("refusing to write an empty frame");
            }

            if (body.Length > WireFraming.MaxBodySize)
            {
                throw new TransportException($"frame of {body.Length} bytes exceeds the 1 MiB limit");
            }

            var stream = RequireStream();

            // One buffer, one write: a separate header write can be observed by the
            // peer as a frame that never arrives if the connection dies between the
            // two, and costs an extra segment on every tick.
            var frameLen = WireFraming.HeaderSize + body.Length;
            if (_writeBuf.Length < frameLen)
                _writeBuf = new byte[frameLen + (frameLen >> 2)];
            WireFraming.WriteLength(_writeBuf, body.Length);
            Buffer.BlockCopy(body, 0, _writeBuf, WireFraming.HeaderSize, body.Length);

            try
            {
                // No FlushAsync: NetworkStream.Flush is a documented no-op, and on the
                // Unity player loop that await was not free — the same measurement that
                // put the read path's ceiling at playerLoopHz/2 puts this path's at
                // playerLoopHz/2 as well, halved again by a flush that does nothing.
                await stream.WriteAsync(_writeBuf, 0, frameLen, cancellationToken).AsUniTask();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException)
            {
                throw new TransportException("write failed: " + ex.Message, ex);
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            try
            {
                _stream?.Close();
            }
            catch (Exception)
            {
                // Closing a link that is already gone is the normal case, not a fault.
            }

            try
            {
                _client?.Close();
            }
            catch (Exception)
            {
                // As above.
            }
        }

        public void Dispose() => Close();

        private NetworkStream RequireStream()
        {
            var stream = _stream;
            if (stream == null)
            {
                throw new TransportException("transport is not connected");
            }

            return stream;
        }

        /// <summary>
        /// One socket read. Deliberately not a read-exactly loop: every await costs a
        /// player-loop frame (see <see cref="_receive"/>), so the caller buffers whatever
        /// arrives and parses frames out of it rather than paying an await per frame
        /// boundary. Returns 0 on EOF.
        /// </summary>
        private static async UniTask<int> ReadSomeAsync(NetworkStream stream, byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            try
            {
                return await stream.ReadAsync(buffer, offset, count, cancellationToken).AsUniTask();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException)
            {
                throw new TransportException("read failed: " + ex.Message, ex);
            }
        }
    }
}
