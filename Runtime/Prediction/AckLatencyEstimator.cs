using System;

namespace Cuvara.Netcode.Prediction
{
    /// <summary>
    /// Measures the pipeline constant the prediction clock's steering target needs and that
    /// <see cref="SnapshotStalenessEstimator"/> cannot see: how far the client's clock must
    /// lead the server's for the two to label the same input with the same tick number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The quantity.</b> The client applies an input at its OWN base tick. The server
    /// applies it at the tick its packet is drained on — the wire's <c>tick</c> field orders
    /// inputs and names the acknowledgement, it does not place the step in time — and reports
    /// it on a snapshot that is already old when the client acts on it. So the client's tick
    /// and the server's tick name the same moment only if the client leads by the uplink
    /// delay, which makes the required steering target <c>uplink + snapshot age</c>.
    /// </para>
    /// <para>
    /// <b>Why the staleness fit cannot supply it.</b> That estimator fits a LOWER ENVELOPE
    /// and reports the height above it, so it measures the variable delay and absorbs every
    /// constant into the fitted offset — deliberately, because a constant one-way delay is
    /// inseparable from the difference between the two clocks' origins. Modelled by varying
    /// the pipeline constant from 0 to 2 base ticks, its reading stays at 0.02–0.04 and the
    /// steering error stays at 0 throughout, while the reconcile corrects by one step per
    /// tick of it at every start and stop. It is invisible to every counter in the package,
    /// which is why it survived two rounds of fixes with everything else reading clean.
    /// </para>
    /// <para>
    /// <b>The measurement.</b> One observation is the time from sending input tick N to
    /// seeing the first snapshot whose <c>ack_tick</c> reaches N. That is
    /// <c>uplink + wait for the next snapshot + age</c>. The wait is the only term that
    /// varies — it sweeps as the send cadence and the snapshot cadence drift against each
    /// other — so the MINIMUM over enough observations converges on <c>uplink + age</c>. Same
    /// argument as <see cref="TickRateEstimator.SnapshotTickGap"/> and the staleness
    /// envelope: the interesting quantity is the floor, and a mean would measure the jitter
    /// sitting on top of it.
    /// </para>
    /// <para>
    /// It needs no new wire traffic and no server change. Both endpoints are already at the
    /// client: it stamped the input, and the acknowledgement is on the snapshot.
    /// </para>
    /// <para>
    /// <b>Where the sweep assumption fails, and why that is checked rather than assumed.</b>
    /// If the send cadence and the snapshot cadence are locked in phase, no observation ever
    /// catches a small wait: every one carries the same fixed wait, the minimum is the constant
    /// PLUS that wait, and the floor reads high by up to a whole snapshot interval. A lead too
    /// large is the original defect arriving from the other side, so this is not a tolerable
    /// failure mode — and it is not hypothetical, because a client sending at the world rate is
    /// sending at exactly the snapshot rate.
    /// </para>
    /// <para>
    /// What rescues it in practice is that the two cadences are driven by different clocks and
    /// drift against each other, so the wait sweeps. What makes it safe is that the sweep is
    /// <b>verified before a floor is offered</b>: the observations within an epoch must span at
    /// least <see cref="MinimumSweepFraction"/> of a snapshot interval, that interval being
    /// itself measured from the gaps between acknowledgements — see
    /// <see cref="AckIntervalSeconds"/>, which is a windowed statistic over a ring of them and
    /// was the single smallest gap until it was measured reading 25% low. Without the sweep there is
    /// no evidence the minimum is near the constant, so nothing is offered and the caller keeps
    /// whatever it used before.
    /// </para>
    /// </remarks>
    public sealed class AckLatencyEstimator
    {
        /// <summary>
        /// Observations required before a floor is offered.
        /// </summary>
        /// <remarks>
        /// <b>There is deliberately no provisional reading.</b> This number ADDS lead, and a
        /// lead invented from too little evidence steers the clock past the server — the
        /// defect this whole estimator exists to remove, arriving from the other side. The
        /// provisional path in <see cref="SnapshotStalenessEstimator"/> is safe because a
        /// reading there can only be clamped DOWNWARD to a figure already in use; there is no
        /// equivalent safe direction here, so nothing is offered until the floor means
        /// something. Eight observations is about half a second at a 15 Hz send rate.
        /// </remarks>
        public const int MinimumSamples = 8;

        /// <summary>How long each epoch collects before it becomes the previous one.</summary>
        /// <remarks>
        /// The floor is the smaller of this epoch's minimum and the last one's, so the memory
        /// is five to ten seconds. Long enough for the send and snapshot cadences to sweep
        /// against each other and expose a small wait; short enough that a route which has
        /// genuinely become slower is followed within about ten seconds rather than being
        /// held down by a measurement from the start of the session.
        /// </remarks>
        public const double EpochSeconds = 5.0;

        /// <summary>
        /// Beyond this, an observation is refused rather than folded in.
        /// </summary>
        /// <remarks>
        /// A floor is not a latency spike. A second of acknowledgement delay is a stall, a
        /// reconnect, or a process that was suspended — none of which describe the steady
        /// pipeline this is measuring, and all of which would steer the clock somewhere
        /// arbitrary if they were allowed to set the floor. Refused and counted, never
        /// clamped: a clamped bad observation is still wrong and now looks plausible.
        /// </remarks>
        public const double MaximumFloorSeconds = 1.0;

        /// <summary>Inputs remembered while they wait for an acknowledgement.</summary>
        /// <remarks>
        /// A ring, oldest dropped. At a 15 Hz send rate 64 is four seconds of unacknowledged
        /// input, which is far past the point at which the connection is the problem.
        /// </remarks>
        private const int PendingCapacity = 64;

        private readonly long[] _pendingTick = new long[PendingCapacity];
        private readonly double[] _pendingSentAt = new double[PendingCapacity];
        private int _head, _count;

        /// <summary>
        /// Share of a snapshot interval the observations must span before a floor is offered.
        /// </summary>
        /// <remarks>
        /// The evidence that the minimum is near the constant is that the varying term was seen
        /// to vary. Half an interval says the phase is sweeping rather than locked, and is small
        /// enough that a healthy client is not left without a reading.
        /// </remarks>
        public const double MinimumSweepFraction = 0.5;

