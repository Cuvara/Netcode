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
        private readonly CommandCorrelator _commands = new CommandCorrelator();

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

        /// <summary>
        /// Raised for every <see cref="CommandResult"/> the server sends, after the awaiting
        /// <see cref="SendCommandAsync"/> (if any) has been completed with it. Includes results
        /// nobody is waiting for any more -- a command cancelled by its caller still executes on
        /// the server, and this is where its outcome is still visible.
        /// </summary>
        /// <remarks>Raised on the connection's read loop, like <see cref="SnapshotReceived"/>.</remarks>
        public event Action<CommandResult> CommandResultReceived;

        /// <summary>
        /// Raised for every unsolicited <see cref="ServerPush"/> (inventory changed, quest
        /// progress, chat line). Decode the payload with Shared.GameLogic's gameplay codec for
        /// its <see cref="ServerPush.Opcode"/>.
        /// </summary>
        /// <remarks>Raised on the connection's read loop, like <see cref="SnapshotReceived"/>.</remarks>
        public event Action<ServerPush> ServerPushReceived;

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

        /// <summary>
        /// The character this connection plays, as the game server echoed it from the join
        /// token's <c>cid</c> claim (ADR-31). Empty from a server predating character slots, and
        /// for the account's default character on one that does not echo it.
        /// </summary>
        /// <remarks>
        /// Bind the local view to THIS, not to the id that was requested: it is what the server
        /// actually loaded, and the request is not trusted by anyone.
        /// </remarks>
        public string CharacterId { get; private set; } = string.Empty;

        /// <summary>
        /// Whether the game server speaks the command channel (protocol version 3, ADR-30).
        /// False before a join, and against a version 2 or unversioned server -- in which case
        /// <see cref="SendCommandAsync"/> completes at once with
        /// <see cref="CommandChannelErrors.ProtocolTooOld"/>.
        /// </summary>
        public bool SupportsCommands =>
            WireProtocolVersion.Supports(ServerProtocolVersion, WireProtocolVersion.CommandChannel);

        /// <summary>
        /// Whether the game server's movement is 3D (protocol version 3, ADR-28): predict with
        /// <c>CharacterMotor</c> rather than the planar <c>MovementSystem</c>. See
        /// <c>LocalMovePredictor.UseServerProtocol</c>.
        /// </summary>
        public bool UsesMotor3D =>
            WireProtocolVersion.Supports(ServerProtocolVersion, WireProtocolVersion.Motor3D);

        /// <summary>Commands sent on this connection and not yet answered.</summary>
        public int PendingCommandCount => _commands.PendingCount;

        /// <summary>
        /// Results that named a seq nothing was waiting for -- a cancelled command's late answer,
        /// or a server bug. Diagnostics.
        /// </summary>
        public int UnmatchedCommandResults => _commands.UnmatchedResults;

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
                CharacterId = response.CharacterId ?? string.Empty;

                if (_settings.RequireSealedSession)
                {
                    await RunSealedHandshakeAsync(connection, assignment, cancellationToken);
                }
            }

            _resolver.Reset();
            _awaitingKeyframe = false;
            ServerTick = 0L;
            AckTick = 0L;

            // Before Start(): a result can only arrive once the read loop runs, and a command
            // can only be sent once this hop is up, so opening here leaves no window either way.
            _commands.Open();

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
            WireConnection connection, MapAssignment assignment, CancellationToken cancellationToken)
        {
            string jti;
            if (!JoinTokenClaims.TryReadJti(assignment.JoinToken, out jti))
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
                    // The identity key and the flag saying what its hop was worth come from
                    // the assignment together, because the gateway hop is where both are known
                    // and neither means anything without the other.
                    result = await SealedHandshakeClient.RunAsync(
                        connection, jti, null,
                        assignment.ServerIdentityKey,
                        assignment.IdentityKeyHopAuthenticated,
                        _settings.RequireServerIdentity,
                        timeout.Token);
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

            // What the session is actually worth is now decided by the ADR-25 identity
            // signature, not by the binding: no shipped client can verify the binding, and
            // none ever will, because doing so would require the key that mints join tokens.
            // The three branches below are the three states a real deployment can be in.
            if (result.Identity.Verified)
            {
                _log.Info(
                    "sealed session established and the server's identity VERIFIED: its Ed25519 " +
                    "signature checked out and its key arrived over an authenticated gateway hop " +
                    "(ADR-25)");
            }
            else if (result.Identity.Checked)
            {
                // The interesting state, and the one a reader is most likely to misread as
                // success. The signature is genuine under the key we were handed -- but the key
                // was handed over plaintext, so an active attacker supplies both halves and this
                // branch is exactly what their session looks like too.
                _log.Warn(
                    "sealed session established and the server's identity signature checked out, " +
                    "but its key arrived over an UNAUTHENTICATED gateway hop, so the signature " +
                    "proves nothing against an active attacker -- who would substitute the key and " +
                    "the signature together. Turn on NetworkSettings.GatewayUseTls (ADR-23) to make " +
                    "this verification mean something");
            }
            else
            {
                // Covers both a pre-ADR-25 backend and a key with no signature. Info, not a
                // warning: with RequireServerIdentity off this is the expected state, and a
                // warning on every join trains the reader to ignore the one that matters.
                _log.Info(
                    "sealed session established; the server's identity was NOT verified" +
                    (string.IsNullOrEmpty(result.Identity.Error) ? "" : " (" + result.Identity.Error + ")") +
                    ". This session is confidential against a passive eavesdropper and offers no " +
                    "man-in-the-middle protection. The server's binding is separately unverifiable " +
                    "by design (ADR-22) and is not what closes this gap -- ADR-25 identity plus " +
                    "gateway TLS is");
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
        /// Sends one input frame carrying an ability.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A separate overload rather than four more optional parameters on
        /// <see cref="SendInput(long, float, float, string)"/>: the common call sends no
        /// ability, and defaulted parameters make the ability path look like something that
        /// happens by accident rather than something a caller opted into.
        /// </para>
        /// <para>
        /// <b>This is a request, and the ability is NOT predicted.</b> Prediction covers
        /// movement only — movement is a pure function of input the client already has, while
        /// an ability outcome depends on cooldowns, content and other entities' state. The
        /// only report a client gets is what comes back in the snapshot: a
        /// <see cref="Protocol.Messages.GameEventType.AbilityCast"/> event if it resolved,
        /// nothing if it did not. Drive a cast animation and a cooldown sweep off that event,
        /// never off this call.
        /// </para>
        /// <para>
        /// <paramref name="abilityTargetId"/> is a server-side entity id, in the same space as
        /// <paramref name="attackTargetId"/> — never an interned handle, which the server
        /// allocates for its own outbound snapshots and would not recognise coming back.
        /// <paramref name="aimX"/>/<paramref name="aimY"/> are a world-space POINT for a
        /// ground-targeted ability, unlike the move vector which is a direction.
        /// </para>
        /// </remarks>
        public void SendAbilityInput(
            long tick,
            float moveX,
            float moveY,
            uint abilityId,
            string abilityTargetId = "",
            float aimX = 0f,
            float aimY = 0f,
            string attackTargetId = "")
        {
            _connection?.Send(MsgType.Input, new InputMessage
            {
                Tick = tick,
                MoveX = moveX,
                MoveY = moveY,
                AttackTargetId = attackTargetId ?? string.Empty,
                AbilityId = abilityId,
                AbilityTargetId = abilityTargetId ?? string.Empty,
                AimX = aimX,
                AimY = aimY
            });
        }

        /// <summary>
        /// Sends one fully-formed input frame, including the protocol version 3 fields
        /// (<see cref="InputMessage.Jump"/>, <see cref="InputMessage.AimZ"/>,
        /// <see cref="InputMessage.RenderTick"/>/<see cref="InputMessage.RenderAlpha"/>,
        /// <see cref="InputMessage.SpawnSeq"/>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The other <c>SendInput</c> overloads cover the protocol 2 shape and stay as they
        /// were; this one exists so a version 3 caller is not forced through a parameter list
        /// that grows with every field. Fill the render time with
        /// <see cref="InputMessage.SetRenderTime"/> from <c>WorldViewBinder.RenderTick</c>.
        /// </para>
        /// <para>
        /// Sent as-is, to a version 2 server too: it skips the fields it does not know, so the
        /// input still moves the player and a jump or a skillshot simply does not happen.
        /// </para>
        /// </remarks>
        public void SendInput(InputMessage input)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            _connection?.Send(MsgType.Input, input);
        }

        /// <summary>
        /// Sends one gameplay command on the generic channel (ADR-30) and completes with the
        /// server's <see cref="CommandResult"/> for it.
        /// </summary>
        /// <param name="opcode">
        /// A Shared.GameLogic <c>GameplayOpcodes</c> value. Allocated from 1; 0 throws.
        /// </param>
        /// <param name="payload">
        /// The opcode's payload, encoded with Shared.GameLogic's gameplay codec. Null is sent as
        /// empty. This package never looks inside it.
        /// </param>
        /// <param name="cancellationToken">
        /// Stops WAITING, not the command: once sent it may still execute, and its result is
        /// still raised on <see cref="CommandResultReceived"/>.
        /// </param>
        /// <returns>
        /// The server's answer, correlated by a per-connection seq starting at 1. Never throws
        /// for a channel failure: a command that cannot be sent or loses its connection
        /// completes with <c>Ok == false</c> and one of the <see cref="CommandChannelErrors"/>
        /// names -- <see cref="CommandChannelErrors.NotConnected"/>,
        /// <see cref="CommandChannelErrors.ProtocolTooOld"/> (the server echoed a protocol
        /// version below 3, checked BEFORE anything is sent), or
        /// <see cref="CommandChannelErrors.ConnectionClosed"/> (it was in flight when the
        /// connection ended, including on a reconnect or transfer).
        /// </returns>
        public UniTask<CommandResult> SendCommandAsync(
            uint opcode, byte[] payload, CancellationToken cancellationToken = default)
        {
            // Thrown here, synchronously, rather than inside the async body where it would be
            // captured into the task: an opcode of 0 is a bug in the caller, not an outcome.
            if (opcode == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(opcode), "opcode 0 means \"not sent\" on the wire and the server refuses it");
            }

            return SendCommandCoreAsync(opcode, payload, cancellationToken);
        }

        private async UniTask<CommandResult> SendCommandCoreAsync(
            uint opcode, byte[] payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var connection = _connection;
            var refusal = CommandCorrelator.Precheck(
                connection != null && connection.IsRunning, ServerProtocolVersion);
            if (refusal != null)
            {
                return CommandCorrelator.LocalFailure(0, refusal);
            }

            if (!_commands.TryBegin(out var seq, out var waiter))
            {
                // The connection closed between the check above and here.
                return CommandCorrelator.LocalFailure(0, CommandChannelErrors.ConnectionClosed);
            }

            connection.Send(MsgType.Command, new CommandRequest
            {
                Seq = seq,
                Opcode = opcode,
                Payload = payload ?? Array.Empty<byte>(),
            });

            if (!cancellationToken.CanBeCanceled)
            {
                return await waiter.Task;
            }

            using (cancellationToken.Register(() => _commands.Cancel(seq, cancellationToken)))
            {
                return await waiter.Task;
            }
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

            // Detaching Closed below means OnClosed will not run for this connection, so the
            // in-flight commands are failed here instead -- every one of them, exactly once.
            _commands.FailAll(CommandChannelErrors.ConnectionClosed);

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
            if (frame.Type == MsgType.CommandResult)
            {
                if (frame.Payload is CommandResult result)
                {
                    _commands.Complete(result);
                    CommandResultReceived?.Invoke(result);
                }

                return;
            }

            if (frame.Type == MsgType.ServerPush)
            {
                if (frame.Payload is ServerPush push)
                {
                    ServerPushReceived?.Invoke(push);
                }

                return;
            }

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
            // Before Closed is raised, so a handler that reacts to the close (a reconnect)
            // never observes a command still pending on the dead connection.
            _commands.FailAll(CommandChannelErrors.ConnectionClosed);
            _log.Info($"game session closed: {info}");
            Closed?.Invoke(info);
        }
    }
}
