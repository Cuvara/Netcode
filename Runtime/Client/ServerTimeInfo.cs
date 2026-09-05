namespace Cuvara.Netcode.Client
{
    /// <summary>
    /// Human-readable server time snapshot for debug HUDs.
    /// Populated by <see cref="NetworkClient"/> each frame while in world.
    /// </summary>
    public readonly struct ServerTimeInfo
    {
        /// <summary>Latest server tick number.</summary>
        public readonly ulong Tick;

        /// <summary>Server tick rate in Hz (from join response).</summary>
        public readonly float TickRateHz;

        /// <summary>Estimated one-way latency in milliseconds.</summary>
        public readonly float LatencyMs;

        /// <summary>Seconds since the client entered the world.</summary>
        public readonly float SessionUptime;

        /// <summary>Number of snapshots received this session.</summary>
        public readonly int SnapshotsReceived;

        public ServerTimeInfo(ulong tick, float tickRateHz, float latencyMs, float sessionUptime, int snapshotsReceived)
        {
            Tick = tick;
            TickRateHz = tickRateHz;
            LatencyMs = latencyMs;
            SessionUptime = sessionUptime;
            SnapshotsReceived = snapshotsReceived;
        }

        public override string ToString() =>
            $"tick {Tick} @ {TickRateHz:F0}Hz | {LatencyMs:F0}ms | {SnapshotsReceived} snaps in {SessionUptime:F0}s";
    }
}