        /// <summary>
        /// The quantiles the sweep is measured between.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Not the minimum and maximum, and that distinction is the whole guard.</b> The
        /// span used to be <c>max - min</c>, which a SINGLE observation satisfies: one lucky
        /// sample far from the rest makes a phase-locked link look swept, and the guard then
        /// certifies exactly the distribution it exists to refuse.
        /// </para>
        /// <para>
        /// Measured live, on a client sending at 15 Hz into a 15 Hz snapshot stream — the
        /// phase-locked case this class's remarks warn about by name — the input-to-ack
        /// distribution was <c>min 5.5 ms, median 58.8, p90 62.2</c>. That is not a spread, it
        /// is a lock with one outlier; <c>max - min</c> read 57 ms against a 33 ms requirement
        /// and passed. The floor then landed on the locked mode at <b>3.33 base ticks</b> while
        /// the harness's own observed minimum was <b>0.33</b> — ten times high, in the opposite
        /// direction from every failure this estimator had produced before, and it went
        /// straight into the steering lead.
        /// </para>
        /// <para>
        /// Between the tenth and ninetieth percentiles, one outlier moves nothing. The same
        /// data reads a span of about 12 ms and is correctly refused. This is the third time in
        /// this class that a statistic taken from extremes turned out to be silent about the
        /// distribution it was standing in for.
        /// </para>
        /// </remarks>
        public const double SweepLowQuantile = 0.10;

        /// <inheritdoc cref="SweepLowQuantile"/>
        public const double SweepHighQuantile = 0.90;

        /// <summary>
        /// Observations required before the span half of <see cref="SweptEnough"/> means
        /// anything: the smallest count at which BOTH sweep quantiles land strictly inside the
        /// sorted observations rather than on an extremum.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Below this the span test is <c>max - min</c> again — the exact statistic the
        /// guard was rewritten to stop being.</b> <see cref="Quantile"/> truncates
        /// <c>q * n</c> to an index, so at <c>n = 8</c> the tenth percentile is index 0 and the
        /// ninetieth is index 7: the minimum and the maximum. The low end escapes the minimum
        /// at <c>n = 10</c> and the high end escapes the maximum only at <c>n = 11</c>
        /// (<c>(int)(0.9 * 10) = 9</c>, which is still the last index of ten), so the whole of
        /// <c>8 .. 10</c> is a window in which the guard's own remarks do not describe what it
        /// computes.
        /// </para>
        /// <para>
        /// <b>Measured consequence, and why this is a refusal rather than a statistic change.</b>
        /// A body at 30-44 ms of a 67 ms interval with one near-instant observation — the
        /// gather-catch shape <see cref="FloorPercentile"/> already documents — occupies three
        /// buckets, so occupancy passes, and reads a span of 39 ms against a 33 ms requirement
        /// at <c>n = 8</c> where the same data reads 14 ms and is refused from <c>n = 10</c> on.
        /// Certified as swept, its floor is taken at index 0 as well: <b>0.20 base ticks against
        /// a body minimum of 1.81</b>, which is the ten-times under-read this estimator's
        /// history is made of. And a floor is not merely added when it appears —
        /// <c>WorldViewBinder.TargetLeadTicks</c> makes it DISPLACE the round-trip fallback — so
        /// a verdict inside this window does reach the lead, and reaches it twice.
        /// </para>
        /// <para>
        /// <b>Why the floor and not an interpolated quantile.</b> Interpolating would make the
        /// index meaningful at every count, but it changes <see cref="FloorSeconds"/> as well as
        /// the span, and <see cref="FloorPercentile"/> is under a deliberately isolated
        /// measurement — shipping a new quantile estimator underneath it would confound exactly
        /// the reading that constant's remarks say must be read rather than argued. Leaning on
        /// occupancy alone in the window was the other candidate and is worse: it drops the
        /// extent test in the one window where the extent test is wrong, when the two are kept
        /// precisely because neither implies the other. Refusing until the arithmetic is honest
        /// costs three observations — about 0.2 s at a 15 Hz send rate, against the 120-140
        /// observations a live arm collects — and it can only withhold a floor, never invent
        /// one, which is the only safe direction here.
        /// </para>
        /// <para>
        /// Derived from the quantiles rather than written as 11, so that changing either
        /// constant cannot silently reopen the window.
        /// </para>
        /// </remarks>
        public static readonly int MinimumSweepSamples = SmallestInteriorSampleCount();

        private static int SmallestInteriorSampleCount()
        {
            for (var n = MinimumSamples; n <= ObservationCapacity; n++)
            {
                if ((int)(SweepLowQuantile * n) > 0 && (int)(SweepHighQuantile * n) < n - 1)
                {
                    return n;
                }
            }

            return ObservationCapacity;
        }

        /// <summary>
        /// Buckets the snapshot interval is divided into, and how many of them the observations
        /// must occupy.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The property that matters here is that this cannot be satisfied by a small number
        /// of samples, and it must not be weakened into something that can.</b> The span between
        /// two order statistics is an improvement on <c>max - min</c> — it takes a tenth of the
        /// observations to move each end rather than one — but it is still a statement about two
        /// points. Occupancy is a statement about the whole distribution: <see cref="MinimumOccupiedBuckets"/>
        /// samples in distinct buckets are needed at minimum, and no arrangement of two
        /// observations can produce it.
        /// </para>
        /// <para>
        /// <b>Do not replace this with a comparison of two quantiles, however wide.</b> That is
        /// the simplification this guard has already been through once: it began as
        /// <c>max - min</c>, which one outlier satisfied, and the whole defect it then certified
        /// — a phase-locked link floored at ten times its own observed minimum — followed from a
        /// span standing in for a distribution. Both tests are kept deliberately; the span
        /// bounds the extent, the occupancy bounds the shape, and neither implies the other.
        /// </para>
        /// </remarks>
        public const int SweepBuckets = 8;

        /// <inheritdoc cref="SweepBuckets"/>
        public const int MinimumOccupiedBuckets = 3;

        /// <summary>
        /// Gaps between acknowledgements the snapshot interval is measured over. A ring,
        /// oldest dropped — the same memory, and for the same reason, as the observation ring.
        /// </summary>
        public const int GapCapacity = 128;

        /// <summary>
        /// How many times the shortest gap a gap may be and still be read as spanning ONE
        /// snapshot interval rather than covering a dropped snapshot.
        /// </summary>
        /// <remarks>
        /// Halfway to the next multiple. A gap covering a dropped snapshot is at least
        /// <c>2T - jitter</c> and the shortest gap is at most <c>T</c>, so this excludes every
        /// doubled gap unconditionally; it admits every single-interval gap as long as the
        /// jitter range is under a fifth of the cadence, and where it does not, the gaps it
        /// wrongly excludes are the LONG ones — which shrinks the window's mean, in the lenient
        /// direction. It is a midpoint between two multiples, not a tolerance to be tuned.
        /// </remarks>
        public const double SingleIntervalFactor = 1.5;

