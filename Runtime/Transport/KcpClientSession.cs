using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// The client side of one KCP conversation, with no sockets, awaits or schedulers:
    /// the ARQ, the optional datagram crypto, the stream-to-frame reassembly, the idle
    /// timeout and the dead-link check. <see cref="KcpTransport"/> is the UniTask/UDP
    /// shell around it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a separate class.</b> Every await in <see cref="KcpTransport"/> goes
    /// through <c>UniTask</c>, which needs <c>UnityEngine</c>; everything that decides
    /// whether a session is healthy does not. Split out, it compiles under
    /// <c>dotnet test</c> (<c>Tests~/Headless</c>), which is where its conversation id,
    /// idle timeout, receive bound and a real loopback exchange are tested.
    /// </para>
    /// <para>
    /// <b>Failures are sticky and visible.</b> Once <see cref="FailureReason"/> is set,
    /// <see cref="TryReadFrame"/> and <see cref="WriteFrame"/> throw a
    /// <see cref="TransportException"/> carrying it, so the connection closes with
    /// <c>TransportError</c> and the reason in the log rather than as a silent EOF.
    /// </para>
    /// <para>
    /// Thread-safe: every public member takes one lock.
    /// </para>
    /// </remarks>
    internal sealed class KcpClientSession : IDisposable
    {
        // The KCP tuning profile. Every value MUST equal GameServer.Net.Transport.KcpTuning
        // and backend/shared/transport/transport.go: nodelay 1/10/2/1, wnd 128/128, MTU 1350.
        internal const int TuningNoDelay = 1;
        internal const int TuningInterval = 10;
        internal const int TuningResend = 2;
        internal const int TuningNoCongestion = 1;
        internal const int TuningSendWindow = 128;
        internal const int TuningRecvWindow = 128;
        internal const int TuningMtu = 1350;

        /// <summary>
        /// No inbound datagram for this long closes the session. The heartbeat (10 s ping,
        /// 30 s pong timeout) normally ends a dead link first; this is the transport's own
        /// backstop, and it also covers the handshake, before the heartbeat runs.
        /// </summary>
        public const int DefaultIdleTimeoutMs = 60_000;

        /// <summary>
        /// Upper bound on reassembled-but-unparsed stream bytes: one maximum frame (header +
        /// 1 MiB body) plus one MTU of slack. Bytes are only drained from the ARQ while no
        /// complete frame is buffered, so a well-formed stream can never reach it; the rest
        /// waits inside KCP's receive queue, whose window then throttles the sender. Reaching
        /// it fails the session — bytes are never dropped.
        /// </summary>
        public const int MaxStreamBytes = WireFraming.HeaderSize + WireFraming.MaxBodySize + TuningMtu;

        private readonly object _lock = new object();
        private readonly Kcp _kcp;
        private readonly KcpCrypto _crypto;
        private readonly int _cryptoHeaderSize;
        private readonly Action<byte[], int> _sendDatagram;
        private readonly Func<long> _clockMs;
        private readonly int _idleTimeoutMs;
        private readonly int _maxStreamBytes;
        private readonly string _remote;
        private readonly byte[] _recvScratch = new byte[TuningMtu];

        private byte[] _stream = new byte[16 * 1024];
        private int _streamStart;
        private int _streamEnd;
        private long _lastInboundMs;
        private long _datagramsReceived;
        private long _datagramsRejected;
        private string _failure;

        /// <param name="conv">Conversation id; see <see cref="NewConversationId"/>.</param>
        /// <param name="transportKey">kcp-go-compatible AES key (64 hex chars), or null/empty for plaintext.</param>
        /// <param name="sendDatagram">Writes one finished datagram to the socket.</param>
        /// <param name="clockMs">Monotonic milliseconds, for the idle timeout.</param>
        /// <param name="remote">Peer as <c>host:port</c>, for error messages.</param>
        /// <param name="idleTimeoutMs">See <see cref="DefaultIdleTimeoutMs"/>.</param>
        /// <param name="maxStreamBytes">See <see cref="MaxStreamBytes"/>; tests pass less.</param>
        public KcpClientSession(
            uint conv,
            string transportKey,
            Action<byte[], int> sendDatagram,
            Func<long> clockMs,
            string remote,
            int idleTimeoutMs = DefaultIdleTimeoutMs,
            int maxStreamBytes = MaxStreamBytes)
        {
            if (conv == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(conv), "a KCP conversation id must be non-zero");
            }

            _sendDatagram = sendDatagram ?? throw new ArgumentNullException(nameof(sendDatagram));
            _clockMs = clockMs ?? throw new ArgumentNullException(nameof(clockMs));
            _remote = string.IsNullOrEmpty(remote) ? "<unknown>" : remote;
            _idleTimeoutMs = idleTimeoutMs;
            _maxStreamBytes = Math.Max(maxStreamBytes, WireFraming.HeaderSize + TuningMtu);

            _crypto = KcpCrypto.TryCreate(transportKey);
            _cryptoHeaderSize = _crypto != null ? KcpCrypto.HeaderSize : 0;

            _kcp = new Kcp(conv, Output);
            _kcp.Stream = 1;
            _kcp.SetNoDelay(TuningNoDelay, TuningInterval, TuningResend, TuningNoCongestion);
            _kcp.WndSize(TuningSendWindow, TuningRecvWindow);
            _kcp.SetMtu(TuningMtu - _cryptoHeaderSize);

            _lastInboundMs = _clockMs();
        }

        public uint Conv => _kcp.Conv;

        /// <summary>True when datagrams are AES-encrypted with a transport key.</summary>
        public bool IsEncrypted => _crypto != null;

        /// <summary>Datagrams the ARQ accepted. Zero after a connect timeout means nothing came back at all.</summary>
        public long DatagramsReceived { get { lock (_lock) { return _datagramsReceived; } } }

        /// <summary>Datagrams dropped as undecryptable or not for this conversation (wrong key, stray traffic).</summary>
        public long DatagramsRejected { get { lock (_lock) { return _datagramsRejected; } } }

        /// <summary>Why the session failed, or null while it is healthy.</summary>
        public string FailureReason { get { lock (_lock) { return _failure; } } }

        public bool IsFailed => FailureReason != null;

        /// <summary>Unparsed stream bytes held. Diagnostics and tests.</summary>
        public int BufferedStreamBytes { get { lock (_lock) { return _streamEnd - _streamStart; } } }

        /// <summary>
        /// A cryptographically random, non-zero conversation id. Per session, so a reconnect
        /// from the same endpoint is never mistaken for the old conversation and an off-path
        /// attacker cannot predict it from a process-wide counter.
        /// </summary>
        public static uint NewConversationId()
        {
            var bytes = new byte[4];
            using (var rng = RandomNumberGenerator.Create())
            {
                while (true)
                {
                    rng.GetBytes(bytes);
                    var conv = (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
                    if (conv != 0)
                    {
                        return conv;
                    }
                }
            }
        }

        /// <summary>
        /// The address to dial: the first IPv4 one when there is any, because the game server
        /// binds IPv4 Any and a host name that resolves to <c>::1</c> first (localhost on
        /// many systems) would otherwise send every datagram to a port nobody listens on.
        /// </summary>
        public static IPAddress PreferIPv4(IPAddress[] addresses)
        {
            if (addresses == null || addresses.Length == 0)
            {
                return null;
            }

            foreach (var a in addresses)
            {
                if (a.AddressFamily == AddressFamily.InterNetwork)
                {
                    return a;
                }
            }

            return addresses[0];
        }

        /// <summary>Feeds one received datagram (still encrypted, if a key is set) to the ARQ.</summary>
        public void Input(byte[] datagram, int length)
        {
            lock (_lock)
            {
                if (_failure != null)
                {
                    return;
                }

                int offset = 0;
                int kcpLength = length;
                if (_crypto != null)
                {
                    kcpLength = _crypto.Open(datagram, 0, length);
                    if (kcpLength <= 0)
                    {
                        // Wrong key or garbage: fail closed, and do not count it as life.
                        _datagramsRejected++;
                        return;
                    }

                    offset = KcpCrypto.HeaderSize;
                }

                if (_kcp.Input(datagram, offset, kcpLength, ackNoDelay: true) < 0)
                {
                    _datagramsRejected++;
                    return;
                }

                _datagramsReceived++;
                _lastInboundMs = _clockMs();
            }
        }

        /// <summary>
        /// Drives the ARQ timer and checks liveness. Call every <see cref="TuningInterval"/> ms.
        /// Returns false once the session has failed.
        /// </summary>
        public bool Tick()
        {
            lock (_lock)
            {
                if (_failure != null)
                {
                    return false;
                }

                _kcp.Update();

                if (_kcp.DeadLinkReached)
                {
                    Fail($"KCP dead link to {_remote}: a segment was retransmitted the maximum number of " +
                         "times without being acknowledged");
                    return false;
                }

                var silentMs = _clockMs() - _lastInboundMs;
                if (_idleTimeoutMs > 0 && silentMs >= _idleTimeoutMs)
                {
                    Fail($"KCP idle timeout: no UDP datagram from {_remote} for {silentMs / 1000.0:F1} s " +
                         $"(limit {_idleTimeoutMs / 1000.0:F0} s)" +
                         (_datagramsReceived == 0
                             ? "; nothing was ever received - check the firewall / UDP port mapping and the transport key"
                             : string.Empty));
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Returns the next complete frame body, or null when more datagrams are needed.
        /// </summary>
        /// <exception cref="TransportException">The session failed, or the peer sent an invalid length.</exception>
        public byte[] TryReadFrame()
        {
            lock (_lock)
            {
                while (true)
                {
                    if (_failure != null)
                    {
                        throw new TransportException(_failure);
                    }

                    var frame = TakeBufferedFrame();
                    if (frame != null)
                    {
                        return frame;
                    }

                    // Lazy drain: one ARQ message at a time, and only while no complete
                    // frame is buffered. That keeps the stream buffer at most one partial
                    // frame plus one segment, and leaves the rest in KCP's receive queue
                    // where the window throttles the sender instead of this buffer growing.
                    if (!DrainOneMessage())
                    {
                        return null;
                    }
                }
            }
        }

        /// <summary>Frames <paramref name="body"/> and hands it to the ARQ, flushing immediately.</summary>
        public void WriteFrame(byte[] body)
        {
            if (body == null || body.Length == 0)
            {
                throw new TransportException("refusing to write an empty frame");
            }

            if (body.Length > WireFraming.MaxBodySize)
            {
                throw new TransportException($"frame of {body.Length} bytes exceeds the 1 MiB limit");
            }

            var frame = new byte[WireFraming.HeaderSize + body.Length];
            WireFraming.WriteLength(frame, body.Length);
            Buffer.BlockCopy(body, 0, frame, WireFraming.HeaderSize, body.Length);

            lock (_lock)
            {
                if (_failure != null)
                {
                    throw new TransportException(_failure);
                }

                int result = _kcp.Send(frame, 0, frame.Length);
                if (result < 0)
                {
                    throw new TransportException($"KCP send to {_remote} failed with code {result}");
                }

                // Flush now rather than on the next Update, matching the server's
                // SetWriteDelay(false): an input should not wait up to one interval.
                _kcp.Flush();
            }
        }

        /// <summary>Marks the session failed with <paramref name="reason"/>, unless it already is.</summary>
        public void Fail(string reason)
        {
            lock (_lock)
            {
                if (_failure == null)
                {
                    _failure = reason;
                }
            }
        }

        public void Dispose()
        {
            _crypto?.Dispose();
        }

        private void Output(byte[] buf, int size)
        {
            // Called from inside Kcp.Flush/Update, i.e. already under _lock.
            var packet = new byte[_cryptoHeaderSize + size];
            Buffer.BlockCopy(buf, 0, packet, _cryptoHeaderSize, size);
            _crypto?.Seal(packet, 0, packet.Length);
            _sendDatagram(packet, packet.Length);
        }

        private byte[] TakeBufferedFrame()
        {
            int buffered = _streamEnd - _streamStart;
            if (buffered < WireFraming.HeaderSize)
            {
                return null;
            }

            int length = WireFraming.ReadLength(_stream, _streamStart);
            if (!WireFraming.IsValidLength(length))
            {
                _failure = $"invalid frame length {length} from {_remote}: the KCP stream is no longer frame-aligned";
                throw new TransportException(_failure);
            }

            if (buffered < WireFraming.HeaderSize + length)
            {
                return null;
            }

            var body = new byte[length];
            Buffer.BlockCopy(_stream, _streamStart + WireFraming.HeaderSize, body, 0, length);
            _streamStart += WireFraming.HeaderSize + length;
            if (_streamStart == _streamEnd)
            {
                _streamStart = 0;
                _streamEnd = 0;
            }

            return body;
        }

        private bool DrainOneMessage()
        {
            int size = _kcp.PeekSize();
            if (size <= 0)
            {
                return false;
            }

            int buffered = _streamEnd - _streamStart;
            if (buffered + size > _maxStreamBytes)
            {
                // Never drop bytes: a stream that cannot be held is failed, visibly.
                _failure = $"KCP receive buffer from {_remote} would exceed {_maxStreamBytes} bytes " +
                           $"({buffered} buffered + {size} arriving) without completing a frame";
                throw new TransportException(_failure);
            }

            var scratch = size <= _recvScratch.Length ? _recvScratch : new byte[size];
            int n = _kcp.Recv(scratch, scratch.Length);
            if (n <= 0)
            {
                return false;
            }

            if (_streamStart > 0)
            {
                Buffer.BlockCopy(_stream, _streamStart, _stream, 0, buffered);
                _streamStart = 0;
                _streamEnd = buffered;
            }

            if (_streamEnd + n > _stream.Length)
            {
                Array.Resize(ref _stream, Math.Min(_maxStreamBytes, Math.Max(_stream.Length * 2, _streamEnd + n)));
            }

            Buffer.BlockCopy(scratch, 0, _stream, _streamEnd, n);
            _streamEnd += n;
            return true;
        }
    }
}
