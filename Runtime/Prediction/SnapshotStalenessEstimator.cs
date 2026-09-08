using System;

namespace Cuvara.Netcode.Prediction
{
    /// <summary>
    /// Measures how old a snapshot already is by the time the client acts on it, in base
    /// ticks, by fitting the server's clock to the client's — <b>offset and rate</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this is for.</b> The prediction clock is steered onto the tick carried by the
    /// newest snapshot, and that tick is old when it is read: the server produced it, put it
    /// on a socket, it crossed the network, and it then waited for a client frame to pick it
    /// up. Steering onto it with no allowance for that age drags the client's clock behind
    /// the server's real one by exactly the age, and a tick number then stops naming the same
    /// moment on both sides — the client's tick N carries inputs the server will not apply
    /// until its own tick N + age. The reconcile reports that as a correction of one input
    /// interval, on every snapshot, forever.
    /// </para>
    /// <para>
    /// <b>Why the age is measured rather than derived.</b> It used to be taken as one
    /// snapshot interval plus the rounded half round trip. Both terms are whole ticks while
    /// the real age is fractional and depends on where a client's frame loop happens to fall
    /// relative to the server's send cadence — a phase fixed at join that then holds for the
    /// session. Measured with two clients on one machine against one server, an unlucky phase
    /// left one of them with a constant <b>0.3333-unit, 4.00-step</b> correction while the
    /// other sat at 0.0033.
    /// </para>
    ///
    /// <para><b>The model.</b> A snapshot stamped with base tick <c>T</c> was produced on the
    /// server at server time <c>T / hz</c>. Observed on the client at local time <c>t</c>,
    ///
    /// <code>t = offset + skew * (T / hz) + delay,   delay &gt;= 0</code>
    ///
    /// <c>offset</c> absorbs both the arbitrary difference between the two clocks' origins and
    /// the minimum one-way delay — inseparable from one side, and neither needs separating.
    /// <c>skew</c> is the ratio of the two clocks' rates. <c>delay</c> is queueing, jitter and
    /// the wait for a frame, and is what this class exists to report.</para>
    ///
    /// <para>Because <c>delay</c> is never negative, the samples lie <b>above</b> a line and
    /// touch it at their best moments. Fitting that lower envelope gives <c>offset</c> and
    /// <c>skew</c>; the height of a sample above it is that snapshot's age beyond the best the
    /// route has shown. Least squares would be wrong here — it fits the middle of a
    /// distribution whose upper side is unbounded delay, so every bad frame drags the answer.
    /// The envelope is the same "minimum, not mean" argument
    /// <see cref="TickRateEstimator.SnapshotTickGap"/> makes about the send cadence.</para>
    ///
    /// <para><b>Why the rate term is not optional, and what it cost to learn.</b> An earlier
    /// version fitted the offset alone, against a fixed rate. A minimum-filtered offset cannot
    /// see a rate difference, and a rate difference it cannot see appears as an offset that
    /// grows without bound. Wired to the steering target it made the client categorically
    /// worse, twice:</para>
    ///
    /// <list type="bullet">
    ///   <item><description>fed a rate measured off the wire (57.7 Hz for a 60 Hz server), it
    ///   drifted 4 % per second: the reading passed <b>613 ticks</b> with the steering target
    ///   following it, and snaps reached <b>71 per five-second window</b>;</description></item>
    ///   <item><description>fed the server's advertised rate, it still settled around
    ///   <b>205 ticks</b> where two or three was right — the same runaway, slower.</description></item>
    /// </list>
    ///
    /// <para>Both are the same defect: a term the model did not have. Solving for it is what
    /// this class does now, and <c>SkewPpm</c> reports it so a rate that disagrees with the
    /// advertised one is visible rather than absorbed. With the rate fitted, two minutes at
    /// the same 4 % difference reads flat and the difference appears as 40 000 ppm — which is
    /// the point: it is a wrong tick rate, and it should be legible as one rather than
    /// arriving as a client that drifts.</para>
    ///
    /// <para><b>How the envelope is fitted.</b> Two anchors, each the lowest sample of its own
    /// epoch, with the line drawn through them. An anchor is a best-case sample by
    /// construction, so a line through two of them, far apart in time, estimates the rate with
    /// the delay largely divided out — the long baseline is what makes a small rate error
    /// measurable at all. This is the cheap form of the convex-hull method used for clock
    /// synchronisation over paths with unknown delay; it keeps two points rather than a hull,
    /// which is enough when the samples arrive at a fixed cadence and the rate is stable over
    /// a session.</para>
    /// </remarks>
    public sealed class SnapshotStalenessEstimator
    {
        /// <summary>
        /// How long each epoch collects before its lowest sample becomes an anchor.
        /// </summary>
        /// <remarks>
        /// Two seconds is ~30 snapshots at the default rates: enough that the lowest of them
        /// is a genuinely good sample rather than whichever arrived first, and short enough
        /// that a joining client has a fit within a few seconds.
        /// </remarks>
        public const double EpochSeconds = 2.0;

