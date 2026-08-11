using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Connection;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Client
{
    /// <summary>
    /// The first hop: authenticate, then ask for a map server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gateway is a redirector, never a gameplay proxy (ADR-3) — nothing after
    /// <c>enter_world</c> goes through it. It is kept connected only so the client
    /// can be told it was evicted, and so the gateway's session record survives (it
    /// destroys the record when the socket closes).
    /// </para>
    /// <para>
    /// The handshake runs frame-by-frame before the loops start, so
    /// <see cref="StartMonitoring"/> must be called after the last
    /// <see cref="EnterWorldAsync"/> — including any retries.
    /// </para>
    /// </remarks>
    public sealed class GatewayClient : IDisposable
    {
        private readonly NetworkSettings _settings;
        private readonly ITransportFactory _transports;
        private readonly IWireCodec _codec;
        private readonly INetLog _log;

        private WireConnection _connection;
        private bool _monitoring;

        public GatewayClient(NetworkSettings settings, ITransportFactory transports, IWireCodec codec, INetLog log)
        {
            _settings = settings;
            _transports = transports;
            _codec = codec;
            _log = log;
        }

        /// <summary>Raised once when the gateway connection ends, however it ends.</summary>
        public event Action<DisconnectInfo> Closed;

        public string UserId { get; private set; } = string.Empty;

        /// <summary>True once the socket exists, whether or not the loops are running.</summary>
        public bool IsConnected => _connection != null;

        /// <summary>True once the connection has been handed to its loops.</summary>
        public bool IsMonitoring => _monitoring;

        /// <summary>Dials the gateway and authenticates with a JWT.</summary>
        public async UniTask AuthenticateAsync(string jwt, CancellationToken cancellationToken)
        {
            if (_connection != null)
            {
                throw new InvalidOperationException("gateway client is already connected");
            }

            var transport = _transports.Create(TransportKind.Tcp);
            var connection = new WireConnection("gateway", transport, _codec, _settings, _log);
            _connection = connection;
            connection.Closed += OnClosed;

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(_settings.ConnectTimeout);

                await transport.ConnectAsync(_settings.GatewayHost, _settings.GatewayPort, timeout.Token);
                await connection.SendFrameAsync(MsgType.Auth, new AuthRequest { Token = jwt }, timeout.Token);

                var frame = await ExpectAsync(connection, MsgType.AuthResp, timeout.Token);
                if (!(frame.Payload is AuthResponse response))
                {
                    throw new NetworkException("gateway sent an auth response we could not decode");
                }

                if (!response.Ok)
                {
                    throw new NetworkException(
                        $"gateway rejected authentication: {response.Error}", response.Error);
                }

                UserId = response.UserId;
            }

            _log.Info($"authenticated with the gateway as '{UserId}'");
        }

        /// <summary>
        /// Asks for a server hosting <paramref name="mapId"/>.
        /// </summary>
        /// <remarks>
        /// Call this again for every join attempt. The token it returns is
        /// single-use and expires in 30 s, so a retry that replays one is rejected
        /// with <c>Token already used</c> rather than merely failing again.
        /// </remarks>
        public async UniTask<MapAssignment> EnterWorldAsync(string mapId, CancellationToken cancellationToken)
        {
            var connection = RequireHandshakeConnection();

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(_settings.ConnectTimeout);

                await connection.SendFrameAsync(
                    MsgType.EnterWorld, new EnterWorldRequest { MapId = mapId }, timeout.Token);

                var frame = await ReceiveAsync(connection, timeout.Token);

                // A failed precondition (expired session, rate limit) comes back as
                // auth_resp, not enter_world_resp — the gateway reuses the response
                // type of the request only for enter_world's own errors.
                if (frame.Type == MsgType.AuthResp && frame.Payload is AuthResponse authError)
                {
                    throw new NetworkException(
                        $"gateway refused enter_world: {authError.Error}", authError.Error);
                }

                if (frame.Type != MsgType.EnterWorldResp || !(frame.Payload is EnterWorldResponse response))
                {
                    throw new NetworkException($"expected enter_world_resp, got {frame.Type}");
                }

                if (!string.IsNullOrEmpty(response.Error))
                {
                    throw new NetworkException(
                        $"gateway could not assign a server: {response.Error}", response.Error);
                }

                if (string.IsNullOrEmpty(response.JoinToken))
                {
                    throw new NetworkException("gateway returned no join token");
                }

                var endpoint = NetworkEndpoint.Parse(response.ServerAddr);
                var transport = TransportKinds.Parse(response.Transport);

                _log.Info($"map '{mapId}' assigned to {endpoint} over {transport}");
                return new MapAssignment(endpoint, response.JoinToken, transport);
            }
        }

        /// <summary>
        /// Hands the gateway socket to the read/write/heartbeat loops, or closes it,
        /// according to <see cref="NetworkSettings.KeepGatewayConnection"/>. No
        /// further handshake calls are legal afterwards.
        /// </summary>
        public void StartMonitoring()
        {
            var connection = _connection;
            if (connection == null || _monitoring)
            {
                return;
            }

            if (!_settings.KeepGatewayConnection)
            {
                _log.Info("dropping the gateway connection; eviction notices will not be seen");
                connection.Leave();
                return;
            }

            _monitoring = true;
            connection.Start();
        }

        public void Close()
        {
            _connection?.Leave();
        }

        public void Dispose()
        {
            var connection = _connection;
            _connection = null;
            if (connection != null)
            {
                connection.Closed -= OnClosed;
                connection.Dispose();
            }
        }

        private void OnClosed(DisconnectInfo info)
        {
            _log.Info($"gateway connection closed: {info}");
            Closed?.Invoke(info);
        }

        private WireConnection RequireHandshakeConnection()
        {
            if (_connection == null)
            {
                throw new InvalidOperationException("authenticate with the gateway first");
            }

            if (_monitoring)
            {
                throw new InvalidOperationException(
                    "the gateway connection is already running its loops; enter_world must precede StartMonitoring");
            }

            return _connection;
        }

        private static async UniTask<WireFrame> ExpectAsync(WireConnection connection, MsgType expected,
            CancellationToken cancellationToken)
        {
            var frame = await ReceiveAsync(connection, cancellationToken);
            if (frame.Type != expected)
            {
                throw new NetworkException($"expected {expected}, got {frame.Type}");
            }

            return frame;
        }

        private static async UniTask<WireFrame> ReceiveAsync(WireConnection connection,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var frame = await connection.ReceiveFrameAsync(cancellationToken);
                if (frame == null)
                {
                    throw new NetworkException("gateway closed the connection during the handshake");
                }

                var value = frame.Value;

                // The gateway pings on its own initiative from the moment the socket
                // is accepted, so a heartbeat can land in the middle of a handshake.
                // Answer it and keep waiting for the frame we came for.
                if (value.Type == MsgType.Ping)
                {
                    var ping = value.Payload as PingMessage;
                    await connection.SendFrameAsync(MsgType.Pong, new PongMessage
                    {
                        Timestamp = ping?.Timestamp ?? 0L,
                        ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }, cancellationToken);
                    continue;
                }

                if (value.Type == MsgType.Pong)
                {
                    continue;
                }

                if (value.Type == MsgType.Kick || value.Type == MsgType.Disconnect)
                {
                    var reason = value.Payload is KickMessage kick
                        ? kick.Reason
                        : (value.Payload as DisconnectMessage)?.Reason ?? string.Empty;
                    throw new NetworkException($"gateway evicted the connection: {reason}", reason);
                }

                return value;
            }
        }
    }
}
