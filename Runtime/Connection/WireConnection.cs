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
        private readonly NetworkSettings _settings;
        private readonly INetLog _log;
        private readonly string _name;

        private readonly ConcurrentQueue<byte[]> _sendQueue = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim _sendSignal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private int _queueDepth;
        private int _started;
        private int _closeSignalled;
        private int _disposed;
        private long _lastPongMs;
        private long _lastRoundTripMs;

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

            // Inbound JSON is always decodable regardless of what we send: the
            // gateway builds eviction frames off the victim connection's goroutine
            // and cannot read its latched encoding, so it writes JSON.
            _jsonInbound = outboundCodec.Encoding == WireEncoding.Json
                ? outboundCodec
                : new JsonWireCodec();

            _lastPongMs = NowMs();
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

        // ─────────────────────────── handshake phase ───────────────────────────

        /// <summary>Writes one frame directly. Legal only before <see cref="Start"/>.</summary>
        public async UniTask SendFrameAsync(MsgType type, IWireMessage payload, CancellationToken cancellationToken)
        {
            RequireHandshakePhase();
            var body = _outbound.EncodeBody(type, payload);
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

            _lastPongMs = NowMs();

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
                body = _outbound.EncodeBody(type, payload);
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
                    // Realtime, so a paused or slowed game still answers the server's
                    // liveness check instead of being dropped at 30 s.
                    await UniTask.Delay(_settings.PingInterval, DelayType.Realtime, PlayerLoopTiming.Update, token);

                    var silentMs = NowMs() - Interlocked.Read(ref _lastPongMs);
                    if (silentMs > (long)_settings.PongTimeout.TotalMilliseconds)
                    {
                        _log.Warn($"{_name}: no pong for {silentMs} ms, declaring the link dead");
                        SignalClose(new DisconnectInfo(DisconnectCause.HeartbeatTimeout));
                        return;
                    }

                    Send(MsgType.Ping, new PingMessage { Timestamp = NowMs() });
                }
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        // ─────────────────────────── frame handling ───────────────────────────

        private WireFrame DecodeInbound(byte[] body)
        {
            var encoding = EncodingSniffer.Sniff(body);
            switch (encoding)
            {
                case WireEncoding.Json:
                    return _jsonInbound.DecodeBody(body);

                case WireEncoding.Protobuf:
                    // Reachable only if a server ever initiates in Protobuf. Neither
                    // does today — both answer in the encoding they were addressed
                    // in — so this is a loud "wire the Protobuf codec up", not a
                    // condition to paper over.
                    throw new WireCodecException(
                        "received a Protobuf frame, which this client cannot decode yet");

                default:
                    throw new WireCodecException(
                        $"frame body starts with 0x{body[0]:X2}, which is neither JSON nor Protobuf");
            }
        }

        private void HandleFrame(WireFrame frame)
        {
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
                        ServerTime = NowMs()
                    });
                    return;

                case MsgType.Pong:
                    var now = NowMs();
                    Interlocked.Exchange(ref _lastPongMs, now);
                    if (frame.Payload is PongMessage pong && pong.Timestamp > 0L)
                    {
                        Interlocked.Exchange(ref _lastRoundTripMs, now - pong.Timestamp);
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

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