        /// <summary>
        /// Shortest baseline between the two anchors that may be used to estimate the rate.
        /// </summary>
        /// <remarks>
        /// The rate is a slope, and a slope over a short baseline is mostly the noise of its
        /// two endpoints: over one second, a millisecond of residual jitter reads as 1000 ppm
        /// of rate error, which is twenty times a real one. Four seconds puts that at 250 ppm
        /// and the baseline grows from there, because the older anchor is deliberately kept.
        /// </remarks>
        public const double MinimumBaselineSeconds = 4.0;

        /// <summary>
        /// Longest a baseline is kept before the older anchor is replaced.
        /// </summary>
        /// <remarks>
        /// A long baseline measures the rate well and follows a change in it slowly, because
        /// half its evidence is minutes old. Two minutes keeps the rate estimate tight while
        /// bounding how long a genuine change — a machine's clock being stepped, a server
        /// restarting on a different tick origin — stays half-believed.
        /// </remarks>
        public const double MaximumBaselineSeconds = 120.0;

        /// <summary>
        /// Snapshots that must have arrived before <see cref="StalenessTicks"/> is offered as
        /// a PROVISIONAL reading, ahead of the fitted line.
        /// </summary>
        /// <remarks>
        /// Four is two snapshot intervals at the default rates — enough that the running floor
        /// is a floor rather than whichever sample happened to arrive first, and short enough
        /// that the reading is available inside a fifth of a second.
        /// </remarks>
        public const int MinimumProvisionalSamples = 4;