        /// <summary>
        /// The most consecutive gaps the interval is averaged over.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Adjacent gaps telescope, so averaging <c>K</c> of them cancels all but <c>1/K</c> of
        /// the arrival jitter — that is the whole of the correction. The cost is that the
        /// window must be free of dropped snapshots, which gets less likely as <c>K</c> grows,
        /// so the reading uses the longest drop-free run the link actually delivered, capped
        /// here. <b>The cap is where the measured gain stops, not a tuned parameter:</b> over
        /// 43 200 arms (four jitter ranges × six jitter shapes × six loss rates × 300 seeds)
        /// the mean absolute error falls 43.4% → 38.7% → 37.2% → <b>36.5%</b> → 35.8% → 35.6%
        /// at caps 4, 6, 8, 12 and 16. Eight also keeps the error on realistic arms inside one
        /// <see cref="SweepBuckets"/> division of the interval (12.5%), which is the resolution
        /// anything scaled by this reading actually has, and spans about half a second at 15 Hz
        /// — short enough that the cadence is still one cadence across it.
        /// </para>
        /// </remarks>
        public const int IntervalWindowMax = 8;

        /// <summary>
        /// Where in the observation distribution the floor is taken from: the minimum.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This was briefly a tenth-percentile quantile, and reverting it is the honest
        /// outcome of fixing the sweep guard.</b> The quantile was introduced because an
        /// extremum over ~140 observations reported 0.17 base ticks on a loaded run whose
        /// input-to-acknowledgement distribution had a minimum of 1.39 — a real best moment,
        /// and a useless description of what the pipeline cost.
        /// </para>
        /// <para>
        /// That reasoning was sound and aimed at the wrong layer. The distribution it was
        /// solving for — a tight body at 23–33 ms with a handful of near-instant observations —
        /// is one whose wait term never swept, and <see cref="SweptEnough"/> should have refused
        /// it outright rather than being asked to floor it well. It did not, because its span
        /// was taken from the extremes and a single outlier satisfied it. With the sweep
        /// measured between quantiles instead, that distribution is refused, no floor is offered
        /// at all, and there is nothing left for a percentile to protect against.
        /// </para>
        /// <para>
        /// <b>The revert to the minimum is prepared and deliberately NOT applied here.</b> The
        /// expectation is that the sweep fix makes this choice moot — a genuinely swept
        /// distribution has its tenth percentile a hair above its minimum, and the distributions
        /// where they diverge are now refused before any statistic is taken. But "expectation"
        /// is the word that has cost this investigation the most: if the sweep fix and the
        /// statistic change ship together and the floor comes back correct, nothing distinguishes
        /// which one did the work, and the answer would have to be reasoned rather than read.
        /// So the sweep fix ships alone and this constant is measured, not argued about.
        /// </para>
        /// <para>
        /// <b>MEASURED, AND THE PRE-REGISTERED REVERT WAS NOT EXECUTED.</b> The floor WAS still
        /// inflated — across eight live arms at two snapshot rates it tracked
        /// <c>intercept + 0.1 × slope</c> to two decimals, against a true pipeline constant of
        /// 0.14–0.28 base ticks. So the rule's CONDITION was met. Its PREMISE was not: it
        /// assumed a working sweep guard makes the minimum safe, and the guard cannot see the
        /// sparse left-tail contamination that halves a minimum. <b>A pre-registered rule whose
        /// premise is falsified by later evidence must not be executed on the strength of its
        /// condition alone</b> — pre-registration protects against reading numbers backwards, not
        /// against the reasoning that set the rule being wrong, and the two look identical from
        /// inside the rule.
        /// </para>
        /// <para>
        /// So the percentile stays and its measured bias is subtracted instead. The
        /// bimodal-under-load justification of record is NOT what keeps it — that regime was
        /// looked for under an 8-player load and did not appear, and a constant defended by a
        /// regime nobody can produce is not defended. What keeps it is a claim about DIRECTION
        /// that needs no contamination rate: the raw tenth percentile over-leads on every clean
        /// run, and corrected it is unbiased on clean data and under-reads under contamination —
        /// safe on clean data and safe when wrong. See <see cref="ConservativeFloorTicks"/>.
        /// </para>
        /// <para>
        /// Either way the lesson does not depend on the outcome: <b>a statistic cannot repair a
        /// guard</b>, and reaching for a more robust one is a sign the guard above it is
        /// admitting data it should not.
        /// </para>
        /// </remarks>
        public const double FloorPercentile = 0.10;

        /// <summary>
        /// Observations the quantile is taken over. A ring, oldest dropped.
        /// </summary>
        /// <remarks>
        /// At a 15 Hz send rate 128 is about eight seconds — deliberately the same memory the
        /// two-epoch minimum had, so a route that has genuinely become slower is still followed
        /// within about ten seconds rather than held down by the start of the session.
        /// </remarks>
        private const int ObservationCapacity = 128;

        private readonly double[] _observations = new double[ObservationCapacity];
        private readonly double[] _sortScratch = new double[ObservationCapacity];
        private int _obsHead, _obsCount;

        /// <summary>
        /// The quantiles the floor's bias correction is fitted through — the same six the
        /// measurement harness prints its ladder at, so the runtime correction and the reported
        /// ladder are the same line rather than two lines that happen to agree.
        /// </summary>
        public static readonly double[] LadderQuantiles = { 0.00, 0.10, 0.25, 0.50, 0.75, 0.90 };

        /// <summary>
        /// How far a ladder point may stray from the fitted line, as a fraction of the fitted
        /// slope, before the affine sweep model is treated as falsified. See
        /// <see cref="TryFitLadder"/> for why it is a fraction rather than a constant.
        /// </summary>
        public const double LadderStraightnessFraction = 0.10;

        private readonly double[] _ladderScratch = new double[LadderQuantiles.Length];

        private double _epochMin = double.MaxValue;
        private double _epochMax = double.MinValue;
        private double _previousEpochMin = double.MaxValue;
        private double _previousEpochMax = double.MinValue;
        private double _epochStartedAt;
        private bool _haveEpochStart;

        // Gaps between successive acknowledgements, oldest dropped: the snapshot interval is a
        // statistic over this ring rather than a running extremum. See AckIntervalSeconds.
        private readonly double[] _gaps = new double[GapCapacity];
        private int _gapHead, _gapCount;

        // The reading, recomputed whenever a gap is appended so every consumer in a frame sees
        // one value rather than each re-deriving it from a ring that is still filling.
        private double _ackIntervalSeconds;

        private double _lastAckAt;
        private long _lastAckTick;

        // The newest input tick this client has ever stamped. An acknowledgement past it did
        // not come from this client's numbering. See AckAheadOfSend.
        private long _maxSentTick;

        // The rate the last acknowledgement was folded in at, so diagnostics can report in
        // base ticks without the caller having to pass it back.
        private float _lastBaseHz;

        /// <summary>Acknowledged observations folded in since construction or <see cref="Reset"/>.</summary>
        public int Samples { get; private set; }

        /// <summary>
        /// Inputs drained by an acknowledgement that also covered a newer one, and therefore
        /// NOT folded into the statistics. See <see cref="RecordAck"/>.
        /// </summary>
        /// <remarks>
        /// These are real inputs and their acknowledgement is real; what is not real is the
        /// <i>interval</i>, because such an input waited for an acknowledgement that a later
        /// input had already earned. Counted rather than silently dropped: a large share of
        /// them means the send cadence is running well ahead of the acknowledgement cadence,
        /// which is worth seeing.
        /// </remarks>
        public int Superseded { get; private set; }

