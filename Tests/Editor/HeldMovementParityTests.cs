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
    /// steps again while <c>baseTick - heldFrom &lt; holdTicks</c>. <c>holdTicks</c> is
    /// <c>_rates.WorldEvery</c> — base ticks per world tick, four at 60/15.
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
        private const int HoldTicks = BaseHz / SendHz;
        private const float Speed = 5f;

        private static float Dt => MovementSystem.DeltaTimeForTickRate(BaseHz);
        private static MapBounds Bounds => MapBounds.Default;

        private static LocalMovePredictor Predictor()
        {
            var p = new LocalMovePredictor(new PredictionSettings(BaseHz, Speed, Bounds));
            p.SetHoldTicks(HoldTicks);
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
            long tick = 0;

            for (var i = 0; i < inputs; i++)
            {
                for (var k = 0; k < HoldTicks; k++)
                {
                    tick++;
                    bool isInputTick = k == 0;

                    if (isInputTick)
                    {
                        if (StepOnce(ref position, moveX, moveY)) heldFrom = tick;
                        continue;
                    }

                    if (heldFrom != 0 && tick - heldFrom < HoldTicks)
                    {
                        StepOnce(ref position, moveX, moveY);
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
            for (var k = 0; k < HoldTicks; k++)
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

            Assert.That(p.SimulatedPosition.X, Is.EqualTo(Speed).Within(0.01f),
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
            for (var k = 0; k < HoldTicks * 3; k++)
            {
                p.Advance(1f / BaseHz);
            }

            Assert.That(p.SimulatedPosition.X, Is.EqualTo(afterMove).Within(1e-5f),
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

            Assert.That(p.Position.X, Is.EqualTo(p.SimulatedPosition.X).Within(1e-4f),
                $"rendered {p.Position.X:F4} against simulated {p.SimulatedPosition.X:F4}. " +
                "The smoothing span is longer than the interval between steps, so the " +
                "avatar is permanently behind its own prediction.");
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

        // ── Rule 3: a step covers the time since the entity last moved ──

        /// <summary>
        /// A gap longer than the hold window must be paid for by the next step, not lost.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The third rule of the server's movement model: <c>dt</c> is
        /// <c>min(now - last_move_tick, cap) / tick_rate</c>. A client sending every tick
        /// never sees it, because <c>last_move_tick == now - 1</c> always. A client
        /// sending at 15 Hz into a 60 Hz base tick sees it whenever arrival jitter opens
        /// a gap past the hold window — and the harness measured send burstiness of 12 to
        /// 49, so those gaps are the normal case, not the exception.
        /// </para>
        /// <para>
        /// This is the defect that survived three releases of hold work at an unchanged
        /// magnitude. The hold fixed how many steps are taken; this fixes how much time
        /// each one covers, and the two are independent. A structural difference produces
        /// a constant correction, which is exactly what a constant 0.1667 was.
        /// </para>
        /// </remarks>
        [Test]
        public void AGapLongerThanTheHoldIsPaidForByTheNextStep()
        {
            var p = Predictor();

            // One input, its hold runs out, then a long silence, then one more input.
            p.RecordInput(1, 1f, 0f);
            for (var k = 0; k < HoldTicks; k++) p.Advance(1f / BaseHz);

            float afterFirst = p.SimulatedPosition.X;

            const int silentTicks = 5;
            for (var k = 0; k < silentTicks; k++) p.Advance(1f / BaseHz);

            Assert.That(p.SimulatedPosition.X, Is.EqualTo(afterFirst).Within(1e-5f),
                "the hold must have expired during the silence");

            p.RecordInput(2, 1f, 0f);

            // The step that lands after the silence covers every tick since the entity
            // last moved -- the hold's final tick -- not one tick.
            float banked = p.SimulatedPosition.X - afterFirst;
            float oneStep = Speed / BaseHz;

            Assert.That(banked, Is.GreaterThan(oneStep * 1.5f),
                $"the step after a {silentTicks}-tick silence moved {banked:F4}, about one " +
                $"step of {oneStep:F4}. The simulated time the gap represents was dropped, " +
                "so the client falls behind a server that banks it -- by a constant, on " +
                "every gap, which no rate fix can reach.");
        }

        [Test]
        public void BankedTimeIsCappedSoASilentClientCannotTeleport()
        {
            var p = Predictor();
            p.RecordInput(1, 1f, 0f);
            for (var k = 0; k < HoldTicks; k++) p.Advance(1f / BaseHz);

            float afterFirst = p.SimulatedPosition.X;

            // Ten seconds of silence -- far past the cap.
            for (var k = 0; k < BaseHz * 10; k++) p.Advance(1f / BaseHz);

            p.RecordInput(2, 1f, 0f);

            float banked = p.SimulatedPosition.X - afterFirst;
            float capped = (Speed / BaseHz) * p.MaxBankedTicks;

            Assert.That(banked, Is.EqualTo(capped).Within(1e-4f),
                $"a step after ten seconds of silence moved {banked:F4}; the cap allows " +
                $"{capped:F4}. The bound is part of the movement model, not a server-side " +
                "valve -- a client banking unbounded time reconciles against a server " +
                "that does not, on exactly the frames where the network was worst.");
        }

        [Test]
        public void TheCapMatchesTheSharedConstant()
        {
            var p = Predictor();

            Assert.That(p.MaxBankedTicks,
                Is.EqualTo(GameConstants.MaxBankedMovementTicks(BaseHz)),
                "the cap must come from Shared.GameLogic and not from a second copy on " +
                "the client. A client-side copy of a server constant is the defect this " +
                "package has now shipped four times.");
        }

        [Test]
        public void EvenSendsAtTheBaseRateAreUnaffectedByRuleThree()
        {
            var p = Predictor();

            // A client sending every base tick always has lastMoveTick == now - 1, so
            // every step is exactly one timestep and rule 3 is invisible to it. This is
            // why the server could add it without regenerating the golden vectors.
            for (var i = 1; i <= BaseHz; i++)
            {
                p.RecordInput(i, 1f, 0f);
                p.Advance(1f / BaseHz);
            }

            // One step of slack, and only one: the loop opens by stepping on the tick it
            // starts on and then advances BaseHz times, so it spans BaseHz + 1 ticks of
            // movement for BaseHz sends. The point of the case is that rule 3 adds no
            // banked distance -- which would be a multiple, not a single step.
            float oneStep = Speed / BaseHz;

            Assert.That(p.SimulatedPosition.X, Is.EqualTo(Speed).Within(oneStep + 0.001f),
                $"a client sending every tick travelled {p.SimulatedPosition.X:F4} against " +
                $"a configured {Speed}. It always has lastMoveTick == now - 1, so every " +
                "step must be one plain timestep and rule 3 must add nothing.");
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

        [Test]
        public void NoHoldMeasuredMeansTheOldOneStepBehaviour()
        {
            var p = new LocalMovePredictor(new PredictionSettings(BaseHz, Speed, Bounds));
            p.Reconcile(Vec2.Zero, 0);

            Assert.That(p.HoldTicks, Is.EqualTo(1),
                "an unmeasured hold window must behave as no hold at all. Guessing a " +
                "window is how a client ends up wrong in a new way instead of the old one.");

            p.RecordInput(1, 1f, 0f);
            for (var k = 0; k < HoldTicks; k++) p.Advance(1f / BaseHz);

            Assert.That(p.SimulatedPosition.X, Is.EqualTo(Speed / BaseHz).Within(1e-5f),
                "with no hold measured, one input must still produce exactly one step");
        }
    }
}