        /// <summary>
        /// Bounds on the fitted rate, as a ratio of client clock to server tick time.
        /// </summary>
        /// <remarks>
        /// A pair that implies an impossible rate is a bad pair, not a discovery: a slope
        /// fitted through one would steer the simulation somewhere arbitrary. What counts as
        /// impossible has to be chosen against what is merely unusual, and the first attempt
        /// got that wrong.
        ///
        /// <para><b>These were 0.90 and 1.10, and every fit was refused.</b> <c>IsUsable</c>
        /// stayed false for a whole session and the steering silently fell back to the derived
        /// figure, with nothing reporting it: <c>SkewPpm</c> reads 0 when there is no fit,
        /// which is indistinguishable from two clocks that agree. The refusals were read at the
        /// time as the bounds being too tight against a machine whose true ratio was believed
        /// to be about <b>1.103</b> — the Windows performance counter running fast against the
        /// Linux clock the server ticks on, with the observed snapshot stream advancing at 54.4
        /// base ticks per client second against a nominal 60 — and the bounds were widened to
        /// admit it.</para>
        ///
        /// <para><b>THAT 1.103 FIGURE WAS AN ARTEFACT, AND THE BOUNDS REST ON IT.</b> The same
        /// Windows-Editor/Linux-container pair, measured twice minutes apart on one machine,
        /// read <b>220 ppm</b> — a ratio of 1.0002, two ordinary crystals — when the Editor was
        /// idle, and <b>90 636 ppm</b> when it was inside a loaded test suite. A crystal ratio
        /// does not move 90 000 ppm in ten minutes. What moves is the DELAY FLOOR: this fit is
        /// a line through two best-case samples and is a rate only if the minimum achievable
        /// delay was the same at both, and a starved frame loop raises that floor so the later
        /// anchor sits above the true line and the slope absorbs the displacement as rate. Over
        /// the 4 s minimum baseline, 90 636 ppm is a floor step of 362 ms — an ordinary hitch.
        /// So the mass refusals that justified widening these bounds were most likely the
        /// clamp working: artefact slopes being correctly rejected, on a machine whose real
        /// ratio is 1.0002.</para>
        ///
        /// <para><b>The bounds are left where they are anyway, and that is deliberate.</b>
        /// Narrowing them back to 0.90/1.10 would not have caught the artefact that prompted
        /// this: the live reading was a skew of <b>0.9169</b>, comfortably inside the old
        /// bounds. A clamp on the magnitude was never the right instrument, because an artefact
        /// and a rate are not told apart by size. They are told apart by whether the reading
        /// survives a change of baseline, which is what <see cref="CorroborationPpm"/> now
        /// tests and what gates the rate reaching a clock. Changing these numbers would be
        /// motion without evidence; the figure they were justified by is what was wrong.</para>
        ///
        /// <para>A third either way still rejects what this is for. A client predicting at the
        /// wrong tick rate — 60 against a 15 Hz server, the failure the clamp exists to catch —
        /// is off by 300%, not by 10. Between "two ordinary machines" and "a rate that is
        /// simply wrong" there is an order of magnitude, and the bound belongs in the middle of
        /// it rather than at the edge of the first.</para>
        /// </remarks>
        public const double MinimumSkew = 0.75;

        /// <inheritdoc cref="MinimumSkew"/>
        public const double MaximumSkew = 1.33;

        /// <summary>
        /// How closely two successive fits must agree, in ppm, before the rate is believed
        /// enough to run a clock on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The assumption this checks, and why it needed checking.</b> The envelope fit is
        /// valid only if the MINIMUM ACHIEVABLE DELAY is the same at both anchors — that is
        /// what makes a line through two best-case samples a rate rather than an accident. The
        /// class documented that assumption and never tested it, and a fit built on it was fed
        /// straight to a clock.
        /// </para>
        /// <para>
        /// When the assumption fails the failure is not small and it does not look like noise.
        /// If the delay floor rises between the anchors — a starved frame loop, a machine that
        /// got busy — the later anchor sits above the true line and the slope absorbs the rise
        /// as RATE. Measured on one machine minutes apart: idle, <b>220 ppm</b>, which is two
        /// ordinary crystals; inside a loaded test suite, <b>90 636 ppm</b>, which is nothing,
        /// because a crystal ratio cannot move 90 000 ppm in ten minutes. Over the 4 s minimum
        /// baseline that slope is a delay-floor step of 362 ms, which is an ordinary hitch.
        /// The client then ran its base-tick clock <b>8.3% slow on purpose</b> and sat at a
        /// three-tick standing error.
        /// </para>
        /// <para>
        /// <b>What separates the two.</b> A rate is constant, so it reads the same over any
        /// baseline. A floor step is a fixed displacement, so the slope it fakes is
        /// <c>step / baseline</c> and SHRINKS as the baseline grows. The anchors here are kept
        /// until <see cref="MaximumBaselineSeconds"/>, so the baseline grows between fits for
        /// free: requiring two successive fits to agree is therefore the same test as
        /// requiring the slope to survive a change of baseline, and it needs no new threshold
        /// to say what "too big" means.
        /// </para>
        /// <para>
        /// A thousand ppm is roughly four times the fit noise the minimum baseline admits — a
        /// millisecond of residual jitter over 4 s reads as 250 ppm — so a real rate
        /// corroborates comfortably while a slope decaying as 1/baseline cannot.
        /// </para>
        /// <para>
        /// <b>The comparison is against a baseline at least twice as long, and that is not a
        /// detail.</b> Comparing consecutive fits is not enough: a decaying slope
        /// <c>D / baseline</c> changes by <c>D * epoch / baseline²</c> between neighbours, which
        /// falls below any fixed tolerance once the baseline is long enough — a 300 ms floor
        /// step self-corroborates at about 25 s, on a reading still 12 000 ppm wrong. The
        /// first version of this guard did exactly that and its own test caught it.
        /// Requiring the baseline to DOUBLE makes the test scale-invariant instead: a pure
        /// decay always disagrees by half of itself, whatever the baseline, while a constant
        /// rate agrees at every scale.
        /// </para>
        /// </remarks>
        public const double CorroborationPpm = 1000.0;

