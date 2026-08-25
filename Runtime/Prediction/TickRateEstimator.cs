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

        private int _minGap;

        // A candidate narrower gap, and how many times it has been seen. A gap only
        // becomes the hold window on its SECOND sighting; see the Sample remarks.
        private int _candidateGap;
        private int _candidateCount;
        private long _firstTick;
        private double _firstSeconds;
        private long _lastTick;
        private double _lastSeconds;

        /// <summary>
        /// Base ticks between consecutive snapshots — the server's <c>WorldEvery</c>, and
        /// therefore the length of its held-movement window. Zero until two snapshots
        /// have been seen.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Snapshots are emitted once per world tick, so the gap between the base ticks
        /// two consecutive ones carry <i>is</i> the ratio of the two rates. Deriving it is
        /// one fewer field to advertise, one fewer constant to configure, and one fewer
        /// thing that can be configured wrongly.
        /// </para>
        /// <para>
        /// <b>This measures the snapshot cadence, and that is all it measures now.</b> It
        /// used to be taken as the hold window too, on the grounds that the server passed
        /// <c>_rates.WorldEvery</c> as <c>holdTicks</c> to <c>ApplyHeldMovement</c> and also
        /// emitted one snapshot per world tick. That was a coincidence of construction and
        /// not a guarantee anything on the wire made — and it stopped being true: the hold
        /// window is a silence timeout the two sides share as a constant, and the server
        /// does not size it from any rate. The prediction path reads
        /// <c>LocalMovePredictor.MaxBankedTicks</c>; a cadence measured here that is wrong
        /// now costs interpolation smoothness rather than a fixed mis-prediction ratio.
        /// </para>
        /// <para>
        /// It is not left to go unnoticed. A hold window wrong by a ratio produces a
        /// correction on every input, and the live measurement asserts that corrections
        /// stay near zero on a healthy link
        /// (<c>PredictionLatencyMeasurement</c>) — the assertion that found the missing
        /// hold in the first place. If that fires and the tick rate agrees, suspect this
        /// coupling before suspecting the arithmetic. `rpg-mmo-server#101` asks for the
        /// hold semantics to be specified normatively and for an advertised
        /// <c>hold_ticks</c> to be considered, which would retire the assumption instead
        /// of documenting it.
        /// </para>
        /// <para>
        /// <b>Minimum, not mean.</b> A dropped or coalesced snapshot only ever widens a
        /// gap, so the smallest observed gap is the true interval and an average is biased
        /// upward by exactly the losses. Overstating the window would make the client
        /// predict motion the server has already stopped.
        /// </para>
        /// <para>
        /// <b>But a minimum has to survive one narrow pair, and it did not.</b> The
        /// premise above — that nothing ever <i>narrows</i> a gap — is false at exactly one
        /// moment, and it is a moment every session passes through. The first snapshot
        /// after joining is a keyframe emitted when the join is handled, not on a world
        /// tick boundary, so the gap between it and the next scheduled snapshot is whatever
        /// the phase happens to be: 1, 2 or 3 base ticks rather than the cadence's 4. A
        /// running minimum that never recovers then pins the hold window at that number
        /// for the whole session.
        /// </para>
        /// <para>
        /// <b>That consequence has since been removed at the source, and the two-sighting
        /// rule below is kept anyway.</b> This number used to be fed straight to
        /// <c>LocalMovePredictor.SetHoldTicks</c> and used as the hold window, so a gap
        /// pinned at 1 by the join keyframe switched the hold off outright for the whole
        /// session and the avatar advanced for part of a tick and froze for the rest. The
        /// predictor now takes its window from <c>MaxBankedTicks</c> — the same silence
        /// timeout the server compiles against — and <c>SetHoldTicks</c> is diagnostic. The
        /// number still reaches the interpolation clock, where being wrong is a visible
        /// smoothness defect rather than a movement one, so it still has to be right.
        /// </para>
        /// <para>
        /// <b>So a narrower gap must be seen twice before it is adopted.</b> The true
        /// cadence repeats on every snapshot and reaches two sightings immediately; a
        /// one-off join keyframe, or a pair batched together by TCP, never does. This
        /// keeps the "minimum, not mean" property for genuine drops — which widen, and are
        /// still ignored — while refusing to let a single anomalous pair set the window.
        /// </para>
        /// </remarks>
        public int SnapshotTickGap => _minGap;

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

            long gap = tick - _lastTick;
            if (gap > 0 && gap < int.MaxValue && (_minGap == 0 || gap < _minGap))
            {
                // Confirm before adopting. One sighting of a narrow gap is as likely to be
                // the join keyframe's off-cadence phase, or two snapshots batched into one
                // read, as it is to be the cadence. Two sightings of the same gap is not.
                var candidate = (int)gap;
                if (candidate == _candidateGap)
                {
                    _candidateCount++;
                }
                else
                {
                    _candidateGap = candidate;
                    _candidateCount = 1;
                }

                if (_candidateCount >= 2)
                {
                    _minGap = candidate;
                }
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
            _minGap = 0;
            _candidateGap = 0;
            _candidateCount = 0;
            _firstTick = 0;
            _lastTick = 0;
            _firstSeconds = 0;
            _lastSeconds = 0;
        }
    }
}
