using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using Cuvara.Netcode.World;
using Shared.GameLogic.Components;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// One second of real time must advance the predictor by one second, no matter how
    /// many things are driving it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this exists for.</b> Two drivers share the predictor's clock:
    /// <see cref="WorldViewBinder.AdvanceFrame"/> once per rendered frame, and the
    /// snapshot pass, which covers the case of a harness that pumps snapshots with no
    /// frame loop at all. The snapshot pass advanced the <i>whole</i> wall-clock gap
    /// since the previous snapshot while the frame loop had already advanced that same
    /// span in slices, so the predictor's clock ran at about <b>twice real time</b>.
    /// </para>
    /// <para>
    /// <b>Why that is a stutter and not a speed-up.</b> The predicted position is still
    /// pinned to the server's every snapshot, so the avatar does not run away — the
    /// doubling is spent on base ticks instead. The server holds a direction for
    /// <c>WorldEvery</c> base ticks and stops the entity when that expires; at a doubled
    /// tick rate the client's copy of that window expired in half the real time, so the
    /// avatar moved for the first half of every send period and stood still for the
    /// second. Frame rate is irrelevant to it — capping to 60 changed nothing — and it
    /// lands on the local avatar alone, because remote entities are driven by the
    /// interpolator's separate clock. "Only the player I control stutters" is the exact
    /// signature.
    /// </para>
    /// <para>
    /// <b>What is asserted.</b> <see cref="LocalMovePredictor.ObservedInputInterval"/>,
    /// which is measured in the predictor's own clock, against inputs fed at a known
    /// real cadence. It reads the doubling directly: the live build reported 0.138s for
    /// inputs sent every 0.067s. Travel distance would not catch this — reconciliation
    /// keeps the position honest while the clock is wrong.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class PredictorClockAdvancesOnceTests
    {
        private sealed class NullView : IEntityView
        {
            public void Spawn(string id, bool isLocal, string type) { }
            public void Despawn(string id) { }
            public void SetState(string id, float x, float y, int hp, int maxHp) { }
        }

        private const string LocalId = "local-user";

        /// <summary>Real seconds between the inputs this test feeds.</summary>
        private const float InputPeriod = 0.060f;

        /// <summary>Frame slice, chosen so several land inside one input period.</summary>
        private const float FrameSlice = 0.010f;

        private const int SlicesPerInput = 6;   // 6 x 10ms == one 60ms input period
        private const int InputCount = 6;

        private static ResolvedSnapshot Keyframe(long tick, params ResolvedEntity[] entities) =>
            new ResolvedSnapshot(tick, tick, true, entities, new string[0]);

        [Test]
        public void FramesAndSnapshotsTogetherAdvanceTheClockOnlyOnce()
        {
            var predictor = new LocalMovePredictor(new PredictionSettings(
                tickRate: 60, speed: 5f, bounds: MapBounds.Default));

            var binder = new WorldViewBinder(new NullView(), predictor);
            var world = new WorldState();

            // Seed, so the binder has a local entity and prediction is live.
            world.Apply(Keyframe(1, new ResolvedEntity(LocalId, "player", 0f, 0f, 100, 100)));
            binder.Tick(world, LocalId);

            long inputTick = 0;

            for (int i = 0; i < InputCount; i++)
            {
                predictor.RecordInput(++inputTick, 1f, 0f);

                // A frame loop and a snapshot stream running at the same time, which is
                // the ordinary case and the one that broke: each slice sleeps for real,
                // so the binder's stopwatch sees the same span the slices report.
                for (int s = 0; s < SlicesPerInput; s++)
                {
                    Thread.Sleep((int)(FrameSlice * 1000f));

                    // Tick every frame, not once per arriving snapshot. That is what a
                    // real client does — the world state is re-read each frame whether or
                    // not a snapshot landed — and it is the pattern under which the two
                    // drivers double-counted every frame.
                    world.Apply(Keyframe(2 + (i * SlicesPerInput) + s,
                        new ResolvedEntity(LocalId, "player", predictor.Position.X, predictor.Position.Y, 100, 100)));
                    binder.Tick(world, LocalId);

                    binder.AdvanceFrame(FrameSlice);
                }
            }

            float observed = predictor.ObservedInputInterval;

            // Generous upper bound: Thread.Sleep overshoots, and the snapshot pass
            // legitimately adds that overshoot as time no frame accounted for. What it
            // must not do is add the whole period a second time.
            Assert.That(observed, Is.GreaterThan(InputPeriod * 0.6f).And.LessThan(InputPeriod * 1.5f),
                $"inputs were fed every {InputPeriod:F3}s of real time but the predictor " +
                $"measured {observed:F4}s between them. Its clock is not running at real " +
                "time, so base ticks accrue at the wrong rate and the server's hold " +
                "window expires early — the local avatar stutters at every frame rate " +
                "while remotes stay smooth.");
        }

        /// <summary>
        /// The complement: with no frame loop at all, the snapshot pass must still carry
        /// the clock. Otherwise this fix would be "delete the snapshot advance", which
        /// freezes prediction in any harness that only pumps snapshots.
        /// </summary>
        [Test]
        public void SnapshotsAloneStillAdvanceTheClock()
        {
            var predictor = new LocalMovePredictor(new PredictionSettings(
                tickRate: 60, speed: 5f, bounds: MapBounds.Default));

            var binder = new WorldViewBinder(new NullView(), predictor);
            var world = new WorldState();

            world.Apply(Keyframe(1, new ResolvedEntity(LocalId, "player", 0f, 0f, 100, 100)));
            binder.Tick(world, LocalId);

            long inputTick = 0;

            for (int i = 0; i < InputCount; i++)
            {
                predictor.RecordInput(++inputTick, 1f, 0f);

                Thread.Sleep((int)(InputPeriod * 1000f));

                world.Apply(Keyframe(2 + i,
                    new ResolvedEntity(LocalId, "player", predictor.Position.X, predictor.Position.Y, 100, 100)));
                binder.Tick(world, LocalId);
            }

            float observed = predictor.ObservedInputInterval;

            Assert.That(observed, Is.GreaterThan(InputPeriod * 0.5f),
                $"with snapshots as the only driver the predictor measured {observed:F4}s " +
                $"between inputs fed every {InputPeriod:F3}s. Its clock is not advancing, " +
                "so prediction is frozen wherever nothing renders frames.");
        }
    }
}
