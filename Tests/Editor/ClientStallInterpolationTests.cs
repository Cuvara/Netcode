using System;
using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Interpolation;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using Cuvara.Netcode.World;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The render clock after a client stall, in the shape the live client sees it: the main
    /// thread stops, then the snapshots the server sent meanwhile are delivered oldest first, one
    /// every 10 ms, not all at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> The clock measured its seconds per tick as arrival gap over tick gap and
    /// took every value between 1 ms and 0.5 s. The first snapshot after a 2 s stall measured
    /// 0.5 s per tick; the backlog behind it measured 2.5 ms per tick, thirty times. The estimate
    /// fell to about 3 ms, the clock ran at about 300 ticks a second, and it ended 91 ticks past
    /// the newest snapshot. The 10 % rate cap then needed 15 s to bring it back, and for all that
    /// time every remote was held at its newest sample and stepped once per snapshot
    /// (rpg-factory testlab S13, runs 20261022T194438Z-S13-vdqw and 20261022T195013Z-S13-q3rr:
    /// render clock 32 to 35 ticks ahead 12 s after the stall, about 26 remote jumps a second).
    /// </para>
    /// <para>
    /// <b>The pacing is the live one.</b> Before the transport drained every queued datagram per
    /// wake, the client read one datagram per frame; the delivery log shows the 2 s backlog
    /// arriving as 32 snapshots over 315 to 353 ms, about one every 10 ms
    /// (<c>delivery.csv</c> of both runs). A harness that hands the whole backlog over in the
    /// stall-end frame (as <c>PredictionHarness</c> did) applies only the newest snapshot and
    /// never sees the defect.
    /// </para>
    /// <para>
    /// Two drivers: the binder, which advances by wall-clock time (the GameObject path), and the
    /// bare clock advanced by a capped frame delta, as the DOTS <c>InterpolationClockSystem</c>
    /// does with <c>Time.deltaTime</c> (Unity caps it at 0.333 s).
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class ClientStallInterpolationTests
    {
        private const string LocalId = "local-user";
        private const string RemoteId = "remote-user";
        private const int TicksPerSnapshot = 4;
        private const double TickHz = 60.0;
        private const double Speed = 5.0;           // units per second, the remote's walk
        private const double FrameSeconds = 0.005;
        private const double DrainFrameSeconds = 0.002;
        private const double DrainSpacingSeconds = 0.010;
        private const double StallAtSeconds = 10.0;
        private const double RecoverWithinSeconds = 1.0;
        private const double JudgeForSeconds = 4.0;
        private const int KeyframeEvery = 30;

        private sealed class RecordingView : IEntityView
        {
            public readonly Dictionary<string, float[]> Positions = new Dictionary<string, float[]>();
            public void Spawn(string id, bool isLocal, string type) { }
            public void Despawn(string id) => Positions.Remove(id);
            public void SetState(string id, float x, float y, int hp, int maxHp) => Positions[id] = new[] { x, y };
        }

        private sealed class ManualClock : IViewClock
        {
            public double NowMs { get; set; }
        }

        /// <summary>One rendered frame: when, the newest tick, the render tick, and the remote's x.</summary>
        private struct Frame
        {
            public double At;
            public double Dt;
            public long Newest;
            public double Render;
            public double X;
            public bool HasX;
        }

        /// <summary>
        /// The server's snapshot stream and the live delivery after a stall: production every
        /// four ticks at 60 Hz; delivered as produced, except that whatever was produced during
        /// the stall, and anything produced while that backlog drains, goes out oldest first at
        /// <see cref="DrainSpacingSeconds"/>.
        /// </summary>
        private sealed class Delivery
        {
            private readonly Queue<long> _queued = new Queue<long>();
            private long _nextTick = TicksPerSnapshot;
            private double _nextDeliverAt;
            public bool Draining;
            public double DrainEndedAt = double.NaN;

            public static double ProducedAt(long tick) => tick / TickHz;

            /// <summary>The snapshot to apply at <paramref name="now"/>, or 0.</summary>
            public long Take(double now)
            {
                while (ProducedAt(_nextTick) <= now)
                {
                    _queued.Enqueue(_nextTick);
                    _nextTick += TicksPerSnapshot;
                }

                if (_queued.Count == 0)
                {
                    if (Draining)
                    {
                        Draining = false;
                        DrainEndedAt = now;
                    }

                    return 0;
                }

                if (!Draining)
                {
                    // Ordinary delivery: at most one per frame here, because frames are shorter
                    // than the snapshot interval; take the newest the way a binder merges.
                    long newest = 0;
                    while (_queued.Count > 0) newest = _queued.Dequeue();
                    return newest;
                }

                if (now < _nextDeliverAt) return 0;
                _nextDeliverAt = now + DrainSpacingSeconds;
                return _queued.Dequeue();
            }

            public void StartDrain(double now)
            {
                Draining = true;
                _nextDeliverAt = now + DrainSpacingSeconds;
            }
        }

        private static double X(long tick) => tick / TickHz * Speed;

        /// <summary>The binder path: wall-clock deltas, the stall frame included.</summary>
        private static List<Frame> RunBinder(double stallSeconds, out double drainEndedAt)
        {
            var clock = new ManualClock();
            var view = new RecordingView();
            var world = new WorldState();
            var binder = new WorldViewBinder(view, null, clock, InterpolationConfig.Default);
            var delivery = new Delivery();
            var frames = new List<Frame>();
            int snapshots = 0;
            bool stalled = false;
            double now = 0.0, previous = 0.0;
            drainEndedAt = double.NaN;

            while (true)
            {
                double dt = delivery.Draining ? DrainFrameSeconds : FrameSeconds;
                bool stallFrame = false;
                if (!stalled && now >= StallAtSeconds)
                {
                    dt = stallSeconds;
                    stalled = true;
                    stallFrame = true;
                }

                now += dt;
                if (stallFrame)
                {
                    // The stall frame: the main thread wakes, nothing has been read yet.
                    delivery.StartDrain(now);
                }
                else
                {
                    long tick = delivery.Take(now);
                    if (tick > 0)
                    {
                        bool keyframe = snapshots++ % KeyframeEvery == 0;
                        var entities = new[] { new ResolvedEntity(RemoteId, "player", (float)X(tick), 0f, 100, 100) };
                        world.Apply(new ResolvedSnapshot(tick, 0L, keyframe, entities, new string[0]));
                    }
                }

                clock.NowMs = now * 1000.0;
                binder.Tick(world, LocalId);

                var f = new Frame { At = now, Dt = now - previous, Newest = world.Tick, Render = binder.RenderTick };
                if (view.Positions.TryGetValue(RemoteId, out var p))
                {
                    f.X = p[0];
                    f.HasX = true;
                }

                frames.Add(f);
                previous = now;

                if (!double.IsNaN(delivery.DrainEndedAt))
                {
                    drainEndedAt = delivery.DrainEndedAt;
                    if (now > drainEndedAt + RecoverWithinSeconds + JudgeForSeconds) break;
                }

                if (now > 60.0) break; // the drain never ended: the judge below reports it
            }

            return frames;
        }

        /// <summary>
        /// The DOTS path: the clock alone, fed each delivered snapshot at its receive time and
        /// advanced by a frame delta capped at Unity's default <c>maximumDeltaTime</c>.
        /// </summary>
        private static List<Frame> RunCappedClock(double stallSeconds, out double drainEndedAt)
        {
            const double MaximumDeltaTime = 1.0 / 3.0;
            var config = InterpolationConfig.Default;
            var clock = new InterpolationClock();
            var delivery = new Delivery();
            var frames = new List<Frame>();
            bool stalled = false;
            double now = 0.0, previous = 0.0;
            drainEndedAt = double.NaN;

            while (true)
            {
                double dt = delivery.Draining ? DrainFrameSeconds : FrameSeconds;
                bool stallFrame = false;
                if (!stalled && now >= StallAtSeconds)
                {
                    dt = stallSeconds;
                    stalled = true;
                    stallFrame = true;
                }

                now += dt;
                if (stallFrame)
                {
                    delivery.StartDrain(now);
                }
                else
                {
                    long tick = delivery.Take(now);
                    if (tick > 0) clock.NoteSnapshot(tick, now, TicksPerSnapshot, config);
                }

                clock.Advance(Math.Min(dt, MaximumDeltaTime), config);
                frames.Add(new Frame { At = now, Dt = now - previous, Newest = clock.NewestTick, Render = clock.RenderTick });
                previous = now;

                if (!double.IsNaN(delivery.DrainEndedAt))
                {
                    drainEndedAt = delivery.DrainEndedAt;
                    if (now > drainEndedAt + RecoverWithinSeconds + JudgeForSeconds) break;
                }

                if (now > 60.0) break;
            }

            return frames;
        }

        /// <summary>
        /// From <see cref="RecoverWithinSeconds"/> after the drain: the clock sits in the delay
        /// window behind the newest tick, and it moves at the server's rate within the 10 % cap.
        /// </summary>
        private static void AssertRecovered(List<Frame> frames, double drainEndedAt, string path, bool judgeX)
        {
            Assert.That(drainEndedAt, Is.Not.NaN, $"{path}: precondition: the backlog drained");

            var config = InterpolationConfig.Default;
            double nominal = 1.0 / TickHz;
            double lo = -config.MaxExtrapolation / nominal - 0.5;
            double hi = (config.TargetDelay + config.DefaultInterval) / nominal + 2.0;
            double from = drainEndedAt + RecoverWithinSeconds;

            int judged = 0;
            double worstAhead = double.NegativeInfinity, worstBehind = double.NegativeInfinity;
            double worstRate = 0.0, worstStep = 0.0;
            Frame last = default;
            bool haveLast = false;
            foreach (var f in frames)
            {
                if (f.At < from)
                {
                    last = f;
                    haveLast = true;
                    continue;
                }

                judged++;
                double behind = f.Newest - f.Render;
                worstBehind = Math.Max(worstBehind, behind);
                worstAhead = Math.Max(worstAhead, -behind);

                if (haveLast)
                {
                    worstRate = Math.Max(worstRate, (f.Render - last.Render) / (f.Dt * TickHz));
                    if (judgeX && f.HasX && last.HasX)
                    {
                        worstStep = Math.Max(worstStep, Math.Abs(f.X - last.X) / (Speed * f.Dt));
                    }
                }

                last = f;
                haveLast = true;
            }

            TestContext.WriteLine(
                $"{path}: drain ended {drainEndedAt:F3} s; judged {judged} frames from {from:F3} s: " +
                $"behind newest max {worstBehind:F2} ticks (window {hi:F2}), ahead max {worstAhead:F2} ticks " +
                $"(allowed {-lo:F2}), clock rate max {worstRate:F3}x, remote step max {worstStep:F3}x normal");

            Assume.That(judged, Is.GreaterThan(500), "precondition: frames to judge");
            Assert.That(worstAhead, Is.LessThanOrEqualTo(-lo),
                $"{path}: {RecoverWithinSeconds} s after the backlog the render clock must not be past the newest tick by more than the extrapolation cap");
            Assert.That(worstBehind, Is.LessThanOrEqualTo(hi),
                $"{path}: {RecoverWithinSeconds} s after the backlog the render clock must be inside the delay window");
            // Against the nominal 60 Hz: the clock's own seconds-per-tick is measured off 5 ms frames
            // and carries a fraction of a percent of quantisation, which is not a rate violation.
            Assert.That(worstRate, Is.LessThanOrEqualTo((1.0 + config.MaxClockRateAdjust) * 1.01),
                $"{path}: after recovery the clock runs at the server's rate within the cap");
            if (judgeX)
            {
                Assert.That(worstStep, Is.LessThanOrEqualTo(1.0 + config.MaxClockRateAdjust + 0.05),
                    $"{path}: after recovery no remote step exceeds the normal per-frame motion");
            }
        }

        [TestCase(0.5)]
        [TestCase(2.0)]
        public void AfterAClientStall_AndAPacedBacklog_TheBinderClockIsBackInTheDelayWindow(double stallSeconds)
        {
            var frames = RunBinder(stallSeconds, out double drainEndedAt);
            AssertRecovered(frames, drainEndedAt, $"binder, {stallSeconds} s stall", judgeX: true);
        }

        [TestCase(0.5)]
        [TestCase(2.0)]
        public void AfterAClientStall_AndAPacedBacklog_ACappedDeltaClockIsBackInTheDelayWindow(double stallSeconds)
        {
            var frames = RunCappedClock(stallSeconds, out double drainEndedAt);
            AssertRecovered(frames, drainEndedAt, $"capped delta, {stallSeconds} s stall", judgeX: false);
        }

        [Test]
        public void ABacklogAndAStallGap_AreNotRateSamples()
        {
            var config = InterpolationConfig.Default;
            var clock = new InterpolationClock();
            double now = 0.0;
            long tick = 4;
            for (; tick <= 240; tick += 4)
            {
                now = tick / TickHz;
                clock.NoteSnapshot(tick, now, 4, config);
            }

            Assume.That(clock.SecondsPerTick, Is.EqualTo(1.0 / TickHz).Within(1e-6), "precondition: a steady 60 Hz stream");

            now += 2.0; // the stall: the next arrival measures 2 s over 4 ticks
            double worst = 0.0;
            long backlogEnd = 240 + (long)(2.0 * TickHz);
            for (tick = 244; tick <= backlogEnd; tick += 4, now += 0.010)
            {
                clock.NoteSnapshot(tick, now, 4, config);
                worst = Math.Max(worst, Math.Abs(clock.SecondsPerTick * TickHz - 1.0));
            }

            TestContext.WriteLine($"worst seconds-per-tick error {worst:P2}, rejected {clock.RejectedIntervalSamples}");
            Assert.That(worst, Is.LessThan(0.01), "neither the stall gap nor the backlog may move the rate");
            Assert.That(clock.RejectedIntervalSamples, Is.EqualTo(30), "the stall gap and the 29 delivered behind it, each counted");
        }

        [TestCase(50.0)]  // 1.2x: inside the band, followed by the smoothing
        [TestCase(20.0)]  // 3x: outside it, relearned after RelearnSeconds
        public void ARateThatReallyChanged_IsStillLearned(double newHz)
        {
            var config = InterpolationConfig.Default;
            var clock = new InterpolationClock();
            double now = 0.0;
            long tick = 4;
            for (; tick <= 240; tick += 4)
            {
                now = tick / TickHz;
                clock.NoteSnapshot(tick, now, 4, config);
            }

            double end = now + InterpolationClock.RelearnSeconds + 3.0;
            while (now < end)
            {
                tick += 4;
                now += 4 / newHz;
                clock.NoteSnapshot(tick, now, 4, config);
            }

            Assert.That(clock.SecondsPerTick * newHz, Is.EqualTo(1.0).Within(0.02),
                $"a server really running at {newHz} Hz is learned");
        }
    }
}
