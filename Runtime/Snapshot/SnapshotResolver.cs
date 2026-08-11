using System.Collections.Generic;
using Cuvara.Netcode.Protocol.Messages;

namespace Cuvara.Netcode.Snapshot
{
    /// <summary>
    /// Turns a wire snapshot into a <see cref="ResolvedSnapshot"/> by resolving
    /// interned entity handles, and reports when it cannot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole of the client's interning implementation. It stores no
    /// world state: reconstructing the world from keyframes and deltas belongs to
    /// <c>Shared.GameLogic.Systems.SnapshotMerger</c>, which is the code the server
    /// diffed against.
    /// </para>
    /// <para>
    /// <b>An unresolvable handle aborts the whole snapshot.</b> Not the entity — the
    /// snapshot. A partially applied one looks like valid state, and guessing (or
    /// skipping the entity, or falling back to the last one seen) produces wrong
    /// state attributed to the wrong entity, which renders as a real entity in the
    /// wrong place and nothing detects it. Absent state is loud and recoverable; the
    /// caller asks for a keyframe with <c>resync</c>.
    /// </para>
    /// </remarks>
    public sealed class SnapshotResolver
    {
        private readonly EntityHandleTable _handles = new EntityHandleTable();

        /// <summary>Number of snapshots that could not be resolved and forced a resync.</summary>
        public int UnresolvedCount { get; private set; }

        /// <summary>Forgets every binding, for a fresh connection or after a map transfer.</summary>
        public void Reset()
        {
            _handles.Clear();
            UnresolvedCount = 0;
        }

        /// <summary>
        /// Resolves one snapshot. Returns false when a handle has no binding; the
        /// caller must then send <c>resync</c> and apply the keyframe instead. No
        /// new binding is recorded on a failed resolve, so a retry sees the same
        /// table — except on a keyframe, which clears it up front and is in any case
        /// about to be replaced by the keyframe the resync asks for.
        /// </summary>
        public bool TryResolve(SnapshotMessage snapshot, out ResolvedSnapshot resolved)
        {
            resolved = default;
            if (snapshot == null)
            {
                return false;
            }

            // A keyframe re-introduces every binding, so the previous interval's
            // table is cleared BEFORE resolving rather than after. Resolving a
            // keyframe against stale bindings is the one way a handle can resolve to
            // the wrong entity, and the wrong entity is worse than no entity.
            if (snapshot.Full)
            {
                _handles.Clear();
            }

            var entities = new List<ResolvedEntity>(snapshot.Entities.Count);
            var pending = new List<KeyValuePair<uint, string>>();

            foreach (var e in snapshot.Entities)
            {
                var id = e.Id;

                if (e.Handle != 0)
                {
                    if (string.IsNullOrEmpty(id))
                    {
                        if (!_handles.TryResolve(e.Handle, out id))
                        {
                            UnresolvedCount++;
                            return false;
                        }
                    }
                    else
                    {
                        // This message introduces the binding. Recorded only once the
                        // whole snapshot has resolved, so an abort leaves nothing
                        // half-bound.
                        pending.Add(new KeyValuePair<uint, string>(e.Handle, id));
                    }
                }

                if (string.IsNullOrEmpty(id))
                {
                    // Neither an id nor a handle: nothing identifies this entity, so
                    // the snapshot is unusable for the same reason an unknown handle
                    // is.
                    UnresolvedCount++;
                    return false;
                }

                entities.Add(new ResolvedEntity(id, e.Type, e.X, e.Y, e.Hp, e.MaxHp));
            }

            foreach (var binding in pending)
            {
                _handles.Bind(binding.Key, binding.Value);
            }

            resolved = new ResolvedSnapshot(
                snapshot.Tick,
                snapshot.AckTick,
                snapshot.Full,
                entities,
                snapshot.Removed);

            return true;
        }
    }
}