        /// <summary>Observations refused as implausible for a floor. See <see cref="MaximumFloorSeconds"/>.</summary>
        public int Refused { get; private set; }

        /// <summary>
        /// Acknowledgements naming an input tick this client has never sent, and the inputs
        /// discarded because of them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is a wire invariant, and breaking it produces a floor far BELOW anything the
        /// route can do.</b> <c>ack_tick</c> is defined as this client's newest accepted input
        /// tick, so an acknowledgement greater than the newest tick this client has stamped
        /// cannot be about this client's inputs. It is the server still holding the previous
        /// session's <c>LastInputTick</c> for the same user while a fresh connection restarts
        /// its numbering at 1 — a reconnect onto a server that has not yet reaped the old
        /// player, and in a test suite, simply the previous run.
        /// </para>
        /// <para>
        /// Left unguarded it is not a small error. Every input the new session sends satisfies
        /// <c>tick &lt;= ackTick</c> the instant it is sent, so it is retired by the very next
        /// snapshot and timed at the wait for one client frame: single-digit milliseconds
        /// against a real pipeline of twenty-five. A minimum filter then holds that for the
        /// whole of its epoch memory, and the floor reads a fifth of the truth — which is the
        /// under-read this estimator was twice suspected of and never actually had. Measured:
        /// an estimator floor of 0.17 base ticks on a run whose input-to-acknowledgement
        /// distribution had a MINIMUM of 1.39 and a p90 of 1.95.
        /// </para>
        /// <para>
        /// So an acknowledgement past <c>_maxSentTick</c> discards the pending ring rather than
        /// timing it, and is counted here. Counted and not silent, because the condition means
        /// something real about the connection: a client that sees this after its first seconds
        /// is talking to a server that thinks it is someone else.
        /// </para>
        /// </remarks>
        public int AckAheadOfSend { get; private set; }

        /// <summary>
        /// Whether <see cref="FloorSeconds"/> and <see cref="FloorTicks"/> mean anything: enough
        /// observations, a positive floor, and evidence that the wait term swept.
        /// </summary>
        public bool HasEstimate =>
            Samples >= MinimumSamples && FloorSeconds > 0.0 && SweptEnough;

        /// <summary>
        /// Whether the observations have spanned enough of a snapshot interval for their minimum
        /// to be near the constant rather than near the constant plus a fixed wait.
        /// </summary>
        /// <remarks>
        /// Exposed so a consumer can tell "no floor yet" from "this link never sweeps", which are
        /// different problems with different answers and read identically otherwise.
        /// </remarks>
        public bool SweptEnough
        {
            get
            {
                if (_gapCount == 0) return false;

                // NOT MinimumSamples. Below MinimumSweepSamples the two quantiles below ARE
                // the minimum and the maximum, so the span test is max - min and this guard
                // silently becomes the thing it was rewritten to stop being. See that
                // constant for the distribution that gets through the window and for why the
                // answer is a refusal rather than a better statistic.
                if (_obsCount < MinimumSweepSamples) return false;

                double lo = Quantile(SweepLowQuantile);
                double hi = Quantile(SweepHighQuantile);

                if (hi - lo < _ackIntervalSeconds * MinimumSweepFraction)
                {
                    return false;
                }

                // EXTENT IS NOT SHAPE. The span above says the observations reach across the
                // interval; it does not say they are spread through it, and two order statistics
                // cannot. See SweepBuckets.
                return OccupiedBuckets() >= MinimumOccupiedBuckets;
            }
        }

        /// <summary>
        /// A quantile of the observations the floor is computed from, in base ticks — for
        /// diagnostics, so a consumer can see the distribution rather than infer it.
        /// </summary>
        /// <remarks>
        /// <b>Exposed because two statistics have now been chosen from the wrong evidence.</b>
        /// The measurement harness times ~20 sample inputs; this estimator times every send,
        /// several times as many and at a different phase. When the two floors disagree there is
        /// no way to tell which distribution is unusual without seeing this one, and both wrong
        /// choices made in this cycle were made by reasoning about the harness's twenty.
        /// </remarks>
        public float ObservationQuantileTicks(double q)
        {
            if (_obsCount == 0 || _lastBaseHz <= 0f) return 0f;
            return (float)(Quantile(q) * _lastBaseHz);
        }

        /// <summary>
        /// How many of <see cref="SweepBuckets"/> equal divisions of the snapshot interval hold
        /// at least one observation, measured from the smallest observation upward.
        /// </summary>
        private int OccupiedBuckets()
        {
            if (_obsCount == 0 || _gapCount == 0) return 0;

            double lo = double.MaxValue;
            for (var i = 0; i < _obsCount; i++)
            {
                if (_observations[i] < lo) lo = _observations[i];
            }

            double width = _ackIntervalSeconds / SweepBuckets;
            if (width <= 0) return 0;

            int mask = 0;
            for (var i = 0; i < _obsCount; i++)
            {
                int bucket = (int)((_observations[i] - lo) / width);
                if (bucket < 0) bucket = 0;
                if (bucket >= SweepBuckets) bucket = SweepBuckets - 1;
                mask |= 1 << bucket;
            }

            int count = 0;
            while (mask != 0)
            {
                count += mask & 1;
                mask >>= 1;
            }

            return count;
        }

        /// <summary>
        /// The requested quantile of the observations held, in seconds, or 0 when there are
        /// none. Sorted per call; the ring is 128 entries and this runs at the snapshot rate.
        /// </summary>
        private double Quantile(double q)
        {
            if (_obsCount == 0) return 0.0;

            SortObservations();
            return _sortScratch[QuantileIndex(q, _obsCount)];
        }

        /// <summary>
        /// Copies the observation ring into <c>_sortScratch</c> in ascending order and returns
        /// how many entries are live. One sort serves every order statistic taken from it, so
        /// the ladder below reads six points off ONE ring in ONE pass — the same property the
        /// harness's ladder was given, and for the same reason: six points taken across
        /// separate sorts of a ring that is still filling do not describe one distribution.
        /// </summary>
        private int SortObservations()
        {
            Array.Copy(_observations, _sortScratch, _obsCount);
            Array.Sort(_sortScratch, 0, _obsCount);
            return _obsCount;
        }

        /// <summary>The index the quantile <paramref name="q"/> reads at, over sorted data.</summary>
        private static int QuantileIndex(double q, int count)
        {
            int index = (int)(q * count);
            if (index >= count) index = count - 1;
            if (index < 0) index = 0;
            return index;
        }

