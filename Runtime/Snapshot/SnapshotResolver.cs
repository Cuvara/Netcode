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
        /// Resolves one snapshot. Returns false when a handle cannot be resolved; the
        /// caller must then send <c>resync</c> and apply the keyframe instead.
        /// </summary>
        /// <remarks>
        /// Nothing is mutated until every entity has resolved — not the handle table,
        /// not a single binding — so a rejected snapshot leaves the client exactly as it
        /// was rather than partially updated or newly empty. Two ways to fail: a delta
        /// naming a handle with no binding, and a keyframe carrying a bare handle, which
        /// is malformed because a keyframe introduces every binding it uses.
        /// </remarks>
        public bool TryResolve(SnapshotMessage snapshot, out ResolvedSnapshot resolved)
        {
            resolved = default;
            if (snapshot == null)
            {
                return false;
            }

            var entities = new List<ResolvedEntity>(snapshot.Entities.Count);
            List<KeyValuePair<uint, string>> pending = null;

            foreach (var e in snapshot.Entities)
            {
                var id = e.Id;

                if (e.Handle != 0)
                {
                    if (string.IsNullOrEmpty(id))
                    {
                        // A keyframe must introduce every binding it uses: the sender
                        // resets its handle space and re-sends each entity with both id
                        // and handle. A bare handle here is therefore malformed, and it
                        // is rejected WITHOUT consulting the table — the previous
                        // interval's binding for this number belongs to a different
                        // entity, so a successful lookup would be the dangerous outcome,
                        // not the safe one.
                        if (snapshot.Full)
                        {
                            UnresolvedCount++;
                            return false;
                        }

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
                        if (pending == null) pending = new List<KeyValuePair<uint, string>>();
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

                entities.Add(new ResolvedEntity(id, e.Type, e.X, e.Y, e.Hp, e.MaxHp, e.Speed));
            }

            // Every entity resolved, so state may now be mutated. The clear happens
            // here — after validation, before the new bindings land — so an aborted
            // snapshot leaves the table exactly as it was. Clearing up front would
            // wipe the table and then abort, leaving the client with no bindings and
            // an empty world until a resync completed.
            if (snapshot.Full)
            {
                _handles.Clear();
            }

            if (pending != null)
            {
                foreach (var binding in pending)
                {
                    _handles.Bind(binding.Key, binding.Value);
                }
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
