using System;

namespace Cuvara.Netcode.Prediction
{
    /// <summary>
    /// How often a client should send input, given how often the server sends snapshots —
    /// and the schedule that makes the chosen rate the rate actually sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The wrong anchor this replaces.</b> Every send loop in this package took its
    /// cadence from <c>GameConstants.DefaultTickRate</c>, which is 15, under a tooltip
    /// reading "matches the server's simulation rate". It does not. The server advertises
    /// <c>JoinTokenResponse.tick_rate = SimulationRates.MovementHz = CriticalHz</c>, which
    /// is <b>60</b>; 15 is <c>WorldHz</c>, the rate of the World group — which is also the
    /// cadence the snapshot broadcast runs at. So the constant was correct and the use was
    /// wrong: anchoring the send cadence to it did not match the simulation rate, it matched
    /// the <i>snapshot</i> rate, and matching the snapshot rate is precisely the condition
    /// <see cref="AckLatencyEstimator"/> cannot measure through.
    /// </para>
    /// <para>
    /// <b>Why that is fatal rather than untidy.</b> One acknowledgement observation is
    /// <c>uplink + wait-for-the-next-snapshot + age</c>. The wait is the only varying term,
    /// so the minimum converges on the constant — but only if the wait sweeps. Send at
    /// exactly the snapshot rate and the two lock in phase: every observation carries the
    /// same fixed wait, and the minimum reads high by up to a whole snapshot interval.
    /// The guard added in v0.34.0 detects this and correctly refuses to offer a floor;
    /// measured live, <c>median 61.9 ms, p90 62.7, min 23.7</c> — a p10-to-median span of
    /// 0.28 ticks, a textbook lock. Refusing is right, because a phase-locked client holds
    /// no evidence about its own pipeline constant. But refusing leaves the term
    /// unobtainable <i>in principle</i> rather than merely unimplemented, and the fallback
    /// (<c>RoundTripMs * 0.5</c>) rounds to zero on a fast link. Offsetting the cadence is
    /// the only thing that closes the term, because it is the only thing that makes the
    /// quantity observable at all.
    /// </para>
    /// <para>
    /// <b>Why this is a rule and not the literal 13.</b> <c>WorldHz</c> is operator
    /// configurable, so a hard-coded 13 is right for one deployment and silently wrong for
    /// the next — which is the same shape of defect as the anchor it replaces.
    /// </para>
    /// <para>
    /// <b>THE FRAME GRID MAKES A COPRIME CADENCE UNREACHABLE WITHOUT
    /// <see cref="InputSendSchedule"/>, AND THAT IS WHY THE SCHEDULE IS NOT
    /// OVER-ENGINEERING.</b> A loop that re-derives its deadline from whenever it woke can
    /// only send on a frame boundary, so its achievable rates are <c>fps / n</c> for integer
    /// <c>n</c>. Write <c>k = fps / snapshotHz</c>. Every achievable rate is then
    /// <c>snapshotHz · k / n</c>, and its phase step against the snapshot stream is
    /// <c>frac(n / k)</c> — always a multiple of <c>1/k</c>, so it visits <b>at most k
    /// phases and never more</b>, whatever constant is written in the source. At the shipping
    /// 60 fps against 15 Hz, <c>k = 4</c>: the achievable rates are 20, 15, 12, 10, 8.57,
    /// 7.5 Hz with phase steps of 0.75, 0, 0.25, 0.5, 0.75, 0 — never better than four
    /// phases, and 20, 15 and 7.5 Hz are outright locked at one. <b>No value of this
    /// method's return can produce a sweeping cadence on that grid.</b> So the pinned
    /// schedule is not an optimisation layered on the cadence choice; it is the thing that
    /// makes any cadence choice reachable. Reverting to <c>await Delay(1f / 13f)</c> silently
    /// restores the defect in full.
    /// </para>
    /// <para>
    /// <b>And the client's frame rate bounds the reading even when the cadence is right.</b>
    /// Acknowledgements are read on a render frame, so the wait term resolves only to a frame
    /// period: the same <c>k</c> bounds how many phases the estimator can DISTINGUISH,
    /// independently of how many the cadence VISITS. Two consequences, both measured against
    /// the real estimator with an injected constant of 1.00 base ticks:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Below <c>k = MinimumOccupiedBuckets</c> the floor is unavailable at any cadence.</b>
    /// At 30 fps against 15 Hz, <c>k = 2</c> and the occupancy test cannot reach 3, so 11, 12,
    /// 13 and 14 Hz are <i>all</i> refused. The first frame rate that works is 45 fps
    /// (<c>k = 3</c>). <b>A 30 fps client cannot measure its own pipeline constant</b>, and on
    /// a mobile target that is not a hypothetical. The refusal is correct and this is a real
    /// limitation of the measurement, not of the cadence.
    /// </description></item>
    /// <item><description>
    /// <b>At 60 fps the coprime advantage is invisible, and it is honest to say so.</b> The
    /// frame period (16.67 ms) is coarser than 13 Hz's phase spacing (5.13 ms), so 11, 12, 13
    /// and 14 Hz all read <c>UnsweptSeconds</c> of 16.67 ms and a lead of 1.00. The advantage
    /// appears once the frame rate can resolve it: at 144 fps, 13 Hz reaches an unswept
    /// remainder of 0.00 ms against 12 Hz's 13.89, and a lead of 1.67 against 0.42.
    /// </description></item>
    /// </list>
    /// <para>
    /// So coprimality is chosen for the regime where it can matter and costs nothing where it
    /// cannot — not because it improves the reading on the machine this shipped from.
    /// </para>
    /// </remarks>
    public static class InputCadence
    {
        /// <summary>
        /// The lowest fraction of the snapshot rate a recommendation may fall to.
        /// </summary>
        /// <remarks>
        /// A backstop on the search, not a tuning knob: it does not bind at any sane
        /// snapshot rate (15 yields 13, 20 yields 17, 30 yields 23, 60 yields 53). It exists
        /// so that a pathological rate cannot make <see cref="RecommendedSendHz"/> return
        /// something absurd in pursuit of a coprime factor. When nothing at or above this
        /// share qualifies, the snapshot rate is returned unchanged and the estimator's own
        /// guard refuses — which is the honest outcome, not a silent one.
        /// </remarks>
        public const double MinimumShareOfSnapshotRate = 0.5;

