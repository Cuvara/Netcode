using System;

namespace Cuvara.Netcode.Prediction
{
    /// <summary>
    /// Keeps a <see cref="LocalMovePredictor"/>'s base-tick clock on the server's: measures the
    /// tick rate, the snapshot age and the acknowledgement floor, feeds a corroborated rate
    /// forward, and steers the phase to the newest snapshot's tick plus the lead those
    /// measurements call for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is its own type.</b> It is the clock half of <c>WorldViewBinder</c>, lifted
    /// out unchanged so that a consumer who does not bind views through the binder -- the
    /// <c>com.cuvara.dots</c> <c>LocalPredictionSystem</c>, which is the path the game actually
    /// runs -- steers the clock with the SAME code rather than a second copy of it. Without
    /// steering, a predictor's clock counts base ticks off the client's wall clock forever:
    /// against a server running 0.7% slow the client's tick line ran from 0 to 22-23 ticks
    /// ahead of the server's over 55 seconds (measured on the game and reproduced by the headless
    /// <c>PredictionHarness</c>), which is that much latency the player feels and, before
    /// <c>ack_applied_tick</c>, that much reconciliation error at every start and stop.
    /// </para>
    /// <para>
    /// <b>How to drive it.</b> Once per NEW snapshot, after merging it and before reconciling:
    /// <see cref="SampleTickRate"/> (the binder does this for every snapshot, predictor or not,
    /// because its interpolation clock reads the same estimator), then
    /// <see cref="OnSnapshot"/> with the predictor. Call <see cref="NoteInputSent"/> beside every
    /// <see cref="LocalMovePredictor.RecordInput(long,float,float)"/>, with the same tick and a
    /// time from the same clock <see cref="OnSnapshot"/> is given, or the acknowledgement floor
    /// is never measured and the lead falls back to half the round trip. Call
    /// <see cref="Reset"/> at a session boundary.
    /// </para>
    /// <para>
    /// Plain C#, no Unity types, single-threaded: drive it from the thread that consumes
    /// snapshots.
    /// </para>
    /// </remarks>
    public sealed class PredictionClockSteering
    {
        /// <summary>
        /// The predictor's ADVERTISED tick rate (the server's <c>tick_rate</c>): what the
        /// acknowledgement floor converts with until a rate is measured. Zero until a predictor is
        /// known, which <see cref="AckLatencyEstimator.RecordAck"/> refuses outright.
        /// </summary>
        private float _advertisedHz;

        /// <summary>Creates the steering with no predictor known yet; <see cref="OnSnapshot"/> supplies one.</summary>
        public PredictionClockSteering()
        {
        }

        /// <summary>
        /// Creates the steering for <paramref name="predictor"/>, whose advertised tick rate is the
        /// fallback the acknowledgement floor converts with until a rate is measured. Null is
        /// accepted (a binder with no predictor still measures the tick rate).
        /// </summary>
        public PredictionClockSteering(LocalMovePredictor predictor)
        {
            _advertisedHz = predictor != null && predictor.IsEnabled ? predictor.TickRateHz : 0f;
        }

        /// <summary>
        /// Measures the server's base tick rate from snapshot arrivals, so the advertised
        /// rate can be verified rather than trusted.
        /// </summary>
        /// <remarks>
        /// Fed here because this is the one place that already sees every snapshot and
        /// owns a clock. Reading it costs nothing; ignoring it costs what a wrong tick
        /// rate costs, which is continuous sub-threshold wrongness that never announces
        /// itself. See <see cref="TickRateEstimator"/>.
        /// </remarks>
        public TickRateEstimator TickRate { get; } = new TickRateEstimator();

        /// <summary>Round-trip time in milliseconds, as last reported by the session.</summary>
        /// <remarks>
        /// Set by the consumer; 0 until then, which makes the steering target degrade to one
        /// snapshot interval rather than to something invented.
        /// </remarks>
        public long RoundTripMs { get; set; }

