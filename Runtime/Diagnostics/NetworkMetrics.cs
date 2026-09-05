using System;

namespace Cuvara.Netcode.Diagnostics
{
    /// <summary>
    /// Default <see cref="INetworkMetrics"/> implementation. Accumulates counters
    /// over a configurable window (default 5 s) and publishes a snapshot at the end
    /// of each window.
    /// </summary>
    public sealed class NetworkMetrics : INetworkMetrics
    {
        private readonly float _windowSeconds;

        // Accumulators for the current window.
        private float _elapsed;
        private int _snapshotsInWindow;
        private int _bytesSentInWindow;
        private int _bytesReceivedInWindow;
        private int _reconcilesInWindow;
        private float _correctionSumInWindow;
        private long _lastServerTick;
        private long _lastAckTick;

        // RTT exponential moving average.
        private float _rttEma;
        private float _rttVar;
        private bool _rttSeeded;

        public event Action<NetworkMetricsSnapshot> Updated;

        public NetworkMetricsSnapshot Current { get; private set; }

        /// <summary>
        /// Creates a metrics collector with the given window duration.
        /// </summary>
        /// <param name="windowSeconds">
        /// How often a snapshot is published. 5 s matches the existing health line
        /// cadence. Shorter windows are noisier; longer ones are less responsive.
        /// </param>
        public NetworkMetrics(float windowSeconds = 5f)
        {
            _windowSeconds = windowSeconds > 0f ? windowSeconds : 5f;
        }

        public void RecordSnapshot(long tick, long ackTick)
        {
            _snapshotsInWindow++;
            _lastServerTick = tick;
            _lastAckTick = ackTick;
        }

        public void RecordRtt(long rttMs)
        {
            float rtt = rttMs;
            if (!_rttSeeded)
            {
                _rttEma = rtt;
                _rttVar = 0f;
                _rttSeeded = true;
            }
            else
            {
                // Smoothed RTT and variance, same algorithm as TCP and KCP.
                float delta = rtt - _rttEma;
                _rttEma += delta * 0.125f;
                float absDelta = delta < 0 ? -delta : delta;
                _rttVar += (absDelta - _rttVar) * 0.25f;
            }
        }

        public void RecordBytes(int sent, int received)
        {
            _bytesSentInWindow += sent;
            _bytesReceivedInWindow += received;
        }

        public void RecordReconciliation(float correctionUnits)
        {
            _reconcilesInWindow++;
            _correctionSumInWindow += correctionUnits;
        }

        public void Tick(float deltaTime)
        {
            _elapsed += deltaTime;
            if (_elapsed < _windowSeconds) return;

            float window = _elapsed;
            var snapshot = new NetworkMetricsSnapshot
            {
                RttMs = _rttEma,
                RttJitterMs = _rttVar,
                SnapshotRate = _snapshotsInWindow / window,
                BytesSentPerSecond = _bytesSentInWindow / window,
                BytesReceivedPerSecond = _bytesReceivedInWindow / window,
                Reconciliations = _reconcilesInWindow,
                MeanCorrectionUnits = _reconcilesInWindow > 0
                    ? _correctionSumInWindow / _reconcilesInWindow
                    : 0f,
                ServerTick = _lastServerTick,
                AckTick = _lastAckTick,
                WindowSeconds = window
            };

            Current = snapshot;

            // Reset window accumulators.
            _elapsed = 0f;
            _snapshotsInWindow = 0;
            _bytesSentInWindow = 0;
            _bytesReceivedInWindow = 0;
            _reconcilesInWindow = 0;
            _correctionSumInWindow = 0f;

            Updated?.Invoke(snapshot);
        }

        public void Reset()
        {
            _elapsed = 0f;
            _snapshotsInWindow = 0;
            _bytesSentInWindow = 0;
            _bytesReceivedInWindow = 0;
            _reconcilesInWindow = 0;
            _correctionSumInWindow = 0f;
            _lastServerTick = 0;
            _lastAckTick = 0;
            _rttSeeded = false;
            _rttEma = 0f;
            _rttVar = 0f;
            Current = default;
        }
    }
}
