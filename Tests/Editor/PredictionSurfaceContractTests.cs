using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Shared.GameLogic.Components;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Pins the public surface of <see cref="LocalMovePredictor"/> that
    /// <c>com.cuvara.dots</c> drives from a system this package cannot see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a test rather than a comment.</b> The DOTS adapter references
    /// <c>Cuvara.Netcode.Runtime</c>; netcode must never reference it back, so the adapter
    /// is not built in this repository and **its compiler errors cannot appear in this
    /// repository's CI**. Rename <c>Reconcile</c> here and everything stays green — the
    /// break surfaces in another repo, or in the Unity project, at whatever point someone
    /// next compiles it. This fixture is the only thing on this side that notices.
    /// </para>
    /// <para>
    /// It deliberately asserts <i>signatures</i>, not behaviour — behaviour is
    /// <see cref="LocalMovePredictorTests"/>' job. A failure here means "you changed a
    /// cross-package contract"; the fix is to add rather than change, or to agree the
    /// change on the dots side before it lands, not after.
    /// </para>
    /// <para>
    /// The call sites at the bottom are the other half: they will not compile if a
    /// signature moves, which catches the same thing a step earlier and with a worse error
    /// message. Both are wanted — the compile failure is immediate, the assertions explain.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class PredictionSurfaceContractTests
    {
        // Matched on name AND parameter types, not on name alone.
        //
        // What these tests guard is that a given SIGNATURE still exists. The remarks above
        // tell callers to extend this surface by adding rather than changing -- and an
        // overload is the sanctioned way to add. Selecting on the name alone made that
        // sanctioned move fail: SingleOrDefault throws "Sequence contains more than one
        // matching element" the moment a second overload appears, so the fixture reports an
        // ADDITION as a broken contract, with an exception rather than an assertion message.
        //
        // The failure is also misdirected. Adding an overload to LocalMovePredictor breaks
        // AdvanceKeepsItsSignature and every other case that shares the fixture, none of
        // which the author touched. Found while prototyping a three-argument Reconcile.
        private static MethodInfo Method(string name, params Type[] parameters) =>
            typeof(LocalMovePredictor)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(m => m.Name == name &&
                    m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters));

        private static void AssertSignature(string name, Type returnType, params Type[] parameters)
        {
            var method = Method(name, parameters);
            Assert.That(method, Is.Not.Null,
                $"LocalMovePredictor.{name} is part of the contract com.cuvara.dots drives. " +
                "Removing or renaming it -- or changing its parameters rather than adding " +
                "an overload -- breaks a consumer that is not compiled by this repo.");

            Assert.That(method.ReturnType, Is.EqualTo(returnType),
                $"LocalMovePredictor.{name}'s return type is part of the cross-package contract.");

            var actual = method.GetParameters().Select(p => p.ParameterType).ToArray();
            Assert.That(actual, Is.EqualTo(parameters),
                $"LocalMovePredictor.{name}'s parameters are part of the cross-package contract.");
        }

        [Test]
        public void RecordInputKeepsItsSignature() =>
            AssertSignature(nameof(LocalMovePredictor.RecordInput),
                typeof(void), typeof(long), typeof(float), typeof(float));

        /// <remarks>
        /// Takes a <see cref="Vec2"/> — the server's 2D space — not a world-space vector.
        /// The DOTS side converts from <c>ReconciliationAnchor.ServerPosition</c> at the
        /// boundary. Widening this to accept world space would silently move the clamp
        /// against <see cref="MapBounds"/> into the wrong coordinate system.
        /// </remarks>
        [Test]
        public void ReconcileKeepsItsSignature() =>
            AssertSignature(nameof(LocalMovePredictor.Reconcile),
                typeof(void), typeof(Vec2), typeof(long));

        [Test]
        public void AdvanceKeepsItsSignature() =>
            AssertSignature(nameof(LocalMovePredictor.Advance), typeof(void), typeof(float));

        [Test]
        public void ResetKeepsItsSignature() =>
            AssertSignature(nameof(LocalMovePredictor.Reset), typeof(void));

        /// <remarks>
        /// Added in 0.16.0. A consumer that binds views itself — rather than through
        /// <c>WorldViewBinder</c> — must call this with the snapshot's world tick, or the
        /// hold window's phase never aligns with the server's and the correction never
        /// goes away. Pinned so that "nobody in this repo calls it" can never be read as
        /// "nothing calls it".
        /// </remarks>
        [Test]
        public void SeedBaseTickKeepsItsSignature() =>
            AssertSignature(nameof(LocalMovePredictor.SeedBaseTick), typeof(void), typeof(long));

        [Test]
        public void PositionIsAReadableVec2()
        {
            var property = typeof(LocalMovePredictor).GetProperty(nameof(LocalMovePredictor.Position));
            Assert.That(property, Is.Not.Null);
            Assert.That(property.PropertyType, Is.EqualTo(typeof(Vec2)),
                "The DOTS driving system reads this straight into LocalTransform via the " +
                "space mapping; its type is part of the contract.");
            Assert.That(property.CanRead, Is.True);
        }

        [Test]
        public void IsEnabledIsAReadableBool()
        {
            var property = typeof(LocalMovePredictor).GetProperty(nameof(LocalMovePredictor.IsEnabled));
            Assert.That(property, Is.Not.Null);
            Assert.That(property.PropertyType, Is.EqualTo(typeof(bool)));
            Assert.That(property.CanRead, Is.True,
                "The DOTS side gates adding the PredictedTransform marker on this. Without " +
                "it, a refusing predictor would still claim LocalTransform and nothing would " +
                "write the transform at all — the avatar freezes, in a build, not in CI.");
        }

        /// <summary>
        /// Pins the diagnostic surface a consumer reads when its own game rubber-bands.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>These are contract, not scaffolding.</b> The distinction the package draws is
        /// simple: <c>Runtime/</c> may expose a member because a <i>consumer</i> needs it,
        /// never because a test does. Everything named here answers a question a developer
        /// building gameplay on this package will actually ask — "is my avatar's rendered
        /// position keeping up with its simulated one, and if not, on which of the six
        /// possible reasons is the hold declining?" — and the pre-existing correction
        /// counters (<c>Snaps</c>, <c>ReplayedSteps</c>, <c>Reconciles</c>…) provably cannot
        /// answer it: in the 0.16.0 freeze the simulated position was right the whole time
        /// and every correction counter stayed clean.
        /// </para>
        /// <para>
        /// <c>Tests/Runtime/PredictionLatencyMeasurement.cs</c> reads them too, but it is a
        /// reporting rig, not an assertion: it prints them for a human. Nothing in
        /// <c>Runtime/</c> depends on it, and removing the rig would not make any member
        /// here unnecessary.
        /// </para>
        /// </remarks>
        [Test]
        public void TheDiagnosticSurfaceIsPinnedDeliberately()
        {
            AssertReadable<float>(nameof(LocalMovePredictor.IntegrationTimestep));
            AssertReadable<float>(nameof(LocalMovePredictor.EffectiveSmoothingSpan));
            AssertReadable<float>(nameof(LocalMovePredictor.RenderStepProgress));
            AssertReadable<float>(nameof(LocalMovePredictor.ObservedStepInterval));
            AssertReadable<bool>(nameof(LocalMovePredictor.HoldIsActive));
            AssertReadable<long>(nameof(LocalMovePredictor.BaseTick));
            AssertReadable<int>(nameof(LocalMovePredictor.BaseTicksAdvanced));
            AssertReadable<int>(nameof(LocalMovePredictor.HeldStepsApplied));
            AssertReadable<int>(nameof(LocalMovePredictor.StepIntervalSamples));
            AssertReadable<int>(nameof(LocalMovePredictor.StepIntervalResets));
            AssertReadable<int>(nameof(LocalMovePredictor.SkipNoHoldWindow));
            AssertReadable<int>(nameof(LocalMovePredictor.SkipNothingHeld));
            AssertReadable<int>(nameof(LocalMovePredictor.SkipInputAlreadyStepped));
            AssertReadable<int>(nameof(LocalMovePredictor.SkipExpired));
            AssertReadable<int>(nameof(LocalMovePredictor.SkipRefusedByMovementModel));
            AssertReadable<int>(nameof(LocalMovePredictor.SkipNoDisplacement));
        }

        /// <summary>
        /// The reason code behind the six <c>Skip*</c> counters stays private: it is how
        /// the class talks to itself, and publishing it would invite a consumer to switch
        /// on values this package expects to be free to extend.
        /// </summary>
        [Test]
        public void TheHoldSkipReasonCodeIsNotPublic()
        {
            var leaked = typeof(LocalMovePredictor)
                .GetNestedTypes(BindingFlags.Public)
                .Select(t => t.Name)
                .ToArray();

            Assert.That(leaked, Is.Empty,
                "LocalMovePredictor should expose no public nested type. Offenders: " +
                string.Join(", ", leaked));
        }

        private static void AssertReadable<T>(string name)
        {
            var property = typeof(LocalMovePredictor).GetProperty(name);
            Assert.That(property, Is.Not.Null,
                $"LocalMovePredictor.{name} is a documented diagnostic. Removing it takes " +
                "away the only way a consumer can see a render-side freeze, which no " +
                "correction counter reports.");
            Assert.That(property.PropertyType, Is.EqualTo(typeof(T)),
                $"LocalMovePredictor.{name}'s type is documented in NETCODE.md.");
            Assert.That(property.CanRead, Is.True);
            Assert.That(property.GetSetMethod(), Is.Null,
                $"LocalMovePredictor.{name} is observation, not configuration.");
        }

        /// <summary>
        /// The predictor must stay free of DOTS and Unity types, or the dots package could
        /// not drive it without this package taking the dependency back.
        /// </summary>
        [Test]
        public void PredictorSurfaceNamesNoEngineTypes()
        {
            var assembly = typeof(LocalMovePredictor).Assembly;

            var offenders = typeof(LocalMovePredictor)
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(DescribeType)
                .Where(t => t != null)
                .Where(t => t.Namespace != null &&
                            (t.Namespace.StartsWith("Unity", StringComparison.Ordinal) ||
                             t.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal)))
                .Select(t => t.FullName)
                .Distinct()
                .ToArray();

            Assert.That(offenders, Is.Empty,
                "LocalMovePredictor's surface must name no Unity or DOTS type. It is driven " +
                "from com.cuvara.dots, which references this assembly — so a DOTS type here " +
                "would force the dependency to point both ways. It is also what keeps the " +
                "algorithm testable in EditMode without a World. Offenders: " +
                string.Join(", ", offenders));

            Assert.That(assembly.GetReferencedAssemblies().Select(a => a.Name),
                Has.No.Member("Unity.Entities"),
                "Cuvara.Netcode.Runtime must not reference Unity.Entities.");
        }

        private static Type DescribeType(MemberInfo member) => member switch
        {
            PropertyInfo p => p.PropertyType,
            FieldInfo f => f.FieldType,
            MethodInfo m => m.ReturnType,
            _ => null,
        };

        /// <summary>
        /// Compile-time half: this is the call sequence the DOTS driving system makes. It
        /// stops compiling if any signature moves, which is the earliest possible warning.
        /// </summary>
        [Test]
        public void TheDotsDrivingSequenceStillCompilesAndRuns()
        {
            var predictor = new LocalMovePredictor(
                new PredictionSettings(tickRate: 15, speed: 5f, MapBounds.Default));

            Assert.That(predictor.IsEnabled, Is.True);

            // Anchor + AckTick, paired by the caller — the seam that only the predictor sees.
            predictor.Reconcile(new Vec2(1f, 2f), 0L);
            predictor.RecordInput(1L, 1f, 0f);
            predictor.Reconcile(new Vec2(1.1f, 2f), 1L);
            predictor.Advance(1f / 60f);

            Vec2 render = predictor.Position;
            Assert.That(float.IsFinite(render.X) && float.IsFinite(render.Y), Is.True);

            predictor.Reset();
        }
    }
}