        /// <summary>
        /// Fits the observation quantiles as a LINE and returns whether the fit is usable.
        /// </summary>
        /// <param name="slopeSeconds">
        /// The fitted slope, seconds per unit q — the range the wait term actually swept.
        /// </param>
        /// <param name="worstResidualSeconds">
        /// The furthest any ladder point strays from the fitted line.
        /// </param>
        /// <remarks>
        /// <para>
        /// One observation is <c>constant + wait</c> and the wait sweeps a snapshot interval,
        /// so the quantiles are affine in <c>q</c>: <c>quantile(q) = C + q · S</c>. The slope
        /// is therefore the swept range, measured from this run's own observations rather than
        /// assumed from a configured snapshot rate — which is what makes the floor correction
        /// in <see cref="ConservativeFloorTicks"/> self-correcting.
        /// </para>
        /// <para>
        /// <b>The fit is a guard before it is an estimate.</b> A ladder that is not straight
        /// falsifies the affine model instead of returning a number from it, which is the
        /// property a single order statistic can never have. So this returns FALSE rather than
        /// a best effort, and the caller refuses to correct rather than correcting by a slope
        /// that describes nothing. That is the package's own rule — a statistic cannot repair a
        /// guard — applied to the correction rather than to the statistic.
        /// </para>
        /// <para>
        /// <b>The straightness tolerance is scale-free on purpose.</b> A tenth of the fitted
        /// slope, not a constant in ticks: tying it to the range the ladder spans keeps it
        /// meaningful at any snapshot rate and stops it becoming a number somebody later tunes
        /// to make a run look good. It is the same rule the measurement harness already prints
        /// its ladder verdict with, and the eight live arms measured against it read worst
        /// residuals of 0.03–0.11 base ticks against slopes near 2.1, so a clean run clears it
        /// by a factor of two or more without the tolerance having been chosen to let it.
        /// </para>
        /// <para>
        /// <b>The six quantiles must land on six distinct samples.</b> A quantile is an index
        /// into sorted data, so on a short ring two of them collide and the "six-point" fit is
        /// really a five-point fit with one point double-weighted at the bottom — which tilts
        /// the very end the correction is read from. This is a property of the data held, not a
        /// threshold: it is checked directly rather than encoded as a minimum sample count.
        /// </para>
        /// </remarks>
        private bool TryFitLadder(out double slopeSeconds, out double worstResidualSeconds)
        {
            slopeSeconds = 0.0;
            worstResidualSeconds = 0.0;

            int count = SortObservations();
            if (count < LadderQuantiles.Length) return false;

            // Six points off ONE sorted ring, and only if they are six DIFFERENT observations.
            double[] y = _ladderScratch;
            int previousIndex = -1;
            for (var i = 0; i < LadderQuantiles.Length; i++)
            {
                int index = QuantileIndex(LadderQuantiles[i], count);
                if (index <= previousIndex) return false;
                previousIndex = index;
                y[i] = _sortScratch[index];
            }

            // Ordinary least squares through (q, seconds). Deliberately not weighted and
            // deliberately not clever: the point is to see whether the points lie on a line.
            double meanQ = 0.0, meanY = 0.0;
            for (var i = 0; i < y.Length; i++) { meanQ += LadderQuantiles[i]; meanY += y[i]; }
            meanQ /= y.Length;
            meanY /= y.Length;

            double sxy = 0.0, sxx = 0.0;
            for (var i = 0; i < y.Length; i++)
            {
                double dq = LadderQuantiles[i] - meanQ;
                sxy += dq * (y[i] - meanY);
                sxx += dq * dq;
            }

            if (sxx <= 0.0) return false;

            double slope = sxy / sxx;
            if (double.IsNaN(slope) || double.IsInfinity(slope) || slope <= 0.0)
            {
                // A flat or descending ladder is not a swept wait. Nothing to subtract.
                return false;
            }

            double intercept = meanY - slope * meanQ;

            double worst = 0.0;
            for (var i = 0; i < y.Length; i++)
            {
                double residual = Math.Abs(y[i] - (intercept + slope * LadderQuantiles[i]));
                if (residual > worst) worst = residual;
            }

            slopeSeconds = slope;
            worstResidualSeconds = worst;

            return worst <= slope * LadderStraightnessFraction;
        }

        /// <summary>The snapshot interval as measured from acknowledgement arrivals, seconds.</summary>
        /// <remarks>
        /// <para>
        /// <b>This was the smallest gap between arrivals, and that read the cadence 25% low.</b>
        /// Everywhere else in this class a minimum is right because the quantity can only be
        /// inflated. A GAP is not that quantity: one arrival late and the next on time shortens
        /// the gap between them by the whole of the first arrival's delay, so the gap
        /// distribution STRADDLES the cadence and its minimum is biased low by the jitter range
        /// — measured at 50.000 ms against a true 66.667 ms.
        /// </para>
        /// <para>
        /// <b>The statistic: the smallest mean of <see cref="IntervalWindowMax"/> consecutive
        /// gaps that are all single-interval, less the observed jitter spread over the same
        /// window, never below the ring minimum.</b> Adjacent gaps telescope, so a window of
        /// <c>K</c> gaps is <c>K·T</c> plus the difference of two arrival delays, and its mean
        /// carries only <c>1/K</c> of the jitter. Dropped snapshots are excluded from the window
        /// rather than averaged through — that is the whole of the difference from the attempt
        /// this replaces.
        /// </para>
        /// <para>
        /// <b>Why the two obvious statistics are both wrong, each measured rather than argued.</b>
        /// The smallest mean of two ADJACENT gaps telescopes correctly and provably never reads
        /// below the old minimum; at one snapshot in three lost it reads <b>99.999 ms against
        /// 66.667 — 50% HIGH</b>, because no adjacent pair is then free of a drop. A raw low
        /// PERCENTILE of the gaps — the move this class made for <see cref="FloorPercentile"/> —
        /// fails the other way on the jitter case: the delay pattern puts three gaps above the
        /// cadence for every one below, so the tenth percentile is still the minimum (50.000 ms)
        /// and the twenty-fifth reads <b>72.222 ms, 8.3% HIGH</b>. A percentile of gaps is not
        /// the same move as a percentile of waits, because a wait cannot fall below the constant
        /// and a gap can.
        /// </para>
        /// <para>
        /// <b>The leniency property, and why the subtraction is load-bearing.</b> A window mean
        /// is <c>T + (delay(n+K) − delay(n))/K</c>, which is an UNBIASED estimate of the cadence
        /// and therefore lands above it about half the time; the guard requires this reading to
        /// stay at or below the cadence. So the observed jitter spread — the widest
        /// single-interval gap less the narrowest — is subtracted over the same window, which
        /// bounds that term for any jitter that is stationary across the window. Measured: over
        /// the 43 200 arms above the corrected reading crossed the cadence <b>zero</b> times
        /// where the old minimum did not, and was never further from the cadence than the old
        /// minimum in ANY arm; without the subtraction the same sweep reads up to <b>7.1%
        /// HIGH</b>. The subtraction is zero when there is no jitter, so an ideal cadence still
        /// reads exactly, dropped snapshots or not.
        /// </para>
        /// <para>
        /// <b>What neither statistic can do, stated here rather than left to be rediscovered.</b>
        /// Both this and the old minimum need at least one observed gap that spans exactly one
        /// cadence interval. Under periodic loss phase-locked to the arrival jitter that gap can
        /// be missing: at one snapshot in two, with the dropped arrival always the on-time one,
        /// every remaining gap spans two intervals and BOTH statistics read about twice the
        /// cadence — the old minimum 122.222 ms, this one 130.556 ms, against 66.667. That is
        /// the forbidden direction, it is a property of the link rather than of the statistic,
        /// and it is indistinguishable from a genuinely halved snapshot rate by anything present
        /// in this class. <see cref="AckIntervalWindow"/> is what a reader has to see it with.
        /// </para>
        /// </remarks>
        public double AckIntervalSeconds => _ackIntervalSeconds;

