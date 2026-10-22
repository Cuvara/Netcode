using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using Cuvara.Netcode.World;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// An entity's life as a view sees it, through the same three stages the client runs:
    /// <see cref="SnapshotResolver"/> (handles to ids), <see cref="WorldState"/> (the merge) and
    /// <see cref="WorldViewBinder"/> (spawn, state, despawn).
    /// </summary>
    /// <remarks>
    /// <see cref="SnapshotResolverInterningTests"/> pins each handle rule on the resolver alone.
    /// This fixture pins what those rules add up to on screen: a despawned entity stays gone, a
    /// handle reused for a different entity after a keyframe moves the new entity and never
    /// resurrects the old one, and a snapshot with an unknown handle changes nothing at all until
    /// the keyframe the caller asks for arrives (<c>GameSessionClient</c> requests it when
    /// <see cref="SnapshotResolver.TryResolve"/> returns false).
    /// </remarks>
    [TestFixture]
    public sealed class EntityLifecycleTests
    {
        private sealed class RecordingView : IEntityView
        {
            public readonly List<string> Events = new List<string>();
            public readonly Dictionary<string, float> X = new Dictionary<string, float>();

            public void Spawn(string id, bool isLocal, string type) => Events.Add("spawn " + id);

            public void Despawn(string id)
            {
                Events.Add("despawn " + id);
                X.Remove(id);
            }

            public void SetState(string id, float x, float y, int hp, int maxHp) => X[id] = x;
        }

        private sealed class Client
        {
            public readonly SnapshotResolver Resolver = new SnapshotResolver();
            public readonly WorldState World = new WorldState();
            public readonly RecordingView View = new RecordingView();
            public readonly WorldViewBinder Binder;
            private long _tick = 100;

            public Client() => Binder = new WorldViewBinder(View);

            /// <summary>Resolves, and applies only what resolved, exactly as GameSessionClient does.</summary>
            public bool Receive(bool full, IEnumerable<EntitySnapshot> entities, params string[] removed)
            {
                var m = new SnapshotMessage { Tick = _tick += 4, AckTick = 0, Full = full };
                foreach (var e in entities) m.Entities.Add(e);
                foreach (var r in removed) m.Removed.Add(r);

                if (!Resolver.TryResolve(m, out ResolvedSnapshot resolved))
                {
                    Binder.Tick(World, "local-user");
                    return false;
                }

                World.Apply(resolved);
                Binder.NoteRemovedIds(resolved.Removed);
                Binder.Tick(World, "local-user");
                return true;
            }
        }

        private static EntitySnapshot Introduce(string id, uint handle, float x) =>
            new EntitySnapshot { Id = id, Handle = handle, Type = "enemy", X = x, Hp = 100, MaxHp = 100 };

        private static EntitySnapshot Interned(uint handle, float x) =>
            new EntitySnapshot { Id = string.Empty, Handle = handle, Type = "enemy", X = x, Hp = 100, MaxHp = 100 };

        [Test]
        public void ARemovedEntity_IsDespawnedOnce_AndStaysGone()
        {
            var c = new Client();
            c.Receive(true, new[] { Introduce("a", 1, 0f), Introduce("b", 2, 0f) });
            Assert.That(c.View.Events, Is.EquivalentTo(new[] { "spawn a", "spawn b" }));

            c.Receive(false, new[] { Interned(2, 1f) }, "a");
            c.Receive(false, new[] { Interned(2, 2f) });
            c.Receive(false, new EntitySnapshot[0]);

            Assert.That(c.View.Events, Is.EqualTo(new[] { "spawn a", "spawn b", "despawn a" }).Or
                .EquivalentTo(new[] { "spawn b", "spawn a", "despawn a" }));
            Assert.That(c.World.Entities.ContainsKey("a"), Is.False);
            Assert.That(c.Binder.DespawnsFromRemoval, Is.EqualTo(1));
            Assert.That(c.View.X.ContainsKey("a"), Is.False);
        }

        [Test]
        public void AnEntityAKeyframeOmits_IsDespawned()
        {
            var c = new Client();
            c.Receive(true, new[] { Introduce("a", 1, 0f), Introduce("b", 2, 0f) });

            c.Receive(true, new[] { Introduce("b", 1, 0f) }); // keyframe: a left the area of interest

            Assert.That(c.View.Events, Has.Member("despawn a"));
            Assert.That(c.View.Events, Has.No.Member("despawn b"));
            Assert.That(c.World.Entities.ContainsKey("a"), Is.False);
        }

        [Test]
        public void AHandleReusedAfterADespawn_MovesTheNewEntity_AndNeverTheOldOne()
        {
            var c = new Client();
            c.Receive(true, new[] { Introduce("a", 1, 0f), Introduce("b", 2, 0f) });
            c.Receive(false, new[] { Interned(2, 1f) }, "a"); // a despawns; handle 1 is free

            // The next keyframe restarts the handle space and gives handle 1 to a new entity.
            c.Receive(true, new[] { Introduce("c", 1, 50f), Introduce("b", 2, 1f) });
            c.Receive(false, new[] { Interned(1, 55f) });

            Assert.That(c.World.Entities.ContainsKey("a"), Is.False, "the old entity was not resurrected");
            Assert.That(c.World.Entities["c"].X, Is.EqualTo(55f), "handle 1 now names c");
            Assert.That(c.View.Events.FindAll(e => e == "spawn a").Count, Is.EqualTo(1));
            Assert.That(c.View.Events.FindAll(e => e == "spawn c").Count, Is.EqualTo(1));
        }

        [Test]
        public void AnUnknownHandle_AbortsTheWholeSnapshot_UntilTheResyncKeyframe()
        {
            var c = new Client();
            c.Receive(true, new[] { Introduce("a", 1, 0f) });

            // Handle 1 moves a, handle 9 was never introduced: nothing of this snapshot applies,
            // including the part that WOULD have resolved.
            bool applied = c.Receive(false, new[] { Interned(1, 5f), Interned(9, 3f) });

            Assert.That(applied, Is.False);
            Assert.That(c.Resolver.UnresolvedCount, Is.EqualTo(1));
            Assert.That(c.World.Entities["a"].X, Is.EqualTo(0f), "a partly applied snapshot attributes state it cannot vouch for");
            Assert.That(c.World.Count, Is.EqualTo(1));
            long tickBefore = c.World.Tick;

            // The keyframe the caller requested.
            Assert.That(c.Receive(true, new[] { Introduce("a", 1, 6f), Introduce("z", 9, 3f) }), Is.True);

            Assert.That(c.World.Tick, Is.GreaterThan(tickBefore));
            Assert.That(c.World.Entities["a"].X, Is.EqualTo(6f));
            Assert.That(c.World.Entities["z"].X, Is.EqualTo(3f));
            Assert.That(c.Receive(false, new[] { Interned(9, 4f) }), Is.True, "handle 9 resolves after the resync");
            Assert.That(c.View.Events, Has.Member("spawn z"));
        }
    }
}
