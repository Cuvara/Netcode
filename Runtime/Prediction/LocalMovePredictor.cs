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

            public PendingInput(long tick, float moveX, float moveY)
            {
                Tick = tick;
                MoveX = moveX;
                MoveY = moveY;
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
        private readonly float _dt;

        private int _head;      // next write slot
        private int _count;     // live entries
        private long _lastRecordedTick;

        private Vec2 _predicted;      // simulated position: authoritative + replayed inputs
        private Vec2 _renderOffset;   // predicted-minus-corrected, decayed to zero
        private bool _seeded;

        /// <summary>
        /// Creates a predictor. Check <see cref="IsEnabled"/> before using the result:
        /// unusable settings produce a predictor that refuses to predict rather than one
        /// that predicts badly.
        /// </summary>
        public LocalMovePredictor(PredictionSettings settings)
        {
            _settings = settings;
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
        public Vec2 Position => new Vec2(
            _predicted.X + _renderOffset.X,
            _predicted.Y + _renderOffset.Y);

        /// <summary>Predicted position with no smoothing applied. Diagnostics and tests.</summary>
        public Vec2 SimulatedPosition => _predicted;

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
            _pending[slot] = new PendingInput(tick, moveX, moveY);
            _count++;

            _predicted = Step(_predicted, moveX, moveY);
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
                return;
            }

            if (!_seeded)
            {
                _seeded = true;
                _predicted = authoritative;
                _renderOffset = Vec2.Zero;
                LastCorrection = 0f;
                return;
            }

            Vec2 before = _predicted;

            DropAcknowledged(ackTick);

            // Rewind to what the server says, then re-apply only what it has not seen.
            Vec2 replayed = authoritative;
            for (var i = 0; i < _count; i++)
            {
                var input = _pending[(_head + i) % Capacity];
                replayed = Step(replayed, input.MoveX, input.MoveY);
                ReplayedSteps++;
            }

            _predicted = replayed;

            float dx = before.X - replayed.X;
            float dy = before.Y - replayed.Y;
            LastCorrection = new Vec2(dx, dy).Magnitude;

            if (LastCorrection > SmoothingThreshold)
            {
                // Too far to hide. Showing the avatar gliding from a place the server has
                // already ruled out is worse than one honest jump.
                _renderOffset = Vec2.Zero;
                Snaps++;
            }
            else if (LastCorrection > 0f)
            {
                // Carry the whole error as a render offset and retire it over the next few
                // frames, so the simulated position is authoritative-correct immediately
                // while the visible one catches up.
                _renderOffset = new Vec2(dx, dy);
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

        /// <summary>Forgets all prediction state. For a new session or a map transfer.</summary>
        public void Reset()
        {
            _head = 0;
            _count = 0;
            _lastRecordedTick = 0;
            _predicted = Vec2.Zero;
            _renderOffset = Vec2.Zero;
            _seeded = false;
            LastCorrection = 0f;
            Snaps = 0;
            SmoothedCorrections = 0;
            ReplayedSteps = 0;
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
        private Vec2 Step(Vec2 from, float moveX, float moveY)
        {
            var probe = new EntityState
            {
                Position = from,
                Speed = _settings.Speed,
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