        /// <summary>
        /// Beyond this, a fitted rate is extraordinary and is counted as such.
        /// </summary>
        /// <remarks>
        /// <see cref="SkewPpm"/> has always said that a few hundred ppm is two crystals and
        /// that tens of thousands "is not skew — it is a tick rate that does not match what
        /// the server is actually running, and it is worth an error rather than a correction".
        /// Nothing enforced it and the correction was issued anyway. One percent is an order
        /// of magnitude above any real oscillator pair and an order of magnitude below the
        /// artefact above, which is what makes it a useful place to draw the line.
        ///
        /// <para><b>It is counted, not refused outright, and that is deliberate.</b> An
        /// unconditional refusal here would permanently disable rate correction on a machine
        /// whose ratio genuinely is extraordinary — which is exactly the failure the original
        /// 0.90/1.10 bounds produced, arriving through a different door. A reading this large
        /// still has to CORROBORATE before it reaches the clock, like every other, and it is
        /// reported so a run that hits it says so rather than quietly steering on it.</para>
        /// </remarks>
        public const double ExtraordinarySkewPpm = 10_000.0;

        private double _offset;
        private double _skew = 1.0;
        private bool _haveFit;

        // Lowest unit-rate residual seen since construction or Reset. Unlike _bestResidual
        // this survives the epoch boundary: it is the floor the PROVISIONAL reading is taken
        // above, and an epoch is far too short a memory for a floor.
        // The unit-rate floor, kept per epoch with one epoch of memory. See the update site.
        private double _floorResidual;
        private bool _haveFloor;
        private double _previousFloorResidual;
        private bool _havePreviousFloor;

        // The older anchor: lowest sample of an earlier epoch, and the far end of the
        // baseline the rate is fitted over.
        private double _anchorX, _anchorY;
        private bool _haveAnchor;

        // The reference fit the next corroboration is judged against: its rate and the
        // baseline it was measured over. See CorroborationPpm for why the baseline matters.
        private double _referenceFitPpm;
        private double _referenceFitBaseline;
        private bool _haveReferenceFit;

        // The current epoch's lowest sample so far.
        private double _bestX, _bestY, _bestResidual;
        private bool _haveBest;
        private double _epochStartedAt;

        /// <summary>Samples taken since construction or <see cref="Reset"/>.</summary>
        public int Samples { get; private set; }

        /// <summary>
        /// The most recent measurement, in base ticks, or 0 before <see cref="IsUsable"/>.
        /// </summary>
        public float StalenessTicks { get; private set; }

        /// <summary>Whether a line has been fitted and <see cref="StalenessTicks"/> means anything.</summary>
        public bool IsUsable => _haveFit;

        /// <summary>
        /// Whether <see cref="StalenessTicks"/> is measured against the fitted line, rather
        /// than against the unit-rate provisional floor.
        /// </summary>
        /// <remarks>
        /// <b><see cref="IsUsable"/> is not this, and reading it as this was a defect.</b> It
        /// says a line was fitted; it does not say the line is trustworthy. The age is
        /// <c>y - (offset + skew * x)</c>, so an uncorroborated SLOPE steers the age exactly as
        /// it would have steered the clock — the further <c>x</c> runs from the anchor, the
        /// larger the displacement. Gating the clock alone left that route wide open.
        /// </remarks>
        public bool AgeIsFitted => _haveFit && RateCorroborated;

