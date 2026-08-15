using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using Cuvara.Netcode.World;
using Shared.GameLogic.Components;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Covers <see cref="WorldViewBinder"/>'s reconcile pass: what it spawns, what it
    /// hands a view, and — the part with a latency cost attached — which entities it
    /// interpolates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How the interpolation assertions avoid depending on wall-clock time.</b> The
    /// binder derives its interpolation factor from time elapsed since the last
    /// snapshot arrived. Immediately after a snapshot that factor is ~0, which means an
    /// interpolated entity renders the <i>previous</i> snapshot's position while a
    /// snapped one renders the new one. Asserting at that instant separates the two
    /// paths by a whole snapshot interval, so the test does not race the clock: it would
    /// take a ~66 ms stall between <c>Apply</c> and <c>Tick</c> to blur them, and the
    /// assertions use ranges rather than exact interpolated values so ordinary jitter
    /// cannot flip them.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class WorldViewBinderTests
    {
        private sealed class RecordingView : IEntityView
        {
            public readonly Dictionary<string, float[]> Positions = new Dictionary<string, float[]>();
            public readonly Dictionary<string, string> Types = new Dictionary<string, string>();
            public readonly List<string> SpawnedLocal = new List<string>();
            public readonly List<string> Despawned = new List<string>();

            public void Spawn(string id, bool isLocal, string type)
            {
                Types[id] = type;
                if (isLocal)
                {
                    SpawnedLocal.Add(id);
                }
            }

            public void Despawn(string id)
            {
                Despawned.Add(id);
                Positions.Remove(id);
            }

            public readonly Dictionary<string, int> Hp = new Dictionary<string, int>();

            public int SetStateCalls { get; private set; }

            public void SetState(string id, float x, float y, int hp, int maxHp)
            {
                SetStateCalls++;
                Positions[id] = new[] { x, y };
                Hp[id] = hp;
            }
        }

        private const string LocalId = "local-user";
        private const string RemoteId = "remote-user";

        private static ResolvedSnapshot Keyframe(long tick, params ResolvedEntity[] entities) =>
            new ResolvedSnapshot(tick, 0L, true, entities, new string[0]);

        private static ResolvedEntity Entity(string id, float x, float y, string type = "player") =>
            new ResolvedEntity(id, type, x, y, 100, 100);

        [Test]
        public void LocalEntitySnapsToTheAuthoritativePosition()
        {
            var view = new RecordingView();
            var binder = new WorldViewBinder(view);
            var world = new WorldState();

            world.Apply(Keyframe(1, Entity(LocalId, 0f, 0f)));
            binder.Tick(world, LocalId);

            world.Apply(Keyframe(2, Entity(LocalId, 5f, 7f)));
            binder.Tick(world, LocalId);

            Assert.That(view.Positions[LocalId], Is.EqualTo(new[] { 5f, 7f }),
                "the local entity must be placed at the position the server sent, not " +
                "eased towards it from where it used to be");
        }

        [Test]
        public void RemoteEntityIsStillInterpolated()
        {
            var view = new RecordingView();
            var binder = new WorldViewBinder(view);
            var world = new WorldState();

            world.Apply(Keyframe(1, Entity(RemoteId, 0f, 0f)));
            binder.Tick(world, LocalId);

            world.Apply(Keyframe(2, Entity(RemoteId, 5f, 7f)));
            binder.Tick(world, LocalId);

            var p = view.Positions[RemoteId];
            Assert.That(p[0], Is.LessThan(1f),
                "a remote entity must still start its interval near the previous snapshot");
            Assert.That(p[1], Is.LessThan(1f),
                "a remote entity must still start its interval near the previous snapshot");
        }

        [Test]
        public void LocalAndRemoteAreTreatedDifferentlyInTheSamePass()
        {
            var view = new RecordingView();
            var binder = new WorldViewBinder(view);
            var world = new WorldState();

            world.Apply(Keyframe(1, Entity(LocalId, 0f, 0f), Entity(RemoteId, 0f, 0f)));
            binder.Tick(world, LocalId);

            world.Apply(Keyframe(2, Entity(LocalId, 5f, 7f), Entity(RemoteId, 5f, 7f)));
            binder.Tick(world, LocalId);

            Assert.That(view.Positions[LocalId], Is.EqualTo(new[] { 5f, 7f }));
            Assert.That(view.Positions[RemoteId][0], Is.LessThan(1f));
        }

        [Test]
        public void EmptyLocalIdLeavesEveryEntityInterpolated()
        {
            var view = new RecordingView();
            var binder = new WorldViewBinder(view);
            var world = new WorldState();

            world.Apply(Keyframe(1, Entity(LocalId, 0f, 0f)));
            binder.Tick(world, string.Empty);

            world.Apply(Keyframe(2, Entity(LocalId, 5f, 7f)));
            binder.Tick(world, string.Empty);

            Assert.That(view.SpawnedLocal, Is.Empty,
                "nothing is the local player when the caller does not know its id yet");
            Assert.That(view.Positions[LocalId][0], Is.LessThan(1f));
        }

        [Test]
        public void ExactlyOneEntityIsSpawnedAsLocal()
        {
            var view = new RecordingView();
            var binder = new WorldViewBinder(view);
            var world = new WorldState();

            world.Apply(Keyframe(1,
                Entity(LocalId, 0f, 0f),
                Entity(RemoteId, 1f, 1f),
                Entity("mob-1", 2f, 2f, "mob")));
            binder.Tick(world, LocalId);

            Assert.That(view.SpawnedLocal, Is.EqualTo(new[] { LocalId }));
        }

        [Test]
        public void SpawnCarriesTheServersEntityType()
        {
            var view = new RecordingView();
            var binder = new WorldViewBinder(view);
            var world = new WorldState();

            world.Apply(Keyframe(1,
                Entity(LocalId, 0f, 0f),
                Entity("mob-1", 2f, 2f, "mob")));
            binder.Tick(world, LocalId);

            Assert.That(view.Types[LocalId], Is.EqualTo("player"));
            Assert.That(view.Types["mob-1"], Is.EqualTo("mob"));
        }

        [Test]
        public void SpawnPassesEmptyStringWhenTheServerSentNoType()
        {
            var view = new RecordingView();
            var binder = new WorldViewBinder(view);
            var world = new WorldState();

            world.Apply(Keyframe(1, new ResolvedEntity("x", null, 0f, 0f, 1, 1)));
            binder.Tick(world, LocalId);

            Assert.That(view.Types["x"], Is.EqualTo(string.Empty),
                "a view must never have to null-check the type it is handed");
        }

        /// <summary>
        /// The "★ YOU appears twice" regression: rejoin as a different user while the
        /// previous session's avatar is still in the world.
        /// </summary>
        /// <remarks>
        /// This is reachable in the DOTS sample because it mints a fresh Nakama device id
        /// (and therefore a fresh user id) on every join, while the server holds a
        /// disconnected player's entity for 30 s. Before the fix, the old id kept the
        /// <c>isLocal</c> it was spawned with and two entities claimed to be the player.
        /// </remarks>
        [Test]
        public void ChangingLocalIdMovesTheLocalFlagToTheNewEntity()
        {
            var view = new RecordingView();
            var binder = new WorldViewBinder(view);
            var world = new WorldState();

            const string oldSelf = "user-session-1";
            const string newSelf = "user-session-2";

            world.Apply(Keyframe(1, Entity(oldSelf, 1f, 1f)));
            binder.Tick(world, oldSelf);
            Assert.That(view.SpawnedLocal, Is.EqualTo(new[] { oldSelf }));

            // Rejoined as a different user; the previous avatar is still held by the
            // server and so is still in the snapshot.
            world.Apply(Keyframe(2, Entity(oldSelf, 1f, 1f), Entity(newSelf, 2f, 2f)));
            binder.Tick(world, newSelf);

            Assert.That(view.SpawnedLocal, Is.EqualTo(new[] { oldSelf, newSelf }),
                "the new id must be spawned as local");
            Assert.That(view.Despawned, Contains.Item(oldSelf),
                "the previous avatar must be dropped so it can come back as a remote " +
                "entity — otherwise it keeps presenting itself as the local player");
            Assert.That(binder.Relocalizations, Is.EqualTo(1));
            Assert.That(binder.DespawnsFromAbsence, Is.Zero,
                "a relocalization is not an entity leaving; counting it as one would " +
                "make the AOI-churn diagnostic lie");
        }

        [Test]
        public void RelocalizedEntityComesBackAndIsInterpolatedAsRemote()
        {
            var view = new RecordingView();
            var binder = new WorldViewBinder(view);
            var world = new WorldState();

            const string oldSelf = "user-session-1";
            const string newSelf = "user-session-2";

            world.Apply(Keyframe(1, Entity(oldSelf, 0f, 0f)));
            binder.Tick(world, oldSelf);

            world.Apply(Keyframe(2, Entity(oldSelf, 0f, 0f), Entity(newSelf, 0f, 0f)));
            binder.Tick(world, newSelf);

            world.Apply(Keyframe(3, Entity(oldSelf, 8f, 8f), Entity(newSelf, 8f, 8f)));
            binder.Tick(world, newSelf);

            Assert.That(view.Positions[newSelf], Is.EqualTo(new[] { 8f, 8f }),
                "the new local entity snaps");
            Assert.That(view.Positions[oldSelf][0], Is.LessThan(1f),
                "the demoted entity is interpolated like any other remote");
        }

        [Test]
        public void UnchangedLocalIdCausesNoChurn()
        {
            var view = new RecordingView();
            var binder = new WorldViewBinder(view);
            var world = new WorldState();

            for (long tick = 1; tick <= 5; tick++)
            {
                world.Apply(Keyframe(tick, Entity(LocalId, tick, tick), Entity(RemoteId, 0f, 0f)));
                binder.Tick(world, LocalId);
            }

            Assert.That(binder.Relocalizations, Is.Zero);
            Assert.That(view.Despawned, Is.Empty);
            Assert.That(view.SpawnedLocal, Is.EqualTo(new[] { LocalId }));
        }

        [Test]
        public void ResetClearsTheRememberedLocalId()
        {
            var view = new RecordingView();
            var binder = new WorldViewBinder(view);
            var world = new WorldState();

            world.Apply(Keyframe(1, Entity(LocalId, 0f, 0f)));
            binder.Tick(world, LocalId);
            binder.Reset();

            // A caller that resets on a session boundary never pays for a relocalization.
            world.Apply(Keyframe(2, Entity("someone-else", 0f, 0f)));
            binder.Tick(world, "someone-else");

            Assert.That(binder.Relocalizations, Is.Zero);
        }

        // ── Prediction wiring ──

        private static LocalMovePredictor UsablePredictor() =>
            new LocalMovePredictor(new PredictionSettings(15, 5f, MapBounds.Default));

        [Test]
        public void NoPredictorMeansTheLocalEntityFollowsTheNewestSnapshot()
        {
            var view = new RecordingView();
            var binder = new WorldViewBinder(view);

            Assert.That(binder.IsPredicting, Is.False);

            var world = new WorldState();
            world.Apply(Keyframe(1, Entity(LocalId, 0f, 0f)));
            binder.Tick(world, LocalId);
            world.Apply(Keyframe(2, Entity(LocalId, 5f, 7f)));
            binder.Tick(world, LocalId);

            Assert.That(view.Positions[LocalId], Is.EqualTo(new[] { 5f, 7f }));
        }

        [Test]
        public void ARefusingPredictorIsTreatedExactlyLikeNoPredictor()
        {
            var view = new RecordingView();
            // Speed 0 — unusable, so the predictor refuses.
            var refused = new LocalMovePredictor(new PredictionSettings(15, 0f, MapBounds.Default));
            var binder = new WorldViewBinder(view, refused);

            Assert.That(binder.IsPredicting, Is.False,
                "falling back to the previous behaviour is correct; predicting with " +
                "settings that cannot match the server is not");

            var world = new WorldState();
            world.Apply(Keyframe(1, Entity(LocalId, 0f, 0f)));
            binder.Tick(world, LocalId);
            world.Apply(Keyframe(2, Entity(LocalId, 5f, 7f)));
            binder.Tick(world, LocalId);

            Assert.That(view.Positions[LocalId], Is.EqualTo(new[] { 5f, 7f }));
        }

        [Test]
        public void PredictedInputMovesTheLocalEntityBeforeTheServerConfirmsIt()
        {
            var view = new RecordingView();
            var predictor = UsablePredictor();
            var binder = new WorldViewBinder(view, predictor);
            var world = new WorldState();

            Assert.That(binder.IsPredicting, Is.True);

            // Seed at the origin.
            world.Apply(Keyframe(1, Entity(LocalId, 0f, 0f)));
            binder.Tick(world, LocalId);
            Assert.That(view.Positions[LocalId], Is.EqualTo(new[] { 0f, 0f }));

            // An input is sent. No new snapshot — the server has not answered yet.
            predictor.RecordInput(1, 1f, 0f);
            binder.Tick(world, LocalId);

            Assert.That(view.Positions[LocalId][0], Is.GreaterThan(0f),
                "the whole point: the avatar moves on the input, not on the round trip");
        }

        [Test]
        public void RemoteEntitiesAreUnaffectedByPrediction()
        {
            var view = new RecordingView();
            var predictor = UsablePredictor();
            var binder = new WorldViewBinder(view, predictor);
            var world = new WorldState();

            world.Apply(Keyframe(1, Entity(LocalId, 0f, 0f), Entity(RemoteId, 0f, 0f)));
            binder.Tick(world, LocalId);
            world.Apply(Keyframe(2, Entity(LocalId, 1f, 1f), Entity(RemoteId, 5f, 7f)));
            binder.Tick(world, LocalId);

            Assert.That(view.Positions[RemoteId][0], Is.LessThan(1f),
                "remote entities keep their interpolation; prediction is local-only");
        }

        [Test]
        public void HpAlwaysComesFromTheServerEvenWhilePredicting()
        {
            var view = new RecordingView();
            var predictor = UsablePredictor();
            var binder = new WorldViewBinder(view, predictor);
            var world = new WorldState();

            world.Apply(Keyframe(1, new ResolvedEntity(LocalId, "player", 0f, 0f, 42, 100)));
            binder.Tick(world, LocalId);

            Assert.That(view.Hp[LocalId], Is.EqualTo(42),
                "only movement is predicted — combat has server-side rules the client " +
                "cannot reproduce, and a wrong HP prediction is worse than a late one");
        }

        [Test]
        public void ChangingLocalIdResetsPrediction()
        {
            var view = new RecordingView();
            var predictor = UsablePredictor();
            var binder = new WorldViewBinder(view, predictor);
            var world = new WorldState();

            world.Apply(Keyframe(1, Entity("user-1", 0f, 0f)));
            binder.Tick(world, "user-1");
            predictor.RecordInput(1, 1f, 0f);
            Assert.That(predictor.PendingCount, Is.EqualTo(1));

            world.Apply(Keyframe(2, Entity("user-1", 0f, 0f), Entity("user-2", 0f, 0f)));
            binder.Tick(world, "user-2");

            Assert.That(predictor.PendingCount, Is.Zero,
                "the buffered inputs belonged to the previous player; replaying them " +
                "would be prediction about the wrong avatar");
        }

        [Test]
        public void ResetClearsPredictionToo()
        {
            var view = new RecordingView();
            var predictor = UsablePredictor();
            var binder = new WorldViewBinder(view, predictor);
            var world = new WorldState();

            world.Apply(Keyframe(1, Entity(LocalId, 0f, 0f)));
            binder.Tick(world, LocalId);
            predictor.RecordInput(1, 1f, 0f);

            binder.Reset();

            Assert.That(predictor.PendingCount, Is.Zero);
        }

        [Test]
        public void LocalEntityLeavingAndReturningStillSnaps()
        {
            var view = new RecordingView();
            var binder = new WorldViewBinder(view);
            var world = new WorldState();

            world.Apply(Keyframe(1, Entity(LocalId, 0f, 0f)));
            binder.Tick(world, LocalId);

            world.Apply(Keyframe(2, Entity(RemoteId, 0f, 0f)));
            binder.Tick(world, LocalId);
            Assert.That(view.Despawned, Is.EqualTo(new[] { LocalId }));

            world.Apply(Keyframe(3, Entity(LocalId, 9f, 9f), Entity(RemoteId, 0f, 0f)));
            binder.Tick(world, LocalId);

            Assert.That(view.Positions[LocalId], Is.EqualTo(new[] { 9f, 9f }));
        }
        // ── The render pump ──

        /// <summary>
        /// The local entity must be re-rendered every frame, not once per snapshot.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Prediction used to be advanced, and the local entity re-rendered, only inside
        /// snapshot processing. The rendered position therefore changed at the world rate
        /// however fast the client drew, so every frame between snapshots showed the
        /// avatar still and the frame a snapshot landed on showed the whole interval at
        /// once. All the smoothing work was being computed and never sampled.
        /// </para>
        /// <para>
        /// Nothing caught it because every test here drives the binder by feeding it
        /// snapshots — the frame loop was not modelled at all, so a position that only
        /// moved on snapshots looked exactly like a correct one.
        /// </para>
        /// </remarks>
        [Test]
        public void AdvanceFrameMovesTheLocalEntityBetweenSnapshots()
        {
            var view = new RecordingView();
            var predictor = new LocalMovePredictor(
                new PredictionSettings(60, 5f, MapBounds.Default));
            predictor.SetHoldTicks(4);
            var binder = new WorldViewBinder(view, predictor);

            var world = new WorldState();
            world.Apply(Keyframe(1, Entity(LocalId, 0f, 0f)));
            binder.Tick(world, LocalId);

            predictor.RecordInput(1, 1f, 0f);

            int callsAfterSnapshot = view.SetStateCalls;

            // Frames pass; no snapshot arrives.
            for (var i = 0; i < 10; i++)
            {
                binder.AdvanceFrame(1f / 300f);
            }

            Assert.That(view.SetStateCalls, Is.GreaterThan(callsAfterSnapshot),
                "the local entity was not re-rendered between snapshots, so its position " +
                "can only change at the world rate however fast the client draws");
        }

        [Test]
        public void AdvanceFrameIsSafeWithoutAPredictorOrALocalEntity()
        {
            var view = new RecordingView();

            var bare = new WorldViewBinder(view);
            Assert.DoesNotThrow(() => bare.AdvanceFrame(1f / 60f),
                "a consumer without prediction must be able to call this unconditionally");

            var predicting = new WorldViewBinder(
                view, new LocalMovePredictor(new PredictionSettings(60, 5f, MapBounds.Default)));
            Assert.DoesNotThrow(() => predicting.AdvanceFrame(1f / 60f),
                "called before any snapshot has identified the local entity");
        }

    }
}
