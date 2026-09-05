namespace Cuvara.Netcode.Diagnostics
{
    /// <summary>
    /// Structured network diagnostics snapshot for UI binding.
    /// Updated once per second by <see cref="NetworkDiagnosticsBridge"/>.
    /// </summary>
    public readonly struct NetworkDiagnosticsViewModel
    {
        /// <summary>Round-trip time in milliseconds.</summary>
        public readonly float RttMs;

        /// <summary>Downstream bandwidth in KB/s.</summary>
        public readonly float DownstreamKBps;

        /// <summary>Upstream bandwidth in KB/s.</summary>
        public readonly float UpstreamKBps;

        /// <summary>Snapshot receive rate in Hz.</summary>
        public readonly float SnapshotHz;

        /// <summary>Number of entities currently visible.</summary>
        public readonly int EntityCount;

        /// <summary>Server tick number.</summary>
        public readonly ulong ServerTick;

        /// <summary>Server tick rate in Hz.</summary>
        public readonly float ServerTickRate;

        /// <summary>Packet loss ratio 0–1 (0 = no loss).</summary>
        public readonly float PacketLoss;

        /// <summary>Connection uptime in seconds.</summary>
        public readonly float UptimeSeconds;

        public NetworkDiagnosticsViewModel(
            float rttMs, float downKBps, float upKBps, float snapshotHz,
            int entityCount, ulong serverTick, float serverTickRate,
            float packetLoss, float uptimeSeconds)
        {
            RttMs = rttMs;
            DownstreamKBps = downKBps;
            UpstreamKBps = upKBps;
            SnapshotHz = snapshotHz;
            EntityCount = entityCount;
            ServerTick = serverTick;
            ServerTickRate = serverTickRate;
            PacketLoss = packetLoss;
            UptimeSeconds = uptimeSeconds;
        }

        public override string ToString() =>
            $"RTT={RttMs:F0}ms | ↓{DownstreamKBps:F1}KB/s ↑{UpstreamKBps:F1}KB/s | {SnapshotHz:F1}Hz | {EntityCount} entities | tick {ServerTick}";
    }
}