        /// <summary>
        /// The send rate to use against a server broadcasting snapshots at
        /// <paramref name="snapshotHz"/>: the fastest rate below it whose phase against the
        /// snapshot stream sweeps finely and quickly enough for the acknowledgement floor to
        /// mean something.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The two conditions, and why neither alone is enough.</b> Write S for the
        /// snapshot interval. A client sending at <c>f</c> visits phases
        /// <c>(n · snapshotHz / f) mod 1</c> of S, so:
        /// </para>
        /// <para>
        /// <i>1. Resolution</i> — the phase set has exactly
        /// <see cref="DistinctPhases"/> = <c>f / gcd(f, snapshotHz)</c> members, evenly
        /// spaced. Since the floor is a low quantile of <c>constant + wait</c>, the coarseness
        /// of that set is a floor on how precisely the constant can be read. Requiring
        /// <c>gcd(f, snapshotHz) == 1</c> maximises it: every rate coprime to the snapshot
        /// rate visits <c>f</c> distinct phases, and every rate sharing a factor visits
        /// dramatically fewer.
        /// </para>
        /// <para>
        /// <i>1b. Sufficiency</i> — coprimality alone does not guarantee the set is big
        /// enough to be judged. It must hold at least
        /// <see cref="AckLatencyEstimator.MinimumOccupiedBuckets"/> members, or the guard it
        /// feeds cannot pass however long the client runs. This is a second explicit coupling
        /// to the estimator's constants; see the warning at the end of these remarks.
        /// </para>
        /// <para>
        /// <i>2. Speed</i> — the phase completes one full cycle every
        /// <see cref="SendsPerSweep"/> = <c>f / |snapshotHz − f|</c> sends. A sweep slower
        /// than the estimator's own evidence window is a sweep the estimator never sees, so
        /// this is bounded by <see cref="AckLatencyEstimator.MinimumSamples"/>.
        /// </para>
        /// <para>
        /// <b>Worked, for the 15 Hz snapshot rate this ships against.</b> Measured by driving
        /// the real <see cref="AckLatencyEstimator"/> against a 60 Hz base / 15 Hz snapshot
        /// pipeline with an injected constant of 1.00 base ticks:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// <b>15 Hz</b> — 1 distinct phase. Locked. <c>SweptEnough</c> false, no floor
        /// offered, <c>UnsweptSeconds</c> pinned at the whole interval. The defect.
        /// </description></item>
        /// <item><description>
        /// <b>14 Hz</b> — coprime, 14 phases, but 14 sends per sweep against a minimum
        /// sample count of 8. Rejected by condition 2. It does read correctly in steady
        /// state; what it lacks is margin, and margin is the point: drifting each candidate
        /// upward over a single 5 s epoch, 13 Hz still yields an estimate at 13.25, 13.5,
        /// 13.75 and 14.0, while <b>14 Hz refuses the moment it reaches 15.0</b>. 13 keeps
        /// 2 Hz of headroom against re-locking where 14 keeps 1.
        /// </description></item>
        /// <item><description>
        /// <b>13 Hz</b> — coprime, <b>13 phases</b> spaced S/13 = 5.13 ms, 6.5 sends per
        /// sweep, a full sweep every 0.5 s against a 5 s epoch. Chosen.
        /// </description></item>
        /// <item><description>
        /// <b>12 Hz</b> — fast enough (4 sends per sweep) but <c>gcd(12, 15) = 3</c>, so it
        /// visits <b>4 phases</b> where 13 visits 13. This is why condition 1 is not "near the
        /// snapshot rate" but coprimality: 12 is CLOSER to 15 than 13 is, and coarser.
        /// <b>Measured honestly, the penalty is invisible at 60 fps and real above it</b> —
        /// see the frame-rate note in the type remarks. At 60 fps the frame period is coarser
        /// than either cadence's phase spacing, so 12 and 13 both read an unswept remainder
        /// of 16.67 ms and a lead of 1.00 against a true 1.00. At 144 fps, where the phases
        /// can be resolved, 13 Hz reaches 0.00 ms unswept and a lead of 1.67 while 12 Hz
        /// stalls at 13.89 ms and 0.42. The rule prefers the cadence that is not the limiting
        /// factor at any frame rate.
        /// </description></item>
        /// </list>
        /// <para>
        /// <b>THIS COUPLES THE WIRE CADENCE TO
        /// <see cref="AckLatencyEstimator.MinimumSamples"/>.</b> The constant is referenced
        /// by name below rather than inlined so the dependency is greppable, but naming it
        /// does not make it obvious: <b>raising <c>MinimumSamples</c> changes how often every
        /// client sends input.</b> At 15 Hz the current value of 8 selects 13; a value of 14
        /// or more would select 14 Hz instead, changing the uplink packet rate and the
        /// phase geometry for a reason that has nothing to do with either. If that constant
        /// is ever tuned, re-read this method before assuming the cadence is unaffected.
        /// </para>
        /// <para>
        /// <b>Side effects of the recommendation, stated because they are real.</b> At 13
        /// against 15 the client sends ~13% fewer uplink packets, and sends strictly slower
        /// than acknowledgements arrive, so <see cref="AckLatencyEstimator.Superseded"/>
        /// falls to zero. Against that, a change of direction now waits up to 76.9 ms rather
        /// than 66.7 ms to reach the server: <b>+5 ms mean, +10 ms worst case</b>. That is a
        /// real cost in feel, accepted deliberately because the term it buys is currently
        /// worth multiple base ticks of standing reconcile error.
        /// </para>
        /// <para>
        /// The server does not care. Its movement model integrates the newest held direction
        /// once per base tick whether or not a packet arrived, and its hold expiry is a
        /// 250 ms <i>silence</i> timeout rather than a send-rate window — so the tolerated
        /// silence is unchanged and a stall still takes four consecutive lost packets at 13
        /// Hz exactly as it did at 15.
        /// </para>
        /// </remarks>
        /// <param name="snapshotHz">
        /// The rate snapshots arrive at — the server's <c>WorldHz</c>, NOT the
        /// <c>tick_rate</c> it advertises in the join response. Those are different numbers
        /// (15 and 60 by default) and confusing them is the defect this type exists to close.
        /// </param>
        /// <returns>
        /// The recommended send rate in Hz, or <paramref name="snapshotHz"/> unchanged when
        /// no rate qualifies — in which case the acknowledgement floor will not be offered,
        /// which is correct rather than convenient.
        /// </returns>
        public static int RecommendedSendHz(int snapshotHz)
        {
            if (snapshotHz <= 1) return snapshotHz;

            // The floor of the search. See MinimumShareOfSnapshotRate — a backstop, and it
            // does not bind at any rate this package is likely to meet.
            int lowest = (int)Math.Ceiling(snapshotHz * MinimumShareOfSnapshotRate);
            if (lowest < 1) lowest = 1;

            // Fastest first: the recommendation should cost as few packets, and as little
            // input latency, as the two conditions allow.
            for (int f = snapshotHz - 1; f >= lowest; f--)
            {
                if (Sweeps(f, snapshotHz)) return f;
            }

            // Nothing qualifies. Returning the snapshot rate is not a fallback that papers
            // over the problem: it is the locked cadence, the estimator's guard refuses it,
            // and the caller keeps whatever lead it had. A fallback here that invented a
            // plausible-looking rate would be a second claim about the same quantity with
            // none of the evidence, which is the failure this whole area has been fixing.
            return snapshotHz;
        }

