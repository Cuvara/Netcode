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

        // Whether any snapshot has been merged since construction or the last Reset. See Apply.
        private bool _hasMerged;

        /// <summary>Server tick of the newest snapshot merged. Never moves backwards.</summary>
        public long Tick => (long)_merger.Tick;

        /// <summary>
        /// Newest input tick the server accepted for this player. Monotonic, and
        /// zero until the first input is accepted — the anchor a prediction layer
        /// rewinds to.
        /// </summary>
        public long AckTick => (long)_merger.AckTick;

        /// <summary>
        /// Server base tick on which the input <see cref="AckTick"/> names was applied
        /// (<c>ack_applied_tick</c>). Zero means "not known": a protocol 2 or older server, or
        /// an acknowledgement that arrived without it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Always the partner of <see cref="AckTick"/>, never of some other one.</b> It is
        /// tracked here rather than in <c>SnapshotMerger</c> because that merger is
        /// <c>Shared.GameLogic</c> and carries no such field; the rule is the merger's own
        /// monotonic ack, extended to the pair: a snapshot whose ack is the newest one sets
        /// the applied tick it carries (zero included, when the ack moved), and a snapshot
        /// carrying an older ack changes nothing.
        /// </para>
        /// <para>
        /// A snapshot that repeats the current ack with a zero applied tick (a server that
        /// reattached the entity and has not applied a new input since) keeps the known value:
        /// the pair still describes the same input, and zero there means "not sent", not
        /// "changed".
        /// </para>
        /// </remarks>
        public long AckAppliedTick { get; private set; }

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
            // NOT NEWER THAN WHAT IS ALREADY MERGED: dropped whole. The shared merger keeps Tick
            // and AckTick monotonic but merges the entities of any snapshot it is given, so a late
            // one would put its stale states back over newer ones -- and an entity that has
            // stopped since is then omitted by every delta until the next keyframe, drawn at the
            // stale position for up to two seconds (SnapshotFreshnessTests). The wire is ordered
            // (KCP), so this is not expected to fire; it is counted so that it is visible if it
            // does.
            //
            // EQUAL counts as not newer, once a snapshot has been merged. The server stages at
            // most one snapshot per connection per world tick and encodes each staging once:
            // keyframe-or-delta is decided inside that one encode, a resync only promotes the
            // NEXT staging (a later tick) to a keyframe, and coalescing replaces an unsent
            // staging with the newer tick's. So a second snapshot carrying the tick already
            // merged is a repeat, never new state, and merging it could only put older entity
            // states back. A snapshot the resolver aborted (unknown handle) never reached this
            // method, so the keyframe answering its resync is still newer than Tick and applies.
            // The flag, not Tick > 0, decides "has merged": a fresh or reset world accepts a
            // first snapshot at tick 0.
            //
            // A new session starts from Reset(), which returns Tick to zero, so a server whose
            // ticks restart lower is accepted from its first snapshot. NetworkClient resets this
            // world at the start of every connect and reconnect. A caller that feeds a new
            // session into an un-reset world will see it dropped here and counted.
            if (snapshot.Tick < Tick || (snapshot.Tick == Tick && _hasMerged))
            {
                StaleSnapshotsDropped++;
                return;
            }

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
                    var core = new EntitySnapshotData(
                        e.Id, e.Type, e.X, e.Y, e.Hp, e.MaxHp, e.Speed, e.FacingBrad, e.Action,
                        actionSeq: e.ActionSeq, changedFields: e.ChangedFields);

                    // Protocol version 3 fields ride through the shared type's own v3
                    // constructor -- the one whose first parameter is a whole core snapshot, so
                    // no positional overload can capture this call the way the ten-argument one
                    // captured actionSeq above. SnapshotMerger owns what a delta means for them
                    // (keep-last-known per mask bit, stats/statuses upserted by id), exactly as it
                    // does for the protocol 2 fields. A version 2 entity has every one at its
                    // "not sent" value and takes the plain core unchanged.
                    converted[i] = e.HasVersion3Fields
                        ? new EntitySnapshotData(
                            in core,
                            e.Z, e.VelX, e.VelY, e.VelZ,
                            e.OwnerId, e.SpawnSeq,
                            e.Stats, e.StatsRemoved,
                            e.Statuses, e.StatusesRemoved)
                        : core;
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
            if (_removedSinceTake != null && removed != null)
            {
                for (var i = 0; i < removed.Length; i++)
                {
                    if (!string.IsNullOrEmpty(removed[i])) _removedSinceTake.Add(removed[i]);
                }
            }

            long ackBefore = AckTick;

            _hasMerged = true;
            _merger.Apply(new SnapshotData(
                (ulong)snapshot.Tick,
                (ulong)snapshot.AckTick,
                snapshot.Full,
                converted,
                removed));

            // The pair moves together, under the merger's own monotonic rule. See AckAppliedTick.
            if (snapshot.AckTick > ackBefore)
            {
                AckAppliedTick = snapshot.AckAppliedTick;
            }
            else if (snapshot.AckTick == ackBefore && snapshot.AckTick != 0L && snapshot.AckAppliedTick != 0L)
            {
                AckAppliedTick = snapshot.AckAppliedTick;
            }
        }

        /// <summary>
        /// Snapshots dropped because their tick was not above <see cref="Tick"/>: older, or a
        /// repeat of the tick already merged. Zero on an ordered transport. Nonzero means either a reordering transport, or a new session fed into this
        /// world without <see cref="Reset"/> (every one of its snapshots is then dropped until its
        /// ticks pass the old session's).
        /// </summary>
        public int StaleSnapshotsDropped { get; private set; }

        /// <summary>Drop all reconstructed state — a new join or a map transfer.</summary>
        public void Reset()
        {
            _merger.Reset();
            _hasMerged = false;
            AckAppliedTick = 0L;
            _removedSinceTake?.Clear();
        }

        // Ids named in `removed` since the last TakeRemovedIds; null until a consumer asks for them.
        private HashSet<string> _removedSinceTake;

        /// <summary>
        /// Moves every id that a snapshot named as removed since the last call into
        /// <paramref name="into"/>, which is cleared first. The first call turns the tracking on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why.</b> A consumer that polls the merged world once per frame sees an id that was
        /// removed and added again between two polls as one entity that never left. Its view entry,
        /// interpolation samples included, then carries over from the old incarnation. That happens
        /// whenever several snapshots merge in one frame, and a backlog read in one frame after a
        /// client freeze does exactly that. Live, entities re-entering the area of interest were
        /// first drawn 0.27 u from where they had left it, 4 u from where they really were
        /// (rpg-factory testlab S09 soak 20261023T040945Z-S09-e26z, entity_lifecycle, all at the
        /// freeze-resume frames). <see cref="View.WorldViewBinder"/> calls this every frame.
        /// </para>
        /// <para>
        /// Off until first called, so a world nobody drains does not accumulate ids.
        /// </para>
        /// </remarks>
        public void TakeRemovedIds(List<string> into)
        {
            into.Clear();
            if (_removedSinceTake == null)
            {
                _removedSinceTake = new HashSet<string>();
                return;
            }

            foreach (var id in _removedSinceTake) into.Add(id);
            _removedSinceTake.Clear();
        }
    }
}
