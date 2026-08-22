using Cuvara.Netcode.Interpolation;

namespace Cuvara.Netcode.View
{
    /// <summary>
    /// One remote entity's retained samples on the GameObject path: a fixed-capacity ring
    /// over a single array, allocated once when the entity is first presented and pooled
    /// for reuse when it despawns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Fixed capacity, never grown, never reallocated.</b> The array is sized at
    /// construction from <see cref="InterpolationConfig.RingCapacity"/> and the oldest
    /// sample is overwritten when it is full, so presenting an entity for an hour costs
    /// the same as presenting it for a frame. Nothing on the per-frame path touches the
    /// heap.
    /// </para>
    /// <para>
    /// <b>The ECS path will not use this class</b> — it stores the identical
    /// <see cref="InterpolationSample"/> in a <c>DynamicBuffer</c> in chunk memory. Both
    /// obey the same admission rule via <see cref="InterpolationRing"/> and both are read
    /// through <see cref="ISampleBuffer"/>, so the shared evaluator sees no difference.
    /// </para>
    /// </remarks>
    public sealed class EntitySampleRing
    {
        private readonly InterpolationSample[] _samples;
        private int _start;
        private int _count;

        /// <param name="capacity">Samples retained. Clamped to at least two.</param>
        public EntitySampleRing(int capacity)
        {
            _samples = new InterpolationSample[capacity < 2 ? 2 : capacity];
        }

        /// <summary>Samples held.</summary>
        public int Length => _count;

        /// <summary>Newest tick held, or zero when empty.</summary>
        public long NewestTick => _count <= 0 ? 0L : this[_count - 1].Tick;

        /// <summary>Sample by age, <c>0</c> oldest.</summary>
        public InterpolationSample this[int index] =>
            _samples[InterpolationRing.Physical(_start, _samples.Length, index)];

        /// <summary>
        /// Appends a sample, evicting the oldest when full. Returns false for a tick that
        /// is not strictly newer than what is held — a duplicate or a reordered arrival,
        /// which the evaluator's bracketing must never see.
        /// </summary>
        public bool TryPush(in InterpolationSample sample)
        {
            if (!InterpolationRing.Accepts(_count, NewestTick, sample.Tick))
            {
                return false;
            }

            int slot = InterpolationRing.Claim(ref _start, ref _count, _samples.Length);
            _samples[slot] = sample;
            return true;
        }

        /// <summary>
        /// Empties the ring without releasing its array, so a pooled instance can be
        /// handed to a different entity.
        /// </summary>
        public void Clear()
        {
            _start = 0;
            _count = 0;
        }
    }

    /// <summary>
    /// Struct view of an <see cref="EntitySampleRing"/> for
    /// <see cref="SnapshotInterpolation.Evaluate{TBuffer}"/>.
    /// </summary>
    /// <remarks>
    /// A struct, and passed to a <c>where TBuffer : struct</c> generic, so the indexer
    /// calls are constrained calls rather than interface dispatch and the value is never
    /// boxed. Constructing one per entity per frame allocates nothing: it is a single
    /// reference-sized field that lives on the stack.
    /// </remarks>
    public readonly struct EntitySampleBuffer : ISampleBuffer
    {
        private readonly EntitySampleRing _ring;

        public EntitySampleBuffer(EntitySampleRing ring)
        {
            _ring = ring;
        }

        /// <inheritdoc />
        public int Length => _ring == null ? 0 : _ring.Length;

        /// <inheritdoc />
        public InterpolationSample this[int index] => _ring[index];
    }
}
