using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Connection;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Client
{
    /// <summary>
    /// The second hop: the long-lived gameplay socket to one game server.
    /// </summary>
    /// <remarks>
    /// Opened with a join token from the gateway and kept for the whole time the
    /// player is on that map. It carries input up and snapshots down, and nothing
    /// else routes through the gateway once it exists (ADR-3).
    /// </remarks>
    public sealed class GameSessionClient : IDisposable
    {
        private readonly NetworkSettings _settings;
        private readonly ITransportFactory _transports;
        private readonly IWireCodec _codec;
        private readonly INetLog _log;
        private readonly SnapshotResolver _resolver = new SnapshotResolver();

        private WireConnection _connection;
        private bool _awaitingKeyframe;

        public GameSessionClient(NetworkSettings settings, ITransportFactory transports, IWireCodec codec, INetLog log)
        {
            _settings = settings;
            _transports = transports;
            _codec = codec;
            _log = log;
        }

        /// <summary>
        /// Raised for every snapshot whose entity handles resolved. A snapshot that
        /// did not resolve is never raised: the client asks for a keyframe instead
        /// of passing on state it cannot attribute.
        /// </summary>
        public event Action<ResolvedSnapshot> SnapshotReceived;

        /// <summary>Raised once when the gameplay connection ends, however it ends.</summary>
        public event Action<DisconnectInfo> Closed;

        public string UserId { get; private set; } = string.Empty;

        /// <summary>
        /// The server's simulation tick rate in Hz, as advertised in its join response.
        /// <b>Zero means the server did not send one</b> — fall back to a configured
        /// default rather than treating it as a rate.
        /// </summary>
        /// <remarks>
        /// This is the cadence the server integrates movement at, so it is the <c>dt</c> a
        /// prediction layer must use. Reading it here rather than assuming a constant is
        /// what stops a client and a server that disagree about the rate from silently
        /// predicting different distances — see <c>JoinTokenResponse.TickRate</c> for what
        /// that cost when it happened.
        /// </remarks>
        public uint TickRate { get; private set; }

        /// <summary>Server tick of the newest snapshot applied. Never moves backwards.</summary>
        public long ServerTick { get; private set; }

        /// <summary>
        /// Newest input tick the server has accepted for us; monotonic, and zero
        /// until the first input is accepted. Surfaced for the prediction layer to
        /// reconcile against — this class never acts on it.
        /// </summary>
        public long AckTick { get; private set; }

        /// <summary>Heartbeat round trip in milliseconds, or zero before the first pong.</summary>
        public long RoundTripMs => _connection?.RoundTripMs ?? 0L;

        public bool IsConnected => _connection != null && _connection.IsRunning;

        /// <summary>
        /// Dials the assigned game server and consumes the join token.
        /// </summary>
        /// <remarks>
        /// The token is spent whether or not the join succeeds — the server consumes
        /// its <c>jti</c> on receipt — so a caller retrying must obtain a fresh one
        /// from <c>enter_world</c> rather than calling this twice with the same
        /// assignment.
        /// </remarks>
        public async UniTask JoinAsync(MapAssignment assignment, CancellationToken cancellationToken)
        {
            if (_connection != null)
            {
                throw new InvalidOperationException("game session is already connected");
            }

            var transport = _transports.Create(assignment.Transport);
            var connection = new WireConnection("gameserver", transport, _codec, _settings, _log);
            _connection = connection;
            connection.Closed += OnClosed;
            connection.FrameReceived += OnFrame;

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(_settings.ConnectTimeout);

                await transport.ConnectAsync(assignment.Endpoint.Host, assignment.Endpoint.Port, timeout.Token);

                // The join token must be the very first frame; the game server
                // rejects anything else outright.
                await connection.SendFrameAsync(
                    MsgType.JoinToken, new JoinTokenRequest { Token = assignment.JoinToken }, timeout.Token);

                var frame = await connection.ReceiveFrameAsync(timeout.Token);
                if (frame == null)
                {
                    throw new NetworkException("game server closed the connection during the join");
                }

                var value = frame.Value;
                if (value.Type != MsgType.JoinTokenResp || !(value.Payload is JoinTokenResponse response))
                {
                    throw new NetworkException($"expected join_token_resp, got {value.Type}");
                }

                if (!response.Ok)
                {
                    throw new NetworkException($"game server refused the join: {response.Error}", response.Error);
                }

                UserId = response.UserId;
                TickRate = response.TickRate;
            }

            _resolver.Reset();
            _awaitingKeyframe = false;
            ServerTick = 0L;
            AckTick = 0L;

            connection.Start();
            _log.Info($"joined {assignment.Endpoint} as '{UserId}'");
        }

        /// <summary>
        /// Sends one input frame.
        /// </summary>
        /// <remarks>
        /// <paramref name="moveX"/> and <paramref name="moveY"/> are a direction,
        /// not a displacement, and <paramref name="tick"/> is the client's own input
        /// sequence number — the server drops anything not strictly greater than the
        /// last it accepted. No validation, normalisation or cooldown check happens
        /// here: those are server-authoritative rules and live in
        /// <c>Shared.GameLogic</c>.
        /// </remarks>
        public void SendInput(long tick, float moveX, float moveY, string attackTargetId = "")
        {
            _connection?.Send(MsgType.Input, new InputMessage
            {
                Tick = tick,
                MoveX = moveX,
                MoveY = moveY,
                AttackTargetId = attackTargetId ?? string.Empty
            });
        }

        /// <summary>
        /// Asks the server to make the next snapshot a keyframe. Repeated calls
        /// while one is already outstanding are dropped: a resync costs a full AOI
        /// snapshot, and one is enough to repair any disagreement.
        /// </summary>
        public void RequestResync()
        {
            if (_awaitingKeyframe || _connection == null)
            {
                return;
            }

            _awaitingKeyframe = true;
            _connection.Send(MsgType.Resync, new ResyncRequest());
        }

        /// <summary>Leaves politely, then closes.</summary>
        public void Leave()
        {
            _connection?.Leave();
        }

        public void Dispose()
        {
            var connection = _connection;
            _connection = null;
            if (connection == null)
            {
                return;
            }

            connection.Closed -= OnClosed;
            connection.FrameReceived -= OnFrame;
            connection.Dispose();
        }

        private void OnFrame(WireFrame frame)
        {
            if (frame.Type != MsgType.Snapshot)
            {
                // transfer_map_resp lands here once map transfer is implemented.
                _log.Info($"game server sent an unhandled {frame.Type}");
                return;
            }

            if (!(frame.Payload is SnapshotMessage snapshot))
            {
                return;
            }

            if (!_resolver.TryResolve(snapshot, out var resolved))
            {
                // We hold no binding for a handle the server used, so we have lost
                // state it assumed we had. Nothing from this snapshot is applied —
                // guessing would attribute an update to the wrong entity.
                _log.Warn($"snapshot at tick {snapshot.Tick} referenced an unknown handle; requesting a keyframe");
                RequestResync();
                return;
            }

            if (resolved.Full)
            {
                _awaitingKeyframe = false;
            }

            if (resolved.Tick > ServerTick)
            {
                ServerTick = resolved.Tick;
            }

            if (resolved.AckTick > AckTick)
            {
                // Monotonic: a snapshot that omits ack_tick carries zero and must
                // never lower it.
                AckTick = resolved.AckTick;
            }

            var handler = SnapshotReceived;
            handler?.Invoke(resolved);
        }

        private void OnClosed(DisconnectInfo info)
        {
            _log.Info($"game session closed: {info}");
            Closed?.Invoke(info);
        }
    }
}
