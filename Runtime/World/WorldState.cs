using System.Collections.Generic;
using Cuvara.Netcode.Snapshot;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace Cuvara.Netcode.World
{
    /// <summary>
    /// The client's reconstruction of authoritative world state, built by feeding
    /// resolved snapshots through <see cref="SnapshotMerger"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The merge rule itself is <b>not</b> written here. It is
    /// <c>Shared.GameLogic.Systems.SnapshotMerger</c> — the same code the server was
    /// diffed against — because a second copy of "keyframe replaces, delta upserts
    /// and removes" in the client is exactly the divergence the shared-logic
    /// boundary exists to prevent (backend ADR-10). This class is only the adapter
    /// between the wire-facing <see cref="ResolvedSnapshot"/> and the simulation
    /// type <see cref="SnapshotData"/> the shared code consumes.
    /// </para>
    /// <para>
    /// The split of responsibilities is deliberate: entity-handle interning is
    /// resolved upstream in <c>SnapshotResolver</c> (the shared merger keys by real
    /// entity id and knows nothing about handles), so everything reaching this
    /// class already carries real ids.
    /// </para>
    /// <para>
    /// Not thread-safe, matching the merger. Drive it from the thread that consumes
    /// the socket.
    /// </para>
    /// </remarks>
    public sealed class WorldState
    {
        private static readonly EntitySnapshotData[] NoEntities = new EntitySnapshotData[0];

        private readonly SnapshotMerger _merger = new SnapshotMerger();

        // Last snapshot's conversion buffers, reused only on an EXACT length match. See
        // Apply for why "exact" rather than "at least".
        private EntitySnapshotData[] _entityBuffer;
        private string[] _removedBuffer;

        /// <summary>Server tick of the newest snapshot merged. Never moves backwards.</summary>
        public long Tick => (long)_merger.Tick;

        /// <summary>
        /// Newest input tick the server accepted for this player. Monotonic, and
        /// zero until the first input is accepted — the anchor a prediction layer
        /// rewinds to.
        /// </summary>
        public long AckTick => (long)_merger.AckTick;

        /// <summary>Keyframes merged so far.</summary>
        public int Keyframes => _merger.Keyframes;

        /// <summary>Deltas merged so far.</summary>
        public int Deltas => _merger.Deltas;

        /// <summary>Entities currently visible, keyed by entity id.</summary>
        public IReadOnlyDictionary<string, EntitySnapshotData> Entities => _merger.Entities;

        /// <summary>
        /// <see cref="Entities"/> as its concrete type. Enumerating the interface boxes the
        /// dictionary's struct enumerator — one 88-byte allocation per foreach, measured —
        /// and <c>WorldViewBinder.Tick</c> paid it once per rendered frame, which at
        /// 300–1000 fps was the only per-frame allocation left in that path. Read-only by
        /// contract: only the merger writes it.
        /// </summary>
        public Dictionary<string, EntitySnapshotData> EntityMap => _merger.EntityMap;

        /// <summary>Number of entities currently visible.</summary>
        public int Count => _merger.Count;

        /// <summary>
        /// Entities carried by the most recently applied snapshot — <b>not</b> the number
        /// currently visible, which is <see cref="Count"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Exists because every other client-side counter measures <i>frames</i>. A snapshot
        /// carrying one entity and a snapshot carrying eight are otherwise indistinguishable:
        /// arrival rate, frame counts, resyncs, rejects and drops all read identically, so a
        /// client rendering one of eight replicated entities produces a perfectly healthy
        /// health line (#161).
        /// </para>
        /// <para>
        /// That cost a day on Cuvara/IndieRPGMMOAdventure#126, where "the wire is delivering
        /// nine" felt like an observation and was an <b>inference</b> — nothing measured it,
        /// and the investigation went down the client stack before the answer turned out to
        /// be positional and upstream of all of it.
        /// </para>
        /// <para>
        /// On a DELTA this is the number of entities that changed, which is normally far
        /// below <see cref="Count"/> and is not a fault. Read it against
        /// <see cref="LastAppliedWasKeyframe"/>: on a keyframe the two should agree.
        /// </para>
        /// </remarks>
        public int LastAppliedEntityCount { get; private set; }

        /// <summary>Despawns carried by the most recently applied snapshot.</summary>
        public int LastAppliedRemovedCount { get; private set; }

        /// <summary>
        /// True when the most recently applied snapshot was a keyframe, which is the only
        /// case in which <see cref="LastAppliedEntityCount"/> is comparable to
        /// <see cref="Count"/>.
        /// </summary>
        public bool LastAppliedWasKeyframe { get; private set; }

        /// <summary>
        /// Total entity records merged since this world was created, across all snapshots.
        /// </summary>
        /// <remarks>
        /// A total rather than only a rate, for the reason the health line already states
        /// about frames: a per-window rate is a difference of two counters over a measured
        /// interval and all three can be wrong, whereas a total divided by elapsed time
        /// cannot. When the two disagree, the bookkeeping is the suspect.
        /// </remarks>
        public long EntitiesApplied { get; private set; }

        /// <summary>Look up one reconstructed entity.</summary>
        public bool TryGet(string id, out EntitySnapshotData entity) => _merger.TryGet(id, out entity);

        /// <summary>
        /// Merge one resolved snapshot into world state.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The conversion buffers are reused across snapshots, but <b>only when the new
        /// length matches the old one exactly</b>. That restriction is the whole of the
        /// original objection to reuse and it still stands: <see cref="SnapshotData"/>
        /// carries an array and no count, and the merger iterates every element of it, so
        /// handing it a buffer longer than the snapshot would replay the tail — real
        /// entities, at last tick's positions, resurrected after a despawn. Growing a
        /// buffer and clearing the tail is no better: a zeroed
        /// <see cref="EntitySnapshotData"/> has a null id and the merger would key its
        /// dictionary on it.
        /// </para>
        /// <para>
        /// So the win is conditional on the entity count repeating, which on a settled AOI
        /// it usually does and on a churning one it does not. When it does not, this costs
        /// one extra reference store over always allocating.
        /// </para>
        /// <para>
        /// The buffers never escape: the merger copies each struct into its own dictionary
        /// inside <c>Apply</c> and retains no reference to the array, and nothing else in
        /// this class hands them out. That is what makes reuse safe here and not in
        /// <c>SnapshotResolver</c>, whose list is published to <c>SnapshotReceived</c>
        /// subscribers this package does not control.
        /// </para>
        /// </remarks>
        public void Apply(in ResolvedSnapshot snapshot)
        {
            var entities = snapshot.Entities;
            EntitySnapshotData[] converted;
            if (entities == null || entities.Count == 0)
            {
                converted = NoEntities;
            }
            else
            {
                if (_entityBuffer != null && _entityBuffer.Length == entities.Count)
                {
                    converted = _entityBuffer;
                }
                else
                {
                    converted = new EntitySnapshotData[entities.Count];
                    _entityBuffer = converted;
                }

                for (var i = 0; i < entities.Count; i++)
                {
                    var e = entities[i];
                    // Speed rides through to the merger so a prediction layer can read
                    // the server's actual value for an entity rather than assume the
                    // spawn default. Zero here means the server sent none — the
                    // fallback is the predictor's decision, not this adapter's.
                    // Facing and action ride through in their raw wire form for the same
                    // reason speed does: whether an absent value should hold the last
                    // known one or derive a new one is a presentation decision, and this
                    // adapter is not where presentation decisions belong.
                    // ActionSeq rides through for the same reason, and the consequence of
                    // dropping it here is the most invisible of the three: the entity would
                    // render correctly, carry the right action, and simply never animate a
                    // second swing — with the codec, the resolver and every test still green.
                    // actionSeq is passed BY NAME, and that is load-bearing rather than
                    // stylistic. Shared.GameLogic 0.5.0 added a ten-argument overload whose
                    // tenth parameter is `uint changedFields`, so this call written with ten
                    // positional arguments and a `uint` last bound to THAT overload: the
                    // retrigger counter landed in the field-delta mask and actionSeq was
                    // forced to 0. It compiled clean. The only thing that objected was
                    // GameEventAndActionSeqTests -- "decoded and resolved but dropped at the
                    // merge", expected 12, got 0.
                    //
                    // The second consequence was worse than the first: ChangedFields then
                    // held 12, a mask asserting Hp|MaxHp were the only fields present, so the
                    // next merge would have reconstructed the entity from a lie.
                    //
                    // changedFields now rides through from the wire (#158). Zero still means
                    // "every field present", so a keyframe, a pre-v2 server and any full
                    // entity all keep the behaviour they had before this line existed.
                    converted[i] = new EntitySnapshotData(
                        e.Id, e.Type, e.X, e.Y, e.Hp, e.MaxHp, e.Speed, e.FacingBrad, e.Action,
                        actionSeq: e.ActionSeq, changedFields: e.ChangedFields);
                }
            }

            LastAppliedEntityCount = converted.Length;
            LastAppliedWasKeyframe = snapshot.Full;
            EntitiesApplied += converted.Length;

            string[] removed = null;
            var removals = snapshot.Removed;
            if (removals != null && removals.Count > 0)
            {
                if (_removedBuffer != null && _removedBuffer.Length == removals.Count)
                {
                    removed = _removedBuffer;
                }
                else
                {
                    removed = new string[removals.Count];
                    _removedBuffer = removed;
                }

                for (var i = 0; i < removals.Count; i++)
                {
                    removed[i] = removals[i];
                }
            }

            LastAppliedRemovedCount = removed?.Length ?? 0;

            _merger.Apply(new SnapshotData(
                (ulong)snapshot.Tick,
                (ulong)snapshot.AckTick,
                snapshot.Full,
                converted,
                removed));
        }

        /// <summary>Drop all reconstructed state — a new join or a map transfer.</summary>
        public void Reset() => _merger.Reset();
    }
}
