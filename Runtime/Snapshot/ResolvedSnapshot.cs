using System.Collections.Generic;

namespace Cuvara.Netcode.Snapshot
{
    /// <summary>
    /// A snapshot with every entity id resolved, ready to be merged into world
    /// state.
    /// </summary>
    /// <remarks>
    /// The merge itself is deliberately not done here. It is
    /// <c>Shared.GameLogic.Systems.SnapshotMerger</c>'s job — the same code the
    /// server was diffed against — and a second copy in the client is exactly the
    /// divergence the shared-logic boundary exists to prevent (ADR-10).
    /// </remarks>
    public readonly struct ResolvedSnapshot
    {
        public ResolvedSnapshot(long tick, long ackTick, bool full,
            IReadOnlyList<ResolvedEntity> entities, IReadOnlyList<string> removed)
        {
            Tick = tick;
            AckTick = ackTick;
            Full = full;
            Entities = entities;
            Removed = removed;
        }

        /// <summary>Server simulation tick this snapshot describes.</summary>
        public long Tick { get; }

        /// <summary>
        /// Newest input tick the server accepted for this player. Surfaced, never
        /// consumed here: it is the reconciliation anchor for the prediction layer.
        /// Zero means "no input accepted yet".
        /// </summary>
        public long AckTick { get; }

        /// <summary>
        /// Keyframe marker. When true, <see cref="Entities"/> is the complete AOI
        /// set and everything not listed must be discarded.
        /// </summary>
        public bool Full { get; }

        public IReadOnlyList<ResolvedEntity> Entities { get; }

        /// <summary>Entity ids that left the AOI or the world. Deltas only.</summary>
        public IReadOnlyList<string> Removed { get; }
    }
}
