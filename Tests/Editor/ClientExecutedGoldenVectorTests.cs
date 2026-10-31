using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cuvara.Netcode.Json;
using NUnit.Framework;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;
using Shared.GameLogic.World;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// ADR-10 / UCF-ADR-13 conformance gate for the golden vectors the client executes beyond the
    /// planar four (<c>vec2</c>, <c>movement</c>, <c>combat</c>, <c>validation</c>, replayed by
    /// <see cref="GoldenVectorTests"/>): <c>motor3d.json</c>, <c>projectile.json</c> and
    /// <c>snapshot_merger.json</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each replay calls the Shared.GameLogic entry point the netcode runtime calls:
    /// <c>CharacterMotor.Step</c> (<c>LocalMovePredictor</c> under protocol 3),
    /// <c>ProjectileLogic.Spawn</c>/<c>Step</c> (<c>ProjectilePredictor</c>) and
    /// <c>SnapshotMerger.Apply</c> (<c>WorldState</c>). Floats are compared as IEEE-754 bit patterns,
    /// never with a tolerance: <c>0f</c> equals <c>-0f</c> and <c>NaN</c> differs from itself under any
    /// epsilon, and a one-ULP Mono-vs-RyuJIT divergence is exactly what these files exist to catch.
    /// </para>
    /// <para>
    /// Not replayed, on purpose: <c>aoi.json</c> (interest management runs on the server only) and
    /// <c>hitbox_rewind.json</c> (lag-compensated hit tests run on the server only). The client never
    /// executes those code paths, so a client-side replay would test nothing the client ships.
    /// </para>
    /// <para>
    /// This file is Unity-free so the headless project compiles it too. It reads the vectors with the
    /// package's own <see cref="JsonParser"/> rather than <c>JsonUtility</c>, because the merger fixture
    /// distinguishes an absent field from an empty one (<c>"ownerId": ""</c> means "expected absent")
    /// and <c>JsonUtility</c> cannot tell the two apart. Case semantics mirror the server's
    /// <c>World3DGoldenVectorTests</c> and <c>SnapshotMergerGoldenVectorTests</c>.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class ClientExecutedGoldenVectorTests
    {
        private const string MotorFile = "motor3d.json";
        private const string ProjectileFile = "projectile.json";
        private const string MergerFile = "snapshot_merger.json";

        private static IEnumerable<TestCaseData> MotorCases() => Cases(MotorFile);

        private static IEnumerable<TestCaseData> ProjectileCases() => Cases(ProjectileFile);

        private static IEnumerable<TestCaseData> MergerCases() => Cases(MergerFile);

        private static IEnumerable<TestCaseData> Cases(string file)
        {
            foreach (var c in Load(file))
            {
                yield return new TestCaseData(c).SetName(file.Replace(".json", "") + "." + c.GetString("name"));
            }
        }

        // ── Loading ──────────────────────────────────────────────────────────

        private static string VectorDirectory()
        {
#if UNITY_EDITOR
            return GoldenVectors.Directory;
#else
            foreach (var attribute in typeof(ClientExecutedGoldenVectorTests).Assembly
                         .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                         .Cast<System.Reflection.AssemblyMetadataAttribute>())
            {
                if (attribute.Key == "SglGoldenVectors")
                {
                    if (!Directory.Exists(attribute.Value))
                    {
                        throw new DirectoryNotFoundException(
                            $"'{attribute.Value}' does not exist -- set -p:SglDir to a Shared.GameLogic checkout that ships GoldenVectors");
                    }

                    return attribute.Value;
                }
            }

            throw new InvalidDataException("assembly metadata SglGoldenVectors is missing from the headless project");
#endif
        }

        private static IReadOnlyList<JsonValue> Load(string file)
        {
            var document = JsonParser.Parse(File.ReadAllText(Path.Combine(VectorDirectory(), file)));
            var cases = document.GetArray("cases");
            if (cases.Count == 0)
            {
                throw new InvalidDataException(file + " produced no cases");
            }

            return cases;
        }

        // ── IEEE-754 hex ─────────────────────────────────────────────────────

        private static float Float(string hex)
        {
            if (string.IsNullOrEmpty(hex))
            {
                throw new ArgumentException("empty float literal in a fixture", nameof(hex));
            }

            var digits = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex.Substring(2) : hex;
            return BitConverter.Int32BitsToSingle(unchecked((int)Convert.ToUInt32(digits, 16)));
        }

        private static float F(JsonValue c, string field) => Float(c.GetString(field));

        private static string Hex(float value) =>
            "0x" + unchecked((uint)BitConverter.SingleToInt32Bits(value)).ToString("X8");

        private static void AssertBits(string expectedHex, float actual, string because)
        {
            var expected = BitConverter.SingleToInt32Bits(Float(expectedHex));
            if (expected != BitConverter.SingleToInt32Bits(actual))
            {
                throw new AssertionException(
                    $"{because}: expected {expectedHex} ({Float(expectedHex)}), got {Hex(actual)} ({actual})");
            }
        }

        private static void AssertBits(JsonValue c, string expectedField, float actual, string name) =>
            AssertBits(c.GetString(expectedField), actual, name + "." + expectedField);

        // ── Geometry (flat strings inside each case) ─────────────────────────

        private static float[] DecodeFloats(string s) =>
            string.IsNullOrEmpty(s) ? Array.Empty<float>() : s.Split(',').Select(Float).ToArray();

        private static StaticBox[] DecodeBoxes(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return Array.Empty<StaticBox>();
            }

            var parts = s.Split(';');
            var boxes = new StaticBox[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                var v = parts[i].Split(',');
                if (v.Length != 6)
                {
                    throw new InvalidDataException("box needs 6 values: " + parts[i]);
                }

                boxes[i] = new StaticBox(
                    Float(v[0]), Float(v[1]), Float(v[2]), Float(v[3]), Float(v[4]), Float(v[5]));
            }

            return boxes;
        }

        private static MapGeometry Geometry(JsonValue c)
        {
            var bounds = new MapBounds(F(c, "minX"), F(c, "minY"), F(c, "maxX"), F(c, "maxY"));

            HeightField hf = null;
            var heights = c.GetString("hfHeights");
            if (!string.IsNullOrEmpty(heights))
            {
                hf = new HeightField(
                    F(c, "hfOriginX"), F(c, "hfOriginY"), F(c, "hfCell"),
                    c.GetInt("hfCols"), c.GetInt("hfRows"), DecodeFloats(heights));
            }

            return new MapGeometry(bounds, hf, DecodeBoxes(c.GetString("boxes")), null, null);
        }

        // ── motor3d ──────────────────────────────────────────────────────────

        [TestCaseSource(nameof(MotorCases))]
        public void Motor(JsonValue c)
        {
            var name = c.GetString("name");
            var geometry = Geometry(c);
            var parameters = new MotorParams
            {
                Gravity = F(c, "gravity"),
                JumpSpeed = F(c, "jumpSpeed"),
                StepHeight = F(c, "stepHeight"),
                MaxSlopeRise = F(c, "maxSlopeRise"),
                CapsuleRadius = F(c, "capsuleRadius"),
                CapsuleHeight = F(c, "capsuleHeight"),
            };

            var state = new MotorState(
                new Vec3(F(c, "posX"), F(c, "posY"), F(c, "posZ")),
                F(c, "velZ"), c.GetBool("grounded"));
            var moveX = F(c, "moveX");
            var moveY = F(c, "moveY");
            var speed = F(c, "speed");
            var dt = F(c, "dt");
            var ticks = c.GetInt("ticks");
            var jumpTicks = c.GetInt("jumpTicks");

            // Same loop as the server: one input for `ticks` steps, jump held for the first `jumpTicks`.
            var result = MoveResult.None;
            for (var i = 0; i < ticks; i++)
            {
                result = CharacterMotor.Step(
                    state, moveX, moveY, i < jumpTicks, speed, dt, geometry, parameters, out var next);
                state = next;
            }

            Assert.AreEqual(c.GetString("expectedResult"), result.ToString(), name + ".result");
            AssertBits(c, "expectedX", state.Position.X, name);
            AssertBits(c, "expectedY", state.Position.Y, name);
            AssertBits(c, "expectedZ", state.Position.Z, name);
            AssertBits(c, "expectedVelZ", state.VelocityZ, name);
            Assert.AreEqual(c.GetBool("expectedGrounded"), state.Grounded, name + ".grounded");
        }

        // ── projectile ───────────────────────────────────────────────────────

        [TestCaseSource(nameof(ProjectileCases))]
        public void Projectile(JsonValue c)
        {
            var name = c.GetString("name");
            var a = new Vec3(F(c, "ax"), F(c, "ay"), F(c, "az"));
            var b = new Vec3(F(c, "bx"), F(c, "by"), F(c, "bz"));
            var kind = c.GetString("kind");

            switch (kind)
            {
                case "spawn":
                {
                    var ok = ProjectileLogic.Spawn(
                        a, b, F(c, "speed"), F(c, "radius"), F(c, "range"), out var s);
                    Assert.AreEqual(c.GetBool("expectedOk"), ok, name + ".ok");
                    AssertBits(c, "expectedX", s.Position.X, name);
                    AssertBits(c, "expectedY", s.Position.Y, name);
                    AssertBits(c, "expectedZ", s.Position.Z, name);
                    AssertBits(c, "expectedVx", s.Velocity.X, name);
                    AssertBits(c, "expectedVy", s.Velocity.Y, name);
                    AssertBits(c, "expectedVz", s.Velocity.Z, name);
                    AssertBits(c, "expectedRemaining", s.RemainingRange, name);
                    break;
                }

                case "step":
                {
                    var geometry = Geometry(c);
                    var state = new ProjectileState(a, b, F(c, "radius"), F(c, "range"));
                    var dt = F(c, "dt");
                    var ticks = c.GetInt("ticks");
                    var alive = true;
                    var hitWorld = false;
                    var executed = 0;
                    while (alive && executed < ticks)
                    {
                        alive = ProjectileLogic.Step(state, dt, geometry, out var next, out hitWorld);
                        state = next;
                        executed++;
                    }

                    Assert.AreEqual(c.GetInt("expectedTicks"), executed, name + ".ticks");
                    Assert.AreEqual(c.GetBool("expectedOk"), alive, name + ".alive");
                    Assert.AreEqual(c.GetBool("expectedHitWorld"), hitWorld, name + ".hitWorld");
                    AssertBits(c, "expectedX", state.Position.X, name);
                    AssertBits(c, "expectedY", state.Position.Y, name);
                    AssertBits(c, "expectedZ", state.Position.Z, name);
                    AssertBits(c, "expectedRemaining", state.RemainingRange, name);
                    break;
                }

                case "sweep":
                {
                    var hit = Geometry(c).SweepSphere(a, b, F(c, "radius"), out var hp, out var t);
                    Assert.AreEqual(c.GetBool("expectedOk"), hit, name + ".hit");
                    AssertBits(c, "expectedX", hp.X, name);
                    AssertBits(c, "expectedY", hp.Y, name);
                    AssertBits(c, "expectedZ", hp.Z, name);
                    AssertBits(c, "expectedT", t, name);
                    break;
                }

                case "segment_capsule":
                {
                    var capBase = new Vec3(F(c, "cx"), F(c, "cy"), F(c, "cz"));
                    var hit = ProjectileLogic.SegmentCapsuleHit(
                        a, b, F(c, "radius"), capBase, F(c, "capsuleRadius"), F(c, "capsuleHeight"), out var t);
                    Assert.AreEqual(c.GetBool("expectedOk"), hit, name + ".hit");
                    AssertBits(c, "expectedT", t, name);
                    break;
                }

                default:
                    throw new InvalidDataException("unknown projectile case kind: " + kind);
            }
        }

        // ── snapshot_merger ──────────────────────────────────────────────────

        private static float OptionalFloat(JsonValue o, string field) =>
            o.TryGetMember(field, out var v) && v.Kind == JsonKind.String ? Float(v.AsString()) : 0f;

        private static StatValueData[] Stats(JsonValue o, string field) =>
            o.TryGetMember(field, out _)
                ? o.GetArray(field).Select(s => new StatValueData(s.GetUInt("statId"), s.GetInt("value"))).ToArray()
                : null;

        private static StatusEffectData[] Statuses(JsonValue o, string field)
        {
            if (!o.TryGetMember(field, out _))
            {
                return null;
            }

            return o.GetArray(field).Select(s => new StatusEffectData(
                s.GetUInt("effectId"), s.GetUInt("stacks"), (ulong)s.GetLong("expiresTick"),
                s.TryGetMember("sourceId", out var src) && src.Kind == JsonKind.String ? src.AsString() : null))
                .ToArray();
        }

        private static uint[] UInts(JsonValue o, string field) =>
            o.TryGetMember(field, out _)
                ? o.GetArray(field).Select(v => (uint)v.AsNumber()).ToArray()
                : null;

        private static EntitySnapshotData Entity(JsonValue e)
        {
            var baseData = new EntitySnapshotData(
                e.GetString("id"), e.GetString("type"),
                F(e, "x"), F(e, "y"),
                e.GetInt("hp"), e.GetInt("maxHp"),
                OptionalFloat(e, "speed"),
                e.GetUInt("facingBrad"),
                (EntityAction)e.GetInt("action"),
                // NAMED, not positional: a positional uint binds to whichever overload accepts it,
                // which is how a retrigger counter once became a field mask (Cuvara/Netcode#159).
                actionSeq: 0u,
                changedFields: e.GetUInt("changedFields"));

            // Protocol version 3 fields, through the constructor that cannot be confused with the
            // positional protocol 2 overloads.
            return new EntitySnapshotData(
                in baseData,
                OptionalFloat(e, "z"), OptionalFloat(e, "velX"), OptionalFloat(e, "velY"), OptionalFloat(e, "velZ"),
                e.TryGetMember("ownerId", out var owner) && owner.Kind == JsonKind.String ? owner.AsString() : null,
                e.GetUInt("spawnSeq"),
                Stats(e, "stats"),
                UInts(e, "statsRemoved"),
                Statuses(e, "statuses"),
                UInts(e, "statusesRemoved"));
        }

        [TestCaseSource(nameof(MergerCases))]
        public void Merger(JsonValue c)
        {
            var name = c.GetString("name");
            var merger = new SnapshotMerger();

            foreach (var step in c.GetArray("steps"))
            {
                var items = step.GetArray("entities");
                var entities = new EntitySnapshotData[items.Count];
                for (var i = 0; i < items.Count; i++)
                {
                    entities[i] = Entity(items[i]);
                }

                var removed = step.GetArray("removed").Select(r => r.AsString()).ToArray();
                merger.Apply(new SnapshotData(
                    (ulong)step.GetLong("tick"), (ulong)step.GetLong("ackTick"), step.GetBool("full"),
                    entities, removed.Length > 0 ? removed : null));
            }

            if (c.GetBool("resetAfterSteps"))
            {
                merger.Reset();
            }

            Assert.AreEqual((ulong)c.GetLong("expectedTick"), merger.Tick, name + ".tick");
            Assert.AreEqual((ulong)c.GetLong("expectedAckTick"), merger.AckTick, name + ".ackTick");
            Assert.AreEqual(c.GetInt("expectedKeyframes"), merger.Keyframes, name + ".keyframes");
            Assert.AreEqual(c.GetInt("expectedDeltas"), merger.Deltas, name + ".deltas");
            Assert.AreEqual(c.GetInt("expectedCount"), merger.Count, name + ".count");

            var actualIds = merger.Entities.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            var expectedIds = c.GetArray("expectedEntityIds").Select(v => v.AsString())
                .OrderBy(k => k, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(expectedIds, actualIds, name + ".ids");

            if (!c.TryGetMember("expectedEntityState", out var expected) || expected.Kind != JsonKind.Object)
            {
                return;
            }

            var id = expected.GetString("id");
            Assert.IsTrue(merger.TryGet(id, out var entity), $"{name}: expected entity {id} not found");
            Assert.AreEqual(expected.GetInt("hp"), entity.Hp, name + "." + id + ".hp");
            var at = name + "." + id;
            AssertBits(expected.GetString("x"), entity.X, at + ".x");
            AssertBits(expected.GetString("y"), entity.Y, at + ".y");

            // An expectation the fixture does not state is not asserted: the older cases predate these
            // fields and are not retro-fitted with numbers nobody computed.
            if (expected.TryGetMember("speed", out _)) AssertBits(expected.GetString("speed"), entity.Speed, at + ".speed");
            if (expected.TryGetMember("facingBrad", out _)) Assert.AreEqual(expected.GetUInt("facingBrad"), entity.FacingBrad, at + ".facingBrad");
            if (expected.TryGetMember("action", out _)) Assert.AreEqual((EntityAction)expected.GetInt("action"), entity.Action, at + ".action");
            if (expected.TryGetMember("z", out _)) AssertBits(expected.GetString("z"), entity.Z, at + ".z");
            if (expected.TryGetMember("velX", out _)) AssertBits(expected.GetString("velX"), entity.VelX, at + ".velX");
            if (expected.TryGetMember("velY", out _)) AssertBits(expected.GetString("velY"), entity.VelY, at + ".velY");
            if (expected.TryGetMember("velZ", out _)) AssertBits(expected.GetString("velZ"), entity.VelZ, at + ".velZ");

            if (expected.TryGetMember("ownerId", out var owner))
            {
                var want = owner.AsString();
                Assert.AreEqual(want.Length == 0 ? null : want, entity.OwnerId, at + ".ownerId");
            }

            if (expected.TryGetMember("spawnSeq", out _)) Assert.AreEqual(expected.GetUInt("spawnSeq"), entity.SpawnSeq, at + ".spawnSeq");

            // Order is part of the contract (SnapshotMerger.MergeStats), so it is asserted.
            var wantStats = Stats(expected, "stats");
            if (wantStats != null)
            {
                CollectionAssert.AreEqual(wantStats, entity.Stats ?? Array.Empty<StatValueData>(), at + ".stats");
            }

            var wantStatuses = Statuses(expected, "statuses");
            if (wantStatuses != null)
            {
                CollectionAssert.AreEqual(wantStatuses, entity.Statuses ?? Array.Empty<StatusEffectData>(), at + ".statuses");
            }

            // The merged state is complete; removal lists are consumed by the merge.
            if (wantStats != null || wantStatuses != null)
            {
                Assert.IsNull(entity.StatsRemoved, at + ".statsRemoved");
                Assert.IsNull(entity.StatusesRemoved, at + ".statusesRemoved");
            }
        }
    }
}
