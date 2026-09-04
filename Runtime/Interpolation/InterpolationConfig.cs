namespace Cuvara.Netcode.Interpolation
{
    /// <summary>
    /// Every tuning number the interpolation core reads, in one blittable struct so it can
    /// be a Burst-readable singleton on the ECS path and a constructor argument on the
    /// GameObject path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a ScriptableObject, deliberately.</b> The ECS consumer is a
    /// <c>[BurstCompile]</c> job; a managed asset reference cannot be read from one. A
    /// struct can, and it keeps both paths configured by the same type rather than by two
    /// that drift.
    /// </para>
    /// <para>
    /// <b>Every field is a number with a stated reason, replacing magic constants.</b> The
    /// implementation this replaced hard-coded a <c>1.2</c> extrapolation cap and an
    /// <c>α = 0.3</c> smoothing factor in the middle of the algorithm, where neither could
    /// be measured, tested at its edges, or changed for one deployment without changing it
    /// for all of them.
    /// </para>
    /// </remarks>
    public struct InterpolationConfig
    {
        /// <summary>
        /// Seconds the rendered timeline is held behind the newest received tick — the
        /// jitter buffer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Default 0.100 s, about 1.5 snapshot intervals at the 15 Hz world rate.</b>
        /// One full interval is the minimum that can possibly work: below it there is
        /// frequently no newer sample to interpolate toward, so the renderer is forced to
        /// extrapolate. The extra half-interval is the jitter margin. It is sized against
        /// the measured RTT spread (8-13 ms, so a few ms of one-way variation) plus
        /// main-thread scheduling jitter on a 60 Hz client, which is itself around
        /// +/-16 ms; 33 ms absorbs both with room left.
        /// </para>
        /// <para>
        /// <b>This delay costs a remote player nothing a human can see, and it is not paid
        /// by the local avatar at all</b> — the local entity is excluded from
        /// interpolation entirely and renders at the newest received position or at the
        /// predicted one. What the delay buys is that ordinary jitter stops being visible
        /// as motion.
        /// </para>
        /// </remarks>
        public double TargetDelay;

        /// <summary>
        /// Seconds the renderer may carry motion past the newest sample before it holds
        /// still. Zero disables extrapolation.
        /// </summary>
        /// <remarks>
        /// Default 0.050 s, under one snapshot interval. With
        /// <see cref="TargetDelay"/> at 1.5 intervals, reaching the newest sample at all
        /// means the stream has been silent for longer than the jitter buffer was built
        /// for, so this is the tail of a real interruption rather than ordinary jitter.
        /// A small carry-over keeps a single dropped packet from reading as a freeze;
        /// going much further would be inventing motion the server never confirmed. It
        /// replaces the old <c>t &lt;= 1.2</c> clamp, which was 20 % of a segment expressed
        /// as a magic number in units nobody could reason about.
        /// </remarks>
        public double MaxExtrapolation;

        /// <summary>
        /// How hard the render clock is pulled toward its target: fractional rate change
        /// per second of clock error.
        /// </summary>
        /// <remarks>
        /// Default 1.0, i.e. 100 ms of error asks for a 10 % rate change. The clock is
        /// never snapped to its target, only run slightly fast or slightly slow until the
        /// error is gone — a snap is exactly the discontinuity this core exists to remove.
        /// </remarks>
        public double ClockCatchUpRate;

        /// <summary>
        /// Hard cap on the render clock's rate deviation, as a fraction of nominal.
        /// </summary>
        /// <remarks>
        /// Default 0.10. Time dilation of 10 % is below what a player can see on another
        /// avatar's motion, and capping it is what makes the rendered position provably
        /// monotonic: the rate is <c>1 + adjust</c> and it can never reach zero, so the
        /// rendered position can never stall and can never step backwards.
        /// </remarks>
        public double MaxClockRateAdjust;

        /// <summary>Lower sanity bound on a measured seconds-per-tick.</summary>
        public double MinInterval;

        /// <summary>Upper sanity bound on a measured seconds-per-tick.</summary>
        public double MaxInterval;

        /// <summary>
        /// Seconds per snapshot assumed before anything has been measured. Divided by the
        /// confirmed snapshot tick gap to seed seconds-per-tick.
        /// </summary>
        public double DefaultInterval;

        /// <summary>EMA weight given to each new seconds-per-tick measurement.</summary>
        public double IntervalSmoothing;

        /// <summary>
        /// Samples retained per entity. Default 8, about 0.53 s at 15 Hz.
        /// </summary>
        /// <remarks>
        /// Must exceed <see cref="TargetDelay"/> measured in intervals (1.5) by a wide
        /// margin, so a burst of snapshots batched by TCP is buffered rather than dropped.
        /// Eight is also the value that keeps a DOTS <c>DynamicBuffer</c> in chunk memory
        /// (8 x 24 B = 192 B) instead of heap-allocating per entity, which is the number
        /// stage 4 will declare as <c>InternalBufferCapacity</c>.
        /// </remarks>
        public int RingCapacity;

        /// <summary>The defaults every field above documents.</summary>
        public static InterpolationConfig Default => new InterpolationConfig
        {
            TargetDelay = 0.100,
            MaxExtrapolation = 0.050,
            ClockCatchUpRate = 1.0,
            MaxClockRateAdjust = 0.10,
            MinInterval = 0.001,
            MaxInterval = 0.500,
            DefaultInterval = 1.0 / 15.0,
            IntervalSmoothing = 0.3,
            RingCapacity = 8
        };

        /// <summary>
        /// This config with any non-positive field replaced by its default.
        /// </summary>
        /// <remarks>
        /// <c>default(InterpolationConfig)</c> is all zeroes, and a zero
        /// <see cref="DefaultInterval"/> or <see cref="RingCapacity"/> would divide by zero
        /// or buffer nothing. A struct has no constructor that can be made mandatory, so
        /// the guard has to live somewhere the consumer calls; both consumers call this
        /// once at construction. <see cref="MaxExtrapolation"/> is exempt because zero is a
        /// meaningful choice there — it means "never extrapolate".
        /// </remarks>
        public InterpolationConfig Normalized()
        {
            var d = Default;
            var c = this;
            if (c.TargetDelay <= 0.0) c.TargetDelay = d.TargetDelay;
            if (c.MaxExtrapolation < 0.0) c.MaxExtrapolation = d.MaxExtrapolation;
            if (c.ClockCatchUpRate <= 0.0) c.ClockCatchUpRate = d.ClockCatchUpRate;
            if (c.MaxClockRateAdjust <= 0.0) c.MaxClockRateAdjust = d.MaxClockRateAdjust;
            if (c.MinInterval <= 0.0) c.MinInterval = d.MinInterval;
            if (c.MaxInterval <= 0.0) c.MaxInterval = d.MaxInterval;
            if (c.MaxInterval < c.MinInterval) c.MaxInterval = d.MaxInterval;
            if (c.DefaultInterval <= 0.0) c.DefaultInterval = d.DefaultInterval;
            if (c.IntervalSmoothing <= 0.0 || c.IntervalSmoothing > 1.0) c.IntervalSmoothing = d.IntervalSmoothing;
            if (c.RingCapacity < 2) c.RingCapacity = d.RingCapacity;
            return c;
        }
    }
}