        /// <summary>
        /// How many consecutive gaps <see cref="AckIntervalSeconds"/> was averaged over: 0 with
        /// no gaps yet, <b>1 when the windowed statistic could not be formed and the reading is
        /// the bare ring minimum</b>, and up to <see cref="IntervalWindowMax"/> otherwise.
        /// </summary>
        /// <remarks>
        /// The fallback is a second claim about the same quantity, so it is reported rather than
        /// taken silently: a 1 here says the link never delivered two snapshots in a row inside
        /// the ring, and that the reading is therefore the 25%-low statistic this one replaced.
        /// </remarks>
        public int AckIntervalWindow { get; private set; }

        /// <summary>Appends one inter-arrival gap and recomputes <see cref="AckIntervalSeconds"/>.</summary>
        private void AppendGap(double gap)
        {
            _gaps[_gapHead] = gap;
            _gapHead = (_gapHead + 1) % GapCapacity;
            if (_gapCount < GapCapacity) _gapCount++;

            RecomputeAckInterval();
        }

        /// <summary>The gap <paramref name="i"/> places from the oldest one held.</summary>
        private double GapAt(int i) =>
            _gaps[(_gapHead - _gapCount + i + GapCapacity * 2) % GapCapacity];

        /// <summary>
        /// The snapshot interval over the gap ring. See <see cref="AckIntervalSeconds"/> for the
        /// statistic, the two rejected ones, and the measurements that separate them.
        /// </summary>
        private void RecomputeAckInterval()
        {
            if (_gapCount == 0)
            {
                _ackIntervalSeconds = 0.0;
                AckIntervalWindow = 0;
                return;
            }

            double min = double.MaxValue;
            for (var i = 0; i < _gapCount; i++)
            {
                double g = GapAt(i);
                if (g < min) min = g;
            }

            // A gap past this covers a dropped snapshot and is not a measurement of the cadence.
            double single = min * SingleIntervalFactor;

            // The widest single-interval gap bounds how far one arrival's delay can move a
            // window mean, and is what the window's share of it is subtracted from below.
            double widest = min;
            int longestRun = 0, run = 0;
            for (var i = 0; i < _gapCount; i++)
            {
                double g = GapAt(i);
                if (g > single) { run = 0; continue; }
                if (g > widest) widest = g;
                run++;
                if (run > longestRun) longestRun = run;
            }

            int window = longestRun < IntervalWindowMax ? longestRun : IntervalWindowMax;
            if (window < 2)
            {
                // FALLBACK, and a reported one: no two snapshots in a row survived inside the
                // ring, so there is no window to telescope and the reading is the old minimum.
                _ackIntervalSeconds = min;
                AckIntervalWindow = 1;
                return;
            }

            double best = double.MaxValue, sum = 0.0;
            run = 0;
            for (var i = 0; i < _gapCount; i++)
            {
                double g = GapAt(i);
                if (g > single) { run = 0; sum = 0.0; continue; }

                sum += g;
                run++;
                if (run > window)
                {
                    sum -= GapAt(i - window);
                    run = window;
                }

                if (run == window && sum < best) best = sum;
            }

            double mean = best / window - (widest - min) / window;
            _ackIntervalSeconds = mean > min ? mean : min;
            AckIntervalWindow = window;
        }

        /// <summary>
        /// Span between the largest and smallest observation held, in seconds — how much of
        /// the wait term's range has actually been seen.
        /// </summary>
        public double ObservedSpanSeconds
        {
            get
            {
                double lo = Math.Min(_epochMin, _previousEpochMin);
                double hi = Math.Max(_epochMax, _previousEpochMax);
                if (lo == double.MaxValue || hi == double.MinValue) return 0.0;
                return hi - lo;
            }
        }

        /// <summary>
        /// The part of the wait term's range never sampled, in seconds — and therefore the
        /// most the floor can be reading high by.
        /// </summary>
        /// <remarks>
        /// The wait ranges over one snapshot interval. The observations have covered
        /// <see cref="ObservedSpanSeconds"/> of it, so the unsampled remainder is the interval
        /// less the span, and the true constant lies somewhere in
        /// <c>[FloorSeconds - this, FloorSeconds]</c>. Never negative: a span wider than the
        /// interval means the range is fully covered, not that the floor is under-read.
        /// </remarks>
        public double UnsweptSeconds
        {
            get
            {
                double interval = AckIntervalSeconds;
                if (interval <= 0.0) return 0.0;
                double slack = interval - ObservedSpanSeconds;
                return slack > 0.0 ? slack : 0.0;
            }
        }

        /// <summary>The measured floor in seconds, or 0 before <see cref="HasEstimate"/>.</summary>
        /// <remarks>
        /// The <see cref="FloorPercentile"/> quantile of the observations held, not their
        /// minimum. See that constant for why, and for the measurement that changed it.
        /// </remarks>
        public double FloorSeconds => Quantile(FloorPercentile);

        /// <summary>The same floor in base ticks, or 0 before <see cref="HasEstimate"/>.</summary>
        /// <remarks>
        /// This is the term to ADD to the steering target, alongside the staleness reading.
        /// The two do not overlap: the staleness fit reports the age ABOVE its envelope floor
        /// and this reports the constant the envelope absorbed, so their sum is the whole of
        /// <c>uplink + age</c> and neither counts anything twice.
        /// </remarks>
        public float FloorTicks { get; private set; }

