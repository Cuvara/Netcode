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
        private readonly byte[] _header = new byte[WireFraming.HeaderSize];

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

            var headerRead = await ReadExactAsync(stream, _header, WireFraming.HeaderSize, cancellationToken);
            if (headerRead == 0)
            {
                return null; // clean EOF
            }

            if (headerRead < WireFraming.HeaderSize)
            {
                throw new TransportException("incomplete length header");
            }

            var length = WireFraming.ReadLength(_header);
            if (!WireFraming.IsValidLength(length))
            {
                throw new TransportException($"invalid frame length: {length}");
            }

            var body = new byte[length];
            var bodyRead = await ReadExactAsync(stream, body, length, cancellationToken);
            if (bodyRead < length)
            {
                throw new TransportException("incomplete frame body");
            }

            return body;
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
            var frame = new byte[WireFraming.HeaderSize + body.Length];
            WireFraming.WriteLength(frame, body.Length);
            Buffer.BlockCopy(body, 0, frame, WireFraming.HeaderSize, body.Length);

            try
            {
                await stream.WriteAsync(frame, 0, frame.Length, cancellationToken).AsUniTask();
                await stream.FlushAsync(cancellationToken).AsUniTask();
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

        private static async UniTask<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int count,
            CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < count)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, offset, count - offset, cancellationToken).AsUniTask();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException)
                {
                    throw new TransportException("read failed: " + ex.Message, ex);
                }

                if (read == 0)
                {
                    return offset; // EOF; 0 here means a clean close between frames
                }

                offset += read;
            }

            return offset;
        }
    }
}
