using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <para>
    /// <b>Interpolation</b>: entity positions are interpolated between the two most
    /// recent snapshot states so movement appears smooth at render frame rate instead
    /// of teleporting every server tick (~66ms at 15 Hz). The interpolation factor is
    /// derived from wall-clock time elapsed since the last snapshot, divided by the
    /// measured snapshot interval. HP values are not interpolated — they snap to the
    /// latest value.
    /// </para>
    /// <para>
    /// <b>The local entity is excluded from interpolation.</b> Smoothing between two
    /// past snapshots means rendering the world as it was one snapshot interval ago —
    /// worth it for entities whose next position this client cannot know, and pure
    /// added latency for the one entity it drives. The local id is snapped to its
    /// authoritative position instead. Which id that is comes from the
    /// <c>localId</c> argument to <see cref="Tick"/>; pass an empty string and nothing
    /// is treated as local, which is the pre-existing behaviour.
    /// </para>
    /// </remarks>
    public sealed class WorldViewBinder
    {
        /// <summary>Per-entity interpolation state: two most recent snapshot positions.</summary>
        private struct InterpEntry
        {
            public float FromX, FromY;
            public float ToX, ToY;
            public int Hp, MaxHp;
            public bool HasFrom;
        }

        private readonly IEntityView _view;
        private readonly HashSet<string> _live = new HashSet<string>();
        private readonly HashSet<string> _explicitlyRemoved = new HashSet<string>();
        private readonly List<string> _gone = new List<string>();

        private readonly Dictionary<string, InterpEntry> _interp = new Dictionary<string, InterpEntry>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private string _localId = string.Empty;
        private long _lastWorldTick;
        private double _lastSnapshotTimeMs;
        private double _snapshotIntervalMs = 1000.0 / 15.0; // default 15 Hz, refined from actual arrivals
        private bool _firstSnapshot = true;

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
        /// Entities re-spawned because the local player's id changed under them. Nonzero
        /// means a session boundary was crossed without <see cref="Reset"/>; see
        /// <see cref="Tick"/>.
        /// </summary>
        public int Relocalizations { get; private set; }

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

            RelocalizeIfLocalIdChanged(localId);

            double nowMs = _clock.Elapsed.TotalMilliseconds;
            bool newSnapshot = world.Tick > _lastWorldTick;

            // Measure actual snapshot interval from wall-clock arrivals
            if (newSnapshot)
            {
                if (!_firstSnapshot)
                {
                    double measured = nowMs - _lastSnapshotTimeMs;
                    // Exponential moving average (α = 0.3) to smooth jitter
                    if (measured > 1.0 && measured < 500.0) // sanity bounds
                    {
                        _snapshotIntervalMs = _snapshotIntervalMs * 0.7 + measured * 0.3;
                    }
                }
                _firstSnapshot = false;
                _lastSnapshotTimeMs = nowMs;
                _lastWorldTick = world.Tick;
            }

            // Compute interpolation factor: 0 = at "from", 1 = at "to"
            // Allow slight extrapolation (up to 1.2) so a late snapshot doesn't
            // cause a visible stall at t=1 — the entity keeps drifting in the
            // same direction at reduced speed. Capped to prevent runaway drift.
            double elapsed = nowMs - _lastSnapshotTimeMs;
            float t = _snapshotIntervalMs > 0.0
                ? (float)(elapsed / _snapshotIntervalMs)
                : 1f;
            if (t < 0f) t = 0f;
            if (t > 1.2f) t = 1.2f;

            foreach (var kv in world.Entities)
            {
                var id = kv.Key;
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var e = kv.Value;
                bool isLocal = id == localId;

                if (_live.Add(id))
                {
                    // Type is carried on every snapshot the entity appears in, keyframe
                    // and delta alike, so it is already correct on the pass that first
                    // sees the id. Null-coalesced because the merger stores whatever the
                    // wire sent and a view should never have to null-check this.
                    _view.Spawn(id, isLocal, e.Type ?? string.Empty);
                }

                if (isLocal)
                {
                    // The local entity is NOT interpolated. Interpolation exists to hide
                    // the gap between snapshots for entities whose future this client
                    // cannot know; for its own avatar it buys nothing and costs a full
                    // interpolation delay (~66 ms at 15 Hz) on every one of its own
                    // inputs. The authoritative position is used directly, and any
                    // interpolation history is dropped so a later change of localId
                    // cannot resume from a stale pair.
                    _interp.Remove(id);
                    _view.SetState(id, e.X, e.Y, e.Hp, e.MaxHp);
                    continue;
                }

                if (newSnapshot)
                {
                    // Shift To → From, store new snapshot as To
                    if (_interp.TryGetValue(id, out var prev))
                    {
                        prev.FromX = prev.ToX;
                        prev.FromY = prev.ToY;
                        prev.ToX = e.X;
                        prev.ToY = e.Y;
                        prev.Hp = e.Hp;
                        prev.MaxHp = e.MaxHp;
                        prev.HasFrom = true;
                        _interp[id] = prev;
                    }
                    else
                    {
                        _interp[id] = new InterpEntry
                        {
                            FromX = e.X, FromY = e.Y,
                            ToX = e.X, ToY = e.Y,
                            Hp = e.Hp, MaxHp = e.MaxHp,
                            HasFrom = false
                        };
                    }
                }

                // Interpolate position, snap HP
                if (_interp.TryGetValue(id, out var entry))
                {
                    float ix, iy;
                    if (entry.HasFrom)
                    {
                        ix = entry.FromX + (entry.ToX - entry.FromX) * t;
                        iy = entry.FromY + (entry.ToY - entry.FromY) * t;
                    }
                    else
                    {
                        ix = entry.ToX;
                        iy = entry.ToY;
                    }
                    _view.SetState(id, ix, iy, entry.Hp, entry.MaxHp);
                }
                else
                {
                    _view.SetState(id, e.X, e.Y, e.Hp, e.MaxHp);
                }
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
                _interp.Remove(id);
                _view.Despawn(id);

                if (_explicitlyRemoved.Remove(id))
                {
                    DespawnsFromRemoval++;
                }
                else
                {
                    DespawnsFromAbsence++;
                }
            }
        }

        /// <summary>
        /// Drops the presentation of any entity whose locality changed because
        /// <c>localId</c> did, so the next pass re-spawns it with the right flag.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this is needed at all.</b> <c>isLocal</c> is handed to the view once,
        /// at <see cref="IEntityView.Spawn"/>, and the view is entitled to keep it —
        /// locality does not change over an entity's lifetime. What can change is
        /// <i>which id is local</i>: <c>NetworkClient.UserId</c> is a session fact, and a
        /// client that leaves and rejoins can come back as a different user while an
        /// entity from the previous session is still on screen. The spawn guard
        /// (<c>_live.Add</c>) then refuses to re-spawn it, so it keeps the locality it was
        /// given — and both the old avatar and the new one present themselves as the
        /// local player.
        /// </para>
        /// <para>
        /// Fixed here rather than in the view because the view is told locality once and
        /// correctly trusts it; the binder is what knows the id changed. Fixed by
        /// despawn-then-respawn rather than by widening <see cref="IEntityView"/> with a
        /// "locality changed" method: this costs one interface no consumer has to
        /// implement, and it is the same pair of calls a despawn/respawn across the
        /// boundary would have produced anyway.
        /// </para>
        /// <para>
        /// The despawns are deliberately NOT counted in
        /// <see cref="DespawnsFromAbsence"/>: the entity did not leave, and folding these
        /// into that counter would make an AOI-churn diagnostic lie. They get
        /// <see cref="Relocalizations"/> instead.
        /// </para>
        /// <para>
        /// Calling <see cref="Reset"/> on a session boundary avoids reaching this path at
        /// all, and is what a caller should do. This is the backstop for the caller that
        /// does not.
        /// </para>
        /// </remarks>
        private void RelocalizeIfLocalIdChanged(string localId)
        {
            var incoming = localId ?? string.Empty;
            if (string.Equals(incoming, _localId, StringComparison.Ordinal))
            {
                return;
            }

            // Only two ids can have changed locality: the one that used to be local and
            // the one that now is. Every other entity's `id == localId` answer is
            // unchanged, so re-spawning them would be churn for nothing.
            Forget(_localId);
            Forget(incoming);
            _localId = incoming;
        }

        /// <summary>
        /// Drops one id from the presentation so the next pass treats it as new. No-op
        /// for an id that is not currently presented.
        /// </summary>
        private void Forget(string id)
        {
            if (string.IsNullOrEmpty(id) || !_live.Remove(id))
            {
                return;
            }

            _interp.Remove(id);
            _view.Despawn(id);
            Relocalizations++;
        }

        /// <summary>Forgets all state and clears the view. For a fresh session.</summary>
        public void Reset()
        {
            foreach (var id in _live)
            {
                _view.Despawn(id);
            }

            _live.Clear();
            _interp.Clear();
            _explicitlyRemoved.Clear();
            _localId = string.Empty;
            _firstSnapshot = true;
            _lastWorldTick = 0;
            DespawnsFromRemoval = 0;
            DespawnsFromAbsence = 0;
            Relocalizations = 0;
        }
    }
}
