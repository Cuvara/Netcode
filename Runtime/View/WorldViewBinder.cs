using System.Collections.Generic;
using Cuvara.Netcode.World;

namespace Cuvara.Netcode.View
{
    /// <summary>
    /// Reconciles an <see cref="IEntityView"/> against <see cref="WorldState"/>: spawn what
    /// is new, despawn what is gone, update the rest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Polls rather than subscribing to snapshots, deliberately.</b> Three reasons:
    /// GameObject APIs are main-thread only and a poll driven from <c>Update</c> is
    /// main-thread by construction; <c>WorldState</c> is already the merged result, so a
    /// poll loses nothing a snapshot event would have carried; and reconciling against the
    /// whole world makes despawn fall out of absence, which is what makes an AOI exit and a
    /// true despawn behave identically here — as they should, because the wire does not
    /// distinguish them.
    /// </para>
    /// <para>
    /// The one thing a poll cannot see is WHY an entity left, so
    /// <see cref="NoteRemovedIds"/> exists for a caller that also subscribes to snapshots
    /// and wants the distinction logged. It is diagnostics only; the reconcile does not
    /// depend on it.
    /// </para>
    /// </remarks>
    public sealed class WorldViewBinder
    {
        private readonly IEntityView _view;
        private readonly HashSet<string> _live = new HashSet<string>();
        private readonly HashSet<string> _explicitlyRemoved = new HashSet<string>();
        private readonly List<string> _gone = new List<string>();

        public WorldViewBinder(IEntityView view)
        {
            _view = view;
        }

        /// <summary>Entities currently presented.</summary>
        public int LiveCount => _live.Count;

        /// <summary>Despawns attributable to an explicit <c>removed</c> id.</summary>
        public int DespawnsFromRemoval { get; private set; }

        /// <summary>Despawns where the entity simply stopped being listed.</summary>
        public int DespawnsFromAbsence { get; private set; }

        /// <summary>
        /// Records ids a snapshot named in <c>removed</c>, so the next reconcile can
        /// attribute their despawn. Optional — call from a <c>SnapshotReceived</c> handler.
        /// </summary>
        /// <remarks>
        /// <c>removed</c> carries entity IDs, never handles, so these are directly
        /// comparable with world keys.
        /// </remarks>
        public void NoteRemovedIds(IReadOnlyList<string> removed)
        {
            if (removed == null)
            {
                return;
            }

            for (var i = 0; i < removed.Count; i++)
            {
                if (!string.IsNullOrEmpty(removed[i]))
                {
                    _explicitlyRemoved.Add(removed[i]);
                }
            }
        }

        /// <summary>
        /// One reconcile pass. <paramref name="localId"/> is <c>NetworkClient.UserId</c>;
        /// the entity key IS the user id, so the local player needs no extra wire field.
        /// </summary>
        public void Tick(WorldState world, string localId)
        {
            if (world == null)
            {
                return;
            }

            foreach (var kv in world.Entities)
            {
                var id = kv.Key;
                if (string.IsNullOrEmpty(id))
                {
                    // An empty key would mean an unresolved interned handle reached the
                    // world, which SnapshotResolver is built to prevent. Skip rather than
                    // render a nameless entity.
                    continue;
                }

                if (_live.Add(id))
                {
                    _view.Spawn(id, id == localId);
                }

                var e = kv.Value;
                _view.SetState(id, e.X, e.Y, e.Hp, e.MaxHp);
            }

            // Anything we hold that the world no longer lists is gone. Collected first
            // because the view mutates the set.
            _gone.Clear();
            foreach (var id in _live)
            {
                if (!world.Entities.ContainsKey(id))
                {
                    _gone.Add(id);
                }
            }

            for (var i = 0; i < _gone.Count; i++)
            {
                var id = _gone[i];
                _live.Remove(id);
                _view.Despawn(id);

                if (_explicitlyRemoved.Remove(id))
                {
                    DespawnsFromRemoval++;
                }
                else
                {
                    // No `removed` id named it, so it left by ceasing to be listed —
                    // an AOI exit, or a hold that finally expired.
                    DespawnsFromAbsence++;
                }
            }
        }

        /// <summary>Forgets all state and clears the view. For a fresh session.</summary>
        public void Reset()
        {
            foreach (var id in _live)
            {
                _view.Despawn(id);
            }

            _live.Clear();
            _explicitlyRemoved.Clear();
            DespawnsFromRemoval = 0;
            DespawnsFromAbsence = 0;
        }
    }
}
