using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// A predictor registered before the join, at a tick rate that is not the server's, and
    /// <see cref="LocalMovePredictor.UseServerTickRate"/>, which adopts the advertised rate once the
    /// join reply has it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The live defect (rpg-factory testlab, run 20261022T042952Z-S09-h87z).</b> The game registers
    /// its one <see cref="LocalMovePredictor"/> at the composition root, before any join, with
    /// <c>GameConstants.DefaultTickRate</c> -- 15, the old world rate -- while the server simulates and
    /// stamps every tick it sends (snapshot <c>tick</c>, <c>ack_applied_tick</c>) at 60 Hz. The
    /// predictor's timestep was fixed at construction, so nothing could put it right after the join.
    /// A 15 Hz base clock steered onto a 60 Hz tick line falls 45 ticks a second behind, so
    /// <see cref="LocalMovePredictor.SteerToServerTick"/> hard-resynchronised about once a second
    /// (dropping the pending inputs and the hold), nearly every reconcile missed the history, and every
    /// correction was a whole 15 Hz step: 0.333 units, i.e. exactly four 60 Hz steps -- the steady
    /// <c>LastCorrection == 0.333</c> of the run. Under a late burst the same mismatch produced
    /// single-frame jumps of the rendered local player of 1.3 to 1.7 units (live: 1.64).
    /// </para>
    /// <para>
    /// The replay of the live input and snapshot timings through a 15 Hz predictor reproduced the
    /// live trace to the millimetre (S03: corrections 0.500, 0.333, 0.333, 0.667 at the same
    /// snapshots, rendered 0.169 vs live 0.169); the same replay at 60 Hz corrected nothing of the
    /// kind. The scenario here is the headless equivalent: a 60 Hz server, snapshots every four
    /// ticks, 13 Hz sends (InputCadence.RecommendedSendHz(15), what the game sends at), a 12 ms round
    /// trip, and two snapshots held back 160 ms and then delivered in a burst.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class PredictorTickRateTests
    {
        private const int ServerHz = 60;
        private const int WorldRateTheGameRegisters = 15; // GameConstants.DefaultTickRate in sgl-v0.7.0

        private static PredictionHarness.Config Scenario(int constructedHz, uint advertisedHz)
        {
            var c = PredictionHarness.Config.Default;
            c.ServerHz = ServerHz;
            c.SnapshotEvery = 4;
            c.SendHz = 13.0;
            c.SendJitterSeconds = 0.002;
            c.UplinkSeconds = 0.006;
            c.DownlinkSeconds = 0.006;
            c.NetworkJitterSeconds = 0.002;
            c.FrameSeconds = 0.0076;
            c.DurationSeconds = 30.0;
            c.Mode = PredictionHarness.ClientMode.MeasuredSteering;
            c.Motion = PredictionHarness.Motion.Circle;
            c.InOrderDownlink = true;
            c.BurstAtSeconds = 20.0;
            c.BurstSnapshots = 2;
            c.BurstDelaySeconds = 0.160;
            c.PredictorTickRate = constructedHz;
            c.AdvertisedTickRate = advertisedHz;
            return c;
        }

        private static PredictionHarness.Result Run(PredictionHarness.Config c, string label)
        {
            PredictionHarness.Result r = PredictionHarness.Run(c);
            TestContext.WriteLine($"{label}: {r}");
            Assume.That(r.Reconciles, Is.GreaterThan(300), "precondition: the run reconciled");
            Assume.That(r.MovingFrames, Is.GreaterThan(1000), "precondition: frames were measured");
            return r;
        }

        // ── End to end ──

        [Test]
        public void Control_PredictorLeftAtTheWorldRate_ReproducesTheLiveDefect()
        {
            // No adoption: what the game ran. Pinned so the fix below is measured against the
            // real failure, not against a harness that cannot fail.
            PredictionHarness.Result r = Run(Scenario(WorldRateTheGameRegisters, 0u), "15 Hz predictor");

            Assert.That(r.HardResyncs, Is.GreaterThan(5), r.ToString());
            Assert.That(r.CorrectionsAtLeastFourSteps * 4, Is.GreaterThan(r.Reconciles),
                "more than a quarter of all reconciles correct by a whole 15 Hz step (four 60 Hz steps). " + r);
            // Measured: 0.345 units in one 7.6 ms frame, 9x the normal per-frame motion (live: 1.64).
            Assert.That(r.MaxFrameMotionRatio, Is.GreaterThan(5.0), r.ToString());
        }

        [Test]
        public void AdoptedServerRate_SteadyMotion_HasNoWholeStepCorrections()
        {
            PredictionHarness.Result r =
                Run(Scenario(WorldRateTheGameRegisters, ServerHz), "15 Hz predictor, adopted 60");

            Assert.That(r.HardResyncs, Is.Zero, r.ToString());
            Assert.That(r.CorrectionsAtLeastFourSteps, Is.Zero,
                "a correction of four 60 Hz steps (0.333) on a steady curve is the rate mismatch. " + r);
            Assert.That(r.MaxCorrection, Is.LessThanOrEqualTo(r.StepLength * 2.01f), r.ToString());
            Assert.That(r.MeanCorrection, Is.LessThan(0.003), r.ToString());
            Assert.That(r.Snaps, Is.Zero, r.ToString());
        }

        [Test]
        public void AdoptedServerRate_LateBurst_DoesNotJumpTheRenderedPlayer()
        {
            PredictionHarness.Result r =
                Run(Scenario(WorldRateTheGameRegisters, ServerHz), "15 Hz predictor, adopted 60");

            // Normal motion is 5 u/s x 7.6 ms = 0.038 per frame. The live defect drew 1.64 in one.
            Assert.That(r.MaxFrameStep, Is.LessThan(0.1),
                "two snapshots 160 ms late and then a burst must not move the local player much " +
                $"further in one frame than it moves in any other. {r}");
            Assert.That(r.MaxFrameMotionRatio, Is.LessThanOrEqualTo(2.0), r.ToString());
        }

        [Test]
        public void AdoptedServerRate_PredictsExactlyLikeAPredictorBuiltAtIt()
        {
            // The adoption is a replacement, not an approximation: the same seeded run through a
            // predictor built at 60 Hz and one built at 15 and told 60 must agree to the bit.
            PredictionHarness.Result built = Run(Scenario(ServerHz, 0u), "built at 60");
            PredictionHarness.Result adopted =
                Run(Scenario(WorldRateTheGameRegisters, ServerHz), "built at 15, adopted 60");

            Assert.That(adopted.ToString(), Is.EqualTo(built.ToString()));
        }

        // ── The method itself ──

        private static LocalMovePredictor NewPredictor(int hz) =>
            new LocalMovePredictor(new PredictionSettings(hz, 5f, MapBounds.Default));

        [Test]
        public void UseServerTickRate_ReplacesTheTimestepAndEverythingDerivedFromIt()
        {
            LocalMovePredictor p = NewPredictor(WorldRateTheGameRegisters);
            LocalMovePredictor reference = NewPredictor(ServerHz);

            p.UseServerTickRate(ServerHz);

            Assert.That(p.TickRateHz, Is.EqualTo(ServerHz));
            Assert.That(p.IntegrationTimestep, Is.EqualTo(MovementSystem.DeltaTimeForTickRate(ServerHz)));
            Assert.That(p.IntegrationTimestep, Is.EqualTo(reference.IntegrationTimestep));
            Assert.That(p.MaxBankedTicks, Is.EqualTo(reference.MaxBankedTicks));
            Assert.That(p.MaxCatchUpTicks, Is.EqualTo(reference.MaxCatchUpTicks));
            Assert.That(p.TickRateIsFallback, Is.False);
            Assert.That(p.IsEnabled, Is.True);
        }

        [Test]
        public void UseServerTickRate_ChangingTheRate_ResetsThePrediction()
        {
            // State predicted on a 15 Hz timestep, against base ticks seeded in the wrong space, is
            // not a valid starting point for a 60 Hz one -- the same rule UseServerProtocol follows.
            LocalMovePredictor p = NewPredictor(WorldRateTheGameRegisters);
            p.Reconcile(new Vec2(1f, 2f), 0L);
            p.SeedBaseTick(5000);
            p.RecordInput(1, 1f, 0f);
            p.Advance(0.2f);
            p.Reconcile(new Vec2(1f, 2f), 0L);
            Assume.That(p.PendingCount, Is.EqualTo(1));
            Assume.That(p.Reconciles, Is.EqualTo(1));

            p.UseServerTickRate(ServerHz);

            Assert.That(p.PendingCount, Is.Zero);
            Assert.That(p.Reconciles, Is.Zero);
            Assert.That(p.BaseTick, Is.EqualTo(1));

            // And it seeds again from the next snapshot, on the server's tick line.
            p.SeedBaseTick(7000);
            Assert.That(p.BaseTick, Is.EqualTo(7000));
        }

        [Test]
        public void UseServerTickRate_TheSameRate_ChangesNothing()
        {
            // Safe to call after every join, including a reconnect to the same server.
            LocalMovePredictor p = NewPredictor(ServerHz);
            p.Reconcile(new Vec2(1f, 2f), 0L);
            p.SeedBaseTick(5000);
            p.RecordInput(1, 1f, 0f);
            p.Advance(0.05f);
            Vec2 before = p.SimulatedPosition;
            long baseTick = p.BaseTick;

            p.UseServerTickRate(ServerHz);

            Assert.That(p.PendingCount, Is.EqualTo(1));
            Assert.That(p.BaseTick, Is.EqualTo(baseTick));
            Assert.That(p.SimulatedPosition.X, Is.EqualTo(before.X));
            Assert.That(p.SimulatedPosition.Y, Is.EqualTo(before.Y));
        }

        [Test]
        public void UseServerTickRate_Zero_MeansNotSentAndKeepsTheConstructedRate()
        {
            LocalMovePredictor p = NewPredictor(WorldRateTheGameRegisters);

            p.UseServerTickRate(0u);

            Assert.That(p.TickRateHz, Is.EqualTo(WorldRateTheGameRegisters));
            Assert.That(p.IntegrationTimestep,
                Is.EqualTo(MovementSystem.DeltaTimeForTickRate(WorldRateTheGameRegisters)));
        }

        [Test]
        public void ClockSteering_NamesTheMismatch_UntilTheServerRateIsAdopted()
        {
            LocalMovePredictor p = NewPredictor(WorldRateTheGameRegisters);
            p.Reconcile(Vec2.Zero, 0L);
            var steering = new PredictionClockSteering(p);

            long tick = 100_000;
            double now = 0.0;
            for (int i = 0; i < 40; i++, tick += 4, now += 4.0 / ServerHz)
            {
                steering.SampleTickRate(tick, now);
                steering.OnSnapshot(p, tick, 0L, now);
            }

            Assert.That(steering.PredictorRateDisagrees, Is.True,
                "a 15 Hz predictor against a 60 Hz tick stream must be legible as exactly that");

            p.UseServerTickRate(ServerHz);
            for (int i = 0; i < 4; i++, tick += 4, now += 4.0 / ServerHz)
            {
                steering.SampleTickRate(tick, now);
                steering.OnSnapshot(p, tick, 0L, now);
            }

            Assert.That(steering.PredictorRateDisagrees, Is.False);
        }

        [Test]
        public void UseServerTickRate_EnablesAPredictorBuiltWithNoRate()
        {
            // A composition root with no rate at all builds a refusing predictor; the advertised
            // rate is exactly what it was missing.
            LocalMovePredictor p = NewPredictor(0);
            Assume.That(p.IsEnabled, Is.False);

            p.UseServerTickRate(ServerHz);

            Assert.That(p.IsEnabled, Is.True);
            Assert.That(p.IntegrationTimestep, Is.EqualTo(MovementSystem.DeltaTimeForTickRate(ServerHz)));
        }
    }
}
