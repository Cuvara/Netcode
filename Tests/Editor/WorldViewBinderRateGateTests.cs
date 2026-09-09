using System;
using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using Cuvara.Netcode.World;
using Shared.GameLogic.Components;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Pins the one line that decides whether a fitted rate is allowed to steer the base-tick
    /// clock: <c>Staleness.IsUsable &amp;&amp; Staleness.RateCorroborated</c> in
    /// <c>WorldViewBinder</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The estimator's own tests pin <see cref="SnapshotStalenessEstimator.RateCorroborated"/>.
    /// They cannot pin that the binder <b>reads</b> it, and until this file existed nothing did:
    /// deleting the second half of that condition left every test in the package green while
    /// restoring the defect in full.
    /// </para>
    /// <para>
    /// The defect: the envelope fit is a rate only if the minimum achievable delay was the same
    /// at both anchors. A starved frame loop raises that floor, the later anchor sits above the
    /// true line, and the slope absorbs the displacement as rate. One machine minutes apart read
    /// 220 ppm idle and 90 636 ppm inside a loaded suite, and the client ran its clock 8.3% slow
    /// on the strength of it.
    /// </para>
    /// <para>
    /// <b>What the server side actually says, stated carefully because the first version of this
    /// remark overstated it.</b> Sampled from outside the client during the same suite,
    /// <c>gameserver_achieved_tick_hz</c> holds at <b>59.99–60.02</b> and dips transiently — one
    /// sample at <b>54.23</b> — while <c>tick_backlog_dropped_total</c> climbs by <b>38</b> over
    /// the run. So the server is not running at 55 Hz sustained, and a constant 8.3% rate
    /// difference is rejected; but it is not flawless either, and an earlier claim here that it
    /// "read 60.013 Hz, so the server was fine" rested on a six-sample window that happened to be
    /// quiet. A window with no drops is not a run with no drops.
    /// </para>
    /// <para>
    /// <b>This test does not depend on which it is, and that is the point of gating on
    /// corroboration rather than on a diagnosis.</b> A transient dip and a fit artefact are both
    /// displacements that fail to reproduce over a doubled baseline; a genuinely slow server
    /// would reproduce, and would be believed. The guard sorts them without anyone having to be
    /// right about the cause.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class WorldViewBinderRateGateTests
    {
        private const int BaseHz = 60;
        private const int SnapshotEvery = 4;                       // 15 Hz on a 60 Hz base
        private const double Interval = SnapshotEvery / (double)BaseHz;
        private const string LocalId = "u1";

        /// <summary>A clock the test drives, so no test has to sleep to move time.</summary>
        private sealed class ManualClock : IViewClock
        {
            public double NowMs { get; set; }
        }

        private sealed class NullView : IEntityView
        {
            public void Spawn(string id, bool isLocal, string type) { }
            public void SetState(string id, float x, float y, int hp, int maxHp,
            uint facingBrad, Shared.GameLogic.Components.EntityAction action) { }
            public void Despawn(string id) { }
        }

        private static ResolvedSnapshot Keyframe(long tick) =>
            new ResolvedSnapshot(tick, tick, true,
                new[] { new ResolvedEntity(LocalId, "player", 0f, 0f, 100, 100) },
                new string[0]);

        /// <summary>
        /// Run a session where the client's clock advances at <paramref name="rate"/> server
        /// seconds per client second, optionally with the DELAY FLOOR stepping up partway —
        /// the shape a starved frame loop produces and the one the fit cannot see by itself.
        /// </summary>
        private static float DriveAndReadClockScale(
            double rate, double seconds, double floorStep = 0.0, double stepAt = 0.0)
        {
            var predictor = new LocalMovePredictor(new PredictionSettings(
                tickRate: BaseHz, speed: 5f, bounds: MapBounds.Default));
            var clock = new ManualClock { NowMs = 1000.0 };
            var binder = new WorldViewBinder(new NullView(), predictor, clock);
            var world = new WorldState();

            long tick = 1000;
            double t0 = 1.0 + tick / (double)BaseHz;
            double elapsed = 0;

            while (elapsed < seconds)
            {
                double floor = (stepAt > 0 && elapsed >= stepAt) ? floorStep : 0.0;
                clock.NowMs = (t0 + rate * elapsed + 0.002 + floor) * 1000.0;

                world.Apply(Keyframe(tick));
                binder.Tick(world, LocalId);

                tick += SnapshotEvery;
                elapsed += Interval;
            }

            return predictor.ClockRateScale;
        }

        [Test]
        public void ADelayFloorStepNeverReachesTheClock()
        {
            // Two clocks that genuinely agree, with a 300 ms delay floor arriving at 6 s. The
            // fit will read this as tens of thousands of ppm; none of it may be applied.
            float scale = DriveAndReadClockScale(
                rate: 1.0, seconds: 40.0, floorStep: 0.300, stepAt: 6.0);

            Assert.That(scale, Is.EqualTo(1f).Within(1e-4f),
                "the slope here is a 300 ms displacement over whatever baseline it was measured "
                + "across, so it reads differently every time the baseline grows. Applying it "
                + "runs the client's clock several percent wrong on purpose — measured live at "
                + "8.3% slow, a three-tick standing error and three whole steps of correction "
                + "at every transition, on a server whose own achieved_tick_hz averaged 60.");
        }

        [Test]
        public void ACorroboratedRateDoesReachTheClock()
        {
            // Half a percent, constant: an unusual but real pair. It reads the same over a 4 s
            // baseline and an 8 s one, so it corroborates and must be believed — the gate must
            // not have bought safety by refusing to correct at all.
            float scale = DriveAndReadClockScale(rate: 1.005, seconds: 40.0);

            Assert.That(scale, Is.Not.EqualTo(1f).Within(1e-4f),
                "a constant rate difference is exactly what SetClockRateScale exists for. "
                + "Refusing it would leave the proportional steer drooping against real drift, "
                + "which is the defect the rate feed was added to remove.");

            Assert.That(scale, Is.EqualTo(1f / 1.005f).Within(2e-3f),
                "and the scale applied must be the reciprocal of the real ratio");
        }
    }
}
