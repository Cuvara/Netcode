using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// KCP-over-UDP implementation of <see cref="ITransport"/>: <b>the</b> gameplay transport.
    /// Wire-compatible with the game server's <c>KcpListener</c> and with
    /// <c>github.com/xtaci/kcp-go/v5</c> as configured by <c>backend/shared/transport</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Realtime gameplay is KCP/UDP only — there is no TCP gameplay transport and no
    /// fallback. KCP runs in <b>stream mode</b>, so the same
    /// <c>[4-byte big-endian length][body]</c> framing as every other hop sits on top, and
    /// nothing above this class knows which transport it is on.
    /// </para>
    /// <para>
    /// One conversation over one UDP socket. KCP has no connection handshake: the server
    /// creates the session when the first datagram from an unknown endpoint arrives, and
    /// adopts the conversation id from its header. <see cref="ConnectAsync"/> therefore
    /// sends nothing — the first datagram is the one carrying the <c>join_token</c> frame.
    /// A dead or firewalled UDP port is consequently indistinguishable from a slow server
    /// until the join times out; <see cref="DatagramsReceived"/> is what lets
    /// <c>GameSessionClient</c> report that timeout as a KCP/UDP connect failure.
    /// </para>
    /// <para>
    /// The session state (ARQ, crypto, reassembly, idle timeout, dead link) lives in the
    /// Unity-free <see cref="KcpClientSession"/>; this class owns the socket and the two
    /// loops (receive, ARQ timer) and the application read path.
    /// </para>
    /// <para>
    /// Encryption is optional. With a transport key every datagram is AES-256-CFB encrypted
    /// in the kcp-go-compatible layout the server uses (<c>TRANSPORT_KEY</c>); without one,
    /// datagrams are plaintext — the dev default. Authenticated confidentiality of gameplay
    /// is the sealed session (ADR-22), not this.
    /// </para>
    /// <para>
    /// <b>WebGL:</b> browsers have no UDP sockets, so this transport cannot exist there and
    /// its constructor throws <see cref="NotSupportedException"/>. There is no fallback.
    /// </para>
    /// </remarks>
    public sealed class KcpTransport : ITransport
    {
        /// <summary>The message a WebGL build gets instead of a transport.</summary>
        public const string WebGlNotSupportedMessage =
            "KCP/UDP gameplay transport is not available on WebGL: browsers cannot open UDP sockets, " +
            "and realtime gameplay is KCP/UDP only (no TCP or WebSocket fallback)";

        private const int MtuLimit = 1500;

        /// <summary>
        /// Socket buffer size. Matches Go's <c>KCPSocketBuffer</c> and the server's
        /// constant. One UDP socket carries all traffic for this session; an
        /// undersized buffer costs throughput under burst.
        /// </summary>
        private const int SocketBufferBytes = 4 * 1024 * 1024;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly string _transportKey;
        private readonly int _idleTimeoutMs;

        private UdpClient _udp;
        private KcpClientSession _session;
        private CancellationTokenSource _cts;
        private int _closed;

        public string RemoteEndPoint { get; private set; } = string.Empty;

        public bool IsConnected =>
            _udp != null && Volatile.Read(ref _closed) == 0 && (_session == null || !_session.IsFailed);

        /// <summary>The conversation id of the current session, or 0 before <see cref="ConnectAsync"/>.</summary>
        public uint Conversation => _session?.Conv ?? 0;

        /// <summary>True when a transport key is set and datagrams are encrypted.</summary>
        public bool IsEncrypted => _session?.IsEncrypted ?? !string.IsNullOrWhiteSpace(_transportKey);

        /// <summary>
        /// Datagrams accepted from the server. Zero when a join times out means nothing came
        /// back at all: a closed/unmapped UDP port, a firewall, or a transport-key mismatch.
        /// </summary>
        public long DatagramsReceived => _session?.DatagramsReceived ?? 0;

        /// <summary>Why the session failed (idle timeout, dead link, overflow), or null.</summary>
        public string FailureReason => _session?.FailureReason;

        /// <summary>
        /// Creates a KCP transport with optional encryption.
        /// </summary>
        /// <param name="transportKey">
        /// The transport encryption key (64 hex characters = 32 bytes). Empty or null for
        /// plaintext (the dev default). Must match the server's <c>TRANSPORT_KEY</c> exactly:
        /// a mismatch is dropped silently by both ends and looks like a dead UDP port.
        /// </param>
        /// <param name="idleTimeoutMs">
        /// Close the session when no datagram arrives for this long;
        /// <see cref="KcpClientSession.DefaultIdleTimeoutMs"/> by default.
        /// </param>
        /// <exception cref="NotSupportedException">On WebGL, which has no UDP.</exception>
        public KcpTransport(string transportKey = null, int idleTimeoutMs = KcpClientSession.DefaultIdleTimeoutMs)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            throw new NotSupportedException(WebGlNotSupportedMessage);
#else
            _transportKey = transportKey;
            _idleTimeoutMs = idleTimeoutMs;
#endif
        }

        public async UniTask ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            if (_udp != null)
            {
                throw new TransportException("transport already connected");
            }

            IPAddress address;
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host).AsUniTask()
                    .AttachExternalCancellation(cancellationToken);
                address = KcpClientSession.PreferIPv4(addresses);
                if (address == null)
                {
                    throw new TransportException($"KCP/UDP: cannot resolve {host}");
                }
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
                throw new TransportException($"KCP/UDP: resolve {host} failed: {ex.Message}", ex);
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
                throw new TransportException($"KCP/UDP dial {host}:{port} ({address}) failed: {ex.Message}", ex);
            }

            _udp = udp;
            RemoteEndPoint = host + ":" + port;

            _session = new KcpClientSession(
                KcpClientSession.NewConversationId(),
                _transportKey,
                SendDatagram,
                NowMs,
                $"{host}:{port} (udp {address})",
                _idleTimeoutMs);

            _cts = new CancellationTokenSource();
            ReceiveLoopAsync(_cts.Token).Forget();
            UpdateLoopAsync(_cts.Token).Forget();

            // Nothing is sent here: KCP has no handshake, and an Update with an empty send
            // queue emits no datagram. The server learns of this conversation from the first
            // datagram that carries data — the join_token frame the caller writes next.
        }

        public async UniTask<byte[]> ReadFrameAsync(CancellationToken cancellationToken)
        {
            var session = _session ?? throw new TransportException("transport is not connected");

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Throws with the session's failure reason (idle timeout, dead link,
                // overflow, bad length) so the close is a visible TransportError.
                var frame = session.TryReadFrame();
                if (frame != null)
                {
                    return frame;
                }

                if (Volatile.Read(ref _closed) != 0)
                {
                    return null;
                }

                try
                {
                    // Realtime so a paused or slow game still pumps.
                    await UniTask.Delay(1, DelayType.Realtime, PlayerLoopTiming.Update, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // A cancelled read is a cancellation, not end-of-stream. Returning null
                    // here made the caller's own join deadline read as "the game server
                    // closed the connection" - the misleading error a Windows client saw
                    // against an unreachable UDP port - instead of reaching the
                    // KCP/UDP connect-timeout message. Same contract as TcpTransport.
                    if (Volatile.Read(ref _closed) != 0)
                    {
                        return null;
                    }

                    throw;
                }
            }
        }

        public UniTask WriteFrameAsync(byte[] body, CancellationToken cancellationToken)
        {
            var session = _session;
            if (session == null || Volatile.Read(ref _closed) != 0)
            {
                throw new TransportException(session?.FailureReason ?? "transport is not connected");
            }

            session.WriteFrame(body);
            return UniTask.CompletedTask;
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;

            try { _cts?.Cancel(); } catch { }
            try { _udp?.Close(); } catch { }
            _session?.Dispose();
        }

        public void Dispose() => Close();

        private static long NowMs() => Clock.ElapsedMilliseconds;

        private void SendDatagram(byte[] packet, int length)
        {
            try
            {
                _udp?.Send(packet, length);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        // ─────────────────────────── internal loops ───────────────────────────

        private async UniTaskVoid ReceiveLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udp.ReceiveAsync().AsUniTask().AttachExternalCancellation(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException)
                {
                    // Per-datagram ICMP errors — ignore and continue. A port that keeps
                    // refusing shows up as the idle timeout / KCP connect timeout instead.
                    continue;
                }

                try
                {
                    _session.Input(result.Buffer, result.Buffer.Length);
                }
                catch (ObjectDisposedException)
                {
                    // Close() disposed the crypto while this datagram was in flight.
                    break;
                }
            }
        }

        private async UniTaskVoid UpdateLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await UniTask.Delay(KcpClientSession.TuningInterval, DelayType.Realtime, PlayerLoopTiming.Update, ct);
                }
                catch (OperationCanceledException) { break; }

                if (!_session.Tick())
                {
                    // Failed: stop the socket but keep the reason. The next read throws it,
                    // so WireConnection closes with TransportError and logs why.
                    try { _cts?.Cancel(); } catch { }
                    try { _udp?.Close(); } catch { }
                    return;
                }
            }
        }
    }
}
