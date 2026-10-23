using System;
using System.Collections.Generic;
using Cuvara.Netcode.Prediction;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// A deterministic client/server pair for measuring prediction the way a player sees it: a
    /// stub of the game server's per-tick movement rules on one clock, a
    /// <see cref="LocalMovePredictor"/> driven like a real client on another, and a seeded
    /// network with latency and jitter between them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The server stub is the server's rules, not an approximation of them.</b> One base tick
    /// is: drain every input that has arrived; reject a tick at or below the acknowledged one;
    /// record the acknowledgement and the tick that applied it (<c>ack_applied_tick</c>); clear
    /// the hold on a stop; step ONCE with the newest input of the batch; then, if no input stepped
    /// this tick, step the held direction while it is inside the silence window. Every step is
    /// <see cref="MovementSystem.TryMove"/>, the same call both real sides make
    /// (<c>InputHandler.ProcessInput</c> / <c>ApplyHeldMovement</c>, flat ground, planar model).
    /// </para>
    /// <para>
    /// <b>Two clocks, really two.</b> The server ticks at <see cref="Config.ServerHz"/> of real
    /// time; the client advances its predictor by frame time at its own rate and is steered onto
    /// the server's tick line exactly as a game would be. A server running 0.7% slow (59.6 Hz) is
    /// the case that exposed the clock droop; 60.0 Hz with no jitter is the case in which nothing
    /// may be corrected at all.
    /// </para>
    /// <para>
    /// Everything is driven from one seeded <see cref="Random"/>, so a run is a pure function of
    /// its <see cref="Config"/>.
    /// </para>
    /// </remarks>
    public sealed class PredictionHarness
    {
        public enum Motion
        {
            /// <summary>Constant speed around a circle: the direction turns a little every input.</summary>
            Circle,

            /// <summary>Move east for a while, stop for a while, repeat: every start and stop.</summary>
            StartStop,
        }

        public enum ClientMode
        {
            /// <summary>Reconcile with the snapshot tick AND ack_applied_tick; steer to a fixed lead.</summary>
            AckApplied,

            /// <summary>Reconcile with the snapshot tick only (a protocol 2 server); steer to a fixed lead.</summary>
            SnapshotTickOnly,

            /// <summary>
            /// Reconcile with ack_applied_tick and steer through <see cref="PredictionClockSteering"/>
            /// with its MEASURED lead, the way WorldViewBinder and the DOTS system do.
            /// </summary>
            MeasuredSteering,

            /// <summary>
            /// The DOTS system before this fix: two-argument Reconcile on a new ack, no steering.
            /// </summary>
            TwoArgumentNoSteering,
        }

        public struct Config
        {
            public double ServerHz;
            public int SnapshotEvery;
            public double SendHz;
            public double SendJitterSeconds;
            public double UplinkSeconds;
            public double DownlinkSeconds;
            public double NetworkJitterSeconds;
            public double FrameSeconds;
            public double DurationSeconds;
            public int LeadTicks;
            public Motion Motion;
            public ClientMode Mode;
            public float Speed;
            public double CircleRadius;
            public double StartStopPeriodSeconds;
            public int Seed;

            /// <summary>Seconds at the start of a run excluded from every statistic.</summary>
            public double WarmupSeconds;

            /// <summary>
            /// Tick rate the predictor is CONSTRUCTED with. 0 = the server's (60). A game that
            /// registers its predictor before it has joined passes a constant here.
            /// </summary>
            public int PredictorTickRate;

            /// <summary>
            /// When nonzero, the client hands the predictor this advertised rate through
            /// <see cref="LocalMovePredictor.UseServerTickRate"/> right after constructing it,
            /// the way a game does once the join reply has arrived.
            /// </summary>
            public uint AdvertisedTickRate;

            /// <summary>
            /// Deliver snapshots in production order, as KCP does: a snapshot held back holds
            /// back every later one with it (head-of-line blocking).
            /// </summary>
            public bool InOrderDownlink;

            /// <summary>
            /// When positive, the first <see cref="BurstSnapshots"/> snapshots produced at or after
            /// this time are held until <see cref="BurstDelaySeconds"/> after the first of them
            /// was produced (plus the downlink), and then delivered together.
            /// </summary>
            public double BurstAtSeconds;

            /// <summary>How many consecutive snapshots the burst holds back.</summary>
            public int BurstSnapshots;

            /// <summary>How long the first held snapshot is late.</summary>
            public double BurstDelaySeconds;

            /// <summary>
            /// When <see cref="HitchSeconds"/> is positive, the first client frame at or after this
            /// time lasts <see cref="HitchSeconds"/> instead of <see cref="FrameSeconds"/>: a client
            /// stall (a GC pause, a scene load, a window drag). The server keeps ticking through it.
            /// </summary>
            public double HitchAtSeconds;

            /// <summary>Length of the one hitched frame. 0 = no hitch.</summary>
            public double HitchSeconds;

            /// <summary>
            /// When positive, the snapshots that arrived during the hitch are not all there when it
            /// ends: they are delivered oldest first, one every this many seconds, and so is every
            /// snapshot produced until the delivery has caught up. Zero hands the whole backlog over
            /// in the hitched frame.
            /// </summary>
            /// <remarks>
            /// The live shape. The client read one datagram per frame, so the backlog of a 2 s stall
            /// reached the world as 32 snapshots over 315 to 353 ms, about one every 10 ms
            /// (rpg-factory testlab S13, runs 20261022T194438Z-S13-vdqw and -q3rr,
            /// <c>delivery.csv</c>). Requires <see cref="InOrderDownlink"/>.
            /// </remarks>
            public double BacklogSpacingSeconds;

            /// <summary>
            /// When positive, the frame time handed to the predictor is capped at this, as Unity
            /// caps <c>Time.deltaTime</c> at <c>maximumDeltaTime</c> (0.333 s by default) for the
            /// DOTS prediction system. Zero passes the real frame time.
            /// </summary>
            public double MaxFrameDeltaSeconds;

            /// <summary>
            /// Record the frame's input before its snapshot is applied, as the client does: the
            /// input is sampled and sent in <c>DotsWorldBridge.Update</c>, and the snapshot is
            /// steered on and reconciled later in the frame by <c>LocalPredictionSystem</c>. False
            /// keeps the harness's original order (snapshot first).
            /// </summary>
            public bool InputBeforeSnapshot;

            /// <summary>
            /// Hand the predictor each frame's duration one frame late, as Unity's
            /// <c>Time.deltaTime</c> does: a frame reports how long the PREVIOUS frame took. A stall
            /// inside a frame therefore reaches <c>Advance</c> only in the frame after it, after that
            /// frame's input and snapshot have already been handled (rpg-factory testlab S13, run
            /// 20261023T010610Z-S13-j8xw: the backlog was applied in the stall frame, f24528, and the
            /// 2 s arrived as deltaTime in f24529).
            /// </summary>
            public bool DeltaTimeLagsOneFrame;

            /// <summary>
            /// A snapshot that arrives during a frame is used by the prediction only in the next
            /// frame, as the client does: the transport applies it to the world late in the frame,
            /// after <c>LocalPredictionSystem</c> has run. A stall's backlog, read in the stall
            /// frame, is then reconciled one frame later, after that frame's input and its
            /// <c>Advance</c> (rpg-factory testlab S13, run 20261023T014834Z-S13-bjzo: backlog
            /// applied in f11811, reconciled in f11812, the next snapshot applied in f11814 and
            /// reconciled in f11815).
            /// </summary>
            public bool SnapshotsVisibleNextFrame;

            /// <summary>When set, receives the predictor's and the steering's call trace.</summary>
            public PredictorTraceSink Trace;

            /// <summary>
            /// When positive, every frame lasts <see cref="FrameSeconds"/> plus a uniform jitter
            /// of up to this much either way (an uneven frame rate).
            /// </summary>
            public double FrameJitterSeconds;

            /// <summary>
            /// When <see cref="ServerStallSeconds"/> is positive, every server tick due in
            /// [<see cref="ServerStallAtSeconds"/>, +<see cref="ServerStallSeconds"/>) runs at the
            /// end of that window instead, back to back: a server stall (a GC pause, a blocked tick
            /// thread) followed by its catch-up. Inputs queue meanwhile and the snapshots of the
            /// stalled ticks leave together.
            /// </summary>
            public double ServerStallAtSeconds;

            /// <summary>Length of the server stall. 0 = no stall.</summary>
            public double ServerStallSeconds;

            public static Config Default => new Config
            {
                ServerHz = 60.0,
                SnapshotEvery = 4,
                SendHz = 13.0,
                SendJitterSeconds = 0.0,
                UplinkSeconds = 0.040,
                DownlinkSeconds = 0.040,
                NetworkJitterSeconds = 0.0,
                FrameSeconds = 1.0 / 144.0,
                DurationSeconds = 30.0,
                LeadTicks = 6,
                Motion = Motion.Circle,
                Mode = ClientMode.AckApplied,
                Speed = 5f,
                CircleRadius = 6.0,
                StartStopPeriodSeconds = 1.0,
                Seed = 12345,
                WarmupSeconds = 3.0,
            };
        }

        public sealed class Result
        {
            public int Reconciles;
            public int NonzeroCorrections;
            public double MeanCorrection;
            public double MaxCorrection;
            public int CorrectionsAtLeastOneStep;
            public float StepLength;
            public double MaxFrameMotionRatio;
            public int FramesOverOneAndAHalf;
            public int MovingFrames;
            public long FinalAckTickOffset;
            public long MinAckTickOffset = long.MaxValue;
            public long MaxAckTickOffset = long.MinValue;
            public long FinalTickError;
            public long FinalClientLeadTicks;
            public int HardResyncs;
            public int Snaps;

            /// <summary>Largest distance the rendered position moved in one moving frame, in units.</summary>
            public double MaxFrameStep;

            /// <summary>Reconciles the history could not answer (see LocalMovePredictor.HistoryMisses).</summary>
            public int HistoryMisses;

            /// <summary>Counted corrections of at least four base steps -- one snapshot interval.</summary>
            public int CorrectionsAtLeastFourSteps;

            /// <summary>
            /// Every counted reconcile in order: when it ran, how far it corrected, and the clock's
            /// tick error at the steer that preceded it.
            /// </summary>
            public readonly List<(double At, float Correction, long TickError)> CorrectionLog =
                new List<(double, float, long)>();

            /// <summary>The predictor's clock-rate scale at the end of the run (1 = the server's rate).</summary>
            public float FinalClockRateScale;

            /// <summary>The staleness estimator's fitted skew at the end of the run, in ppm.</summary>
            public double FinalSkewPpm;

            /// <summary>Frames whose time the predictor clamped (LocalMovePredictor.ClampedFrames).</summary>
            public int ClampedFrames;

            /// <summary>Clock jumps made for a stall (LocalMovePredictor.StallResyncs).</summary>
            public int StallResyncs;

            public override string ToString() =>
                $"reconciles={Reconciles} nonzero={NonzeroCorrections} mean={MeanCorrection:F5} " +
                $"max={MaxCorrection:F5} (>=1 step: {CorrectionsAtLeastOneStep}, >=4 steps: {CorrectionsAtLeastFourSteps}, " +
                $"step={StepLength:F4}) misses={HistoryMisses} maxFrameStep={MaxFrameStep:F3} " +
                $"frameMotion max={MaxFrameMotionRatio:F2}x over1.5x={FramesOverOneAndAHalf}/{MovingFrames} " +
                $"offset final={FinalAckTickOffset} range=[{MinAckTickOffset},{MaxAckTickOffset}] " +
                $"clientLead={FinalClientLeadTicks} " +
                $"tickError={FinalTickError} hardResyncs={HardResyncs} snaps={Snaps}";
        }

        private struct InFlightInput
        {
            public double Arrival;
            public long Tick;
            public float MoveX;
            public float MoveY;
        }

        private struct InFlightSnapshot
        {
            public double Arrival;
            public long Tick;
            public long AckTick;
            public long AckAppliedTick;
            public Vec2 Position;
        }

        /// <summary>The server's per-tick movement rules for one player.</summary>
        private sealed class ServerStub
        {
            private static readonly MapBounds Bounds = MapBounds.Default;

            private readonly float _dt;
            private readonly float _speed;
            private readonly int _maxBankedTicks;

            public long Tick;
            public Vec2 Position;
            public long AckTick;
            public long AckAppliedTick;
            private float _heldX, _heldY;
            private long _heldFrom;

            public ServerStub(int nominalHz, float speed, Vec2 start, long startTick)
            {
                _dt = MovementSystem.DeltaTimeForTickRate(nominalHz);
                _speed = speed;
                _maxBankedTicks = GameConstants.MaxBankedMovementTicks(nominalHz);
                Position = start;
                Tick = startTick;
            }

            public void Step(List<InFlightInput> drained)
            {
                Tick++;

                // InputHandler.ProcessInput, in drain order. Only the newest input of the batch
                // moves (TickLoop's _newestInputIndex); every one of them still updates the ack
                // and may clear the hold.
                int newest = -1;
                for (int i = 0; i < drained.Count; i++)
                {
                    if (newest < 0 || drained[i].Tick > drained[newest].Tick) newest = i;
                }

                bool steppedByInput = false;
                for (int i = 0; i < drained.Count; i++)
                {
                    InFlightInput input = drained[i];
                    if (input.Tick <= AckTick) continue; // StaleTick

                    AckTick = input.Tick;
                    AckAppliedTick = Tick;

                    float magSq = input.MoveX * input.MoveX + input.MoveY * input.MoveY;
                    if (magSq <= GameConstants.InputDeadzoneSq)
                    {
                        _heldFrom = 0;
                    }

                    if (i != newest) continue;

                    var probe = new EntityState { Position = Position, Speed = _speed, Dead = false };
                    MoveResult r = MovementSystem.TryMove(
                        in probe, input.MoveX, input.MoveY, _dt, in Bounds, out Vec2 next);
                    if (r is MoveResult.Accepted or MoveResult.Clamped)
                    {
                        Position = next;
                        _heldX = input.MoveX;
                        _heldY = input.MoveY;
                        _heldFrom = Tick;
                        steppedByInput = true;
                    }
                    else if (r == MoveResult.None)
                    {
                        _heldFrom = 0;
                    }
                }

                // InputHandler.ApplyHeldMovement.
                if (steppedByInput || _heldFrom == 0 || _heldFrom == Tick) return;
                if (Tick - _heldFrom > _maxBankedTicks) return;

                var held = new EntityState { Position = Position, Speed = _speed, Dead = false };
                MoveResult hr = MovementSystem.TryMove(
                    in held, _heldX, _heldY, _dt, in Bounds, out Vec2 heldNext);
                if (hr is MoveResult.Accepted or MoveResult.Clamped)
                {
                    Position = heldNext;
                }
            }
        }

        public static Result Run(Config c)
        {
            const int NominalHz = 60;
            var rng = new Random(c.Seed);
            var result = new Result();

            var start = new Vec2(0f, 0f);
            var server = new ServerStub(NominalHz, c.Speed, start, startTick: 100_000);
            int constructedHz = c.PredictorTickRate > 0 ? c.PredictorTickRate : NominalHz;
            var predictor = new LocalMovePredictor(new PredictionSettings(constructedHz, c.Speed, MapBounds.Default));
            predictor.Trace = c.Trace;
            if (c.AdvertisedTickRate > 0u)
            {
                predictor.UseServerTickRate(c.AdvertisedTickRate);
            }

            var steering = new PredictionClockSteering(predictor);
            steering.Trace = c.Trace;

            result.StepLength = c.Speed / NominalHz;

            var uplink = new List<InFlightInput>();
            var downlink = new List<InFlightSnapshot>();
            var drained = new List<InFlightInput>();

            double serverPeriod = 1.0 / c.ServerHz;
            double nextServerTick = serverPeriod;
            double nextSend = 0.25;
            double now = 0.0;

            double lastDownlinkArrival = 0.0;
            int burstHeld = 0;
            double burstRelease = 0.0;

            long inputTick = 0;
            long lastAppliedSnapshotTick = 0;
            long lastAck = 0;
            bool seeded = false;

            double correctionSum = 0.0;
            bool havePrevious = false;
            Vec2 previous = default;

            bool hitched = false;
            double previousFrame = c.FrameSeconds;
            bool pacing = false;

            while (now < c.DurationSeconds)
            {
                double frame = c.FrameJitterSeconds > 0.0
                    ? c.FrameSeconds + Jitter(rng, c.FrameJitterSeconds)
                    : c.FrameSeconds;
                bool hitchFrame = false;
                bool moving = false;
                if (c.HitchSeconds > 0.0 && !hitched && now >= c.HitchAtSeconds)
                {
                    frame = c.HitchSeconds;
                    hitched = true;
                    hitchFrame = true;
                }

                now += frame;

                // ── Server, on its own clock ──
                while (ServerRunsAt(c, nextServerTick) <= now)
                {
                    double runAt = ServerRunsAt(c, nextServerTick);
                    drained.Clear();
                    for (int i = uplink.Count - 1; i >= 0; i--)
                    {
                        if (uplink[i].Arrival <= runAt)
                        {
                            drained.Add(uplink[i]);
                            uplink.RemoveAt(i);
                        }
                    }

                    // Arrival order, which is the order the server drains in.
                    drained.Sort((a, b) => a.Arrival.CompareTo(b.Arrival));
                    server.Step(drained);

                    if (server.Tick % c.SnapshotEvery == 0)
                    {
                        double arrival = runAt + c.DownlinkSeconds + Jitter(rng, c.NetworkJitterSeconds);
                        if (c.BurstAtSeconds > 0.0 && nextServerTick >= c.BurstAtSeconds && burstHeld < c.BurstSnapshots)
                        {
                            if (burstHeld == 0)
                            {
                                burstRelease = arrival + c.BurstDelaySeconds;
                            }

                            burstHeld++;
                            if (arrival < burstRelease) arrival = burstRelease;
                        }

                        if (c.InOrderDownlink)
                        {
                            if (arrival < lastDownlinkArrival) arrival = lastDownlinkArrival;
                            if (pacing)
                            {
                                if (arrival < lastDownlinkArrival + c.BacklogSpacingSeconds)
                                {
                                    arrival = lastDownlinkArrival + c.BacklogSpacingSeconds;
                                }
                                else
                                {
                                    pacing = false;
                                }
                            }

                            lastDownlinkArrival = arrival;
                        }

                        downlink.Add(new InFlightSnapshot
                        {
                            Arrival = arrival,
                            Tick = server.Tick,
                            AckTick = server.AckTick,
                            AckAppliedTick = server.AckAppliedTick,
                            Position = server.Position,
                        });
                    }

                    nextServerTick += serverPeriod;
                }

                // ── The backlog of a hitch, delivered at the transport's pace ──
                if (hitchFrame && c.BacklogSpacingSeconds > 0.0)
                {
                    var backlog = downlink.FindAll(x => x.Arrival <= now);
                    backlog.Sort((a, b) => a.Tick.CompareTo(b.Tick));
                    double at = now;
                    foreach (var held in backlog)
                    {
                        at += c.BacklogSpacingSeconds;
                        int index = downlink.FindIndex(x => x.Tick == held.Tick);
                        InFlightSnapshot restamped = downlink[index];
                        restamped.Arrival = at;
                        downlink[index] = restamped;
                    }

                    // Everything produced later queues behind the backlog, and is paced too until
                    // the delivery has caught up with the server.
                    for (int i = 0; i < downlink.Count; i++)
                    {
                        if (downlink[i].Arrival > at) continue;
                        if (backlog.Exists(x => x.Tick == downlink[i].Tick)) continue;
                        at += c.BacklogSpacingSeconds;
                        InFlightSnapshot later = downlink[i];
                        later.Arrival = at;
                        downlink[i] = later;
                    }

                    lastDownlinkArrival = at;
                    pacing = backlog.Count > 0;
                }

                // ── Live order: the input is sampled and recorded before the frame's snapshot ──
                if (c.InputBeforeSnapshot)
                {
                    if (hitchFrame && nextSend < now)
                    {
                        // A stalled client sends nothing during the stall and then resumes its cadence
                        // (InputCadence resynchronises rather than emitting the backlog at once).
                        nextSend = now;
                    }

                    if (now >= nextSend)
                    {
                        nextSend += 1.0 / c.SendHz + Jitter(rng, c.SendJitterSeconds);
                        Autopilot(c, now, out float mx, out float my);
                        inputTick++;
                        uplink.Add(new InFlightInput
                        {
                            Arrival = now + c.UplinkSeconds + Jitter(rng, c.NetworkJitterSeconds),
                            Tick = inputTick,
                            MoveX = mx,
                            MoveY = my,
                        });
                        steering.NoteInputSent(inputTick, now);
                        predictor.RecordInput(inputTick, mx, my);
                    }
                }

                // ── Client: snapshots that have arrived, newest only (a binder merges them) ──
                double visibleUntil = c.SnapshotsVisibleNextFrame ? now - frame : now;
                int newestIndex = -1;
                for (int i = 0; i < downlink.Count; i++)
                {
                    if (downlink[i].Arrival > visibleUntil) continue;
                    if (newestIndex < 0 || downlink[i].Tick > downlink[newestIndex].Tick) newestIndex = i;
                }

                if (newestIndex >= 0)
                {
                    InFlightSnapshot s = downlink[newestIndex];
                    downlink.RemoveAll(x => x.Arrival <= visibleUntil);

                    if (s.Tick > lastAppliedSnapshotTick)
                    {
                        lastAppliedSnapshotTick = s.Tick;
                        bool counted = seeded && now >= c.WarmupSeconds;
                        ApplySnapshot(c, predictor, steering, s, now, ref lastAck, ref seeded, out bool reconciled);

                        if (reconciled && counted)
                        {
                            float correction = predictor.LastCorrection;
                            result.Reconciles++;
                            if (correction > 0f) result.NonzeroCorrections++;
                            correctionSum += correction;
                            if (correction > result.MaxCorrection) result.MaxCorrection = correction;
                            if (correction >= result.StepLength) result.CorrectionsAtLeastOneStep++;
                            if (correction >= result.StepLength * 3.999f) result.CorrectionsAtLeastFourSteps++;
                            result.CorrectionLog.Add((now, correction, predictor.TickError));
                            if (predictor.AckOffsetSamples > 0)
                            {
                                long offset = predictor.AckTickOffset;
                                if (offset < result.MinAckTickOffset) result.MinAckTickOffset = offset;
                                if (offset > result.MaxAckTickOffset) result.MaxAckTickOffset = offset;
                            }
                        }
                    }
                }

                // ── Client: send an input on its own cadence ──
                if (!c.InputBeforeSnapshot)
                {
                    if (hitchFrame && nextSend < now)
                    {
                        // A stalled client sends nothing during the stall and then resumes its cadence
                        // (InputCadence resynchronises rather than emitting the backlog at once).
                        nextSend = now;
                    }

                    if (now >= nextSend)
                    {
                        nextSend += 1.0 / c.SendHz + Jitter(rng, c.SendJitterSeconds);
                        Autopilot(c, now, out float mx, out float my);
                        inputTick++;
                        uplink.Add(new InFlightInput
                        {
                            Arrival = now + c.UplinkSeconds + Jitter(rng, c.NetworkJitterSeconds),
                            Tick = inputTick,
                            MoveX = mx,
                            MoveY = my,
                        });
                        steering.NoteInputSent(inputTick, now);
                        predictor.RecordInput(inputTick, mx, my);
                    }
                }

                double reported = c.DeltaTimeLagsOneFrame ? previousFrame : frame;
                previousFrame = frame;
                predictor.Advance((float)(c.MaxFrameDeltaSeconds > 0.0 && reported > c.MaxFrameDeltaSeconds
                    ? c.MaxFrameDeltaSeconds
                    : reported));

                // ── What the player sees ──
                Vec2 shown = predictor.Position;
                Autopilot(c, now, out float ax, out float ay);
                moving = ax != 0f || ay != 0f;
                // The hitched frame is excluded: time really passed, so the player really moved.
                if (havePrevious && now >= c.WarmupSeconds && moving && !hitchFrame)
                {
                    float dx = shown.X - previous.X, dy = shown.Y - previous.Y;
                    double motion = Math.Sqrt(dx * dx + dy * dy);
                    double normal = c.Speed * c.FrameSeconds;
                    double ratio = motion / normal;
                    result.MovingFrames++;
                    if (ratio > result.MaxFrameMotionRatio) result.MaxFrameMotionRatio = ratio;
                    if (motion > result.MaxFrameStep) result.MaxFrameStep = motion;
                    if (ratio > 1.5) result.FramesOverOneAndAHalf++;
                }

                previous = shown;
                havePrevious = true;
            }

            result.MeanCorrection = result.Reconciles > 0 ? correctionSum / result.Reconciles : 0.0;
            result.FinalAckTickOffset = predictor.AckTickOffset;
            result.FinalTickError = predictor.TickError;
            result.FinalClientLeadTicks = predictor.BaseTick - server.Tick;
            result.HardResyncs = predictor.HardResyncs;
            result.Snaps = predictor.Snaps;
            result.HistoryMisses = predictor.HistoryMisses;
            result.FinalClockRateScale = predictor.ClockRateScale;
            result.ClampedFrames = predictor.ClampedFrames;
            result.StallResyncs = predictor.StallResyncs;
            result.FinalSkewPpm = steering.Staleness.SkewPpm;
            if (result.MinAckTickOffset == long.MaxValue) result.MinAckTickOffset = 0;
            if (result.MaxAckTickOffset == long.MinValue) result.MaxAckTickOffset = 0;
            return result;
        }

        private static void ApplySnapshot(
            Config c, LocalMovePredictor predictor, PredictionClockSteering steering, InFlightSnapshot s,
            double now, ref long lastAck, ref bool seeded, out bool reconciled)
        {
            reconciled = false;

            switch (c.Mode)
            {
                case ClientMode.TwoArgumentNoSteering:
                    // The DOTS system as it was: seed once, reconcile only on a NEW ack, with no
                    // snapshot tick and no steering.
                    predictor.SeedBaseTick(s.Tick);
                    if (!seeded)
                    {
                        predictor.Reconcile(s.Position, s.AckTick);
                        seeded = true;
                        lastAck = s.AckTick;
                        return;
                    }

                    if (s.AckTick > lastAck)
                    {
                        lastAck = s.AckTick;
                        predictor.Reconcile(s.Position, s.AckTick);
                        reconciled = true;
                    }

                    return;

                case ClientMode.MeasuredSteering:
                    steering.SampleTickRate(s.Tick, now);
                    steering.OnSnapshot(predictor, s.Tick, s.AckTick, now);
                    break;

                default:
                    predictor.SeedBaseTick(s.Tick);
                    predictor.SteerToServerTick(s.Tick, c.LeadTicks);
                    break;
            }

            long applied = c.Mode == ClientMode.SnapshotTickOnly ? 0L : s.AckAppliedTick;
            predictor.Reconcile(s.Position, s.AckTick, s.Tick, applied);
            reconciled = seeded;
            seeded = true;
        }

        /// <summary>The input a player would be holding at <paramref name="now"/>.</summary>
        private static void Autopilot(Config c, double now, out float moveX, out float moveY)
        {
            if (c.Motion == Motion.Circle)
            {
                double omega = c.Speed / c.CircleRadius;
                double angle = omega * now;
                moveX = (float)Math.Cos(angle);
                moveY = (float)Math.Sin(angle);
                return;
            }

            bool go = ((long)Math.Floor(now / c.StartStopPeriodSeconds) & 1L) == 0L;
            moveX = go ? 1f : 0f;
            moveY = 0f;
        }

        /// <summary>When the server tick due at <paramref name="due"/> actually runs.</summary>
        private static double ServerRunsAt(Config c, double due)
        {
            if (c.ServerStallSeconds > 0.0 && due >= c.ServerStallAtSeconds
                && due < c.ServerStallAtSeconds + c.ServerStallSeconds)
            {
                return c.ServerStallAtSeconds + c.ServerStallSeconds;
            }

            return due;
        }

        private static double Jitter(Random rng, double amplitude) =>
            amplitude <= 0.0 ? 0.0 : (rng.NextDouble() * 2.0 - 1.0) * amplitude;
    }
}