        /// <summary>
        /// The floor with its own measurement uncertainty subtracted, in base ticks, or 0
        /// before <see cref="HasEstimate"/>. <b>This is the term to add to the lead.</b>
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why not the floor itself, and why not the floor truncated to whole ticks.</b>
        /// An over-lead steers the client past the server and is the original defect arriving
        /// from the other side; an under-lead merely leaves residual in place. The asymmetry
        /// is real and the reading must be biased low — but the first attempt did that by
        /// truncating to whole base ticks, and that is a guard which fires as a total loss.
        /// On any link whose <c>uplink + age</c> is under one base tick — every localhost run
        /// measured so far, at 0.14 and 0.68 ticks — <c>Math.Floor</c> returns zero and the
        /// estimator contributes nothing at all, in exactly the regime it exists for. And a
        /// sub-tick deficit is not a sub-tick problem: the tick LABEL is an integer, so a
        /// client leading by 0.68 ticks too little carries the wrong tick number for 68% of
        /// every tick and the reconcile returns a whole step of correction for it. That is the
        /// second step of the live residual, and truncation is what left it there.
        /// </para>
        /// <para>
        /// <b>What is subtracted, and why it is no longer <see cref="UnsweptSeconds"/>.</b> The
        /// floor is not a minimum — it is the <see cref="FloorPercentile"/> quantile — and on a
        /// swept link the quantiles are affine in <c>q</c>, so the tenth percentile sits
        /// <c>0.1 · S</c> ABOVE the constant BY CONSTRUCTION, on every clean run, whether or not
        /// anything is contaminated. That is a measured, systematic bias in the over-lead
        /// direction, which is the original defect arriving from the other side. So this
        /// subtracts exactly that: <c>quantile(0.10) − 0.10 × slope</c>, with the slope fitted
        /// from this run's own ladder (<see cref="TryFitLadder"/>) rather than assumed from a
        /// configured snapshot rate. The correction therefore follows the link instead of
        /// trusting a constant, and on clean data it lands on the pipeline constant rather than
        /// a tenth of a snapshot interval above it.
        /// </para>
        /// <para>
        /// The previous version subtracted <see cref="UnsweptSeconds"/> — about <c>S / phases</c>
        /// on a swept link — from a floor inflated by <c>0.1 · S</c>. <b>Those are unrelated
        /// quantities</b> that nearly cancelled only because the shipped cadences visit 13 and 23
        /// phases, either side of the 10 at which <c>1/phases</c> equals
        /// <see cref="FloorPercentile"/> and the cancellation would be exact. The remainder was
        /// <c>S · (0.1 − 1/phases)</c>, whose SIGN FLIPS below ten phases — a cadence
        /// recommendation selecting 8 phases would have inverted it with nothing in any counter
        /// changing. Subtracting the bias that is actually there removes the reason to subtract
        /// something else that happens to be about the same size. <c>UnsweptSeconds</c> remains
        /// as a diagnostic — it is still a true statement about how much of the range was seen —
        /// and is no longer part of this term. The arithmetic is preserved in the changelog.
        /// </para>
        /// <para>
        /// <b>When the ladder is not straight this reads ZERO, and says so.</b> A fallback is
        /// another claim about the same quantity and gets the same scrutiny as the estimate it
        /// replaces. The two candidates were the raw percentile — which is the known over-lead
        /// bias, i.e. the defect — and nothing. Nothing wins: an under-lead merely leaves
        /// residual in place, and a distribution the affine model does not describe should be
        /// REFUSED rather than handed to a statistic chosen to survive it. It is not the
        /// total-loss guard <c>Math.Floor</c> was, which fired on every healthy fast link; this
        /// fires only when the model itself is falsified. And it is never silent:
        /// <see cref="FloorCorrectionApplied"/> carries the state,
        /// <see cref="FloorCorrectionRefusals"/> counts it, and
        /// <see cref="FloorBiasTicks"/> is what was actually subtracted.
        /// </para>
        /// </remarks>
        public float ConservativeFloorTicks { get; private set; }

        /// <summary>
        /// Whether the last acknowledgement's <see cref="ConservativeFloorTicks"/> is a
        /// corrected floor (true) or the refusal (false).
        /// </summary>
        /// <remarks>
        /// False whenever there is no estimate at all, and false when there is one but the
        /// ladder could not be fitted straight. Read it beside <see cref="HasEstimate"/> to
        /// tell those apart.
        /// </remarks>
        public bool FloorCorrectionApplied { get; private set; }

        /// <summary>
        /// Acknowledgements that offered a floor but whose ladder was refused, so no correction
        /// could be made and <see cref="ConservativeFloorTicks"/> fell back to zero.
        /// </summary>
        /// <remarks>
        /// <b>Counted because an invisible fallback is how three defects reached this package.</b>
        /// A non-zero reading here on an otherwise healthy run means the estimator is
        /// contributing nothing to the lead, which reads identically to "the estimator is
        /// contributing correctly and the link is instant" in every other counter.
        /// </remarks>
        public int FloorCorrectionRefusals { get; private set; }

        /// <summary>
        /// The bias removed from the floor by the last correction, in base ticks — that is
        /// <c>FloorPercentile × slope</c>. Zero when the correction was refused.
        /// </summary>
        public float FloorBiasTicks { get; private set; }

        /// <summary>
        /// The fitted ladder slope in base ticks per unit q — the range the wait term swept, as
        /// this run measured it. Zero when the ladder was refused.
        /// </summary>
        /// <remarks>
        /// On a fully swept link this should read near the snapshot interval in base ticks. It
        /// is exposed so a reader can see the quantity the correction was computed from rather
        /// than infer it from the difference between two printed floors.
        /// </remarks>
        public float LadderSlopeTicks { get; private set; }

        /// <summary>
        /// The furthest a ladder point strayed from the fitted line on the last fit, in base
        /// ticks — the straightness evidence itself, reported whether the fit passed or not.
        /// </summary>
        public float LadderWorstResidualTicks { get; private set; }

        /// <summary>
        /// Remembers that an input was sent, so its acknowledgement can be timed.
        /// </summary>
        /// <param name="inputTick">The tick stamped on the input, as sent on the wire.</param>
        /// <param name="nowSeconds">
        /// Local monotonic time of the send. Must come from the same clock as
        /// <see cref="RecordAck"/>; mixing sources makes the difference meaningless.
        /// </param>
        public void RecordSent(long inputTick, double nowSeconds)
        {
            if (inputTick <= 0 || double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds))
            {
                return;
            }

            if (_count == PendingCapacity)
            {
                _head = (_head + 1) % PendingCapacity;
                _count--;
            }

            if (inputTick > _maxSentTick) _maxSentTick = inputTick;

            int slot = (_head + _count) % PendingCapacity;
            _pendingTick[slot] = inputTick;
            _pendingSentAt[slot] = nowSeconds;
            _count++;
        }

        /// <summary>
        /// Folds in a snapshot's acknowledgement, timing every input it covers.
        /// </summary>
        /// <param name="ackTick">The snapshot's <c>ack_tick</c>.</param>
        /// <param name="nowSeconds">Local monotonic time the snapshot is acted on.</param>
        /// <param name="baseHz">The server's base tick rate, for the tick conversion.</param>
        public void RecordAck(long ackTick, double nowSeconds, float baseHz)
        {
            if (ackTick <= 0 || baseHz <= 0f || double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds))
            {
                return;
            }

