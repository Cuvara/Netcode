using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// KCP-over-UDP implementation of <see cref="ITransport"/>, wire-compatible
    /// with the game server's <c>KcpListener</c> and with
    /// <c>github.com/xtaci/kcp-go/v5</c> as configured by
    /// <c>backend/shared/transport</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// KCP runs in <b>stream mode</b> — the same contract as TCP — so the
    /// identical <c>[4-byte big-endian length][body]</c> framing sits on top, and
    /// nothing above this class knows which transport it is on.
    /// </para>
    /// <para>
    /// One conversation over one UDP socket. The server creates the session the
    /// moment the first datagram arrives — KCP has no connection handshake.
    /// </para>
    /// <para>
    /// Three concurrent loops run once <see cref="ConnectAsync"/> returns:
    /// a receive loop reading datagrams from the socket and feeding them to the
    /// ARQ, an update loop driving the ARQ timer, and the application read path
    /// that drains reassembled bytes out of the ARQ.
    /// </para>
    /// <para>
    /// Encryption is optional. When a <c>transportKey</c> is provided, every
    /// outgoing datagram is AES-256-CFB encrypted and every incoming one is
    /// decrypted — the same kcp-go-compatible scheme the server uses. When absent,
    /// datagrams are plaintext — the dev default.
    /// </para>
    /// </remarks>
    public sealed class KcpTransport : ITransport
    {
        /// <summary>
        /// The KCP tuning profile. Every value MUST equal the constants in
        /// <c>GameServer.Net.Transport.KcpTuning</c> and
        /// <c>backend/shared/transport/transport.go</c>.
        /// </summary>
        private const int TuningNoDelay = 1;
        private const int TuningInterval = 10;
        private const int TuningResend = 2;
        private const int TuningNoCongestion = 1;
        private const int TuningSendWindow = 128;
        private const int TuningRecvWindow = 128;
        private const int TuningMtu = 1350;
        private const int MtuLimit = 1500;
        private const int IdleTimeoutMs = 60_000;

        /// <summary>
        /// Socket buffer size. Matches Go's <c>KCPSocketBuffer</c> and the server's
        /// constant. One UDP socket carries all traffic for this session; an
        /// undersized buffer costs throughput under burst.
        /// </summary>
        private const int SocketBufferBytes = 4 * 1024 * 1024;

        private static int _nextConv;

        private readonly string _transportKey;

        private UdpClient _udp;
        private Kcp _kcp;
        private KcpCrypto _crypto;
        private int _cryptoHeaderSize;
        private readonly object _kcpLock = new object();

        // Stream-mode receive buffer: KCP hands back chunks, the framing layer
        // needs arbitrary byte counts.
        private byte[] _streamBuf = new byte[16 * 1024];
        private int _streamStart;
        private int _streamEnd;

        // Scratch buffer for KCP.Recv.
        private readonly byte[] _recvScratch = new byte[TuningRecvWindow * TuningMtu];

        private CancellationTokenSource _cts;
        private int _closed;

        public string RemoteEndPoint { get; private set; } = string.Empty;

        public bool IsConnected => _udp != null && Volatile.Read(ref _closed) == 0;

        /// <summary>
        /// Creates a KCP transport with optional encryption.
        /// </summary>
        /// <param name="transportKey">
        /// The transport encryption key. Empty or null for plaintext (the dev default).
        /// Must match the server's <c>TRANSPORT_KEY</c> exactly.
        /// </param>
        public KcpTransport(string transportKey = null)
        {
            _transportKey = transportKey;
        }

        public async UniTask ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            if (_udp != null)
            {
                throw new TransportException("transport already connected");
            }

            // Resolve the host to get the address family right (IPv4 vs IPv6).
            IPAddress address;
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host).AsUniTask()
                    .AttachExternalCancellation(cancellationToken);
                if (addresses.Length == 0)
                    throw new TransportException($"cannot resolve {host}");
                address = addresses[0];
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TransportException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new TransportException($"resolve {host} failed: {ex.Message}", ex);
            }

            var remoteEp = new IPEndPoint(address, port);

            UdpClient udp = null;
            try
            {
                udp = new UdpClient(address.AddressFamily);

                // Best-effort large buffers — some sandboxes cap SO_RCVBUF.
                try { udp.Client.ReceiveBufferSize = SocketBufferBytes; } catch { }
                try { udp.Client.SendBufferSize = SocketBufferBytes; } catch { }

                // On Windows, suppress ICMP port-unreachable raising ConnectionReset.
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    const int SIO_UDP_CONNRESET = -1744830452;
                    try { udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[4], null); }
                    catch { }
                }

                udp.Connect(remoteEp);
            }
            catch (Exception ex) when (!(ex is TransportException))
            {
                udp?.Close();
                throw new TransportException($"dial {host}:{port} failed: {ex.Message}", ex);
            }

            _udp = udp;
            RemoteEndPoint = host + ":" + port;

            // Set up crypto if a key is provided.
            _crypto = KcpCrypto.TryCreate(_transportKey);
            _cryptoHeaderSize = _crypto != null ? KcpCrypto.HeaderSize : 0;

            // Pick a conversation id. kcp-go's Dial uses a monotonic counter.
            uint conv = (uint)Interlocked.Increment(ref _nextConv);

            _kcp = new Kcp(conv, (buf, size) =>
            {
                // Reserve room for the crypto header.
                var packet = new byte[_cryptoHeaderSize + size];
                Buffer.BlockCopy(buf, 0, packet, _cryptoHeaderSize, size);

                if (_crypto != null)
                    _crypto.Seal(packet, 0, packet.Length);

                try
                {
                    _udp?.Send(packet, packet.Length);
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            });

            // Apply the tuning profile.
            _kcp.Stream = 1;
            _kcp.SetNoDelay(TuningNoDelay, TuningInterval, TuningResend, TuningNoCongestion);
            _kcp.WndSize(TuningSendWindow, TuningRecvWindow);
            _kcp.SetMtu(TuningMtu - _cryptoHeaderSize);

            _cts = new CancellationTokenSource();

            // Start the receive and update loops.
            ReceiveLoopAsync(_cts.Token).Forget();
            UpdateLoopAsync(_cts.Token).Forget();

            // Send a tiny payload to make the server create the session. KCP has no
            // connection handshake — the server springs a session into existence on
            // the first datagram from an unknown endpoint, reading the conv from
            // the KCP header.
            lock (_kcpLock)
            {
                _kcp.Update();
            }
        }

        public async UniTask<byte[]> ReadFrameAsync(CancellationToken cancellationToken)
        {
            // Same framing as TcpTransport: [4-byte BE length][body] over the
            // KCP byte stream. The stream buffer holds reassembled bytes from
            // the ARQ; when it cannot satisfy a frame, we wait for more.
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _closed) != 0) return null;

                int buffered = _streamEnd - _streamStart;
                if (buffered >= WireFraming.HeaderSize)
                {
                    int length = WireFraming.ReadLength(_streamBuf, _streamStart);
                    if (!WireFraming.IsValidLength(length))
                    {
                        throw new TransportException($"invalid frame length: {length}");
                    }

                    if (buffered >= WireFraming.HeaderSize + length)
                    {
                        var body = new byte[length];
                        Buffer.BlockCopy(_streamBuf, _streamStart + WireFraming.HeaderSize, body, 0, length);
                        _streamStart += WireFraming.HeaderSize + length;
                        if (_streamStart == _streamEnd)
                        {
                            _streamStart = 0;
                            _streamEnd = 0;
                        }
                        return body;
                    }

                    // Ensure the buffer is large enough for this frame.
                    if (_streamBuf.Length < WireFraming.HeaderSize + length)
                    {
                        Array.Resize(ref _streamBuf, WireFraming.HeaderSize + length);
                    }
                }

                // Compact the buffer.
                if (_streamStart > 0 && buffered > 0)
                {
                    Buffer.BlockCopy(_streamBuf, _streamStart, _streamBuf, 0, buffered);
                    _streamStart = 0;
                    _streamEnd = buffered;
                }
                else if (_streamStart > 0)
                {
                    _streamStart = 0;
                    _streamEnd = 0;
                }

                // Try to drain from the ARQ.
                int received = DrainKcpToStream();
                if (received > 0) continue;

                // Nothing ready — wait a short interval for more datagrams.
                // UniTask.Delay with Realtime so paused/slow games still pump.
                try
                {
                    await UniTask.Delay(1, DelayType.Realtime, PlayerLoopTiming.Update, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }

        public UniTask WriteFrameAsync(byte[] body, CancellationToken cancellationToken)
        {
            if (body == null || body.Length == 0)
            {
                throw new TransportException("refusing to write an empty frame");
            }

            if (body.Length > WireFraming.MaxBodySize)
            {
                throw new TransportException($"frame of {body.Length} bytes exceeds the 1 MiB limit");
            }

            if (Volatile.Read(ref _closed) != 0)
            {
                throw new TransportException("transport is not connected");
            }

            // Build the framed message: [4-byte BE length][body].
            var frame = new byte[WireFraming.HeaderSize + body.Length];
            WireFraming.WriteLength(frame, body.Length);
            Buffer.BlockCopy(body, 0, frame, WireFraming.HeaderSize, body.Length);

            lock (_kcpLock)
            {
                int result = _kcp.Send(frame, 0, frame.Length);
                if (result < 0)
                {
                    throw new TransportException($"KCP send failed with code {result}");
                }
                // Flush immediately so the data hits the wire without waiting for
                // the next Update interval — matches the server's SetWriteDelay(false).
                _kcp.Flush();
            }

            return UniTask.CompletedTask;
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;

            try { _cts?.Cancel(); } catch { }

            try { _udp?.Close(); } catch { }

            _crypto?.Dispose();
        }

        public void Dispose() => Close();

        // ─────────────────────────── internal loops ───────────────────────────

        private async UniTaskVoid ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[MtuLimit];

            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udp.ReceiveAsync().AsUniTask()
                        .AttachExternalCancellation(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException)
                {
                    // Per-datagram ICMP errors — ignore and continue.
                    continue;
                }

                var data = result.Buffer;
                int dataLength = data.Length;

                // Decrypt if needed.
                if (_crypto != null)
                {
                    int kcpLength = _crypto.Open(data, 0, dataLength);
                    if (kcpLength <= 0) continue; // bad checksum — wrong key or garbage
                    // After Open, the KCP bytes start at KcpCrypto.HeaderSize.
                    lock (_kcpLock)
                    {
                        _kcp.Input(data, KcpCrypto.HeaderSize, kcpLength, ackNoDelay: true);
                    }
                }
                else
                {
                    lock (_kcpLock)
                    {
                        _kcp.Input(data, 0, dataLength, ackNoDelay: true);
                    }
                }
            }
        }

        private async UniTaskVoid UpdateLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await UniTask.Delay(TuningInterval, DelayType.Realtime, PlayerLoopTiming.Update, ct);
                }
                catch (OperationCanceledException) { break; }

                lock (_kcpLock)
                {
                    _kcp.Update();

                    if (_kcp.DeadLinkReached)
                    {
                        Close();
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Drains all complete messages from the KCP ARQ into the stream buffer.
        /// Returns the number of bytes added. Called under no lock — takes the lock itself.
        /// </summary>
        private int DrainKcpToStream()
        {
            int totalAdded = 0;

            lock (_kcpLock)
            {
                while (true)
                {
                    int n = _kcp.Recv(_recvScratch, _recvScratch.Length);
                    if (n <= 0) break;

                    // Ensure the stream buffer has room.
                    int needed = _streamEnd + n;
                    if (needed > _streamBuf.Length)
                    {
                        int newSize = Math.Max(_streamBuf.Length * 2, needed);
                        Array.Resize(ref _streamBuf, newSize);
                    }

                    Buffer.BlockCopy(_recvScratch, 0, _streamBuf, _streamEnd, n);
                    _streamEnd += n;
                    totalAdded += n;
                }
            }

            return totalAdded;
        }
    }
}
