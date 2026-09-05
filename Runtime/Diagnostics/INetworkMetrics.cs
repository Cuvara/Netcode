using System;

namespace Cuvara.Netcode.Diagnostics
{
    /// <summary>
    /// Observable network metrics. Consumers subscribe to <see cref="Updated"/>
    /// and read the snapshot; the health line in the DOTS sample becomes one such
    /// consumer rather than computing its own counters.
    /// </summary>
    public interface INetworkMetrics
    {
        /// <summary>Raised after every metric window closes (default: every 5 s).</summary>
        event Action<NetworkMetricsSnapshot> Updated;

        /// <summary>The most recent snapshot, or default before the first window.</summary>
        NetworkMetricsSnapshot Current { get; }

        /// <summary>Record an inbound snapshot from the game server.</summary>
        void RecordSnapshot(long tick, long ackTick);

        /// <summary>Record a round-trip measurement from the heartbeat.</summary>
        void RecordRtt(long rttMs);

        /// <summary>Record bytes sent and received on the wire.</summary>
        void RecordBytes(int sent, int received);

        /// <summary>Record a prediction reconciliation correction.</summary>
        void RecordReconciliation(float correctionUnits);

        /// <summary>Advance the clock; call once per frame with unscaled delta time.</summary>
        void Tick(float deltaTime);

        /// <summary>Reset all counters (on disconnect or map transfer).</summary>
        void Reset();
    }

    /// <summary>
    /// An immutable point-in-time reading of all network metrics, produced at
    /// the end of each measurement window.
    /// </summary>
    public struct NetworkMetricsSnapshot
    {
        /// <summary>Smoothed round-trip time in milliseconds.</summary>
        public float RttMs;

        /// <summary>RTT jitter (standard deviation) in milliseconds.</summary>
        public float RttJitterMs;

        /// <summary>Snapshots received per second over the window.</summary>
        public float SnapshotRate;

        /// <summary>Bytes sent per second over the window.</summary>
        public float BytesSentPerSecond;

        /// <summary>Bytes received per second over the window.</summary>
        public float BytesReceivedPerSecond;

        /// <summary>Number of reconciliations in the window.</summary>
        public int Reconciliations;

        /// <summary>Mean prediction correction in units over the window.</summary>
        public float MeanCorrectionUnits;

        /// <summary>The newest server tick received.</summary>
        public long ServerTick;

        /// <summary>The newest acknowledged client tick.</summary>
        public long AckTick;

        /// <summary>Duration of the measurement window in seconds.</summary>
        public float WindowSeconds;
    }
}
