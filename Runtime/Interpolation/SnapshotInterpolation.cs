using System;
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
        /// <summary>
        /// As <see cref="EvaluateAt{TBuffer}(in TBuffer, double, double, in InterpolationConfig, out float, out float)"/>,
        /// and additionally reports whether the answer is a <b>hold</b> rather than a
        /// position the entity was actually at.
        /// </summary>
        /// <param name="holding">
        /// True when the buffer cannot bracket the render instant — a single sample, or a
        /// render clock still behind the oldest one. The position returned is the oldest
        /// known state repeated, which is correct to render only if there is no better
        /// option; a caller that can defer showing the entity should prefer that, because a
        /// newly seen entity holds for <c>TargetDelay</c> before the render clock reaches
        /// its first sample, and a frozen body that then starts moving is the most visible
        /// artefact the jitter buffer produces.
        /// </param>
        public static bool EvaluateAt<TBuffer>(in TBuffer buffer, double renderTick, double secondsPerTick,
                                               in InterpolationConfig config, out float x, out float y,
                                               out bool holding)
            where TBuffer : struct, ISampleBuffer
        {
            int n = buffer.Length;
            holding = n > 0 && (n == 1 || renderTick <= buffer[0].Tick);
            return EvaluateAt(buffer, renderTick, secondsPerTick, config, out x, out y);
        }

        /// <summary>
        /// As <see cref="Evaluate{TBuffer}(in TBuffer, in InterpolationClock, in InterpolationConfig, out float, out float)"/>,
        /// reporting whether the answer is a hold. See the other overload's remarks.
        /// </summary>
        public static bool Evaluate<TBuffer>(in TBuffer buffer, in InterpolationClock clock,
                                             in InterpolationConfig config,
                                             out float x, out float y, out bool holding)
            where TBuffer : struct, ISampleBuffer
            => EvaluateAt(buffer, clock.RenderTick, clock.SecondsPerTick, config, out x, out y, out holding);

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
        /// <see cref="InterpolationConfig.MaxExtrapolation"/>; holds at the cap for
        /// <see cref="InterpolationConfig.ExtrapolationHold"/>; then eases back onto the newest
        /// sample over <see cref="InterpolationConfig.ExtrapolationSettle"/>.
        /// </summary>
        /// <remarks>
        /// Reaching here means the stream has been quiet for longer than
        /// <see cref="InterpolationConfig.TargetDelay"/>, so this is a real interruption
        /// rather than jitter. Note there is no correction to undo afterwards: the render
        /// clock is not reset by the arrival that ends the gap, it simply continues, so
        /// the extrapolated distance is absorbed by the next segment instead of being
        /// stepped back out of. That backward step is precisely what the old <c>t</c>
        /// clamp produced.
        /// <para>
        /// The settle exists because "quiet" also means "stopped": the binder pushes no sample
        /// for an entity whose position did not change, so an entity that stopped is past its
        /// newest sample permanently. Holding at the cap forever drew it permanently past where
        /// it stopped (StoppedEntityConvergesTests).
        /// </para>
        /// <para>
        /// <b>The settle moves the rendered entity backward</b>, along the segment it was carried
        /// on, onto the newest sample. That is a deliberate trade and not the backward step the
        /// note above rules out: it happens only after the stream has been quiet for the cap plus
        /// <see cref="InterpolationConfig.ExtrapolationHold"/> (200 ms by default), its distance
        /// is bounded by the cap (speed x 50 ms, 0.125 units for an enemy), and it is eased over
        /// <see cref="InterpolationConfig.ExtrapolationSettle"/> rather than stepped. The
        /// alternative is the entity drawn permanently past where the server says it stopped.
        /// An entity that resumes moving before the hold expires never settles.
        /// </para>
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

            if (config.MaxExtrapolationSpan > 0.0 && span * secondsPerTick > config.MaxExtrapolationSpan)
            {
                // Stale segment: the entity was still, then moved. No velocity to carry.
                return;
            }

            if (config.MaxExtrapolationSpeed > 0.0)
            {
                double dx = newest.X - previous.X;
                double dy = newest.Y - previous.Y;
                double speed = Math.Sqrt((dx * dx) + (dy * dy)) / (span * secondsPerTick);
                if (speed > config.MaxExtrapolationSpeed)
                {
                    // A teleport, not motion: there is no velocity to carry.
                    return;
                }
            }

            double overshoot = renderTick - newest.Tick;
            if (overshoot <= 0.0)
            {
                return;
            }

            double cap = config.MaxExtrapolation / secondsPerTick;
            double carried = Math.Min(overshoot, cap);
            double settleStart = cap + (config.ExtrapolationHold / secondsPerTick);
            if (overshoot > settleStart)
            {
                double settle = config.ExtrapolationSettle / secondsPerTick;
                double remaining = settle <= 0.0 ? 0.0 : 1.0 - ((overshoot - settleStart) / settle);
                if (remaining <= 0.0)
                {
                    return;
                }

                // Smoothstep: no velocity jump where the ease starts or where it lands.
                remaining = remaining * remaining * (3.0 - (2.0 * remaining));
                carried *= remaining;
            }

            double f = carried / span;
            x = (float)(newest.X + (newest.X - previous.X) * f);
            y = (float)(newest.Y + (newest.Y - previous.Y) * f);
        }
    }
}
