using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace Cuvara.Netcode.Prediction
{
    /// <summary>
    /// Client-side prediction and reconciliation for the local player's <b>movement</b>,
    /// and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The problem.</b> Without prediction, an input travels to the server, is applied
    /// on the next server tick, and comes back in a snapshot before the player's own
    /// avatar moves — a full round trip of visible lag on your own keypress. Prediction
    /// applies the input locally the instant it is sent and then corrects itself against
    /// the authoritative answer when it arrives.
    /// </para>
    /// <para>
    /// <b>The loop.</b> Each input gets a tick number, is applied immediately to the
    /// predicted position, and is kept. Each snapshot carries <c>AckTick</c> — the newest
    /// input tick the server accepted for this player — so the client can drop everything
    /// up to and including it, rewind to the authoritative position, and replay only what
    /// the server has not seen yet.
    /// </para>
    ///
    /// <para><b>Movement only, on purpose.</b> The sample has an HP-prediction path and
    /// this does not touch it. Combat has server-side rules the client cannot faithfully
    /// reproduce — cooldowns counted in server ticks, range checked against positions the
    /// client only has a stale copy of, validation that can reject outright. A predicted
    /// hit that the server refuses shows the player damage that never happened and then
    /// takes it back, which is worse than showing the damage late. Movement is predictable
    /// because it is a pure function of (position, direction, speed, dt) that both sides
    /// compute with the same code.</para>
    ///
    /// <para><b>Replay goes through <c>Shared.GameLogic</c>, never through a
    /// re-implementation.</b> <see cref="MovementSystem.TryMove"/> is called — the exact
    /// entry point <c>InputHandler</c> calls on the server — which internally runs
    /// <see cref="MovementSystem.ResolveDirection"/> and then
    /// <see cref="MovementSystem.Integrate"/>. Both matter:</para>
    /// <list type="bullet">
    /// <item><description><c>Integrate</c> splits its multiply-add into separate float
    /// locals specifically to deny the JIT an FMA contraction, which rounds once instead
    /// of twice. A hand-written <c>pos += dir * speed * dt</c> re-introduces exactly the
    /// divergence that split exists to prevent, and it diverges in the last place — it
    /// drifts silently rather than failing.</description></item>
    /// <item><description><c>ResolveDirection</c> normalizes a magnitude above 1, so raw
    /// diagonal input <c>(1,1)</c> moves at unit speed, not 1.414×. Calling
    /// <c>Integrate</c> directly with an unnormalized vector would predict diagonal
    /// movement 41% too fast — correct arithmetic on the wrong input.</description></item>
    /// </list>
    ///
    /// <para><b>Corrections are smoothed below a threshold and snapped above it.</b> Every
    /// reconcile produces some error, mostly sub-millimetre float noise, and hard-setting
    /// the position on each one is visible as jitter. Blending the whole way is worse in
    /// the other direction: a genuine correction — a rejected input, a collision, a
    /// desync — arrives as a slow glide from a wrong place, during which the avatar is
    /// somewhere neither the client nor the server believes it is. So: below
    /// <see cref="SmoothingThreshold"/> the error is absorbed into a decaying render
    /// offset; above it the offset is dropped and the avatar snaps.</para>
    ///
    /// <para><b>Known divergence: superseded inputs.</b> When two inputs reach the server
    /// inside one simulation tick, only the newest moves the entity — the server refuses
    /// to let packet rate buy speed. The client predicted both, so it is one step ahead
    /// until the next snapshot pulls it back. This is bounded (it cannot accumulate past a
    /// snapshot) and is why the client's input rate should match the server's tick rate
    /// rather than exceed it.</para>
    ///
    /// <para><b>Two ways to drive it, and they must not both be used on one entity.</b>
    /// This class is only the algorithm and the input buffer; something else decides
    /// where the authoritative position comes from and where the predicted one goes.</para>
    /// <list type="bullet">
    /// <item><description><b>Through <c>WorldViewBinder</c></b> — pass it to the
    /// two-argument constructor and the binder reconciles and pushes the predicted
    /// position out through <c>IEntityView.SetState</c>. For views that render whatever
    /// <c>SetState</c> hands them. <b>Not for the <c>com.cuvara.dots</c> adapter</b>,
    /// which reads that position as authoritative.</description></item>
    /// <item><description><b>Directly, from a DOTS system</b> — call
    /// <see cref="Reconcile"/> with the position from that package's
    /// <c>ReconciliationAnchor</c> and the tick from
    /// <see cref="World.WorldState.AckTick"/>, then read <see cref="Position"/> into
    /// <c>LocalTransform</c>. Construct the binder without a predictor in that
    /// case.</description></item>
    /// </list>
    ///
    /// <para><b>Positions here are in the server's coordinate space, not the client's
    /// world space.</b> That is not a detail: <see cref="MovementSystem.TryMove"/> clamps
    /// to <see cref="MapBounds"/>, which the server expresses in its own 2D space, so a
    /// caller holding a projected world-space position must recover the server-space one
    /// rather than project back — a round trip through a projection is not bit-exact, and
    /// bit-exactness is the entire point of replaying through the shared library.</para>
    ///
    /// <para><b>This type's public surface is a cross-package contract.</b>
    /// <c>com.cuvara.dots</c> drives it from a system this package cannot see and must
    /// never reference. Six members are load-bearing across that seam —
    /// <see cref="RecordInput"/>, <see cref="Reconcile"/>, <see cref="Advance"/>,
    /// <see cref="Position"/>, <see cref="IsEnabled"/> and <see cref="Reset"/> — and
    /// changing any of their signatures breaks a consumer whose compiler errors will not
    /// appear in this repository's CI, because the DOTS adapter is not built here. Add
    /// rather than change, and if one of them genuinely has to change, say so on the dots
    /// side before it lands, not after.</para>
    ///
    /// <para>Not thread-safe. Drive it from the thread that sends input and consumes
    /// snapshots.</para>
    /// </remarks>
    public sealed class LocalMovePredictor
    {
        private readonly struct PendingInput
        {
            public readonly long Tick;
            public readonly float MoveX;
            public readonly float MoveY;

            /// <summary>
            /// The local base tick this input was applied on. Replay needs it because the
            /// server's hold window is counted in base ticks, not in inputs: reproducing
            /// the server means reproducing <i>when</i> each input landed on the timeline,
            /// not merely the order they arrived in.
            /// </summary>
            public readonly long BaseTick;

            /// <summary>
            /// The entity's last-moved tick as it stood immediately before this input.
            /// </summary>
            /// <remarks>
            /// <para>Replay restores this rather than reconstructing it as
            /// <c>BaseTick - 1</c>. The two differ whenever the input before this one did
            /// not move the entity — a deadzone, a rejected vector — and the value decides
            /// which ticks <see cref="ApplyHeld"/> considers already stepped during replay.
            /// Replay and the live path must produce identical positions or every reconcile
            /// injects a correction the network did not cause.</para>
            ///
            /// <para>It used to matter for a second and larger reason: <c>dt</c> was
            /// <c>(now - lastMoveTick) * _dt</c>, so reconstructing the value wrongly handed
            /// the first replayed step a different timestep from the live one. That rule is
            /// gone — every step is one tick — which removes the amplification, not the
            /// bookkeeping.</para>
            /// </remarks>
            public readonly long LastMoveTickBefore;

            public PendingInput(
                long tick, float moveX, float moveY, long baseTick, long lastMoveTickBefore)
            {
                Tick = tick;
                MoveX = moveX;
                MoveY = moveY;
                BaseTick = baseTick;
                LastMoveTickBefore = lastMoveTickBefore;
            }
        }

        /// <summary>
        /// Unacknowledged inputs the buffer holds before the oldest is discarded.
        /// </summary>
        /// <remarks>
        /// At a 15 Hz input rate this is over eight seconds of history, which is far
        /// beyond any round trip a playable session has. Overflowing therefore does not
        /// mean "busy network", it means the server stopped acknowledging — so the
        /// overflow is counted in <see cref="DroppedInputs"/> rather than absorbed.
        /// </remarks>
        public const int Capacity = 128;

        /// <summary>
        /// Correction distance, in world units, above which the avatar is snapped instead
        /// of eased.
        /// </summary>
        /// <remarks>
        /// Chosen against the movement model rather than by taste: at the default speed of
        /// 5 units/s and a 15 Hz tick, one tick of movement is 0.33 units. This is 1.5
        /// ticks' worth — large enough that ordinary float noise and a single superseded
        /// input stay in the smooth path, small enough that a real desync does not glide.
        /// </remarks>
        public const float SmoothingThreshold = 0.5f;

        /// <summary>
        /// Fraction of the remaining render offset retired per second, as the base of an
        /// exponential decay.
        /// </summary>
        /// <remarks>
        /// Frame-rate independent by construction: the offset is scaled by
        /// <c>pow(base, dt)</c>, so 30 fps and 144 fps retire the same fraction per second
        /// rather than per frame. At this value an error is ~99% gone after 250 ms.
        /// </remarks>
        public const float OffsetDecayPerSecond = 1e-8f;

        private readonly PendingInput[] _pending = new PendingInput[Capacity];
        private readonly PredictionSettings _settings;
        private float _speed;
        private readonly float _dt;

        private int _head;      // next write slot
        private int _count;     // live entries
        private long _lastRecordedTick;

        private Vec2 _predicted;      // simulated position: authoritative + replayed inputs
        private Vec2 _renderOffset;   // predicted-minus-corrected, decayed to zero
        private Vec2 _step;           // displacement the most recent input produced
        private float _sinceInput;    // seconds since it, for spreading that step over frames
        // Starts at 1, never 0: zero is the "nothing held" sentinel for _heldFrom, so a
        // hold set by the very first input on tick 0 would read as no hold at all and
        // that input alone would take one step instead of the window's four. The server
        // avoids the collision the same way — its _currentTick has already advanced by
        // the time any input is processed.
        private long _baseTick = 1;   // local base-tick counter, advanced by Advance
        private float _tickAccumulator; // seconds carried toward the next base tick
        private float _heldX, _heldY; // direction the server would still be integrating
        private long _heldFrom;       // base tick the hold started on; 0 = nothing held
        private long _lastMoveTick;   // base tick this entity last actually moved on
        private float _inputInterval; // observed seconds between inputs; diagnostics
        private float _stepInterval;  // observed seconds between consecutive steps
        private float _lastStepAt;    // _elapsed when the previous step landed
        private float _lastInputAt;   // _elapsed when the previous input arrived
        private float _elapsed;       // monotonic seconds fed through Advance

        // The simulated position as it stood BEFORE anything stepped the current base tick,
        // and the tick that snapshot belongs to. Used to re-take a tick's single step when a
        // real input turns up after the hold has already taken it -- see RecordInput.
        private readonly int _maxCatchUpTicks;

        /// <summary>
        /// Where the prediction stood at the end of each of the last <see cref="HistoryTicks"/>
        /// base ticks, indexed by <c>tick % HistoryTicks</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is what makes a correction a comparison rather than a guess.</b> A
        /// snapshot describes the world at ONE tick, and by the time it arrives the client's
        /// clock is somewhere past it. Comparing the snapshot against the client's position
        /// NOW compares two different moments, so the reconcile has to make up the
        /// difference — by replaying the gap forward from the server's answer, which is the
        /// only thing it can do without a record of where the client actually was.
        /// </para>
        /// <para>
        /// <b>That extrapolation is wrong whenever the client's tick lead is phase rather
        /// than travel</b>, and it usually is: ticks the client advanced before its first
        /// input, or on which an input had already stepped, carry a tick of clock and no
        /// motion. Measured against a live server, replaying an 8-tick lead added <b>6
        /// steps</b> of travel the client had never taken, on every snapshot -- a forward
        /// shove fifteen times a second, constant in size, with the clock in step and every
        /// other counter clean.
        /// </para>
        /// <para>
        /// With a history the question becomes the one that has an answer: where did the
        /// client think it was at the tick the snapshot describes? The difference between
        /// that and the snapshot is the whole error, and everything the client did since is
        /// preserved by applying the same difference to its current position.
        /// </para>
        /// </remarks>
        private const int HistoryTicks = 256;

        private readonly Vec2[] _history = new Vec2[HistoryTicks];
        private readonly long[] _historyTick = new long[HistoryTicks];

        private void RecordHistory(long tick, in Vec2 position)
        {
            int i = (int)((tick % HistoryTicks + HistoryTicks) % HistoryTicks);
            _history[i] = position;
            _historyTick[i] = tick;
        }

        private bool TryReadHistory(long tick, out Vec2 position)
        {
            int i = (int)((tick % HistoryTicks + HistoryTicks) % HistoryTicks);
            if (_historyTick[i] == tick)
            {
                position = _history[i];
                return true;
            }

            position = Vec2.Zero;
            return false;
        }

        /// <summary>
        /// Shift every live history entry by the correction just applied, so the next
        /// comparison is made against a timeline that includes it.
        /// </summary>
        /// <remarks>
        /// Without this the same error is measured again on the following snapshot and
        /// corrected again — a correction that never converges, applied at the snapshot
        /// rate, which is exactly the artefact the history exists to remove.
        /// </remarks>
        private void ShiftHistory(float dx, float dy)
        {
            for (int i = 0; i < HistoryTicks; i++)
            {
                if (_historyTick[i] == 0) continue;
                _history[i] = new Vec2(_history[i].X + dx, _history[i].Y + dy);
            }
        }

        /// <summary>Reconciles answered by comparing at the snapshot's own tick.</summary>
        public int HistoryHits { get; private set; }

        /// <summary>Reconciles that fell back to replaying, the history not reaching back far enough.</summary>
        public int HistoryMisses { get; private set; }

        private Vec2 _tickStart;
        private long _tickStartTick;

        // _lastMoveTick as it stood before that first step. Replay seeds its own bookkeeping
        // from this, so a tick whose step was re-taken replays as one input step rather than
        // as a coalesce followed by a held step in the previous direction.
        private long _tickStartLastMove;
        private bool _seeded;
        private bool _baseTickSeeded; // true once SeedBaseTick has aligned _baseTick

        /// <summary>
        /// Creates a predictor. Check <see cref="IsEnabled"/> before using the result:
        /// unusable settings produce a predictor that refuses to predict rather than one
        /// that predicts badly.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Construct exactly one per local player, at the composition root, and inject
        /// it into everything that touches it.</b> <see cref="RecordInput"/> is called by
        /// whatever sends input; <see cref="Reconcile"/> is called by whatever consumes
        /// snapshots — a binder here, or a system in the DOTS package. Those must be the
        /// <i>same instance</i>.
        /// </para>
        /// <para>
        /// Two instances is the failure worth naming, because it is silent: the one that
        /// records inputs is never reconciled and drifts unbounded, while the one that
        /// reconciles has an empty buffer, replays nothing, and returns the authoritative
        /// position every time. Nothing throws, no counter looks wrong —
        /// <see cref="PendingCount"/> is legitimately zero on the second one — and the
        /// symptom is simply that prediction appears to do nothing. Someone then goes
        /// looking for a bug in the replay arithmetic, which is correct.
        /// </para>
        /// <para>
        /// So: register it in the DI scope and take it as a constructor dependency. Do not
        /// let a consumer construct its own alongside an injected one.
        /// </para>
        /// </remarks>
        public LocalMovePredictor(PredictionSettings settings)
        {
            _settings = settings;
            _speed = settings.Speed;
            IsEnabled = settings.IsUsable;
            _dt = IsEnabled ? MovementSystem.DeltaTimeForTickRate(settings.TickRate) : 0f;
            _maxCatchUpTicks = GameConstants.MaxBankedMovementTicks(settings.TickRate);

            // DeltaTimeForTickRate is the server's own helper, so this cannot disagree
            // with the server's dt for a tick rate both sides agree on. A zero here would
            // mean IsUsable let a non-positive rate through.
            if (_dt <= 0f)
            {
                IsEnabled = false;
            }
        }

        /// <summary>
        /// Whether this predictor will produce positions. When false, every method is a
        /// no-op, <see cref="Position"/> is whatever was last handed to
        /// <see cref="Reconcile"/>, and the caller must fall back to rendering the
        /// authoritative position directly.
        /// </summary>
        /// <remarks>
        /// A predictor that cannot reproduce the server's arithmetic must be absent, not
        /// approximate. An approximation drifts silently and reads as a network problem;
        /// an absence reads as "prediction is off", which is true and diagnosable.
        /// </remarks>
        public bool IsEnabled { get; }

        /// <summary>
        /// Position to render the local player at: the predicted position plus whatever
        /// remains of the last smoothed correction.
        /// </summary>
        public Vec2 Position
        {
            get
            {
                // Walk back the part of the latest step that has not been shown yet, so
                // the step is spread across the frames of an input interval instead of
                // landing on one of them. See the class remarks.
                float remaining = 1f - StepProgress;
                return new Vec2(
                    _predicted.X - _step.X * remaining + _renderOffset.X,
                    _predicted.Y - _step.Y * remaining + _renderOffset.Y);
            }
        }

        /// <summary>
        /// How much of the latest input's step has been rendered, 0..1. Saturates at 1 —
        /// which is what stops this from extrapolating; see the class remarks.
        /// </summary>
        private float StepProgress
        {
            get
            {
                float span = SmoothingSpan;
                if (span <= 0f) return 1f;
                float t = _sinceInput / span;
                return t >= 1f ? 1f : t < 0f ? 0f : t;
            }
        }

        /// <summary>
        /// Seconds to spread one step over: the interval at which inputs are actually
        /// arriving, falling back to the integration timestep until two have been seen.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why not the integration timestep.</b> Until 0.12.0 these were the same
        /// number, because the predictor was constructed with the client's own rate. Once
        /// the timestep came from the server's 60 Hz base tick while the client kept
        /// sending at its own slower cadence, spreading over the timestep showed the whole
        /// step in the first 16.7 ms and then froze the avatar for the remaining 50 —
        /// measured at 150 frozen frames out of 200. Correcting the tick rate made the
        /// visible stutter worse, and no correction counter could show it, because the
        /// simulation was exactly right and only the rendering was wrong.
        /// </para>
        /// <para>
        /// <b>Still interpolation.</b> The span changes; the bound does not. Progress
        /// saturates at 1, so the rendered position never passes the step that an input
        /// actually produced, however long the next one takes to arrive. A longer span
        /// makes the avatar reach the step later, never further.
        /// </para>
        /// <para>
        /// <b>Clamped below at the timestep</b> so a burst of inputs arriving closer
        /// together than the simulation advances cannot drive the span towards zero and
        /// reintroduce the jump this removes. Not clamped above: after a pause the first
        /// input restarts the measurement rather than inheriting the gap, so an
        /// arbitrarily long span cannot arise from idling.
        /// </para>
        /// </remarks>
        private float SmoothingSpan
        {
            get
            {
                // With a hold window the server steps every base tick, so the steps this
                // is spreading arrive one timestep apart no matter how slowly the client
                // sends. Using the input interval here would stretch each step across
                // four timesteps and leave the avatar permanently lagging its own
                // simulation — the 0.12.3 fix, applied where it is now wrong.
                //
                // <b>But "one timestep apart" is the steady case, not the only one.</b>
                // This returned _dt unconditionally, and a live measurement caught the
                // gap it does not cover: the tick immediately after an input is declined
                // by rule 1 — ApplyHeld's `heldFrom == baseTick` guard, because the input
                // already stepped that tick — so the next step lands one FULL timestep
                // after the following boundary, a gap of up to 2 * _dt. Spreading over
                // _dt then finishes the step part-way through the gap, StepProgress pins
                // at 1, and Position is bit-identical for the remainder: a still run at
                // the frame rate, on the one entity the player is controlling. It is
                // invisible to every correction counter because the SIMULATED position is
                // right throughout — only the rendered one stops.
                //
                // So the span follows the interval steps are actually arriving at. In
                // sustained movement that interval IS _dt and this is what it always was;
                // it only widens across the boundary that was freezing.
                // Unconditional since the hold stopped depending on a measured window.
                // The branch that stood here fell back to the interval between INPUTS when
                // no hold was configured, which was right then and is unreachable now:
                // steps arrive once per base tick whether or not a packet did, so the step
                // interval is always the interval to spread across. _inputInterval is kept
                // as a diagnostic -- it is what the send cadence reads out -- but it no
                // longer sizes anything the player sees.
                return _stepInterval > _dt ? _stepInterval : _dt;
            }
        }

        /// <summary>
        /// The measured interval between inputs, in seconds. Zero until two have been
        /// observed. Diagnostics: a value far from the client's send period means inputs
        /// are not being submitted at the cadence the client believes.
        /// </summary>
        public float ObservedInputInterval => _inputInterval;

        /// <summary>Predicted position with no smoothing applied. Diagnostics and tests.</summary>
        public Vec2 SimulatedPosition => _predicted;

        /// <summary>
        /// The outstanding correction still being blended out. Zero when the rendered and
        /// simulated positions have converged.
        /// </summary>
        /// <remarks>
        /// Kept from the parallel implementation in #25 — it is a genuinely useful
        /// diagnostic and costs nothing. Note that under this implementation
        /// <see cref="Position"/> also converges on <see cref="SimulatedPosition"/> once
        /// the step is fully shown, so the stronger assertion is available again and is
        /// what the tests use.
        /// </remarks>
        public Vec2 SmoothingOffset => _renderOffset;

        /// <summary>Inputs sent but not yet acknowledged.</summary>
        public int PendingCount => _count;

        /// <summary>Distance between the predicted position and the replayed one, last reconcile.</summary>
        public float LastCorrection { get; private set; }

        /// <summary>Reconciles whose correction exceeded <see cref="SmoothingThreshold"/>.</summary>
        public int Snaps { get; private set; }

        /// <summary>Reconciles absorbed into the render offset.</summary>
        public int SmoothedCorrections { get; private set; }

        /// <summary>Replay steps run. One per unacknowledged input per reconcile.</summary>
        public int ReplayedSteps { get; private set; }

        /// <summary>
        /// Times <see cref="Reconcile"/> has folded in an authoritative position.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="ReplayedSteps"/>, and the distinction matters: a
        /// reconcile with nothing pending replays nothing, so zero replay steps does not
        /// mean reconciliation is not running. Diagnosing that took a live run and an
        /// isolation experiment; this counter makes it readable directly.
        /// <para>
        /// The seeding call — the first <see cref="Reconcile"/> on a fresh predictor — is
        /// deliberately <b>not</b> counted. It adopts a starting position without
        /// comparing anything, so counting it would make a nonzero value mean "we
        /// initialised" rather than "a real reconcile happened".
        /// </para>
        /// </remarks>
        public int Reconciles { get; private set; }

        /// <summary>
        /// Inputs discarded because the buffer was full — the server has stopped
        /// acknowledging. Nonzero means predictions are running against a history that is
        /// missing its oldest entries and can no longer be fully replayed.
        /// </summary>
        public int DroppedInputs { get; private set; }

        /// <summary>
        /// Inputs refused because their tick did not advance. Mirrors the server, which
        /// ignores an input whose tick is not greater than the last one it accepted, so a
        /// client that repeats or reorders a tick predicts a move the server will not make.
        /// </summary>
        public int RejectedInputs { get; private set; }

        /// <summary>
        /// Inputs that arrived on a tick already stepped, and so moved nothing of their
        /// own. Mirrors the server coalescing several inputs in one tick to a single step.
        /// </summary>
        /// <remarks>
        /// Not a fault. A steady count at a send rate below the base tick rate would be
        /// odd and worth looking at; at or above it, it is the rule working.
        /// </remarks>
        public int CoalescedInputs { get; private set; }

        /// <summary>
        /// The integration timestep in seconds — the server's base tick period, from
        /// <c>MovementSystem.DeltaTimeForTickRate</c>. Diagnostics.
        /// </summary>
        public float IntegrationTimestep => _dt;

        /// <summary>
        /// The span one step is currently being spread across, in seconds.
        /// </summary>
        /// <remarks>
        /// <b>Read this against the interval steps actually arrive at.</b> If the span is
        /// shorter than that interval, the rendered position finishes the step and then
        /// holds — <see cref="RenderStepProgress"/> pins at 1 and <see cref="Position"/>
        /// goes exactly constant — for the remainder. If it is longer, the avatar
        /// permanently lags its own simulation. Neither shows up in any correction
        /// counter, because the simulated position is correct in both cases; only the
        /// rendered one is wrong.
        /// </remarks>
        public float EffectiveSmoothingSpan => SmoothingSpan;

        /// <summary>
        /// How much of the current step has been rendered, 0..1. At 1 the rendered
        /// position has caught up with the simulated one and stops moving.
        /// </summary>
        public float RenderStepProgress => StepProgress;

        /// <summary>
        /// Whether the held direction is still inside the server's hold window. False
        /// means the predictor has stopped integrating it and nothing will move the
        /// rendered position until the next input or snapshot.
        /// </summary>
        public bool HoldIsActive =>
            _heldFrom != 0 && _baseTick - _heldFrom <= MaxBankedTicks;

        /// <summary>
        /// The simulation rate this predictor was built with — the server's advertised rate,
        /// not one measured off the wire.
        /// </summary>
        /// <remarks>
        /// Exposed because a measurement that converts ticks to seconds must use the rate the
        /// SERVER stamped those ticks at. Using an estimated rate instead makes the
        /// conversion drift against the wall clock at the estimate's error: a 57.7 Hz
        /// estimate of a 60 Hz server drifts four percent, which took
        /// <c>SnapshotStalenessEstimator</c> from a stable reading to 613 ticks and climbing
        /// in under a minute.
        /// </remarks>
        public int TickRateHz => _settings.TickRate;

        /// <summary>Base ticks <see cref="Advance"/> has stepped.</summary>
        public int BaseTicksAdvanced { get; private set; }

        /// <summary>
        /// How many base ticks one <see cref="Advance"/> call may consume before the rest of
        /// the frame's elapsed time is discarded.
        /// </summary>
        /// <remarks>
        /// The same silence-timeout budget the hold window uses
        /// (<see cref="GameConstants.MaxBankedMovementMs"/>), and for the same reason: past
        /// a quarter second the client has stalled rather than run slowly, and simulating
        /// through the stall invents motion the server has no input for.
        /// </remarks>
        public int MaxCatchUpTicks => _maxCatchUpTicks;

        /// <summary>Frames whose elapsed time was clamped by <see cref="MaxCatchUpTicks"/>.</summary>
        /// <remarks>
        /// Nonzero at startup is normal -- the first frames of a scene load take seconds.
        /// Nonzero in steady state means the client is hitching, and every one of those
        /// hitches used to become a permanent step of prediction lead.
        /// </remarks>
        public int ClampedFrames { get; private set; }

        /// <summary>Total wall-clock seconds discarded by that clamp.</summary>
        public float DiscardedCatchUpSeconds { get; private set; }

        /// <summary>
        /// Base ticks on which the held direction actually moved the entity.
        /// </summary>
        /// <remarks>
        /// Compare against <see cref="BaseTicksAdvanced"/>. The server integrates the held
        /// direction on every base tick inside the window, so during sustained movement
        /// these should track each other closely. The shortfall between the two is the
        /// declined ticks, split by reason across the six <c>Skip*</c> counters; a large
        /// share there means the rendered position is being refreshed far less often than
        /// once per tick, whatever the hold window says it should be.
        /// </remarks>
        public int HeldStepsApplied { get; private set; }

        /// <summary>
        /// The predictor's current base tick — the same timeline the server's
        /// <c>WorldState.Tick</c> is on, once <see cref="SeedBaseTick"/> has aligned them.
        /// </summary>
        /// <remarks>
        /// Public because <see cref="SeedBaseTick"/> is: a consumer writing its own view
        /// binding has to call that itself (see the remarks there), and this is how it
        /// confirms the epoch took rather than discovering months later that its binder
        /// never seeded. It is also what makes a client-side prediction log line up
        /// against a server-side one — both name the same tick number.
        /// </remarks>
        public long BaseTick => _baseTick;

        /// <summary>
        /// Records an input that has just been sent and applies it to the predicted
        /// position immediately. Call with the same tick and vector handed to
        /// <c>SendInput</c>.
        /// </summary>
        /// <remarks>
        /// Applied on send rather than on acknowledgement — that immediacy is the entire
        /// point. The tick must strictly increase, matching the server's monotonic
        /// <c>InputCursor</c> check; a repeated tick is counted and dropped rather than
        /// predicted, because the server will drop it too.
        /// </remarks>
        public void RecordInput(long tick, float moveX, float moveY)
        {
            if (!IsEnabled || !_seeded)
            {
                return;
            }

            if (tick <= _lastRecordedTick)
            {
                RejectedInputs++;
                return;
            }

            _lastRecordedTick = tick;

            if (_count == Capacity)
            {
                // Drop the oldest. It cannot be replayed any more, so the predicted
                // position is now an estimate the next snapshot will have to correct.
                _head = (_head + 1) % Capacity;
                _count--;
                DroppedInputs++;
            }

            // Decided before the buffer store, because the store carries LastMoveTickBefore
            // and replay reads it to tell one input on a tick from a second one.
            bool coalesced = _lastMoveTick == _baseTick;
            bool replacesHeldStep = coalesced && _tickStartTick == _baseTick;

            int slot = (_head + _count) % Capacity;
            _pending[slot] = new PendingInput(
                tick, moveX, moveY, _baseTick,
                // For a replacement, the tick as it stood before the HOLD stepped it. Storing
                // _lastMoveTick -- which this input is about to overwrite with _baseTick
                // anyway -- makes replay read the tick as already stepped, so it coalesces the
                // input away and then applies the hold in the PREVIOUS direction. Live and
                // replay then diverge by one step per input and the correction accumulates
                // instead of converging: measured at 120 steps over 30 snapshots.
                replacesHeldStep ? _tickStartLastMove : _lastMoveTick);
            _count++;

            // Whatever of the previous step was still unshown would otherwise vanish
            // when the new step replaces it. Carry it into the render offset so the
            // visible position does not jump on an input boundary.
            Vec2 before = Position;

            Vec2 previous = _predicted;

            // Rule 1: at most one step per player per server tick. The server coalesces
            // inputs landing in the same tick to the newest and moves once; a client that
            // moves for each of them travels further than the server for the same input,
            // which is the thing rule 1 exists to prevent.
            //
            // Here the tick may already have been stepped by the hold, because Advance
            // and RecordInput are driven by the frame loop and the send loop separately
            // and can fall either way round within one tick. The server never has that
            // ambiguity — it drains inputs and then applies holds, in that order, inside
            // one tick — so it guards on the hold side and this guards on both.
            MoveResult verdict;
            if (replacesHeldStep)
            {
                CoalescedInputs++;

                // RULE 1 IS "ONE STEP PER TICK", NOT "FIRST STEP WINS". The server drains a
                // tick's inputs and then applies holds, so on a tick that received an input
                // the step is the INPUT's; the hold stands down. The client cannot order the
                // two -- Advance runs on the frame loop and RecordInput on the send loop, and
                // either can reach a given tick first -- so when the hold got there first,
                // the tick's one step is re-taken from where the tick began, with this
                // input's direction. Same count, same origin, same arithmetic: one step, and
                // the newest direction, exactly as the server has it.
                //
                // Discarding the input instead is what the plain coalescing branch did, and
                // it applied the PREVIOUS direction to a tick the server turned on: a
                // direction change landed one base tick late for a client sending at the
                // base rate, and the live path disagreed with Reconcile's own replay, which
                // has always run inputs before holds. It cost nothing while the hold expired
                // on exactly the tick the next input arrived on; widening the window to the
                // silence timeout is what made the two paths meet.
                verdict = StepResult(_tickStart, moveX, moveY, _dt, out Vec2 replaced);

                // Including a STOP, which rolls the tick back to where it began: a deadzone
                // input means the server moved that tick not at all, so neither may the
                // client. Leaving the held step standing instead was tried and is wrong --
                // it leaves the client a step ahead on every release, and "every release"
                // is the most common thing a player does, so the correction is not the
                // one-off it looks like.
                //
                // It is not a visible backward jump. Position is _predicted offset by
                // _renderOffset, and the carry at the end of this method folds the whole
                // rollback into that offset, which decays over the next few frames. The
                // rendered avatar eases; only the simulated position moves at once.
                _predicted = verdict is MoveResult.Accepted or MoveResult.Clamped
                    ? replaced
                    : _tickStart;
            }
            else if (coalesced)
            {
                // Two real inputs on one tick: the server keeps the newest for movement and
                // moves once, so the second one moves nothing. Its VERDICT still matters --
                // an explicit stop has to clear the hold even when it is not the newest in
                // the batch, which InputHandler.ProcessInput guards explicitly and this is
                // the client's half of. Asked of MovementSystem rather than re-tested
                // against the deadzone constant here: a second copy of that threshold is
                // free to drift from the server's.
                CoalescedInputs++;
                verdict = StepResult(_predicted, moveX, moveY, _dt, out _);
                if (verdict is MoveResult.Clamped) verdict = MoveResult.Accepted;
            }
            else
            {
                _tickStart = _predicted;
                _tickStartTick = _baseTick;
                _tickStartLastMove = _lastMoveTick;

                verdict = StepResult(
                    _predicted, moveX, moveY,
                    StepDeltaTime(_baseTick, _lastMoveTick, _heldFrom),
                    out _predicted);
            }

            // A coalesced input produced no displacement, so it must not touch the render
            // state: _step would become zero and _sinceInput would restart, which freezes
            // the rendered position for the remainder of the tick while the step the HOLD
            // took on it is still mid-interpolation. Measured as a render lag rising from
            // 1.00 to 2.04 steps on the tick after every send, decaying back over the
            // interval -- the avatar visibly hitching once per send.
            //
            // It was unreachable before: the hold expired on exactly the tick the next
            // input arrived on, so an input never landed on a tick the hold had stepped.
            // Widening the window to the silence timeout is what made these two paths meet.
            if (replacesHeldStep)
            {
                // Measured from where the TICK began, not from `previous`. `previous` is the
                // position after the hold's step, so the difference between the two is near
                // zero -- exactly zero when the direction did not change -- and a zero step
                // tells the renderer the tick produced no motion. It produced a full one:
                // the hold's step, re-aimed. Reporting the difference stalled the rendered
                // position for the rest of every send interval, measured as the lag rising
                // from 1.00 to 1.95 steps and decaying back, once per send.
                //
                // _sinceInput is deliberately NOT reset with it: the step this replaces is
                // already part-shown, and restarting its clock would show it twice.
                _step = new Vec2(_predicted.X - _tickStart.X, _predicted.Y - _tickStart.Y);
            }
            else if (!coalesced)
            {
                _step = new Vec2(_predicted.X - previous.X, _predicted.Y - previous.Y);
            }

            // Set or clear the hold on the server's rule: a direction becomes held only
            // after a step it actually produced, and an explicit stop clears it at once.
            // That asymmetry is deliberate server-side — "released the stick" must halt
            // the entity now, while "went quiet" is what the window exists to cover — so
            // predicting it wrongly makes a release feel sticky, which is the one artefact
            // a player attributes directly to their own input.
            if (verdict is MoveResult.Accepted or MoveResult.Clamped)
            {
                _heldX = moveX;
                _heldY = moveY;
                _heldFrom = _baseTick;
                _lastMoveTick = _baseTick;
            }
            else if (verdict == MoveResult.None)
            {
                // An explicit stop. Both fields move, exactly as
                // InputHandler.ProcessInput does on a deadzone vector: it clears
                // HeldFromTick AND stamps LastMoveTick with the current tick, in the
                // pre-check and again in the MoveResult.None branch. The
                // comment that used to stand here claimed the reverse of what the server
                // does, and leaving LastMoveTick stale is what let an idle bank time: the
                // next real input then covered MaxBankedTicks worth of distance where the
                // server covered one, which was the whole of issue #12. Banking is gone from
                // both sides, so that particular amplification cannot recur -- but clearing
                // _heldFrom still decides whether the pause is coasted through at all, which
                // is the difference between a player who released the stick and one whose
                // packets stopped arriving.
                //
                // A Rejected vector still leaves both alone, matching the server, which
                // logs and drops it without disturbing state.
                _heldFrom = 0;
                _lastMoveTick = _baseTick;
            }

            // Measure the gap to the previous input. A gap far longer than the ones
            // before it is a pause, not a cadence: restart the measurement rather than
            // smear the next step across the length of the idle.
            float gap = _elapsed - _lastInputAt;
            if (_lastInputAt > 0f || _elapsed > 0f)
            {
                bool pause = _inputInterval > 0f && gap > _inputInterval * 4f;
                _inputInterval = pause || gap <= 0f ? 0f : gap;
            }

            _lastInputAt = _elapsed;

            if (!coalesced)
            {
                _sinceInput = 0f;

                // An input that moved the entity is a step like any other, and the gap from
                // it to the next one is exactly the gap that was freezing. A coalesced one
                // moved nothing, and reporting it as a step feeds NoteStep a zero gap, which
                // discards the measured cadence for no reason.
                if (verdict is MoveResult.Accepted or MoveResult.Clamped)
                {
                    NoteStep();
                }
            }

            Vec2 after = Position;
            _renderOffset = new Vec2(
                _renderOffset.X + (before.X - after.X),
                _renderOffset.Y + (before.Y - after.Y));
        }

        /// <summary>
        /// Folds in the server's authoritative position for the local player: drop
        /// acknowledged inputs, rewind, replay the rest.
        /// </summary>
        /// <param name="authoritative">Local player's position from the newest snapshot.</param>
        /// <param name="ackTick">
        /// The snapshot's <c>AckTick</c> — the newest input tick the server accepted.
        /// Everything at or below it is already reflected in
        /// <paramref name="authoritative"/> and must not be replayed on top of it.
        /// </param>
        /// <remarks>
        /// The first call seeds the predictor and produces no correction: there is nothing
        /// to compare against, and treating an initial spawn position as a correction
        /// would count a snap that never happened.
        /// </remarks>
        public void Reconcile(Vec2 authoritative, long ackTick) =>
            Reconcile(authoritative, ackTick, NoServerTick);

        /// <summary>Sentinel for "the caller did not tell us when the snapshot was produced".</summary>
        private const long NoServerTick = long.MinValue;

        /// <summary>
        /// As <see cref="Reconcile(Vec2,long)"/>, plus the base tick the snapshot was
        /// produced on — which is what lets the prediction lead survive an acknowledgement
        /// that empties the pending buffer (#53).
        /// </summary>
        /// <param name="serverBaseTick">
        /// The tick the authoritative position is FROM, in the same space as
        /// <see cref="BaseTick"/>. <see cref="SeedBaseTick"/> puts the two clocks in one
        /// space; without that seeding this value is meaningless and should not be passed.
        /// </param>
        /// <remarks>
        /// <para>
        /// An overload rather than a changed signature: the two-argument form is a
        /// cross-package contract enforced by <c>PredictionSurfaceContractTests</c> and
        /// driven by <c>com.cuvara.dots</c>. Callers that cannot supply the tick keep
        /// today's behaviour exactly, including today's defect.
        /// </para>
        /// <para>
        /// <b>Why the tick is genuinely needed.</b> The obvious cheaper fix — anchoring the
        /// replay to the acknowledged input's own base tick, which the predictor already
        /// holds — was implemented and measured. It rebuilds the lead correctly and then
        /// over-replays whenever the snapshot already includes the hold window, producing a
        /// phantom correction as large as the defect: 3 steps where there was no lead at
        /// all. The anchor supplies a start; only the snapshot's tick supplies the end.
        /// </para>
        /// </remarks>
        public void Reconcile(Vec2 authoritative, long ackTick, long serverBaseTick)
        {
            if (!IsEnabled)
            {
                _predicted = authoritative;
                _renderOffset = Vec2.Zero;
                _step = Vec2.Zero;
                return;
            }

            if (!_seeded)
            {
                _seeded = true;
                _predicted = authoritative;
                _renderOffset = Vec2.Zero;
                _step = Vec2.Zero;
                _sinceInput = 0f;
                LastCorrection = 0f;
                return;
            }

            Reconciles++;

            Vec2 before = _predicted;

            // COMPARE AT THE SNAPSHOT'S OWN TICK, when the history reaches it.
            //
            // The snapshot describes one tick; the client is past it. Comparing it against
            // where the client is NOW compares two moments, and the difference then has to
            // be manufactured -- by replaying the gap forward from the server's answer,
            // which is all a predictor without a history can do. That extrapolation is wrong
            // whenever the lead is clock phase rather than travel: measured live, an 8-tick
            // lead had the reconcile add 6 steps the client had never taken, every snapshot.
            //
            // With the history the comparison is like for like. Everything the client did
            // after that tick is kept by construction -- the same offset is applied to the
            // current position -- so there is nothing to replay and nothing to guess.
            if (serverBaseTick != NoServerTick && serverBaseTick > 0
                && TryReadHistory(serverBaseTick, out Vec2 thenPredicted))
            {
                HistoryHits++;
                DropAcknowledged(ackTick);

                float ex = authoritative.X - thenPredicted.X;
                float ey = authoritative.Y - thenPredicted.Y;

                if (ex != 0f || ey != 0f)
                {
                    _predicted = new Vec2(_predicted.X + ex, _predicted.Y + ey);
                    ShiftHistory(ex, ey);

                    // The current tick's start marker moves with it. RecordInput's
                    // replacement path re-takes the tick's step FROM that marker, so a
                    // marker captured before this correction rolls the correction straight
                    // back out on the next input -- measured as the prediction moving
                    // backwards between snapshots while the reconcile insisted it had just
                    // been put right, a correction applied and undone fifteen times a
                    // second.
                    if (_tickStartTick == _baseTick)
                    {
                        _tickStart = new Vec2(_tickStart.X + ex, _tickStart.Y + ey);
                    }
                }

                ApplyCorrectionToRender(before);
                return;
            }

            if (serverBaseTick != NoServerTick && serverBaseTick > 0)
            {
                HistoryMisses++;
            }

            DropAcknowledged(ackTick);

            // Rewind to what the server says, then re-run the base-tick timeline it has
            // not seen. Not one step per pending input: the server integrates the held
            // direction on every base tick in the window, including ticks where no packet
            // arrived, so replaying input-by-input reproduces a quarter of its motion at
            // a 15 Hz send rate against a 60 Hz base tick.
            Vec2 replayed = authoritative;

            if (_count > 0)
            {
                long heldFrom = 0;
                float heldX = 0f, heldY = 0f;
                var next = 0;

                // Replay banks time exactly as the live path did, seeded from the value
                // that was actually in effect when the first surviving input was recorded.
                long replayLastMove = _pending[_head].LastMoveTickBefore;

                // START AT THE SNAPSHOT'S OWN TICK, not at the first input the server has
                // not seen. `authoritative` is where the entity was on `serverBaseTick`;
                // beginning the loop at the first pending input's tick silently skips every
                // tick in between, and the held motion the server took over them is dropped.
                //
                // That gap is not an edge case: inputs go at 15 Hz against a 60 Hz base
                // tick, and an acknowledgement normally lands on the snapshot that carries
                // it, so the gap is one input interval EVERY reconcile. Measured against a
                // live server it read as a correction of exactly 4.01 steps, snapshot after
                // snapshot, with the clock in step and every other counter clean -- a
                // constant backward tug at the snapshot rate, which is what a player calls
                // jerky. Occasionally 12 steps, when two snapshots collapsed into one frame.
                //
                // The direction to hold over that stretch is the newest ACKNOWLEDGED input:
                // the server holds the last direction it accepted, and by definition that is
                // the input the ack names. DropAcknowledged keeps it for exactly this.
                long start = _pending[_head].BaseTick;
                if (serverBaseTick != NoServerTick
                    && serverBaseTick > 0
                    && serverBaseTick < start
                    && _haveLastAcked)
                {
                    start = serverBaseTick + 1;
                    heldX = _lastAcked.MoveX;
                    heldY = _lastAcked.MoveY;
                    heldFrom = _lastAcked.BaseTick;
                    replayLastMove = serverBaseTick;
                }

                for (long t = start; t <= _baseTick; t++)
                {
                    // WHICH OF THE TWO GOT TO THIS TICK FIRST, live. Normally inputs step
                    // first and the hold stands down, exactly as ProcessInput runs before
                    // ApplyHeldMovement within a server tick. But the live client cannot
                    // order the two -- Advance is the frame loop and RecordInput the send
                    // loop -- and when the hold arrived first, RecordInput re-took the
                    // tick's step from where the tick began. Replay has to reproduce THAT
                    // ordering or it lands somewhere the live path never was, and every
                    // reconcile then injects a correction the network did not cause.
                    //
                    // The buffer records it: an input whose LastMoveTickBefore is its own
                    // base tick was recorded onto a tick something had already stepped.
                    bool holdWentFirst =
                        next < _count
                        && _pending[(_head + next) % Capacity].BaseTick == t
                        && _pending[(_head + next) % Capacity].LastMoveTickBefore == t;

                    Vec2 tickStart = replayed;
                    var heldStepped = false;

                    if (holdWentFirst)
                    {
                        heldStepped = ApplyHeld(
                            ref replayed, t, heldFrom, heldX, heldY, ref replayLastMove);
                        if (heldStepped) ReplayedSteps++;
                    }

                    var stepped = false;
                    while (next < _count && _pending[(_head + next) % Capacity].BaseTick == t)
                    {
                        var input = _pending[(_head + next) % Capacity];
                        MoveResult verdict;
                        if (heldStepped)
                        {
                            // The replacement, live-identical: one step, from where the tick
                            // began, in this input's direction -- and if the vector is a stop
                            // or the model refuses it, the held step stands rather than being
                            // rolled back. See RecordInput for why that trade is made.
                            verdict = StepResult(tickStart, input.MoveX, input.MoveY, _dt,
                                out Vec2 replacedStep);
                            replayed = verdict is MoveResult.Accepted or MoveResult.Clamped
                                ? replacedStep
                                : tickStart;
                        }
                        else if (replayLastMove == t)
                        {
                            // Already stepped this tick by an earlier INPUT — rule 1, as
                            // above: the server keeps the newest for movement and moves once.
                            verdict = MoveResult.Accepted;
                        }
                        else
                        {
                            verdict = StepResult(
                                replayed, input.MoveX, input.MoveY,
                                StepDeltaTime(t, replayLastMove, heldFrom), out replayed);
                        }
                        ReplayedSteps++;
                        stepped = true;

                        if (verdict is MoveResult.Accepted or MoveResult.Clamped)
                        {
                            heldX = input.MoveX;
                            heldY = input.MoveY;
                            heldFrom = t;
                            replayLastMove = t;
                        }
                        else if (verdict == MoveResult.None)
                        {
                            heldFrom = 0;
                            replayLastMove = t;
                        }

                        next++;
                    }

                    if (!stepped && !heldStepped &&
                        ApplyHeld(ref replayed, t, heldFrom, heldX, heldY, ref replayLastMove))
                    {
                        ReplayedSteps++;
                    }
                }
            }
            else if (serverBaseTick != NoServerTick && serverBaseTick < _baseTick)
            {
                // Nothing outstanding, but the client has held its direction forward since the
                // tick the snapshot was produced on. That motion is the prediction lead, and
                // dropping it produced a backward pull at snapshot rate whose size tracked the
                // acknowledgement interval rather than any disagreement (#53).
                //
                // Two things are deliberately NOT replayed here, and each is a measured
                // failure of an earlier attempt:
                //
                //  - the acknowledged input's own step. The server applied it, which is what
                //    "acknowledged" means, so it is already in `authoritative`.
                //  - anything at or before `serverBaseTick`. The snapshot already includes it.
                //    Anchoring to the acknowledged INPUT's tick instead of the snapshot's put
                //    3 steps of phantom correction on a case with no lead at all.
                //
                // The held state is the live one, because no input has arrived since: these
                // are exactly the ticks Advance integrated, replayed with the same guards so
                // the hold window expires at the same place.
                long replayLastMove = serverBaseTick;

                for (long t = serverBaseTick + 1; t <= _baseTick; t++)
                {
                    if (ApplyHeld(ref replayed, t, _heldFrom, _heldX, _heldY, ref replayLastMove))
                    {
                        ReplayedSteps++;
                    }
                }
            }

            // Same reasoning as the history path: a tick-start marker captured before this
            // rewind would undo it on the next input.
            if (_tickStartTick == _baseTick)
            {
                _tickStart = new Vec2(
                    _tickStart.X + (replayed.X - _predicted.X),
                    _tickStart.Y + (replayed.Y - _predicted.Y));
            }

            _predicted = replayed;

            // The replayed timeline replaces the history for those ticks: the next snapshot
            // must be compared against what the client believes NOW, not against what it
            // believed before this rewind.
            RecordHistory(_baseTick, _predicted);

            ApplyCorrectionToRender(before);
        }

        /// <summary>
        /// Hide the jump just applied to <see cref="SimulatedPosition"/>, or accept that it
        /// is too large to hide.
        /// </summary>
        /// <param name="before">Where the prediction stood before the correction.</param>
        /// <remarks>
        /// Called by both reconcile paths -- the history comparison and the replay fallback
        /// -- because how a correction is SHOWN is a property of its size and nothing else.
        /// Two copies of this decision is how the two paths would come to disagree about
        /// what a snap is.
        /// </remarks>
        private void ApplyCorrectionToRender(in Vec2 before)
        {
            float dx = before.X - _predicted.X;
            float dy = before.Y - _predicted.Y;

            // Measured here, from the move actually made, so both paths report the same
            // thing: how far the simulated position just jumped. Computing it separately per
            // path is how the two came to disagree about what a snap is -- the replay path
            // stopped reporting one at all when this was factored out.
            LastCorrection = new Vec2(dx, dy).Magnitude;

            if (LastCorrection > SmoothingThreshold)
            {
                // Too far to hide. Showing the avatar gliding from a place the server has
                // already ruled out is worse than one honest jump.
                //
                // The in-step remainder goes too: it belongs to a step taken from a
                // position that has just been ruled out, so replaying it from the
                // corrected one would add a second, smaller wrong movement after the snap.
                _renderOffset = Vec2.Zero;
                _step = Vec2.Zero;
                Snaps++;
            }
            else if (LastCorrection > 0f)
            {
                // Carry the whole error as a render offset and retire it over the next few
                // frames, so the simulated position is authoritative-correct immediately
                // while the visible one catches up.
                //
                // Added to the existing offset rather than replacing it: with in-step
                // smoothing there can be an outstanding remainder from an input boundary,
                // and overwriting it would discard part of a step mid-flight — a small
                // jump at snapshot rate, which is the exact artefact this release is
                // removing.
                _renderOffset = new Vec2(_renderOffset.X + dx, _renderOffset.Y + dy);
                SmoothedCorrections++;
            }
        }

        /// <summary>
        /// Advances the smoothing of the outstanding correction. Call once per rendered
        /// frame with that frame's delta time.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Reconcile"/> because corrections arrive at the
        /// snapshot rate (~15 Hz) and are consumed at the frame rate. Decay is
        /// <c>offset *= pow(base, dt)</c>, which retires the same fraction per second at
        /// any frame rate — a per-frame multiplier would make the correction visibly
        /// slower on a slower machine.
        /// </remarks>
        public void Advance(float deltaTime)
        {
            if (!IsEnabled || deltaTime <= 0f)
            {
                return;
            }

            // Always advanced, even with no correction outstanding: this is what makes
            // the rendered position move between inputs at all.
            _sinceInput += deltaTime;
            _elapsed += deltaTime;

            // Run whole base ticks. The server integrates the held direction once per
            // base tick whether or not a packet arrived (TickLoop calls ApplyHeldMovement
            // on empty ticks too), so a client that only moves when it sends an input
            // reproduces a quarter of the server's motion at a 15 Hz send rate against a
            // 60 Hz base tick. Stepping here is what closes that.
            if (_dt > 0f)
            {
                _tickAccumulator += deltaTime;

                // A SINGLE FRAME MAY NOT MANUFACTURE AN UNBOUNDED NUMBER OF TICKS.
                //
                // deltaTime is whatever the last frame took, and at startup that is
                // seconds: scene load, subscene streaming, shader warmup, the first DOTS
                // world coming up. Without this clamp the accumulator carries all of it and
                // the loop below burst-advances hundreds of base ticks in one frame --
                // stepping the held direction on every one of them.
                //
                // Measured against a live server, a player that had been running for a
                // minute sat 355 base ticks -- 5.9 SECONDS -- ahead of the server's own
                // tick, at a constant offset, with both clocks otherwise at a matched 60Hz.
                // That is not drift, it is a startup burst that never gets given back: the
                // reconcile's lead replay is bounded by the hold window, so a lead that far
                // outside it cannot be replayed and every snapshot lands as a large
                // correction instead. Snaps ran at 1 to 2 per second, which is exactly what
                // the player reports as the avatar jerking while it moves.
                //
                // The budget is the silence timeout, for the same reason it bounds the hold:
                // past a quarter second the client has stalled, and motion invented for a
                // frame that never rendered is motion the server has no input for. Time over
                // the budget is DISCARDED rather than carried -- carrying it is the same
                // burst one frame later.
                float maxAccumulated = _dt * _maxCatchUpTicks;
                if (_tickAccumulator > maxAccumulated)
                {
                    DiscardedCatchUpSeconds += _tickAccumulator - maxAccumulated;
                    ClampedFrames++;
                    _tickAccumulator = maxAccumulated;
                }

                while (_tickAccumulator >= _dt)
                {
                    _tickAccumulator -= _dt;
                    _baseTick++;

                    BaseTicksAdvanced++;

                    Vec2 before = _predicted;
                    if (ApplyHeld(ref _predicted, _baseTick))
                    {
                        _step = new Vec2(_predicted.X - before.X, _predicted.Y - before.Y);
                        _sinceInput = 0f;
                        NoteStep();
                        HeldStepsApplied++;
                    }

                    RecordHistory(_baseTick, _predicted);
                }
            }

            if (_renderOffset.X == 0f && _renderOffset.Y == 0f)
            {
                return;
            }

            float factor = (float)System.Math.Pow(OffsetDecayPerSecond, deltaTime);
            float x = _renderOffset.X * factor;
            float y = _renderOffset.Y * factor;

            // Settle exactly, so Position stops changing rather than approaching forever.
            const float epsilon = 1e-4f;
            if (System.Math.Abs(x) < epsilon && System.Math.Abs(y) < epsilon)
            {
                _renderOffset = Vec2.Zero;
                return;
            }

            _renderOffset = new Vec2(x, y);
        }

        /// <summary>
        /// Adopts the server's speed for the local entity, as carried on the snapshot
        /// (<c>wire.proto</c> field 9).
        /// </summary>
        /// <param name="speed">
        /// The value from the snapshot. <b>Non-positive is ignored</b>, because on the
        /// wire that means "not sent" rather than "immobile": proto3 elides a zero float,
        /// so a server predating the field is indistinguishable from a stationary entity.
        /// Accepting a zero would pin the predicted speed to zero against an older server
        /// and the local player would stop moving — worse than the drift this exists to
        /// fix.
        /// </param>
        /// <remarks>
        /// <para>
        /// Separate from <see cref="Reconcile"/> rather than an extra parameter on it, and
        /// deliberately so: that signature is a cross-package contract enforced by
        /// <c>PredictionSurfaceContractTests</c> and driven from <c>com.cuvara.dots</c>,
        /// whose compiler errors cannot appear in this repository. Adding a method breaks
        /// nobody; widening an existing one breaks a consumer with no signal here.
        /// </para>
        /// <para>
        /// Until this is called, the speed is whatever <see cref="PredictionSettings"/>
        /// was constructed with — which remains the correct fallback for a server that
        /// does not send the field.
        /// </para>
        /// </remarks>
        public void SetServerSpeed(float speed)
        {
            if (speed > 0f && !float.IsNaN(speed) && !float.IsInfinity(speed))
            {
                _speed = speed;
            }
        }

        /// <summary>
        /// Seeds the predictor's base-tick counter from the server's world tick, so the
        /// two timelines share the same epoch and the hold window's phase aligns.
        /// </summary>
        /// <param name="serverTick">
        /// The server's current base tick, as carried on the snapshot
        /// (<c>WorldState.Tick</c>). Must be positive; zero or negative is ignored.
        /// </param>
        /// <remarks>
        /// <para>
        /// Takes effect once: the first call with a positive value sets
        /// <see cref="_baseTick"/> and clears <see cref="_tickAccumulator"/>. Subsequent
        /// calls are no-ops — the accumulator-driven clock in <see cref="Advance"/> is
        /// the authority after seeding, and re-seeding on every snapshot would fight it.
        /// </para>
        /// <para>
        /// Without this, <c>_baseTick</c> starts at 1 while the server's
        /// <c>current_tick</c> is in the hundreds of thousands. The absolute values do
        /// not matter — <c>StepDeltaTime</c> and <c>ApplyHeld</c> use differences — but
        /// the <b>phase</b> does: the hold window is <c>MaxBankedTicks</c> base ticks, and
        /// where each clock's tick boundary falls relative to an input changes how many
        /// held steps get applied between inputs. On localhost with matched rates and no
        /// loss, this showed as 17 of 20 samples needing a correction of exactly 2 steps
        /// (issue #13).
        /// </para>
        /// <para>
        /// Separate from <see cref="Reconcile"/> for the same reason as
        /// <see cref="SetServerSpeed"/>: that signature is a cross-package contract
        /// enforced by <c>PredictionSurfaceContractTests</c>.
        /// </para>
        /// </remarks>
        public void SeedBaseTick(long serverTick)
        {
            if (_baseTickSeeded || serverTick <= 0)
            {
                return;
            }

            _baseTickSeeded = true;
            _baseTick = serverTick;
            _tickAccumulator = 0f;
        }

        /// <summary>
        /// How far ahead of the newest snapshot's tick the client's clock is, in base
        /// ticks, as of the last <see cref="SteerToServerTick"/>. 0 before the first one.
        /// </summary>
        public long TickError { get; private set; }

        /// <summary>Times the clock was hard-resynchronised rather than steered.</summary>
        public int HardResyncs { get; private set; }

        /// <summary>
        /// Beyond this much error the clock is set rather than steered — two seconds at the
        /// simulation rate.
        /// </summary>
        private int HardResyncTicks => _settings.TickRate * 2;

        /// <summary>
        /// Pull the client's base-tick clock toward the server's, given the tick of the
        /// newest snapshot and how far ahead of it the client is meant to run.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Seeding once is not enough, and the gap it leaves is not small.</b>
        /// <see cref="SeedBaseTick"/> aligns the two clocks at join and never speaks again,
        /// so from then on the client counts base ticks off its own wall clock while the
        /// server counts them off its own. Any difference — a real rate difference, a frame
        /// the client spent stalled, or simply snapshots arriving slower than the server
        /// emits them — accumulates with nothing to bound it.
        /// </para>
        /// <para>
        /// <b>Measured against a live server</b>, on a build where every other counter was
        /// clean: the client's tick sat <b>456 ticks — 7.6 seconds —</b> past the newest
        /// snapshot it held, and still climbing, with <c>Snaps=0</c> and
        /// <c>LastCorrection=0.0000</c> throughout. That silence is the trap: the
        /// three-argument <see cref="Reconcile"/> replays everything between the snapshot's
        /// tick and the client's as legitimate prediction lead, so a clock running away
        /// produces no correction at all. The local player looks perfect while the server —
        /// and therefore every other player — has it seconds behind where its own screen
        /// shows it.
        /// </para>
        /// <para>
        /// <b>Steered, not jumped.</b> The correction is a bounded nudge to the tick
        /// accumulator, so the clock runs a few percent fast or slow until the error is
        /// gone. A jump would move <see cref="BaseTick"/> out from under the pending inputs,
        /// the held-from tick and the last-moved tick all at once, and every one of those is
        /// compared against it. Only a gap too large to steer out in reasonable time is set
        /// outright, and that path drops the pending buffer with it because those inputs
        /// refer to ticks that no longer exist.
        /// </para>
        /// </remarks>
        /// <param name="serverTick">Base tick the newest snapshot was produced on.</param>
        /// <param name="targetLeadTicks">
        /// Base ticks the client should run ahead of that: the snapshot is already one
        /// interval plus half a round trip old by the time it is read, and predicting to
        /// "now" means covering that.
        /// </param>
        public void SteerToServerTick(long serverTick, int targetLeadTicks)
        {
            if (!IsEnabled || !_baseTickSeeded || serverTick <= 0)
            {
                return;
            }

            if (targetLeadTicks < 0) targetLeadTicks = 0;

            long target = serverTick + targetLeadTicks;
            long error = _baseTick - target;
            TickError = error;

            if (error == 0)
            {
                return;
            }

            if (error > HardResyncTicks || error < -HardResyncTicks)
            {
                // Too far to walk back. Steering out two seconds at the rate below would
                // take a minute of visibly wrong speed, which is worse than one correction
                // the reconcile is about to smooth anyway.
                _baseTick = target;
                _tickAccumulator = 0f;
                _heldFrom = 0;
                _lastMoveTick = 0;
                _tickStartTick = 0;
                _head = 0;
                _count = 0;
                HardResyncs++;
                return;
            }

            // Proportional, and capped at half a tick per call. Called once per snapshot
            // (the world rate), that is at most ~7 ticks per second of correction — a
            // twelfth of the simulation rate, which is under the threshold at which a
            // player reads a speed change, and still closes a second of error in a few
            // seconds.
            float ticks = error * SteerGain;
            if (ticks > MaxSteerTicksPerCall) ticks = MaxSteerTicksPerCall;
            if (ticks < -MaxSteerTicksPerCall) ticks = -MaxSteerTicksPerCall;

            // Ahead of the server means run slower: take time OFF the accumulator.
            _tickAccumulator -= ticks * _dt;

            // Never below the negative of one tick, or a large error would leave the clock
            // owing time it then has to catch up on -- the same burst the catch-up clamp
            // exists to prevent, arriving from the other direction.
            if (_tickAccumulator < -_dt)
            {
                _tickAccumulator = -_dt;
            }
        }

        /// <summary>Fraction of the tick error corrected per steering call.</summary>
        private const float SteerGain = 0.1f;

        /// <summary>Ceiling on one steering call, in base ticks.</summary>
        private const float MaxSteerTicksPerCall = 0.5f;

        /// <summary>
        /// True when the tick rate came from a local fallback rather than from the server.
        /// </summary>
        /// <remarks>
        /// Exposed so a consumer can make the substitution observable, which the protocol
        /// requires: a silent fallback is the pre-<c>tick_rate</c> behaviour and
        /// reintroduces the defect the field exists to close.
        /// </remarks>
        public bool TickRateIsFallback => _settings.TickRateIsFallback;

        /// <summary>The speed replay is currently integrating with.</summary>
        /// <remarks>
        /// Equal to <see cref="PredictionSettings.Speed"/> until a snapshot supplies one.
        /// Diverging from the configured default is the normal, healthy case once the
        /// server sends field 9.
        /// </remarks>
        public float EffectiveSpeed => _speed;

        /// <summary>Forgets all prediction state. For a new session or a map transfer.</summary>
        public void Reset()
        {
            _head = 0;
            _count = 0;
            _lastRecordedTick = 0;
            BaseTicksAdvanced = 0;
            HeldStepsApplied = 0;
            SkipNoHoldWindow = 0;
            SkipNothingHeld = 0;
            SkipInputAlreadyStepped = 0;
            SkipExpired = 0;
            SkipRefusedByMovementModel = 0;
            SkipNoDisplacement = 0;
            StepIntervalSamples = 0;
            StepIntervalResets = 0;
            _predicted = Vec2.Zero;
            _renderOffset = Vec2.Zero;
            _step = Vec2.Zero;
            _sinceInput = 0f;
            _inputInterval = 0f;
            _stepInterval = 0f;
            _lastStepAt = 0f;
            _lastInputAt = 0f;
            _elapsed = 0f;
            _baseTick = 1;
            _tickAccumulator = 0f;
            _heldX = 0f;
            _heldY = 0f;
            _heldFrom = 0;
            _lastMoveTick = 0;
            _seeded = false;
            _baseTickSeeded = false;
            // Back to the configured fallback: the previous session's speed belonged to
            // a different entity, and possibly a different player.
            _speed = _settings.Speed;
            LastCorrection = 0f;
            Snaps = 0;
            SmoothedCorrections = 0;
            ReplayedSteps = 0;
            CoalescedInputs = 0;
            Reconciles = 0;
            DroppedInputs = 0;
            RejectedInputs = 0;
        }


        /// <summary>
        /// Records that a step just landed, and updates the interval the next one will be
        /// spread across.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Smoothed, and bounded above by the hold window.</b> An exponential moving
        /// average (α = 0.3, the same shape <c>WorldViewBinder</c> uses on snapshot
        /// arrivals) converges on <c>_dt</c> during sustained movement, so the steady case
        /// is exactly what it was before this existed.
        /// </para>
        /// <para>
        /// A gap longer than the whole hold window is not a cadence — it means the hold
        /// lapsed and this step begins a new burst rather than continuing one. Adopting it
        /// would spread the next step across an idle period and leave the avatar crawling
        /// behind its own simulation, which is the 0.12.3 defect in a new place. Such a gap
        /// restarts the measurement instead, and the span falls back to its <c>_dt</c>
        /// floor.
        /// </para>
        /// <para>
        /// <b>Saturation is deliberately untouched.</b> <see cref="StepProgress"/> still
        /// pins at 1, so a wider span makes the avatar reach the step later — never
        /// further than the step an input actually produced.
        /// </para>
        /// </remarks>
        /// <summary>
        /// How large a gap between two steps may be before it is read as a pause rather
        /// than as a cadence, in base ticks.
        /// </summary>
        /// <remarks>
        /// The largest LEGITIMATE gap is two ticks: rule 1 declines the tick right after an
        /// input, because the input already stepped it, so the next step lands one full
        /// timestep after the following boundary. Four is that with a factor of two of
        /// slack. It used to be <c>HoldTicks</c>, which was four at 60/15 by coincidence of
        /// the rates rather than by intent — and would now be fifteen, wide enough to smear
        /// a quarter-second pause into the cadence it exists to reject.
        /// </remarks>
        private const int StepGapPauseTicks = 4;

        private void NoteStep()
        {
            float gap = _elapsed - _lastStepAt;
            _lastStepAt = _elapsed;

            if (gap <= 0f || gap > StepGapPauseTicks * _dt)
            {
                _stepInterval = 0f;
                StepIntervalResets++;
                return;
            }

            _stepInterval = _stepInterval > 0f
                ? _stepInterval * 0.7f + gap * 0.3f
                : gap;
            StepIntervalSamples++;
        }

        /// <summary>The measured interval between consecutive steps, in seconds.</summary>
        /// <remarks>
        /// Zero when the last gap was rejected as a pause, in which case
        /// <see cref="EffectiveSmoothingSpan"/> falls back to its <c>_dt</c> floor.
        /// </remarks>
        public float ObservedStepInterval => _stepInterval;

        /// <summary>Gaps folded into <see cref="ObservedStepInterval"/>.</summary>
        public int StepIntervalSamples { get; private set; }

        /// <summary>
        /// Gaps rejected as a pause, which zero <see cref="ObservedStepInterval"/>.
        /// </summary>
        /// <remarks>
        /// A high count against a low <see cref="StepIntervalSamples"/> means the interval
        /// is being torn down faster than it is being built, and the span is effectively
        /// always at its floor.
        /// </remarks>
        public int StepIntervalResets { get; private set; }

        /// <summary>
        /// Why a base tick's held step did not move the entity.
        /// </summary>
        /// <remarks>
        /// A tick the hold declines is a tick on which <see cref="Position"/> is
        /// bit-identical for its whole duration: <c>_predicted</c> does not move,
        /// <c>_sinceInput</c> is not re-armed, <see cref="RenderStepProgress"/> stays
        /// saturated and <c>remaining</c> stays zero. At a high frame rate that is a still
        /// run one base tick long. The reasons need completely different responses, so
        /// they are counted apart rather than summed.
        /// </remarks>
        private enum HoldSkip
        {
            /// <summary>The tick stepped; nothing was skipped.</summary>
            None = 0,

            /// <summary>
            /// Retired. The hold used to be switched off entirely until the snapshot gap
            /// had been observed, which is what this counted; the window is now the fixed
            /// silence timeout, so nothing can turn the hold off and this is never raised.
            ///
            /// <para>Kept, at zero, rather than removed: <c>SkipNoHoldWindow</c> is part of
            /// the counter surface <c>PredictionSurfaceContractTests</c> pins, and a reader
            /// finding the name gone would have no way to learn it means "impossible now"
            /// rather than "renamed". Delete it with the counter, in a change that can run
            /// the Editor suite.</para>
            /// </summary>
            NoHoldWindow,

            /// <summary>Nothing is held — an explicit stop, or no input yet.</summary>
            NothingHeld,

            /// <summary>An input already stepped this tick; rule 1 forbids a second.</summary>
            InputAlreadyStepped,

            /// <summary>The hold window has run out.</summary>
            Expired,

            /// <summary>The movement model refused the held vector.</summary>
            RefusedByMovementModel,

            /// <summary>The step produced no displacement.</summary>
            NoDisplacement,
        }

        /// <summary>
        /// Always 0. See <see cref="HoldSkip.NoHoldWindow"/> — the hold can no longer be
        /// switched off, so nothing increments this.
        /// </summary>
        public int SkipNoHoldWindow { get; private set; }

        /// <summary>Base ticks the hold declined because nothing was held.</summary>
        public int SkipNothingHeld { get; private set; }

        /// <summary>Base ticks the hold declined because an input had already stepped it.</summary>
        public int SkipInputAlreadyStepped { get; private set; }

        /// <summary>Base ticks the hold declined because the window had run out.</summary>
        public int SkipExpired { get; private set; }

        /// <summary>Base ticks the hold declined because the movement model refused.</summary>
        public int SkipRefusedByMovementModel { get; private set; }

        /// <summary>Base ticks the hold declined because the step produced no displacement.</summary>
        public int SkipNoDisplacement { get; private set; }

        /// <summary>
        /// One simulation step, through the same <c>Shared.GameLogic</c> entry point the
        /// server's <c>InputHandler</c> uses.
        /// </summary>
        /// <remarks>
        /// <c>Dead</c> is false because a dead entity is not predicted at all — the server
        /// returns early for one, and predicting movement for a corpse would be a
        /// prediction guaranteed to be corrected.
        /// </remarks>
        /// <summary>
        /// One base tick of held movement, mirroring
        /// <c>InputHandler.ApplyHeldMovement</c>. Returns whether the position moved.
        /// </summary>
        /// <remarks>
        /// The three guards are the server's, in the server's order: nothing held, already
        /// stepped on this tick by a real input, and the window expired at
        /// <c>baseTick - heldFrom &gt; MaxBankedTicks</c>. Reproduced rather than approximated,
        /// because an off-by-one here is a fixed fraction of every step and lands under
        /// the smoothing threshold — the failure mode this class has now produced twice.
        /// </remarks>
        private bool ApplyHeld(ref Vec2 position, long baseTick)
        {
            // Where this tick started, kept so a real input arriving after the hold has
            // already stepped can RE-TAKE the tick's single step from the same place rather
            // than being coalesced away. See RecordInput for why that matters.
            Vec2 tickStart = position;
            long lastMoveBefore = _lastMoveTick;

            bool stepped = ApplyHeld(
                ref position, baseTick, _heldFrom, _heldX, _heldY, ref _lastMoveTick,
                out HoldSkip reason);

            if (stepped)
            {
                _tickStart = tickStart;
                _tickStartTick = baseTick;
                _tickStartLastMove = lastMoveBefore;
            }

            // Counted only on the live path. Reconcile replays the same guards over the
            // unacknowledged timeline, and folding those in would make the counter a
            // measure of how much replay ran rather than of how the rendered position
            // behaved.
            switch (reason)
            {
                case HoldSkip.NoHoldWindow: SkipNoHoldWindow++; break;
                case HoldSkip.NothingHeld: SkipNothingHeld++; break;
                case HoldSkip.InputAlreadyStepped: SkipInputAlreadyStepped++; break;
                case HoldSkip.Expired: SkipExpired++; break;
                case HoldSkip.RefusedByMovementModel: SkipRefusedByMovementModel++; break;
                case HoldSkip.NoDisplacement: SkipNoDisplacement++; break;
            }

            return stepped;
        }

        private bool ApplyHeld(
            ref Vec2 position, long baseTick, long heldFrom, float heldX, float heldY,
            ref long lastMoveTick) =>
            ApplyHeld(ref position, baseTick, heldFrom, heldX, heldY, ref lastMoveTick, out _);

        private bool ApplyHeld(
            ref Vec2 position, long baseTick, long heldFrom, float heldX, float heldY,
            ref long lastMoveTick, out HoldSkip reason)
        {
            reason = HoldSkip.None;

            if (heldFrom == 0) { reason = HoldSkip.NothingHeld; return false; }
            if (heldFrom == baseTick) { reason = HoldSkip.InputAlreadyStepped; return false; }

            // The window is the SILENCE TIMEOUT the server uses, not the snapshot gap this
            // predictor used to measure. Two changes in one line, both of which have to
            // match InputHandler.ApplyHeldMovement or the sides disagree structurally:
            //
            //   * the budget is MaxBankedTicks (250ms at any rate) rather than HoldTicks
            //     (one snapshot interval). A window sized to a guessed send rate has no
            //     slack: a 15Hz client's packets were measured arriving 4.19 base ticks
            //     apart against a 4-tick window.
            //   * the comparison is > rather than >=, so gaps 1..MaxBankedTicks step
            //     INCLUSIVE. That reproduces the coverage the old banked step gave in one
            //     multiplied hit; dropping the last tick costs a bursting 15Hz client a
            //     sixth of its distance.
            if (baseTick - heldFrom > MaxBankedTicks) { reason = HoldSkip.Expired; return false; }

            // The hold path updates LastMoveTick exactly as the packet path does, on both
            // sides, so the two agree on which ticks have already been stepped.
            MoveResult result = StepResult(
                position, heldX, heldY, StepDeltaTime(baseTick, lastMoveTick, heldFrom),
                out Vec2 moved);

            if (result is not (MoveResult.Accepted or MoveResult.Clamped))
            {
                reason = HoldSkip.RefusedByMovementModel;
                return false;
            }

            if (moved.X == position.X && moved.Y == position.Y)
            {
                reason = HoldSkip.NoDisplacement;
                return false;
            }

            position = moved;
            lastMoveTick = baseTick;
            return true;
        }

        /// <summary>
        /// The observed gap in base ticks between consecutive snapshots. <b>No longer the
        /// hold window</b> — see <see cref="MaxBankedTicks"/>, which is.
        /// </summary>
        /// <remarks>
        /// <para>This was the hold window, measured rather than configured: while the
        /// server held a direction for one world interval and sent on the same interval,
        /// the gap between the base ticks two snapshots carry <i>was</i> the window. The
        /// server no longer holds for a send-rate-shaped interval — the coupling that made
        /// the measurement valid is gone with it — so the window is read from the shared
        /// constant both sides compile against instead of inferred from the wire.</para>
        ///
        /// <para>The value is still tracked because it is a genuine diagnostic (a server
        /// replicating slower than it claims shows up here first) and because
        /// <c>WorldViewBinder</c> feeds the same number to the interpolation clock, where it
        /// still means what it says. It no longer gates any movement.</para>
        /// </remarks>
        public int HoldTicks { get; private set; } = 1;

        /// <summary>
        /// Records how many base ticks separate consecutive snapshots. Values below 1 are
        /// ignored, on the same "zero means not sent" rule the speed and tick-rate fields
        /// follow.
        /// </summary>
        /// <remarks>
        /// Diagnostic only since the hold window became a silence timeout; calling it or not
        /// calling it changes no predicted position. See <see cref="HoldTicks"/>.
        /// </remarks>
        public void SetHoldTicks(int holdTicks)
        {
            if (holdTicks >= 1)
            {
                HoldTicks = holdTicks;
            }
        }

        /// <summary>
        /// One step, reporting the server's own verdict on it.
        /// </summary>
        /// <remarks>
        /// The verdict is taken from <c>MovementSystem</c> rather than recomputed here.
        /// Whether a vector counts as a stop is the deadzone rule, and a second copy of
        /// that threshold on the client is a constant free to drift from the server's —
        /// the identical shape as the tick rate and the hold window. There is one
        /// implementation of the movement model and this asks it.
        /// </remarks>
        private MoveResult StepResult(
            Vec2 from, float moveX, float moveY, float deltaTime, out Vec2 moved)
        {
            var probe = new EntityState { Position = from, Speed = _speed, Dead = false };
            MoveResult result = MovementSystem.TryMove(
                in probe, moveX, moveY, deltaTime, in _settings.Bounds, out Vec2 next);

            moved = result is MoveResult.Accepted or MoveResult.Clamped ? next : from;
            return result;
        }

        /// <summary>
        /// The timestep one step covers. Always exactly one tick.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Rule 3 of the model the server's <c>docs/API.md</c> requires a predicting client
        /// to reproduce, and the rule the whole of prediction rests on: over any interval
        /// client and server each take one step per tick, so both travel
        /// <c>speed x ticks</c> and the two positions agree <b>by construction</b> rather
        /// than by two independent measurements of elapsed time happening to match. Jitter
        /// shifts <i>when</i> a step lands; it cannot change <i>how many</i> there are,
        /// which is what reconciliation absorbs.
        /// </para>
        /// <para>
        /// <b>This method used to bank.</b> It returned
        /// <c>min(now - lastMoveTick, MaxBankedTicks) * dt</c>, mirroring what the server
        /// then did, so that the inputs per-tick coalescing discards from a burst did not
        /// take their simulated time with them. Both sides have dropped it. It restored the
        /// right distance and destroyed the frames: measured against a live server it
        /// produced a 1.36-unit step where a normal one is 0.083, and the two sides only
        /// agreed when their two elapsed-time measurements agreed — which, across a network,
        /// is precisely what cannot be relied on. The time a burst loses is recovered by
        /// <see cref="ApplyHeld"/> stepping on each tick of the gap instead.
        /// </para>
        /// <para>
        /// It is kept as a method rather than inlined as <c>_dt</c> because the parity this
        /// file exists to hold is with <c>InputHandler.StepDeltaTime</c>, which is also
        /// still a method, and because a future change to the timestep rule has to happen in
        /// two places that are easy to find rather than in one that is not.
        /// </para>
        /// </remarks>
        private float StepDeltaTime(long baseTick, long lastMoveTick, long heldFrom)
        {
            _ = baseTick;
            _ = lastMoveTick;
            _ = heldFrom;
            return _dt;
        }

        /// <summary>
        /// How many base ticks a held direction keeps producing steps for after the input
        /// that set it, at this predictor's rate — the silence timeout, 250ms.
        /// </summary>
        /// <remarks>
        /// The name is inherited from <see cref="GameConstants.MaxBankedMovementMs"/>, which
        /// keeps its own name for a release reason recorded at its definition: renaming it
        /// means tagging <c>Shared.GameLogic</c> and bumping both <c>manifest.json</c> and
        /// <c>packages-lock.json</c>. The value is unchanged and the server reads the same
        /// constant, so the two sides expire a held direction on the same tick.
        /// </remarks>
        public int MaxBankedTicks =>
            GameConstants.MaxBankedMovementTicks(_settings.TickRate);

        private Vec2 Step(Vec2 from, float moveX, float moveY)
        {
            var probe = new EntityState
            {
                Position = from,
                Speed = _speed,
                Dead = false,
            };

            MoveResult result = MovementSystem.TryMove(
                in probe, moveX, moveY, _dt, in _settings.Bounds, out Vec2 moved);

            return result is MoveResult.Accepted or MoveResult.Clamped ? moved : from;
        }

        /// <summary>Discards inputs the server has confirmed it applied.</summary>
        /// <summary>
        /// Drop every pending input the server has acknowledged, remembering the newest of
        /// them.
        /// </summary>
        /// <remarks>
        /// The newest acknowledged input is the direction the SERVER was holding over the
        /// ticks between the snapshot and the first input it has not seen yet, so replay
        /// needs it to reproduce that stretch. Without it those ticks are simply skipped —
        /// see the replay loop, and the constant four-step correction that cost.
        /// </remarks>
        private void DropAcknowledged(long ackTick)
        {
            _haveLastAcked = false;

            while (_count > 0 && _pending[_head].Tick <= ackTick)
            {
                _lastAcked = _pending[_head];
                _haveLastAcked = true;
                _head = (_head + 1) % Capacity;
                _count--;
            }
        }

        private PendingInput _lastAcked;
        private bool _haveLastAcked;
    }
}
