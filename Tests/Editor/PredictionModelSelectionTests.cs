using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Cuvara.Netcode.Snapshot;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;
using Shared.GameLogic.World;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Which movement model <see cref="LocalMovePredictor"/> runs, chosen by the protocol the
    /// server echoed -- the planar <see cref="MovementSystem"/> for protocol 2, the 3D
    /// <see cref="CharacterMotor"/> for protocol 3 (ADR-28) -- and the projectile prediction
    /// helper (ADR-29).
    /// </summary>
    /// <remarks>
    /// The motor assertions compare against <see cref="CharacterMotor.Step"/> run directly, with
    /// exact equality, for the reason <see cref="LocalMovePredictorTests"/> gives: a tolerance
    /// would hide the last-place drift that replaying through the shared library exists to
    /// prevent.
    /// </remarks>
    public class PredictionModelSelectionTests
    {
        private const int TickRate = 60;
        private const float Speed = 5f;

        private static float Dt => MovementSystem.DeltaTimeForTickRate(TickRate);

        private static LocalMovePredictor Predictor(uint serverProtocol)
        {
            var p = new LocalMovePredictor(new PredictionSettings(TickRate, Speed, MapBounds.Default));
            p.UseServerProtocol(serverProtocol);
            return p;
        }

        // ── selection ────────────────────────────────────────────────────────────

        [Test]
        public void Protocol2AndUnversionedServersGetThePlanarModel()
        {
            Assert.That(Predictor(2u).UsesCharacterMotor, Is.False);
            Assert.That(Predictor(0u).UsesCharacterMotor, Is.False);
            Assert.That(new LocalMovePredictor(new PredictionSettings(TickRate, Speed, MapBounds.Default))
                .UsesCharacterMotor, Is.False, "the default is the planar model");
        }

        [Test]
        public void AProtocol3ServerGetsTheCharacterMotor_OverAFlatMapByDefault()
        {
            var p = Predictor(3u);
            Assert.That(p.UsesCharacterMotor, Is.True);
            Assert.That(p.Geometry, Is.Not.Null);
            Assert.That(p.Geometry.IsFlat, Is.True);
        }

        [Test]
        public void ChangingTheModelResetsThePredictor_KeepingItDoesNot()
        {
            var p = Predictor(2u);
            p.Reconcile(new Vec2(1f, 1f), 0);
            p.Advance(Dt);
            p.RecordInput(1, 1f, 0f);
            Assert.That(p.PendingCount, Is.EqualTo(1));

            p.UseServerProtocol(2u);
            Assert.That(p.PendingCount, Is.EqualTo(1), "same model: nothing to reset");

            p.UseServerProtocol(3u);
            Assert.That(p.PendingCount, Is.Zero, "a body predicted in 2D is not a 3D starting point");
            Assert.That(p.UsesCharacterMotor, Is.True);
        }

        // ── the planar model is what it was ──────────────────────────────────────

        [Test]
        public void ThePlanarModelIgnoresJumpAndHeight()
        {
            var p = Predictor(2u);
            p.Reconcile(new Vec3(0f, 0f, 5f), 3f, 0, 0);
            p.Advance(Dt);
            p.RecordInput(1, 1f, 0f, jump: true);

            Assert.That(p.SimulatedPosition3.Z, Is.EqualTo(0f));
            Assert.That(p.IsGrounded, Is.True);
            Assert.That(p.VerticalVelocity, Is.EqualTo(0f));

            // The move itself is MovementSystem's, to the bit.
            var probe = new EntityState { Position = Vec2.Zero, Speed = Speed, Dead = false };
            MovementSystem.TryMove(in probe, 1f, 0f, Dt, MapBounds.Default, out Vec2 expected);
            Assert.That(p.SimulatedPosition, Is.EqualTo(expected));
        }

        // ── the motor ────────────────────────────────────────────────────────────

        [Test]
        public void UnderTheMotorAnInputStepIsCharacterMotorStep_ToTheBit()
        {
            var p = Predictor(3u);
            p.Reconcile(new Vec3(0f, 0f, 0f), 0f, 0, 0);
            p.Advance(Dt);
            p.RecordInput(1, 1f, 0f, jump: true);

            var geo = MapGeometry.Flat(MapBounds.Default);
            CharacterMotor.Step(MotorState.StandingAt(Vec3.Zero), 1f, 0f, true, Speed, Dt, geo,
                MotorParams.Default, out MotorState expected);

            Assert.That(p.SimulatedPosition3, Is.EqualTo(expected.Position));
            Assert.That(p.VerticalVelocity, Is.EqualTo(expected.VelocityZ));
            Assert.That(p.IsGrounded, Is.False, "the jump left the ground");
            Assert.That(p.SimulatedPosition3.Z, Is.GreaterThan(0f));
        }

        /// <summary>
        /// Gravity does not wait for input: after a jump with nothing held, the body keeps
        /// rising, peaks, and lands -- on ticks no input and no hold stepped.
        /// </summary>
        [Test]
        public void AnAirborneBodyKeepsFallingWithNoInput_AndLands()
        {
            var p = Predictor(3u);
            p.Reconcile(new Vec3(0f, 0f, 0f), 0f, 0, 0);
            p.Advance(Dt);
            p.RecordInput(1, 0f, 0f, jump: true);  // jump in place: a stop, so nothing is held

            float peak = 0f;
            for (int i = 0; i < 120; i++)
            {
                p.Advance(Dt);
                if (p.SimulatedPosition3.Z > peak) peak = p.SimulatedPosition3.Z;
            }

            Assert.That(peak, Is.GreaterThan(1f), "apex of the default jump is 1.6 units");
            Assert.That(p.IsGrounded, Is.True, "two seconds is ample to land");
            Assert.That(p.SimulatedPosition3.Z, Is.EqualTo(0f));
            Assert.That(p.SimulatedPosition3.X, Is.EqualTo(0f), "a jump in place stays in place");
        }

        [Test]
        public void ABoxInTheGeometryBlocksTheMotorButNotThePlanarModel()
        {
            var bounds = MapBounds.Default;
            var wall = new StaticBox(0.6f, -5f, 0f, 1.6f, 5f, 3f);
            var geo = new MapGeometry(bounds, null, new[] { wall }, null, null);

            var motor = Predictor(3u);
            motor.SetMapGeometry(geo);
            motor.Reconcile(new Vec3(0f, 0f, 0f), 0f, 0, 0);

            var planar = Predictor(2u);
            planar.SetMapGeometry(geo);
            planar.Reconcile(new Vec2(0f, 0f), 0);

            for (long tick = 1; tick <= 30; tick++)
            {
                motor.Advance(Dt);
                motor.RecordInput(tick, 1f, 0f);
                planar.Advance(Dt);
                planar.RecordInput(tick, 1f, 0f);
            }

            Assert.That(motor.SimulatedPosition.X, Is.LessThan(0.6f - MotorParams.Default.CapsuleRadius + 1e-3f),
                "the capsule stops at the wall");
            Assert.That(planar.SimulatedPosition.X, Is.GreaterThan(1.6f), "the planar model has no walls");
        }

        [Test]
        public void Reconcile3D_SeedsGroundedFromVelocityAndSupport()
        {
            var standing = Predictor(3u);
            standing.Reconcile(new Vec3(1f, 1f, 0f), 0f, 0, 0);
            Assert.That(standing.IsGrounded, Is.True);

            var falling = Predictor(3u);
            falling.Reconcile(new Vec3(1f, 1f, 2f), -3f, 0, 0);
            Assert.That(falling.IsGrounded, Is.False);
            Assert.That(falling.VerticalVelocity, Is.EqualTo(-3f));
            Assert.That(falling.SimulatedPosition3, Is.EqualTo(new Vec3(1f, 1f, 2f)));

            // At the apex vertical velocity is momentarily zero, but there is no support under it.
            var apex = Predictor(3u);
            apex.Reconcile(new Vec3(1f, 1f, 1.6f), 0f, 0, 0);
            Assert.That(apex.IsGrounded, Is.False);
        }

        [Test]
        public void Position3AgreesWithPositionOnTheGroundPlane()
        {
            var p = Predictor(3u);
            p.Reconcile(new Vec3(0f, 0f, 0f), 0f, 0, 0);
            p.Advance(Dt);
            p.RecordInput(1, 1f, 1f, jump: true);
            p.Advance(Dt * 0.5f);

            Assert.That(p.Position3.X, Is.EqualTo(p.Position.X));
            Assert.That(p.Position3.Y, Is.EqualTo(p.Position.Y));
        }

        // ── projectiles ──────────────────────────────────────────────────────────

        private static ResolvedSnapshot SnapshotWith(params ResolvedEntity[] entities) =>
            new ResolvedSnapshot(1L, 0L, false, entities, new string[0]);

        private static ResolvedEntity AuthoritativeProjectile(string id, uint spawnSeq)
        {
            var core = new ResolvedEntity(id, "projectile", 0f, 0f, 0, 0);
            return new ResolvedEntity(in core, 1f, 10f, 0f, 0f, "me", spawnSeq, null, null, null, null);
        }

        [Test]
        public void FireAllocatesSpawnSeqFromOne_AndRefusesAShotWithNoDirection()
        {
            var projectiles = new ProjectilePredictor(TickRate);

            Assert.That(projectiles.Fire(new Vec3(0f, 0f, 1f), new Vec3(10f, 0f, 1f), 20f, 0.2f, 30f), Is.EqualTo(1u));
            Assert.That(projectiles.Fire(new Vec3(0f, 0f, 1f), new Vec3(0f, 10f, 1f), 20f, 0.2f, 30f), Is.EqualTo(2u));
            Assert.That(projectiles.Fire(new Vec3(5f, 5f, 1f), new Vec3(5f, 5f, 1f), 20f, 0.2f, 30f), Is.EqualTo(0u),
                "aim point on the origin: no direction, so nothing is fired or predicted");
            Assert.That(projectiles.Count, Is.EqualTo(2));
        }

        [Test]
        public void APredictedProjectileFliesExactlyAsProjectileLogicSteps()
        {
            var projectiles = new ProjectilePredictor(TickRate);
            var origin = new Vec3(0f, 0f, 1f);
            var aim = new Vec3(10f, 0f, 1f);
            uint seq = projectiles.Fire(origin, aim, 20f, 0.2f, 30f);

            for (int i = 0; i < 5; i++) projectiles.Advance(Dt);

            ProjectileLogic.Spawn(origin, aim, 20f, 0.2f, 30f, out ProjectileState expected);
            var geo = MapGeometry.Flat(MapBounds.Default);
            for (int i = 0; i < 5; i++)
            {
                ProjectileLogic.Step(expected, Dt, geo, out expected, out _);
            }

            Assert.That(projectiles.TryGet(seq, out var predicted), Is.True);
            Assert.That(predicted.State.Position, Is.EqualTo(expected.Position));
            Assert.That(predicted.Flying, Is.True);
        }

        [Test]
        public void TheAuthoritativeEntityWithTheSameSpawnSeqTakesOver()
        {
            var projectiles = new ProjectilePredictor(TickRate);
            uint first = projectiles.Fire(new Vec3(0f, 0f, 1f), new Vec3(10f, 0f, 1f), 20f, 0.2f, 30f);
            uint second = projectiles.Fire(new Vec3(0f, 0f, 1f), new Vec3(0f, 10f, 1f), 20f, 0.2f, 30f);

            var handed = new List<(uint seq, string id)>();
            projectiles.HandedOver += (p, id) => handed.Add((p.SpawnSeq, id));

            // Someone else's projectile carries no spawn_seq (the server sends it to the owner
            // only), and must not claim anything.
            int count = projectiles.ApplySnapshot(SnapshotWith(
                AuthoritativeProjectile("theirs", 0u),
                AuthoritativeProjectile("proj_77", second)));

            Assert.That(count, Is.EqualTo(1));
            Assert.That(handed, Is.EqualTo(new[] { (second, "proj_77") }));
            Assert.That(projectiles.TryGet(second, out _), Is.False, "handed over, so no longer predicted");
            Assert.That(projectiles.TryGet(first, out _), Is.True, "still waiting for its own entity");
            Assert.That(projectiles.HandOvers, Is.EqualTo(1));
        }

        [Test]
        public void APredictionNoEntityClaimsIsDroppedAfterTheTimeout()
        {
            var projectiles = new ProjectilePredictor(TickRate) { HandoverTimeoutSeconds = 0.5f };
            uint seq = projectiles.Fire(new Vec3(0f, 0f, 1f), new Vec3(10f, 0f, 1f), 20f, 0.2f, 30f);

            PredictedProjectile dropped = default;
            projectiles.Unconfirmed += p => dropped = p;

            for (int i = 0; i < 40; i++) projectiles.Advance(Dt);   // 0.67 s

            Assert.That(projectiles.Count, Is.Zero);
            Assert.That(dropped.SpawnSeq, Is.EqualTo(seq));
            Assert.That(projectiles.Unconfirmations, Is.EqualTo(1));
        }

        [Test]
        public void AProjectileThatHitsTheGroundStopsAndHolds()
        {
            var projectiles = new ProjectilePredictor(TickRate);
            // Aimed down into the ground plane from one unit up.
            uint seq = projectiles.Fire(new Vec3(0f, 0f, 1f), new Vec3(1f, 0f, -1f), 20f, 0.1f, 30f);

            for (int i = 0; i < 10; i++) projectiles.Advance(Dt);

            Assert.That(projectiles.TryGet(seq, out var p), Is.True, "held until confirmed or timed out");
            Assert.That(p.Flying, Is.False);
            Assert.That(p.RenderPosition, Is.EqualTo(p.State.Position));
        }
    }
}