        /// <summary>
        /// Whether <see cref="StalenessTicks"/> carries a reading at all — fitted, or the
        /// provisional one taken above the running floor before the fit lands.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why a second, weaker signal exists.</b> <see cref="IsUsable"/> requires a fitted
        /// RATE, and a rate is a slope that cannot honestly be fitted over a short baseline —
        /// which is why <see cref="MinimumBaselineSeconds"/> is what it is, and why
        /// <c>NothingIsOfferedUntilTheBaselineIsLongEnoughToFitARateOver</c> pins it. Measured
        /// against a 15 Hz snapshot stream, the first fit lands <b>8.2 seconds</b> after join:
        /// epoch one only sets an anchor, and two consecutive two-second epochs cannot span
        /// the four seconds a fit needs, so the earliest fit is the third or fourth epoch.
        /// </para>
        /// <para>
        /// For those eight seconds the caller had nothing and fell back to a derived guess of
        /// one snapshot interval. That guess is not small: on localhost the true age is
        /// <b>0.06 base ticks</b> and the guess is <b>4</b>, so the client's clock is steered
        /// four base ticks past the server's, a tick number stops naming the same moment on
        /// both sides, and the reconcile reports the difference as a correction of exactly one
        /// input interval — the <b>0.3333-unit, 4.00-step</b> tug this class's remarks already
        /// name — at every start and every stop, for the first eight seconds of every session.
        /// </para>
        /// <para>
        /// The AGE does not need the rate. It is the height of a sample above the lower
        /// envelope, and over a few seconds the envelope's slope is one to within a few
        /// hundred ppm — 0.02 base ticks over ten seconds, which is nothing next to the four
        /// ticks it replaces. The rate term earns its place over minutes, not over the warm-up.
        /// </para>
        /// <para>
        /// <b>The provisional reading is deliberately only trusted downwards.</b> An unfitted
        /// rate drifts the residual, and it drifts it UPWARD when the client's clock runs fast
        /// — a 10% ratio, of the kind <see cref="MinimumSkew"/>'s remarks discuss, would read as tens of ticks
        /// of "age" within the warm-up. So the caller must clamp the provisional reading by the
        /// guess it replaces and take the smaller: below the guess the reading is evidence,
        /// above it it is drift, and the clamp makes this strictly safer than the old fallback
        /// in every direction. <see cref="WorldViewBinder"/> does exactly that.
        /// </para>
        /// </remarks>
        public bool HasEstimate => _haveFit || Samples >= MinimumProvisionalSamples;

        /// <summary>
        /// The fitted rate difference between the two clocks, in parts per million, or 0
        /// before <see cref="IsUsable"/>.
        /// </summary>
        /// <remarks>
        /// Positive means the client's clock runs fast relative to the server's tick stream.
        /// A few hundred ppm is two ordinary crystals disagreeing. Tens of thousands is not
        /// skew — it is a tick rate that does not match what the server is actually running,
        /// and it is worth an error rather than a correction.
        /// </remarks>
        public double SkewPpm => _haveFit ? (_skew - 1.0) * 1e6 : 0.0;

        /// <summary>
        /// Whether the fitted rate has been reproduced by a second fit over a longer baseline,
        /// and is therefore safe to run a clock on. See <see cref="CorroborationPpm"/>.
        /// </summary>
        /// <remarks>
        /// <b>Separate from <see cref="IsUsable"/> on purpose.</b> A single fit is good enough
        /// to report an AGE with — the residual above the line, which a wrong slope perturbs
        /// only slightly over one snapshot — and not good enough to set a clock's RATE with,
        /// where the same wrong slope is applied every second forever. The two readings have
        /// different evidence requirements and used to share one gate.
        /// </remarks>
        public bool RateCorroborated { get; private set; }

