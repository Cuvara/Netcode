using NUnit.Framework;
using Cuvara.Netcode.Interpolation;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// <see cref="InterpolationConfig.Normalized"/> and the three fields 0.48.0 added
    /// (<see cref="InterpolationConfig.ExtrapolationHold"/>,
    /// <see cref="InterpolationConfig.ExtrapolationSettle"/>,
    /// <see cref="InterpolationConfig.MaxExtrapolationSpan"/>), for which zero is a meaningful
    /// explicit value.
    /// </summary>
    /// <remarks>
    /// <c>DotsNetcodeBootstrap.Install(world, view)</c> passes <c>default(InterpolationConfig)</c>
    /// and promises netcode's defaults. Before this fix the new fields were filled only when
    /// negative, so the all-zero struct normalised to hold 0 / settle 0 / span 0 while the binder
    /// ran with <see cref="InterpolationConfig.Default"/>'s 0.15 / 0.10 / 0.25: two presentation
    /// paths, one package, two behaviours (UnityDots audit finding F4).
    /// </remarks>
    [TestFixture]
    public sealed class InterpolationConfigNormalizationTests
    {
        [Test]
        public void TheAllZeroConfig_TakesTheDefaultExtrapolationShaping()
        {
            InterpolationConfig normalised = default(InterpolationConfig).Normalized();
            InterpolationConfig d = InterpolationConfig.Default;

            Assert.That(normalised.ExtrapolationHold, Is.EqualTo(d.ExtrapolationHold));
            Assert.That(normalised.ExtrapolationSettle, Is.EqualTo(d.ExtrapolationSettle));
            Assert.That(normalised.MaxExtrapolationSpan, Is.EqualTo(d.MaxExtrapolationSpan));
        }

        [Test]
        public void TheAllZeroConfig_KeepsItsOtherRules()
        {
            // Only the three new fields change meaning for the all-zero struct. MaxExtrapolation
            // keeps its rule (zero means "never extrapolate") and the speed guard stays off.
            InterpolationConfig normalised = default(InterpolationConfig).Normalized();

            Assert.That(normalised.MaxExtrapolation, Is.EqualTo(0.0));
            Assert.That(normalised.MaxExtrapolationSpeed, Is.EqualTo(0.0));
            Assert.That(normalised.DeferUntilBracketed, Is.False);
            Assert.That(normalised.RingCapacity, Is.EqualTo(InterpolationConfig.Default.RingCapacity));
        }

        [Test]
        public void ExplicitZeros_OnAConfiguredValue_AreKept()
        {
            // A configured value that sets these to zero asked for exactly that: snap back with no
            // hold, and no span check.
            InterpolationConfig c = InterpolationConfig.Default;
            c.ExtrapolationHold = 0.0;
            c.ExtrapolationSettle = 0.0;
            c.MaxExtrapolationSpan = 0.0;

            InterpolationConfig normalised = c.Normalized();

            Assert.That(normalised.ExtrapolationHold, Is.EqualTo(0.0));
            Assert.That(normalised.ExtrapolationSettle, Is.EqualTo(0.0));
            Assert.That(normalised.MaxExtrapolationSpan, Is.EqualTo(0.0));
        }

        [Test]
        public void ExplicitValues_AreKept_AndNegativesStillTakeTheDefault()
        {
            InterpolationConfig c = InterpolationConfig.Default;
            c.ExtrapolationHold = 0.5;
            c.ExtrapolationSettle = -1.0;
            c.MaxExtrapolationSpan = double.NaN;

            InterpolationConfig normalised = c.Normalized();

            Assert.That(normalised.ExtrapolationHold, Is.EqualTo(0.5));
            Assert.That(normalised.ExtrapolationSettle, Is.EqualTo(InterpolationConfig.Default.ExtrapolationSettle));
            Assert.That(normalised.MaxExtrapolationSpan, Is.EqualTo(InterpolationConfig.Default.MaxExtrapolationSpan));
        }
    }
}
