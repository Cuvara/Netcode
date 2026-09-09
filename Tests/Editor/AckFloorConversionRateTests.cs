using System;
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
    /// The acknowledgement floor is a DURATION, and a duration converts to base ticks with the
    /// MEASURED rate — not the advertised one the staleness fit next to it must use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> <c>WorldViewBinder</c> converted the same kind of quantity two ways.
    /// The round-trip term used <c>TickRate.EstimatedHz</c>, the rate measured off the wire; the
    /// acknowledgement floor used the advertised rate the server announced at join. When the two
    /// disagree by <c>e</c> — a client clock running fast, or a server genuinely running slow,
    /// and nothing here can tell those apart — the floor comes out <c>(1+e)</c> too large and the
    /// steering lead, which is compared against the server's own tick NUMBERS, over-leads by
    /// <c>e × floor</c>. That is the direction this estimator exists to avoid.
    /// </para>
    /// <para>
    /// <b>Why this is not a revert of the comment beside it.</b> The advertised rate is
    /// deliberate for <c>SnapshotStalenessEstimator.Sample</c>, and there is a measured failure
    /// behind that choice: it converts a TICK NUMBER to a time (<c>snapshotTick / baseHz</c>), so
    /// a rate error accumulates against a growing counter — 613 ticks and climbing inside a
    /// minute, on a 57.7 Hz reading of a 60 Hz server. <c>AckLatencyEstimator</c> never does
    /// that. It uses <c>baseHz</c> for one thing, <c>FloorSeconds * baseHz</c>, where there is no
    /// counter to accumulate against and the quantity is tens of milliseconds. The two lines
    /// shared a comment and needed different rates.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class AckFloorConversionRateTests
    {
        private const string LocalId = "local";

        /// <summary>What the server said at join.</summary>
        private const int AdvertisedHz = 60;

        /// <summary>
        /// What the wire actually delivers, as the client's clock sees it — 8% slow.
        /// </summary>
        /// <remarks>
        /// Large enough that the two conversions cannot be confused for each other, and the same
        /// order as the disagreement measured live on this project's own machine, where a 60 Hz
        /// server read 55.1 Hz against a client clock 9% fast.
        /// </remarks>
        private const double MeasuredHz = 55.0;

        /// <summary>
        /// The floor is converted with the rate measured off the wire, not the advertised one.
        /// </summary>
        /// <remarks>
        /// <b>This test fails against the code it was written for.</b> With the advertised rate
        /// the reported floor is <c>60/55 = 1.09</c> times larger than the truth, and the
        /// assertion below is tight enough to see it: the expected and defective values differ
        /// by more than the tolerance by construction, which is checked in the test itself so
        /// that a future loosening of the tolerance cannot quietly make it pass either way.
        /// </remarks>
        [Test]
        public void TheFloorIsConvertedWithTheMeasuredRateNotTheAdvertisedOne()
        {
            var (binder, floorSeconds) = DriveASweptSession();

            Assert.That(binder.AckLatency.HasEstimate, Is.True,
                "precondition: the session must have produced a floor at all, or this test is " +
                "asserting about zero");
            Assert.That(binder.AckFloorRateIsFallback, Is.False,
                "precondition: the tick rate estimator must have a measurement, or the binder " +
                "is legitimately using the advertised rate and there is nothing to test");
            Assert.That(binder.AckFloorConversionHz, Is.EqualTo((float)MeasuredHz).Within(1.0f),
                "precondition: the wire rate the binder measured must be the one this session " +
                "delivered, or the expectation below is about the wrong number");

            // COMPARED AGAINST THE RATE THE BINDER SAYS IT USED, not against a hand-computed
            // expectation. The first attempt asserted `FloorTicks == floorSeconds * MeasuredHz`
            // with a tolerance, and its own discrimination self-check refused it: the two
            // candidate conversions differ by only ~0.135 base ticks here, which is smaller than
            // any tolerance loose enough to absorb the estimator's measurement error. Widening
            // the tolerance to fit would have produced a test that passes against both the
            // correct and the defective conversion, which is worse than no test.
            //
            // Splitting the claim removes the tension. WHICH rate is in use is a question about
            // AckFloorConversionHz and is answered by a wide margin (55 against 60). WHETHER the
            // floor was converted with that rate is an exact arithmetic identity and holds to
            // floating point. Neither half needs a tolerance sized against the other.
            double reported = binder.AckLatency.FloorTicks;
            double viaTheRateInUse = floorSeconds * binder.AckFloorConversionHz;
            double viaAdvertised = floorSeconds * AdvertisedHz;
            const double exactness = 0.01;

            Assert.That(binder.AckFloorConversionHz,
                Is.Not.EqualTo((float)AdvertisedHz).Within(2.0f),
                "the rate in use must not be the advertised one - that is the defect");

            Assert.That(reported, Is.EqualTo(viaTheRateInUse).Within(exactness),
                "the floor must actually be converted with the rate the binder reports using. " +
                "This is the assertion that fails if RecordAck is handed _predictor.TickRateHz " +
                "again: AckFloorConversionHz would still read the measured rate while the floor " +
                "was computed from the advertised one, and the two would stop agreeing.");

            // AND THE TEST MUST BE ABLE TO TELL THE TWO APART. Asserted rather than assumed,
            // with the margin tied to the exactness above rather than to a number chosen by eye:
            // a test that cannot fail reads as coverage while providing none.
            Assert.That(Math.Abs(reported - viaAdvertised), Is.GreaterThan(exactness * 5.0),
                "this test cannot discriminate: converting with the advertised rate would give " +
                $"{viaAdvertised:F3} base ticks against the {reported:F3} observed, and those " +
                "are too close to separate at this exactness. Widen the gap between " +
                "AdvertisedHz and MeasuredHz.");
        }

        /// <summary>
        /// Before the wire rate has been measured the advertised rate stands in — and the
        /// substitution is visible and counted rather than silent.
        /// </summary>
        /// <remarks>
        /// A fallback is another claim about the same quantity, not the absence of one. Every
        /// fallback in this area that went wrong went wrong by being invisible, so the one
        /// introduced here is required to announce itself.
        /// </remarks>
        [Test]
        public void TheFallbackToTheAdvertisedRateIsVisibleAndCounted()
        {
            var view = new NullView();
            var predictor = new LocalMovePredictor(
                new PredictionSettings(AdvertisedHz, 5f, MapBounds.Default));
            var clock = new ManualClock();
            var binder = new WorldViewBinder(view, predictor, clock);

            Assert.That(binder.AckFloorRateIsFallback, Is.True,
                "with no snapshots seen there is no measured rate, so the advertised one must " +
                "be in use");
            Assert.That(binder.AckFloorConversionHz, Is.EqualTo((float)AdvertisedHz),
                "and the fallback must be the advertised rate rather than zero or a guess");
            Assert.That(binder.AckFloorRateFallbacks, Is.Zero,
                "nothing has been converted yet, so nothing has been counted");

            // One snapshot: not enough for the rate estimator, so the fallback is exercised.
            var world = new WorldState();
            world.Apply(new ResolvedSnapshot(
                100, 1, true, new[] { Entity(LocalId) }, Array.Empty<string>()));
            binder.NoteInputSent(1);
            binder.Tick(world, LocalId);

            Assert.That(binder.AckFloorRateFallbacks, Is.GreaterThan(0),
                "an acknowledgement converted with the advertised rate must be counted, or a " +
                "fallback that never stops being one is invisible");
        }

        /// <summary>
        /// A binder with no predictor and no measured rate offers no conversion rate at all,
        /// rather than inventing one.
        /// </summary>
        [Test]
        public void WithNeitherRateAvailableNothingIsInvented()
        {
            var binder = new WorldViewBinder(new NullView());

            Assert.That(binder.AckFloorConversionHz, Is.Zero,
                "there is no rate to convert with, and RecordAck refuses a non-positive one " +
                "outright — which is the correct outcome, not a case for a plausible default");
            Assert.That(binder.AckFloorRateIsFallback, Is.True);
        }

        // ---- the session ----

        /// <summary>
        /// Runs a client whose snapshots arrive at <see cref="MeasuredHz"/> while its predictor
        /// was configured at <see cref="AdvertisedHz"/>, with the send cadence offset from the
        /// snapshot cadence so the acknowledgement wait sweeps and a floor is offered at all.
        /// </summary>
        /// <returns>The binder, and the floor it measured in seconds.</returns>
        private static (WorldViewBinder Binder, double FloorSeconds) DriveASweptSession()
        {
            var view = new NullView();
            var predictor = new LocalMovePredictor(
                new PredictionSettings(AdvertisedHz, 5f, MapBounds.Default));
            var clock = new ManualClock();
            var binder = new WorldViewBinder(view, predictor, clock);
            var world = new WorldState();

            // The wire as the client's clock sees it: base ticks at MeasuredHz, snapshots every
            // fourth one, and an input cadence offset from the snapshot cadence so the wait term
            // sweeps. Sending at the snapshot rate would phase-lock the two and the estimator
            // would correctly refuse to offer any floor -- which is the defect InputCadence
            // exists to prevent and would leave this test asserting about zero.
            const int snapshotEveryBaseTicks = 4;
            double snapshotPeriod = snapshotEveryBaseTicks / MeasuredHz;
            double snapshotHz = 1.0 / snapshotPeriod;
            int sendHz = InputCadence.RecommendedSendHz((int)Math.Round(snapshotHz));
            double sendPeriod = 1.0 / sendHz;

            const double uplinkPlusAge = 0.020;

            var arrivals = new Queue<(long Tick, double At)>();
            double now = 0.0;
            double nextSend = 0.0, nextSnapshot = snapshotPeriod * 0.37;
            long inputTick = 0, accepted = 0, serverTick = 100;

            while (now < 30.0)
            {
                now = Math.Min(nextSend, nextSnapshot);
                clock.NowMs = now * 1000.0;

                if (now >= nextSend)
                {
                    nextSend = now + sendPeriod;
                    inputTick++;
                    binder.NoteInputSent(inputTick);
                    arrivals.Enqueue((inputTick, now + uplinkPlusAge));
                }

                if (now < nextSnapshot) continue;

                nextSnapshot = now + snapshotPeriod;
                serverTick += snapshotEveryBaseTicks;

                while (arrivals.Count > 0 && arrivals.Peek().At <= now)
                {
                    accepted = arrivals.Dequeue().Tick;
                }

                if (accepted <= 0) continue;

                world.Apply(new ResolvedSnapshot(
                    serverTick, accepted, true, new[] { Entity(LocalId) }, Array.Empty<string>()));
                binder.Tick(world, LocalId);
            }

            return (binder, binder.AckLatency.FloorSeconds);
        }

        private static ResolvedEntity Entity(string id) =>
            new ResolvedEntity(id, "player", 0f, 0f, 100, 100, 0f);

        private sealed class NullView : IEntityView
        {
            public void Spawn(string id, bool isLocal, string type)
            {
            }

            public void Despawn(string id)
            {
            }

            public void SetState(string id, float x, float y, int hp, int maxHp,
            uint facingBrad, Shared.GameLogic.Components.EntityAction action)
            {
            }
        }

        private sealed class ManualClock : IViewClock
        {
            public double NowMs { get; set; }
        }
    }
}