        /// <summary>
        /// Whether <paramref name="sendHz"/> against <paramref name="snapshotHz"/> satisfies
        /// both conditions in <see cref="RecommendedSendHz"/>: a maximal phase set, swept
        /// inside the estimator's evidence window.
        /// </summary>
        public static bool Sweeps(int sendHz, int snapshotHz)
        {
            if (sendHz <= 0 || snapshotHz <= 0 || sendHz == snapshotHz) return false;

            // Condition 1: coprime, so the phase set is as fine as the send rate allows.
            if (Gcd(sendHz, snapshotHz) != 1) return false;

            // Condition 1b: THE PHASE SET MUST BE ABLE TO SATISFY THE GUARD IT IS FEEDING.
            //
            // Coprimality alone does not give this. A send rate of 1 against a snapshot rate
            // of 2 is coprime and sweeps in one send by the arithmetic below, yet it visits
            // exactly ONE phase -- it is phase-locked, the condition this whole type exists
            // to avoid, certified as swept. Requiring the set to be at least as large as the
            // occupancy the estimator demands closes it, and ties the two together where the
            // relationship is actually decided: a distribution with fewer distinct values
            // than MinimumOccupiedBuckets cannot occupy that many buckets however long it
            // runs, so recommending one would be recommending a cadence whose reading is
            // refused by construction.
            if (DistinctPhases(sendHz, snapshotHz) < AckLatencyEstimator.MinimumOccupiedBuckets)
            {
                return false;
            }

            // Condition 2: a full sweep inside the estimator's minimum evidence. The
            // reference is deliberately by name -- this is a coupling between the wire
            // cadence and a statistics constant, and it must stay visible to a grep.
            return SendsPerSweep(sendHz, snapshotHz) <= AckLatencyEstimator.MinimumSamples;
        }