        /// <summary>Fits accepted but not yet reproduced by a second one. See <see cref="CorroborationPpm"/>.</summary>
        public int FitsUncorroborated { get; private set; }

        /// <summary>Fits whose rate exceeded <see cref="ExtraordinarySkewPpm"/>.</summary>
        /// <remarks>
        /// Nonzero is worth reading even when the fit later corroborates: it means the two
        /// clocks are claimed to differ by more than one percent, which is either a remarkable
        /// machine or a measurement taken across something that moved.
        /// </remarks>
        public int FitsExtraordinary { get; private set; }

        /// <summary>Wall-clock span the current rate estimate was fitted over, in seconds.</summary>
        public double BaselineSeconds { get; private set; }

        /// <summary>Times a new line has been fitted.</summary>
        public int Fits { get; private set; }

        /// <summary>
        /// Times a fit was refused because the pair implied a rate outside
        /// <see cref="MinimumSkew"/>..<see cref="MaximumSkew"/>.
        /// </summary>
        /// <remarks>
        /// Reported because a refusal is otherwise invisible: <see cref="SkewPpm"/> reads 0
        /// without a fit, which looks exactly like two clocks that agree. A bound set too
        /// tightly therefore disables the measurement for a whole session and says nothing —
        /// which is what happened at the original 0.90/1.10. Note that the 1.103 ratio that
        /// episode was attributed to is now believed to have been a delay-floor artefact
        /// rather than a real clock difference; see <see cref="MinimumSkew"/>.
        /// </remarks>
        public int FitsRefused { get; private set; }

        /// <summary>The rate the last refused pair implied, in ppm, or 0 if none was refused.</summary>
        /// <remarks>
        /// The number that was rejected, so a bound that is wrong can be seen to be wrong
        /// rather than inferred from an absence.
        /// </remarks>
        public double RefusedSkewPpm { get; private set; }

