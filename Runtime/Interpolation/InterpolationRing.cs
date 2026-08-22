namespace Cuvara.Netcode.Interpolation
{
    /// <summary>
    /// The ring-buffer index arithmetic and the admission rule, shared so the GameObject
    /// path's pooled array and the ECS path's <c>DynamicBuffer</c> cannot disagree about
    /// which samples are kept or in what order.
    /// </summary>
    /// <remarks>
    /// Small enough to look trivial, which is exactly why it is shared: the admission rule
    /// is load-bearing. <see cref="SnapshotInterpolation.EvaluateAt{TBuffer}"/> assumes
    /// strictly increasing ticks, and a duplicate or reordered sample slipped into the
    /// buffer would make the bracketing search pick a pair that spans no time and render
    /// the wrong endpoint, silently.
    /// </remarks>
    public static class InterpolationRing
    {
        /// <summary>
        /// Whether a sample may be appended: the buffer is empty, or the tick is strictly
        /// newer than everything in it.
        /// </summary>
        public static bool Accepts(int count, long newestTick, long tick)
        {
            return count <= 0 || tick > newestTick;
        }

        /// <summary>Physical slot of the logical index <paramref name="index"/> (0 = oldest).</summary>
        public static int Physical(int start, int capacity, int index)
        {
            return capacity <= 0 ? 0 : (start + index) % capacity;
        }

        /// <summary>
        /// Claims the slot a new sample should be written to, evicting the oldest when
        /// full, and updates <paramref name="start"/> and <paramref name="count"/>.
        /// </summary>
        public static int Claim(ref int start, ref int count, int capacity)
        {
            if (capacity <= 0)
            {
                return 0;
            }

            if (count < capacity)
            {
                int slot = (start + count) % capacity;
                count++;
                return slot;
            }

            int oldest = start;
            start = (start + 1) % capacity;
            return oldest;
        }
    }
}
