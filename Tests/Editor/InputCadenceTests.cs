using System;
using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Prediction;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Pins the send cadence against the snapshot cadence, and the schedule that makes the
    /// chosen cadence the one actually sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cases that matter here drive the <b>real</b> <see cref="AckLatencyEstimator"/>
    /// through a simulated pipeline rather than asserting against a hand-built distribution.
    /// That is deliberate: the whole defect is a relationship between two cadences and a
    /// guard, and a hand-built distribution assumes the very relationship under test. A test
    /// that fed the estimator a distribution someone believed a locked client produces would
    /// pass whether or not a locked client produces it.
    /// </para>
    /// <para>
    /// The pipeline is the one the package actually runs against: a 60 Hz base tick, a 15 Hz
    /// snapshot broadcast, and a fixed <c>uplink + age</c> the estimator is supposed to
    /// recover.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class InputCadenceTests
    {
        private const float BaseHz = 60f;
        private const int SnapshotHz = 15;
        private const double SnapshotPeriod = 1.0 / SnapshotHz;
        private const double ClockOffset = 4321.5;

        /// <summary>
        /// Runs a client sending at <paramref name="sendHz"/> into a server broadcasting
        /// snapshots at <see cref="SnapshotHz"/>, where an input becomes visible to the
        /// server <paramref name="uplinkPlusAge"/> seconds after it is sent and is
        /// acknowledged on the next snapshot after that.
        /// </summary>
        /// <remarks>
        /// The send schedule is pinned — <see cref="InputSendSchedule"/> — because that is
        /// what the production loops now do, and a test driving an ideal timer would not be
        /// testing the code that ships.
        /// </remarks>
        private static AckLatencyEstimator DrivePipeline(
            int sendHz, double uplinkPlusAge, double seconds)
        {
            var estimator = new AckLatencyEstimator();
            var arrivals = new Queue<(long Tick, double At)>();

            var schedule = new InputSendSchedule();
            schedule.Start(sendHz, ClockOffset);

            double now = ClockOffset;
            // Deliberately not phase-aligned with the first send: a server's broadcast has no
            // reason to line up with a client's first packet.
            double nextSnapshot = ClockOffset + SnapshotPeriod * 0.37;
            long inputTick = 0, accepted = 0;
            double end = ClockOffset + seconds;

            while (now < end)
            {
                double nextSend = now + schedule.SecondsUntilDue(now);
                now = Math.Min(nextSend, nextSnapshot);

                if (now >= nextSend)
                {
                    inputTick++;
                    estimator.RecordSent(inputTick, now);
                    arrivals.Enqueue((inputTick, now + uplinkPlusAge));
                    schedule.NoteSent(now);
                }

                if (now < nextSnapshot) continue;

                nextSnapshot += SnapshotPeriod;
                while (arrivals.Count > 0 && arrivals.Peek().At <= now)
                {
                    accepted = arrivals.Dequeue().Tick;
                }

                if (accepted > 0) estimator.RecordAck(accepted, now, BaseHz);
            }

            return estimator;
        }

        // ---- the defect and the fix, on the same pipeline ----

        /// <summary>
        /// Sending at the snapshot rate produces no estimate; sending at the recommended
        /// cadence produces one, and it lands on the real constant.
        /// </summary>
        /// <remarks>
        /// <b>This is the whole change in one assertion pair.</b> Both halves run the same
        /// server, the same route and the same injected constant; the only difference is how
        /// often the client speaks. That is what makes it evidence about the cadence rather
        /// than about the estimator.
        /// </remarks>
        [Test]
        public void ThePhaseLockedCadenceOffersNothingAndTheOffsetOneMeasuresTheConstant()
        {
            const double constantSeconds = 1.0 / BaseHz;   // one base tick of uplink + age

            var locked = DrivePipeline(SnapshotHz, constantSeconds, seconds: 20.0);
            var offset = DrivePipeline(
                InputCadence.RecommendedSendHz(SnapshotHz), constantSeconds, seconds: 20.0);

            Assert.That(locked.Samples, Is.GreaterThan(AckLatencyEstimator.MinimumSamples),
                "precondition: the locked run must have produced plenty of observations, or " +
                "it is refusing for lack of data rather than for lack of a sweep");

            Assert.That(locked.SweptEnough, Is.False,
                "sending at the snapshot rate holds the wait term fixed, so nothing observed " +
                "is evidence about where the floor is");
            Assert.That(locked.HasEstimate, Is.False,
                "and no floor may be offered on that evidence");

            Assert.That(offset.SweptEnough, Is.True,
                "the offset cadence makes the wait sweep, which is the entire point of it");
            Assert.That(offset.HasEstimate, Is.True,
                "and a swept distribution is what lets a floor be offered at all");
            Assert.That(offset.FloorTicks, Is.EqualTo(constantSeconds * BaseHz).Within(1.0f),
                "the floor must land on the injected constant rather than on the constant " +
                "plus a fixed wait");
        }

        /// <summary>
        /// The offset cadence sweeps the whole snapshot interval rather than a corner of it.
        /// </summary>
        /// <remarks>
        /// <see cref="AckLatencyEstimator.UnsweptSeconds"/> is the part of the wait's range
        /// never sampled, and therefore the most the floor can be reading high by. Asserting
        /// it is small is asserting the cadence did its job — and it is the measurement that
        /// separates 13 Hz from 12 Hz, which also sweeps but visits only four phases.
        /// </remarks>
        [Test]
        public void TheOffsetCadenceLeavesLittleOfTheWaitUnsampled()
        {
            var e = DrivePipeline(
                InputCadence.RecommendedSendHz(SnapshotHz), 1.0 / BaseHz, seconds: 20.0);

            int phases = InputCadence.DistinctPhases(
                InputCadence.RecommendedSendHz(SnapshotHz), SnapshotHz);

            // The phase set is finite and evenly spaced, so the unsampled remainder cannot be
            // smaller than one gap between adjacent phases. Allowing two gives room for the
            // quantisation of an ack onto a snapshot boundary without loosening this into a
            // statement that any cadence would satisfy.
            Assert.That(e.UnsweptSeconds, Is.LessThanOrEqualTo(SnapshotPeriod * 2.0 / phases),
                $"a {phases}-phase sweep should leave at most a couple of phase gaps of the " +
                "interval unsampled; much more than that means the cadence is not sweeping " +
                "the interval, only part of it");
        }

        /// <summary>
        /// A locked client is refused, and refusing is not the same as having no data.
        /// </summary>
        [Test]
        public void TheLockedCadenceIsRefusedForTheSweepAndNotForTheSampleCount()
        {
            var locked = DrivePipeline(SnapshotHz, 1.0 / BaseHz, seconds: 20.0);

            Assert.That(locked.SweptEnough, Is.False);
            Assert.That(locked.UnsweptSeconds, Is.EqualTo(SnapshotPeriod).Within(1e-6),
                "a locked client samples one phase, so the whole interval is unswept — this " +
                "is the number that says the refusal is about the sweep");
            Assert.That(locked.FloorTicks, Is.EqualTo(0f));
            Assert.That(locked.ConservativeFloorTicks, Is.EqualTo(0f),
                "nothing may reach the lead from a client with no evidence about its own " +
                "pipeline constant");
        }

        // ---- the rule ----

        [Test]
        public void TheRecommendationIsOffsetFromTheSnapshotRate()
        {
            Assert.That(InputCadence.RecommendedSendHz(SnapshotHz), Is.EqualTo(13),
                "13 against 15: coprime, so it visits 13 distinct phases, and it completes a " +
                "sweep every 6.5 sends");
            Assert.That(InputCadence.RecommendedSendHz(SnapshotHz), Is.Not.EqualTo(SnapshotHz),
                "a recommendation equal to the snapshot rate is the defect");
        }

        [TestCase(15, 13)]
        [TestCase(20, 17)]
        [TestCase(30, 23)]
        [TestCase(10, 7)]
        public void TheRuleHoldsAtOtherSnapshotRates(int snapshotHz, int expected)
        {
            Assert.That(InputCadence.RecommendedSendHz(snapshotHz), Is.EqualTo(expected));
            Assert.That(InputCadence.Sweeps(expected, snapshotHz), Is.True);
        }

        /// <summary>
        /// 12 Hz is rejected even though it sweeps quickly, and that rejection is the reason
        /// the rule is about coprimality rather than about closeness.
        /// </summary>
        [Test]
        public void ACadenceSharingAFactorWithTheSnapshotRateIsRejected()
        {
            Assert.That(InputCadence.SendsPerSweep(12, 15), Is.LessThan(
                    AckLatencyEstimator.MinimumSamples),
                "precondition: 12 Hz sweeps FASTER than the recommendation, so speed is not " +
                "what disqualifies it");
            Assert.That(InputCadence.DistinctPhases(12, 15), Is.EqualTo(4),
                "gcd(12, 15) = 3, so it only ever visits four phases");
            Assert.That(InputCadence.Sweeps(12, 15), Is.False,
                "and four phases is too coarse a set to read a floor from");

            Assert.That(InputCadence.DistinctPhases(13, 15), Is.EqualTo(13));
            Assert.That(InputCadence.Sweeps(13, 15), Is.True);
        }

        /// <summary>
        /// 14 Hz is rejected for the other condition: it is fine-grained but slow to sweep.
        /// </summary>
        [Test]
        public void ACadenceThatSweepsTooSlowlyIsRejected()
        {
            Assert.That(InputCadence.DistinctPhases(14, 15), Is.EqualTo(14),
                "precondition: 14 Hz is coprime with 15, so resolution is not what " +
                "disqualifies it");
            Assert.That(InputCadence.SendsPerSweep(14, 15), Is.GreaterThan(
                    AckLatencyEstimator.MinimumSamples),
                "it needs 14 sends to complete a sweep, against a minimum evidence count of " +
                "8 — so its first verdict would rest on a partial sweep");
            Assert.That(InputCadence.Sweeps(14, 15), Is.False);
        }

        /// <summary>
        /// A rate that divides the snapshot rate is never recommended, however coprime the
        /// arithmetic looks.
        /// </summary>
        /// <remarks>
        /// 1 Hz against 2 Hz is coprime and completes a "sweep" in one send, yet it visits a
        /// single phase — it is phase-locked. The rule has to reject it on the size of the
        /// phase set, not on the two headline conditions, and this is the case that proves
        /// the third condition earns its place.
        /// </remarks>
        [Test]
        public void ACoprimeRateThatStillVisitsOnePhaseIsNotRecommended()
        {
            Assert.That(InputCadence.DistinctPhases(1, 2), Is.EqualTo(1));
            Assert.That(InputCadence.Sweeps(1, 2), Is.False,
                "one phase is a lock, whatever the other two conditions say");

            // With nothing eligible the snapshot rate comes back unchanged. That is the
            // honest answer -- the estimator's own guard then refuses -- and NOT a fallback
            // that invents a plausible-looking cadence.
            Assert.That(InputCadence.RecommendedSendHz(2), Is.EqualTo(2));
            Assert.That(InputCadence.Sweeps(InputCadence.RecommendedSendHz(2), 2), Is.False);
        }

        [Test]
        public void APhaseLockedPairIsNeverReportedAsSweeping()
        {
            Assert.That(InputCadence.Sweeps(15, 15), Is.False);
            Assert.That(InputCadence.DistinctPhases(15, 15), Is.EqualTo(1));
            Assert.That(InputCadence.SendsPerSweep(15, 15), Is.EqualTo(double.PositiveInfinity));
        }

        // ---- the schedule ----

        /// <summary>
        /// The schedule delivers the rate it is asked for on a quantised frame clock, where
        /// re-deriving the deadline from the wake-up does not.
        /// </summary>
        /// <remarks>
        /// <b>This is the case that would have made the whole change a no-op.</b> The old
        /// loops delayed one period after each send; because a delay resumes on the first
        /// frame at or past the period and the remainder is discarded, every nominal rate in
        /// (12, 15] arrived as 12 Hz at 60 fps. The second half of this test reproduces that
        /// so the regression has a name.
        /// </remarks>
        [Test]
        public void ThePinnedScheduleHoldsItsNominalRateOnAQuantisedClock()
        {
            const double fps = 60.0;
            const double frame = 1.0 / fps;
            const int sendHz = 13;

            var schedule = new InputSendSchedule();
            double now = 0.0;
            schedule.Start(sendHz, now);

            double first = double.NaN, last = double.NaN;
            int sends = 0;

            for (var i = 0; i < (int)(fps * 30); i++)
            {
                if (schedule.SecondsUntilDue(now) <= 1e-9)
                {
                    if (double.IsNaN(first)) first = now;
                    last = now;
                    sends++;
                    schedule.NoteSent(now);
                }

                now += frame;
            }

            double achieved = (sends - 1) / (last - first);
            Assert.That(achieved, Is.EqualTo(sendHz).Within(0.05),
                "the pinned schedule must deliver the rate it was asked for; drifting off it " +
                "is what silently chose the cadence before");
            Assert.That(schedule.Resyncs, Is.Zero,
                "a steady clock must not trip the stall resynchronisation");

            // And the shape it replaces, on the same clock: re-arm from the wake-up and the
            // remainder is thrown away every iteration.
            double accumulating = 0.0, period = 1.0 / sendHz;
            double nextDue = 0.0, firstAcc = double.NaN, lastAcc = double.NaN;
            int accSends = 0;

            for (var i = 0; i < (int)(fps * 30); i++)
            {
                if (accumulating >= nextDue)
                {
                    if (double.IsNaN(firstAcc)) firstAcc = accumulating;
                    lastAcc = accumulating;
                    accSends++;
                    nextDue = accumulating + period;   // <-- from NOW, not from the schedule
                }

                accumulating += frame;
            }

            double accAchieved = (accSends - 1) / (lastAcc - firstAcc);
            Assert.That(accAchieved, Is.LessThan(sendHz - 0.5),
                "the loop shape this replaces must be shown to MISS the nominal rate, or the " +
                "pinned schedule above is solving a problem that does not exist");
            Assert.That(InputCadence.Sweeps((int)Math.Round(accAchieved), SnapshotHz), Is.False,
                "and what it drifts to is not merely a different rate, it is one that shares " +
                "a factor with the snapshot rate — so the cadence fix would have been undone " +
                "by the loop that carried it");
        }

        /// <summary>
        /// After a stall the schedule resynchronises instead of emitting the backlog at once.
        /// </summary>
        /// <remarks>
        /// Unbounded catch-up would be a burst at the server's input drain, and a burst
        /// destroys the phase relationship this whole type exists to preserve: every input in
        /// it lands inside one snapshot interval and is superseded, contributing no
        /// observation.
        /// </remarks>
        [Test]
        public void AStallResynchronisesRatherThanBurstingTheBacklog()
        {
            var schedule = new InputSendSchedule();
            schedule.Start(13, 0.0);

            double period = 1.0 / 13.0;
            for (var i = 0; i < 5; i++) schedule.NoteSent(i * period);
            Assert.That(schedule.Resyncs, Is.Zero, "precondition: a steady run does not resync");

            schedule.NoteSent(10.0);   // ten seconds of nothing: a breakpoint, or a suspend

            Assert.That(schedule.Resyncs, Is.EqualTo(1));
            Assert.That(schedule.SecondsUntilDue(10.0), Is.EqualTo(period).Within(1e-9),
                "the next send must be one period away, not a backlog of a hundred and thirty " +
                "due immediately");
        }

        // ---- what the cadence CANNOT fix ----

        /// <summary>
        /// Acknowledgements are read on a render frame, so the client's frame rate bounds how
        /// many phases can be told apart — and below
        /// <see cref="AckLatencyEstimator.MinimumOccupiedBuckets"/> of them no cadence passes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the limit of the fix and it is pinned deliberately.</b> Writing
        /// <c>k = fps / snapshotHz</c>, the wait resolves only to a frame period, so at most
        /// <c>k</c> phases are distinguishable however many the cadence visits. At 30 fps
        /// against a 15 Hz snapshot rate <c>k = 2</c>, the occupancy test needs 3, and 11, 12,
        /// 13 and 14 Hz are <i>all</i> refused — correctly, because the evidence genuinely is
        /// not there. A 30 fps client cannot measure its own pipeline constant, which on a
        /// mobile target is not hypothetical.
        /// </para>
        /// <para>
        /// Asserted so that a future reader finding "13 Hz still offers no floor" on a slow
        /// device looks at the frame rate rather than at the cadence.
        /// </para>
        /// </remarks>
        [Test]
        public void BelowThreeFramesPerSnapshotNoCadenceCanSweep()
        {
            // The bound the estimator's own occupancy test imposes, expressed in frames.
            int minimumFps = AckLatencyEstimator.MinimumOccupiedBuckets * SnapshotHz;
            Assert.That(minimumFps, Is.EqualTo(45),
                "three resolvable phases against a 15 Hz snapshot rate is 45 fps");

            foreach (var sendHz in new[] { 11, 12, 13, 14 })
            {
                var slow = DriveFrameQuantised(sendHz, fps: 30.0, 1.0 / BaseHz, seconds: 20.0);
                Assert.That(slow.SweptEnough, Is.False,
                    $"at 30 fps only two phases are distinguishable, so {sendHz} Hz cannot " +
                    "satisfy the occupancy test — the cadence is not the binding constraint");
            }

            var fast = DriveFrameQuantised(
                InputCadence.RecommendedSendHz(SnapshotHz), fps: 60.0, 1.0 / BaseHz,
                seconds: 20.0);
            Assert.That(fast.SweptEnough, Is.True,
                "and at 60 fps the same cadence sweeps, so the refusal above is about the " +
                "frame rate and not about the cadence");
        }

        /// <summary>
        /// As <see cref="DrivePipeline"/>, but acknowledgements are folded in on a render
        /// frame rather than at the instant they arrive — which is what a real client does,
        /// and what bounds the resolution of the whole measurement.
        /// </summary>
        private static AckLatencyEstimator DriveFrameQuantised(
            int sendHz, double fps, double uplinkPlusAge, double seconds)
        {
            var estimator = new AckLatencyEstimator();
            var arrivals = new Queue<(long Tick, double At)>();
            var readyAcks = new Queue<(long Ack, double At)>();

            var schedule = new InputSendSchedule();
            schedule.Start(sendHz, ClockOffset);

            double frame = 1.0 / fps;
            double nextSnapshot = ClockOffset + SnapshotPeriod * 0.37;
            long inputTick = 0, accepted = 0;

            for (double now = ClockOffset; now < ClockOffset + seconds; now += frame)
            {
                // The server keeps its own schedule regardless of the client's frames.
                while (nextSnapshot <= now + frame)
                {
                    while (arrivals.Count > 0 && arrivals.Peek().At <= nextSnapshot)
                    {
                        accepted = arrivals.Dequeue().Tick;
                    }

                    if (accepted > 0) readyAcks.Enqueue((accepted, nextSnapshot));
                    nextSnapshot += SnapshotPeriod;
                }

                if (schedule.SecondsUntilDue(now) <= 1e-12)
                {
                    inputTick++;
                    estimator.RecordSent(inputTick, now);
                    arrivals.Enqueue((inputTick, now + uplinkPlusAge));
                    schedule.NoteSent(now);
                }

                // Read on the frame, at the frame's timestamp: the client cannot observe an
                // arrival more precisely than the frame it notices it on.
                while (readyAcks.Count > 0 && readyAcks.Peek().At <= now)
                {
                    estimator.RecordAck(readyAcks.Dequeue().Ack, now, BaseHz);
                }
            }

            return estimator;
        }

        [Test]
        public void AnUnstartedScheduleIsDueImmediatelyRatherThanNever()
        {
            var schedule = new InputSendSchedule();

            Assert.That(schedule.SecondsUntilDue(123.0), Is.Zero,
                "a misconfigured rate must not be able to wedge a send loop forever");

            schedule.Start(0, 0.0);
            Assert.That(schedule.SecondsUntilDue(123.0), Is.Zero);
        }
    }
}
