using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Auth;
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

        /// <summary>
        /// The wire protocol version the game server reported on the last successful
        /// join. Zero means it advertised none, i.e. it predates the field and never
        /// checked ours.
        /// </summary>
        public uint ServerProtocolVersion { get; private set; }

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

        /// <summary>Frames decoded off the wire, of every type. Diagnostics only.</summary>
        public long FramesReceived => _connection?.FramesReceived ?? 0L;

        public bool IsConnected => _connection != null && _connection.IsRunning;

        /// <summary>How the gameplay connection ended, once it has; null while it is up.</summary>
        public DisconnectInfo? CloseInfo => _connection?.CloseInfo;

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
                    MsgType.JoinToken,
                    new JoinTokenRequest
                    {
                        Token = assignment.JoinToken,
                        ProtocolVersion = WireProtocolVersion.Current,
                    },
                    timeout.Token);

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

                // Checked independently of the gateway hop (ADR-3: two connections,
                // two separately deployed processes). It is THIS hop that a version
                // disagreement corrupts, because the snapshot stream is where a
                // misparse turns into a wrong world.
                ServerProtocolVersion = response.ProtocolVersion;
                if (WireProtocolVersion.IsUnversioned(response.ProtocolVersion))
                {
                    _log.Warn(
                        "game server did not advertise a wire protocol version; this client speaks " +
                        WireProtocolVersion.Current +
                        " and the agreement is unverified (the server predates the field)");
                }
                else if (!WireProtocolVersion.IsCompatible(response.ProtocolVersion))
                {
                    // Defence in depth: the server should already have refused us. If
                    // it accepted a version it does not speak, we disagree and it did
                    // not notice — refuse rather than start merging snapshots whose
                    // fields we may be reading as something else.
                    throw new NetworkException(
                        $"game server speaks wire protocol version {response.ProtocolVersion}, " +
                        $"this client speaks {WireProtocolVersion.Current}",
                        KickReasons.ProtocolVersionMismatch);
                }

                UserId = response.UserId;
                TickRate = response.TickRate;

                if (_settings.RequireSealedSession)
                {
                    await RunSealedHandshakeAsync(connection, assignment.JoinToken, cancellationToken);
                }
            }

            _resolver.Reset();
            _awaitingKeyframe = false;
            ServerTick = 0L;
            AckTick = 0L;

            connection.Start();
            _log.Info($"joined {assignment.Endpoint} as '{UserId}'");
        }

        /// <summary>
        /// Run the sealed-session handshake, or fail the join. There is no cleartext
        /// fallback on any path.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Runs after the join reply and before <c>Start</c>, so no frame is ever written
        /// half-sealed, and it mirrors where the server runs its half.
        /// </para>
        /// <para>
        /// <b>The client passes no join-token secret, and that is deliberate.</b> Verifying
        /// the server's binding needs material derived from <c>JOIN_TOKEN_SECRET</c>, and a
        /// client binary must not carry that secret — putting it there is the pre-shared-key
        /// mistake ADR-22 supersedes, where extracting it once compromises everyone for ever.
        /// So the session is confidential against a passive eavesdropper and offers nothing
        /// against an active one until ADR-22's pinned gateway identity key lands. That is
        /// logged at join, once, rather than left for someone to infer.
        /// </para>
        /// </remarks>
        private async UniTask RunSealedHandshakeAsync(
            WireConnection connection, string joinToken, CancellationToken cancellationToken)
        {
            string jti;
            if (!JoinTokenClaims.TryReadJti(joinToken, out jti))
            {
                // The jti is the handshake's salt. Without it the client would derive keys
                // the server cannot match, and the failure would surface as an unexplained
                // refusal several steps later.
                throw new NetworkException(
                    "the join token carries no readable jti, which the sealed handshake needs as its salt");
            }

            SealedHandshakeClient.Result result;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(_settings.SealedHandshakeTimeout);

                try
                {
                    result = await SealedHandshakeClient.RunAsync(connection, jti, null, timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The commonest cause by far, and the one worth naming: this client is
                    // configured to seal and the server is not, so no hello is ever coming.
                    // Without this the symptom is a silent stall during join.
                    throw new NetworkException(
                        "timed out waiting for the server's sealed hello. The likeliest cause is a " +
                        "configuration mismatch: NetworkSettings.RequireSealedSession is on and the " +
                        "game server is not running with sealing required. There is no negotiation " +
                        "and no fallback, by design, so the two must be set together.");
                }
            }

            if (!result.Ok)
            {
                throw new NetworkException(
                    $"sealed handshake failed ({result.Outcome}): {result.Error}");
            }

            if (!result.BindingVerified)
            {
                // Not a warning about a defect — it is the shipped state, and it is logged so
                // that "the session is encrypted" is never read as "the server is
                // authenticated".
                _log.Info(
                    "sealed session established; the server's binding was NOT verified, so this " +
                    "session is confidential against a passive eavesdropper and offers no " +
                    "man-in-the-middle protection (ADR-22, pending the pinned gateway identity key)");
            }
            else
            {
                _log.Info("sealed session established and the server's binding verified");
            }
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