        /// <summary>
        /// Measures how old the newest snapshot is by the time it is used. See
        /// <see cref="SnapshotStalenessEstimator"/>.
        /// </summary>
        public SnapshotStalenessEstimator Staleness { get; } = new SnapshotStalenessEstimator();

        /// <summary>
        /// Measures the pipeline constant the staleness fit absorbs. See
        /// <see cref="AckLatencyEstimator"/>.
        /// </summary>
        /// <remarks>
        /// Fed from two places: <see cref="NoteInputSent"/>, which the consumer must call, and
        /// <see cref="OnSnapshot"/>, which sees every acknowledgement. A
        /// consumer that never calls <see cref="NoteInputSent"/> simply never gets a floor and
        /// keeps the round-trip fallback — no worse off than before this existed.
        /// </remarks>
        public AckLatencyEstimator AckLatency { get; } = new AckLatencyEstimator();

        /// <summary>
        /// The rate the acknowledgement floor's seconds-to-ticks conversion uses: the measured
        /// one where there is a measurement, the advertised one until then.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The measured rate is the correct factor, not merely the safer one.</b>
        /// <see cref="AckLatencyEstimator"/> uses this only to turn a duration in CLIENT seconds
        /// into base ticks, and <see cref="TickRateEstimator.EstimatedHz"/> is "server ticks per
        /// client second" — the two units match by construction. Converting with the advertised
        /// rate instead makes a client whose clock runs fast by <c>e</c> report a floor
        /// <c>(1+e)</c> too large, and the steering lead over-lead by <c>e × floor</c>.
        /// </para>
        /// <para>
        /// <b>The fallback is guarded and counted, not silent.</b> Until the estimator has a
        /// measurement there is nothing to convert with but the advertised rate — and a fallback
        /// is another claim about the same quantity, not an absence of one. Every fallback in
        /// this area that went wrong went wrong by being invisible: a floor truncated to whole
        /// ticks contributing zero while still taking its branch, a round trip rounding to zero
        /// on a fast link, a clamp saturating and delivering its bound on every call. So the
        /// substitution is visible in <see cref="AckFloorRateIsFallback"/> and countable in
        /// <see cref="AckFloorRateFallbacks"/>, and a consumer is expected to report it.
        /// </para>
        /// <para>
        /// Zero when there is neither — no predictor and no estimate — which
        /// <c>RecordAck</c> refuses outright rather than converting with a made-up rate.
        /// </para>
        /// </remarks>
        public float AckFloorConversionHz
        {
            get
            {
                if (TickRate.HasEstimate && TickRate.EstimatedHz > 0f)
                {
                    return TickRate.EstimatedHz;
                }

                return _advertisedHz;
            }
        }

        /// <summary>
        /// Whether <see cref="AckFloorConversionHz"/> is currently the advertised rate standing
        /// in for a measurement that does not exist yet.
        /// </summary>
        public bool AckFloorRateIsFallback => !(TickRate.HasEstimate && TickRate.EstimatedHz > 0f);

        /// <summary>
        /// Acknowledgements folded in using the advertised rate because no measured one was
        /// available. Counted so a fallback that never stops being one is visible.
        /// </summary>
        /// <remarks>
        /// A handful at the start of a session is the normal case — the tick rate estimator
        /// needs snapshots before it can report. A count that keeps climbing means the estimator
        /// never reached an estimate, and every floor on that session was converted with a rate
        /// nobody measured.
        /// </remarks>
        public int AckFloorRateFallbacks { get; private set; }

        /// <summary>
        /// Records that an input has just been sent, so its acknowledgement can be timed.
        /// </summary>
        /// <param name="inputTick">The tick stamped on the input, as handed to <c>SendInput</c>.</param>
        /// <param name="nowSeconds">
        /// Now, in seconds, from the SAME clock later passed to <see cref="OnSnapshot"/>: the two
        /// ends of the measurement must not come from different clocks.
        /// </param>
        /// <remarks>
        /// <b>What it buys.</b> Without it the steering target is missing the constant part of
        /// <c>uplink + snapshot age</c>, which no other measurement in the package can see, and
        /// the reconcile returns one step of correction per base tick of it at every start and
        /// stop. Measured live at 2.00 steps against an acknowledgement floor of 1.28 ticks.
        /// </remarks>
        public void NoteInputSent(long inputTick, double nowSeconds) =>
            AckLatency.RecordSent(inputTick, nowSeconds);