        /// <summary>
        /// How many distinct phases of the snapshot interval a client sending at
        /// <paramref name="sendHz"/> ever visits: <c>sendHz / gcd(sendHz, snapshotHz)</c>.
        /// </summary>
        /// <remarks>
        /// The resolution of the whole measurement. The phase advances by
        /// <c>gcd / sendHz</c> of an interval per send and closes after this many, so the
        /// floor cannot resolve the pipeline constant more finely than one interval divided
        /// by this. One means phase-locked: the estimator sees a single fixed wait and
        /// refuses, correctly.
        /// </remarks>
        public static int DistinctPhases(int sendHz, int snapshotHz)
        {
            if (sendHz <= 0 || snapshotHz <= 0) return 0;
            return sendHz / Gcd(sendHz, snapshotHz);
        }

        /// <summary>
        /// Sends needed for the phase to complete one full cycle:
        /// <c>sendHz / |snapshotHz − sendHz|</c>, or infinity when the two are equal.
        /// </summary>
        public static double SendsPerSweep(int sendHz, int snapshotHz)
        {
            if (sendHz <= 0 || snapshotHz <= 0) return double.PositiveInfinity;
            int beat = Math.Abs(snapshotHz - sendHz);
            return beat == 0 ? double.PositiveInfinity : (double)sendHz / beat;
        }

        /// <summary>Seconds for the phase to complete one full cycle.</summary>
        public static double SweepSeconds(int sendHz, int snapshotHz)
        {
            if (sendHz <= 0 || snapshotHz <= 0) return double.PositiveInfinity;
            int beat = Math.Abs(snapshotHz - sendHz);
            return beat == 0 ? double.PositiveInfinity : 1.0 / beat;
        }

        private static int Gcd(int a, int b)
        {
            while (b != 0)
            {
                int t = b;
                b = a % b;
                a = t;
            }

            return a < 0 ? -a : a;
        }
    }

