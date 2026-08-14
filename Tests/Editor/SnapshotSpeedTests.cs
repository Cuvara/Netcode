using NUnit.Framework;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Snapshot;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Covers the per-entity <c>speed</c> field (wire.proto field 9) on its way from the
    /// decoded wire message to <see cref="ResolvedEntity"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The field exists so prediction can replay local input at the speed the server is
    /// actually integrating with, rather than an assumed spawn default. A value that
    /// decodes correctly but is dropped at handle resolution would restore exactly the
    /// bug it was added to fix, and would do it silently — the entity still renders, it
    /// just predicts at the wrong speed.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class SnapshotSpeedTests
    {
        private static SnapshotMessage Snapshot(bool full, params EntitySnapshot[] entities)
        {
            var m = new SnapshotMessage { Tick = 1, AckTick = 0, Full = full };
            m.Entities.AddRange(entities);
            return m;
        }

        [Test]
        public void ResolverCarriesSpeedThrough()
        {
            var resolver = new SnapshotResolver();

            Assert.That(resolver.TryResolve(
                Snapshot(true, new EntitySnapshot
                {
                    Id = "e1", Type = "player", X = 1f, Y = 2f, Hp = 10, MaxHp = 10, Speed = 6.25f,
                }),
                out var resolved), Is.True);

            Assert.That(resolved.Entities[0].Speed, Is.EqualTo(6.25f));
        }

        /// <summary>
        /// Speed rides handle-only mentions too. The server writes it on every mention
        /// precisely so a delta is complete; dropping it here would make speed correct
        /// once per keyframe interval and stale in between.
        /// </summary>
        [Test]
        public void SpeedSurvivesHandleOnlyResolution()
        {
            var resolver = new SnapshotResolver();

            // Keyframe introduces the binding.
            Assert.That(resolver.TryResolve(
                Snapshot(true, new EntitySnapshot
                {
                    Id = "e1", Handle = 1, Type = "player", Speed = 5f,
                }),
                out _), Is.True);

            // Delta names the handle only.
            Assert.That(resolver.TryResolve(
                Snapshot(false, new EntitySnapshot
                {
                    Id = "", Handle = 1, Type = "player", Speed = 9.5f,
                }),
                out var delta), Is.True);

            Assert.That(delta.Entities[0].Id, Is.EqualTo("e1"), "handle must still resolve");
            Assert.That(delta.Entities[0].Speed, Is.EqualTo(9.5f));
        }

        /// <summary>
        /// A server predating field 9 sends nothing, which decodes as zero. That must
        /// stay zero rather than becoming a fabricated default here — the fallback is the
        /// prediction layer's decision, made where the configured default lives, not the
        /// resolver's.
        /// </summary>
        [Test]
        public void AbsentSpeedResolvesToZeroRatherThanAGuess()
        {
            var resolver = new SnapshotResolver();

            Assert.That(resolver.TryResolve(
                Snapshot(true, new EntitySnapshot { Id = "e1", Type = "player" }),
                out var resolved), Is.True);

            Assert.That(resolved.Entities[0].Speed, Is.Zero);
        }
    }
}