            // AN ACKNOWLEDGEMENT CANNOT NAME AN INPUT THIS CLIENT HAS NEVER SENT.
            //
            // When it does, it belongs to a previous session the server has not reaped, and
            // every pending input satisfies `tick <= ackTick` on arrival — so they would all be
            // timed at the wait for one client frame and set a floor an order of magnitude
            // below the route. Discard them; there is no interval here to measure. See
            // AckAheadOfSend for the measurements this cost.
            _lastBaseHz = baseHz;

            if (ackTick > _maxSentTick)
            {
                AckAheadOfSend += _count;
                _head = 0;
                _count = 0;
                return;
            }

            if (!_haveEpochStart)
            {
                _epochStartedAt = nowSeconds;
                _haveEpochStart = true;
            }
            else if (nowSeconds - _epochStartedAt >= EpochSeconds)
            {
                _previousEpochMin = _epochMin;
                _previousEpochMax = _epochMax;
                _epochMin = double.MaxValue;
                _epochMax = double.MinValue;
                _epochStartedAt = nowSeconds;
            }

            // The snapshot interval, measured over a RING of gaps between acknowledgements
            // rather than as the single smallest one. See AckIntervalSeconds for the statistic
            // and for the measurements that chose it over the minimum this used to be.
            if (ackTick > _lastAckTick)
            {
                if (_lastAckTick > 0)
                {
                    double gap = nowSeconds - _lastAckAt;
                    if (gap > 0.0) AppendGap(gap);
                }

                _lastAckTick = ackTick;
                _lastAckAt = nowSeconds;
            }

            // Retire every input this acknowledgement covers, but time only the NEWEST of
            // them.
            //
            // An older input's interval is not a measurement of this pipeline: it waited for
            // an acknowledgement that a LATER input had already earned, so it carries the
            // constant plus the whole of its own extra wait. Folding those in cannot lower the
            // floor -- a minimum is monotone, and larger values never move it down -- so this
            // is not what makes the floor read low. What it does corrupt is the SPAN, and the
            // span is the evidence SweptEnough rests on: superseded observations stretch the
            // maximum by however far the send cadence runs ahead of the acknowledgement
            // cadence, so the sweep looks satisfied on evidence that is not about the wait at
            // all. A guard that gates the whole reading must not be fed values from outside
            // the quantity it is guarding.
            double newestSentAt = 0.0;
            bool haveNewest = false;

            while (_count > 0 && _pendingTick[_head] <= ackTick)
            {
                if (haveNewest)
                {
                    // The one held so far is superseded by this newer one.
                    Superseded++;
                }

                newestSentAt = _pendingSentAt[_head];
                haveNewest = true;

                _head = (_head + 1) % PendingCapacity;
                _count--;
            }

            if (haveNewest)
            {
                double latency = nowSeconds - newestSentAt;

                if (latency <= 0.0 || latency > MaximumFloorSeconds)
                {
                    // The acknowledgement cannot precede the send: a non-positive reading is a
                    // clock that moved, not a fast route. And a floor is not a latency spike --
                    // a second of acknowledgement delay is a stall, a reconnect or a suspended
                    // process. Refused and counted, never clamped: a clamped bad observation is
                    // still wrong and now looks plausible.
                    Refused++;
                }
                else
                {
                    Samples++;

                    // The epoch min/max are the SWEEP evidence and nothing else; the floor
                    // itself is a quantile over the ring below.
                    if (latency < _epochMin) _epochMin = latency;
                    if (latency > _epochMax) _epochMax = latency;

                    if (_obsCount == ObservationCapacity)
                    {
                        _observations[_obsHead] = latency;
                        _obsHead = (_obsHead + 1) % ObservationCapacity;
                    }
                    else
                    {
                        _observations[(_obsHead + _obsCount) % ObservationCapacity] = latency;
                        _obsCount++;
                    }
                }
            }

            if (HasEstimate)
            {
                FloorTicks = (float)(FloorSeconds * baseHz);

                // THE FLOOR IS A QUANTILE, SO IT IS BIASED UP BY 0.1 * S BY CONSTRUCTION.
                //
                // Subtract that, measured from this run's own ladder rather than assumed from
                // a configured snapshot rate. If the ladder is not straight the affine model
                // that predicts the bias does not describe this distribution, and there is no
                // bias to subtract because there is no model to subtract it from -- so the
                // reading is refused, not repaired. See ConservativeFloorTicks.
                bool straight = TryFitLadder(out double slopeSeconds, out double worstSeconds);

                LadderWorstResidualTicks = (float)(worstSeconds * baseHz);

                if (straight)
                {
                    double bias = FloorPercentile * slopeSeconds;

                    LadderSlopeTicks = (float)(slopeSeconds * baseHz);
                    FloorBiasTicks = (float)(bias * baseHz);
                    FloorCorrectionApplied = true;

                    double conservative = (FloorSeconds - bias) * baseHz;
                    ConservativeFloorTicks = conservative > 0.0 ? (float)conservative : 0f;
                }
                else
                {
                    LadderSlopeTicks = 0f;
                    FloorBiasTicks = 0f;
                    FloorCorrectionApplied = false;
                    FloorCorrectionRefusals++;
                    ConservativeFloorTicks = 0f;
                }
            }
            else
            {
                FloorTicks = 0f;
                ConservativeFloorTicks = 0f;
                FloorCorrectionApplied = false;
                FloorBiasTicks = 0f;
                LadderSlopeTicks = 0f;
                LadderWorstResidualTicks = 0f;
            }
        }

        /// <summary>
        /// Forget the route. Call on a session boundary: this describes one connection to one
        /// server, and carrying it across a reconnect measures the new one against the old.
        /// </summary>
        public void Reset()
        {
            _head = 0;
            _count = 0;
            _epochMin = double.MaxValue;
            _epochMax = double.MinValue;
            _previousEpochMin = double.MaxValue;
            _previousEpochMax = double.MinValue;
            _epochStartedAt = 0;
            _haveEpochStart = false;
            _gapHead = 0;
            _gapCount = 0;
            _ackIntervalSeconds = 0.0;
            AckIntervalWindow = 0;
            _lastAckAt = 0;
            _lastAckTick = 0;
            _maxSentTick = 0;
            _lastBaseHz = 0f;
            _obsHead = 0;
            _obsCount = 0;
            Samples = 0;
            Refused = 0;
            Superseded = 0;
            AckAheadOfSend = 0;
            FloorTicks = 0f;
            ConservativeFloorTicks = 0f;
            FloorCorrectionApplied = false;
            FloorCorrectionRefusals = 0;
            FloorBiasTicks = 0f;
            LadderSlopeTicks = 0f;
            LadderWorstResidualTicks = 0f;
        }
    }
}
