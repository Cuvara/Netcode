using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Covers client-side prediction of local player movement, and above all the one
    /// property the whole design rests on: <b>replay is bit-identical to the server</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference simulation in <see cref="ServerWalk"/> is not a second copy of the
    /// movement rule — it is a loop around the same
    /// <see cref="MovementSystem.TryMove"/> the server's <c>InputHandler</c> calls, with
    /// the same per-input <c>dt</c>. That is what makes the equality assertions
    /// meaningful: if the predictor drifted to a hand-rolled
    /// <c>pos += dir * speed * dt</c>, the FMA-contraction difference that
    /// <c>Integrate</c>'s split multiply exists to prevent would show up here as a
    /// last-place mismatch. Exact equality is asserted deliberately — a tolerance would
    /// hide precisely the class of bug this is guarding.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class LocalMovePredictorTests
    {
        private const int TickRate = 15;

        /// <summary>The base timestep, for advancing a tick between inputs.</summary>
        private static float Dt => MovementSystem.DeltaTimeForTickRate(TickRate);
        private const float Speed = 5f;

        private static MapBounds Bounds => MapBounds.Default;

        private static PredictionSettings Settings() =>
            new PredictionSettings(TickRate, Speed, Bounds);

        private static LocalMovePredictor Seeded(Vec2 start)
        {
            var p = new LocalMovePredictor(Settings());
            p.Reconcile(start, 0);
            return p;
        }

        /// <summary>
        /// What the server would do with this input sequence: one
        /// <see cref="MovementSystem.TryMove"/> per accepted input, at the fixed timestep.
        /// </summary>
        private static Vec2 ServerWalk(Vec2 from, (float x, float y)[] inputs) =>
            ServerWalk(from, inputs, Speed);

        /// <summary>
        /// The server's movement model, restated at a given speed. One copy: a second
        /// inline copy of this loop is how a test kept asserting the pre-rule-3 model
        /// after the shared one had moved on.
        /// </summary>
        private static Vec2 ServerWalk(Vec2 from, (float x, float y)[] inputs, float speed)
        {
            float dt = MovementSystem.DeltaTimeForTickRate(TickRate);
            int cap = GameConstants.MaxBankedMovementTicks(TickRate);
            Vec2 pos = from;

            // One input per tick, and a step covers the time since this entity last
            // actually moved (rule 3), bounded by the shared cap. A fixed dt here would
            // model a server that predates the rule -- and because clearing the
            // last-moved tick reproduces a fixed dt exactly, that difference is invisible
            // unless a walk contains an input that does not move, which this one does.
            long tick = 0;
            long lastMove = 0;

            foreach (var (x, y) in inputs)
            {
                tick++;

                float stepDt = dt;
                if (lastMove != 0 && tick > lastMove)
                {
                    long elapsed = tick - lastMove;
                    if (elapsed > cap) elapsed = cap;
                    stepDt = dt * elapsed;
                }

                var probe = new EntityState { Position = pos, Speed = speed, Dead = false };
                var result = MovementSystem.TryMove(in probe, x, y, stepDt, Bounds, out var moved);
                if (result is MoveResult.Accepted or MoveResult.Clamped)
                {
                    pos = moved;
                    lastMove = tick;
                }
            }

            return pos;
        }

        private static readonly (float x, float y)[] Walk =
        {
            (1f, 0f), (1f, 0f), (0.5f, 0.5f), (1f, 1f), (0f, 1f),
            (-1f, 0.25f), (0.3f, -0.9f), (1f, 1f), (0f, 0f), (-0.7f, -0.7f),
        };

        // ── The determinism property ──

        [Test]
        public void PredictingForwardMatchesTheServerExactly()
        {
            var predictor = Seeded(Vec2.Zero);

            for (var i = 0; i < Walk.Length; i++)
            {
                predictor.RecordInput(i + 1, Walk[i].x, Walk[i].y);
                // One tick between inputs. Without it every input lands on the same base
                // tick and rule 1 coalesces them to a single step -- which is correct, and
                // not what a walk of four separate inputs is meant to model.
                predictor.Advance(Dt);
            }

            Vec2 expected = ServerWalk(Vec2.Zero, Walk);

            Assert.That(predictor.SimulatedPosition.X, Is.EqualTo(expected.X),
                "predicted X must equal the server's bit-for-bit, not approximately");
            Assert.That(predictor.SimulatedPosition.Y, Is.EqualTo(expected.Y),
                "predicted Y must equal the server's bit-for-bit, not approximately");
        }

        [Test]
        public void ReplayAfterReconcileReproducesTheSamePositionAsPredictingForward()
        {
            // Path A: predict all ten inputs with no correction in between.
            var forward = Seeded(Vec2.Zero);
            for (var i = 0; i < Walk.Length; i++)
            {
                forward.RecordInput(i + 1, Walk[i].x, Walk[i].y);
                // One tick between inputs. Without it every input lands on the same base
                // tick and rule 1 coalesces them to a single step -- which is correct, and
                // not what a walk of four separate inputs is meant to model.
                forward.Advance(Dt);
            }

            // Path B: same inputs, but the server acknowledges the first four midway, so
            // the last six are rewound and replayed.
            var replayed = Seeded(Vec2.Zero);
            for (var i = 0; i < Walk.Length; i++)
            {
                replayed.RecordInput(i + 1, Walk[i].x, Walk[i].y);
                // One tick between inputs. Without it every input lands on the same base
                // tick and rule 1 coalesces them to a single step -- which is correct, and
                // not what a walk of four separate inputs is meant to model.
                replayed.Advance(Dt);
            }

            var ackedThrough4 = ServerWalk(Vec2.Zero, new[] { Walk[0], Walk[1], Walk[2], Walk[3] });
            replayed.Reconcile(ackedThrough4, 4);

            Assert.That(replayed.SimulatedPosition.X, Is.EqualTo(forward.SimulatedPosition.X),
                "rewind-and-replay must land exactly where uninterrupted prediction did");
            Assert.That(replayed.SimulatedPosition.Y, Is.EqualTo(forward.SimulatedPosition.Y));
            Assert.That(replayed.PendingCount, Is.EqualTo(6), "inputs 5..10 remain unacknowledged");
            Assert.That(replayed.LastCorrection, Is.Zero,
                "a server that agrees with the client produces no correction at all");
        }

        [Test]
        public void ReplayIsRepeatable()
        {
            var a = Seeded(new Vec2(3f, -2f));
            var b = Seeded(new Vec2(3f, -2f));

            for (var i = 0; i < Walk.Length; i++)
            {
                a.RecordInput(i + 1, Walk[i].x, Walk[i].y);
                // One tick between inputs. Without it every input lands on the same base
                // tick and rule 1 coalesces them to a single step -- which is correct, and
                // not what a walk of four separate inputs is meant to model.
                a.Advance(Dt);
                b.RecordInput(i + 1, Walk[i].x, Walk[i].y);
                // One tick between inputs. Without it every input lands on the same base
                // tick and rule 1 coalesces them to a single step -- which is correct, and
                // not what a walk of four separate inputs is meant to model.
                b.Advance(Dt);
            }

            var anchor = ServerWalk(new Vec2(3f, -2f), new[] { Walk[0], Walk[1] });
            a.Reconcile(anchor, 2);
            b.Reconcile(anchor, 2);

            Assert.That(a.SimulatedPosition, Is.EqualTo(b.SimulatedPosition),
                "same start, same inputs, same ack must give the same position every time");
        }

        [Test]
        public void DiagonalInputIsNormalizedExactlyAsTheServerNormalizesIt()
        {
            // The trap this pins: calling Integrate directly with a raw (1,1) would move
            // sqrt(2) times too far. TryMove runs ResolveDirection first, as the server does.
            var predictor = Seeded(Vec2.Zero);
            predictor.RecordInput(1, 1f, 1f);

            float dt = MovementSystem.DeltaTimeForTickRate(TickRate);
            float travelled = predictor.SimulatedPosition.Magnitude;

            Assert.That(travelled, Is.EqualTo(Speed * dt).Within(1e-5f),
                "diagonal input must cover one step's distance, not 1.414 of one");
        }

        // ── Acknowledgement bookkeeping ──

        [Test]
        public void AcknowledgedInputsAreDropped()
        {
            var predictor = Seeded(Vec2.Zero);
            for (var i = 1; i <= 5; i++)
            {
                predictor.RecordInput(i, 1f, 0f);
            }

            Assert.That(predictor.PendingCount, Is.EqualTo(5));

            predictor.Reconcile(ServerWalk(Vec2.Zero, new[] { Walk[0], Walk[0], Walk[0] }), 3);

            Assert.That(predictor.PendingCount, Is.EqualTo(2),
                "ticks 1..3 are reflected in the authoritative position and must not replay");
        }

        [Test]
        public void AckOfEverythingLeavesNothingToReplay()
        {
            var predictor = Seeded(Vec2.Zero);
            for (var i = 1; i <= 4; i++)
            {
                predictor.RecordInput(i, 1f, 0f);
            }

            var final = ServerWalk(Vec2.Zero, new[] { Walk[0], Walk[0], Walk[0], Walk[0] });
            predictor.Reconcile(final, 4);

            Assert.That(predictor.PendingCount, Is.Zero);
            Assert.That(predictor.SimulatedPosition, Is.EqualTo(final),
                "with nothing pending the predicted position IS the authoritative one");
        }

        [Test]
        public void NonAdvancingTickIsRefusedLikeTheServerRefusesIt()
        {
            var predictor = Seeded(Vec2.Zero);
            predictor.RecordInput(5, 1f, 0f);
            var after = predictor.SimulatedPosition;

            predictor.RecordInput(5, 1f, 0f);   // repeat
            predictor.RecordInput(3, 1f, 0f);   // reorder

            Assert.That(predictor.SimulatedPosition, Is.EqualTo(after),
                "the server ignores an input whose tick does not advance, so predicting " +
                "one would be predicting a move that never happens");
            Assert.That(predictor.RejectedInputs, Is.EqualTo(2));
            Assert.That(predictor.PendingCount, Is.EqualTo(1));
        }

        // ── Corrections ──

        [Test]
        public void SmallDisagreementIsSmoothedNotSnapped()
        {
            var predictor = Seeded(Vec2.Zero);
            predictor.RecordInput(1, 1f, 0f);

            // Server ended up slightly elsewhere than predicted, well under one tick step.
            predictor.Reconcile(new Vec2(0.05f, 0f), 1);

            Assert.That(predictor.LastCorrection, Is.GreaterThan(0f));
            Assert.That(predictor.LastCorrection, Is.LessThan(LocalMovePredictor.SmoothingThreshold));
            Assert.That(predictor.SmoothedCorrections, Is.EqualTo(1));
            Assert.That(predictor.Snaps, Is.Zero);

            // The simulated position obeys the server immediately; only the rendered one lags.
            Assert.That(predictor.SimulatedPosition, Is.EqualTo(new Vec2(0.05f, 0f)));
            Assert.That(predictor.Position, Is.Not.EqualTo(predictor.SimulatedPosition),
                "the render position still carries the error, which is what makes it smooth");
        }

        [Test]
        public void LargeDisagreementSnaps()
        {
            var predictor = Seeded(Vec2.Zero);
            predictor.RecordInput(1, 1f, 0f);

            predictor.Reconcile(new Vec2(40f, 40f), 1);

            Assert.That(predictor.LastCorrection, Is.GreaterThan(LocalMovePredictor.SmoothingThreshold));
            Assert.That(predictor.Snaps, Is.EqualTo(1));
            Assert.That(predictor.SmoothedCorrections, Is.Zero);
            Assert.That(predictor.Position, Is.EqualTo(predictor.SimulatedPosition),
                "a snap leaves no residual offset — gliding in from a place the server " +
                "has already ruled out is worse than one honest jump");
        }

        [Test]
        public void SmoothedOffsetDecaysToExactlyZero()
        {
            var predictor = Seeded(Vec2.Zero);
            predictor.RecordInput(1, 1f, 0f);
            predictor.Reconcile(new Vec2(0.05f, 0f), 1);

            Assert.That(predictor.SmoothingOffset, Is.Not.EqualTo(Vec2.Zero));

            for (var frame = 0; frame < 60; frame++)
            {
                predictor.Advance(1f / 60f);
            }

            // Both, deliberately.
            //
            // #25 replaced `Position == SimulatedPosition` with this offset assertion, and
            // that was the better test independently of which smoothing won: the intent
            // is "the correction settles at exactly zero", and the equality was a proxy
            // that happened to hold. Asserting the intent directly is kept.
            //
            // The equality is kept too, because under interpolation it is true again and
            // it covers something the offset alone does not — that the step is fully
            // shown as well as the correction retired. Under #25's extrapolating version
            // it could not hold, which is why it had to go there.
            Assert.That(predictor.SmoothingOffset, Is.EqualTo(Vec2.Zero),
                "the correction must settle at exactly zero rather than approaching it");

            Assert.That(predictor.Position, Is.EqualTo(predictor.SimulatedPosition),
                "the offset must settle exactly, not approach zero forever");
        }

        [Test]
        public void SmoothingIsFrameRateIndependent()
        {
            var slow = Seeded(Vec2.Zero);
            var fast = Seeded(Vec2.Zero);
            slow.RecordInput(1, 1f, 0f);
            fast.RecordInput(1, 1f, 0f);
            slow.Reconcile(new Vec2(0.2f, 0f), 1);
            fast.Reconcile(new Vec2(0.2f, 0f), 1);

            // One second of wall clock, at 30 fps and at 144 fps.
            for (var i = 0; i < 30; i++) slow.Advance(1f / 30f);
            for (var i = 0; i < 144; i++) fast.Advance(1f / 144f);

            Assert.That(slow.Position.X, Is.EqualTo(fast.Position.X).Within(1e-4f),
                "a correction must not resolve faster on a faster machine");
        }

        // ── Refusal ──

        [Test]
        public void PredictorRefusesWithoutATickRate()
        {
            var predictor = new LocalMovePredictor(new PredictionSettings(0, Speed, Bounds));
            Assert.That(predictor.IsEnabled, Is.False);
        }

        [Test]
        public void PredictorRefusesWithoutASpeed()
        {
            var predictor = new LocalMovePredictor(new PredictionSettings(TickRate, 0f, Bounds));
            Assert.That(predictor.IsEnabled, Is.False);
        }

        [Test]
        public void PredictorRefusesOnANonFiniteSpeed()
        {
            Assert.That(new LocalMovePredictor(
                new PredictionSettings(TickRate, float.NaN, Bounds)).IsEnabled, Is.False);
            Assert.That(new LocalMovePredictor(
                new PredictionSettings(TickRate, float.PositiveInfinity, Bounds)).IsEnabled, Is.False);
        }

        [Test]
        public void ADisabledPredictorFollowsTheServerAndPredictsNothing()
        {
            var predictor = new LocalMovePredictor(new PredictionSettings(0, 0f, Bounds));

            predictor.Reconcile(new Vec2(1f, 2f), 0);
            predictor.RecordInput(1, 1f, 0f);

            Assert.That(predictor.PendingCount, Is.Zero, "a refusing predictor buffers nothing");
            Assert.That(predictor.Position, Is.EqualTo(new Vec2(1f, 2f)),
                "it reports the authoritative position, which is the correct fallback — " +
                "an approximation would drift silently instead of being visibly absent");
        }

        // ── Edges ──

        [Test]
        public void NothingIsPredictedBeforeTheFirstSnapshot()
        {
            var predictor = new LocalMovePredictor(Settings());

            predictor.RecordInput(1, 1f, 0f);

            Assert.That(predictor.PendingCount, Is.Zero,
                "there is no position to predict from until the server has sent one");
            Assert.That(predictor.SimulatedPosition, Is.EqualTo(Vec2.Zero));
        }

        [Test]
        public void TheFirstSnapshotSeedsWithoutCountingAsACorrection()
        {
            var predictor = new LocalMovePredictor(Settings());
            predictor.Reconcile(new Vec2(12f, 34f), 0);

            Assert.That(predictor.SimulatedPosition, Is.EqualTo(new Vec2(12f, 34f)));
            Assert.That(predictor.LastCorrection, Is.Zero);
            Assert.That(predictor.Snaps, Is.Zero, "a spawn position is not a mispredict");
        }

        [Test]
        public void MovementIsClampedToTheMapExactlyAsTheServerClampsIt()
        {
            var corner = new Vec2(Bounds.MaxX, Bounds.MaxY);
            var predictor = Seeded(corner);

            for (var i = 1; i <= 20; i++)
            {
                predictor.RecordInput(i, 1f, 1f);
            }

            Assert.That(predictor.SimulatedPosition.X, Is.EqualTo(Bounds.MaxX));
            Assert.That(predictor.SimulatedPosition.Y, Is.EqualTo(Bounds.MaxY));
        }

        [Test]
        public void OverflowingTheBufferIsCountedRatherThanHidden()
        {
            var predictor = Seeded(Vec2.Zero);

            for (var i = 1; i <= LocalMovePredictor.Capacity + 10; i++)
            {
                predictor.RecordInput(i, 1f, 0f);
            }

            Assert.That(predictor.PendingCount, Is.EqualTo(LocalMovePredictor.Capacity));
            Assert.That(predictor.DroppedInputs, Is.EqualTo(10),
                "a full buffer means the server stopped acknowledging — that is a fact " +
                "worth surfacing, not one to absorb");
        }

        // ── Server-supplied speed (wire.proto field 9) ──

        [Test]
        public void ServerSpeedReplacesTheConfiguredDefault()
        {
            var predictor = Seeded(Vec2.Zero);
            Assert.That(predictor.EffectiveSpeed, Is.EqualTo(Speed));

            predictor.SetServerSpeed(20f);
            Assert.That(predictor.EffectiveSpeed, Is.EqualTo(20f));

            predictor.RecordInput(1, 1f, 0f);

            float dt = MovementSystem.DeltaTimeForTickRate(TickRate);
            Assert.That(predictor.SimulatedPosition.X, Is.EqualTo(20f * dt).Within(1e-5f),
                "replay must integrate at the speed the server reported, not the default");
        }

        /// <summary>
        /// The rule that makes the field safe to add to a live protocol: proto3 elides a
        /// zero float, so an older server is indistinguishable from an immobile entity.
        /// Accepting the zero would pin the predicted speed to zero and stop the local
        /// player moving — strictly worse than the drift the field exists to fix.
        /// </summary>
        [Test]
        public void NonPositiveServerSpeedIsIgnoredBecauseItMeansNotSent()
        {
            var predictor = Seeded(Vec2.Zero);

            predictor.SetServerSpeed(0f);
            Assert.That(predictor.EffectiveSpeed, Is.EqualTo(Speed));

            predictor.SetServerSpeed(-3f);
            Assert.That(predictor.EffectiveSpeed, Is.EqualTo(Speed));

            predictor.SetServerSpeed(float.NaN);
            Assert.That(predictor.EffectiveSpeed, Is.EqualTo(Speed));
        }

        [Test]
        public void ServerSpeedStillMatchesTheServerExactly()
        {
            const float serverSpeed = 12.5f;

            var predictor = new LocalMovePredictor(new PredictionSettings(TickRate, Speed, Bounds));
            predictor.Reconcile(Vec2.Zero, 0);
            predictor.SetServerSpeed(serverSpeed);

            for (var i = 0; i < Walk.Length; i++)
            {
                predictor.RecordInput(i + 1, Walk[i].x, Walk[i].y);
                // One tick between inputs. Without it every input lands on the same base
                // tick and rule 1 coalesces them to a single step -- which is correct, and
                // not what a walk of four separate inputs is meant to model.
                predictor.Advance(Dt);
            }

            // Same reference walk, through the one server model, at the server's speed.
            Vec2 pos = ServerWalk(Vec2.Zero, Walk, serverSpeed);

            Assert.That(predictor.SimulatedPosition.X, Is.EqualTo(pos.X),
                "bit-exact at the server's speed, not merely close");
            Assert.That(predictor.SimulatedPosition.Y, Is.EqualTo(pos.Y));
        }

        [Test]
        public void ResetReturnsToTheConfiguredFallbackSpeed()
        {
            var predictor = Seeded(Vec2.Zero);
            predictor.SetServerSpeed(20f);

            predictor.Reset();

            Assert.That(predictor.EffectiveSpeed, Is.EqualTo(Speed),
                "the previous session's speed belonged to a different entity");
        }

        [Test]
        public void ResetForgetsEverything()
        {
            var predictor = Seeded(new Vec2(5f, 5f));
            predictor.RecordInput(1, 1f, 0f);
            predictor.Reconcile(new Vec2(50f, 50f), 1);

            predictor.Reset();

            Assert.That(predictor.PendingCount, Is.Zero);
            Assert.That(predictor.Snaps, Is.Zero);
            Assert.That(predictor.SimulatedPosition, Is.EqualTo(Vec2.Zero));

            // And the tick counter, so a new session starting at tick 1 is not refused.
            predictor.Reconcile(new Vec2(1f, 1f), 0);
            predictor.RecordInput(1, 1f, 0f);
            Assert.That(predictor.PendingCount, Is.EqualTo(1));
            Assert.That(predictor.RejectedInputs, Is.Zero);
        }
    }
}