    /// <summary>
    /// A send schedule that does not lose time, so the rate a caller names is the rate that
    /// goes on the wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists, and why choosing a cadence without it is a no-op.</b> Both send
    /// loops in this package were shaped <c>send(); await UniTask.Delay(period);</c>.
    /// <c>UniTask.Delay</c> starts a stopwatch when the delay is <i>constructed</i> — after
    /// the send — and completes on the first Update frame where the elapsed time has reached
    /// the period. The remainder between the period and the frame it actually resumes on is
    /// therefore discarded, every single iteration, and never recovered.
    /// </para>
    /// <para>
    /// The effect is not small and it is not jitter. Simulated at 60 fps against the real
    /// <see cref="AckLatencyEstimator"/>, every nominal rate in the half-open range
    /// <c>(12, 15]</c> collapses onto <b>60/5 = 12 Hz</b> — 15 nominal sends 12, 14 nominal
    /// sends 12, 13 nominal sends 12. A cadence change alone, applied to the loop as it was
    /// written, would have changed <i>nothing whatsoever</i>: same packets, same phase, same
    /// verdict, while reading as a fix in the diff and passing a test driven by an ideal
    /// timer.
    /// </para>
    /// <para>
    /// <b>So the achieved cadence was never a property of the constant.</b> It was a property
    /// of the loop shape and the frame rate, and the two loops in this package disagreed by
    /// 3 Hz because of it: the PlayMode measurement harness pumps against an absolute
    /// deadline and achieved ~15 Hz — which is the lock that was measured live — while the
    /// bootstrap and sample loops achieved ~12 and swept by accident, at a cadence nobody
    /// chose, with only four distinct phases, and only until the frame rate moved.
    /// </para>
    /// <para>
    /// This type keeps the schedule instead of re-deriving the deadline from whenever it
    /// happened to wake: <see cref="NoteSent"/> advances by exactly one period from the
    /// previous <i>scheduled</i> instant, so quantisation error cancels across iterations
    /// rather than accumulating.
    /// </para>
    /// </remarks>
    public struct InputSendSchedule
    {
        /// <summary>
        /// Periods behind schedule beyond which the schedule is resynchronised rather than
        /// caught up.
        /// </summary>
        /// <remarks>
        /// <b>Catch-up is bounded deliberately, because unbounded catch-up is worse than the
        /// drift it fixes.</b> After a stall — an editor breakpoint, a suspended app, a long
        /// GC — a schedule that insists on its backlog emits every missed send at once. That
        /// is a burst at the server's input drain, and it destroys the very phase
        /// relationship this type exists to preserve: a burst arrives inside one snapshot
        /// interval, so every input in it is superseded and contributes no observation.
        /// Four periods is the same shape of test <c>LocalMovePredictor</c> already uses to
        /// tell a pause from a cadence.
        /// </remarks>
        public const int ResyncAfterPeriods = 4;

        private double _period;
        private double _nextDueAt;
        private bool _started;

        /// <summary>
        /// Times the schedule fell far enough behind to be resynchronised instead of caught
        /// up. Counted rather than silent: a client seeing these is stalling, and a resync
        /// restarts the phase relationship the acknowledgement floor depends on.
        /// </summary>
        public int Resyncs { get; private set; }

        /// <summary>Sends the schedule has issued.</summary>
        public int Sends { get; private set; }

        /// <summary>The configured period in seconds; zero before <see cref="Start"/>.</summary>
        public double PeriodSeconds => _period;

        /// <summary>Begins a schedule at <paramref name="sendHz"/>, due immediately.</summary>
        public void Start(int sendHz, double nowSeconds)
        {
            _period = sendHz > 0 ? 1.0 / sendHz : 0.0;
            _nextDueAt = nowSeconds;
            _started = _period > 0.0;
            Resyncs = 0;
            Sends = 0;
        }

        /// <summary>
        /// Seconds until the next send is due; zero or negative when it is due now. Returns
        /// zero on an unstarted schedule so a caller cannot be made to wait forever by a
        /// misconfiguration.
        /// </summary>
        public double SecondsUntilDue(double nowSeconds) =>
            !_started ? 0.0 : _nextDueAt - nowSeconds;

        /// <summary>
        /// Records that a send happened and advances the schedule by exactly one period from
        /// the previous scheduled instant — NOT from <paramref name="nowSeconds"/>, which is
        /// the whole point. See the remarks on <see cref="InputSendSchedule"/>.
        /// </summary>
        public void NoteSent(double nowSeconds)
        {
            if (!_started) return;

            Sends++;
            _nextDueAt += _period;

            // Bounded catch-up. See ResyncAfterPeriods.
            double behind = nowSeconds - _nextDueAt;
            if (behind > _period * ResyncAfterPeriods)
            {
                _nextDueAt = nowSeconds + _period;
                Resyncs++;
            }
        }
    }
}
