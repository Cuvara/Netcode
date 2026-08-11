using System;
using System.IO;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Reads the ADR-10 golden vectors that ship inside the
    /// <c>com.rpgmmo.shared-gamelogic</c> package, and compares floats bit-for-bit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server's xUnit suite replays these same files. That is the whole point:
    /// the fixtures are the contract, and if the client and the server disagree
    /// about what <c>(state, input, dt)</c> produces, one of the two suites goes
    /// red.
    /// </para>
    /// <para>
    /// Floats are stored as IEEE-754 bit patterns and compared as bits. A tolerance
    /// comparison would pass on exactly the divergence these vectors exist to
    /// catch — a NativeAOT-x64 result that differs from an IL2CPP-ARM64 one in the
    /// last place still accumulates into visible desync over a few hundred ticks,
    /// and <c>0f</c> vs <c>-0f</c> and <c>NaN</c> vs <c>NaN</c> both compare equal
    /// under any epsilon.
    /// </para>
    /// </remarks>
    internal static class GoldenVectors
    {
        private const string PackageName = "com.rpgmmo.shared-gamelogic";

        /// <summary>
        /// Fixture directory inside the resolved package.
        /// </summary>
        /// <remarks>
        /// Resolved through the package manager rather than by an
        /// <c>Assets/</c>-relative path: a git package lives in
        /// <c>Library/PackageCache/&lt;name&gt;@&lt;hash&gt;</c>, and the hash changes
        /// whenever the pinned tag moves.
        /// </remarks>
        public static string Directory
        {
            get
            {
                var package = PackageInfo.FindForPackageName(PackageName);
                if (package == null)
                {
                    throw new DirectoryNotFoundException(
                        $"package '{PackageName}' is not resolved — check Packages/manifest.json");
                }

                var directory = Path.Combine(package.resolvedPath, "GoldenVectors");
                if (!System.IO.Directory.Exists(directory))
                {
                    throw new DirectoryNotFoundException(
                        $"'{directory}' does not exist — the pinned tag ships no golden vectors");
                }

                return directory;
            }
        }

        public static string PathTo(string file) => Path.Combine(Directory, file);

        /// <summary>
        /// Loads one fixture file. <c>JsonUtility</c> is used deliberately: it is
        /// built into the engine, so the conformance gate needs no extra package,
        /// and the fixture schema on the server side is constrained to the subset it
        /// binds (one top-level object, one <c>cases</c> array, flat public fields).
        /// </summary>
        /// <remarks>
        /// One wrapper type per fixture rather than a generic one: Unity's
        /// serializer does not bind open generic classes, so
        /// <c>JsonUtility.FromJson&lt;CaseFile&lt;T&gt;&gt;</c> silently returns an
        /// object with a null array instead of failing.
        /// </remarks>
        private static TCase[] Load<TFile, TCase>(string file, Func<TFile, TCase[]> select)
        {
            var json = File.ReadAllText(PathTo(file));
            var document = JsonUtility.FromJson<TFile>(json);
            var cases = document == null ? null : select(document);
            if (cases == null || cases.Length == 0)
            {
                throw new InvalidDataException($"{file} produced no cases");
            }

            return cases;
        }

        public static Vec2Case[] LoadVec2() =>
            Load<Vec2CaseFile, Vec2Case>("vec2.json", f => f.cases);

        public static MovementCase[] LoadMovement() =>
            Load<MovementCaseFile, MovementCase>("movement.json", f => f.cases);

        public static CombatCase[] LoadCombat() =>
            Load<CombatCaseFile, CombatCase>("combat.json", f => f.cases);

        public static ValidationCase[] LoadValidation() =>
            Load<ValidationCaseFile, ValidationCase>("validation.json", f => f.cases);

        // ── IEEE-754 hex ─────────────────────────────────────────────────────

        public static string Hex(float value) =>
            "0x" + unchecked((uint)BitConverter.SingleToInt32Bits(value)).ToString("X8");

        public static float Float(string hex)
        {
            if (string.IsNullOrEmpty(hex))
            {
                throw new ArgumentException("empty float literal in a fixture", nameof(hex));
            }

            var digits = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex.Substring(2) : hex;
            return BitConverter.Int32BitsToSingle(unchecked((int)Convert.ToUInt32(digits, 16)));
        }

        /// <summary>
        /// Bit-exact comparison. <c>==</c> is wrong here in both directions: it calls
        /// <c>NaN</c> different from itself, and <c>0f</c> the same as <c>-0f</c>.
        /// </summary>
        public static void AssertBitEqual(string expectedHex, float actual, string because)
        {
            var expectedBits = unchecked((int)Convert.ToUInt32(expectedHex.Substring(2), 16));
            var actualBits = BitConverter.SingleToInt32Bits(actual);
            if (expectedBits != actualBits)
            {
                throw new NUnit.Framework.AssertionException(
                    $"{because}: expected {expectedHex} ({Float(expectedHex)}), got {Hex(actual)} ({actual})");
            }
        }
    }

    // ── Fixture schema ───────────────────────────────────────────────────────
    //
    // Field names and types mirror the server's GameServer.Tests/Golden/GoldenVectors.cs
    // exactly. Public fields on a [Serializable] class is what JsonUtility binds.

    [Serializable]
    public sealed class Vec2CaseFile
    {
        public Vec2Case[] cases;
    }

    [Serializable]
    public sealed class MovementCaseFile
    {
        public MovementCase[] cases;
    }

    [Serializable]
    public sealed class CombatCaseFile
    {
        public CombatCase[] cases;
    }

    [Serializable]
    public sealed class ValidationCaseFile
    {
        public ValidationCase[] cases;
    }

    [Serializable]
    public sealed class MovementCase
    {
        public string name;
        public string posX;
        public string posY;
        public string moveX;
        public string moveY;
        public string speed;
        public string dt;
        public string minX;
        public string minY;
        public string maxX;
        public string maxY;
        public bool dead;
        public string expectedResult;
        public string expectedX;
        public string expectedY;
    }

    [Serializable]
    public sealed class CombatCase
    {
        public string name;
        public string kind;

        public int attackerAttack;
        public int defenderDefense;
        public int expectedDamage;

        public int hp;
        public bool alreadyDead;
        public bool expectedDied;
        public int expectedHp;
        public bool expectedDead;

        public string attackerX;
        public string attackerY;
        public string targetX;
        public string targetY;
        public bool targetDead;
        public long currentTick;
        public long cooldownUntilTick;
        public bool expectedValid;

        public string expectedErrorPrefix;
    }

    [Serializable]
    public sealed class Vec2Case
    {
        public string name;
        public string ax;
        public string ay;
        public string bx;
        public string by;
        public string expectedSqrMagnitudeA;
        public string expectedMagnitudeA;
        public string expectedNormalizedX;
        public string expectedNormalizedY;
        public string expectedDistanceSq;
        public string expectedDistance;
    }

    [Serializable]
    public sealed class ValidationCase
    {
        public string name;
        public bool dead;
        public string moveX;
        public string moveY;
        public string attackTargetId;
        public bool targetPresent;
        public string attackerX;
        public string attackerY;
        public string targetX;
        public string targetY;
        public bool targetDead;
        public long currentTick;
        public long cooldownUntilTick;
        public bool expectedValid;
        public string expectedErrorPrefix;
    }
}
