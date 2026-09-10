using System;
using System.Collections.Concurrent;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Connection
{
    /// <summary>
    /// One framed, encoded, heartbeated link to a server — gateway or game server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both hops speak the same protocol, ping on the same 10 s cadence and drop
    /// the peer after the same 30 s of silence, so this is implemented once and
    /// used twice. The only difference between the hops is which messages the owner
    /// cares about.
    /// </para>
    /// <para>
    /// <b>Two phases.</b> Before <see cref="Start"/> the connection is in handshake
    /// mode: the owner drives it with <see cref="SendFrameAsync"/> and
    /// <see cref="ReceiveFrameAsync"/>, one frame at a time, exactly as both servers
    /// drive their own handshakes. <see cref="Start"/> then hands the socket to the
    /// read, write and heartbeat loops, and the direct methods become illegal —
    /// calling them would race the read loop for the same bytes.
    /// </para>
    /// <para>
    /// <b>Encoding.</b> Outbound frames all use the codec supplied at construction,
    /// for the life of the connection: both servers latch their reply encoding from
    /// the first frame we send, so changing ours mid-connection would silently
    /// change theirs. Inbound frames are classified per frame from their first byte
    /// instead of being assumed to match, because eviction frames arrive as JSON
    /// whatever the connection latched.
    /// </para>
    /// </remarks>
    public sealed class WireConnection : IDisposable
    {
        private readonly ITransport _transport;
        private readonly IWireCodec _outbound;
        private readonly IWireCodec _jsonInbound;
        private readonly IWireCodec _protobufInbound;
        private readonly NetworkSettings _settings;
        private readonly INetLog _log;
        private readonly string _name;

        // Null until the sealed handshake installs them. Once installed they are never
        // removed: a session that has been sealed must not be talked back down to cleartext.
        private Crypto.SealedSession _sealedOutbound;
        private Crypto.SealedSession _sealedInbound;

        private readonly ConcurrentQueue<byte[]> _sendQueue = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim _sendSignal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private int _queueDepth;
        private int _started;
        private int _closeSignalled;
        private int _disposed;
        // Monotonic milliseconds (NetworkSettings.MonotonicClock), never wall
        // clock: heartbeat age and RTT are elapsed-time questions, and a wall
        // clock that steps — NTP sync, suspend/resume — must not read as silence.
        private long _lastPongMono;
        private long _lastRoundTripMs;

        // The ping in flight: the wall-clock timestamp it carried (the protocol
        // field the server echoes back, used purely as a match token) and the
        // monotonic instant it left, which is what the RTT is measured from.
        private long _pingSentWall;
        private long _pingSentMono;

        /// <summary>
        /// Set when a <c>kick</c> has been seen, so the <c>disconnect</c> the server
        /// sends immediately afterwards is recognised as the second half of one
        /// eviction rather than reported as a separate event.
        /// </summary>
        private bool _evicted;

        public WireConnection(string name, ITransport transport, IWireCodec outboundCodec,
            NetworkSettings settings, INetLog log)
        {
            _name = name;
            _transport = transport;
            _outbound = outboundCodec;
            _settings = settings;
            _log = log;

            // Inbound is decoded per frame by sniffing, never by assuming the peer
            // mirrored us, so BOTH codecs are kept ready regardless of what we send.
            // JSON must always be decodable because the gateway builds eviction frames
            // off the victim connection's goroutine and cannot read its latched
            // encoding, so it writes JSON whatever the connection negotiated.
            _jsonInbound = outboundCodec.Encoding == WireEncoding.Json
                ? outboundCodec
                : new JsonWireCodec();

            _protobufInbound = outboundCodec.Encoding == WireEncoding.Protobuf
                ? outboundCodec
                : new ProtobufWireCodec();

            _lastPongMono = MonoMs();
        }

        /// <summary>
        /// Raised for every decoded frame the connection does not handle itself.
        /// Heartbeat and eviction frames never reach it.
        /// </summary>
        public event Action<WireFrame> FrameReceived;

        /// <summary>Raised exactly once, when the connection ends, for any reason.</summary>
        public event Action<DisconnectInfo> Closed;

        public string Name => _name;

        public bool IsRunning => Volatile.Read(ref _started) == 1 && Volatile.Read(ref _closeSignalled) == 0;

        /// <summary>Most recent round trip in milliseconds, from the heartbeat. Zero until the first pong.</summary>
        public long RoundTripMs => Interlocked.Read(ref _lastRoundTripMs);

        /// <summary>How the connection ended, once it has.</summary>
        public DisconnectInfo? CloseInfo { get; private set; }

        /// <summary>Whether every frame on this connection is sealed.</summary>
        public bool IsSealed { get { return _sealedOutbound != null; } }

        /// <summary>Refusals on the inbound sealed session, by cause. Zero when not sealed.</summary>
        public ulong SealedRejectedTotal { get { return _sealedInbound == null ? 0UL : _sealedInbound.RejectedTotal; } }

        /// <summary>
        /// Install the two one-direction sealed sessions. Everything written afterwards is
        /// sealed and everything read afterwards must be.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Call once, from the sealed handshake, before <see cref="Start"/>. Installing
        /// after the loops are running would seal mid-stream and leave the peer's counter
        /// out of step with ours.
        /// </para>
        /// <para>
        /// <b>There is no uninstall, deliberately.</b> A connection that can revert to
        /// cleartext is a connection an attacker can talk down to cleartext, which is the
        /// whole reason the sealed handshake has no negotiation and no fallback.
        /// </para>
        /// </remarks>
        public void InstallSealedSession(Crypto.SealedSession inbound, Crypto.SealedSession outbound)
        {
            if (inbound == null) throw new ArgumentNullException(nameof(inbound));
            if (outbound == null) throw new ArgumentNullException(nameof(outbound));

            RequireHandshakePhase();

            if (_sealedOutbound != null)
                throw new InvalidOperationException($"{_name}: sealed session already installed");

            _sealedInbound = inbound;
            _sealedOutbound = outbound;
        }

        /// <summary>Seal an encoded body if this connection is sealed; otherwise pass it through.</summary>
        private byte[] SealIfNeeded(byte[] body)
        {
            var session = _sealedOutbound;
            return session == null ? body : session.Seal(body);
        }

        // ─────────────────────────── handshake phase ───────────────────────────

        /// <summary>Writes one frame directly. Legal only before <see cref="Start"/>.</summary>
        public async UniTask SendFrameAsync(MsgType type, IWireMessage payload, CancellationToken cancellationToken)
        {
            RequireHandshakePhase();
            var body = SealIfNeeded(_outbound.EncodeBody(type, payload));
            await _transport.WriteFrameAsync(body, cancellationToken);
        }

        /// <summary>
        /// Reads one frame directly. Legal only before <see cref="Start"/>. Returns
        /// null on a clean EOF.
        /// </summary>
        public async UniTask<WireFrame?> ReceiveFrameAsync(CancellationToken cancellationToken)
        {
            RequireHandshakePhase();
            var body = await _transport.ReadFrameAsync(cancellationToken);
            if (body == null)
            {
                return null;
            }

            return DecodeInbound(body);
        }

        // ─────────────────────────── running phase ───────────────────────────

        /// <summary>
        /// Starts the read, write and heartbeat loops. Call once, after the
        /// handshake has succeeded.
        /// </summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException($"{_name}: connection already started");
            }

            _lastPongMono = MonoMs();

            ReadLoopAsync().Forget();
            WriteLoopAsync().Forget();
            HeartbeatLoopAsync().Forget();
        }

        /// <summary>
        /// Queues a frame. Never blocks and never throws on a dead connection — a
        /// send racing a disconnect is normal, not exceptional.
        /// </summary>
        public void Send(MsgType type, IWireMessage payload)
        {
            if (Volatile.Read(ref _closeSignalled) != 0)
            {
                return;
            }

            byte[] body;
            try
            {
                body = SealIfNeeded(_outbound.EncodeBody(type, payload));
            }
            catch (WireCodecException ex)
            {
                // Our own message, so this is a bug on this side rather than a peer
                // problem: log it and drop the frame rather than killing the session.
                _log.Error($"{_name}: cannot encode {type}", ex);
                return;
            }

            if (Interlocked.Increment(ref _queueDepth) > _settings.SendQueueCapacity &&
                _sendQueue.TryDequeue(out _))
            {
                // Drop the oldest, as the game server's bounded channel does. The
                // signal count is left alone: a surplus permit only costs the write
                // loop one empty pass.
                Interlocked.Decrement(ref _queueDepth);
                _log.Warn($"{_name}: send queue full, dropped the oldest frame");
            }

            _sendQueue.Enqueue(body);

            try
            {
                _sendSignal.Release();
            }
            catch (ObjectDisposedException)
            {
                // Raced a Dispose(). The frame is simply never written, which is
                // what "send on a closing connection" has to mean.
            }
        }

        /// <summary>
        /// Closes the connection from this side, reporting
        /// <see cref="DisconnectCause.LocalClose"/> if nothing has been reported yet.
        /// </summary>
        public void Close(string reason = "")
        {
            SignalClose(new DisconnectInfo(DisconnectCause.LocalClose, reason));
        }

        /// <summary>
        /// Sends a polite <c>disconnect</c> and then closes. Best effort: the frame
        /// is queued, so it only reaches the wire if the write loop is still alive.
        /// </summary>
        public void Leave(string reason = "")
        {
            if (IsRunning)
            {
                Send(MsgType.Disconnect, new DisconnectMessage { Reason = reason });
            }

            Close(reason);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Close();
            _transport.Dispose();

            // The loops observe the cancellation before this runs only in the
            // common case; disposing the source they may still be awaiting on is
            // exactly the race that bit the game server, so it is deliberately not
            // disposed here. It holds no unmanaged resource.
            _sendSignal.Dispose();
        }

        // ─────────────────────────── loops ───────────────────────────

        private async UniTaskVoid ReadLoopAsync()
        {
            var token = _cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var body = await _transport.ReadFrameAsync(token);
                    if (body == null)
                    {
                        // Clean EOF. After an eviction this is the expected tail of
                        // the sequence and must not overwrite the recorded cause.
                        SignalClose(new DisconnectInfo(DisconnectCause.PeerClosed));
                        return;
                    }

                    HandleFrame(DecodeInbound(body));
                }
            }
            catch (OperationCanceledException)
            {
                // Expected: Close() cancelled us.
            }
            catch (WireCodecException ex)
            {
                SignalClose(new DisconnectInfo(DisconnectCause.ProtocolError, string.Empty, ex));
            }
            catch (Exception ex)
            {
                SignalClose(new DisconnectInfo(DisconnectCause.TransportError, string.Empty, ex));
            }
        }

        private async UniTaskVoid WriteLoopAsync()
        {
            var token = _cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await _sendSignal.WaitAsync(token).AsUniTask();

                    while (_sendQueue.TryDequeue(out var body))
                    {
                        Interlocked.Decrement(ref _queueDepth);
                        await _transport.WriteFrameAsync(body, token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
            catch (ObjectDisposedException)
            {
                // The semaphore went away under a concurrent Dispose(); the
                // connection is closing anyway.
            }
            catch (Exception ex)
            {
                SignalClose(new DisconnectInfo(DisconnectCause.TransportError, string.Empty, ex));
            }
        }

        private async UniTaskVoid HeartbeatLoopAsync()
        {
            var token = _cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Realtime by default (NetworkSettings.HeartbeatScheduler), so a
                    // paused or slowed game still answers the server's liveness
                    // check instead of being dropped at 30 s.
                    await _settings.HeartbeatScheduler(_settings.PingInterval, token);

                    if (!TickHeartbeat())
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
            catch (Exception ex)
            {
                // A heartbeat loop that dies quietly leaves the connection to be
                // dropped by the server 30 s later with no client-side record of
                // why. Say so.
                _log.Error($"{_name}: heartbeat loop faulted", ex);
            }
        }

        /// <summary>
        /// One heartbeat round: declare the link dead if the last pong is older
        /// than <see cref="NetworkSettings.PongTimeout"/> on the monotonic clock,
        /// otherwise send a ping. Returns false once the link has been declared dead.
        /// </summary>
        private bool TickHeartbeat()
        {
            var now = MonoMs();
            var silentMs = now - Interlocked.Read(ref _lastPongMono);
            if (silentMs > (long)_settings.PongTimeout.TotalMilliseconds)
            {
                _log.Warn($"{_name}: no pong for {silentMs} ms, declaring the link dead");
                SignalClose(new DisconnectInfo(DisconnectCause.HeartbeatTimeout));
                return false;
            }

            // The timestamp is a protocol field and stays wall-clock; it is only
            // ever compared for equality with the echo, never subtracted from.
            var wall = WallMs();
            Interlocked.Exchange(ref _pingSentWall, wall);
            Interlocked.Exchange(ref _pingSentMono, now);
            Send(MsgType.Ping, new PingMessage { Timestamp = wall });
            return true;
        }

        // ─────────────────────────── frame handling ───────────────────────────

        private WireFrame DecodeInbound(byte[] body)
        {
            body = UnsealIfNeeded(body);

            var encoding = EncodingSniffer.Sniff(body);
            switch (encoding)
            {
                case WireEncoding.Json:
                    return _jsonInbound.DecodeBody(body);

                case WireEncoding.Protobuf:
                    return _protobufInbound.DecodeBody(body);

                default:
                    throw new WireCodecException(
                        $"frame body starts with 0x{body[0]:X2}, which is neither JSON nor Protobuf");
            }
        }

        /// <summary>
        /// Open a sealed body, or throw. Once sealed, a cleartext frame is an attack, not a
        /// message.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Both failures throw the same exception and the caller kills the session.</b>
        /// The peer must not learn which one it was: distinguishing "the tag did not verify"
        /// from "that was a replay" tells an attacker whether a forged frame reached the
        /// replay window.
        /// </para>
        /// <para>
        /// <b><c>NotSealed</c> throws too, and that is the downgrade defence.</b> Accepting a
        /// cleartext Envelope after the handshake would let anyone who can inject one frame
        /// speak to this client unauthenticated — the sealing would still be running, and
        /// the session would look perfectly healthy the whole time. There is no reading of
        /// a cleartext frame here that is not either an attack or a broken peer, and the
        /// answer to both is the same.
        /// </para>
        /// </remarks>
        private byte[] UnsealIfNeeded(byte[] body)
        {
            var session = _sealedInbound;
            if (session == null) return body;

            byte[] plaintext;
            Crypto.SealedOpenResult result = session.Open(body, out plaintext);
            if (result == Crypto.SealedOpenResult.Ok) return plaintext;

            throw new WireCodecException(
                result == Crypto.SealedOpenResult.NotSealed
                    ? "a cleartext frame arrived on a sealed session; refusing it rather than " +
                      "accepting a downgrade"
                    : "a sealed frame was refused");
        }

        /// <summary>
        /// Frames decoded off the wire since the connection opened, of every type.
        /// </summary>
        /// <remarks>
        /// Compared against how many snapshots reach <c>WorldState.Apply</c>, this is what
        /// separates "the server did not send it" from "the client did not consume it" —
        /// the two have opposite fixes and no counter in this package could tell them
        /// apart. A client whose applied rate sits below its received rate is dropping
        /// frames in decode or resolve; one whose received rate sits below the server's
        /// send rate is not reading the socket fast enough.
        /// </remarks>
        public long FramesReceived { get; private set; }

        private void HandleFrame(WireFrame frame)
        {
            FramesReceived++;

            switch (frame.Type)
            {
                case MsgType.Ping:
                    // Answer regardless of session state, as both servers do: a
                    // heartbeat carries no session semantics and must not depend on
                    // gameplay state to be serviced.
                    var ping = frame.Payload as PingMessage;
                    Send(MsgType.Pong, new PongMessage
                    {
                        Timestamp = ping?.Timestamp ?? 0L,
                        ServerTime = WallMs()
                    });
                    return;

                case MsgType.Pong:
                    var now = MonoMs();
                    Interlocked.Exchange(ref _lastPongMono, now);
                    if (frame.Payload is PongMessage pong
                        && pong.Timestamp > 0L
                        && pong.Timestamp == Interlocked.Read(ref _pingSentWall))
                    {
                        // Matched to the ping in flight by its echoed timestamp, then
                        // measured on the monotonic clock — a wall-clock step between
                        // ping and pong changes neither the match nor the result.
                        Interlocked.Exchange(ref _lastRoundTripMs, now - Interlocked.Read(ref _pingSentMono));
                    }

                    return;

                case MsgType.Kick:
                    var kick = frame.Payload as KickMessage;
                    _evicted = true;
                    _log.Info($"{_name}: evicted by the server, reason '{kick?.Reason}'");
                    SignalClose(new DisconnectInfo(DisconnectCause.Kicked, kick?.Reason ?? string.Empty));
                    return;

                case MsgType.Disconnect:
                    var bye = frame.Payload as DisconnectMessage;
                    if (_evicted)
                    {
                        // The paired legacy frame of an eviction we have already
                        // reported. Reporting it again would surface every eviction
                        // twice; the order kick-then-disconnect is contractual.
                        _log.Info($"{_name}: ignoring the disconnect paired with the kick");
                        return;
                    }

                    SignalClose(new DisconnectInfo(DisconnectCause.ServerDisconnect, bye?.Reason ?? string.Empty));
                    return;

                default:
                    RaiseFrameReceived(frame);
                    return;
            }
        }

        private void RaiseFrameReceived(WireFrame frame)
        {
            var handler = FrameReceived;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(frame);
            }
            catch (Exception ex)
            {
                // A consumer's exception must not take the socket down with it.
                _log.Error($"{_name}: handler for {frame.Type} threw", ex);
            }
        }

        private void SignalClose(DisconnectInfo info)
        {
            if (Interlocked.Exchange(ref _closeSignalled, 1) != 0)
            {
                return;
            }

            CloseInfo = info;

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }

            _transport.Close();

            var handler = Closed;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(info);
            }
            catch (Exception ex)
            {
                _log.Error($"{_name}: close handler threw", ex);
            }
        }

        private void RequireHandshakePhase()
        {
            if (Volatile.Read(ref _started) != 0)
            {
                throw new InvalidOperationException(
                    $"{_name}: direct frame access is only legal before Start()");
            }
        }

        /// <summary>
        /// Wall clock, for protocol timestamp fields only. Overridable by tests so
        /// a clock step can be staged and shown not to matter.
        /// </summary>
        internal Func<long> WallClock { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private long WallMs() => WallClock();

        /// <summary>Monotonic clock, for every elapsed-time comparison.</summary>
        private long MonoMs() => _settings.MonotonicClock();
    }
}