        /// <summary>
        /// Record a snapshot and return how old it was when it was acted on, in base ticks.
        /// </summary>
        /// <param name="snapshotTick">Base tick the snapshot was produced on.</param>
        /// <param name="nowSeconds">
        /// Local time the client is acting on it — the moment of use, not of arrival, so the
        /// wait for a frame is inside the measurement. Must come from one monotonic clock;
        /// mixing sources makes the differences meaningless.
        /// </param>
        /// <param name="baseHz">
        /// The rate the SERVER stamps ticks at, as advertised by it. Used only to turn a tick
        /// into a server-clock time, and any error in it is absorbed by the fitted rate rather
        /// than accumulating — which is the whole reason the rate is fitted.
        /// </param>
        /// <returns>
        /// Age in base ticks, or 0 before a line has been fitted. Never negative: a snapshot
        /// cannot be read before it was produced, so a sample below the line means the line is
        /// stale, not that time ran backwards.
        /// </returns>
        public float Sample(long snapshotTick, double nowSeconds, float baseHz)
        {
            if (snapshotTick <= 0 || baseHz <= 0f || double.IsNaN(nowSeconds))
            {
                return StalenessTicks;
            }

            double x = snapshotTick / (double)baseHz;   // server time this tick was produced
            double y = nowSeconds;                      // client time it was acted on

            if (Samples < int.MaxValue) Samples++;

            // Height above the current line, or above a unit-rate line through the origin of
            // the first sample while there is no fit yet. Either way it ranks samples within
            // the epoch consistently, which is all the anchor selection needs.
            double residual = _haveFit ? y - (_offset + _skew * x) : y - x;

            if (!_haveBest || residual < _bestResidual)
            {
                _bestX = x;
                _bestY = y;
                _bestResidual = residual;
                _haveBest = true;
            }

            if (_epochStartedAt == 0.0)
            {
                _epochStartedAt = nowSeconds;
            }
            else if (nowSeconds - _epochStartedAt >= EpochSeconds)
            {
                CloseEpoch();
                _epochStartedAt = nowSeconds;
            }

            // The unit-rate floor, kept across epochs. `residual` is y - x exactly while
            // there is no fit, which is that same unit-rate line.
            //
            // Kept live while a fit exists but is UNCORROBORATED, because that is when the
            // provisional reading below is the one being used and a frozen floor would make it
            // stale. See the age branch beneath.
            //
            // AND KEPT PER EPOCH, WHICH IS THE POINT. A floor that only ever moves downward is a
            // memory of the session's fastest moment, and the height above it therefore carries
            // the whole of any rate difference accumulated since: the provisional reading has no
            // slope term, so a client clock n% fast adds n% of elapsed time to it every second.
            // Measured, unbounded: 45.56 base ticks -- 759 ms -- on a run whose apparent skew was
            // 81 351 ppm over ten seconds, against 0.09 on a run whose skew was -55 ppm. The
            // fitted path forgets through its epochs and this one did not.
            if (!_haveFit || !RateCorroborated)
            {
                double unit = y - x;
                if (!_haveFloor || unit < _floorResidual)
                {
                    _floorResidual = unit;
                    _haveFloor = true;
                }
            }

            // THE AGE IS MEASURED AGAINST THE FITTED LINE ONLY ONCE THE SLOPE IS CORROBORATED,
            // AND THAT IS THE OTHER HALF OF THE RATE FIX.
            //
            // `above` subtracts `_offset + _skew * x`, and `_skew` is the SAME slope
            // RateCorroborated refuses. So gating only the clock left the refused slope steering
            // the lead by the other route, undiminished and growing with the distance from the
            // anchor: measured live, a 51 225 ppm fit over a 6.1 s baseline displaces the line by
            // 0.31 s -- 18.7 base ticks -- and the age read 5.24 against a true idle age of 0.06,
            // taking the lead to 6 on a run where the rate had been correctly refused.
            //
            // An uncorroborated fit therefore falls back to the provisional reading below, which
            // is measured at UNIT RATE against a running floor and so cannot accumulate with the
            // baseline at all. That is not a new code path invented for this: it is the branch
            // that already exists for the pre-fit window, and it is safe for exactly the reason
            // it was safe there -- it carries no slope. The caller must clamp it from above; see
            // HasEstimate, and WorldViewBinder.TargetLeadTicks does so with Math.Min(.., gap).
            if (_haveFit && RateCorroborated)
            {
                double above = y - (_offset + _skew * x);
                if (above < 0) above = 0;
                StalenessTicks = (float)(above * baseHz);
            }
            else if (Samples >= MinimumProvisionalSamples && (_haveFloor || _havePreviousFloor))
            {
                // Provisional: height above the running floor, at unit rate. See HasEstimate
                // for why this is offered and why the caller must clamp it from above.
                double floor = _floorResidual;
                if (_havePreviousFloor && _previousFloorResidual < floor)
                {
                    floor = _previousFloorResidual;
                }

                double above = (y - x) - floor;
                if (above < 0) above = 0;
                StalenessTicks = (float)(above * baseHz);
            }

            return StalenessTicks;
        }

