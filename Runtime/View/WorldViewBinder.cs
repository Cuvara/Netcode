using System;
using System.Collections.Generic;
using Cuvara.Netcode.Interpolation;
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
    /// <b>Interpolation</b>: remote entity positions are interpolated between buffered
    /// snapshot states so movement appears smooth at render frame rate instead of
    /// teleporting every server tick (~66 ms at 15 Hz). The math is not here — it lives in
    /// <see cref="Cuvara.Netcode.Interpolation.SnapshotInterpolation"/>, which this class
    /// calls and which the DOTS path calls from a Burst job, so there is one
    /// implementation rather than two that drift. HP is never interpolated; it snaps,
    /// because a half-applied hit is not a state the server was ever in.
    /// </para>
    /// <para>
    /// <b>The render moment comes from a free-running clock, not from the last arrival.</b>
    /// <see cref="Cuvara.Netcode.Interpolation.InterpolationClock"/> holds a fractional
    /// server tick that advances with real time and is steered — never snapped — toward
    /// <see cref="Cuvara.Netcode.Interpolation.InterpolationConfig.TargetDelay"/> behind
    /// the newest received tick. Samples are bracketed by their <i>tick</i>, so where an
    /// entity is drawn is a function of what the server said and when, and not of when a
    /// packet happened to arrive.
    /// </para>
    /// <para>
    /// <b>This replaced a phase that restarted at zero on every arrival</b>, which is
    /// worth recording because the symptoms were subtle and were being read as network
    /// problems. A snapshot arriving a little early threw away the unrendered remainder of
    /// the current segment, and the avatar lurched forward — measured at 4.3x a normal
    /// frame's travel in one frame, on ordinary jitter with no packet loss. A late one ran
    /// to a clamp, froze, and then stepped backwards by up to a fifth of a segment when
    /// the snapshot landed, which reads as rubber-banding. And a single dropped snapshot
    /// made an entity render at over 1.5x speed and then stall, because a doubled position
    /// delta was divided by an arrival-interval average that had moved only 30 % of the
    /// way. All three are covered by <c>RemoteInterpolationContinuityTests</c>.
    /// </para>
    /// <para>
    /// <b>The local player is excluded from interpolation.</b> Rendering against a
    /// deliberately delayed clock puts an entity behind the newest authoritative position
    /// by <see cref="Cuvara.Netcode.Interpolation.InterpolationConfig.TargetDelay"/> —
    /// deliberate for remote entities, whose
    /// smoothness is the entire reason this code exists, and indefensible for the one
    /// entity whose response delay the player is holding a key to feel. The local id is
    /// rendered at the newest received position instead.
    /// </para>
    /// <para>
    /// <b>What the local entity does when a snapshot is late:</b> it holds at the last
    /// received position. It does not extrapolate. There is nothing honest to extrapolate
    /// from — the client does not simulate the local player, so a guess would be the
    /// binder inventing motion the server never confirmed, and it would have to be
    /// visibly undone when the real snapshot lands. Remote entities may still carry
    /// motion a little past the newest sample —
    /// <see cref="Cuvara.Netcode.Interpolation.InterpolationConfig.MaxExtrapolation"/>,
    /// 50 ms by default — because for them the alternative is a visible stall and the
    /// correction is somebody else's avatar drifting slightly, not the player's own.
    /// Unlike the <c>t = 1.2</c> clamp this replaced, the carried distance is not stepped
    /// back out when the snapshot arrives: the render clock is not reset by an arrival, so
    /// the next segment absorbs it.
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
        /// <summary>
        /// Per-entity presentation state: the retained samples the shared interpolator
        /// reads, plus the HP that is snapped rather than interpolated.
        /// </summary>
        /// <remarks>
        /// This used to hold exactly two positions and a flag. Two is the fewest that can
        /// be lerped between and one fewer than continuity needs: with only a pair, a
        /// snapshot arriving early had nowhere to wait, so it displaced the segment being
        /// rendered and the entity lurched. The ring lets an early arrival be buffered and
        /// consumed when the render clock reaches it, which is what a jitter buffer is.
        /// </remarks>
        private struct InterpEntry
        {
            public EntitySampleRing Ring;
            public int Hp, MaxHp;
        }

        private readonly IEntityView _view;
        private readonly LocalMovePredictor _predictor;
        private readonly HashSet<string> _live = new HashSet<string>();
        private readonly HashSet<string> _explicitlyRemoved = new HashSet<string>();
        private readonly List<string> _gone = new List<string>();

        private readonly Dictionary<string, InterpEntry> _interp = new Dictionary<string, InterpEntry>();

        /// <summary>
        /// Rings of despawned entities, kept for the next spawn. Area-of-interest churn
        /// makes spawn/despawn a steady-state event rather than a rare one, and a fresh
        /// array per entry would put that churn on the garbage collector.
        /// </summary>
        private readonly Stack<EntitySampleRing> _ringPool = new Stack<EntitySampleRing>();

        private readonly InterpolationConfig _interpConfig;
        private InterpolationClock _interpClock;
        private double _lastInterpAdvanceMs = -1.0;

        private readonly IViewClock _clock;
        private string _localId = string.Empty;
        private long _lastWorldTick;
        private double _lastRenderMs;

        /// <summary>Set once AdvanceFrame is driving the clock, which then owns it.</summary>
        private bool _frameDriven;
        private int _localHp;
        private int _localMaxHp;
        private bool _localSeen;

        /// <summary>
        /// Binds a view with no prediction: the local entity renders at the newest
        /// received position.
        /// </summary>
        public WorldViewBinder(IEntityView view) : this(view, null, null)
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
            : this(view, predictor, null)
        {
        }

        /// <summary>
        /// Binds a view, driving interpolation from a supplied <see cref="IViewClock"/>
        /// instead of from a <see cref="StopwatchViewClock"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A test seam, not a runtime option.</b> Remote interpolation is a function of
        /// time since arrival divided by the measured arrival interval, and every
        /// interesting property of the rendered motion lives <i>between</i> arrivals.
        /// With the clock fixed to a self-starting <c>Stopwatch</c>, a test can only
        /// sample the instant it executed at — which, immediately after handing the binder
        /// a snapshot, is <c>t ≈ 0</c>, the one point where continuity is trivially true.
        /// That is why the existing interpolation tests assert <c>Is.LessThan(1f)</c> and
        /// nothing sharper.
        /// </para>
        /// <para>
        /// Callers in production pass nothing here and get the same
        /// <c>Stopwatch</c> this class has always used. Supplying a clock does not
        /// change what the binder computes, only where the numbers it computes from come
        /// from.
        /// </para>
        /// </remarks>
        /// <param name="clock">
        /// Time source for interpolation, or null for <see cref="StopwatchViewClock"/>.
        /// Null rather than an overload without the parameter for the same reason
        /// <paramref name="predictor"/> accepts null: the default is applied in one place,
        /// so no call site can construct a binder with no clock at all.
        /// </param>
        public WorldViewBinder(IEntityView view, LocalMovePredictor predictor, IViewClock clock)
            : this(view, predictor, clock, InterpolationConfig.Default)
        {
        }

        /// <summary>
        /// Binds a view with explicit interpolation tuning.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The defaults are what every other overload uses and what the package is tuned
        /// for at a 15 Hz world rate; this exists so a deployment at a different rate, or
        /// a test pinning an edge of the algorithm, can say so rather than discover it.
        /// Any non-positive field is replaced by its default —
        /// <c>default(InterpolationConfig)</c> is all zeroes, and a zero interval or ring
        /// capacity would divide by zero or buffer nothing.
        /// </para>
        /// <para>
        /// A struct rather than a <c>ScriptableObject</c> deliberately: stage 4 reads the
        /// same type from a <c>[BurstCompile]</c> job, which cannot follow a managed asset
        /// reference. Both paths configured by one type is the point.
        /// </para>
        /// </remarks>
        public WorldViewBinder(IEntityView view, LocalMovePredictor predictor, IViewClock clock,
                               InterpolationConfig interpolation)
        {
            _view = view;
            _predictor = predictor != null && predictor.IsEnabled ? predictor : null;
            _clock = clock ?? new StopwatchViewClock();
            _interpConfig = interpolation.Normalized();
        }

        /// <summary>
        /// The interpolation tuning in force, after defaulting.
        /// </summary>
        public InterpolationConfig Interpolation => _interpConfig;

        /// <summary>
        /// The moment, in server ticks, remote entities are currently being rendered at.
        /// Diagnostics: it should sit a little under
        /// <see cref="InterpolationConfig.TargetDelay"/> behind the newest received tick.
        /// </summary>
        public double RenderTick => _interpClock.RenderTick;

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

        /// <summary>
        /// Base tick carried by the newest snapshot applied, or 0 before the first one.
        /// </summary>
        /// <remarks>
        /// Exposed for diagnostics: compared against <c>LocalMovePredictor.BaseTick</c> it is
        /// the prediction lead in ticks, which is the one number that says whether the two
        /// clocks are keeping step. Nothing in the package reads it.
        /// </remarks>
        public long LastServerTick => _lastWorldTick;

        /// <summary>Round-trip time in milliseconds, as last reported by the session.</summary>
        /// <remarks>
        /// Set by the consumer; 0 until then, which makes the steering target degrade to one
        /// snapshot interval rather than to something invented.
        /// </remarks>
        public long RoundTripMs { get; set; }

        /// <summary>
        /// Measures how old the newest snapshot is by the time it is used. See
        /// <see cref="Prediction.SnapshotStalenessEstimator"/>.
        /// </summary>
        public SnapshotStalenessEstimator Staleness { get; } = new SnapshotStalenessEstimator();

        /// <summary>
        /// Base ticks the client's clock should sit ahead of the newest snapshot's tick: how
        /// old that snapshot already is when it is acted on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The clock is steered onto the newest snapshot's tick, and that tick is old by the
        /// time it is read. Steering to it with no allowance drags the client's clock behind
        /// the server's real one by exactly that age, and a tick number then stops naming the
        /// same moment on both sides: the client's tick N carries inputs the server will not
        /// apply until its own tick N + age. The reconcile reports that as a correction of one
        /// input interval, on every snapshot.
        /// </para>
        /// <para>
        /// <b>Measured, with a derived fallback.</b> <see cref="Staleness"/> fits the server's
        /// clock to the client's — offset and rate — and reports the height of the newest
        /// snapshot above that line, which is its age beyond the best the route has shown.
        /// Until it has a line, the old derived figure stands: one snapshot interval, because
        /// the newest snapshot describes the tick it was produced on and the next is a whole
        /// interval behind it. The derived figure is quantised to whole ticks while the real
        /// age is fractional, which is what left an unlucky join phase with a constant
        /// 0.3333-unit correction; it is a fallback for the first seconds of a session, not
        /// the answer.
        /// </para>
        /// <para>
        /// Either way the half round trip is added on top: the one-way delay sits inside the
        /// fitted offset, inseparable from the difference between the two clocks' origins, so
        /// no arrival-time measurement can recover it and the caller must supply it.
        /// </para>
        /// <para>
        /// <b>The ceiling is not decoration.</b> This number steers a clock, and a measurement
        /// that can run away will take the simulation with it — the clock follows, the
        /// reconcile reports the growing gap as a correction, and both keep going. An earlier
        /// estimator that fitted the offset alone did exactly that, reaching 613 ticks with
        /// the lead tracking it the whole way. Two snapshot intervals plus a round trip is
        /// past anything a healthy link produces and far short of a runaway.
        /// </para>
        /// </remarks>
        private int TargetLeadTicks()
        {
            int gap = TickRate.SnapshotTickGap > 0 ? TickRate.SnapshotTickGap : 1;

            float lead = Staleness.IsUsable ? Staleness.StalenessTicks : gap;

            int rttTicks = 0;
            if (RoundTripMs > 0 && TickRate.EstimatedHz > 0f)
            {
                rttTicks = (int)Math.Round(RoundTripMs * TickRate.EstimatedHz / 1000.0);
                lead += rttTicks * 0.5f;
            }

            int ticks = (int)Math.Round(lead);
            if (ticks < 0) ticks = 0;

            int ceiling = gap * 2 + rttTicks;
            return ticks > ceiling ? ceiling : ticks;
        }

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

            double nowMs = _clock.NowMs;
            double nowSeconds = nowMs / 1000.0;
            bool newSnapshot = world.Tick > _lastWorldTick;

            if (newSnapshot)
            {
                _lastWorldTick = world.Tick;

                // The tick carried here is a BASE tick, so its rate is the movement
                // integration rate even though snapshots arrive at the slower world rate.
                TickRate.Sample(world.Tick, nowMs / 1000.0);

                // The gap between consecutive snapshot ticks is the server's world
                // interval, which is exactly how long it keeps integrating a held
                // direction. Handing it to the predictor here means no consumer has to
                // know the number, and none can configure it wrongly.
                _predictor?.SetHoldTicks(TickRate.SnapshotTickGap);

                // The same confirmed gap seeds the interpolation clock's
                // seconds-per-tick. It was already being computed two lines above and
                // handed to the predictor; the interpolator ignored it and measured
                // arrival intervals instead, which is why a dropped snapshot changed the
                // rendered speed. SnapshotTickGap is a MINIMUM and is only adopted after
                // two sightings, so a drop never widens it.
                _interpClock.NoteSnapshot(world.Tick, nowSeconds, TickRate.SnapshotTickGap, _interpConfig);
            }

            // Advance the render timeline by real frame time. Tick is called once per
            // rendered frame by a real client, so this delta is the frame time; a pass
            // that carries a snapshot is still just a frame. Tracked separately from
            // _lastRenderMs because that field belongs to the predictor's clock-ownership
            // rule (see AdvanceFrame) and conflating the two is how the double-advance
            // bug happened the first time.
            if (_lastInterpAdvanceMs >= 0.0)
            {
                _interpClock.Advance((nowMs - _lastInterpAdvanceMs) / 1000.0, _interpConfig);
            }

            _lastInterpAdvanceMs = nowMs;

            // The concrete map, not the interface: a foreach over the IReadOnlyDictionary
            // boxes the struct enumerator — 88 bytes on every rendered frame, ~44 KB/s at
            // 500 fps into Unity's stop-the-world GC, measured. This was the only per-frame
            // allocation left in the render path.
            foreach (var kv in world.EntityMap)
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
                        _predictor.SeedBaseTick(world.Tick);
                        _predictor.SetServerSpeed(e.Speed);

                        // ...and keep them together afterwards. Seeding aligns the two
                        // clocks once, at join; from then on each counts base ticks off its
                        // own wall clock and nothing bounds the difference. Measured live,
                        // the client's tick ran 456 ticks -- 7.6 seconds -- past the newest
                        // snapshot and was still climbing, with every correction counter
                        // reading clean, because Reconcile replays that whole span as
                        // prediction lead. See LocalMovePredictor.SteerToServerTick.
                        //
                        // The target lead is what the client has to predict THROUGH to be
                        // showing "now": one snapshot interval, because that is how stale
                        // the newest snapshot already is when it arrives, plus half a round
                        // trip for the journey. Both are measured rather than assumed.
                        // Sampled at the moment of USE, not of arrival, so the wait for this
                        // frame is inside the measurement -- that wait is a real part of how
                        // old the snapshot is when the client acts on it, and it is the part
                        // that varies with a client's join phase.
                        // The ADVERTISED rate, not the one measured off the wire. The
                        // measurement converts a tick to a time, so it has to use the rate the
                        // server stamped that tick at; an estimate's error becomes a drift
                        // against the wall clock, and a 57.7 Hz reading of a 60 Hz server took
                        // this from a stable figure to 613 ticks and climbing in under a
                        // minute, dragging the steering with it.
                        Staleness.Sample(world.Tick, nowSeconds, _predictor.TickRateHz);

                        _predictor.SteerToServerTick(world.Tick, TargetLeadTicks());

                        // world.Tick is the tick this snapshot was produced on, and
                        // SeedBaseTick has already put it in the same space as the
                        // predictor's base tick. Passing it keeps the prediction lead when
                        // the acknowledgement empties the pending buffer, which happens in
                        // ordinary play: inputs go at ~15 Hz against a 60 Hz base tick, so
                        // there is a window of up to four base ticks after an
                        // acknowledgement before the next input is recorded (#53).
                        _predictor.Reconcile(new Vec2(e.X, e.Y), world.AckTick, world.Tick);
                    }

                    // Only the time no frame has advanced yet. AdvanceFrame is the
                    // ordinary clock and stamps _lastRenderMs itself, so this is normally
                    // a residue near zero; it carries the full gap only when nothing is
                    // driving frames — a headless harness that pumps snapshots and
                    // nothing else, which must still see prediction move.
                    //
                    // It used to pass the whole wall-clock gap unconditionally while
                    // AdvanceFrame was separately advancing the same span in frame
                    // slices, so the predictor's clock ran at ~2x real time. Every
                    // consequence of that lands on the local avatar alone: base ticks
                    // accrued twice as fast, so the server's hold window (WorldEvery
                    // ticks) expired in half the real time it should, and the avatar
                    // stood still between inputs. That is a stutter no frame rate can
                    // fix, on the one entity the player is looking at, while remotes —
                    // driven by the interpolator's own clock — stayed smooth.
                    // Only when nothing else is driving the clock. AdvanceFrame is the
                    // ordinary driver and sets _frameDriven; this fallback exists for a
                    // harness that pumps snapshots and renders nothing, which must still
                    // see prediction move.
                    if (!_frameDriven)
                    {
                        double unadvancedMs = nowMs - _lastRenderMs;
                        if (unadvancedMs > 0.0)
                        {
                            _predictor.Advance((float)(unadvancedMs / 1000.0));
                        }
                    }

                    _lastRenderMs = nowMs;

                    // Kept so AdvanceFrame can re-render between snapshots. Only movement
                    // is predicted, so HP stays whatever the server last said.
                    _localHp = e.Hp;
                    _localMaxHp = e.MaxHp;
                    _localSeen = true;

                    var predicted = _predictor.Position;
                    _view.SetState(id, predicted.X, predicted.Y, e.Hp, e.MaxHp);

                    // HP is deliberately still the server's. Only movement is predicted;
                    // see LocalMovePredictor for why combat is not.
                    continue;
                }

                if (newSnapshot)
                {
                    if (!_interp.TryGetValue(id, out var fresh))
                    {
                        fresh = new InterpEntry { Ring = RentRing() };
                    }

                    // Rejected for a tick that is not strictly newer, which the
                    // evaluator's bracketing must never see. Nothing is lost: a
                    // superseded state is not worth rendering.
                    fresh.Ring.TryPush(new InterpolationSample
                    {
                        Tick = world.Tick,
                        ReceiveTime = nowSeconds,
                        X = e.X,
                        Y = e.Y
                    });

                    // HP is snapped, never interpolated. A half-applied hit is not a
                    // state the server ever occupied.
                    fresh.Hp = e.Hp;
                    fresh.MaxHp = e.MaxHp;
                    _interp[id] = fresh;
                }

                if (_interp.TryGetValue(id, out var entry) && entry.Ring.Length > 0)
                {
                    float ix, iy;

                    // 'isLocal': the local player renders at the newest received position,
                    // never behind it. See the class remarks — this is a render delay
                    // removal, not prediction, and a late snapshot holds rather than
                    // extrapolates. The jitter buffer is for entities whose smoothness is
                    // worth a delay; the one the player is holding a key to move is not
                    // one of them.
                    if (isLocal)
                    {
                        var newest = entry.Ring[entry.Ring.Length - 1];
                        ix = newest.X;
                        iy = newest.Y;
                    }
                    else if (!SnapshotInterpolation.Evaluate(
                                 new EntitySampleBuffer(entry.Ring), _interpClock, _interpConfig,
                                 out ix, out iy))
                    {
                        var newest = entry.Ring[entry.Ring.Length - 1];
                        ix = newest.X;
                        iy = newest.Y;
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
                if (!world.EntityMap.ContainsKey(id))
                {
                    _gone.Add(id);
                }
            }

            for (var i = 0; i < _gone.Count; i++)
            {
                var id = _gone[i];
                _live.Remove(id);
                ReleaseRing(id);
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
        /// Advances prediction and re-renders the local entity. Call once per rendered
        /// frame, from <c>Update</c> or equivalent — not from snapshot handling.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Without this the smoothing does nothing observable.</b> Prediction used to
        /// be advanced, and the local entity re-rendered, only inside snapshot
        /// processing — so the rendered position changed at the <i>world</i> rate, 15 Hz,
        /// however fast the client was drawing. Every frame between snapshots showed the
        /// avatar perfectly still, and the frame a snapshot landed on showed the whole
        /// interval's movement at once. Spreading a step across an interval is worthless
        /// when nothing samples the position during that interval: the interpolation was
        /// only ever read at its endpoints.
        /// </para>
        /// <para>
        /// It shows up in frame-delta burstiness as roughly <i>frames per snapshot
        /// interval</i> — about 20 at 300 fps against 15 Hz — which is the band the live
        /// measurement reported on the predicting and non-predicting paths alike. That
        /// the two were similar was the clue: a number that does not care whether
        /// prediction is on is not measuring prediction.
        /// </para>
        /// <para>
        /// Safe before a local entity exists and safe without a predictor — it no-ops in
        /// both cases, and ignores a non-positive delta.
        /// </para>
        /// </remarks>
        /// <param name="deltaTime">Seconds since the previous call.</param>
        public void AdvanceFrame(float deltaTime)
        {
            if (_predictor == null || !_localSeen || deltaTime <= 0f)
            {
                return;
            }

            _predictor.Advance(deltaTime);

            // Claim the clock. Both drivers share it and only one may advance it, or the
            // predictor runs fast: Tick is called once per rendered frame by a real
            // client, not once per arriving snapshot, so leaving the snapshot path
            // advancing too made every frame count twice and the predictor's clock ran at
            // 2x real time.
            //
            // The whole cost of that lands on the local avatar. Base ticks accrued twice
            // as fast, so the server's hold window (WorldEvery base ticks) expired in half
            // the real time it should, and the avatar stood still between inputs —
            // measured at 15 sends per real second being read as one every 0.133s. No
            // frame rate fixes it, and remote entities, driven by the interpolator's own
            // clock, stayed smooth throughout. "Only the player I control stutters" is the
            // signature of exactly this.
            _frameDriven = true;
            _lastRenderMs = _clock.NowMs;

            var predicted = _predictor.Position;
            _view.SetState(_localId, predicted.X, predicted.Y, _localHp, _localMaxHp);
        }

        /// <summary>Forgets all state and clears the view. For a fresh session.</summary>
        public void Reset()
        {
            _frameDriven = false;
            foreach (var id in _live)
            {
                _view.Despawn(id);
            }

            _live.Clear();
            foreach (var kv in _interp)
            {
                Recycle(kv.Value.Ring);
            }

            _interp.Clear();
            _interpClock.Reset();
            _lastInterpAdvanceMs = -1.0;
            _explicitlyRemoved.Clear();
            _predictor?.Reset();
            TickRate.Reset();

            // The floor describes one route to one server; carrying it across a session
            // boundary measures the new connection against the old one's best case, and a
            // best case that is no longer achievable reads as constant staleness.
            Staleness.Reset();
            _localId = string.Empty;
            _localHp = 0;
            _localMaxHp = 0;
            _localSeen = false;
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
        /// <summary>A ring from the pool, or a new one when the pool is dry.</summary>
        private EntitySampleRing RentRing()
        {
            return _ringPool.Count > 0
                ? _ringPool.Pop()
                : new EntitySampleRing(_interpConfig.RingCapacity);
        }

        /// <summary>Drops an entity's interpolation state and keeps its ring for reuse.</summary>
        private void ReleaseRing(string id)
        {
            if (_interp.TryGetValue(id, out var entry))
            {
                Recycle(entry.Ring);
                _interp.Remove(id);
            }
        }

        /// <summary>
        /// Returns a ring to the pool, emptied. Emptying matters: a reused ring still
        /// holding the previous entity's samples would interpolate a newly spawned entity
        /// from wherever the last one stood.
        /// </summary>
        private void Recycle(EntitySampleRing ring)
        {
            if (ring == null)
            {
                return;
            }

            ring.Clear();
            _ringPool.Push(ring);
        }

        private void Forget(string id)
        {
            if (string.IsNullOrEmpty(id) || !_live.Remove(id))
            {
                return;
            }

            ReleaseRing(id);
            _view.Despawn(id);
            Relocalizations++;
        }
    }
}
