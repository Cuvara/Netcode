using System.Linq;
using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Pins prediction against the server's held-movement rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule.</b> `InputHandler.ProcessInput` steps once on the input's own base
    /// tick and records the direction as held; `InputHandler.ApplyHeldMovement`, called
    /// from `TickLoop` on <i>every</i> base tick including ones where no packet arrived,
    /// steps again while <c>baseTick - heldFrom &lt;= MaxBankedTicks</c> — a silence
    /// timeout of 250ms, 15 base ticks at 60Hz, read from the shared constant on both
    /// sides. Every step is one tick; no step ever covers more.
    /// </para>
    /// <para>
    /// <b>What changed.</b> The window used to be <c>_rates.WorldEvery</c>, inferred
    /// client-side from the snapshot gap, and a step used to cover
    /// <c>min(now - lastMoveTick, cap)</c> so a gap was repaid in one multiplied hit. Both
    /// are gone from both sides. The old shape agreed only when the two ends' independent
    /// measurements of elapsed time agreed, which is what a network is worst at; the
    /// current one agrees by construction, because each end takes one step per tick.
    /// </para>
    /// <para>
    /// <b>Why this fixture exists.</b> A client that takes one step per input reproduces
    /// a quarter of that at a 15 Hz send rate, and is wrong by a fixed <i>ratio</i> on
    /// every input. A fixed ratio of a small step lands under the 0.5 smoothing
    /// threshold, so it never snaps, no counter reads unhealthy, and the only available
    /// symptom is a player saying it does not feel right. That is the third defect in
    /// this package with exactly that signature, and the reason each of these tests
    /// asserts against a simulation of the server rather than against a constant.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class HeldMovementParityTests
    {
        private const int BaseHz = 60;
        private const int SendHz = 15;

        /// <summary>Base ticks between two sends at <see cref="SendHz"/> — the cadence.</summary>
        private const int SendEvery = BaseHz / SendHz;

        /// <summary>
        /// Base ticks a held direction survives after the input that set it — the silence
        /// timeout, 250ms, 15 ticks at 60Hz.
        /// </summary>
        /// <remarks>
        /// This used to be the same number as <see cref="SendEvery"/>, and the two being one
        /// constant hid a defect rather than saving a line. The window was the client's
        /// measured snapshot gap, i.e. a guess at its own send rate expressed as a deadline,
        /// which left no slack at all: a 15Hz client's packets were measured arriving 4.19
        /// base ticks apart against a 4-tick window. The window is now a property of how long
        /// silence may be tolerated and the cadence is a property of the client, so they are
        /// separate constants that happen to be measured in the same unit.
        /// </remarks>
        private static readonly int HoldWindow = GameConstants.MaxBankedMovementTicks(BaseHz);

        private const float Speed = 5f;

        private static float Dt => MovementSystem.DeltaTimeForTickRate(BaseHz);
        private static MapBounds Bounds => MapBounds.Default;

        private static LocalMovePredictor Predictor()
        {
            var p = new LocalMovePredictor(new PredictionSettings(BaseHz, Speed, Bounds));
            p.SetHoldTicks(SendEvery);
            p.Reconcile(Vec2.Zero, 0);
            return p;
        }

        /// <summary>
        /// The server, reimplemented from its own rule for comparison only — it drives
        /// the same <c>MovementSystem.TryMove</c>, so the arithmetic is shared and only
        /// the scheduling is restated.
        /// </summary>
        private static Vec2 ServerAfter(int inputs, float moveX, float moveY)
        {
            var position = Vec2.Zero;
            long heldFrom = 0;
            long lastMove = 0;
            long tick = 1;                 // the base tick the first input lands on

            for (var i = 0; i < inputs; i++)
            {
                // Rule 1 on the input's own tick: a tick the hold already stepped coalesces
                // into it rather than stepping twice. Unreachable under the old window,
                // where the hold expired on exactly the tick the next input arrived on --
                // which is why this model could count the input tick as one of the
                // interval's four and still agree.
                if (lastMove != tick && StepOnce(ref position, moveX, moveY))
                {
                    heldFrom = tick;
                    lastMove = tick;
                }

                for (var k = 0; k < SendEvery; k++)
                {
                    tick++;

                    if (heldFrom != 0 && tick != heldFrom && tick - heldFrom <= HoldWindow
                        && StepOnce(ref position, moveX, moveY))
                    {
                        lastMove = tick;
                    }
                }
            }

            return position;
        }

        private static bool StepOnce(ref Vec2 position, float moveX, float moveY)
        {
            var probe = new EntityState { Position = position, Speed = Speed, Dead = false };
            MapBounds bounds = Bounds;
            MoveResult result = MovementSystem.TryMove(
                in probe, moveX, moveY, Dt, in bounds, out Vec2 moved);

            if (result is MoveResult.Accepted or MoveResult.Clamped)
            {
                position = moved;
                return true;
            }

            return false;
        }

        private static void SendAndAdvance(LocalMovePredictor p, long tick, float x, float y)
        {
            p.RecordInput(tick, x, y);
            for (var k = 0; k < SendEvery; k++)
            {
                p.Advance(1f / BaseHz);
            }
        }

        [Test]
        public void PredictionMatchesTheServerOverASustainedHold()
        {
            var p = Predictor();
            const int inputs = 10;

            for (var i = 1; i <= inputs; i++)
            {
                SendAndAdvance(p, i, 1f, 0f);
            }

            Vec2 server = ServerAfter(inputs, 1f, 0f);

            Assert.That(p.SimulatedPosition.X, Is.EqualTo(server.X).Within(1e-4f),
                $"predicted {p.SimulatedPosition.X:F4} against the server's {server.X:F4}. " +
                "One step per input instead of one per base tick in the hold window is a " +
                "fixed-ratio shortfall that smooths rather than snaps.");
        }

        [Test]
        public void SustainedInputTravelsAtTheConfiguredSpeed()
        {
            var p = Predictor();
            const int inputs = 15;   // one second of sends at 15 Hz

            for (var i = 1; i <= inputs; i++)
            {
                SendAndAdvance(p, i, 1f, 0f);
            }

            // One step of slack, and only one: the driver steps on the tick the first
            // input lands on and then advances a full second of ticks on top, so it spans
            // BaseHz + 1 ticks of movement. What the case is measuring is that a second of
            // held input is a second of travel and not a quarter of one -- a defect of
            // ratio, never of a single step.
            float oneStep = Speed / BaseHz;

            Assert.That(p.SimulatedPosition.X, Is.EqualTo(Speed).Within(oneStep + 0.01f),
                $"one second of held input moved {p.SimulatedPosition.X:F4} units at a " +
                $"configured speed of {Speed}. The client and server can agree perfectly " +
                "on a wrong number, so speed is asserted directly and not inferred from " +
                "the absence of corrections.");
        }

        [Test]
        public void ReplayReproducesTheHoldRatherThanOneStepPerInput()
        {
            var p = Predictor();

            for (var i = 1; i <= 4; i++)
            {
                SendAndAdvance(p, i, 1f, 0f);
            }

            Vec2 predicted = p.SimulatedPosition;

            // The server acknowledges nothing and confirms the origin: replay must
            // rebuild the identical position from scratch.
            p.Reconcile(Vec2.Zero, 0);

            Assert.That(p.SimulatedPosition.X, Is.EqualTo(predicted.X).Within(1e-4f),
                "replaying the pending inputs did not reproduce the position prediction " +
                "already produced. Replay and the live path must run one timeline, or " +
                "every reconcile injects a correction the network did not cause.");
        }

        [Test]
        public void AnExplicitStopEndsTheHoldImmediately()
        {
            var p = Predictor();
            SendAndAdvance(p, 1, 1f, 0f);

            float afterMove = p.SimulatedPosition.X;

            // A zero vector is the deadzone: the server clears the hold on it rather
            // than letting the window run out.
            p.RecordInput(2, 0f, 0f);

            // Sampled AFTER the stop, not before it. The stop lands on a tick the hold has
            // already stepped, and rule 1 gives that tick exactly one step -- the newest
            // input's, which is a stop, so the tick's step is rolled back. The server does
            // the same thing by never taking it: it drains the input, finds a deadzone,
            // moves nothing and skips the hold. Comparing against `afterMove` would be
            // comparing against a position the server was never in.
            float afterStop = p.SimulatedPosition.X;

            Assert.That(afterStop, Is.LessThanOrEqualTo(afterMove + 1e-5f),
                "an explicit stop added travel");

            for (var k = 0; k < HoldWindow + 2; k++)
            {
                p.Advance(1f / BaseHz);
            }

            Assert.That(p.SimulatedPosition.X, Is.EqualTo(afterStop).Within(1e-5f),
                "the avatar kept coasting after an explicit stop. Releasing the stick " +
                "must halt it at once — a player attributes that latency directly to " +
                "their own input, unlike a correction they cannot see.");
        }

        /// <summary>
        /// The rendered position must not lag the simulation once a hold is in play.
        /// </summary>
        /// <remarks>
        /// 0.12.3 widened the smoothing span to the interval between inputs, to fix an
        /// avatar that arrived early and then froze. With a hold window the steps arrive
        /// one timestep apart rather than one input apart, so that same span is now four
        /// times too long and produces the opposite defect: a rendered position that
        /// never catches up. A fix aimed at the symptom outlives the cause, so this pins
        /// the corrected behaviour rather than trusting the earlier reasoning.
        /// </remarks>
        [Test]
        public void TheRenderedPositionKeepsUpWithTheHold()
        {
            var p = Predictor();
            for (var i = 1; i <= 6; i++)
            {
                SendAndAdvance(p, i, 1f, 0f);
            }

            // At most one step behind, and that one step is the interpolation of the step
            // that just landed -- the rendered position is always travelling toward the
            // newest simulated one. Equality used to hold exactly, and only by accident:
            // the last tick of every send interval was past the old hold window, so it
            // produced no step and the renderer had a free tick to catch up in. With a step
            // on every tick there is no such tick, and demanding equality would be
            // demanding that the renderer teleport.
            float lag = p.SimulatedPosition.X - p.Position.X;
            float oneStep = Speed / BaseHz;

            Assert.That(lag, Is.InRange(-1e-4f, oneStep + 1e-4f),
                $"rendered {p.Position.X:F4} against simulated {p.SimulatedPosition.X:F4}, " +
                $"a lag of {lag / oneStep:F2} steps. More than one step means the smoothing " +
                "span is longer than the interval steps arrive at, so the avatar is " +
                "permanently behind its own prediction rather than one step behind it.");
        }

        [Test]
        public void EveryFrameMovesWhileTheHoldIsActive()
        {
            var p = Predictor();
            float frame = 1f / 240f;
            float previous = p.Position.X;
            var still = 0;
            var counted = 0;

            for (var i = 1; i <= 6; i++)
            {
                p.RecordInput(i, 1f, 0f);
                for (var f = 0; f < 16; f++)   // 16 frames at 240 fps = one 15 Hz interval
                {
                    p.Advance(frame);
                    if (i == 1) { previous = p.Position.X; continue; }

                    counted++;
                    if (p.Position.X - previous <= 1e-6f) still++;
                    previous = p.Position.X;
                }
            }

            Assert.That(still, Is.Zero,
                $"{still} of {counted} frames did not move while a direction was held. " +
                "Continuous input must produce continuous motion; the gaps are the " +
                "stutter, and they are invisible to every correction counter.");
        }

        // ── Rule 3: every step is exactly one tick ──

        /// <summary>
        /// A gap the hold covers is paid for by <b>stepping through it</b>, one tick at a
        /// time — not by the next step growing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This assertion is the inverse of the one it replaces, and the inversion is the
        /// whole change. Rule 3 used to read <c>dt = min(now - last_move_tick, cap) /
        /// tick_rate</c>: a client that had gone quiet paid for the gap in one multiplied
        /// step. It produced the right distance and the wrong frames — measured against a
        /// live server, a 1.36-unit step where a normal one is 0.083 — and it only agreed
        /// with the server when the two sides' independent measurements of elapsed time
        /// agreed, which across a network is exactly what cannot be relied on. The result
        /// was the reported symptom: move one step, jerk back, continue.
        /// </para>
        /// <para>
        /// One step per tick makes the two sides agree structurally instead. The distance a
        /// gap is worth is still recovered — this case measures that it is — but it arrives
        /// as the same steps the server took, on the same ticks.
        /// </para>
        /// </remarks>
        [Test]
        public void AGapInsideTheHoldIsPaidForOneTickAtATime()
        {
            var p = Predictor();

            p.RecordInput(1, 1f, 0f);
            float afterInput = p.SimulatedPosition.X;

            // Silence well inside the window, so the hold is still live throughout.
            const int silentTicks = 6;
            for (var k = 0; k < silentTicks; k++) p.Advance(1f / BaseHz);

            float travelled = p.SimulatedPosition.X - afterInput;
            float oneStep = Speed / BaseHz;

            Assert.That(travelled, Is.EqualTo(oneStep * silentTicks).Within(1e-4f),
                $"{silentTicks} silent ticks moved {travelled:F4}, not the " +
                $"{oneStep * silentTicks:F4} those ticks are worth. The hold is what recovers " +
                "the time a gap represents now that no step banks it, so a hold that stops " +
                "early is a client that falls behind the server by a constant.");
        }

        /// <summary>
        /// No step is ever larger than one tick, however long the client was silent.
        /// </summary>
        /// <remarks>
        /// The property that makes prediction work at all: over any interval client and
        /// server each take one step per tick, so the two positions agree by construction
        /// rather than by two elapsed-time measurements matching. A client that banks
        /// reconciles against a server that does not — and the reverse is just as bad, which
        /// is why this moved on both sides in one change.
        /// </remarks>
        [Test]
        public void ASilentClientResumesWithOnePlainStepAndNeverTeleports()
        {
            var p = Predictor();
            p.RecordInput(1, 1f, 0f);

            // Ten seconds of silence -- far past the hold window, which expires long before.
            for (var k = 0; k < BaseHz * 10; k++) p.Advance(1f / BaseHz);

            float beforeResume = p.SimulatedPosition.X;
            p.RecordInput(2, 1f, 0f);

            float resumed = p.SimulatedPosition.X - beforeResume;
            float oneStep = Speed / BaseHz;

            Assert.That(resumed, Is.EqualTo(oneStep).Within(1e-4f),
                $"the step after ten seconds of silence moved {resumed:F4}; one step is " +
                $"{oneStep:F4}. Repaying the silence restores the distance and destroys the " +
                "agreement: the server takes one step there, so everything above one step " +
                "is a correction the client hands itself.");
        }

        /// <summary>
        /// And the hold really did stop: the ten seconds themselves are not travelled.
        /// </summary>
        [Test]
        public void TheHoldExpiresAfterTheSilenceTimeoutRatherThanDriftingForever()
        {
            var p = Predictor();
            p.RecordInput(1, 1f, 0f);
            float afterInput = p.SimulatedPosition.X;

            for (var k = 0; k < BaseHz * 10; k++) p.Advance(1f / BaseHz);

            float coasted = p.SimulatedPosition.X - afterInput;
            float oneStep = Speed / BaseHz;

            Assert.That(coasted, Is.EqualTo(oneStep * HoldWindow).Within(1e-4f),
                $"ten seconds of silence coasted {coasted:F4}, against the " +
                $"{oneStep * HoldWindow:F4} the {HoldWindow}-tick window allows. An expiry " +
                "that never fires is an avatar walking away from a player who let go.");
        }

        [Test]
        public void TheCapMatchesTheSharedConstant()
        {
            var p = Predictor();

            Assert.That(p.MaxBankedTicks,
                Is.EqualTo(GameConstants.MaxBankedMovementTicks(BaseHz)),
                "the hold window must come from Shared.GameLogic and not from a second copy " +
                "on the client, and not from anything measured off the wire. Both sides " +
                "expire a held direction on the same tick only because both compile against " +
                "this constant; a client-side copy of a server constant is the defect this " +
                "package has now shipped four times.");
        }

        [Test]
        public void SendingEveryBaseTickTravelsExactlyTheConfiguredSpeed()
        {
            var p = Predictor();

            // The case rule 3 is measured against: one input per tick, one step per tick,
            // so a second of input is a second of travel. It read the same under the banked
            // rule -- lastMoveTick == now - 1 always -- which is why the golden vectors did
            // not have to be regenerated when banking arrived, or when it left.
            for (var i = 1; i <= BaseHz; i++)
            {
                p.RecordInput(i, 1f, 0f);
                p.Advance(1f / BaseHz);
            }

            // One step of slack, and only one: the loop opens by stepping on the tick it
            // starts on and then advances BaseHz times, so it spans BaseHz + 1 ticks of
            // movement for BaseHz sends. The point of the case is that nothing adds
            // distance on top -- which would be a multiple, not a single step.
            float oneStep = Speed / BaseHz;

            Assert.That(p.SimulatedPosition.X, Is.EqualTo(Speed).Within(oneStep + 0.001f),
                $"a client sending every tick travelled {p.SimulatedPosition.X:F4} against " +
                $"a configured {Speed}. Every step is one timestep, so a second of input " +
                "must be exactly a second of travel.");
        }

        // ── One frame may not manufacture a simulation ──

        /// <summary>
        /// A frame that took seconds advances the clock by the silence timeout and no more.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>deltaTime</c> is whatever the last frame took, and at startup that is seconds:
        /// scene load, subscene streaming, shader warmup, the first DOTS world coming up.
        /// Carrying all of it into the fixed-step accumulator makes one frame burst-advance
        /// hundreds of base ticks, each one stepping the held direction.
        /// </para>
        /// <para>
        /// <b>Measured against a live server</b>, a player a minute into a session sat
        /// <b>355 base ticks — 5.9 seconds —</b> ahead of the server's own tick, at a
        /// constant offset, both clocks otherwise at a matched 60Hz. Not drift: a startup
        /// burst that is never given back. The reconcile's lead replay is bounded by the
        /// hold window, so a lead that far outside it cannot be replayed and every snapshot
        /// lands as a large correction instead — snaps ran at 1 to 2 per second, which is
        /// what a player reports as the avatar jerking while it moves.
        /// </para>
        /// </remarks>
        [Test]
        public void AFrameThatTookSecondsDoesNotManufactureSecondsOfSimulation()
        {
            var p = Predictor();
            p.RecordInput(1, 1f, 0f);

            int before = p.BaseTicksAdvanced;

            // Five seconds in one frame: a scene load, or a breakpoint.
            p.Advance(5f);

            int advanced = p.BaseTicksAdvanced - before;

            Assert.That(advanced, Is.LessThanOrEqualTo(p.MaxCatchUpTicks),
                $"one frame advanced {advanced} base ticks. Every tick over the budget is a " +
                "tick of prediction lead the server never sees and the reconcile cannot " +
                "replay, so it is paid back as a correction on the next snapshot.");

            Assert.That(p.ClampedFrames, Is.EqualTo(1), "the clamp must be observable");
            Assert.That(p.DiscardedCatchUpSeconds, Is.GreaterThan(4f),
                "the discarded time is what the counter is for");
        }

        /// <summary>
        /// And the time is DISCARDED, not carried: the next frames run at their own rate
        /// rather than replaying the stall one tick at a time.
        /// </summary>
        [Test]
        public void TimeOverTheCatchUpBudgetIsDiscardedRatherThanCarried()
        {
            var p = Predictor();
            p.RecordInput(1, 1f, 0f);
            p.Advance(5f);

            int afterStall = p.BaseTicksAdvanced;

            // Ten ordinary frames.
            for (var i = 0; i < 10; i++) p.Advance(1f / BaseHz);

            Assert.That(p.BaseTicksAdvanced - afterStall, Is.EqualTo(10),
                "the frames after a stall advanced more than one tick each, so the stall " +
                "was carried rather than discarded — the same burst, one frame later.");
        }

        // ── The two clocks are kept together, not merely started together ──

        /// <summary>
        /// A client whose clock has run ahead is steered back, a fraction of the error at a
        /// time, until it sits the intended lead ahead of the server's tick.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>SeedBaseTick</c> aligns the two clocks once, at join, and never speaks again.
        /// Measured against a live server on a build where every other counter was clean,
        /// the client's tick sat <b>456 ticks — 7.6 seconds —</b> past the newest snapshot
        /// it held and was still climbing, with <c>Snaps=0</c> and
        /// <c>LastCorrection=0.0000</c> throughout: the three-argument <c>Reconcile</c>
        /// replays that whole span as prediction lead, so a runaway clock raises nothing.
        /// The local player looks perfect while the server, and every other player, has it
        /// seconds behind its own screen.
        /// </para>
        /// </remarks>
        [Test]
        public void AClockThatHasRunAheadIsSteeredBackTowardTheServer()
        {
            var p = Predictor();
            p.SeedBaseTick(1000);

            // Run the clock forward on its own for a while, as a client whose snapshots
            // arrive slower than the server emits them does.
            for (var i = 0; i < 120; i++) p.Advance(1f / BaseHz);

            long before = p.BaseTick;

            // The server is 60 ticks behind where the client thinks it is.
            const int target = 4;
            long serverTick = before - 60 - target;

            // One steering call per snapshot, a second's worth.
            for (var i = 0; i < SendHz; i++)
            {
                p.SteerToServerTick(serverTick, target);
                for (var k = 0; k < SendEvery; k++) p.Advance(1f / BaseHz);
                serverTick += SendEvery;
            }

            long error = p.BaseTick - (serverTick + target);

            Assert.That(System.Math.Abs(error), Is.LessThan(60),
                $"after a second of steering the clock is still {error} ticks out. Nothing " +
                "else bounds this: the reconcile replays the whole gap as lead and reports " +
                "no correction while it grows.");

            Assert.That(p.HardResyncs, Is.Zero,
                "an error this size must be steered out, not jumped: a jump moves BaseTick " +
                "out from under the pending inputs, the held-from tick and the last-moved " +
                "tick, all of which are compared against it.");
        }

        /// <summary>
        /// Steering does not stall the simulation: the clock still advances while it is
        /// being pulled back, a few percent slow rather than stopped.
        /// </summary>
        [Test]
        public void SteeringSlowsTheClockRatherThanStoppingIt()
        {
            var p = Predictor();
            p.SeedBaseTick(1000);
            for (var i = 0; i < 120; i++) p.Advance(1f / BaseHz);

            long serverTick = p.BaseTick - 60;
            int before = p.BaseTicksAdvanced;

            for (var i = 0; i < SendHz; i++)
            {
                p.SteerToServerTick(serverTick, 0);
                for (var k = 0; k < SendEvery; k++) p.Advance(1f / BaseHz);
                serverTick += SendEvery;
            }

            int advanced = p.BaseTicksAdvanced - before;

            Assert.That(advanced, Is.InRange(BaseHz / 2, BaseHz),
                $"a second of real time advanced {advanced} base ticks while steering. " +
                "Stopping the clock to burn off the error is a visible freeze; the point of " +
                "steering is that the correction is spread thin enough not to read as one.");
        }

        /// <summary>
        /// An error too large to steer out is set outright, and the pending inputs go with
        /// it — they refer to ticks that no longer exist.
        /// </summary>
        [Test]
        public void AnErrorTooLargeToSteerIsResynchronisedOutright()
        {
            var p = Predictor();
            p.SeedBaseTick(1000);
            p.RecordInput(1, 1f, 0f);
            for (var i = 0; i < 600; i++) p.Advance(1f / BaseHz);   // ten seconds adrift

            p.SteerToServerTick(1000, 0);

            Assert.That(p.BaseTick, Is.EqualTo(1000),
                "an error past the hard-resync threshold must be set, not walked back over " +
                "a minute of visibly wrong speed");
            Assert.That(p.HardResyncs, Is.EqualTo(1));
            Assert.That(p.PendingCount, Is.Zero,
                "the pending inputs refer to base ticks that no longer exist; replaying " +
                "them against the new clock is replaying them at the wrong time");
        }

        // ── Evenness of the rendered motion ──

        /// <summary>
        /// Rendered motion must be even across frames under live conditions: a hold
        /// active, frames far faster than the base tick, and inputs slower than it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every other case here asserts <i>where</i> the avatar is. None asserted how
        /// evenly it gets there, and evenness is the thing the user actually reports — so
        /// a change that made predicted motion less even than no prediction at all passed
        /// the whole fixture. This measures the same max/mean frame delta the live harness
        /// reports, so a regression fails here rather than being discovered on a build.
        /// </para>
        /// <para>
        /// The failure it was written for: an input that moves nothing — coalesced by
        /// rule 1, or a deadzone — used to restart the interpolation with a zero step,
        /// which snaps the rendered position onto the simulated one and discards the
        /// in-flight interpolation of the step that tick did take.
        /// </para>
        /// </remarks>
        [Test]
        public void RenderedMotionIsEvenAcrossFramesWithAHoldActive()
        {
            var p = Predictor();

            const int framesPerSend = 20;              // 300 fps against 15 Hz sends
            float sendInterval = 1f / SendHz;
            float frame = sendInterval / framesPerSend;

            var deltas = new System.Collections.Generic.List<float>();
            float previous = p.Position.X;

            for (var i = 1; i <= 8; i++)
            {
                p.RecordInput(i, 1f, 0f);
                for (var f = 0; f < framesPerSend; f++)
                {
                    p.Advance(frame);
                    if (i == 1) { previous = p.Position.X; continue; }

                    deltas.Add(p.Position.X - previous);
                    previous = p.Position.X;
                }
            }

            float max = deltas.Max();
            float mean = deltas.Average();
            float burstiness = mean > 0f ? max / mean : float.NaN;

            Assert.That(burstiness, Is.LessThan(2.5f),
                $"frame-delta burstiness {burstiness:F2} (max {max:F5}, mean {mean:F5}). " +
                "1.00 is perfectly even. The avatar is arriving in lurches rather than " +
                "travelling, which is what a player calls not smooth and what no " +
                "correction counter can see.");
        }

        [Test]
        public void AnInputThatMovesNothingDoesNotSnapTheRenderedPosition()
        {
            var p = Predictor();
            p.RecordInput(1, 1f, 0f);

            // Part way through a tick, mid-interpolation.
            p.Advance((1f / BaseHz) * 0.4f);
            float before = p.Position.X;

            Assert.That(before, Is.LessThan(p.SimulatedPosition.X),
                "precondition: the step should be part-way shown");

            // An input on a tick the hold already stepped: coalesced, moves nothing.
            p.RecordInput(2, 1f, 0f);

            Assert.That(p.Position.X, Is.EqualTo(before).Within(1e-6f),
                "an input that moved nothing restarted the interpolation, snapping the " +
                "rendered position onto the simulated one. The step that tick actually " +
                "took was mid-flight and is now discarded.");
        }

        /// <summary>
        /// At a frame rate far above the tick rate, essentially every frame must move.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The existing evenness case runs 300 fps against a 15 Hz send rate, where the
        /// frames divide the tick exactly. A real client runs at whatever it runs at — a
        /// player build measured ~500 fps against a 60 Hz tick, 8.3 frames per tick, which
        /// divides into nothing. If the rendered position advances once per tick rather
        /// than once per frame, 7 of every 8.3 frames show nothing: 87.5% still, and a
        /// burstiness of about 8 from the ratio alone.
        /// </para>
        /// <para>
        /// That is a different assertion from "the endpoint is right", and it is the one
        /// the player is reporting. Asserted as a percentage because the percentage names
        /// its own cause: a figure near <c>(F-1)/F</c> is a per-tick render, whatever F is.
        /// </para>
        /// </remarks>
        [Test]
        public void AlmostEveryFrameMovesAtAFrameRateThatDoesNotDivideTheTick()
        {
            var p = Predictor();

            const float fps = 500f;                     // 8.33 frames per 60 Hz tick
            float frame = 1f / fps;
            int framesPerSend = (int)(fps / SendHz);

            float previous = p.Position.X;
            var still = 0;
            var counted = 0;

            for (var i = 1; i <= 10; i++)
            {
                p.RecordInput(i, 1f, 0f);
                for (var f = 0; f < framesPerSend; f++)
                {
                    p.Advance(frame);
                    if (i == 1) { previous = p.Position.X; continue; }

                    counted++;
                    if (p.Position.X - previous <= 1e-7f) still++;
                    previous = p.Position.X;
                }
            }

            float percent = 100f * still / counted;

            Assert.That(percent, Is.LessThan(25f),
                $"{percent:F1}% of {counted} frames showed no movement at {fps:F0} fps " +
                $"against a {BaseHz} Hz tick. Near {100f * (fps / BaseHz - 1f) / (fps / BaseHz):F0}% " +
                "would mean the rendered position advances once per tick rather than once " +
                "per frame, and the avatar is teleporting between still poses.");
        }

        // ── The correction is a clock meter ──

        /// <summary>
        /// Runs a full client/server loop and returns the steady-state correction, in
        /// steps, for a predictor whose clock runs at <paramref name="clockFactor"/> times
        /// real time.
        /// </summary>
        private static float SteadyStateCorrectionInSteps(float clockFactor) =>
            SteadyStateCorrectionInSteps(clockFactor, withSnapshotTick: true);

        private static float SteadyStateCorrectionInSteps(float clockFactor, bool withSnapshotTick)
        {
            MapBounds bounds = Bounds;
            var p = new LocalMovePredictor(new PredictionSettings(BaseHz, Speed, bounds));
            p.SetHoldTicks(SendEvery);
            p.Reconcile(Vec2.Zero, 0);

            var serverPos = Vec2.Zero;
            long serverTick = 0, heldFrom = 0, lastAck = 0;
            float heldX = 0f, heldY = 0f;

            void ServerStep(long tick, bool hasInput, float mx, float my, long inputTick)
            {
                if (hasInput)
                {
                    var probe = new EntityState { Position = serverPos, Speed = Speed, Dead = false };
                    if (MovementSystem.TryMove(in probe, mx, my, Dt, in bounds, out Vec2 moved)
                        is MoveResult.Accepted or MoveResult.Clamped)
                    {
                        serverPos = moved; heldX = mx; heldY = my; heldFrom = tick;
                    }
                    lastAck = inputTick;
                    return;
                }

                if (heldFrom != 0 && tick != heldFrom && tick - heldFrom <= HoldWindow)
                {
                    var probe = new EntityState { Position = serverPos, Speed = Speed, Dead = false };
                    if (MovementSystem.TryMove(in probe, heldX, heldY, Dt, in bounds, out Vec2 moved)
                        is MoveResult.Accepted or MoveResult.Clamped)
                    {
                        serverPos = moved;
                    }
                }
            }

            const float frame = 1f / 300f;
            float last = 0f;

            for (var interval = 1; interval <= 30; interval++)
            {
                p.RecordInput(interval, 1f, 0f);

                for (var k = 0; k < SendEvery; k++)
                {
                    serverTick++;
                    ServerStep(serverTick, k == 0, 1f, 0f, interval);
                }

                float real = 0f;
                while (real < 1f / SendHz)
                {
                    p.Advance(frame * clockFactor);
                    real += frame;
                }

                // Three-argument form: the snapshot's own base tick. Without it Reconcile
                // cannot tell a prediction LEAD from a disagreement, and there is a real one
                // now -- the client's clock has entered the tick after the snapshot's and the
                // hold has stepped it, which the next input will re-take. Discarding that is
                // issue #53, and with the two-argument form this measured a flat 1.00 step
                // at a clock that was exactly right.
                if (withSnapshotTick)
                {
                    p.Reconcile(serverPos, lastAck, serverTick);
                }
                else
                {
                    p.Reconcile(serverPos, lastAck);
                }
                last = p.LastCorrection / (Speed / BaseHz);
            }

            return last;
        }

        /// <summary>
        /// A client whose clock is right disagrees with the server by <b>nothing</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The property the whole prediction path exists to have, and nothing asserted it
        /// end to end. Individual pieces were pinned — the step, the hold, replay parity —
        /// but never client and server run against each other over many snapshots with the
        /// answer required to be exactly zero.
        /// </para>
        /// <para>
        /// It matters because a persistent correction is <i>not</i> harmless below the snap
        /// threshold. Under the threshold it is smoothed, which means a decaying offset
        /// injected on every snapshot — fifteen times a second — and that reads as jerk at
        /// any frame rate. "Smoothed" is not "unseen"; it was read that way here for
        /// several releases.
        /// </para>
        /// </remarks>
        [Test]
        public void ACorrectClockProducesNoCorrectionAtAll()
        {
            float steps = SteadyStateCorrectionInSteps(1.0f);

            Assert.That(steps, Is.EqualTo(0f).Within(0.01f),
                $"steady-state correction is {steps:F2} steps against a server the client " +
                "agrees with by construction. A persistent correction is smoothed rather " +
                "than snapped, so nothing jumps and no counter reads unhealthy — it is " +
                "visible only as uneven motion on the locally predicted entity.");
        }

        /// <summary>
        /// A clock that is wrong shows up in <c>TickError</c>, not in <c>LastCorrection</c>,
        /// and that is the whole point of measuring it there.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This case has been through three meanings, each true of the design at the time.
        /// It began pinning <c>correction_steps = (clockFactor - 1) * SendEvery</c> exactly,
        /// which made <c>LastCorrection</c> a clock instrument. Then the hold window widened
        /// and the reconcile started replaying the whole lead, so every factor read 1.00.
        /// Now the reconcile compares the snapshot against the client's own position at that
        /// snapshot's tick — and at the SAME tick number a fast clock and a slow one have
        /// taken the same steps, so a clock error produces no position error at all. It
        /// produces a TICK error, which is a different quantity and now has its own reading.
        /// </para>
        /// <para>
        /// <b>The instrument moved; the property did not.</b> A desynced client must still be
        /// visible on a counter, which is what this asserts — of <c>TickError</c>, the signal
        /// the steering acts on.
        /// </para>
        /// </remarks>
        [TestCase(1.25f)]
        [TestCase(1.5f)]
        [TestCase(2.0f)]
        public void AWrongClockShowsUpAsATickError(float clockFactor)
        {
            var p = Predictor();
            p.SeedBaseTick(1000);

            long serverTick = 1000;

            // A second of real time, with the client's clock running at clockFactor.
            for (var i = 0; i < SendHz; i++)
            {
                for (var k = 0; k < SendEvery; k++)
                {
                    p.Advance(clockFactor / BaseHz);
                    serverTick++;
                }
            }

            // One steering call reads the error before acting on it.
            p.SteerToServerTick(serverTick, 0);

            // A second at clockFactor is (clockFactor - 1) * BaseHz ticks of error -- 15, 30
            // and 60 at the three factors. Half of that is the floor, which separates a
            // clock fault from ordinary rounding by an order of magnitude without pinning
            // the arithmetic.
            float expected = (clockFactor - 1f) * BaseHz;

            Assert.That(p.TickError, Is.GreaterThan(expected * 0.5f),
                $"a {clockFactor:F2}x clock over a second left a tick error of {p.TickError}, " +
                $"against about {expected:F0} owed. A clock that runs at the wrong rate has " +
                "to be visible somewhere, or a desynced client looks healthy on every " +
                "counter the package exposes.");
        }

        /// <summary>
        /// And a clock that is right produces neither a tick error nor a correction.
        /// </summary>
        [Test]
        public void ACorrectClockProducesNoTickError()
        {
            var p = Predictor();
            p.SeedBaseTick(1000);

            long serverTick = 1000;
            for (var i = 0; i < SendHz; i++)
            {
                for (var k = 0; k < SendEvery; k++)
                {
                    p.Advance(1f / BaseHz);
                    serverTick++;
                }

                p.SteerToServerTick(serverTick, 0);
            }

            Assert.That(System.Math.Abs(p.TickError), Is.LessThanOrEqualTo(2),
                $"a correct clock drifted to a tick error of {p.TickError}");
        }

        /// <summary>
        /// The hold does not wait to be measured. A predictor that has never been told a
        /// snapshot gap holds exactly as one that has.
        /// </summary>
        /// <remarks>
        /// The inverse of what this case asserted before, for the reason the window changed:
        /// it was inferred from the snapshot gap, so until a gap had been observed the safe
        /// thing was to hold not at all. That safety had a cost paid on every session — the
        /// first snapshot after joining is a keyframe emitted off the tick boundary, so the
        /// measured gap could pin at 1 and switch the hold off for the whole session
        /// (<c>TickRateEstimator.SnapshotTickGap</c>). Reading the window from the shared
        /// constant removes the measurement, and with it the window in which the client
        /// deliberately disagreed with the server.
        /// </remarks>
        [Test]
        public void TheHoldRunsWithoutAnyMeasuredSnapshotGap()
        {
            var p = new LocalMovePredictor(new PredictionSettings(BaseHz, Speed, Bounds));
            p.Reconcile(Vec2.Zero, 0);

            p.RecordInput(1, 1f, 0f);
            for (var k = 0; k < SendEvery; k++) p.Advance(1f / BaseHz);

            // The input's own step plus one per advanced tick: the window is 15, so none of
            // them expires.
            Assert.That(p.SimulatedPosition.X,
                Is.EqualTo((Speed / BaseHz) * (1 + SendEvery)).Within(1e-5f),
                "a predictor that was never told a snapshot gap stepped differently from one " +
                "that was. The hold window is a shared constant now; nothing about it is " +
                "measured, so nothing about it can be unmeasured.");
        }
    }
}
