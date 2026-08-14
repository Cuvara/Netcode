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
    /// <b>The local player is excluded from interpolation.</b> Interpolating between the
    /// previous and the newest snapshot renders an entity behind the newest authoritative
    /// position by up to one snapshot interval — deliberate for remote entities, whose
    /// smoothness is the entire reason this code exists, and indefensible for the one
    /// entity whose response delay the player is holding a key to feel. The local id is
    /// rendered at the newest received position instead.
    /// </para>
    /// <para>
    /// <b>What the local entity does when a snapshot is late:</b> it holds at the last
    /// received position. It does not extrapolate. There is nothing honest to extrapolate
    /// from — the client does not simulate the local player, so a guess would be the
    /// binder inventing motion the server never confirmed, and it would have to be
    /// visibly undone when the real snapshot lands. Remote entities still extrapolate up
    /// to <c>t = 1.2</c>, because for them the alternative is a visible stall and the
    /// correction is somebody else's avatar drifting slightly, not the player's own.
    /// </para>
    /// <para>
    /// <b>This is not prediction.</b> It removes the render buffer, not the round trip.
    /// Keypress-to-visible remains input-send quantisation plus RTT plus server tick;
    /// closing that needs a prediction layer reconciling against
    /// <c>WorldState.AckTick</c>, which is surfaced for exactly that purpose and which
    /// nothing currently consumes.
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

                if (_live.Add(id))
                {
                    // Type is carried on every snapshot the entity appears in, keyframe
                    // and delta alike, so it is already correct on the pass that first
                    // sees the id. Null-coalesced because the merger stores whatever the
                    // wire sent and a view should never have to null-check this.
                    _view.Spawn(id, id == localId, e.Type ?? string.Empty);
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
                    // 'id != localId': the local player renders at the newest received
                    // position, never behind it. See the class remarks — this is a render
                    // delay removal, not prediction, and a late snapshot holds rather
                    // than extrapolates.
                    if (entry.HasFrom && id != localId)
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
            _firstSnapshot = true;
            _lastWorldTick = 0;
            DespawnsFromRemoval = 0;
            DespawnsFromAbsence = 0;
        }
    }
}
