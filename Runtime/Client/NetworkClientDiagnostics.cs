namespace Cuvara.Netcode.Client
{
    /// <summary>
    /// One read of every counter the client keeps about its link, from
    /// <see cref="NetworkClient.GetDiagnostics"/>. A plain value: reading it allocates nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Scope.</b> Counters that come from the session or its transport (frames, bytes,
    /// datagrams, queue drops, resyncs, unresolved snapshots, KCP timers) belong to the
    /// <em>current</em> session and start from 0 again after a reconnect; <see cref="Generation"/>
    /// tells two sessions apart. <see cref="StaleSnapshotsDropped"/> belongs to the world state,
    /// which is reset on every connect and reconnect. <see cref="ReconnectAttempts"/> counts for
    /// the whole life of the client.
    /// </para>
    /// <para>
    /// <b>Not applicable means 0.</b> Before a join, or once a session is gone, every
    /// session-scoped field is 0 (and <see cref="State"/> says why). KCP fields are also 0 when
    /// the transport underneath is not a <c>KcpTransport</c> and no
    /// <c>ITransportDecorator</c> reveals one.
    /// </para>
    /// <para>Read it on the main thread.</para>
    /// </remarks>
    public readonly struct NetworkClientDiagnostics
    {
        /// <summary>Client connection state.</summary>
        public readonly NetworkClientState State;

        /// <summary>Operation generation, see <c>NetworkClient.Generation</c>.</summary>
        public readonly int Generation;

        /// <summary>Wire protocol version the game server announced at join. 0 without a session.</summary>
        public readonly uint ServerProtocolVersion;

        /// <summary>Last heartbeat round trip in milliseconds. 0 until the first pong.</summary>
        public readonly long RttMs;

        /// <summary>KCP retransmission timeout in milliseconds. 0 without a KCP session.</summary>
        public readonly uint KcpRtoMs;

        /// <summary>Milliseconds of server silence counted so far. 0 while the link is healthy.</summary>
        public readonly long KcpSilenceMs;

        /// <summary>KCP segments sent and not yet acknowledged.</summary>
        public readonly int KcpPendingSendSegments;

        /// <summary>UDP datagrams accepted by the KCP session.</summary>
        public readonly long DatagramsReceived;

        /// <summary>UDP datagrams discarded (failed decryption or checksum). Nonzero points at a transport key mismatch or corruption.</summary>
        public readonly long DatagramsRejected;

        /// <summary>Frames decoded off the wire, of every type (including heartbeats).</summary>
        public readonly long FramesReceived;

        /// <summary>Frames handed to the transport.</summary>
        public readonly long FramesSent;

        /// <summary>Frame body bytes read, after transport decryption and before the sealed session is opened. No length prefix, no KCP/UDP headers.</summary>
        public readonly long BytesReceived;

        /// <summary>Frame body bytes written, counted like <see cref="BytesReceived"/>.</summary>
        public readonly long BytesSent;

        /// <summary>Outbound frames dropped because the send queue was full.</summary>
        public readonly long SendQueueDrops;

        /// <summary>Resync (keyframe) requests sent.</summary>
        public readonly long ResyncRequests;

        /// <summary>Snapshots dropped because their tick was not above the world's tick.</summary>
        public readonly int StaleSnapshotsDropped;

        /// <summary>Snapshots discarded for naming an entity handle the client had no binding for.</summary>
        public readonly long UnresolvedSnapshots;

        /// <summary>Snapshots that resolved and were raised.</summary>
        public readonly long SnapshotsReceived;

        /// <summary>Highest server tick seen. 0 without a session.</summary>
        public readonly long ServerTick;

        /// <summary>Highest input tick the server acknowledged. 0 without a session.</summary>
        public readonly long AckTick;

        /// <summary>Command-channel requests sent and not yet answered.</summary>
        public readonly int PendingCommands;

        /// <summary>Reconnect attempts started over the client's life (every raise of <c>ReconnectAttemptStarted</c>).</summary>
        public readonly int ReconnectAttempts;

        public NetworkClientDiagnostics(
            NetworkClientState state, int generation, uint serverProtocolVersion, long rttMs,
            uint kcpRtoMs, long kcpSilenceMs, int kcpPendingSendSegments,
            long datagramsReceived, long datagramsRejected,
            long framesReceived, long framesSent, long bytesReceived, long bytesSent,
            long sendQueueDrops, long resyncRequests, int staleSnapshotsDropped,
            long unresolvedSnapshots, long snapshotsReceived, long serverTick, long ackTick,
            int pendingCommands, int reconnectAttempts)
        {
            State = state;
            Generation = generation;
            ServerProtocolVersion = serverProtocolVersion;
            RttMs = rttMs;
            KcpRtoMs = kcpRtoMs;
            KcpSilenceMs = kcpSilenceMs;
            KcpPendingSendSegments = kcpPendingSendSegments;
            DatagramsReceived = datagramsReceived;
            DatagramsRejected = datagramsRejected;
            FramesReceived = framesReceived;
            FramesSent = framesSent;
            BytesReceived = bytesReceived;
            BytesSent = bytesSent;
            SendQueueDrops = sendQueueDrops;
            ResyncRequests = resyncRequests;
            StaleSnapshotsDropped = staleSnapshotsDropped;
            UnresolvedSnapshots = unresolvedSnapshots;
            SnapshotsReceived = snapshotsReceived;
            ServerTick = serverTick;
            AckTick = ackTick;
            PendingCommands = pendingCommands;
            ReconnectAttempts = reconnectAttempts;
        }
    }
}