        /// <summary>
        /// Promote the epoch's lowest sample to an anchor, and refit if the baseline is long
        /// enough to say anything about the rate.
        /// </summary>
        private void CloseEpoch()
        {
            if (!_haveBest)
            {
                return;
            }

            // Retire the unit-rate floor with the epoch, so the provisional reading is a height
            // above a RECENT minimum rather than the session's best moment. One epoch of memory,
            // the same shape AckLatencyEstimator uses, so a single unlucky epoch cannot leave the
            // client without a reading.
            _previousFloorResidual = _floorResidual;
            _havePreviousFloor = _haveFloor;
            _haveFloor = false;

            if (!_haveAnchor)
            {
                _anchorX = _bestX;
                _anchorY = _bestY;
                _haveAnchor = true;
                _haveBest = false;
                return;
            }

            double span = _bestX - _anchorX;
            if (span >= MinimumBaselineSeconds)
            {
                double skew = (_bestY - _anchorY) / span;

                // A pair that implies an impossible rate is a bad pair, not a discovery. Keep
                // the line that stands and let the next epoch try again.
                if (skew >= MinimumSkew && skew <= MaximumSkew)
                {
                    _skew = skew;
                    _offset = _anchorY - _skew * _anchorX;
                    _haveFit = true;
                    BaselineSeconds = span;
                    Fits++;

                    double ppm = (skew - 1.0) * 1e6;
                    if (Math.Abs(ppm) > ExtraordinarySkewPpm)
                    {
                        FitsExtraordinary++;
                    }

                    // CORROBORATION. A rate reads the same over any baseline; a delay-floor
                    // step fakes a slope of step/baseline, which halves when the baseline
                    // doubles. So the reference is only re-examined once the baseline has
                    // doubled, and a decay then disagrees by half of itself at every scale.
                    // See CorroborationPpm for the measurement that made this necessary.
                    //
                    // DO NOT SIMPLIFY THIS INTO "compare the last two fits". That is what it
                    // was first, and it is subtly wrong in the one direction that matters: a
                    // decaying slope changes less and less between neighbours, so it slips
                    // under ANY fixed tolerance once the baseline is long enough and then
                    // corroborates itself on a reading still thousands of ppm out. The
                    // doubling is what makes the test scale-invariant -- a decay disagrees by
                    // half of itself at every scale, a real rate agrees at every scale -- and
                    // dropping it produces a guard that passes its own tests and fails on
                    // exactly the case it exists for.
                    if (!_haveReferenceFit)
                    {
                        _referenceFitPpm = ppm;
                        _referenceFitBaseline = span;
                        _haveReferenceFit = true;
                        RateCorroborated = false;
                    }
                    else if (span >= _referenceFitBaseline * 2.0)
                    {
                        if (Math.Abs(ppm - _referenceFitPpm) <= CorroborationPpm)
                        {
                            RateCorroborated = true;
                        }
                        else
                        {
                            FitsUncorroborated++;
                            RateCorroborated = false;
                        }

                        _referenceFitPpm = ppm;
                        _referenceFitBaseline = span;
                    }
                }
                else
                {
                    FitsRefused++;
                    RefusedSkewPpm = (skew - 1.0) * 1e6;
                }

                // The older anchor is kept so the baseline keeps growing and the rate estimate
                // keeps tightening -- until it is old enough that half the evidence is stale,
                // at which point the newer sample becomes the far end of a fresh baseline.
                if (span >= MaximumBaselineSeconds)
                {
                    _anchorX = _bestX;
                    _anchorY = _bestY;
                }
            }
            else if (_bestY - _anchorY < 0)
            {
                // Too short to fit a rate over, but lower than the anchor: the anchor was not
                // a best case after all, so it is replaced rather than kept as one.
                _anchorX = _bestX;
                _anchorY = _bestY;
            }

            _haveBest = false;
        }

        /// <summary>
        /// Forget the fit. Call on a session boundary: the line describes one route to one
        /// server, and carrying it across a reconnect measures the new connection against the
        /// old one's clock.
        /// </summary>
        public void Reset()
        {
            _offset = 0;
            _skew = 1.0;
            _haveFit = false;
            _anchorX = _anchorY = 0;
            _haveAnchor = false;
            _previousFloorResidual = 0;
            _havePreviousFloor = false;
            _bestX = _bestY = _bestResidual = 0;
            _haveBest = false;
            _floorResidual = 0;
            _haveFloor = false;
            _epochStartedAt = 0;
            Samples = 0;
            StalenessTicks = 0f;
            BaselineSeconds = 0;
            Fits = 0;
            FitsRefused = 0;
            RefusedSkewPpm = 0;
        }
    }
}
