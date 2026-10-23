namespace Cuvara.Netcode.Interpolation
{
    /// <summary>
    /// The render timeline: a free-running clock, expressed in server ticks, that says
    /// which moment of the world every entity should be drawn at this frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One per connection, not one per entity.</b> Every entity's ticks come from the
    /// same server clock, so they must be rendered against the same moment or they will
    /// disagree with each other — two avatars passing each other would be drawn at
    /// different instants of the same world.
    /// </para>
    /// <para>
    /// <b>It measures itself in ticks, not in seconds, and that is the fix.</b> The
    /// implementation this replaced stamped the arrival time of each snapshot and computed
    /// a phase as <c>(now - arrival) / measuredInterval</c>. Both terms were network
    /// facts, so the phase restarted at 0 on every arrival: a snapshot arriving early
    /// threw away the unrendered remainder of the current segment and lurched forward, a
    /// late one ran to a clamp, froze, and then stepped backwards when the snapshot
    /// landed. Here <see cref="RenderTick"/> is a position on the <i>server's</i> timeline
    /// that only ever moves forward, at very close to one tick per
    /// <see cref="SecondsPerTick"/>, and an arrival changes nothing about it except where
    /// it is eventually heading.
    /// </para>
    /// <para>
    /// <b>Never snapped, only dilated.</b> When the clock is behind or ahead of
    /// <see cref="TargetTick"/> it runs slightly fast or slightly slow — bounded by
    /// <see cref="InterpolationConfig.MaxClockRateAdjust"/> — until the error is gone. The
    /// rate is <c>1 + adjust</c> and is floored above zero, so
    /// <see cref="RenderTick"/> is <b>strictly increasing for any positive delta</b>. That
    /// single invariant is what makes "the rendered position never steps backwards"
    /// provable rather than hoped for, because the rendered position is a monotonic
    /// function of a monotonic clock along a fixed path.
    /// </para>
    /// <para>
    /// <b>Two exceptions, both after a stall and both still forward-only.</b> A clock left more
    /// than <see cref="ResyncBehindSeconds"/> behind (a capped frame delta) is set forward once
    /// the stream is back on its cadence; a clock more than the extrapolation hold window past
    /// the newest sample while snapshots arrive on cadence is held at the rate floor. Neither
    /// happens under jitter, and neither moves the clock backwards.
    /// </para>
    /// <para>
    /// Blittable: <c>bool</c>, <c>long</c> and <c>double</c> only. It is an
    /// <c>IComponentData</c> singleton on the ECS path and a field on
    /// <see cref="Cuvara.Netcode.View.WorldViewBinder"/> on the GameObject path.
    /// </para>
    /// </remarks>
    public struct InterpolationClock
    {
        /// <summary>Whether any snapshot has been seen. Nothing renders before that.</summary>
        public bool HasSamples;

        /// <summary>Newest server tick heard from, on any entity.</summary>
        public long NewestTick;

        /// <summary>
        /// Measured seconds a single server tick takes, seeded from the confirmed
        /// snapshot tick gap and refined by an EMA of <c>arrivalGap / tickGap</c>.
        /// </summary>
        /// <remarks>
        /// <b>Per tick, not per snapshot</b>, which is what makes a skipped tick harmless.
        /// A snapshot arriving after a doubled gap carries a doubled tick delta too, so
        /// the ratio is unchanged and the estimate does not move. The implementation this
        /// replaced averaged arrival intervals directly, so one dropped snapshot dragged
        /// the divisor 30 % of the way to twice its value while the position delta it
        /// divided had genuinely doubled — the entity rendered at over 1.5x speed and then
        /// froze.
        /// </remarks>
        public double SecondsPerTick;

        /// <summary>
        /// The moment being rendered, in server ticks. Fractional, and strictly increasing.
        /// </summary>
        public double RenderTick;

        /// <summary>
        /// Band, as a ratio of the current <see cref="SecondsPerTick"/>, inside which a measured
        /// interval is a rate sample at all. Also the band an arrival must fall in to count as
        /// on the stream's cadence.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why there is a band.</b> An arrival gap over a tick gap is a rate only when the
        /// snapshots left the server and reached the client at the same pace. After a client
        /// stall that is false twice over: the first snapshot after it measures the stall (2 s
        /// over 4 ticks, 0.5 s per tick), and the backlog behind it is delivered back to back
        /// (10 ms over 4 ticks, 2.5 ms per tick). Both were accepted. Live, the backlog pulled
        /// the estimate from 16.7 ms to about 3 ms per tick, the clock ran at about 300 ticks a
        /// second and ended 91 ticks past the newest snapshot, and every remote was held and
        /// stepped once per snapshot for the 12 s it took the 10 % rate cap to undo
        /// (rpg-factory testlab S13, runs 20261022T194438Z-S13-vdqw and -q3rr).
        /// </para>
        /// <para>
        /// Ordinary jitter stays inside it: 20 ms either way on a 66.7 ms snapshot interval is
        /// a ratio of 0.4 to 1.6 per arrival, and a sample is measured from the last ACCEPTED
        /// arrival, so the occasional early one is simply folded into the next. A dropped
        /// snapshot doubles the tick gap with the arrival gap and does not move the ratio.
        /// </para>
        /// </remarks>
        public const double MinSampleRatio = 0.5;

        /// <summary>Upper end of the band; see <see cref="MinSampleRatio"/>.</summary>
        public const double MaxSampleRatio = 2.0;

        /// <summary>
        /// Seconds of continuous rejection after which the rejected span is adopted as the rate.
        /// </summary>
        /// <remarks>
        /// The band would otherwise lock in a wrong estimate forever, for instance a first
        /// measurement taken off a burst. A backlog ends when it is drained, which takes a
        /// fraction of a second once the transport reads every queued datagram per wake; a rate
        /// that really is outside the band is still outside it five seconds later.
        /// </remarks>
        public const double RelearnSeconds = 5.0;

        /// <summary>
        /// Shortest span, from the first snapshot, that the first rate measurement is taken over.
        /// About four snapshot intervals at 15 Hz: long enough that the bunching of a join does not
        /// set the rate, short enough that the seed (the nominal interval over the observed tick
        /// gap) is used only briefly.
        /// </summary>
        public const double FirstMeasurementSeconds = 0.25;

        /// <summary>
        /// Seconds behind <see cref="TargetTick"/> past which the clock is set forward rather than
        /// dilated, once the stream is back on its cadence.
        /// </summary>
        /// <remarks>
        /// Five jitter buffers. Ordinary jitter and the 10 % rate cap never leave the clock this
        /// far behind; a frame whose delta the caller capped does: the DOTS path advances by
        /// <c>Time.deltaTime</c>, which Unity limits to <c>maximumDeltaTime</c> (0.333 s by
        /// default), so a 2 s stall leaves the clock 1.7 s behind, and dilation at 10 % would
        /// take 17 s to close it. Setting it forward keeps <see cref="RenderTick"/> strictly
        /// increasing.
        /// </remarks>
        public const double ResyncBehindSeconds = 0.5;

        /// <summary>
        /// Interval samples rejected by the band (<see cref="MinSampleRatio"/>). Lifetime total:
        /// not cleared by <see cref="Reset"/>.
        /// </summary>
        public int RejectedIntervalSamples { get; private set; }

        /// <summary>
        /// Times the clock was set forward (<see cref="ResyncBehindSeconds"/>). Lifetime total.
        /// </summary>
        public int ClockResyncs { get; private set; }

        /// <summary>
        /// Frames the clock was held because it was past the newest sample by more than the
        /// hold window while snapshots kept arriving on cadence. Lifetime total.
        /// </summary>
        public int AheadHoldFrames { get; private set; }

        private double _lastReceiveTime;
        private bool _hasMeasurement;

        // The last ACCEPTED rate sample. Samples are measured from here rather than from the
        // previous arrival, so a run of rejected arrivals (a stall gap, then a backlog delivered
        // back to back) becomes one ordinary span once the stream is back on its cadence.
        private long _anchorTick;
        private double _anchorTime;

        // A run of rejected samples, for RelearnSeconds.
        private bool _inRejectStreak;
        private long _streakTick;
        private double _streakTime;

        // Whether the newest arrival came on the stream's cadence, and the seconds the clock has
        // advanced since it.
        private bool _lastArrivalOnCadence;
        private double _sinceArrival;

        // Set by an arrival off cadence (a stall's late first snapshot, its backlog); cleared by
        // the one forward-resync decision made once the stream is back on cadence.
        private bool _recovering;

        /// <summary>
        /// Where <see cref="RenderTick"/> is heading: the newest tick, held back by
        /// <see cref="InterpolationConfig.TargetDelay"/>.
        /// </summary>
        /// <remarks>
        /// Built from the tick number, which is an exact integer off the wire, so the
        /// target does not move when a packet is early or late — only when the server
        /// actually advances. That is the difference between a jitter buffer and a
        /// jitter amplifier.
        /// </remarks>
        public double TargetTick(in InterpolationConfig config)
        {
            if (SecondsPerTick <= 0.0)
            {
                return NewestTick;
            }

            return NewestTick - config.TargetDelay / SecondsPerTick;
        }

        /// <summary>
        /// Records a snapshot arrival: refines <see cref="SecondsPerTick"/> and moves the
        /// target. Ignores anything not newer than <see cref="NewestTick"/>.
        /// </summary>
        /// <param name="tick">The snapshot's server tick.</param>
        /// <param name="receiveTimeSeconds">Caller's monotonic clock, in seconds.</param>
        /// <param name="snapshotTickGap">
        /// Confirmed gap between consecutive snapshot ticks, or zero if not yet known —
        /// <see cref="Cuvara.Netcode.Prediction.TickRateEstimator.SnapshotTickGap"/>. Used
        /// only to seed the very first estimate, because it is a <i>minimum</i> and so
        /// never widens when a snapshot is dropped.
        /// </param>
        /// <param name="config">Tuning.</param>
        public void NoteSnapshot(long tick, double receiveTimeSeconds, int snapshotTickGap, in InterpolationConfig config)
        {
            if (HasSamples && tick <= NewestTick)
            {
                // Out of order or a duplicate. The wire is ordered today, but a reordered
                // snapshot must not be allowed to drag the timeline backwards, and
                // dropping it costs nothing: its state is already superseded.
                return;
            }

            if (!HasSamples)
            {
                var gap = snapshotTickGap > 0 ? snapshotTickGap : 1;
                SecondsPerTick = config.DefaultInterval / gap;
                ClampInterval(config);

                // Start the render clock a full TargetDelay behind the first tick rather
                // than at it. Starting at the tick would leave nothing to interpolate
                // toward, so the first frames would extrapolate — the buffer has to fill
                // before it can be a buffer, and this is what filling it looks like: the
                // entity holds at its first known position for one delay and then moves.
                RenderTick = tick - config.TargetDelay / SecondsPerTick;
                HasSamples = true;
                NewestTick = tick;
                _lastReceiveTime = receiveTimeSeconds;
                _anchorTick = tick;
                _anchorTime = receiveTimeSeconds;
                _inRejectStreak = false;
                _lastArrivalOnCadence = true;
                _sinceArrival = 0.0;
                _recovering = false;
                return;
            }

            // Judged against the estimate as it stood before this arrival.
            _lastArrivalOnCadence = OnCadence(tick - NewestTick, receiveTimeSeconds - _lastReceiveTime);
            _sinceArrival = 0.0;
            if (!_lastArrivalOnCadence) _recovering = true;

            // UNTIL MEASURED, THE SEED FOLLOWS THE TICK GAP. The seed assumes the nominal snapshot
            // interval over a tick gap the caller may not know yet (the binder's is confirmed only
            // on its second sighting, the DOTS path passes none), and a gap of one made it 15 ticks
            // a second against a 30 Hz server. The gap between the first snapshots is a fact.
            long arrivalTickDelta = tick - NewestTick;
            if (!_hasMeasurement && arrivalTickDelta > 0 && arrivalTickDelta < 16)
            {
                SecondsPerTick = config.DefaultInterval / arrivalTickDelta;
                ClampInterval(config);
            }

            long tickDelta = tick - _anchorTick;
            double secondsDelta = receiveTimeSeconds - _anchorTime;

            // THE FIRST MEASUREMENT SPANS AT LEAST FirstMeasurementSeconds. A join delivers its first
            // snapshots close together: live, the first two of a 30 Hz server arrived 31 ms apart for
            // two ticks. Taken as the rate (15.5 ms per tick) and then guarded by the band, the
            // real 36 ms per tick sat at 2.3x and was refused for the whole RelearnSeconds, and the
            // first joiner's render clock ran at about 60 ticks a second, 93 ticks past the newest
            // snapshot, then froze (rpg-factory testlab S12, run 20261023T010228Z-S12-qiaw).
            if (!_hasMeasurement && secondsDelta < FirstMeasurementSeconds)
            {
                tickDelta = 0;
            }

            if (tickDelta > 0 && secondsDelta > 0.0)
            {
                double perTick = secondsDelta / tickDelta;
                bool sane = perTick >= config.MinInterval && perTick <= config.MaxInterval;
                // Both conditions: the span since the last accepted sample is a plausible rate,
                // AND this arrival itself came on cadence. Without the second, a span that ends
                // inside a backlog (2.25 s over 88 ticks, live) passes the band and still reads
                // a rate 50 % slow.
                if (sane && (!_hasMeasurement || (InBand(perTick) && _lastArrivalOnCadence)))
                {
                    // The first real measurement replaces the seed outright: the seed is a
                    // guess from a nominal rate and a tick gap that may not have been
                    // confirmed yet, and letting an EMA crawl away from a wrong guess
                    // means rendering at a wrong rate for several seconds after every
                    // join. After that, smooth.
                    SecondsPerTick = _hasMeasurement
                        ? SecondsPerTick * (1.0 - config.IntervalSmoothing) + perTick * config.IntervalSmoothing
                        : perTick;
                    _hasMeasurement = true;
                    ClampInterval(config);
                    _anchorTick = tick;
                    _anchorTime = receiveTimeSeconds;
                    _inRejectStreak = false;
                }
                else if (_hasMeasurement)
                {
                    // A stall gap or a backlog: not a rate. The anchor stays, so the span is
                    // measured whole once the stream is back on its cadence.
                    RejectedIntervalSamples++;
                    if (!_inRejectStreak)
                    {
                        _inRejectStreak = true;
                        _streakTick = tick;
                        _streakTime = receiveTimeSeconds;
                    }
                    else if (receiveTimeSeconds - _streakTime >= RelearnSeconds && tick > _streakTick)
                    {
                        // Still outside the band after RelearnSeconds: the rate really changed.
                        // Measured from the first rejected arrival, so the gap that started the
                        // streak is not part of it.
                        double relearned = (receiveTimeSeconds - _streakTime) / (tick - _streakTick);
                        if (relearned >= config.MinInterval && relearned <= config.MaxInterval)
                        {
                            SecondsPerTick = relearned;
                            _anchorTick = tick;
                            _anchorTime = receiveTimeSeconds;
                            _inRejectStreak = false;
                        }
                    }
                }
            }

            NewestTick = tick;
            _lastReceiveTime = receiveTimeSeconds;
        }

        /// <summary>
        /// Whether the newest arrival came on the stream's cadence: its arrival gap over its tick
        /// gap inside the band (<see cref="MinSampleRatio"/>). False for the first snapshot after
        /// a stall and for every snapshot of the backlog behind it.
        /// </summary>
        public bool LastArrivalOnCadence => _lastArrivalOnCadence;

        private bool OnCadence(long tickDelta, double secondsDelta)
        {
            if (tickDelta <= 0 || secondsDelta <= 0.0 || SecondsPerTick <= 0.0)
            {
                return false;
            }

            return InBand(secondsDelta / tickDelta);
        }

        private bool InBand(double perTick) =>
            perTick >= SecondsPerTick * MinSampleRatio && perTick <= SecondsPerTick * MaxSampleRatio;

        /// <summary>
        /// Advances the render clock by one frame. Call once per rendered frame, before
        /// evaluating any entity.
        /// </summary>
        /// <param name="deltaSeconds">Real seconds since the previous call. Non-positive is ignored.</param>
        /// <param name="config">Tuning.</param>
        public void Advance(double deltaSeconds, in InterpolationConfig config)
        {
            if (!HasSamples || deltaSeconds <= 0.0 || SecondsPerTick <= 0.0)
            {
                return;
            }

            _sinceArrival += deltaSeconds;
            double target = TargetTick(config);
            double errorSeconds = (target - RenderTick) * SecondsPerTick;

            // BEHIND BY A STALL, AND THE STREAM IS BACK: set forward, once. Only after an episode
            // of arrivals off cadence, and only when the stream is back on it, so a backlog still
            // being delivered does not drag the clock through it in several jumps, and a clock
            // that is merely behind (no stall) is dilated within the cap as it always was.
            if (_recovering && _lastArrivalOnCadence)
            {
                _recovering = false;
                if (errorSeconds > ResyncBehindSeconds)
                {
                    RenderTick = target;
                    ClockResyncs++;
                    return;
                }
            }

            double adjust = errorSeconds * config.ClockCatchUpRate;
            if (adjust > config.MaxClockRateAdjust) adjust = config.MaxClockRateAdjust;
            if (adjust < -config.MaxClockRateAdjust) adjust = -config.MaxClockRateAdjust;

            double rate = 1.0 + adjust;

            // AHEAD OF EVERYTHING RECEIVED, WHILE SNAPSHOTS KEEP ARRIVING ON CADENCE: hold. Past
            // the extrapolation cap and its hold every entity is already drawn at its newest
            // sample, so holding the clock changes nothing on screen; it lets the newest tick
            // catch up at the server's rate instead of the 10 % cap's. Not while the stream is
            // silent (a server stall): there the clock runs on, as it always has, so the
            // snapshots after the stall do not find it behind.
            double aheadSeconds = (RenderTick - NewestTick) * SecondsPerTick;
            if (_lastArrivalOnCadence
                && _sinceArrival < config.MaxExtrapolationSpan
                && aheadSeconds > config.MaxExtrapolation + config.ExtrapolationHold)
            {
                rate = 0.0;
                AheadHoldFrames++;
            }

            // Floored above zero unconditionally, whatever the config says. This is the
            // monotonicity invariant the continuity tests rest on: a clock that can stop
            // can stall the render, and a clock that can reverse can pop it. A
            // misconfigured MaxClockRateAdjust must not be able to reintroduce either.
            if (rate < 0.01) rate = 0.01;

            RenderTick += deltaSeconds / SecondsPerTick * rate;
        }

        /// <summary>Forgets everything. For a fresh session.</summary>
        public void Reset()
        {
            HasSamples = false;
            NewestTick = 0;
            SecondsPerTick = 0.0;
            RenderTick = 0.0;
            _lastReceiveTime = 0.0;
            _hasMeasurement = false;
            _anchorTick = 0;
            _anchorTime = 0.0;
            _inRejectStreak = false;
            _streakTick = 0;
            _streakTime = 0.0;
            _lastArrivalOnCadence = false;
            _sinceArrival = 0.0;
            _recovering = false;
        }

        private void ClampInterval(in InterpolationConfig config)
        {
            if (SecondsPerTick < config.MinInterval) SecondsPerTick = config.MinInterval;
            if (SecondsPerTick > config.MaxInterval) SecondsPerTick = config.MaxInterval;
        }
    }
}
