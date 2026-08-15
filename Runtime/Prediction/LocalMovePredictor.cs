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

            public PendingInput(long tick, float moveX, float moveY, long baseTick)
            {
                Tick = tick;
                MoveX = moveX;
                MoveY = moveY;
                BaseTick = baseTick;
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
        private float _inputInterval; // observed seconds between inputs; diagnostics
        private float _lastInputAt;   // _elapsed when the previous input arrived
        private float _elapsed;       // monotonic seconds fed through Advance
        private bool _seeded;

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
                if (HoldTicks > 1) return _dt;

                // No hold: one step per input, so the span is the gap between inputs.
                return _inputInterval > _dt ? _inputInterval : _dt;
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

            int slot = (_head + _count) % Capacity;
            _pending[slot] = new PendingInput(tick, moveX, moveY, _baseTick);
            _count++;

            // Whatever of the previous step was still unshown would otherwise vanish
            // when the new step replaces it. Carry it into the render offset so the
            // visible position does not jump on an input boundary.
            Vec2 before = Position;

            Vec2 previous = _predicted;
            MoveResult verdict = StepResult(_predicted, moveX, moveY, out _predicted);
            _step = new Vec2(_predicted.X - previous.X, _predicted.Y - previous.Y);

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
            }
            else if (verdict == MoveResult.None)
            {
                // An explicit stop. A Rejected vector deliberately leaves the hold alone,
                // matching the server, which logs and drops it without disturbing state.
                _heldFrom = 0;
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
            _sinceInput = 0f;

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
        public void Reconcile(Vec2 authoritative, long ackTick)
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

                for (long t = _pending[_head].BaseTick; t <= _baseTick; t++)
                {
                    // Inputs recorded on this tick step first and take the hold, exactly
                    // as ProcessInput runs before ApplyHeldMovement within a tick.
                    var stepped = false;
                    while (next < _count && _pending[(_head + next) % Capacity].BaseTick == t)
                    {
                        var input = _pending[(_head + next) % Capacity];
                        MoveResult verdict = StepResult(
                            replayed, input.MoveX, input.MoveY, out replayed);
                        ReplayedSteps++;
                        stepped = true;

                        if (verdict is MoveResult.Accepted or MoveResult.Clamped)
                        {
                            heldX = input.MoveX;
                            heldY = input.MoveY;
                            heldFrom = t;
                        }
                        else if (verdict == MoveResult.None)
                        {
                            heldFrom = 0;
                        }

                        next++;
                    }

                    if (!stepped && ApplyHeld(ref replayed, t, heldFrom, heldX, heldY))
                    {
                        ReplayedSteps++;
                    }
                }
            }

            _predicted = replayed;

            float dx = before.X - replayed.X;
            float dy = before.Y - replayed.Y;
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
                while (_tickAccumulator >= _dt)
                {
                    _tickAccumulator -= _dt;
                    _baseTick++;

                    Vec2 before = _predicted;
                    if (ApplyHeld(ref _predicted, _baseTick))
                    {
                        _step = new Vec2(_predicted.X - before.X, _predicted.Y - before.Y);
                        _sinceInput = 0f;
                    }
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
            _predicted = Vec2.Zero;
            _renderOffset = Vec2.Zero;
            _step = Vec2.Zero;
            _sinceInput = 0f;
            _inputInterval = 0f;
            _lastInputAt = 0f;
            _elapsed = 0f;
            _baseTick = 1;
            _tickAccumulator = 0f;
            _heldX = 0f;
            _heldY = 0f;
            _heldFrom = 0;
            _seeded = false;
            // Back to the configured fallback: the previous session's speed belonged to
            // a different entity, and possibly a different player.
            _speed = _settings.Speed;
            LastCorrection = 0f;
            Snaps = 0;
            SmoothedCorrections = 0;
            ReplayedSteps = 0;
            Reconciles = 0;
            DroppedInputs = 0;
            RejectedInputs = 0;
        }

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
        /// <c>baseTick - heldFrom &gt;= HoldTicks</c>. Reproduced rather than approximated,
        /// because an off-by-one here is a fixed fraction of every step and lands under
        /// the smoothing threshold — the failure mode this class has now produced twice.
        /// </remarks>
        private bool ApplyHeld(ref Vec2 position, long baseTick) =>
            ApplyHeld(ref position, baseTick, _heldFrom, _heldX, _heldY);

        private bool ApplyHeld(ref Vec2 position, long baseTick, long heldFrom, float heldX, float heldY)
        {
            if (HoldTicks <= 1) return false;
            if (heldFrom == 0) return false;                 // nothing held
            if (heldFrom == baseTick) return false;          // the input already stepped it
            if (baseTick - heldFrom >= HoldTicks) return false;   // expired

            Vec2 moved = Step(position, heldX, heldY);
            if (moved.X == position.X && moved.Y == position.Y) return false;

            position = moved;
            return true;
        }

        /// <summary>
        /// Base ticks the server keeps integrating a direction for after the input that
        /// set it. One world interval — <c>_rates.WorldEvery</c> server-side.
        /// </summary>
        /// <remarks>
        /// Measured, not configured: consecutive snapshots are emitted one world tick
        /// apart, so the gap between the base ticks they carry <i>is</i> this number —
        /// but only while the server holds for the same interval it sends on. See
        /// <c>TickRateEstimator.SnapshotTickGap</c>, which states that coupling and what
        /// happens if it ever stops holding.
        /// Falls back to 1 — no hold — until it has been observed, which makes an
        /// unmeasured hold behave exactly like the pre-0.13.0 predictor rather than
        /// guessing a window and being wrong in a new way.
        /// </remarks>
        public int HoldTicks { get; private set; } = 1;

        /// <summary>
        /// Tells the predictor how many base ticks separate consecutive snapshots, which
        /// is the length of the server's hold window. Values below 1 are ignored, on the
        /// same "zero means not sent" rule the speed and tick-rate fields follow.
        /// </summary>
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
        private MoveResult StepResult(Vec2 from, float moveX, float moveY, out Vec2 moved)
        {
            var probe = new EntityState { Position = from, Speed = _speed, Dead = false };
            MoveResult result = MovementSystem.TryMove(
                in probe, moveX, moveY, _dt, in _settings.Bounds, out Vec2 next);

            moved = result is MoveResult.Accepted or MoveResult.Clamped ? next : from;
            return result;
        }

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
        private void DropAcknowledged(long ackTick)
        {
            while (_count > 0 && _pending[_head].Tick <= ackTick)
            {
                _head = (_head + 1) % Capacity;
                _count--;
            }
        }
    }
}
