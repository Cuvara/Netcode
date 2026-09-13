using NUnit.Framework;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using Cuvara.Netcode.World;
using SimAction = Shared.GameLogic.Components.EntityAction;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The per-entity facing and action fields (wire.proto fields 10 and 11) on their way
    /// from the decoded wire message to <see cref="ResolvedEntity"/>, plus the decode
    /// rules a view layer depends on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The property that matters most is that <b>no real angle collides with the reserved
    /// zero</b>. Facing is a biased integer rather than a float precisely because 0.0
    /// radians is a perfectly ordinary facing — due east — and proto3 elides a zero, so a
    /// float would make "facing east" and "this server predates the field" identical
    /// bytes. If the bias is ever lost, that collision comes back silently and every
    /// entity from an old server points the same way.
    /// </para>
    /// <para>
    /// The sibling risk is a value that decodes correctly but is dropped at handle
    /// resolution — the entity still renders, it just faces the wrong way. That is what
    /// <see cref="ResolverCarriesFacingAndActionThrough"/> and
    /// <see cref="FieldsSurviveHandleOnlyResolution"/> are for; the same shape as
    /// <c>SnapshotSpeedTests</c>.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class FacingAndActionTests
    {
        private static SnapshotMessage Snapshot(bool full, params EntitySnapshot[] entities)
        {
            var m = new SnapshotMessage { Tick = 1, AckTick = 0, Full = full };
            m.Entities.AddRange(entities);
            return m;
        }

        // ---- The encoding ---------------------------------------------------

        /// <summary>
        /// Swept far more finely than the encoding's own 65536-step resolution: every
        /// legal wire value must decode, and the reserved zero must not.
        /// </summary>
        [Test]
        public void EveryLegalWireValueDecodesAndZeroDoesNot()
        {
            Assert.That(FacingCodec.TryToRadians(FacingCodec.NotSent, out _), Is.False,
                "zero is reserved for 'not sent' and must never decode as a direction");

            Assert.That(FacingCodec.TryToRadians(1u, out float east), Is.True);
            Assert.That(east, Is.EqualTo(0f),
                "wire value 1 is due east — 0.0 radians, the value a float would have elided");

            Assert.That(FacingCodec.TryToRadians((uint)FacingCodec.BradSteps, out _), Is.True,
                "the top of the legal range must decode");
            Assert.That(FacingCodec.TryToRadians((uint)FacingCodec.BradSteps + 1, out _), Is.False,
                "out of range must be refused, not wrapped — a wrong facing is harder to notice than an absent one");
        }

        /// <summary>
        /// Decoded angles must be monotonic across the range and cover a full turn, which
        /// is what catches a lost or doubled bias.
        /// </summary>
        [Test]
        public void DecodedAnglesSpanExactlyOneTurn()
        {
            Assert.That(FacingCodec.TryToRadians(1u, out float first), Is.True);
            Assert.That(FacingCodec.TryToRadians((uint)FacingCodec.BradSteps, out float last), Is.True);

            Assert.That(first, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(last, Is.LessThan(6.28318531f),
                "the last step must stay below a full turn, or it aliases onto the first");
            Assert.That(last, Is.GreaterThan(6.28f), "the range must cover very nearly a full turn");

            // Quarter turn.
            uint quarter = (uint)(FacingCodec.BradSteps / 4) + 1;
            Assert.That(FacingCodec.TryToRadians(quarter, out float q), Is.True);
            Assert.That(q, Is.EqualTo(1.57079633f).Within(0.0001f));
        }

        /// <summary>
        /// The Unity conversion. The server's angle is counter-clockwise from +X in a
        /// right-handed 2D plane; Unity's Y rotation is clockwise from +Z. Getting this
        /// wrong produces a mirrored or quarter-turned world that still animates
        /// smoothly, which is exactly the kind of bug that survives a casual look.
        /// </summary>
        [Test]
        public void UnityYawMapsServerEastToUnityPlusX()
        {
            // Server east (+X, 0 rad) -> Unity yaw 90 (facing +X).
            Assert.That(FacingCodec.TryToUnityYaw(1u, out float east), Is.True);
            Assert.That(east, Is.EqualTo(90f).Within(0.001f));

            // Server north (+Y, PI/2) -> Unity yaw 0 (facing +Z).
            uint north = (uint)(FacingCodec.BradSteps / 4) + 1;
            Assert.That(FacingCodec.TryToUnityYaw(north, out float n), Is.True);
            Assert.That(n, Is.EqualTo(0f).Within(0.01f));
        }

        /// <summary>A not-sent facing must produce no yaw, so a view holds its last one.</summary>
        [Test]
        public void NotSentProducesNoYaw()
        {
            Assert.That(FacingCodec.TryToUnityYaw(FacingCodec.NotSent, out _), Is.False);
        }

        /// <summary>
        /// Idle is 1, not 0, so "standing still" and "no action reported" stay distinct.
        /// These values are on the wire and are frozen.
        /// </summary>
        [Test]
        public void ActionZeroIsReservedAndIdleIsOne()
        {
            Assert.That((int)SimAction.Unspecified, Is.EqualTo(0));
            Assert.That((int)SimAction.Idle, Is.EqualTo(1));
            Assert.That((int)SimAction.Moving, Is.EqualTo(2));
            Assert.That((int)SimAction.Attacking, Is.EqualTo(3));
            Assert.That((int)SimAction.Dead, Is.EqualTo(4));
        }

        // ---- The resolver ---------------------------------------------------

        [Test]
        public void ResolverCarriesFacingAndActionThrough()
        {
            var resolver = new SnapshotResolver();
            uint north = (uint)(FacingCodec.BradSteps / 4) + 1;

            Assert.That(resolver.TryResolve(
                Snapshot(true, new EntitySnapshot
                {
                    Id = "e1", Type = "player", X = 1f, Y = 2f, Hp = 10, MaxHp = 10,
                    Speed = 6.25f, FacingBrad = north, Action = SimAction.Moving,
                }),
                out var resolved), Is.True);

            Assert.That(resolved.Entities[0].FacingBrad, Is.EqualTo(north));
            Assert.That(resolved.Entities[0].Action, Is.EqualTo(SimAction.Moving));
        }

        /// <summary>
        /// Both ride handle-only mentions. The server writes them on every mention
        /// precisely so a delta is complete; dropping them here would make facing correct
        /// once per keyframe interval and stale in between — an entity visibly turning
        /// only twice a second.
        /// </summary>
        [Test]
        public void FieldsSurviveHandleOnlyResolution()
        {
            var resolver = new SnapshotResolver();
            uint east = 1u;
            uint north = (uint)(FacingCodec.BradSteps / 4) + 1;

            Assert.That(resolver.TryResolve(
                Snapshot(true, new EntitySnapshot
                {
                    Id = "e1", Handle = 1, Type = "player",
                    FacingBrad = east, Action = SimAction.Idle,
                }),
                out _), Is.True);

            Assert.That(resolver.TryResolve(
                Snapshot(false, new EntitySnapshot
                {
                    Id = "", Handle = 1, Type = "player",
                    FacingBrad = north, Action = SimAction.Attacking,
                }),
                out var delta), Is.True);

            Assert.That(delta.Entities[0].Id, Is.EqualTo("e1"), "handle must still resolve");
            Assert.That(delta.Entities[0].FacingBrad, Is.EqualTo(north));
            Assert.That(delta.Entities[0].Action, Is.EqualTo(SimAction.Attacking));
        }

        /// <summary>
        /// A server predating these fields sends nothing, which decodes as zero. That
        /// must STAY zero rather than becoming a fabricated east or a fabricated idle
        /// here — whether to hold the last value or derive one is the view layer's
        /// decision, made where the context is, not the resolver's.
        /// </summary>
        [Test]
        public void AbsentFieldsResolveToNotSentRatherThanAGuess()
        {
            var resolver = new SnapshotResolver();

            Assert.That(resolver.TryResolve(
                Snapshot(true, new EntitySnapshot { Id = "e1", Type = "player" }),
                out var resolved), Is.True);

            Assert.That(resolved.Entities[0].FacingBrad, Is.EqualTo(FacingCodec.NotSent));
            Assert.That(resolved.Entities[0].Action, Is.EqualTo(SimAction.Unspecified),
                "must be Unspecified, NOT Idle — an old server would otherwise freeze every entity into an idle pose");
            Assert.That(FacingCodec.TryToRadians(resolved.Entities[0].FacingBrad, out _), Is.False);
        }

        // ---- The view seam --------------------------------------------------

        /// <summary>A view that implements only <see cref="IEntityView"/>.</summary>
        private sealed class PoselessView : IEntityView
        {
            public int SetStateCalls;
            public void Spawn(string id, bool isLocal, string type) { }
            public void Despawn(string id) { }
            public void SetState(string id, float x, float y, int hp, int maxHp) => SetStateCalls++;
        }

        /// <summary>A view that opts in to facing and action.</summary>
        private sealed class PoseView : IEntityView, IEntityPoseView
        {
            public int SetStateCalls;
            public uint LastFacingBrad;
            public SimAction LastAction;

            public void Spawn(string id, bool isLocal, string type) { }
            public void Despawn(string id) { }
            public void SetState(string id, float x, float y, int hp, int maxHp) => SetStateCalls++;

            public void SetPose(string id, uint facingBrad, SimAction action)
            {
                LastFacingBrad = facingBrad;
                LastAction = action;
            }
        }

        private static WorldState WorldWith(uint facingBrad, SimAction action)
        {
            var world = new WorldState();
            var resolver = new SnapshotResolver();
            Assert.That(resolver.TryResolve(
                Snapshot(true, new EntitySnapshot
                {
                    Id = "e1", Type = "player", X = 1f, Y = 2f, Hp = 10, MaxHp = 10,
                    Speed = 4f, FacingBrad = facingBrad, Action = action,
                }),
                out var resolved), Is.True);
            world.Apply(resolved);
            return world;
        }

        /// <summary>
        /// The compatibility guarantee, and the reason <see cref="IEntityPoseView"/> is a
        /// separate interface at all: a view that does not implement it keeps working
        /// untouched — no stub, no assembly reference, no recompile.
        /// </summary>
        /// <remarks>
        /// This is the case that was NOT covered when facing and action were first added
        /// by widening <c>IEntityView.SetState</c>. That compiled here and broke
        /// <c>com.cuvara.dots</c>, a separate package in a separate repository, which
        /// could not even name <see cref="SimAction"/> from its assembly. Nothing in this
        /// repository could see it. This test is the thing that would have.
        /// </remarks>
        [Test]
        public void AViewWithoutThePoseInterfaceStillWorks()
        {
            var view = new PoselessView();
            var binder = new WorldViewBinder(view);

            Assert.DoesNotThrow(() => binder.Tick(WorldWith(1u, SimAction.Moving), localId: null));
            Assert.That(view.SetStateCalls, Is.GreaterThan(0),
                "an opted-out view must still receive position and hp");
        }

        /// <summary>A view that opts in receives the facing and action alongside SetState.</summary>
        [Test]
        public void APoseViewReceivesFacingAndAction()
        {
            var view = new PoseView();
            var binder = new WorldViewBinder(view);
            uint north = (uint)(FacingCodec.BradSteps / 4) + 1;

            binder.Tick(WorldWith(north, SimAction.Attacking), localId: null);

            Assert.That(view.SetStateCalls, Is.GreaterThan(0));
            Assert.That(view.LastFacingBrad, Is.EqualTo(north));
            Assert.That(view.LastAction, Is.EqualTo(SimAction.Attacking));
        }

        /// <summary>
        /// A not-sent facing still reaches the view as zero rather than being filtered
        /// out by the binder. Holding the last value is the VIEW's decision — the binder
        /// suppressing the call would take that choice away and make "not sent"
        /// indistinguishable from "no update at all".
        /// </summary>
        [Test]
        public void ANotSentFacingIsStillDelivered()
        {
            var view = new PoseView { LastFacingBrad = 12345u };
            var binder = new WorldViewBinder(view);

            binder.Tick(WorldWith(FacingCodec.NotSent, SimAction.Unspecified), localId: null);

            Assert.That(view.LastFacingBrad, Is.EqualTo(FacingCodec.NotSent));
            Assert.That(view.LastAction, Is.EqualTo(SimAction.Unspecified));
        }

        // ---- The codecs -----------------------------------------------------

        private static byte[] Inbound(MsgType type, string payloadJson) =>
            System.Text.Encoding.UTF8.GetBytes(
                "{\"type\":" + (int)type + ",\"payload\":" + payloadJson + "}");

        /// <summary>
        /// The JSON decoder reads the exact snake_case keys the servers write. A typo
        /// here is a field that silently never arrives.
        /// </summary>
        [Test]
        public void JsonDecodesBothFields()
        {
            var codec = new JsonWireCodec();
            var frame = codec.DecodeBody(Inbound(MsgType.Snapshot,
                "{\"tick\":1,\"full\":true,\"entities\":[" +
                "{\"id\":\"e1\",\"type\":\"player\",\"x\":1,\"y\":2,\"hp\":10,\"max_hp\":10," +
                "\"speed\":4,\"facing_brad\":16385,\"action\":3}]}"));

            var snapshot = (SnapshotMessage)frame.Payload;
            Assert.That(snapshot.Entities[0].FacingBrad, Is.EqualTo(16385u));
            Assert.That(snapshot.Entities[0].Action, Is.EqualTo(SimAction.Attacking));
        }

        /// <summary>
        /// Both encodings must spell "not sent" the same way: absence. This is the one
        /// place JSON does not write a zero the way it does for speed, because zero here
        /// is a reserved value rather than a legitimate reading.
        /// </summary>
        [Test]
        public void JsonOmittingBothFieldsDecodesAsNotSent()
        {
            var codec = new JsonWireCodec();
            var frame = codec.DecodeBody(Inbound(MsgType.Snapshot,
                "{\"tick\":1,\"full\":true,\"entities\":[" +
                "{\"id\":\"e1\",\"type\":\"player\",\"x\":1,\"y\":2,\"hp\":10,\"max_hp\":10,\"speed\":4}]}"));

            var snapshot = (SnapshotMessage)frame.Payload;
            Assert.That(snapshot.Entities[0].FacingBrad, Is.EqualTo(FacingCodec.NotSent));
            Assert.That(snapshot.Entities[0].Action, Is.EqualTo(SimAction.Unspecified));
        }
    }
}
