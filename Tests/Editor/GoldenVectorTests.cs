using System;
using System.Collections.Generic;
using NUnit.Framework;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The ADR-10 conformance gate, client side. Replays the golden vectors that
    /// ship in <c>com.rpgmmo.shared-gamelogic</c> through the same
    /// <c>Shared.GameLogic</c> the server runs, and compares every float bit for bit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server's <c>GameServer.Tests/Golden/GoldenVectorTests.cs</c> replays the
    /// exact same files. Together they are the only thing that actually proves
    /// client prediction and the authoritative simulation compute the same numbers —
    /// "we compile the same source" is a claim about the build, not about the
    /// result, and IL2CPP-ARM64 and NativeAOT-x64 are free to disagree at a
    /// <c>MathF.Sqrt</c>.
    /// </para>
    /// <para>
    /// These tests assert nothing the fixtures do not say. A red test here means
    /// either the shared logic changed without the vectors being regenerated, or
    /// Unity's compilation of it genuinely produces different numbers — and those
    /// two need telling apart, not averaging over.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class GoldenVectorTests
    {
        // NUnit builds the case source at test-collection time; a fixture that
        // cannot be read must surface as one clear failure rather than as an empty,
        // silently-passing suite.

        private static IEnumerable<TestCaseData> Vec2Cases() => Cases(GoldenVectors.LoadVec2(), c => c.name);

        private static IEnumerable<TestCaseData> MovementCases() => Cases(GoldenVectors.LoadMovement(), c => c.name);

        private static IEnumerable<TestCaseData> CombatCases() => Cases(GoldenVectors.LoadCombat(), c => c.name);

        private static IEnumerable<TestCaseData> ValidationCases() => Cases(GoldenVectors.LoadValidation(), c => c.name);

        private static IEnumerable<TestCaseData> Cases<T>(T[] cases, Func<T, string> name)
        {
            foreach (var c in cases)
            {
                yield return new TestCaseData(c).SetName(name(c));
            }
        }

        /// <summary>
        /// The <c>MathF.Sqrt</c> call sites, pinned directly. Two of them
        /// (<c>Vec2.Magnitude</c>, <c>Vec2.Normalized</c>) have no caller inside the
        /// library and a third (<c>Vec2.Distance</c>) is only used to format an error
        /// message, so no behaviour vector reaches them.
        /// </summary>
        [TestCaseSource(nameof(Vec2Cases))]
        public void Vector(Vec2Case c)
        {
            var a = new Vec2(GoldenVectors.Float(c.ax), GoldenVectors.Float(c.ay));
            var b = new Vec2(GoldenVectors.Float(c.bx), GoldenVectors.Float(c.by));
            var normalized = a.Normalized;

            GoldenVectors.AssertBitEqual(c.expectedSqrMagnitudeA, a.SqrMagnitude, c.name + ".sqrMagnitude");
            GoldenVectors.AssertBitEqual(c.expectedMagnitudeA, a.Magnitude, c.name + ".magnitude");
            GoldenVectors.AssertBitEqual(c.expectedNormalizedX, normalized.X, c.name + ".normalized.x");
            GoldenVectors.AssertBitEqual(c.expectedNormalizedY, normalized.Y, c.name + ".normalized.y");
            GoldenVectors.AssertBitEqual(c.expectedDistanceSq, Vec2.DistanceSq(a, b), c.name + ".distanceSq");
            GoldenVectors.AssertBitEqual(c.expectedDistance, Vec2.Distance(a, b), c.name + ".distance");
        }

        [TestCaseSource(nameof(MovementCases))]
        public void Movement(MovementCase c)
        {
            var entity = new EntityState
            {
                Id = "e",
                Type = "player",
                Position = new Vec2(GoldenVectors.Float(c.posX), GoldenVectors.Float(c.posY)),
                Speed = GoldenVectors.Float(c.speed),
                Dead = c.dead,
                Hp = c.dead ? 0 : 100,
                MaxHp = 100
            };

            var bounds = new MapBounds(
                GoldenVectors.Float(c.minX), GoldenVectors.Float(c.minY),
                GoldenVectors.Float(c.maxX), GoldenVectors.Float(c.maxY));

            var result = MovementSystem.TryMove(
                entity,
                GoldenVectors.Float(c.moveX),
                GoldenVectors.Float(c.moveY),
                GoldenVectors.Float(c.dt),
                bounds,
                out var position);

            Assert.AreEqual(c.expectedResult, result.ToString(), c.name + ".result");
            GoldenVectors.AssertBitEqual(c.expectedX, position.X, c.name + ".x");
            GoldenVectors.AssertBitEqual(c.expectedY, position.Y, c.name + ".y");
        }

        [TestCaseSource(nameof(CombatCases))]
        public void Combat(CombatCase c)
        {
            switch (c.kind)
            {
                case "damage":
                {
                    var attacker = Entity("a", 0f, 0f, c.attackerAttack);
                    var defender = Entity("b", 1f, 0f, defense: c.defenderDefense);
                    Assert.AreEqual(c.expectedDamage, CombatLogic.CalculateDamage(attacker, defender), c.name);
                    break;
                }

                case "death":
                {
                    var entity = Entity("a", 0f, 0f);
                    entity.Hp = c.hp;
                    entity.Dead = c.alreadyDead;
                    var died = CombatLogic.HandleDeath(ref entity);
                    Assert.AreEqual(c.expectedDied, died, c.name + ".died");
                    Assert.AreEqual(c.expectedHp, entity.Hp, c.name + ".hp");
                    Assert.AreEqual(c.expectedDead, entity.Dead, c.name + ".dead");
                    break;
                }

                case "validate_attack":
                {
                    var attacker = Entity("a", GoldenVectors.Float(c.attackerX), GoldenVectors.Float(c.attackerY));
                    attacker.CooldownUntilTick = (ulong)c.cooldownUntilTick;

                    var target = Entity("b", GoldenVectors.Float(c.targetX), GoldenVectors.Float(c.targetY));
                    target.Dead = c.targetDead;

                    var error = CombatLogic.ValidateAttack(attacker, target, (ulong)c.currentTick);
                    Assert.AreEqual(c.expectedValid, error == null, c.name + ".valid (error: " + error + ")");
                    Assert.AreEqual(c.expectedErrorPrefix, Prefix(error), c.name + ".error");
                    break;
                }

                default:
                    Assert.Fail($"unknown combat vector kind '{c.kind}' in {c.name}");
                    break;
            }
        }

        [TestCaseSource(nameof(ValidationCases))]
        public void Validation(ValidationCase c)
        {
            var entity = Entity("a", GoldenVectors.Float(c.attackerX), GoldenVectors.Float(c.attackerY));
            entity.Dead = c.dead;
            entity.CooldownUntilTick = (ulong)c.cooldownUntilTick;

            var target = Entity("b", GoldenVectors.Float(c.targetX), GoldenVectors.Float(c.targetY));
            target.Dead = c.targetDead;
            var present = c.targetPresent;

            var input = new InputData(
                1,
                GoldenVectors.Float(c.moveX),
                GoldenVectors.Float(c.moveY),
                string.IsNullOrEmpty(c.attackTargetId) ? null : c.attackTargetId);

            var error = ValidationLogic.ValidateInput(
                entity, input, id => present && id == "b" ? target : (EntityState?)null, (ulong)c.currentTick);

            Assert.AreEqual(c.expectedValid, error == null, c.name + ".valid (error: " + error + ")");
            Assert.AreEqual(c.expectedErrorPrefix, Prefix(error), c.name + ".error");
        }

        /// <summary>
        /// Every float in the fixtures is an 8-digit IEEE-754 bit pattern, not
        /// decimal text. If this ever fails the fixtures stopped being a bit-exact
        /// contract and the rest of the suite is comparing formatted numbers.
        /// </summary>
        [Test]
        public void FloatsAreStoredAsBitPatterns()
        {
            foreach (var c in GoldenVectors.LoadMovement())
            {
                foreach (var hex in new[]
                         {
                             c.posX, c.posY, c.moveX, c.moveY, c.speed, c.dt,
                             c.minX, c.minY, c.maxX, c.maxY, c.expectedX, c.expectedY
                         })
                {
                    StringAssert.IsMatch("^0x[0-9A-F]{8}$", hex);
                }
            }
        }

        /// <summary>
        /// The hex encoding round-trips every float bit for bit under IL2CPP/Mono's
        /// <c>BitConverter</c> too — including NaN, the infinities and negative zero,
        /// each of which a tolerance comparison would wave through.
        /// </summary>
        [TestCase(0f)]
        [TestCase(-0.0f)]
        [TestCase(1f)]
        [TestCase(0.1f)]
        [TestCase(3.3333333f)]
        [TestCase(float.MaxValue)]
        [TestCase(float.Epsilon)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void HexRoundTripsExactly(float value)
        {
            Assert.AreEqual(
                BitConverter.SingleToInt32Bits(value),
                BitConverter.SingleToInt32Bits(GoldenVectors.Float(GoldenVectors.Hex(value))));
        }

        [Test]
        public void NegativeZeroIsDistinctFromPositiveZero()
        {
            Assert.AreNotEqual(GoldenVectors.Hex(0f), GoldenVectors.Hex(-0.0f));
        }

        /// <summary>
        /// The fixture files exist and none of them is empty. Without this a broken
        /// package path would make every <c>TestCaseSource</c> above yield nothing,
        /// and an empty suite reports green.
        /// </summary>
        [Test]
        public void AllFixturesLoad()
        {
            Assert.IsNotEmpty(GoldenVectors.LoadVec2(), "vec2.json");
            Assert.IsNotEmpty(GoldenVectors.LoadMovement(), "movement.json");
            Assert.IsNotEmpty(GoldenVectors.LoadCombat(), "combat.json");
            Assert.IsNotEmpty(GoldenVectors.LoadValidation(), "validation.json");
        }

        // ── Fixture helpers, mirroring GoldenVectorGenerator on the server ──────
        //
        // Test scaffolding, not game rules: the values here are the ones the
        // generator used when it produced the expectations, so they are part of the
        // fixture, not a second implementation of anything.

        private static EntityState Entity(string id, float x, float y, int attack = 10, int defense = 0) =>
            new EntityState
            {
                Id = id,
                Type = "player",
                Position = new Vec2(x, y),
                Hp = 100,
                MaxHp = 100,
                Attack = attack,
                Defense = defense,
                Speed = 5f
            };

        /// <summary>
        /// Reduce an error message to its float-free leading part — the range error
        /// embeds a formatted distance, and number formatting across two runtimes is
        /// not what these vectors test.
        /// </summary>
        private static string Prefix(string error)
        {
            if (error == null)
            {
                return string.Empty;
            }

            if (error.StartsWith("target out of range", StringComparison.Ordinal))
            {
                return "target out of range";
            }

            if (error.StartsWith("invalid move direction", StringComparison.Ordinal))
            {
                return "invalid move direction";
            }

            return error;
        }
    }
}

