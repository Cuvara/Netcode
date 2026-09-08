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
    /// itself measured as the smallest gap between acknowledgements. Without the sweep there is
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
        /// If the floor tracks the harness with the sweep guard working, the tenth percentile
        /// stays and its bimodal-under-load justification stands as one regime of three. If the
        /// floor is still inflated, the statistic is implicated on its own evidence and the
        /// minimum returns — for the original reason, that a quantile above the minimum biases
        /// the lead UPWARD, which is the over-lead direction.
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

        private double _epochMin = double.MaxValue;
        private double _epochMax = double.MinValue;
        private double _previousEpochMin = double.MaxValue;
        private double _previousEpochMax = double.MinValue;
        private double _epochStartedAt;
        private bool _haveEpochStart;

        // Smallest gap between successive acknowledgements: the snapshot interval, measured
        // the same way everything else here is measured.
        private double _ackIntervalMin = double.MaxValue;
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
                if (_ackIntervalMin == double.MaxValue) return false;

                // NOT MinimumSamples. Below MinimumSweepSamples the two quantiles below ARE
                // the minimum and the maximum, so the span test is max - min and this guard
                // silently becomes the thing it was rewritten to stop being. See that
                // constant for the distribution that gets through the window and for why the
                // answer is a refusal rather than a better statistic.
                if (_obsCount < MinimumSweepSamples) return false;

                double lo = Quantile(SweepLowQuantile);
                double hi = Quantile(SweepHighQuantile);

                if (hi - lo < _ackIntervalMin * MinimumSweepFraction)
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
            if (_obsCount == 0 || _ackIntervalMin == double.MaxValue) return 0;

            double lo = double.MaxValue;
            for (var i = 0; i < _obsCount; i++)
            {
                if (_observations[i] < lo) lo = _observations[i];
            }

            double width = _ackIntervalMin / SweepBuckets;
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

            Array.Copy(_observations, _sortScratch, _obsCount);
            Array.Sort(_sortScratch, 0, _obsCount);

            int index = (int)(q * _obsCount);
            if (index >= _obsCount) index = _obsCount - 1;
            if (index < 0) index = 0;
            return _sortScratch[index];
        }

        /// <summary>The snapshot interval as measured from acknowledgement arrivals, seconds.</summary>
        public double AckIntervalSeconds =>
            _ackIntervalMin == double.MaxValue ? 0.0 : _ackIntervalMin;

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
        /// So the bias is kept and moved into the right units: units of what is actually
        /// uncertain. The floor is a minimum over <c>constant + wait</c>, so it reads high by
        /// however much of the wait's range was never sampled — which is
        /// <see cref="UnsweptSeconds"/>, and is measured rather than assumed. Subtracting it
        /// gives the low end of the bracket the observations actually support: still biased
        /// low, still incapable of over-leading on the evidence held, and it goes to zero only
        /// when nothing was swept rather than whenever the link is fast.
        /// </para>
        /// </remarks>
        public float ConservativeFloorTicks { get; private set; }

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

            // The snapshot interval, measured as the smallest gap between acknowledgements that
            // actually advanced. Minimum rather than mean for the same reason as everywhere else
            // here: a gap can be stretched by a late frame, never shortened below the cadence.
            //
            // KNOWN LENIENCE, now MEASURED and still deliberately not fixed here. A gap can be
            // shortened below the cadence, by one arrival being late and the next on time: the
            // minimum therefore reads the interval LESS the arrival jitter, and every
            // requirement scaled by it -- SweptEnough's, above all -- is weakened in
            // proportion. It is the same shape as the two defects this guard has already had:
            // a minimum standing in for a quantity it is silent about.
            //
            // MEASURED: on a 66.667 ms cadence with one 60 fps frame of jitter this reads
            // 50.000 ms, exactly the cadence less the whole jitter range -- 25% low. See
            // AckLatencyEstimatorTests.TheSnapshotIntervalReadsLowByTheArrivalJitter.
            //
            // WHY IT IS STILL NOT FIXED, which is a measurement and not a preference. The
            // obvious correction is the smallest mean of two ADJACENT gaps: they telescope, so
            // a single arrival's delay cancels exactly, and the result can never fall below
            // this minimum. It was implemented and driven, and on a link losing one snapshot in
            // three it read 99.999 ms against the same 66.667 ms cadence -- 50% HIGH -- because
            // no adjacent pair of gaps is then free of a drop. The present error is LENIENT and
            // cannot cause an over-lead; that one is STRICT and can. A correct fix needs a
            // robust statistic over a ring of gaps rather than a running scalar, which is a
            // larger change than this line. The drop case is pinned as a test --
            // AnIdealCadenceIsMeasuredExactly_DropsOrNot -- so the next attempt is measured
            // against loss before it is believed rather than after.
            if (ackTick > _lastAckTick)
            {
                if (_lastAckTick > 0)
                {
                    double gap = nowSeconds - _lastAckAt;
                    if (gap > 0.0 && gap < _ackIntervalMin) _ackIntervalMin = gap;
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

                double conservative = (FloorSeconds - UnsweptSeconds) * baseHz;
                ConservativeFloorTicks = conservative > 0.0 ? (float)conservative : 0f;
            }
            else
            {
                FloorTicks = 0f;
                ConservativeFloorTicks = 0f;
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
            _ackIntervalMin = double.MaxValue;
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
        }
    }
}
