namespace Cuvara.Netcode.Prediction
{
    /// <summary>
    /// Recovers the server's base tick rate from the snapshots it sends, so a client can
    /// verify the advertised rate instead of trusting it — and can still predict when no
    /// rate is advertised at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How.</b> For any two snapshots, <c>(tick₂ − tick₁)</c> divided by the wall-clock
    /// interval between them <i>is</i> the base tick rate. This works even though snapshots
    /// arrive at the slower world rate, because the <c>tick</c> they carry is a <b>base</b>
    /// tick — consecutive snapshots simply differ by more than one. At 60 Hz simulated and
    /// 15 Hz sent, successive snapshots are four ticks apart and the arithmetic still
    /// yields 60.
    /// </para>
    /// <para>
    /// <b>Why bother when the server advertises it.</b> The advertised value is one number
    /// travelling one path; this is an independent observation of the thing that actually
    /// happened. `gameserver-dotnet/docs/API.md` recommends using it as a cross-check even
    /// when a rate is present, and the reason is specific: a client predicting at a wrong
    /// rate is wrong by a fixed <i>ratio</i> on every input, which lands under a typical
    /// correction threshold and smooths rather than snaps. It never announces itself. A
    /// second, independent measurement is the only thing that does.
    /// </para>
    /// <para>
    /// <b>What it is not.</b> Not a substitute for the advertised rate when one exists —
    /// this is sampled over a network and will be approximate; the advertised value is
    /// exact. Use this to <i>disagree</i>, not to replace.
    /// </para>
    /// </remarks>
    public sealed class TickRateEstimator
    {
        /// <summary>
        /// Wall-clock span the samples must cover before an estimate is offered.
        /// </summary>
        /// <remarks>
        /// A short window divides a small tick delta by a small, jittery interval and
        /// produces a confident-looking number that is mostly scheduling noise. One second
        /// at the default rates is ~15 snapshots and ~60 ticks, which is enough to separate
        /// the plausible rates from each other by a wide margin.
        /// </remarks>
        public const double MinimumWindowSeconds = 1.0;

        /// <summary>Snapshots required before an estimate is offered.</summary>
        public const int MinimumSamples = 5;

        /// <summary>
        /// Relative disagreement above which <see cref="Disagrees"/> reports a mismatch.
        /// </summary>
        /// <remarks>
        /// 15% is far wider than measurement noise over a one-second window and far
        /// narrower than the gaps between plausible rates — the nearest realistic pair is
        /// 15 and 20 Hz, a 33% step, and the failure that motivated this was 4×. It is
        /// sized to catch a wrong rate, not to police jitter.
        /// </remarks>
        public const float DisagreementTolerance = 0.15f;

        private long _firstTick;
        private double _firstSeconds;
        private long _lastTick;
        private double _lastSeconds;

        /// <summary>Snapshots sampled since the last <see cref="Reset"/>.</summary>
        public int Samples { get; private set; }

        /// <summary>
        /// Whether enough has been observed to offer <see cref="EstimatedHz"/>.
        /// </summary>
        public bool HasEstimate =>
            Samples >= MinimumSamples &&
            _lastSeconds - _firstSeconds >= MinimumWindowSeconds &&
            _lastTick > _firstTick;

        /// <summary>
        /// The measured base tick rate in Hz, or zero when <see cref="HasEstimate"/> is
        /// false. Zero means "not measured yet", never "the server is stopped".
        /// </summary>
        public float EstimatedHz =>
            HasEstimate
                ? (float)((_lastTick - _firstTick) / (_lastSeconds - _firstSeconds))
                : 0f;

        /// <summary>
        /// Records one snapshot arrival. Ticks that do not advance are ignored — a
        /// re-delivered or duplicate snapshot carries no timing information.
        /// </summary>
        /// <param name="tick">The snapshot's server tick, a base tick.</param>
        /// <param name="nowSeconds">Arrival time on any monotonic wall clock.</param>
        public void Sample(long tick, double nowSeconds)
        {
            if (Samples == 0)
            {
                _firstTick = tick;
                _firstSeconds = nowSeconds;
                _lastTick = tick;
                _lastSeconds = nowSeconds;
                Samples = 1;
                return;
            }

            if (tick <= _lastTick)
            {
                return;
            }

            _lastTick = tick;
            _lastSeconds = nowSeconds;
            Samples++;
        }

        /// <summary>
        /// Whether the measured rate contradicts <paramref name="advertisedHz"/> by more
        /// than <see cref="DisagreementTolerance"/>.
        /// </summary>
        /// <remarks>
        /// False while there is no estimate and false for a non-positive advertised rate:
        /// "not measured yet" and "nothing was advertised" are both absence of evidence,
        /// and reporting either as a disagreement would cry wolf during every join.
        /// </remarks>
        public bool Disagrees(int advertisedHz)
        {
            if (!HasEstimate || advertisedHz <= 0)
            {
                return false;
            }

            float relative = (EstimatedHz - advertisedHz) / advertisedHz;
            if (relative < 0f)
            {
                relative = -relative;
            }

            return relative > DisagreementTolerance;
        }

        /// <summary>Forgets every sample. For a new session or a map transfer.</summary>
        public void Reset()
        {
            Samples = 0;
            _firstTick = 0;
            _lastTick = 0;
            _firstSeconds = 0;
            _lastSeconds = 0;
        }
    }
}
