using System.Collections.Generic;
using Cuvara.Netcode.Protocol.Messages;
using Shared.GameLogic.Components;

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

        /// <summary>
        /// Event participants whose handle had no binding. Counted, never escalated: see
        /// <see cref="ResolvedGameEvent"/> for why an event does not force a resync the way
        /// an entity does.
        /// </summary>
        /// <remarks>
        /// A steadily climbing value here means the server is referring to entities it
        /// believes this client knows about and it does not — which is the same class of
        /// disagreement <see cref="UnresolvedCount"/> reports, seen from a channel that
        /// cannot afford to act on it. Worth an eye in a diagnostics overlay; not worth a
        /// keyframe.
        /// </remarks>
        public int UnresolvedEventParticipants { get; private set; }

        /// <summary>
        /// Protocol version 3 references to ANOTHER entity -- a projectile's <c>owner</c>, a
        /// status effect's <c>source</c> -- whose handle had no binding. Counted, never
        /// escalated, for the reason <see cref="UnresolvedEventParticipants"/> is: the entity
        /// carrying the reference is still correctly identified, and the reference resolves to
        /// null rather than to a guess.
        /// </summary>
        public int UnresolvedEntityReferences { get; private set; }

        /// <summary>Forgets every binding, for a fresh connection or after a map transfer.</summary>
        public void Reset()
        {
            _handles.Clear();
            UnresolvedCount = 0;
            UnresolvedEventParticipants = 0;
            UnresolvedEntityReferences = 0;
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

                entities.Add(new ResolvedEntity(
                    id, e.Type, e.X, e.Y, e.Hp, e.MaxHp, e.Speed, e.FacingBrad, e.Action,
                    actionSeq: e.ActionSeq, changedFields: e.ChangedFields));
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

            // Protocol version 3 references (owner, status source) resolve against the same
            // table, AFTER the bindings land for the same reason events do: a projectile is
            // routinely introduced in the same snapshot as its caster.
            ResolveVersion3Fields(snapshot, entities);

            // Resolved AFTER the bindings above have landed, which is load-bearing rather
            // than incidental: an event routinely names an entity introduced by THIS
            // snapshot (the thing that just spawned and immediately took damage), and
            // resolving events first would miss exactly those bindings and report them
            // unresolved. On a keyframe it matters twice over, because the table was cleared
            // a few lines up.
            IReadOnlyList<ResolvedGameEvent> events = ResolveEvents(snapshot);

            resolved = new ResolvedSnapshot(
                snapshot.Tick,
                snapshot.AckTick,
                snapshot.Full,
                entities,
                snapshot.Removed,
                events);

            return true;
        }

        private static readonly ResolvedGameEvent[] NoEvents = new ResolvedGameEvent[0];

        private IReadOnlyList<ResolvedGameEvent> ResolveEvents(SnapshotMessage snapshot)
        {
            // The overwhelmingly common case is a tick in which nothing happened. Returning
            // a shared empty array keeps that path free of an allocation per snapshot per
            // tick, which at 15 Hz is the difference between zero garbage and a steady drip.
            if (snapshot.Events.Count == 0)
            {
                return NoEvents;
            }

            var events = new List<ResolvedGameEvent>(snapshot.Events.Count);

            foreach (var e in snapshot.Events)
            {
                events.Add(new ResolvedGameEvent(
                    e.Type,
                    ResolveParticipant(e.Source, e.SourceId),
                    ResolveParticipant(e.Target, e.TargetId),
                    e.Amount,
                    e.AbilityId,
                    e.Flags,
                    e.EffectId));
            }

            return events;
        }

        /// <summary>
        /// Rebuilds every entity that carries protocol version 3 data with that data attached,
        /// its owner and status sources resolved to ids.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A version 2 entity -- every entity a version 2 server sends, and most of what a
        /// version 3 server sends on a delta -- is skipped without allocating, so the v2 path
        /// costs one branch per entity.
        /// </para>
        /// <para>
        /// The arrays are fresh per entity, never the decoded message's lists: the codec may
        /// reuse those for the next snapshot, and a resolved snapshot is published to
        /// subscribers this package does not control.
        /// </para>
        /// </remarks>
        private void ResolveVersion3Fields(SnapshotMessage snapshot, List<ResolvedEntity> entities)
        {
            for (var i = 0; i < entities.Count; i++)
            {
                var e = snapshot.Entities[i];
                if (!HasVersion3Fields(e))
                {
                    continue;
                }

                StatValueData[] stats = null;
                if (e.Stats.Count > 0)
                {
                    stats = new StatValueData[e.Stats.Count];
                    for (var s = 0; s < stats.Length; s++)
                    {
                        stats[s] = new StatValueData(e.Stats[s].StatId, e.Stats[s].Value);
                    }
                }

                StatusEffectData[] statuses = null;
                if (e.Statuses.Count > 0)
                {
                    statuses = new StatusEffectData[e.Statuses.Count];
                    for (var s = 0; s < statuses.Length; s++)
                    {
                        var status = e.Statuses[s];
                        var source = ResolveReference(status.Source, null);
                        statuses[s] = new StatusEffectData(
                            status.EffectId, status.Stacks, status.ExpiresTick, source);
                    }
                }

                var core = entities[i];
                entities[i] = new ResolvedEntity(
                    in core,
                    e.Z, e.VelX, e.VelY, e.VelZ,
                    ResolveReference(e.Owner, e.OwnerId),
                    e.SpawnSeq,
                    stats,
                    e.StatsRemoved.Count > 0 ? e.StatsRemoved.ToArray() : null,
                    statuses,
                    e.StatusesRemoved.Count > 0 ? e.StatusesRemoved.ToArray() : null);
            }
        }

        private static bool HasVersion3Fields(EntitySnapshot e) =>
            e.Z != 0f || e.VelX != 0f || e.VelY != 0f || e.VelZ != 0f
            || e.Owner != 0u || !string.IsNullOrEmpty(e.OwnerId) || e.SpawnSeq != 0u
            || e.Stats.Count > 0 || e.StatsRemoved.Count > 0
            || e.Statuses.Count > 0 || e.StatusesRemoved.Count > 0;

        /// <summary>
        /// A reference to another entity: handle first, explicit id as the JSON fallback, null
        /// for none. An unbound handle is counted in <see cref="UnresolvedEntityReferences"/> and
        /// reads as null -- never as a guess.
        /// </summary>
        private string ResolveReference(uint handle, string explicitId)
        {
            if (handle != 0)
            {
                if (_handles.TryResolve(handle, out var id))
                {
                    return id;
                }

                UnresolvedEntityReferences++;
                return null;
            }

            return string.IsNullOrEmpty(explicitId) ? null : explicitId;
        }

        /// <summary>
        /// Turns one event participant into an entity id: handle first, explicit id as the
        /// fallback, empty when neither identifies anything.
        /// </summary>
        /// <remarks>
        /// The precedence matches <see cref="EntitySnapshot"/>'s own id/handle rule. A zero
        /// handle with an empty id is not a failure — it is how the server says "no such
        /// participant, or not one you can see" — so it is NOT counted as unresolved. Only a
        /// non-zero handle with no binding is.
        /// </remarks>
        private string ResolveParticipant(uint handle, string explicitId)
        {
            if (handle != 0)
            {
                if (_handles.TryResolve(handle, out var id))
                {
                    return id;
                }

                // A handle the table does not know. Reported as absent rather than guessed,
                // and counted so the disagreement is visible.
                UnresolvedEventParticipants++;
                return string.Empty;
            }

            // JSON, which never interns and names participants outright.
            return explicitId ?? string.Empty;
        }
    }
}