        /// <summary>
        /// Folds a new snapshot's base tick into <see cref="TickRate"/>. Call once per new
        /// snapshot, before <see cref="OnSnapshot"/>.
        /// </summary>
        public void SampleTickRate(long worldTick, double nowSeconds) =>
            TickRate.Sample(worldTick, nowSeconds);

        /// <summary>
        /// Seeds, measures and steers <paramref name="predictor"/>'s clock for one new snapshot.
        /// </summary>
        /// <param name="predictor">The predictor to steer; nothing happens when null or disabled.</param>
        /// <param name="worldTick">The snapshot's server base tick (<c>WorldState.Tick</c>).</param>
        /// <param name="ackTick">The snapshot's acknowledgement (<c>WorldState.AckTick</c>).</param>
        /// <param name="nowSeconds">Now, in seconds, from the clock <see cref="NoteInputSent"/> uses.</param>
        public void OnSnapshot(LocalMovePredictor predictor, long worldTick, long ackTick, double nowSeconds)
        {
            if (predictor == null || !predictor.IsEnabled)
            {
                return;
            }

            _advertisedHz = predictor.TickRateHz;

            // Idempotent: only the first positive tick of a session takes effect.
            predictor.SeedBaseTick(worldTick);

            // ...and keep them together afterwards. Seeding aligns the two
            // clocks once, at join; from then on each counts base ticks off its
            // own wall clock and nothing bounds the difference. Measured live,
            // the client's tick ran 456 ticks -- 7.6 seconds -- past the newest
            // snapshot and was still climbing, with every correction counter
            // reading clean, because Reconcile replays that whole span as
            // prediction lead. See LocalMovePredictor.SteerToServerTick.
            //
            // The target lead is what the client has to predict THROUGH to be
            // showing "now": one snapshot interval, because that is how stale
            // the newest snapshot already is when it arrives, plus half a round
            // trip for the journey. Both are measured rather than assumed.
            // Sampled at the moment of USE, not of arrival, so the wait for this
            // frame is inside the measurement -- that wait is a real part of how
            // old the snapshot is when the client acts on it, and it is the part
            // that varies with a client's join phase.
            // The ADVERTISED rate, not the one measured off the wire. The
            // measurement converts a TICK NUMBER to a time -- `snapshotTick /
            // baseHz` -- so it has to use the rate the server stamped that tick
            // at; an estimate's error becomes a drift against the wall clock that
            // grows with the tick counter, and a 57.7 Hz reading of a 60 Hz server
            // took this from a stable figure to 613 ticks and climbing in under a
            // minute, dragging the steering with it.
            Staleness.Sample(worldTick, nowSeconds, predictor.TickRateHz);

            // THE MEASURED RATE, AND THAT IS NOT A CONTRADICTION OF THE COMMENT
            // ABOVE -- these two lines convert different things and want different
            // rates, which is why they no longer share a justification.
            //
            // AckLatencyEstimator uses baseHz for exactly one purpose: turning a
            // DURATION in client seconds into a number of base ticks
            // (`FloorSeconds * baseHz`). It never converts a tick number to a time,
            // so the drift that broke the staleness fit cannot arise here -- there
            // is no growing tick counter for an error to accumulate against, only a
            // few tens of milliseconds.
            //
            // And for a duration the measured rate is the CORRECT one, not merely
            // the safer one. EstimatedHz is "server ticks per CLIENT second", which
            // is exactly the factor a duration measured on the client's clock needs.
            // With the advertised rate instead, a client whose clock runs fast by
            // `e` reports a floor `(1+e)` too large and the steering lead -- which
            // is compared against the server's own tick numbers -- over-leads by
            // `e * floor`. Bounded at 0.02 * floor on any run the validity gate
            // admits, and measured at 0.068 base ticks on a refused arm: small,
            // real, and in the over-lead direction this estimator exists to avoid.
            //
            // It also does not matter whether the disagreement is a fast client or
            // a slow server. Both produce an error of `advertised/measured - 1` and
            // both take this same correction, so the fix does not depend on
            // attributing it -- which is fortunate, because nothing here can.
            //
            // The round-trip term at TargetLeadTicks already converts with the
            // measured rate. This was the same kind of quantity converted two ways
            // in one class.
            if (AckFloorRateIsFallback) AckFloorRateFallbacks++;
            AckLatency.RecordAck(ackTick, nowSeconds, AckFloorConversionHz);

            // RATE FIRST, THEN PHASE. A rate difference left to the steering alone was
            // absorbed as a standing tick offset of drift / (gain * snapshotHz) -- 3.6 base
            // ticks for a client clock 9% fast at 15 snapshots a second -- while the steering
            // was proportional only. It now has an integral term that closes that droop on its
            // own (SteerToServerTick), but slowly and only within its clamp; a corroborated
            // measured rate removes the drift at the source, and the integrator then has
            // nothing left to learn.
            //
            // Gated on IsUsable, never on HasEstimate: the provisional reading
            // carries no rate at all, and a rate wired into this clock from a
            // short baseline is the failure SnapshotStalenessEstimator's remarks
            // record twice, once reaching 613 ticks.
            //
            // AND GATED ON RateCorroborated, WHICH IS THE FIX FOR A THIRD.
            //
            // The envelope fit is only a rate if the minimum achievable delay was
            // the same at both anchors. That assumption was documented and never
            // tested, and the fit resting on it was handed straight to a clock. A
            // starved frame loop raises the delay floor, the later anchor sits
            // above the true line, and the slope absorbs the rise AS RATE: one
            // machine minutes apart read 220 ppm idle and 90 636 ppm inside a
            // loaded suite, and the client obediently ran its clock 8.3% slow and
            // sat at a three-tick standing error.
            //
            // IsUsable is the right gate for the AGE and the wrong one for the
            // RATE. A wrong slope perturbs a residual slightly, once per snapshot;
            // it perturbs a clock rate every second, forever. Different evidence
            // requirements, and they used to share one gate.
            if (Staleness.IsUsable && Staleness.RateCorroborated)
            {
                predictor.SetClockRateScale(
                    (float)(1.0 / (1.0 + Staleness.SkewPpm / 1e6)));
            }

            predictor.SteerToServerTick(worldTick, TargetLeadTicks());
        }

