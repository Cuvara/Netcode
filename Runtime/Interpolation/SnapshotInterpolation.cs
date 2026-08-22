namespace Cuvara.Netcode.Interpolation
{
    /// <summary>
    /// The interpolation math, once, for every path that renders a remote entity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Pure and static on purpose.</b> It reads a buffer and a clock and returns a
    /// position; it owns no state, allocates nothing, and throws nothing. That is what
    /// lets the GameObject path call it from <c>Update</c> and lets stage 4 call the very
    /// same method from a <c>[BurstCompile]</c> <c>IJobEntity</c> without a second
    /// implementation existing to disagree with this one.
    /// </para>
    /// <para>
    /// <b>Bracketing is by tick, not by time since arrival.</b> Given a fractional
    /// <see cref="InterpolationClock.RenderTick"/>, it finds the two samples whose ticks
    /// straddle it and lerps by the tick fraction. A skipped server tick therefore
    /// interpolates across two ticks' worth of distance in two ticks' worth of time, at
    /// unchanged speed — where the arrival-interval approach it replaced divided a doubled
    /// distance by a barely-changed interval and rendered at over 1.5x speed before
    /// freezing.
    /// </para>
    /// </remarks>
    public static class SnapshotInterpolation
    {
        /// <summary>
        /// Position for the moment <paramref name="clock"/> is rendering. False when the
        /// buffer is empty and there is nothing honest to draw.
        /// </summary>
        public static bool Evaluate<TBuffer>(in TBuffer buffer, in InterpolationClock clock,
                                             in InterpolationConfig config, out float x, out float y)
            where TBuffer : struct, ISampleBuffer
        {
            return EvaluateAt(buffer, clock.RenderTick, clock.SecondsPerTick, config, out x, out y);
        }

        /// <summary>
        /// Position at an explicit render tick. The clock-free form, so the bracketing can
        /// be tested at its edges without constructing a timeline to reach them.
        /// </summary>
        /// <param name="buffer">Samples, oldest first, ticks strictly increasing.</param>
        /// <param name="renderTick">Fractional server tick to render.</param>
        /// <param name="secondsPerTick">
        /// Used only to convert <see cref="InterpolationConfig.MaxExtrapolation"/> from
        /// seconds into ticks. Non-positive disables extrapolation.
        /// </param>
        /// <param name="config">Tuning.</param>
        /// <param name="x">Rendered X.</param>
        /// <param name="y">Rendered Y.</param>
        public static bool EvaluateAt<TBuffer>(in TBuffer buffer, double renderTick, double secondsPerTick,
                                               in InterpolationConfig config, out float x, out float y)
            where TBuffer : struct, ISampleBuffer
        {
            x = 0f;
            y = 0f;

            int count = buffer.Length;
            if (count <= 0)
            {
                return false;
            }

            var oldest = buffer[0];
            if (count == 1 || renderTick <= oldest.Tick)
            {
                // Before the buffer, or only one sample in it. Hold at the oldest known
                // state rather than extrapolating backwards: this is the jitter buffer
                // still filling after a join or an area-of-interest entry, and the entity
                // has no history to have come from.
                x = oldest.X;
                y = oldest.Y;
                return true;
            }

            var newest = buffer[count - 1];
            if (renderTick >= newest.Tick)
            {
                ExtrapolatePastNewest(buffer, count, renderTick, secondsPerTick, config, out x, out y);
                return true;
            }

            // Newest first: the render tick is nearly always inside the last segment or
            // the one before it, so this exits after one or two iterations. The loop
            // exists for the batched-arrival case, where several snapshots land in one
            // read and the render clock is still working through the earlier ones.
            for (int i = count - 1; i > 0; i--)
            {
                var to = buffer[i];
                var from = buffer[i - 1];
                if (renderTick < from.Tick || renderTick > to.Tick)
                {
                    continue;
                }

                long span = to.Tick - from.Tick;
                if (span <= 0)
                {
                    x = to.X;
                    y = to.Y;
                    return true;
                }

                double f = (renderTick - from.Tick) / span;
                x = (float)(from.X + (to.X - from.X) * f);
                y = (float)(from.Y + (to.Y - from.Y) * f);
                return true;
            }

            // Unreachable while ticks are strictly increasing, which the ring enforces.
            // Rendering the newest state is the safe answer if it ever is reached: it is
            // the most recent thing the server actually said.
            x = newest.X;
            y = newest.Y;
            return true;
        }

        /// <summary>
        /// Carries the last segment's direction past the newest sample, capped by
        /// <see cref="InterpolationConfig.MaxExtrapolation"/>, then holds.
        /// </summary>
        /// <remarks>
        /// Reaching here means the stream has been quiet for longer than
        /// <see cref="InterpolationConfig.TargetDelay"/>, so this is a real interruption
        /// rather than jitter. Note there is no correction to undo afterwards: the render
        /// clock is not reset by the arrival that ends the gap, it simply continues, so
        /// the extrapolated distance is absorbed by the next segment instead of being
        /// stepped back out of. That backward step is precisely what the old <c>t</c>
        /// clamp produced.
        /// </remarks>
        private static void ExtrapolatePastNewest<TBuffer>(in TBuffer buffer, int count, double renderTick,
                                                           double secondsPerTick, in InterpolationConfig config,
                                                           out float x, out float y)
            where TBuffer : struct, ISampleBuffer
        {
            var newest = buffer[count - 1];
            x = newest.X;
            y = newest.Y;

            if (config.MaxExtrapolation <= 0.0 || secondsPerTick <= 0.0 || count < 2)
            {
                return;
            }

            var previous = buffer[count - 2];
            long span = newest.Tick - previous.Tick;
            if (span <= 0)
            {
                return;
            }

            double overshoot = renderTick - newest.Tick;
            double cap = config.MaxExtrapolation / secondsPerTick;
            if (overshoot > cap) overshoot = cap;
            if (overshoot <= 0.0)
            {
                return;
            }

            double f = overshoot / span;
            x = (float)(newest.X + (newest.X - previous.X) * f);
            y = (float)(newest.Y + (newest.Y - previous.Y) * f);
        }
    }
}
