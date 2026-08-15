using System;
using System.Collections.Generic;
using System.Diagnostics;
using Cuvara.Netcode.Prediction;
using Cuvara.Netcode.World;
using Shared.GameLogic.Components;

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
    /// <b>Excluding the local entity from interpolation is not prediction.</b> On its own
    /// it removes the render buffer, not the round trip: keypress-to-visible is still
    /// input-send quantisation plus RTT plus server tick. Closing the rest needs a
    /// prediction layer reconciling against <c>WorldState.AckTick</c> — which is what the
    /// optional <see cref="LocalMovePredictor"/> constructor overload supplies.
    /// </para>
    /// <para>
    /// <b>The predictor overload is for views that render what <c>SetState</c> hands
    /// them, not for the <c>com.cuvara.dots</c> adapter</b>, which reads that position as
    /// authoritative and stores it as a reconciliation anchor. See the constructor's own
    /// remarks — getting this wrong is invisible at runtime.
    /// </para>
    /// <para>
    /// <b>With a predictor, the local entity is driven by prediction instead</b>: the
    /// snapshot becomes the anchor that prediction rewinds to rather than the thing
    /// rendered, and <see cref="IsPredicting"/> reports which of the two is live. Without
    /// one, everything above stands unchanged — passing no predictor is not a degraded
    /// mode, it is 0.4.0's behaviour, and it is what a client must fall back to when it
    /// cannot state the server's tick rate, speed and bounds. Only <b>movement</b> is
    /// predicted; HP always comes from the snapshot.
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
        private readonly LocalMovePredictor _predictor;
        private readonly HashSet<string> _live = new HashSet<string>();
        private readonly HashSet<string> _explicitlyRemoved = new HashSet<string>();
        private readonly List<string> _gone = new List<string>();

        private readonly Dictionary<string, InterpEntry> _interp = new Dictionary<string, InterpEntry>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private string _localId = string.Empty;
        private long _lastWorldTick;
        private double _lastSnapshotTimeMs;
        private double _lastRenderMs;
        private double _snapshotIntervalMs = 1000.0 / 15.0; // default 15 Hz, refined from actual arrivals
        private bool _firstSnapshot = true;

        /// <summary>
        /// Binds a view with no prediction: the local entity renders at the newest
        /// received position.
        /// </summary>
        public WorldViewBinder(IEntityView view) : this(view, null)
        {
        }

        /// <summary>
        /// Binds a view, driving the local entity from a
        /// <see cref="LocalMovePredictor"/> instead of from the newest snapshot.
        /// <b>For views that render what <see cref="IEntityView.SetState"/> hands them —
        /// NOT for the <c>com.cuvara.dots</c> adapter.</b>
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Do not use this with <c>com.cuvara.dots</c>' <c>DotsEntityView</c>.</b> That
        /// adapter treats the position it receives from <c>SetState</c> as authoritative
        /// and stores it in a <c>ReconciliationAnchor</c> component — "what the server
        /// said", the value a predictor rewinds to. This overload sends it the
        /// <i>predicted</i> position instead, so the anchor would hold a predicted value
        /// under a name that promises authority. Nothing detects that: the entity renders
        /// correctly, and the damage only appears when something finally reads the anchor
        /// and rewinds to a position its own prediction produced. That reads as float
        /// divergence and gets debugged as one, in the wrong package.
        /// </para>
        /// <para>
        /// <b>The DOTS path drives the predictor from the other side.</b> A system in that
        /// package reads <c>ReconciliationAnchor</c>, pairs it with
        /// <see cref="World.WorldState.AckTick"/>, calls this same
        /// <see cref="LocalMovePredictor"/>, and writes <c>LocalTransform</c> itself —
        /// claiming it with a <c>PredictedTransform</c> marker so the adapter stops
        /// writing it. Construct the binder with the single-argument constructor there and
        /// hand the predictor to that system instead. <see cref="LocalMovePredictor"/> is
        /// deliberately free of DOTS types so both paths share one implementation of the
        /// algorithm.
        /// </para>
        /// <para>
        /// This overload exists for the views where no anchor exists and
        /// <see cref="IEntityView.SetState"/> is the only channel there is —
        /// <see cref="GameObjectEntityView"/>, the WorldView sample, and the DOTS sample
        /// in this package, which uses its own view rather than the adapter.
        /// </para>
        /// </remarks>
        /// <param name="predictor">
        /// Prediction for the local player's movement, or null for none. A predictor
        /// reporting <see cref="LocalMovePredictor.IsEnabled"/> false is treated exactly
        /// like null: it is the predictor's job to refuse when it cannot reproduce the
        /// server's arithmetic, and the binder's job to believe it rather than to
        /// second-guess it with an approximation.
        /// </param>
        public WorldViewBinder(IEntityView view, LocalMovePredictor predictor)
        {
            _view = view;
            _predictor = predictor != null && predictor.IsEnabled ? predictor : null;
        }

        /// <summary>
        /// Whether the local entity is driven by prediction rather than by the newest
        /// snapshot. False when no predictor was supplied or the one supplied refused.
        /// </summary>
        public bool IsPredicting => _predictor != null;

        /// <summary>
        /// Measures the server's base tick rate from snapshot arrivals, so the advertised
        /// rate can be verified rather than trusted.
        /// </summary>
        /// <remarks>
        /// Fed here because this is the one place that already sees every snapshot and
        /// owns a clock. Reading it costs nothing; ignoring it costs what a wrong tick
        /// rate costs, which is continuous sub-threshold wrongness that never announces
        /// itself. See <see cref="TickRateEstimator"/>.
        /// </remarks>
        public TickRateEstimator TickRate { get; } = new TickRateEstimator();

        /// <summary>Entities currently presented.</summary>
        public int LiveCount => _live.Count;

        /// <summary>Despawns attributable to an explicit <c>removed</c> id.</summary>
        public int DespawnsFromRemoval { get; private set; }

        /// <summary>Despawns where the entity simply stopped being listed.</summary>
        public int DespawnsFromAbsence { get; private set; }

        /// <summary>
        /// Entities re-spawned because the local player's id changed under them. Nonzero
        /// means a session boundary was crossed without <see cref="Reset"/>.
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

                // The tick carried here is a BASE tick, so its rate is the movement
                // integration rate even though snapshots arrive at the slower world rate.
                TickRate.Sample(world.Tick, nowMs / 1000.0);

                // The gap between consecutive snapshot ticks is the server's world
                // interval, which is exactly how long it keeps integrating a held
                // direction. Handing it to the predictor here means no consumer has to
                // know the number, and none can configure it wrongly.
                _predictor?.SetHoldTicks(TickRate.SnapshotTickGap);
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

                if (isLocal && _predictor != null)
                {
                    // Prediction owns the local entity's position outright. The snapshot
                    // is still the authority — it is what Reconcile rewinds to — but what
                    // gets rendered is the predicted result, which is the whole point:
                    // the server's answer is by definition a round trip old.
                    if (newSnapshot)
                    {
                        // Adopt the server's speed before replaying: a buff, mount or
                        // slow changes what the server integrates with, and predicting
                        // at the old value desyncs every tick with no error on either
                        // side. Non-positive is ignored inside — on the wire that means
                        // "not sent", so the configured fallback stands.
                        _predictor.SetServerSpeed(e.Speed);
                        _predictor.Reconcile(new Vec2(e.X, e.Y), world.AckTick);
                    }

                    _predictor.Advance((float)((nowMs - _lastRenderMs) / 1000.0));

                    var predicted = _predictor.Position;
                    _view.SetState(id, predicted.X, predicted.Y, e.Hp, e.MaxHp);

                    // HP is deliberately still the server's. Only movement is predicted;
                    // see LocalMovePredictor for why combat is not.
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
                    // 'id != localId': the local player renders at the newest received
                    // position, never behind it. See the class remarks — this is a render
                    // delay removal, not prediction, and a late snapshot holds rather
                    // than extrapolates.
                    if (entry.HasFrom && !isLocal)
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

            _lastRenderMs = nowMs;

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
            _predictor?.Reset();
            TickRate.Reset();
            _localId = string.Empty;
            _firstSnapshot = true;
            _lastWorldTick = 0;
            DespawnsFromRemoval = 0;
            DespawnsFromAbsence = 0;
            Relocalizations = 0;
        }

        /// <summary>
        /// Drops the presentation of any entity whose locality changed because
        /// <c>localId</c> did, so the next pass re-spawns it with the right flag.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A backstop, not the mechanism.</b> The DOTS sample's rejoin path resets this
        /// binder on a session boundary, which is the correct fix and makes this path
        /// unreachable from there. This exists because the failure it prevents is silent
        /// and expensive: <c>isLocal</c> is handed to a view once at
        /// <see cref="IEntityView.Spawn"/> and the view is entitled to keep it — locality
        /// does not change over an entity's lifetime — but <i>which id is local</i> is a
        /// session fact, and a client that rejoins as a different user while the server
        /// still holds the previous session's entity (30 s, 60 s in a dungeon) would leave
        /// the old avatar presenting itself as the local player forever. Any caller that
        /// forgets to reset gets a wrong screen with no error.
        /// </para>
        /// <para>
        /// Despawn-then-respawn rather than a fourth <see cref="IEntityView"/> method:
        /// 0.4.0 already broke every implementation of that interface over one parameter,
        /// and this needs no new vocabulary — it is the same pair of calls a despawn and
        /// respawn across the boundary would have produced. At most two entities can flip.
        /// </para>
        /// <para>
        /// Counted in <see cref="Relocalizations"/>, deliberately not in
        /// <see cref="DespawnsFromAbsence"/>: the entity did not leave, and folding these
        /// into that counter would make an AOI-churn diagnostic lie in exactly the
        /// situation someone would be reading it.
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
            // the one that now is. Every other entity's answer is unchanged, so
            // re-spawning them would be churn for nothing.
            Forget(_localId);
            Forget(incoming);

            // The predicted position belonged to the previous player. Replaying this
            // player's inputs from that anchor would be prediction about the wrong avatar.
            _predictor?.Reset();

            _localId = incoming;
        }

        /// <summary>
        /// Drops one id from the presentation so the next pass treats it as new. No-op for
        /// an id that is not currently presented.
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
    }
}
