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

        /// <summary>Look up one reconstructed entity.</summary>
        public bool TryGet(string id, out EntitySnapshotData entity) => _merger.TryGet(id, out entity);

        /// <summary>
        /// Merge one resolved snapshot into world state.
        /// </summary>
        /// <remarks>
        /// Allocates one array per snapshot rather than reusing a buffer:
        /// <see cref="SnapshotData"/> stores the array it is handed and the merger
        /// iterates all of it, so a longer shared buffer would replay stale entries.
        /// At the default 15 Hz this is a handful of short-lived arrays per second.
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
                converted = new EntitySnapshotData[entities.Count];
                for (var i = 0; i < entities.Count; i++)
                {
                    var e = entities[i];
                    // Speed rides through to the merger so a prediction layer can read
                    // the server's actual value for an entity rather than assume the
                    // spawn default. Zero here means the server sent none — the
                    // fallback is the predictor's decision, not this adapter's.
                    converted[i] = new EntitySnapshotData(e.Id, e.Type, e.X, e.Y, e.Hp, e.MaxHp, e.Speed);
                }
            }

            string[] removed = null;
            var removals = snapshot.Removed;
            if (removals != null && removals.Count > 0)
            {
                removed = new string[removals.Count];
                for (var i = 0; i < removals.Count; i++)
                {
                    removed[i] = removals[i];
                }
            }

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
