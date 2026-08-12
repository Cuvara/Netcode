using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Snapshot;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Entity-handle interning: the branches only a Protobuf connection can reach.
    /// </summary>
    /// <remarks>
    /// On JSON every entity carries its id, so resolution cannot fail and none of this
    /// is exercised — which is exactly why these cases were untested while the client
    /// looked fully certified. Each failure here is silent in production: it renders as
    /// an entity in the wrong place, or as two entities collapsed into one, with no
    /// exception and no log.
    /// </remarks>
    [TestFixture]
    public sealed class SnapshotResolverInterningTests
    {
        private static SnapshotMessage Keyframe(params EntitySnapshot[] entities) =>
            Build(true, entities);

        private static SnapshotMessage Delta(params EntitySnapshot[] entities) =>
            Build(false, entities);

        private static SnapshotMessage Build(bool full, EntitySnapshot[] entities)
        {
            var m = new SnapshotMessage { Tick = 1, AckTick = 0, Full = full };
            foreach (var e in entities)
            {
                m.Entities.Add(e);
            }

            return m;
        }

        private static EntitySnapshot Introduce(string id, uint handle) =>
            new EntitySnapshot { Id = id, Handle = handle, Type = "player", Hp = 100, MaxHp = 100 };

        private static EntitySnapshot Interned(uint handle) =>
            new EntitySnapshot { Id = string.Empty, Handle = handle, Type = "player", Hp = 100, MaxHp = 100 };

        [Test]
        public void IntroducedHandleResolvesOnALaterDelta()
        {
            var resolver = new SnapshotResolver();

            Assert.IsTrue(resolver.TryResolve(Keyframe(Introduce("entity-a", 1)), out _));
            Assert.IsTrue(resolver.TryResolve(Delta(Interned(1)), out var delta));

            Assert.AreEqual("entity-a", delta.Entities[0].Id,
                "a delta carrying only a handle must resolve to the id the keyframe introduced");
        }

        // --- Branch 1: a delta naming a handle we hold no binding for ---

        [Test]
        public void UnknownHandleOnADeltaAbortsTheWholeSnapshot()
        {
            var resolver = new SnapshotResolver();
            resolver.TryResolve(Keyframe(Introduce("entity-a", 1)), out _);

            // Handle 7 was never introduced.
            var ok = resolver.TryResolve(Delta(Interned(1), Interned(7)), out _);

            Assert.IsFalse(ok, "an unresolvable handle must fail the snapshot, not the entity");
            Assert.AreEqual(1, resolver.UnresolvedCount);
        }

        [Test]
        public void AnAbortedSnapshotLeavesEarlierBindingsUsable()
        {
            var resolver = new SnapshotResolver();
            resolver.TryResolve(Keyframe(Introduce("entity-a", 1)), out _);
            resolver.TryResolve(Delta(Interned(7)), out _);   // aborts

            // The abort must not have disturbed handle 1.
            Assert.IsTrue(resolver.TryResolve(Delta(Interned(1)), out var after),
                "an aborted snapshot must leave the handle table untouched");
            Assert.AreEqual("entity-a", after.Entities[0].Id);
        }

        [Test]
        public void ABindingIntroducedByAnAbortedSnapshotIsNotRecorded()
        {
            var resolver = new SnapshotResolver();
            resolver.TryResolve(Keyframe(Introduce("entity-a", 1)), out _);

            // Introduces handle 2, then fails on unknown handle 7 in the same snapshot.
            resolver.TryResolve(Delta(Introduce("entity-b", 2), Interned(7)), out _);

            Assert.IsFalse(resolver.TryResolve(Delta(Interned(2)), out _),
                "a binding from a snapshot that aborted must never be committed");
        }

        // --- Branch 2: a keyframe carrying a bare handle is malformed ---

        [Test]
        public void BareHandleOnAKeyframeAborts()
        {
            var resolver = new SnapshotResolver();
            resolver.TryResolve(Keyframe(Introduce("entity-a", 1)), out _);

            // A keyframe resets the sender's handle space and re-introduces every
            // binding, so a keyframe entity with a handle and no id is malformed.
            var ok = resolver.TryResolve(Keyframe(Interned(1)), out _);

            Assert.IsFalse(ok, "a keyframe must introduce every binding it uses");
        }

        [Test]
        public void BareHandleOnAKeyframeIsRejectedWithoutConsultingTheTable()
        {
            var resolver = new SnapshotResolver();
            resolver.TryResolve(Keyframe(Introduce("entity-a", 1)), out _);

            // Handle 1 IS STILL BOUND — to the previous interval's entity. That is what
            // makes this test discriminating: without the guard the lookup SUCCEEDS, so
            // the snapshot is accepted and this update is silently attributed to
            // entity-a. A test using an UNBOUND handle would pass either way and prove
            // nothing, because the table miss would catch it regardless.
            var ok = resolver.TryResolve(Keyframe(Interned(1)), out var rejected);

            Assert.IsFalse(ok, "a bound-but-stale handle on a keyframe must fail, not resolve");

            // Nothing may be handed back to the caller — not even the one entity that
            // "resolved". A caller that applied this would overwrite entity-a's state
            // with another entity's.
            Assert.IsNull(rejected.Entities,
                "a rejected snapshot must yield no entities at all");

            // All-or-nothing: the table is exactly as it was. This assertion is what
            // fails if anyone later hoists the keyframe clear above the resolve loop.
            Assert.IsTrue(resolver.TryResolve(Delta(Interned(1)), out var after),
                "a rejected keyframe must not clear the handle table");
            Assert.AreEqual("entity-a", after.Entities[0].Id,
                "the previously bound entity must be intact and still bound to handle 1");
        }

        // --- The handle space restarts at every keyframe ---

        [Test]
        public void AKeyframeRebindsTheSameHandleToADifferentEntity()
        {
            var resolver = new SnapshotResolver();

            resolver.TryResolve(Keyframe(Introduce("entity-a", 1)), out _);
            resolver.TryResolve(Keyframe(Introduce("entity-b", 1)), out _);

            Assert.IsTrue(resolver.TryResolve(Delta(Interned(1)), out var after));
            Assert.AreEqual("entity-b", after.Entities[0].Id,
                "handle 1 after a keyframe is a different entity than handle 1 before it");
        }

        [Test]
        public void DoubleRebindAcrossAKeyframeKeepsEachHandleDistinct()
        {
            var resolver = new SnapshotResolver();

            resolver.TryResolve(
                Keyframe(Introduce("alpha", 1), Introduce("beta", 2), Introduce("gamma", 3)), out _);

            // Two handles swap owners across the resync, one keeps its owner. A client
            // that reused its old table would render alpha and gamma as each other.
            resolver.TryResolve(
                Keyframe(Introduce("gamma", 1), Introduce("beta", 2), Introduce("alpha", 3)), out _);

            Assert.IsTrue(resolver.TryResolve(
                Delta(Interned(1), Interned(2), Interned(3)), out var after));

            Assert.AreEqual("gamma", after.Entities[0].Id);
            Assert.AreEqual("beta", after.Entities[1].Id);
            Assert.AreEqual("alpha", after.Entities[2].Id);
        }

        [Test]
        public void HandleBoundOnlyInAPreviousIntervalNoLongerResolves()
        {
            var resolver = new SnapshotResolver();

            resolver.TryResolve(Keyframe(Introduce("alpha", 1), Introduce("delta", 4)), out _);
            resolver.TryResolve(Keyframe(Introduce("alpha", 1)), out _);   // 4 not re-introduced

            Assert.IsFalse(resolver.TryResolve(Delta(Interned(4)), out _),
                "a handle the new keyframe did not re-introduce must not resolve from the old table");
        }

        // --- Sentinels and removals ---

        [Test]
        public void HandleZeroMeansNotInternedAndTheIdIsUsedDirectly()
        {
            var resolver = new SnapshotResolver();

            // handle 0 + a real id is a legitimate frame, not an error: interning is
            // optional and JSON never uses it.
            Assert.IsTrue(resolver.TryResolve(
                Delta(new EntitySnapshot { Id = "plain", Handle = 0, Type = "mob" }), out var resolved));
            Assert.AreEqual("plain", resolved.Entities[0].Id);
        }

        [Test]
        public void AnEntityWithNeitherIdNorHandleAborts()
        {
            var resolver = new SnapshotResolver();

            Assert.IsFalse(resolver.TryResolve(
                Delta(new EntitySnapshot { Id = string.Empty, Handle = 0 }), out _),
                "nothing identifies this entity, so the snapshot is unusable");
        }

        [Test]
        public void RemovalsPassThroughAsIdsAndDoNotReleaseTheHandle()
        {
            var resolver = new SnapshotResolver();
            resolver.TryResolve(Keyframe(Introduce("entity-a", 1)), out _);

            var removal = Delta(Interned(1));
            removal.Removed.Add("entity-a");

            Assert.IsTrue(resolver.TryResolve(removal, out var resolved));
            CollectionAssert.Contains(resolved.Removed, "entity-a",
                "removed carries entity IDs, never handles");

            // A removal does not release the binding — the handle stays valid for the
            // rest of the interval.
            Assert.IsTrue(resolver.TryResolve(Delta(Interned(1)), out var after));
            Assert.AreEqual("entity-a", after.Entities[0].Id);
        }

        [Test]
        public void ResetForgetsEveryBinding()
        {
            var resolver = new SnapshotResolver();
            resolver.TryResolve(Keyframe(Introduce("entity-a", 1)), out _);

            resolver.TryResolve(Delta(Interned(9)), out _);   // bump the counter
            Assert.AreEqual(1, resolver.UnresolvedCount);

            resolver.Reset();
            Assert.AreEqual(0, resolver.UnresolvedCount, "Reset also clears the counter");

            Assert.IsFalse(resolver.TryResolve(Delta(Interned(1)), out _),
                "a fresh connection starts with no bindings");
        }
    }
}
