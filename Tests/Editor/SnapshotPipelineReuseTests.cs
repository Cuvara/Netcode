using System.Collections.Generic;
using System.Reflection;
using Google.Protobuf;
using NUnit.Framework;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.World;
using Pb = RpgMmo.Wire.V1;
using SimAction = Shared.GameLogic.Components.EntityAction;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The buffer reuse added for Cuvara/IndieRPGMMOAdventure#61, asserted from the side
    /// that can actually go wrong: content, never allocation counts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reusing a decode buffer has exactly one failure mode and it is silent. A field left
    /// unwritten, or a list left untrimmed, does not throw and does not log — it serves the
    /// PREVIOUS snapshot's value as though it were this tick's, which renders as an entity
    /// that stopped updating, or as one that came back from the dead at its old position.
    /// So every test here decodes or applies at least twice and asserts on the second
    /// result, because a single-shot test passes against a completely broken pool.
    /// </para>
    /// <para>
    /// The second frame is deliberately made SMALLER and given different values than the
    /// first. A second frame of the same shape would be indistinguishable from stale data.
    /// </para>
    /// </remarks>
    public class SnapshotPipelineReuseTests
    {
        private static byte[] SnapshotBody(bool full, long tick, params Pb.EntitySnapshot[] entities)
        {
            var s = new Pb.SnapshotMessage { Tick = (ulong)tick, Full = full };
            s.Entities.AddRange(entities);
            var env = new Pb.Envelope
            {
                Type = (uint)MsgType.Snapshot,
                Payload = ByteString.CopyFrom(s.ToByteArray()),
            };
            return env.ToByteArray();
        }

        private static Pb.EntitySnapshot Entity(string id, uint handle, float x, int hp) =>
            new Pb.EntitySnapshot { Id = id, Handle = handle, X = x, Hp = hp, MaxHp = 100 };

        // ── The decode pool ──────────────────────────────────────────────────────

        [Test]
        public void DefaultCodec_ReturnsAFreshMessagePerDecode()
        {
            // The default must keep the contract it always had: two decoded snapshots can
            // be held at once. Every caller outside WireConnection relies on it.
            var codec = new ProtobufWireCodec();
            byte[] body = SnapshotBody(true, 1, Entity("a", 1, 1f, 90));

            var first = (SnapshotMessage)codec.DecodeBody(body).Payload;
            var second = (SnapshotMessage)codec.DecodeBody(body).Payload;

            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.Entities[0], Is.Not.SameAs(first.Entities[0]));
        }

        [Test]
        public void ReusingCodec_ReturnsTheSameMessageInstance()
        {
            var codec = ProtobufWireCodec.CreatePooled();
            byte[] body = SnapshotBody(true, 1, Entity("a", 1, 1f, 90));

            var first = (SnapshotMessage)codec.DecodeBody(body).Payload;
            var second = (SnapshotMessage)codec.DecodeBody(body).Payload;

            Assert.That(second, Is.SameAs(first),
                "the opt-in pool is documented to invalidate the previous decode; if this " +
                "stops being true the WireConnection comment justifying it is wrong too");
        }

        [Test]
        public void ReusingCodec_SecondSnapshotDoesNotInheritTheFirstsEntities()
        {
            var codec = ProtobufWireCodec.CreatePooled();

            codec.DecodeBody(SnapshotBody(true, 1,
                Entity("a", 1, 1f, 90), Entity("b", 2, 2f, 80), Entity("c", 3, 3f, 70)));

            var second = (SnapshotMessage)codec.DecodeBody(
                SnapshotBody(false, 2, Entity("a", 1, 9f, 10))).Payload;

            // The list must be trimmed to one, not left holding b and c at last tick's
            // positions. This is the resurrection failure the pool would otherwise cause.
            Assert.That(second.Entities.Count, Is.EqualTo(1));
            Assert.That(second.Entities[0].Id, Is.EqualTo("a"));
            Assert.That(second.Entities[0].X, Is.EqualTo(9f));
            Assert.That(second.Entities[0].Hp, Is.EqualTo(10));
            Assert.That(second.Tick, Is.EqualTo(2));
            Assert.That(second.Full, Is.False);
        }

        [Test]
        public void ReusingCodec_FieldsAbsentFromTheSecondFrameReadAsDefaultsNotAsStale()
        {
            // proto3 elides a zero, so the second frame carries NO bytes for speed, facing,
            // action or action_seq. A pooled entity that skipped writing them would serve
            // the first frame's values, and every one of those reads as a plausible live
            // value rather than as an error.
            var codec = ProtobufWireCodec.CreatePooled();

            var rich = new Pb.EntitySnapshot
            {
                Id = "a", Handle = 1, X = 5f, Y = 6f, Hp = 90, MaxHp = 100,
                Speed = 4.5f, FacingBrad = 1234, Action = (Pb.EntityAction)2,
                ActionSeq = 7, ChangedFields = 12,
            };
            codec.DecodeBody(SnapshotBody(true, 1, rich));

            var bare = new Pb.EntitySnapshot { Id = "a", Handle = 1 };
            var second = (SnapshotMessage)codec.DecodeBody(SnapshotBody(true, 2, bare)).Payload;
            var e = second.Entities[0];

            Assert.That(e.Speed, Is.Zero, "stale speed would silently override the spawn default");
            Assert.That(e.FacingBrad, Is.Zero);
            Assert.That(e.Action, Is.EqualTo(default(SimAction)));
            Assert.That(e.ActionSeq, Is.Zero, "a stale action_seq replays a swing that never happened");
            Assert.That(e.ChangedFields, Is.Zero,
                "a stale mask asserts fields are present that the frame never carried");
            Assert.That(e.X, Is.Zero);
            Assert.That(e.Y, Is.Zero);
            Assert.That(e.Hp, Is.Zero);
        }

        [Test]
        public void ReusingCodec_RemovedAndEventsDoNotAccumulate()
        {
            var codec = ProtobufWireCodec.CreatePooled();

            var withExtras = new Pb.SnapshotMessage { Tick = 1, Full = false };
            withExtras.Removed.AddRange(new[] { "x", "y" });
            withExtras.Events.Add(new Pb.GameEvent { Type = (Pb.GameEventType)1, Amount = 5 });
            codec.DecodeBody(Wrap(withExtras));

            var plain = new Pb.SnapshotMessage { Tick = 2, Full = false };
            plain.Removed.Add("z");
            var second = (SnapshotMessage)codec.DecodeBody(Wrap(plain)).Payload;

            Assert.That(second.Removed, Is.EqualTo(new List<string> { "z" }));
            Assert.That(second.Events.Count, Is.Zero,
                "a replayed event shows the player a hit that happened once, twice");
        }

        private static byte[] Wrap(Pb.SnapshotMessage s) => new Pb.Envelope
        {
            Type = (uint)MsgType.Snapshot,
            Payload = ByteString.CopyFrom(s.ToByteArray()),
        }.ToByteArray();

        [Test]
        public void ProtobufWireCodec_HasExactlyOneParameterlessConstructor()
        {
            // Guards the DI contract from outside Unity. `RegisterNetworking` registers this
            // type with VContainer, whose TypeAnalyzer picks a constructor by reflection and
            // takes the GREEDIEST one, then tries to resolve its parameters out of the
            // container. Adding `ProtobufWireCodec(bool)` broke RegisterNetworking at
            // resolve time while every codec test here stayed green: 691/692 in CI.
            //
            // NonPublic is in the mask deliberately, and it is the whole point of this
            // assertion. The first attempt at a fix only made that constructor private,
            // which looked sufficient and was not — VContainer reflects with NonPublic
            // included, so the identical failure came back on the next run. A test checking
            // only public constructors passed both times and proved nothing.
            var ctors = typeof(ProtobufWireCodec).GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(ctors.Length, Is.EqualTo(1),
                "VContainer reflects over NON-PUBLIC constructors too and takes the " +
                "greediest, so a second constructor of any accessibility makes it try to " +
                "inject that constructor's parameters");
            Assert.That(ctors[0].GetParameters(), Is.Empty);
            Assert.That(ctors[0].IsPublic, Is.True);
        }

        // ── WorldState's conversion buffers ──────────────────────────────────────

        private static ResolvedSnapshot Resolved(bool full, long tick,
            IReadOnlyList<ResolvedEntity> entities, IReadOnlyList<string> removed = null) =>
            new ResolvedSnapshot(tick, 0, full, entities, removed);

        private static ResolvedEntity Ent(string id, float x, int hp) =>
            new ResolvedEntity(id, "mob", x, 0f, hp, 100, 0f, 0, default(SimAction));

        [Test]
        public void WorldState_ShrinkingThenGrowingDoesNotResurrectEntities()
        {
            // The exact-length rule under test: three entities, then two, then three again.
            // A buffer reused at the wrong length would replay whatever the tail still held.
            var world = new WorldState();

            world.Apply(Resolved(true, 1, new[] { Ent("a", 1f, 90), Ent("b", 2f, 80), Ent("c", 3f, 70) }));
            Assert.That(world.Count, Is.EqualTo(3));

            world.Apply(Resolved(true, 2, new[] { Ent("a", 10f, 50), Ent("b", 20f, 40) }));
            Assert.That(world.Count, Is.EqualTo(2), "c was absent from a keyframe and must be gone");
            Assert.That(world.TryGet("c", out _), Is.False);

            world.Apply(Resolved(true, 3, new[] { Ent("a", 100f, 5), Ent("b", 200f, 6), Ent("d", 300f, 7) }));
            Assert.That(world.Count, Is.EqualTo(3));
            Assert.That(world.TryGet("c", out _), Is.False, "c must not come back from a reused buffer");

            world.TryGet("a", out var a);
            Assert.That(a.X, Is.EqualTo(100f));
            Assert.That(a.Hp, Is.EqualTo(5));
            world.TryGet("d", out var d);
            Assert.That(d.X, Is.EqualTo(300f));
        }

        [Test]
        public void WorldState_RepeatedSameSizedSnapshotsCarryTheNewValues()
        {
            // The case the reuse actually hits: identical lengths back to back. The buffer
            // is the same array every time, so every element must be overwritten.
            var world = new WorldState();
            world.Apply(Resolved(true, 1, new[] { Ent("a", 1f, 90), Ent("b", 2f, 80) }));

            for (var i = 2; i <= 5; i++)
            {
                world.Apply(Resolved(true, i, new[] { Ent("a", i, 90 - i), Ent("b", i * 2, 80 - i) }));
            }

            world.TryGet("a", out var a);
            world.TryGet("b", out var b);
            Assert.That(a.X, Is.EqualTo(5f));
            Assert.That(a.Hp, Is.EqualTo(85));
            Assert.That(b.X, Is.EqualTo(10f));
            Assert.That(world.Count, Is.EqualTo(2));
        }

        [Test]
        public void WorldState_ShrinkingRemovalListDoesNotRemoveStaleIds()
        {
            var world = new WorldState();
            world.Apply(Resolved(true, 1, new[] { Ent("a", 1f, 90), Ent("b", 2f, 80), Ent("c", 3f, 70) }));

            // Two removals, then one. A reused string[] of length 2 on the second delta
            // would still name "b" in its tail and delete an entity the server kept.
            world.Apply(Resolved(false, 2, new ResolvedEntity[0], new[] { "a", "b" }));
            Assert.That(world.Count, Is.EqualTo(1));

            world.Apply(Resolved(true, 3, new[] { Ent("a", 1f, 90), Ent("b", 2f, 80), Ent("c", 3f, 70) }));
            world.Apply(Resolved(false, 4, new ResolvedEntity[0], new[] { "c" }));

            Assert.That(world.Count, Is.EqualTo(2));
            Assert.That(world.TryGet("a", out _), Is.True, "a must survive a shorter removal list");
            Assert.That(world.TryGet("b", out _), Is.True, "b must survive a shorter removal list");
            Assert.That(world.TryGet("c", out _), Is.False);
            Assert.That(world.LastAppliedRemovedCount, Is.EqualTo(1));
        }
    }
}
