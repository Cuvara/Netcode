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

        private double _lastReceiveTime;
        private bool _hasMeasurement;

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
                return;
            }

            long tickDelta = tick - NewestTick;
            double secondsDelta = receiveTimeSeconds - _lastReceiveTime;
            if (tickDelta > 0 && secondsDelta > 0.0)
            {
                double perTick = secondsDelta / tickDelta;
                if (perTick >= config.MinInterval && perTick <= config.MaxInterval)
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
                }
            }

            NewestTick = tick;
            _lastReceiveTime = receiveTimeSeconds;
        }

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

            double errorSeconds = (TargetTick(config) - RenderTick) * SecondsPerTick;
            double adjust = errorSeconds * config.ClockCatchUpRate;
            if (adjust > config.MaxClockRateAdjust) adjust = config.MaxClockRateAdjust;
            if (adjust < -config.MaxClockRateAdjust) adjust = -config.MaxClockRateAdjust;

            double rate = 1.0 + adjust;

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
        }

        private void ClampInterval(in InterpolationConfig config)
        {
            if (SecondsPerTick < config.MinInterval) SecondsPerTick = config.MinInterval;
            if (SecondsPerTick > config.MaxInterval) SecondsPerTick = config.MaxInterval;
        }
    }
}
