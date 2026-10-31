using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
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

        // Diagnostics counters. Written from the read and write loops, read from the main thread.
        private long _bytesReceived;
        private long _bytesSent;
        private long _framesSent;
        private long _sendQueueDrops;

        // Set once a graceful leave has begun: nothing may be queued after its disconnect frame.
        private int _leaving;

        // One transport write at a time. The write loop holds it around each queued frame and a
        // leave holds it for its own writes: TcpTransport reuses one buffer per write and has no
        // lock, and a TLS stream rejects a second write while one is pending.
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);

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

            // Its own instance even when the outbound codec is already Protobuf, rather
            // than aliasing it. Two reasons, and the first is a correctness one: the
            // outbound codec is registered as a DI SINGLETON and is therefore shared by the
            // gateway connection and the game-session connection, so a decode buffer living
            // on it would be shared between two connections' read loops. The second is that
            // the reuse below is only sound per connection, where frames are decoded and
            // consumed one at a time on one read loop.
            _protobufInbound = ProtobufWireCodec.CreatePooled();

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
            if (Volatile.Read(ref _leaving) != 0)
            {
                // After the goodbye. The server stops reading this session at the disconnect
                // frame, so anything queued behind it would only delay the acknowledgement.
                return;
            }

            Enqueue(type, payload);
        }

        /// <summary>Encodes, seals and queues one frame; returns the queued body, or null.</summary>
        private byte[] Enqueue(MsgType type, IWireMessage payload)
        {
            if (Volatile.Read(ref _closeSignalled) != 0)
            {
                return null;
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
                return null;
            }

            if (Interlocked.Increment(ref _queueDepth) > _settings.SendQueueCapacity &&
                _sendQueue.TryDequeue(out _))
            {
                // Drop the oldest, as the game server's bounded channel does. The
                // signal count is left alone: a surplus permit only costs the write
                // loop one empty pass.
                Interlocked.Decrement(ref _queueDepth);
                Interlocked.Increment(ref _sendQueueDrops);
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

            return body;
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
        /// Longest a leave waits for a transport write that does not complete on the calling
        /// thread. KCP writes complete at once, so this only bounds other transports.
        /// </summary>
        public const int LeaveWriteTimeoutMs = 500;

        /// <summary>
        /// Whether the last graceful leave's disconnect frame was acknowledged by the peer before
        /// the connection closed: null until a leave has finished (or when the transport cannot
        /// tell, as TCP cannot), true when it was, false when the bound ran out first.
        /// </summary>
        public bool? LeaveAcknowledged { get; private set; }

        /// <summary>
        /// Sends a polite <c>disconnect</c>, waits for it to be delivered, and then closes.
        /// Returns at once; see <see cref="LeaveAsync"/>.
        /// </summary>
        /// <remarks>
        /// The connection must not be disposed until the leave has finished, or the socket goes
        /// with it: <c>GameSessionClient.Leave</c> hands the connection over for exactly that
        /// reason.
        /// </remarks>
        public void Leave(string reason = "")
        {
            LeaveAsync(reason).Forget();
        }

        /// <summary>
        /// Sends a polite <c>disconnect</c> on the calling thread, waits until the transport has
        /// delivered it, then closes. When no transport write is in flight (always so for KCP,
        /// whose writes complete synchronously) the frame is handed to the transport before the
        /// first suspension, so it is on the wire when this returns its task even if the player
        /// loop never runs again. Otherwise it first waits on the write gate for the in-flight
        /// write (bounded by <c>LeaveWriteTimeoutMs</c>). The wait is bounded by the transport's own flush budget (KCP:
        /// <see cref="KcpClientSession.CloseFlushBudget"/>, at most 500 ms).
        /// </summary>
        /// <remarks>
        /// <para>
        /// This used to queue the frame and close in the same call. Closing cancelled the write
        /// loop before it ran, so the frame was never written, and on KCP even a written frame
        /// needs the socket alive until it is acknowledged. The server therefore never saw a
        /// graceful leave: both rpg-factory testlab S08G runs ended on the server's KCP dead-link
        /// timeout instead.
        /// </para>
        /// <para>
        /// A later version still queued the frame and let the write loop send it. That loop
        /// resumes on the player loop, which does not run after <c>Application.Quit</c>: a
        /// quitting client left no datagram behind and the server saw a dead link (Netcode #182).
        /// The write loop is bypassed now. Frames already queued are written first, in order, then
        /// the disconnect. Nothing queued after it is sent.
        /// </para>
        /// </remarks>
        public async UniTask LeaveAsync(string reason = "")
        {
            if (IsRunning && Interlocked.Exchange(ref _leaving, 1) == 0)
            {
                try
                {
                    // Takes the gate and writes synchronously when nothing is in flight, which is
                    // always so for a transport whose writes complete at once (KCP). Otherwise it
                    // waits, bounded, for the write loop's current write to finish first.
                    var written = false;
                    if (await _writeGate.WaitAsync(LeaveWriteTimeoutMs, _cts.Token))
                    {
                        try
                        {
                            written = await WriteLeaveFramesAsync(reason);
                        }
                        finally
                        {
                            _writeGate.Release();
                        }
                    }

                    if (written && IsRunning && _transport is IFlushableTransport flushable)
                    {
                        LeaveAcknowledged = await flushable.FlushAsync(_cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Closed or disposed meanwhile: nothing left to wait for.
                }
                catch (Exception ex)
                {
                    _log.Warn($"{_name}: leave did not complete cleanly: {ex.Message}");
                }
            }

            Close(reason);
        }

        /// <summary>
        /// Writes what is queued, then the disconnect, straight to the transport. Returns whether
        /// the disconnect was handed over.
        /// </summary>
        private async UniTask<bool> WriteLeaveFramesAsync(string reason)
        {
            while (_sendQueue.TryDequeue(out var queued))
            {
                Interlocked.Decrement(ref _queueDepth);
                if (!await WriteBoundedAsync(queued))
                {
                    return false;
                }
            }

            byte[] bye;
            try
            {
                bye = SealIfNeeded(_outbound.EncodeBody(MsgType.Disconnect, new DisconnectMessage { Reason = reason }));
            }
            catch (WireCodecException ex)
            {
                _log.Error($"{_name}: cannot encode {MsgType.Disconnect}", ex);
                return false;
            }

            return await WriteBoundedAsync(bye);
        }

        private async UniTask<bool> WriteBoundedAsync(byte[] body)
        {
            var write = _transport.WriteFrameAsync(body, CancellationToken.None);
            if (write.Status.IsCompleted())
            {
                write.GetAwaiter().GetResult();
                CountSent(body);
                return true;
            }

            // A Task timer rather than a player-loop delay, so the wait also ends in an edit-mode
            // Editor, where only the synchronization context is pumped.
            var task = write.AsTask();
            await Task.WhenAny(task, Task.Delay(LeaveWriteTimeoutMs, _cts.Token)).AsUniTask();
            if (task.Status == TaskStatus.RanToCompletion)
            {
                CountSent(body);
                return true;
            }

            return false;
        }

        private void CountSent(byte[] body)
        {
            Interlocked.Increment(ref _framesSent);
            Interlocked.Add(ref _bytesSent, body.Length);
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

                    Interlocked.Add(ref _bytesReceived, body.Length);
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

                    while (true)
                    {
                        await _writeGate.WaitAsync(token);
                        try
                        {
                            if (!_sendQueue.TryDequeue(out var body))
                            {
                                break;
                            }

                            Interlocked.Decrement(ref _queueDepth);
                            await _transport.WriteFrameAsync(body, token);
                            CountSent(body);
                        }
                        finally
                        {
                            _writeGate.Release();
                        }
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

        /// <summary>
        /// Bytes of frame bodies read from the transport since <see cref="Start"/>, as the
        /// transport hands them up: after the transport's own decryption, before the sealed
        /// session is opened. Excludes the 4-byte length prefix and any KCP/UDP headers.
        /// </summary>
        public long BytesReceived => Interlocked.Read(ref _bytesReceived);

        /// <summary>
        /// Frames handed to the transport and completed since <see cref="Start"/>. A frame still
        /// queued or dropped from a full queue is not counted.
        /// </summary>
        public long FramesSent => Interlocked.Read(ref _framesSent);

        /// <summary>Bytes of frame bodies written, counted like <see cref="BytesReceived"/>.</summary>
        public long BytesSent => Interlocked.Read(ref _bytesSent);

        /// <summary>
        /// Frames discarded because the send queue was full (the oldest frame is dropped).
        /// Nonzero means the transport drains slower than the game queues input.
        /// </summary>
        public long SendQueueDrops => Interlocked.Read(ref _sendQueueDrops);

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