        /// <summary>
        /// Base ticks the client's clock should sit ahead of the newest snapshot's tick: how
        /// old that snapshot already is when it is acted on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The clock is steered onto the newest snapshot's tick, and that tick is old by the
        /// time it is read. Steering to it with no allowance drags the client's clock behind
        /// the server's real one by exactly that age, and a tick number then stops naming the
        /// same moment on both sides: the client's tick N carries inputs the server will not
        /// apply until its own tick N + age. The reconcile reports that as a correction of one
        /// input interval, on every snapshot.
        /// </para>
        /// <para>
        /// <b>Measured, with a derived fallback — and the fallback is now bounded by a
        /// measurement too.</b> <see cref="Staleness"/> fits the server's clock to the
        /// client's — offset and rate — and reports the height of the newest snapshot above
        /// that line, which is its age beyond the best the route has shown. Until it has a
        /// line, the derived figure is one snapshot interval, because the newest snapshot
        /// describes the tick it was produced on and the next is a whole interval behind it.
        /// </para>
        /// <para>
        /// <b>That derived figure alone was wrong for eight seconds of every session, and
        /// wrong by the whole of the defect it was meant to cover.</b> A rate is a slope and
        /// cannot honestly be fitted over a short baseline, so
        /// <see cref="SnapshotStalenessEstimator.IsUsable"/> only turns true once two anchors
        /// sit <see cref="SnapshotStalenessEstimator.MinimumBaselineSeconds"/> apart —
        /// measured against a 15 Hz snapshot stream, <b>8.2 s after join</b>. Over those eight
        /// seconds the real age on localhost is <b>0.06 base ticks</b> and the derived figure
        /// is <b>4</b>, so the clock is steered four base ticks past the server's and the
        /// reconcile reports exactly that as a correction — <b>0.3333 world units, 4.00
        /// steps</b> — at every start and every stop. Live, that is 36 corrections in 162
        /// reconciles with the tick rate agreeing on both sides and every other counter clean,
        /// which reads as a rate mismatch and is not one.
        /// </para>
        /// <para>
        /// So the age is taken from the estimator as soon as it has a reading at all
        /// (<see cref="SnapshotStalenessEstimator.HasEstimate"/>), fitted or provisional, and
        /// the derived figure becomes a CEILING on it rather than a substitute for it. The
        /// asymmetry is the point and it is not a heuristic: an unfitted rate can only drift
        /// the provisional reading UPWARD, so below the derived figure the reading is evidence
        /// and above it it is drift. Taking the smaller is therefore never worse than the old
        /// fallback and is four ticks better on the measured case.
        /// </para>
        /// <para>
        /// Either way the half round trip is added on top: the one-way delay sits inside the
        /// fitted offset, inseparable from the difference between the two clocks' origins, so
        /// no arrival-time measurement can recover it and the caller must supply it.
        /// </para>
        /// <para>
        /// <b>The ceiling is not decoration.</b> This number steers a clock, and a measurement
        /// that can run away will take the simulation with it — the clock follows, the
        /// reconcile reports the growing gap as a correction, and both keep going. An earlier
        /// estimator that fitted the offset alone did exactly that, reaching 613 ticks with
        /// the lead tracking it the whole way. Two snapshot intervals plus a round trip is
        /// past anything a healthy link produces and far short of a runaway.
        /// </para>
        /// </remarks>
        /// <remarks>
        /// <b>Public, not hidden:</b> this number steers a clock, and the defect it last
        /// carried was invisible from outside — a four-tick lead against a real age of 0.06
        /// reads on every other counter as a healthy client. It is asserted directly by
        /// <c>WorldViewBinderLeadTests</c> (through the binder) rather than inferred from a
        /// downstream symptom, and a consumer driving this type itself can read it the same way.
        /// </remarks>
        public int TargetLeadTicks()
        {
            int gap = TickRate.SnapshotTickGap > 0 ? TickRate.SnapshotTickGap : 1;

            float lead;
            if (Staleness.AgeIsFitted)
            {
                // A fitted line WHOSE SLOPE HAS REPRODUCED. Believe it; the ceiling below is
                // the only guard it then needs.
                //
                // This used to read `Staleness.IsUsable`, with the comment "A fitted line.
                // Believe it." That was true while the only thing that could go wrong with a
                // fit was noise. It is not true now that a fit can be a displacement divided by
                // a baseline: the age is the height above THAT line, so an uncorroborated slope
                // reached the lead through the residual even after the clock stopped listening
                // to it. Live, with the rate correctly refused, a 51 225 ppm fit still drove the
                // age to 5.24 base ticks against a true idle 0.06, and the lead to 6.
                lead = Staleness.StalenessTicks;
            }
            else if (Staleness.HasEstimate)
            {
                // Provisional, so trusted only downwards -- but a reading that SATURATES that
                // clamp is not a reading at all, and treating it as one delivers the defect.
                //
                // Math.Min(.., gap) was written as a ceiling on a provisional figure expected to
                // be roughly right and occasionally high. A provisional reading has no slope
                // term, so when the timebase is untrustworthy it accumulates at the apparent
                // skew: measured at 45.56 base ticks against a true age of 0.09, on a run whose
                // apparent skew was 81 351 ppm. Clamped, that returns `gap` on every call -- and
                // a saturated clamp is a constant, which is precisely the warm-up fallback this
                // work exists to remove. All three arms of that run steered on a lead of 4
                // against an age under a tenth of a tick, and the report labelled it, one line
                // below: "a lead equal to this is the warm-up fallback, not a measurement".
                //
                // THE PRINCIPLE, because it generalises past this function: AN UNTRUSTWORTHY
                // TIMEBASE MUST PRODUCE AN UNDER-LEAD, NOT THE LARGEST LEAD AVAILABLE. And the
                // corollary a future reader needs on seeing the Math.Min this replaced: A CLAMP
                // ON AN UNTRUSTED QUANTITY IS NOT A GUARD, IT IS A DEFAULT -- and defaults get
                // delivered. A clamp only guards while the quantity it bounds is roughly right;
                // once the quantity saturates it, the clamp value IS the output, on every call,
                // and whatever that value happens to be is what the system now does.
                //
                // So saturation is treated as EVIDENCE OF AN UNUSABLE READING rather than as a
                // number to clamp. A provisional age above one snapshot interval is not a
                // plausible age for a healthy route; a route genuinely that slow produces a fit
                // whose slope REPRODUCES, and takes the branch above. Down here it means the
                // timebase cannot be trusted, and an untrustworthy timebase must produce an
                // UNDER-lead, not the largest one available -- the uplink is still covered by the
                // acknowledgement floor, which is measured independently of this.
                float provisional = Staleness.StalenessTicks;
                lead = provisional > gap ? 0f : provisional;
            }
            else
            {
                lead = gap;
            }

            // THE PIPELINE CONSTANT. The staleness reading above is the age ABOVE its own
            // envelope floor; this is the constant that envelope absorbed, plus the uplink,
            // which no arrival-time measurement can recover at all. Their sum is the whole of
            // uplink + age and neither counts anything twice.
            //
            // THE ROUND TRIP IS COMPUTED WHETHER OR NOT THERE IS A FLOOR, AND THAT IS A FIX.
            //
            // It used to sit in an `else if` behind AckLatency.HasEstimate, which made HAVING
            // a floor and USING a floor the same condition. They are not the same condition,
            // because the floor was truncated to whole base ticks: on a localhost link, where
            // uplink + age is a fraction of a tick, it contributed exactly ZERO while still
            // taking the branch. Turning the estimator on therefore did nothing at all except
            // DELETE the half-round-trip term from the lead and its whole-tick term from the
            // ceiling below.
            //
            // That is the coupling behind the second open question on this work — the measured
            // clock error moving from -1 to -2/-4 when the estimator was wired in, from a
            // change that argued it was safe by construction. It was not the floor steering
            // anything. It was the round trip no longer steering anything. An estimator whose
            // safety argument is "the worst case is that it contributes nothing" must not be
            // able to make the lead SMALLER than it was without it, and behind an `else` it
            // could, by exactly rttTicks * 0.5.
            //
            // So both terms are computed and the LARGER is used. AckLatency times the real
            // path end to end -- the input drain, the server's staged snapshot write, the
            // wire, the wait for a client frame -- which is the quantity the reconcile needs,
            // and it is the better number wherever it is the bigger one. RoundTripMs is a
            // heartbeat ping through the socket: it sees none of the staging, and half of it
            // is not the quantity either. Taking the maximum keeps the floor's contribution
            // where it is real, and keeps the fallback bit-for-bit as it was for a consumer
            // that supplies a round trip and never calls NoteInputSent.
            int rttTicks = 0;
            float rttLead = 0f;
            if (RoundTripMs > 0 && TickRate.EstimatedHz > 0f)
            {
                rttTicks = (int)Math.Round(RoundTripMs * TickRate.EstimatedHz / 1000.0);
                rttLead = rttTicks * 0.5f;
            }

            // FRACTIONAL, AND BIASED LOW BY ITS OWN UNCERTAINTY RATHER THAN BY TRUNCATION.
            //
            // The bias is not optional -- an over-lead steers the client past the server, the
            // original defect arriving from the other side -- but truncating the floor to
            // whole base ticks is a guard that fires as a total loss. Live it read 0.14 and
            // 0.68 base ticks and Math.Floor returned zero both times. Combined with the
            // displacement below, that is the whole story of why wiring this in changed
            // nothing except to make the clock error worse: the floor contributed zero while
            // still taking the branch that removed the round trip.
            //
            // And a sub-tick deficit is not a sub-tick problem. The tick LABEL is an integer:
            // a client leading 0.68 ticks short carries the wrong tick number for 68% of every
            // tick, and the reconcile returns a WHOLE step of correction for it. That is the
            // second step of the live 2.00 against a floor of 1.00 -- so truncation did not
            // merely cost accuracy here, it was the reason the term stayed open.
            //
            // AckLatencyEstimator.ConservativeFloorTicks keeps the bias and puts it where the
            // bias actually IS: the floor is the tenth percentile of `constant + wait`, so on a
            // swept link it sits 0.1 * S above the constant BY CONSTRUCTION, on every clean run.
            // That measured offset is what it subtracts, using the ladder slope fitted from the
            // run's own observations. It reads zero -- and says so, via FloorCorrectionRefusals
            // -- only when the ladder is not straight and the model predicting the bias is
            // therefore false, instead of whenever the link is fast enough that the constant is
            // under one base tick.
            float ackLead = AckLatency.HasEstimate ? AckLatency.ConservativeFloorTicks : 0f;

            // THE FLOOR DISPLACES THE ROUND TRIP RATHER THAN ADDING TO IT, BECAUSE THEY
            // MEASURE THE SAME THING. Adding them counts the pipeline twice. The floor wins
            // because it times the real path end to end -- the input drain, the server's
            // staged snapshot write, the wire, the wait for a client frame -- while
            // RoundTripMs is a heartbeat through the socket that sees none of the staging, and
            // half of it is not the quantity anyway.
            //
            // BUT THE DISPLACEMENT IS THE ONE PATH BY WHICH THIS ESTIMATOR CAN TOUCH THE
            // STEER, and it is worth naming because it went unnoticed once. A floor that
            // appears removes rttLead from the lead, so a small floor against a large round
            // trip makes the lead SMALLER -- which is the coupling behind the clock error
            // moving from -1 to -2/-4 when this was first wired in, from a change that argued
            // it was safe by construction. It was never the floor steering anything; it was
            // the round trip no longer steering anything, while a truncated floor put nothing
            // back. With the truncation gone the displacement is a measurement decision rather
            // than a silent deletion, which is what makes it defensible; it is still a real
            // coupling and a live run that moves the clock error should look here first.
            lead += AckLatency.HasEstimate ? ackLead : rttLead;
            int ticks = (int)Math.Round(lead);
            if (ticks < 0) ticks = 0;

            // The ceiling bounds a RUNAWAY, not a measurement. The acknowledgement floor is a
            // measured constant with its own refusal band, and on a slow link it legitimately
            // exceeds two snapshot intervals -- clamping it there would reintroduce the
            // under-lead this estimator exists to remove, on exactly the connections that
            // suffer most from it. So it raises the ceiling with it rather than being cut by
            // it, while the derived terms stay bounded as before.
            //
            // rttTicks is in the ceiling UNCONDITIONALLY, and that is a fix. It used to be
            // computed only on the branch the floor did not take, so the arrival of a floor
            // silently lowered the ceiling by rttTicks as well as lowering the lead -- a
            // clamp tightening for a reason that has nothing to do with a runaway. A ceiling
            // that shrinks the moment a measurement appears is not a bound, it is a second,
            // accidental steer.
            int ceiling = gap * 2 + rttTicks + (int)Math.Ceiling(ackLead);
            return ticks > ceiling ? ceiling : ticks;
        }

        /// <summary>
        /// Forgets the tick-rate and staleness measurements: they describe one route to one
        /// server, and carrying them across a session boundary measures the new connection against
        /// the old one's best case.
        /// </summary>
        public void Reset()
        {
            TickRate.Reset();
            Staleness.Reset();
        }
    }
}
