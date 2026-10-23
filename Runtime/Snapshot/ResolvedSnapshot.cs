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
            : this(tick, ackTick, full, entities, removed, NoEvents)
        {
        }

        public ResolvedSnapshot(long tick, long ackTick, bool full,
            IReadOnlyList<ResolvedEntity> entities, IReadOnlyList<string> removed,
            IReadOnlyList<ResolvedGameEvent> events)
            : this(tick, ackTick, full, entities, removed, events, 0L)
        {
        }

        /// <summary>
        /// The full form: as above, plus <paramref name="ackAppliedTick"/> -- the server tick that
        /// applied the input <paramref name="ackTick"/> names (wire field 7; 0 = not sent).
        /// </summary>
        public ResolvedSnapshot(long tick, long ackTick, bool full,
            IReadOnlyList<ResolvedEntity> entities, IReadOnlyList<string> removed,
            IReadOnlyList<ResolvedGameEvent> events, long ackAppliedTick)
        {
            Tick = tick;
            AckTick = ackTick;
            AckAppliedTick = ackAppliedTick;
            Full = full;
            Entities = entities;
            Removed = removed;
            Events = events ?? NoEvents;
        }

        private static readonly ResolvedGameEvent[] NoEvents = new ResolvedGameEvent[0];

        /// <summary>Server simulation tick this snapshot describes.</summary>
        public long Tick { get; }

        /// <summary>
        /// Newest input tick the server accepted for this player. Surfaced, never
        /// consumed here: it is the reconciliation anchor for the prediction layer.
        /// Zero means "no input accepted yet".
        /// </summary>
        public long AckTick { get; }

        /// <summary>
        /// Server base tick on which the input <see cref="AckTick"/> names was applied
        /// (<c>ack_applied_tick</c>). Zero means "not sent" (protocol 2 or an older server).
        /// </summary>
        /// <remarks>
        /// <see cref="AckTick"/> counts on the client's tick line, <see cref="Tick"/> on the
        /// server's; this joins them. A predictor compares this snapshot with its history at
        /// <c>Tick + (client base tick of the acked input - AckAppliedTick)</c> rather than at
        /// <c>Tick</c>. Only meaningful together with the <see cref="AckTick"/> of the SAME
        /// snapshot.
        /// </remarks>
        public long AckAppliedTick { get; }

        /// <summary>
        /// Keyframe marker. When true, <see cref="Entities"/> is the complete AOI
        /// set and everything not listed must be discarded.
        /// </summary>
        public bool Full { get; }

        public IReadOnlyList<ResolvedEntity> Entities { get; }

        /// <summary>Entity ids that left the AOI or the world. Deltas only.</summary>
        public IReadOnlyList<string> Removed { get; }

        /// <summary>
        /// Edge-triggered occurrences this tick produced, participants already resolved to
        /// entity ids. Empty on most snapshots, and never null.
        /// </summary>
        /// <remarks>
        /// <b>Consume these once.</b> They are not state and they are not re-sent: a keyframe
        /// restates the world, not its history, so an event replayed by a consumer that
        /// re-reads an old snapshot would show a player a hit that happened twice.
        /// </remarks>
        public IReadOnlyList<ResolvedGameEvent> Events { get; }
    }
}
