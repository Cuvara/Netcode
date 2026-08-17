using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Prediction;
using Cuvara.Netcode.Transport;
using Cuvara.Netcode.View;
using Shared.GameLogic.Components;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cuvara.Netcode.Tests.PlayMode
{
    /// <summary>
    /// Measures the interval client-side prediction removes between submitting an input
    /// and the local avatar visibly moving, against a live backend.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this does NOT measure, stated first so the number is not overclaimed.</b>
    /// It is <b>not</b> keypress-to-visible. A keypress-to-visible figure includes the
    /// keyboard, the OS input stack, and the display pipeline, and measuring it honestly
    /// needs external capture — a high-speed camera or a hardware probe. Nothing running
    /// inside the engine can see those legs, and a number that quietly folded them in
    /// would be a guess wearing a measurement's clothes.
    /// </para>
    /// <para>
    /// What it measures is <b>input-submitted → local avatar moves on screen</b>: from the
    /// frame the client hands an input to the network layer, to the frame the view is told
    /// a changed position for the local entity. That is the whole of the interval
    /// prediction is capable of affecting, and it is the dominant term in the player's
    /// complaint. The legs it excludes are constant between the two configurations, so the
    /// *difference* it reports is unaffected by their absence.
    /// </para>
    ///
    /// <para><b>Method.</b> Each sample:</para>
    /// <list type="number">
    /// <item><description>Hold zero input until the rendered local position is stable, so
    /// any subsequent movement is attributable to one input rather than to the previous
    /// one still arriving.</description></item>
    /// <item><description>Submit one input at tick <c>T</c> with a non-zero direction and
    /// stamp <c>Time.realtimeSinceStartup</c>.</description></item>
    /// <item><description><b>t_visible</b> — the first frame the view is told a local
    /// position differing from the settled one.</description></item>
    /// <item><description><b>t_authoritative</b> — the first snapshot whose
    /// <c>AckTick >= T</c>, i.e. when the server's own answer for that input
    /// lands.</description></item>
    /// </list>
    /// <para>
    /// With prediction, <c>t_visible</c> should be the same frame as the submit. Without
    /// it, <c>t_visible</c> can only be <c>t_authoritative</c>, because nothing else moves
    /// the entity. <b>The difference between the two configurations is what prediction
    /// buys</b>, measured rather than derived.
    /// </para>
    ///
    /// <para><b>Why it cannot run in CI.</b> It needs a gateway, a game server and Nakama.
    /// The CI job runs <c>testMode: EditMode</c> and never executes PlayMode tests, so this
    /// assembly compiles there — which is worth having, it catches breakage — but nothing
    /// here runs. That is a deliberate gap, stated rather than hidden.
    /// </para>
    /// <para>
    /// <b>It does not skip when the backend is down; it fails.</b> A test that quietly
    /// turns green when its dependency is missing is the exact failure this repository has
    /// paid for repeatedly — a suite reporting success while executing nothing. If you run
    /// this without a backend you get a red result naming what was unreachable.
    /// </para>
    /// </remarks>
    [TestFixture]
    [Category("LiveBackend")]
    public sealed class PredictionLatencyMeasurement
    {
        /// <summary>Samples per configuration.</summary>
        /// <remarks>
        /// Enough that the spread is visible rather than one lucky frame. At 15 Hz with a
        /// settle window between samples this is roughly 20 s per configuration.
        /// </remarks>
        private const int Samples = 20;

        /// <summary>
        /// Times each configuration is measured. The median run is reported and the
        /// spread printed beside it.
        /// </summary>
        /// <remarks>
        /// Three, because two cannot distinguish an outlier from a trend and the run
        /// already takes long enough that the backend's own state drifts across it. The
        /// spread matters more than the middle: a metric whose runs disagree by more than
        /// the change being investigated cannot settle the question, and reporting one
        /// number invites exactly that mistake.
        /// </remarks>
        private const int Repeats = 3;

        /// <summary>Frames of zero input before a sample, to settle.</summary>
        private const int SettleTicks = 6;

        /// <summary>Give up on a sample after this long and report it, rather than hang.</summary>
        private const float SampleTimeoutSeconds = 3f;

        private const float MoveEpsilon = 0.001f;

        // How the divergence run forces a disagreement, and the two ways that do NOT work:
        //
        //   A wrong SPEED does not survive. The wire carries per-entity speed (field 9)
        //   and the binder feeds it to SetServerSpeed every snapshot, so the error is
        //   corrected back within one snapshot.
        //
        //   DROPPING the input does not diverge at all, which cost a live run to learn.
        //   An input that is never sent is never acknowledged, so it is never removed
        //   from the pending buffer, so every reconcile replays it on top of the
        //   authoritative position and reproduces the prediction exactly. Correction is
        //   zero because nothing HAS diverged: from the client's side a dropped input is
        //   indistinguishable from one still in flight, which is what it is.
        //
        // What works is sending a DIFFERENT vector than the one predicted. The server
        // acknowledges the tick — so the input leaves the buffer — having moved somewhere
        // else, and the disagreement is real and permanent.

        private sealed class Sample
        {
            public float InputToVisibleMs;
            public float InputToAuthoritativeMs;
            public bool VisibleTimedOut;
            public bool AuthoritativeTimedOut;
        }

        private sealed class Run
        {
            public string Name;
            public List<Sample> Samples = new List<Sample>();
            public int PendingPeak;
            public int ReplayedSteps;
            public int Reconciles;
            public int TickRateInUse;
            public bool TickRateIsFallback;
            public float MeasuredTickRate;
            public bool TickRateDisagrees;

            /// <summary>Distinct positions the view was told for the local entity.</summary>
            /// <remarks>
            /// One means the entity never moved at all. Separates "the server did not
            /// move it" from "the harness did not notice", which otherwise report
            /// identically.
            /// </remarks>
            public int DistinctPositions;

            /// <summary>Hold window the predictor derived, for reading the clock error.</summary>
            public int HoldTicksInUse;

            /// <summary>Times SetState was called for any entity.</summary>
            public int SetStateCalls;

            /// <summary>Wall-clock length of each sample's observation window, seconds.</summary>
            /// <remarks>
            /// <b>The window is not a constant and it is not the hold window.</b> A sample
            /// runs until the predicted move is visible AND the server has acknowledged the
            /// tick, so its length is set by the acknowledgement. The motion it contains is
            /// set by the hold window — <c>HoldTicks</c> base ticks, ~67 ms at 60/15 — and
            /// nothing refreshes the hold afterwards, because a sample sends exactly one
            /// input. Every base tick past the fourth therefore hits <c>ApplyHeld</c>'s
            /// expiry guard, which is a still run a base tick long.
            /// <para>
            /// So the ratio of these two durations is a term in
            /// <see cref="StillFramePercent"/> whether or not any rendering is at fault,
            /// and it has to be visible rather than derived from the counters afterwards.
            /// </para>
            /// </remarks>
            public readonly System.Collections.Generic.List<float> SampleSeconds =
                new System.Collections.Generic.List<float>();

            /// <summary>Samples whose acknowledgement never arrived before the timeout.</summary>
            /// <remarks>
            /// Printed on its own line because a timed-out sample is three seconds of
            /// almost entirely still frames — at ~1000 fps that is 3000 frames, more than
            /// twenty ordinary samples put together, so a single one dominates every
            /// frame-derived figure in this report.
            /// </remarks>
            public int AuthoritativeTimeouts =>
                Samples.Count(x => x.AuthoritativeTimedOut);

            /// <summary>Wall-clock gaps between consecutive input sends, seconds.</summary>
            /// <remarks>
            /// The server discards inputs that clump into one tick along with the
            /// simulated time they carried (rpg-mmo-server#100), losing up to 46% of
            /// movement at 60/15/5. A client sending unevenly is therefore legitimately
            /// outrun by its own prediction. Measured rather than assumed even, because
            /// "the sends are evenly spaced" is exactly the sort of thing this harness
            /// has now been wrong about twice.
            /// </remarks>
            public readonly System.Collections.Generic.List<float> SendGaps =
                new System.Collections.Generic.List<float>();

            public float SendGapBurstiness
            {
                get
                {
                    if (SendGaps.Count < 2) return float.NaN;
                    float mean = SendGaps.Average();
                    return mean > 0f ? SendGaps.Max() / mean : float.NaN;
                }
            }

            /// <summary>Displacement one accepted input produces on the server.</summary>
            public float ExpectedStep =>
                TickRateInUse > 0 && !float.IsNaN(EffectiveSpeed)
                    ? EffectiveSpeed / TickRateInUse
                    : 0f;
            public int Snaps;
            public int SmoothedCorrections;
            public float MaxCorrection;
            public float EffectiveSpeed;
            public bool Predicting;

            /// <summary>
            /// Per-render-frame movement of the rendered local position, while an input
            /// is in flight. This is the stutter, quantified: at 15 Hz input and a high
            /// frame rate, unsmoothed motion is a long run of exact zeros punctuated by
            /// one whole step, so <see cref="StillFramePercent"/> near 100 and a large
            /// <see cref="MaxFrameDelta"/> are the signature. Smooth motion is a small,
            /// near-constant delta every frame.
            /// </summary>
            public List<float> FrameDeltas = new List<float>();

            /// <summary>Wall-clock length of each sampled frame, for the fps context.</summary>
            public List<float> FrameSeconds = new List<float>();

            /// <summary>
            /// The sampled frames with each sample's trailing run of still frames removed.
            /// <b>Diagnostic only — nothing asserts on this.</b> See
            /// <see cref="TailFramesTrimmed"/>.
            /// </summary>
            public List<float> FrameDeltasWhileMoving = new List<float>();

            /// <summary>
            /// Frames on which the harness's own frame clock reported a non-positive delta,
            /// so <c>AdvanceFrame</c> was handed a zero and returned without advancing
            /// anything.
            /// </summary>
            /// <remarks>
            /// <para>
            /// <b>This is a property of the harness, not of the renderer.</b>
            /// <c>WorldViewBinder.AdvanceFrame</c> and <c>LocalMovePredictor.Advance</c>
            /// both early-out on <c>deltaTime &lt;= 0f</c>, so such a frame leaves the
            /// rendered position bit-identical and contributes an <i>exact</i> zero to
            /// <see cref="FrameDeltas"/> — which is what a still frame is defined as.
            /// </para>
            /// <para>
            /// The harness used to build that delta by subtracting two readings of
            /// <c>Time.realtimeSinceStartup</c>, <b>which is a <c>float</c></b>. Its
            /// resolution is not fixed: the representable spacing at a value <c>v</c> is
            /// <c>2^(exponent(v) - 23)</c>, so it coarsens as the process runs. Past 4.5
            /// hours of uptime the spacing reaches ~1.95 ms, and a loop running at ~1000
            /// fps then reads the same instant twice on roughly half its iterations. Half
            /// the frames get a zero delta, half get a double one, the mean frame time —
            /// and therefore the reported fps — stays exactly right, and the still-frame
            /// figure sits near 50% no matter what the rendering code does.
            /// </para>
            /// <para>
            /// Nothing in the runtime does this. <c>WorldViewBinder</c> keeps a
            /// <c>Stopwatch</c>, and the sample bridge passes <c>Time.deltaTime</c>. Only
            /// the harness rolled its own clock, which is why the effect appears in a
            /// measurement and not in play.
            /// </para>
            /// </remarks>
            public int NonAdvancingFrames;

            /// <summary>
            /// Lengths of the unbroken runs of still frames that occurred while the hold
            /// was active — the runs behind <see cref="RenderingFaultPercent"/>.
            /// </summary>
            /// <remarks>
            /// <b>The distribution says what the residual is; the count never can.</b>
            /// Twenty single-frame runs and three seven-frame runs give a similar
            /// percentage and mean completely different things: the first is one isolated
            /// frame somewhere periodic, the second is a real freeze the interpolation is
            /// not covering.
            /// </remarks>
            public readonly System.Collections.Generic.List<int> ActiveStillRuns =
                new System.Collections.Generic.List<int>();

            /// <summary>Histogram of <see cref="ActiveStillRuns"/> as "1:n 2:n 3:n 4-7:n 8+:n".</summary>
            public string ActiveStillRunHistogram
            {
                get
                {
                    if (ActiveStillRuns.Count == 0) return "none";

                    int ones = ActiveStillRuns.Count(r => r == 1);
                    int twos = ActiveStillRuns.Count(r => r == 2);
                    int threes = ActiveStillRuns.Count(r => r == 3);
                    int mid = ActiveStillRuns.Count(r => r >= 4 && r <= 7);
                    int big = ActiveStillRuns.Count(r => r >= 8);
                    return $"1:{ones}  2:{twos}  3:{threes}  4-7:{mid}  8+:{big}  " +
                           $"(longest {ActiveStillRuns.Max()})";
                }
            }

            /// <summary>
            /// Frames read before any time had been advanced in that sample, and therefore
            /// not recorded.
            /// </summary>
            /// <remarks>
            /// <para>
            /// The sampling loop reads the rendered position at the top of an iteration and
            /// calls <c>AdvanceFrame</c> at the bottom, so iteration <i>n</i>'s reading is
            /// the position as of iteration <i>n-1</i>'s advance. That is a correct
            /// one-frame delta for every iteration but the first, which has no advance
            /// behind it at all: it reads the position as it stood the instant
            /// <c>RecordInput</c> returned, against a baseline captured moments earlier.
            /// </para>
            /// <para>
            /// <b>That reading is necessarily zero, and it is right that it is.</b>
            /// <c>RecordInput</c> deliberately preserves the rendered position across an
            /// input — it folds the unshown remainder into <c>_renderOffset</c>
            /// (<c>LocalMovePredictor.cs:537-541</c>) precisely so the avatar does not jump
            /// on an input boundary. No time has passed, so no movement is correct.
            /// </para>
            /// <para>
            /// Recording it counted one guaranteed still frame per sample: 20 of the 20
            /// that the rendering-fault figure reported. This is not a tail being trimmed —
            /// it is declining to measure a velocity over an interval of zero length.
            /// Counted and printed so the exclusion is auditable, and it should always
            /// equal the sample count exactly; anything else means the loop structure has
            /// changed underneath this reasoning.
            /// </para>
            /// </remarks>
            public int ZeroDurationFramesSkipped;

            /// <summary>Largest delta handed to <c>AdvanceFrame</c>, seconds.</summary>
            public float LargestAdvanceSeconds;

            /// <summary>
            /// Calls to <c>AdvanceFrame</c> with a delta wider than one base tick.
            /// </summary>
            /// <remarks>
            /// Each one steps the predictor more than once inside a single call, and a
            /// delta of several ticks retires the whole hold window before a frame is
            /// rendered against it — which empties the denominator of
            /// <see cref="RenderingFaultPercent"/> rather than changing its numerator.
            /// Always a fault in whatever drives the clock, never in the predictor.
            /// </remarks>
            public int OversizedAdvances;

            /// <summary>Smallest non-zero frame time the harness clock could resolve, seconds.</summary>
            /// <remarks>
            /// The clock's actual granularity, measured rather than assumed. If this is of
            /// the same order as the mean frame time, the frame deltas are being quantised
            /// by the clock and no smoothness figure taken from them means anything.
            /// </remarks>
            public float SmallestFrameSeconds = float.NaN;

            /// <summary>
            /// Still frames on which the rendered step had already been fully shown
            /// (<c>RenderStepProgress</c> saturated at 1) — the avatar had caught up with
            /// its own simulation and had nothing left to render.
            /// </summary>
            /// <remarks>
            /// <b>This is the discriminator.</b> A still frame has exactly two possible
            /// explanations and they need opposite fixes:
            /// <list type="bullet">
            /// <item><description><b>Saturated</b> — the step was spread over a span
            /// shorter than the interval between steps, so the render finishes early and
            /// holds. The span is wrong, or the steps are arriving further apart than the
            /// span assumes. Read <see cref="SmoothingSpanMs"/> against
            /// <see cref="HeldStepsPerBaseTick"/>.</description></item>
            /// <item><description><b>Not saturated</b> — the render still had step left to
            /// show and did not show it. That is not a span problem at all and points at
            /// the position never reaching the view.</description></item>
            /// </list>
            /// </remarks>
            public int StillFramesStepSaturated;

            /// <summary>Still frames on which the hold window had already expired.</summary>
            /// <remarks>
            /// Separates "the predictor has legitimately stopped integrating, exactly as
            /// the server has" from "the predictor is still integrating and the render is
            /// not following it". Only the second is a rendering fault.
            /// </remarks>
            public int StillFramesHoldExpired;

            /// <summary>Still frames while the hold window was still running.</summary>
            public int StillFramesHoldActive;

            /// <summary>
            /// Frames sampled while the hold window was running — the frames on which the
            /// predictor was still integrating and the rendered position was therefore
            /// supposed to be moving.
            /// </summary>
            /// <remarks>
            /// <b>This is the honest denominator</b>, and picking it is the whole point.
            /// The old figure divided by every sampled frame, which folded in the frames
            /// after the hold expired — where the predictor has correctly stopped, exactly
            /// as the server has, and a motionless avatar is the right answer. A live run
            /// split 437 still frames into 417 of those and 20 genuine ones, so ~95% of
            /// the number being asserted on was measuring correct behaviour.
            /// </remarks>
            public int FramesHoldActive;

            /// <summary>
            /// Share of the frames that should have been moving on which the avatar did
            /// not move. <b>This is the rendering fault, isolated.</b>
            /// </summary>
            /// <remarks>
            /// Perfect per-frame interpolation makes this exactly zero, not merely small:
            /// while the hold is running there is a step in flight on every frame, so
            /// every frame has something to show. Anything above zero is a frame the
            /// renderer had motion available and did not render.
            /// </remarks>
            public float RenderingFaultPercent =>
                FramesHoldActive == 0 ? float.NaN
                    : 100f * StillFramesHoldActive / FramesHoldActive;

            /// <summary>Integration timestep the predictor is using, milliseconds.</summary>
            public float IntegrationTimestepMs = float.NaN;

            /// <summary>Span one step is spread across, milliseconds.</summary>
            public float SmoothingSpanMs = float.NaN;

            /// <summary>Measured interval between consecutive steps, milliseconds.</summary>
            public float StepIntervalMs = float.NaN;

            /// <summary>Gaps <c>NoteStep</c> accepted into the moving average.</summary>
            public int StepIntervalSamples;

            /// <summary>Gaps <c>NoteStep</c> rejected as a pause, zeroing the interval.</summary>
            public int StepIntervalResets;

            /// <summary>Observed interval between inputs, milliseconds.</summary>
            public float InputIntervalMs = float.NaN;

            /// <summary>Base ticks the predictor stepped, inside the sample windows only.</summary>
            /// <remarks>
            /// Scoped to the windows the frame deltas come from. Counting across the whole
            /// run would fold in the 1.5 s spawn pump and the settle phases — where the
            /// avatar is deliberately stationary and the hold is deliberately off — and
            /// the ratio would then describe the harness's idle time rather than the
            /// measured motion.
            /// </remarks>
            public int BaseTicksAdvanced;

            /// <summary>Base ticks on which the held direction actually moved.</summary>
            public int HeldStepsApplied;

            /// <summary>
            /// Base ticks the hold declined, by reason, inside the sample windows.
            /// </summary>
            /// <remarks>
            /// <b>A declined tick is a still run one base tick long.</b> On such a tick
            /// <c>_predicted</c> does not move and <c>_sinceInput</c> is not re-armed, so
            /// <c>StepProgress</c> stays saturated and <c>Position</c> is bit-identical for
            /// the tick's whole duration — 16.8 frames of it at 1007 fps. The reasons are
            /// counted apart because they need opposite responses: an expiry says the hold
            /// window is too short for the input cadence, an already-stepped tick is rule 1
            /// working, and a refusal or a zero displacement says the movement model
            /// declined a vector the server would have accepted.
            /// </remarks>
            public int SkipExpired;

            public int SkipNothingHeld;
            public int SkipInputAlreadyStepped;
            public int SkipNoHoldWindow;
            public int SkipRefusedByMovementModel;
            public int SkipNoDisplacement;

            /// <summary>Base ticks inside the sample windows on which the hold declined.</summary>
            public int TicksSkipped =>
                SkipExpired + SkipNothingHeld + SkipInputAlreadyStepped +
                SkipNoHoldWindow + SkipRefusedByMovementModel + SkipNoDisplacement;

            /// <summary>
            /// Held steps per base tick. The server integrates the held direction on every
            /// base tick inside the window; a value well below 1 means the rendered
            /// position is being given a fresh step far less often than the span assumes.
            /// </summary>
            public float HeldStepsPerBaseTick =>
                BaseTicksAdvanced == 0 ? float.NaN
                    : (float)HeldStepsApplied / BaseTicksAdvanced;

            /// <summary>Longest unbroken run of still frames, in frames.</summary>
            /// <remarks>
            /// <b>This is the number that identifies the cadence</b>, and it is the one the
            /// percentage cannot give you. A given still-frame percentage is produced by
            /// many different arrangements, and they have different causes:
            /// <list type="bullet">
            /// <item><description>runs of <b>1–2</b> frames, thousands of them — the
            /// rendered position is advancing on a cadence a little slower than the frame
            /// rate. At ~1000 fps that is the ~500 Hz band, and the only thing in this code
            /// path with that period is the harness's own clock granularity (see
            /// <see cref="NonAdvancingFrames"/>).</description></item>
            /// <item><description>runs of ~<c>fps / tickRate</c> — the rendered position
            /// advances once per simulation tick and interpolation is not reaching the
            /// view at all. This is the defect the assertion exists to catch.</description></item>
            /// <item><description>one long run per sample, at the end — the avatar is at
            /// rest because the hold window expired, and the sample is still running
            /// because it is waiting for an acknowledgement (see
            /// <see cref="TailFramesTrimmed"/>).</description></item>
            /// </list>
            /// </remarks>
            public int LongestStillRun;

            /// <summary>Number of separate runs of still frames.</summary>
            public int StillRunCount;

            /// <summary>Mean length of a still run, in frames.</summary>
            public float MeanStillRun =>
                StillRunCount == 0 ? 0f
                    : (float)FrameDeltas.Count(d => d <= 1e-6f) / StillRunCount;

            /// <summary>
            /// Frames dropped from the end of samples because the motion the input
            /// produced had already finished.
            /// </summary>
            /// <remarks>
            /// <para>
            /// <b>Why a tail exists at all, and why counting it is wrong.</b> A sample
            /// sends ONE input and then watches until both the predicted move is visible
            /// and the server has acknowledged the tick. Those two events have nothing to
            /// do with each other in duration. The motion lasts exactly one hold window —
            /// the server integrates a held direction for <c>WorldEvery</c> base ticks and
            /// then stops (<c>LocalMovePredictor.ApplyHeld</c>, the
            /// <c>baseTick - heldFrom &gt;= HoldTicks</c> guard), which is one snapshot
            /// interval, ~67 ms at 60 Hz base / 15 Hz world. The <i>watch</i> lasts until
            /// the acknowledgement comes back, which is round-trip time plus up to a
            /// snapshot interval, and against a remote game server that is comfortably
            /// longer than the motion.
            /// </para>
            /// <para>
            /// So every sample ends with a run of frames on which the avatar is
            /// <b>correctly</b> stationary: the input's motion is over, the server has
            /// stopped moving it too, and the predictor agrees with the server (which is
            /// why <see cref="Snaps"/> and <see cref="SmoothedCorrections"/> stay near zero
            /// through exactly these frames).
            /// </para>
            /// <para>
            /// <b>That tail is NOT the explanation for a ~50% figure, and this count is not
            /// subtracted from anything.</b> The arithmetic does not support it: the motion
            /// lasts ~67 ms, while a same-host acknowledgement costs at most one base tick
            /// plus one world interval — under 50 ms on average, i.e. <i>shorter</i> than
            /// the motion. A localhost control run measured 51.6% anyway, which the tail
            /// cannot produce. It is reported because it is a real and previously invisible
            /// component of the figure, and because the size of the gap between
            /// <see cref="StillFramePercent"/> and
            /// <see cref="StillFramePercentWhileMoving"/> is itself evidence: if they are
            /// close, essentially none of the still frames are the at-rest tail and all of
            /// them are inside the motion, where a renderer is supposed to be moving.
            /// <see cref="LongestStillRun"/> is what actually names the cause.
            /// </para>
            /// </remarks>
            public int TailFramesTrimmed;

            /// <summary>
            /// Share of sampled frames on which the avatar did not move. <b>This is the
            /// asserted figure, over every frame of every sample.</b>
            /// </summary>
            public float StillFramePercent =>
                FrameDeltas.Count == 0 ? float.NaN
                    : 100f * FrameDeltas.Count(d => d <= 1e-6f) / FrameDeltas.Count;

            /// <summary>
            /// <see cref="StillFramePercent"/> with each sample's trailing at-rest run
            /// excluded. <b>Diagnostic, not asserted.</b> The gap between the two says how
            /// much of the figure is the avatar correctly at rest waiting for an
            /// acknowledgement, and how much is frames inside the motion — which is the
            /// half that would be a rendering fault.
            /// </summary>
            public float StillFramePercentWhileMoving =>
                FrameDeltasWhileMoving.Count == 0 ? float.NaN
                    : 100f * FrameDeltasWhileMoving.Count(d => d <= 1e-6f)
                        / FrameDeltasWhileMoving.Count;

            public float MaxFrameDelta => FrameDeltas.Count == 0 ? float.NaN : FrameDeltas.Max();

            public float MeanFrameDelta => FrameDeltas.Count == 0 ? float.NaN : FrameDeltas.Average();

            /// <summary>
            /// Worst frame divided by the average frame — <b>the frame-rate-independent
            /// smoothness number</b>, and the one to quote.
            /// </summary>
            /// <remarks>
            /// <para>
            /// <b>1.0 is perfect</b>: every frame moved the same distance. Unsmoothed
            /// motion puts a whole step on one frame and nothing on the rest, so the ratio
            /// is the number of frames per input interval.
            /// </para>
            /// <para>
            /// <b>Why not quote the raw distance.</b> Two runs of the same build reported a
            /// largest single-frame jump of 0.0149 and 0.0244 world units — a 60% spread
            /// that looks like measurement noise and is not. It is frame rate: those values
            /// imply 336 fps and 205 fps, and a smoothed step necessarily divides into
            /// larger pieces when there are fewer frames to divide it across. The raw
            /// figure is not comparable between runs, or between machines, or between a
            /// developer's Editor and a player's build. This ratio is.
            /// </para>
            /// </remarks>
            public float FrameDeltaBurstiness =>
                FrameDeltas.Count == 0 || MeanFrameDelta <= 0f ? float.NaN
                    : MaxFrameDelta / MeanFrameDelta;

            /// <summary>Observed render rate during the sampled frames, for context.</summary>
            public float ObservedFps { get; set; }

            /// <summary>Standard deviation of the per-frame movement — flat when smooth.</summary>
            public float FrameDeltaStdDev
            {
                get
                {
                    if (FrameDeltas.Count < 2) return float.NaN;
                    float mean = MeanFrameDelta;
                    double sum = FrameDeltas.Sum(d => (d - mean) * (double)(d - mean));
                    return (float)System.Math.Sqrt(sum / (FrameDeltas.Count - 1));
                }
            }
        }

        [UnityTest]
        public IEnumerator InputToVisibleMovement_WithAndWithoutPrediction() => UniTask.ToCoroutine(async () =>
        {
            // Skip, loudly, when there is nothing to measure against.
            //
            // The [Category] attribute is not enough on its own: a consuming project runs
            // the whole PlayMode suite without filtering by category, so the gate does
            // nothing there and this failed their CI with "Cannot connect to destination
            // host". A package cannot rely on a consumer's runner passing the right
            // filter — correctness has to live in the test.
            //
            // Ignore, not silent-pass: an ignored test with a reason is visible in the
            // report and names what is missing. A test that quietly goes green by doing
            // nothing is the failure this repository has spent two days eliminating, and
            // it is not being reintroduced in the one place whose job is honest numbers.
            string unreachable = await FirstUnreachableAsync();
            if (unreachable != null)
            {
                Assert.Ignore(
                    unreachable + ". This measurement needs a live backend and does not " +
                    "run in CI; start the stack and run it locally, or select/exclude it " +
                    "by its 'LiveBackend' category. " + LiveBackendConfig.Describe());
            }

            Debug.Log("[Measure] endpoints: " + LiveBackendConfig.Describe() +
                      "\n[Measure] NOTE: the tickRate above is only a FALLBACK. The rate " +
                      "actually predicted with comes from the server and is reported per run below.");

            // Each configuration is measured Repeats times, interleaved, and the spread
            // is reported.
            //
            // One sample per configuration cannot tell a regression from a noisy metric,
            // and this one is noisy: a run where nothing in the non-predicting path had
            // changed still moved its burstiness by a third. Interleaved rather than
            // batched because the machine and the backend drift over the length of a run,
            // and batching would put that drift entirely into whichever configuration
            // went last.
            var predictedRuns = new List<Run>();
            var unpredictedRuns = new List<Run>();

            for (var r = 0; r < Repeats; r++)
            {
                predictedRuns.Add(await MeasureAsync(predict: true));
                unpredictedRuns.Add(await MeasureAsync(predict: false));
            }

            Run withPrediction = Representative(predictedRuns);
            Run withoutPrediction = Representative(unpredictedRuns);

            ReportSpread("prediction ON", predictedRuns);
            ReportSpread("prediction OFF", unpredictedRuns);

            // A third run whose only purpose is to make the predictor WRONG, so the
            // correction machinery has something to correct. Without it the healthy run's
            // 0.0000 is unfalsifiable: a predictor that never corrects and one that never
            // needs to are indistinguishable.
            //
            // Its TIMINGS are not comparable with the other two and are not used in the
            // comparison — the avatar is being deliberately mispredicted. Only its
            // MaxCorrection is read.
            var diverging = await MeasureAsync(predict: true, forceDivergence: true);

            Report(withPrediction);
            Report(withoutPrediction);
            Report(diverging);
            ReportComparison(withPrediction, withoutPrediction);

            // ---- Guards that make the numbers mean something ----

            Assert.That(withPrediction.Predicting, Is.True,
                "the predictor refused to run, so the 'with prediction' run measured the " +
                "same code path as the other one. Check tick rate and speed in " +
                "LiveBackendConfig.");

            // A predictor that never reconciles is indistinguishable from a perfectly
            // accurate one by position alone: both just look right. These counters are the
            // only evidence that the loop closed at all.
            Assert.That(withPrediction.PendingPeak, Is.GreaterThan(0),
                "no input was ever pending acknowledgement — inputs are not reaching the " +
                "buffer, so nothing was predicted ahead of the server.");
            Assert.That(withPrediction.ReplayedSteps, Is.GreaterThan(0),
                "no input was ever replayed after a reconcile. Prediction ran open-loop: " +
                "it never rewound to an authoritative position, so its agreement with the " +
                "server is untested and this measurement proves nothing about " +
                "reconciliation.");

            // NOT asserted: MaxCorrection > 0 on the healthy run.
            //
            // The first live run failed exactly that assertion, and the assertion was
            // wrong. On localhost, with no loss and Shared.GameLogic bit-exact on both
            // sides, a correction of 0.0000 is the DESIGNED outcome — it is what ADR-10,
            // the FMA-denying split in Integrate and the golden vectors are all for. The
            // same run disproved the assertion's own diagnosis: `replayed steps 3` means
            // Reconcile fired, which is precisely what open-loop cannot do.
            //
            // "Is reconciliation alive?" is answered by ReplayedSteps, asserted above.
            // "Do the two sides disagree?" is what LastCorrection answers, and on a
            // lossless link the healthy answer is no. Conflating them cost a red run.
            //
            // What that assertion was reaching for is covered instead by the deliberate
            // divergence configuration below, and by ReconciliationDivergenceTests in
            // EditMode, which pins all four readings without needing a backend.

            Assert.That(withPrediction.EffectiveSpeed, Is.EqualTo(LiveBackendConfig.PlayerSpeed).Within(0.001f),
                $"the speed replay used ({withPrediction.EffectiveSpeed}) does not match the " +
                $"server's ({LiveBackendConfig.PlayerSpeed}). Either the server's default " +
                "changed or the wire speed is not being adopted — both make every predicted " +
                "step wrong by the ratio.");

            // ---- The other half of the correction guard ----
            //
            // The divergence check below proves corrections CAN happen. Nothing proved they
            // do NOT happen when nothing should diverge, and that missing half let the
            // harness report `corrections smoothed 20` out of 20 samples — every input
            // diverging by 0.25 units, under the snap threshold, invisible — while
            // measuring a 4x tick-rate mismatch it had introduced itself.
            //
            // On localhost with matched rates and bit-exact shared logic, a healthy run
            // should correct essentially never.

            // Smoothing has to earn its place. Predicted motion being LESS even than
            // unpredicted motion is a regression in the one metric closest to what the
            // user reports, whatever the latency numbers say.
            //
            // Gated on the spread: this comparison is only meaningful when the runs of a
            // single configuration agree more closely than the two configurations differ.
            // Asserting through a noisy metric manufactures both regressions and fixes.
            float onSpread = Spread(predictedRuns);
            float offSpread = Spread(unpredictedRuns);
            float worstSpread = Math.Max(onSpread, offSpread);
            float gap = Math.Abs(withPrediction.FrameDeltaBurstiness - withoutPrediction.FrameDeltaBurstiness);

            if (!float.IsNaN(worstSpread) && gap > worstSpread)
            {
                Assert.That(withPrediction.FrameDeltaBurstiness,
                    Is.LessThan(withoutPrediction.FrameDeltaBurstiness),
                    $"predicted motion is LESS even than unpredicted: " +
                    $"{withPrediction.FrameDeltaBurstiness:F2} against " +
                    $"{withoutPrediction.FrameDeltaBurstiness:F2}, a gap of {gap:F2} " +
                    $"against a within-configuration spread of {worstSpread:F2}. The gap " +
                    "is larger than the noise, so this is real. Smoothing that makes " +
                    "motion less even than no smoothing is not earning its place.");
            }
            else
            {
                Debug.LogWarning(
                    $"[Measure] burstiness comparison NOT asserted: ON " +
                    $"{withPrediction.FrameDeltaBurstiness:F2} vs OFF " +
                    $"{withoutPrediction.FrameDeltaBurstiness:F2} differ by {gap:F2}, " +
                    $"within the {worstSpread:F2} spread between runs of one " +
                    "configuration. The metric cannot resolve a difference this small; " +
                    "raise Repeats or reduce what else is running on the machine.");
            }

            // Assert on the frames that SHOULD have been moving, not on every sampled
            // frame.
            //
            // A sample sends one input and then watches until the server acknowledges it.
            // The motion that input produces lasts exactly one hold window — ApplyHeld
            // stops at `baseTick - heldFrom >= HoldTicks` — and the watch outlasts it.
            // Every frame after that is the avatar correctly at rest: the predictor has
            // stopped because the server has, which is why corrections stay at zero
            // straight through them. Counting them made the figure a ratio of two
            // unrelated durations. Measured on a live run: 437 still frames, 417 of them
            // after the hold expired and 20 genuine. The old denominator was ~95% noise.
            //
            // The split is not an argument, it is a counter: every still frame is
            // classified at the moment it occurs by whether the hold was still running
            // (Run.StillFramesHoldActive / Run.FramesHoldActive). That is what makes this
            // narrowing honest rather than a tail trimmed on faith — an earlier attempt
            // trimmed one and was right about the shape for entirely the wrong reason.
            //
            // THE BUDGET, and why it is this number.
            //
            // The correct value is EXACTLY ZERO, not "small". While the hold runs there is
            // a step in flight on every frame, so every frame has something to show. Two
            // things could in principle put a frame under the 1e-6 still threshold anyway,
            // and neither survives arithmetic:
            //
            //   A frame that advanced no time. AdvanceFrame early-outs on deltaTime <= 0
            //   and the rendered position is then bit-identical. Counted separately as
            //   Run.NonAdvancingFrames, measured at 0 since the harness clock moved to
            //   realtimeSinceStartupAsDouble, and it would be reported rather than
            //   absorbed here.
            //
            //   A frame whose movement rounds below the threshold. Per frame the render
            //   moves |_step| * dtFrame / span — about 0.0833 * (1/16.7) = 5e-3 world
            //   units at these rates, some 5000x the 1e-6 threshold. Reaching it needs a
            //   frame roughly 2000x shorter than typical, i.e. under a microsecond, which
            //   the harness clock (resolving 0.018 ms) cannot even represent as distinct.
            //
            // So a residual is not expected, and the budget is margin against scheduling
            // noise rather than against a known source. 0.5% of the ~819-frame denominator
            // is 4 frames: enough that a single unlucky hitch cannot fail the suite,
            // nowhere near enough to hide a systematic one. The two systematic shapes this
            // measurement has actually produced are far above it — a per-sample residual
            // is 20 frames (2.4%) and a per-step residual would be ~3 per sample, 60
            // frames (7%) — so this discriminates rather than accommodates.
            //
            // Read it with `fault run lengths`. A percentage cannot tell 20 isolated
            // frames from 3 freezes of 7, and those need different responses.
            const float RenderingFaultBudget = 0.5f;

            Assert.That(withPrediction.RenderingFaultPercent, Is.LessThan(RenderingFaultBudget),
                $"{withPrediction.RenderingFaultPercent:F2}% of the frames that should have " +
                $"been moving showed no movement at all — {withPrediction.StillFramesHoldActive} " +
                $"of {withPrediction.FramesHoldActive}. These are frames on which the hold " +
                "window was still running, so the predictor was still integrating and there " +
                "was a step in flight to render; the rendered position did not follow it. " +
                $"At {withPrediction.ObservedFps:F0} fps against " +
                $"{withPrediction.TickRateInUse} Hz there are " +
                $"{withPrediction.ObservedFps / Math.Max(1, withPrediction.TickRateInUse):F1} " +
                "frames per tick, so a run of them is the avatar holding a still pose for " +
                "part of every tick — which is what a player calls stutter and what no " +
                "correction counter can see, because the SIMULATED position is right " +
                "throughout and only the rendered one stops. " +
                $"(Still frames over every sampled frame, the old figure, was " +
                $"{withPrediction.StillFramePercent:F1}% — but " +
                $"{withPrediction.StillFramesHoldExpired} of those were after the hold " +
                "expired, where a motionless avatar is correct.)");

            Assert.That(withPrediction.TickRateDisagrees, Is.False,
                $"predicting at {withPrediction.TickRateInUse} Hz while the wire measures " +
                $"{withPrediction.MeasuredTickRate:F1} Hz. Every predicted step is wrong by " +
                "that ratio, and at these magnitudes it smooths rather than snaps — so it " +
                "reads as soft movement, not as an error.");

            // LEFT AS IT WAS, AND CURRENTLY FAILING. That is not this change's doing.
            //
            // A live run measures 19 snaps with a max correction of 16 whole steps. Pure
            // develop measures the same 19. They are pre-existing and are tracked as their
            // own issue; folding a fix for them into a rendering change would put two
            // unrelated things in one diff.
            //
            // They were invisible until now for a structural reason worth remembering: the
            // still-frame assertion above fires FIRST, so while it failed this line never
            // executed. A green run above is what made this reachable, not what made it
            // true. Beware of reading "assertion never failed" as "condition never held" —
            // in an ordered sequence of asserts those are different statements.
            //
            // When picking this up, note that Representative() selects the reported run by
            // FrameDeltaBurstiness, a RENDERED metric (see Representative), while Snaps is
            // a simulation property. Any rendering change reselects which repeat is
            // reported and can move this count with no simulation change at all. Read
            // SNAPS PER RUN in the spread block rather than this single figure.
            Assert.That(withPrediction.Snaps, Is.Zero,
                "a snap in ordinary localhost play means the client and server disagreed by " +
                "more than half a step, which nothing in a healthy configuration should do.");

            int correctionBudget = Samples / 4;
            Assert.That(withPrediction.SmoothedCorrections, Is.LessThanOrEqualTo(correctionBudget),
                $"{withPrediction.SmoothedCorrections} of {Samples} samples needed a " +
                "correction. On localhost with matched rates and the same shared logic on " +
                "both sides, agreement should be the rule and a correction the exception — " +
                "a correction on nearly every input means a systematic disagreement, and " +
                "the last time this fired it was a 4x tick-rate mismatch that no other " +
                "counter showed.");

            var predictedMedian = Median(withPrediction.Samples.Where(s => !s.VisibleTimedOut)
                .Select(s => s.InputToVisibleMs));

            var predictedMedian = Median(withPrediction.Samples.Where(s => !s.VisibleTimedOut)
                .Select(s => s.InputToVisibleMs));
            var unpredictedMedian = Median(withoutPrediction.Samples.Where(s => !s.VisibleTimedOut)
                .Select(s => s.InputToVisibleMs));

            Assert.That(predictedMedian, Is.LessThan(unpredictedMedian),
                "prediction did not make the avatar move sooner, which is the only thing " +
                "it exists to do.");

            // The falsifiability check. A predictor fed a speed the server is not using
            // MUST be corrected; if this is zero too, then corrections never happen at
            // all and the healthy run's 0.0000 meant nothing.
            Assert.That(diverging.Reconciles, Is.GreaterThan(0),
                "the divergence run never reconciled at all, so it cannot say anything " +
                "about whether corrections work.");

            Assert.That(diverging.MaxCorrection, Is.GreaterThan(0f),
                "the client predicted a step while sending the server a zero vector, so " +
                "the two MUST disagree by one step once the tick is acknowledged — and " +
                "the predictor still reported no correction. That means reconcile is not " +
                "comparing against the server's position, and the zero correction on the " +
                "healthy run above is meaningless rather than reassuring.");
        });

        /// <summary>
        /// Names the first backend endpoint that cannot be reached, or null when both can.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Cheap and bounded on purpose</b> — a TCP connect with a short timeout, not
        /// the auth flow. The point is to decide whether to run at all, and a probe that
        /// took as long as the thing it guards would be its own problem.
        /// </para>
        /// <para>
        /// <b>Any exception here is treated as "unreachable", never as a failure.</b> A
        /// throw from the probe is the same situation as a refused connection — no
        /// backend — and surfacing it as a test failure would recreate exactly the bug
        /// this method exists to fix.
        /// </para>
        /// </remarks>
        private static async UniTask<string> FirstUnreachableAsync()
        {
            string gateway = await ProbeAsync(
                LiveBackendConfig.GatewayHost, LiveBackendConfig.GatewayPort, "gateway");
            if (gateway != null) return gateway;

            // Nakama is contacted first by the run, so an unreachable one fails earlier
            // and more confusingly than the gateway. Both are checked.
            return await ProbeAsync(
                LiveBackendConfig.NakamaHost, LiveBackendConfig.NakamaPort, "Nakama");
        }

        private const int ProbeTimeoutMs = 1500;

        private static async UniTask<string> ProbeAsync(string host, int port, string what)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    Task connect = client.ConnectAsync(host, port);
                    Task finished = await Task.WhenAny(connect, Task.Delay(ProbeTimeoutMs)).AsUniTask();

                    if (finished != connect)
                    {
                        return $"no {what} at {host}:{port} — connect timed out after {ProbeTimeoutMs} ms";
                    }

                    if (connect.IsFaulted)
                    {
                        // Observed deliberately: an unobserved faulted Task would surface
                        // later as an unrelated error in whatever test runs next.
                        string why = connect.Exception?.GetBaseException().Message ?? "connect failed";
                        return $"no {what} at {host}:{port} — {why}";
                    }

                    return client.Connected
                        ? null
                        : $"no {what} at {host}:{port} — the socket did not open";
                }
            }
            catch (Exception ex)
            {
                return $"no {what} at {host}:{port} — {ex.Message}";
            }
        }

        /// <param name="forceDivergence">
        /// Send a zero vector while predicting a non-zero one, so the server acknowledges
        /// the tick having moved nowhere while the client predicted a step. See the note
        /// on <c>WrongSpeed</c> above for why the two more obvious approaches — a wrong
        /// speed, and dropping the input — do not produce a divergence at all.
        /// </param>
        private static async UniTask<Run> MeasureAsync(bool predict, bool forceDivergence = false)
        {
            var run = new Run
            {
                Name = forceDivergence
                    ? "prediction ON, predicted vector != sent vector (forced divergence)"
                    : predict ? "prediction ON" : "prediction OFF",
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var ct = cts.Token;

            var auth = new NakamaDeviceAuth();
            // Unique device per run: two runs in one session must not share a player
            // entity, or the second inherits the first's position and speed.
            var jwt = await auth.GetGatewayTokenAsync(
                $"measure-{(forceDivergence ? "diverge" : predict ? "on" : "off")}-{DateTime.UtcNow.Ticks}", ct);

            using var client = new NetworkClient(
                new NetworkSettings
                {
                    GatewayHost = LiveBackendConfig.GatewayHost,
                    GatewayPort = LiveBackendConfig.GatewayPort,
                },
                new DefaultTransportFactory(), new ProtobufWireCodec(), new UnityNetLog());

            // CONNECT FIRST. The prediction timestep comes from the server's join
            // response, so the predictor cannot exist until the join has happened.
            //
            // This ordering is the fix for a defect in this file: the harness used to build
            // PredictionSettings from CUVARA_TICK_RATE before connecting, so against the
            // multi-rate backend it measured a client predicting at 15 against a server
            // integrating at 60 — and reported `corrections smoothed 20` out of 20 samples
            // while every other counter read healthy. The instrument had its own copy of
            // the constant the instrument exists to check.
            await client.ConnectAsync(jwt, LiveBackendConfig.MapId, ct);

            string localId = client.UserId;
            Assert.That(localId, Is.Not.Empty, "joined without a user id");

            var settings = PredictionSettings.FromServer(
                client.TickRate,
                fallbackTickRate: LiveBackendConfig.TickRate,
                LiveBackendConfig.PlayerSpeed,
                MapBounds.Default);

            run.TickRateInUse = settings.TickRate;
            run.TickRateIsFallback = settings.TickRateIsFallback;

            LocalMovePredictor predictor = predict ? new LocalMovePredictor(settings) : null;

            var view = new ProbeView { TrackedId = localId };
            var binder = new WorldViewBinder(view, predictor);
            run.Predicting = binder.IsPredicting;

            long tick = 0;
            var lastSendAt = 0.0;

            // double, not float, and deliberately.
            //
            // Time.realtimeSinceStartup is a float. The representable spacing at a value v
            // is 2^(exponent(v) - 23), so the clock coarsens the longer the process has
            // been alive: ~0.98 ms past 2.3 hours, ~1.95 ms past 4.5. A loop running near
            // 1000 fps then reads the SAME instant on consecutive iterations, hands
            // AdvanceFrame a zero delta, and AdvanceFrame — correctly — does nothing. The
            // rendered position is then bit-identical to the previous frame's and the
            // harness records an exact zero as a still frame.
            //
            // That produces a still-frame figure near 50% at ~1000 fps that is a reading
            // of the clock and not of the renderer: it is independent of the network, of
            // the server, and of every line of rendering code. The mean frame time comes
            // out exactly right (the zeros are paid back by the doubled frames), so the
            // reported fps looks healthy and gives no hint.
            //
            // realtimeSinceStartupAsDouble has 52 bits of mantissa and stays at
            // sub-microsecond spacing for centuries. Nothing in the runtime had this
            // problem — WorldViewBinder keeps a Stopwatch and the sample bridge passes
            // Time.deltaTime — only the harness rolled its own clock out of a float.
            // Re-stamped after every PumpAsync, and that is not bookkeeping.
            //
            // PumpAsync drives its own frame loop over the settle window and advances the
            // predictor across it. This variable was only ever written inside the sample
            // loop, so the first AdvanceFrame of each sample handed the predictor
            // `now - <end of the PREVIOUS sample>` — the whole settle span again, on top of
            // what PumpAsync had already advanced. Six settle sends at 15 Hz is ~400 ms, so
            // that one call ran `while (_tickAccumulator >= _dt)` about 24 times and jumped
            // _baseTick 24 ticks in a single frame.
            //
            // The hold window is four ticks wide, so it expired inside that one call, on
            // every sample, before a single frame had been rendered against it. The
            // measurement saw it exactly: FramesHoldActive came back as 20 across 20
            // samples — one frame each, the one read before that first AdvanceFrame — where
            // four ticks of hold at ~1000 fps should give some 67 frames per sample. A
            // "100% of frames that should have been moving were still" built on a
            // denominator of one frame per sample is not measuring the renderer at all.
            //
            // This is the same double-advance WorldViewBinder documents at AdvanceFrame and
            // guards with _frameDriven, reappearing in the harness's own clock rather than
            // in the binder's.
            double lastFrameAt = Time.realtimeSinceStartupAsDouble;

            // One sample's frames, reused. See Run.TailFramesTrimmed.
            var sampleDeltas = new List<float>();
            var sampleSeconds = new List<float>();

            // The SEND cadence, which is a client choice and deliberately not the server's
            // integration rate. Conflating the two is what produced the defect above.
            float dt = 1f / LiveBackendConfig.TickRate;

            // Let the world arrive and the local entity spawn before measuring.
            await PumpAsync(client, binder, localId, seconds: 1.5f, ct);

            // PumpAsync runs its own frame clock and has already advanced this span.
            // See the note on lastFrameAt.
            lastFrameAt = Time.realtimeSinceStartupAsDouble;

            for (int i = 0; i < Samples; i++)
            {
                // --- settle on zero input ---
                for (int s = 0; s < SettleTicks; s++)
                {
                    tick++;
                    client.Session?.SendInput(tick, 0f, 0f, "");
                    predictor?.RecordInput(tick, 0f, 0f);
                    await PumpAsync(client, binder, localId, dt, ct);
                    lastFrameAt = Time.realtimeSinceStartupAsDouble;
                }

                if (!view.TryGet(localId, out var settled))
                {
                    Assert.Fail("the local entity is not in the view — it never spawned, " +
                                "or the id the server gave us is not the one it is sending.");
                }

                // --- one input, then watch ---
                tick++;
                long sampleTick = tick;
                double t0 = Time.realtimeSinceStartupAsDouble;

                // The divergence run sends a vector the server will act on (nothing)
                // while predicting one it will not. The tick is still SENT, so it is
                // acknowledged and leaves the buffer — which is what makes the
                // disagreement real rather than merely pending.
                if (lastSendAt > 0f)
                {
                    run.SendGaps.Add((float)(t0 - lastSendAt));
                }

                lastSendAt = t0;

                client.Session?.SendInput(sampleTick, forceDivergence ? 0f : 1f, 0f, "");
                predictor?.RecordInput(sampleTick, 1f, 0f);

                var sample = new Sample { VisibleTimedOut = true, AuthoritativeTimedOut = true };
                bool sawVisible = false, sawAuthoritative = false;

                var previousRendered = settled;
                double previousFrameTime = Time.realtimeSinceStartupAsDouble;

                // Buffered per sample rather than appended straight to the run, because
                // the trailing still frames can only be identified once the sample is
                // over. See Run.TailFramesTrimmed.
                sampleDeltas.Clear();
                sampleSeconds.Clear();

                // No AdvanceFrame has run in this sample yet, so the first reading spans
                // zero time. See Run.ZeroDurationFramesSkipped.
                var advancedThisSample = false;
                var activeStillRun = 0;

                // Scoped to this sample window, so the counters describe the measured
                // motion and not the settle phases either side of it.
                int ticks0 = predictor?.BaseTicksAdvanced ?? 0;
                int stepped0 = predictor?.HeldStepsApplied ?? 0;
                int skipExpired0 = predictor?.SkipExpired ?? 0;
                int skipNothing0 = predictor?.SkipNothingHeld ?? 0;
                int skipAlready0 = predictor?.SkipInputAlreadyStepped ?? 0;
                int skipNoWindow0 = predictor?.SkipNoHoldWindow ?? 0;
                int skipRefused0 = predictor?.SkipRefusedByMovementModel ?? 0;
                int skipNoDisp0 = predictor?.SkipNoDisplacement ?? 0;

                while (Time.realtimeSinceStartupAsDouble - t0 < SampleTimeoutSeconds &&
                       !(sawVisible && sawAuthoritative))
                {
                    binder.Tick(client.World, localId);

                    // One reading per render frame: how far the avatar moved since the
                    // last frame. The distribution of these IS the stutter.
                    if (view.TryGet(localId, out var rendered) && !advancedThisSample)
                    {
                        // Re-baseline instead of recording. The reading is real, the
                        // interval it spans is not.
                        run.ZeroDurationFramesSkipped++;
                        previousRendered = rendered;
                        previousFrameTime = Time.realtimeSinceStartupAsDouble;
                    }
                    else if (view.TryGet(localId, out rendered))
                    {
                        var frameDelta = (rendered - previousRendered).magnitude;
                        sampleDeltas.Add(frameDelta);
                        previousRendered = rendered;

                        bool active = predictor != null && predictor.HoldIsActive;
                        if (active && frameDelta <= 1e-6f)
                        {
                            activeStillRun++;
                        }
                        else if (activeStillRun > 0)
                        {
                            run.ActiveStillRuns.Add(activeStillRun);
                            activeStillRun = 0;
                        }

                        // Classify the still frame by the predictor state that produced
                        // it, at the moment it was produced. Reconstructing this
                        // afterwards is not possible — the state has moved on — and
                        // guessing at it is what has cost this investigation three rounds.
                        if (predictor != null && predictor.HoldIsActive)
                        {
                            run.FramesHoldActive++;
                        }

                        if (frameDelta <= 1e-6f && predictor != null)
                        {
                            if (predictor.RenderStepProgress >= 1f)
                            {
                                run.StillFramesStepSaturated++;
                            }

                            if (predictor.HoldIsActive)
                            {
                                run.StillFramesHoldActive++;
                            }
                            else
                            {
                                run.StillFramesHoldExpired++;
                            }
                        }

                        double nowFrame = Time.realtimeSinceStartupAsDouble;
                        var frameSeconds = (float)(nowFrame - previousFrameTime);
                        sampleSeconds.Add(frameSeconds);
                        previousFrameTime = nowFrame;

                        // The clock's real granularity, measured. If this approaches the
                        // mean frame time the deltas are being quantised by the clock and
                        // no smoothness figure taken from them means anything.
                        if (frameSeconds > 0f &&
                            (float.IsNaN(run.SmallestFrameSeconds) ||
                             frameSeconds < run.SmallestFrameSeconds))
                        {
                            run.SmallestFrameSeconds = frameSeconds;
                        }
                    }

                    if (!sawVisible && view.TryGet(localId, out var now) &&
                        (now - settled).sqrMagnitude > MoveEpsilon * MoveEpsilon)
                    {
                        sample.InputToVisibleMs = (float)((Time.realtimeSinceStartupAsDouble - t0) * 1000.0);
                        sample.VisibleTimedOut = false;
                        sawVisible = true;
                    }

                    if (!sawAuthoritative && client.World.AckTick >= sampleTick)
                    {
                        sample.InputToAuthoritativeMs = (float)((Time.realtimeSinceStartupAsDouble - t0) * 1000.0);
                        sample.AuthoritativeTimedOut = false;
                        sawAuthoritative = true;
                    }

                    if (predictor != null)
                    {
                        run.PendingPeak = Math.Max(run.PendingPeak, predictor.PendingCount);
                        run.MaxCorrection = Math.Max(run.MaxCorrection, predictor.LastCorrection);
                    }

                    // The sampling loop is a frame loop too, and it is the one whose
                    // frame deltas become the burstiness figure.
                    double frameNow = Time.realtimeSinceStartupAsDouble;
                    var advanceBy = (float)(frameNow - lastFrameAt);

                    // Counted, not silently tolerated. A non-positive delta makes
                    // AdvanceFrame a no-op, which leaves the rendered position unchanged
                    // and lands in the smoothness figure as a still frame the renderer
                    // never had a chance to move. With a double clock this should be zero;
                    // if it is not, the still-frame percentage is measuring the harness.
                    if (advanceBy <= 0f)
                    {
                        run.NonAdvancingFrames++;
                    }

                    if (advanceBy > run.LargestAdvanceSeconds)
                    {
                        run.LargestAdvanceSeconds = advanceBy;
                    }

                    // A delta wider than a base tick makes the predictor step more than one
                    // tick in a single call, which can retire a whole hold window before
                    // anything is rendered against it. That is always a fault in whatever
                    // is driving the clock, never in the predictor.
                    if (run.TickRateInUse > 0 && advanceBy > 1f / run.TickRateInUse)
                    {
                        run.OversizedAdvances++;
                    }

                    binder.AdvanceFrame(advanceBy);
                    lastFrameAt = frameNow;
                    advancedThisSample = true;

                    await UniTask.Yield(PlayerLoopTiming.Update, ct);
                }

                // Keep everything up to and including the last frame that moved; drop the
                // run of still frames after it. That boundary is the end of the motion one
                // input produces, and it is set by the simulation — the hold window — not
                // by how long the acknowledgement took to come back. Frames past it are
                // the avatar correctly at rest, and folding them into a smoothness figure
                // makes that figure a latency measurement wearing a rendering
                // measurement's clothes. Run.TailFramesTrimmed carries the full argument.
                //
                // A sample in which NOTHING moved is kept whole rather than trimmed away.
                // It has no "last moving frame", and silently discarding it would turn the
                // worst possible result — the avatar never moved at all — into an empty
                // contribution that cannot fail anything.
                var lastMoving = sampleDeltas.FindLastIndex(d => d > 1e-6f);
                if (lastMoving < 0)
                {
                    lastMoving = sampleDeltas.Count - 1;
                }

                for (var f = 0; f < sampleDeltas.Count; f++)
                {
                    run.FrameDeltas.Add(sampleDeltas[f]);
                    run.FrameSeconds.Add(sampleSeconds[f]);

                    if (f <= lastMoving)
                    {
                        run.FrameDeltasWhileMoving.Add(sampleDeltas[f]);
                    }
                    else
                    {
                        run.TailFramesTrimmed++;
                    }
                }

                // Still-run lengths, measured per sample so a sample boundary does not
                // splice two runs into one. This is what tells a ~2-frame cadence apart
                // from a per-tick one from an at-rest tail; see Run.LongestStillRun.
                var currentRun = 0;
                for (var f = 0; f < sampleDeltas.Count; f++)
                {
                    if (sampleDeltas[f] <= 1e-6f)
                    {
                        currentRun++;
                        continue;
                    }

                    if (currentRun > 0)
                    {
                        run.StillRunCount++;
                        run.LongestStillRun = Math.Max(run.LongestStillRun, currentRun);
                        currentRun = 0;
                    }
                }

                if (currentRun > 0)
                {
                    run.StillRunCount++;
                    run.LongestStillRun = Math.Max(run.LongestStillRun, currentRun);
                }

                if (predictor != null)
                {
                    run.BaseTicksAdvanced += predictor.BaseTicksAdvanced - ticks0;
                    run.HeldStepsApplied += predictor.HeldStepsApplied - stepped0;
                    run.SkipExpired += predictor.SkipExpired - skipExpired0;
                    run.SkipNothingHeld += predictor.SkipNothingHeld - skipNothing0;
                    run.SkipInputAlreadyStepped +=
                        predictor.SkipInputAlreadyStepped - skipAlready0;
                    run.SkipNoHoldWindow += predictor.SkipNoHoldWindow - skipNoWindow0;
                    run.SkipRefusedByMovementModel +=
                        predictor.SkipRefusedByMovementModel - skipRefused0;
                    run.SkipNoDisplacement += predictor.SkipNoDisplacement - skipNoDisp0;
                }

                if (activeStillRun > 0)
                {
                    run.ActiveStillRuns.Add(activeStillRun);
                }

                run.SampleSeconds.Add((float)(Time.realtimeSinceStartupAsDouble - t0));
                run.Samples.Add(sample);
            }

            run.DistinctPositions = view.DistinctTrackedPositions;
            run.HoldTicksInUse = predictor?.HoldTicks ?? 0;
            run.SetStateCalls = view.SetStateCalls;

            if (run.FrameSeconds.Count > 0)
            {
                float mean = run.FrameSeconds.Average();
                run.ObservedFps = mean > 0f ? 1f / mean : 0f;
            }

            run.MeasuredTickRate = binder.TickRate.HasEstimate ? binder.TickRate.EstimatedHz : 0f;
            run.TickRateDisagrees = binder.TickRate.Disagrees(run.TickRateInUse);

            if (predictor != null)
            {
                run.ReplayedSteps = predictor.ReplayedSteps;
                run.Reconciles = predictor.Reconciles;
                run.Snaps = predictor.Snaps;
                run.SmoothedCorrections = predictor.SmoothedCorrections;
                run.EffectiveSpeed = predictor.EffectiveSpeed;
                run.IntegrationTimestepMs = predictor.IntegrationTimestep * 1000f;
                run.SmoothingSpanMs = predictor.EffectiveSmoothingSpan * 1000f;
                run.StepIntervalMs = predictor.ObservedStepInterval * 1000f;
                run.StepIntervalSamples = predictor.StepIntervalSamples;
                run.StepIntervalResets = predictor.StepIntervalResets;
                run.InputIntervalMs = predictor.ObservedInputInterval * 1000f;
                // BaseTicksAdvanced / HeldStepsApplied / the Skip* counters are
                // accumulated per sample window above, deliberately not read whole here.
            }
            else
            {
                // Deliberately NOT set from LiveBackendConfig. It was, and it printed
                // "effective speed 5" for a run in which nothing moved, which read as
                // evidence that snapshots were arriving and carrying speed. It was the
                // configured constant echoing back. An instrument may print a measured
                // value or say it has none; printing a constant in the slot where a
                // measurement belongs is how a reader is misled by a working tool.
                run.EffectiveSpeed = float.NaN;
            }

            client.Disconnect();
            return run;
        }

        /// <summary>Drives the binder for a wall-clock duration, as a frame loop would.</summary>
        private static async UniTask PumpAsync(
            NetworkClient client, WorldViewBinder binder, string localId, float seconds, CancellationToken ct)
        {
            double until = Time.realtimeSinceStartupAsDouble + seconds;
            double last = Time.realtimeSinceStartupAsDouble;

            while (Time.realtimeSinceStartupAsDouble < until)
            {
                binder.Tick(client.World, localId);

                // Per frame, not per snapshot. Snapshot processing advances prediction
                // once per arriving snapshot, which is the world rate; a client that
                // renders only then shows the avatar still between snapshots and jumps
                // on the frame one lands. That is what the harness was measuring.
                double now = Time.realtimeSinceStartupAsDouble;
                binder.AdvanceFrame((float)(now - last));
                last = now;

                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }
        }

        // ---- reporting ----

        private static void Report(Run run)
        {
            var visible = run.Samples.Where(s => !s.VisibleTimedOut).Select(s => s.InputToVisibleMs).ToList();
            var auth = run.Samples.Where(s => !s.AuthoritativeTimedOut).Select(s => s.InputToAuthoritativeMs).ToList();

            Debug.Log(
                $"[Measure] === {run.Name} ===\n" +
                $"  samples                  {visible.Count}/{run.Samples.Count} usable" +
                (run.Samples.Count != visible.Count ? "  (timeouts are counted, not dropped)" : "") + "\n" +
                $"  input -> visible move    {Describe(visible)}\n" +
                $"  input -> authoritative   {Describe(auth)}\n" +
                $"  predicting               {run.Predicting}\n" +
                $"  pending peak             {run.PendingPeak}\n" +
                $"  reconciles               {run.Reconciles}\n" +
                $"  replayed steps           {run.ReplayedSteps}   (zero is normal when nothing was pending)\n" +
                $"  corrections smoothed     {run.SmoothedCorrections}\n" +
                $"  corrections snapped      {run.Snaps}\n" +
                $"  max correction           {run.MaxCorrection:F4} world units\n" +
                $"  max correction in steps  {(run.ExpectedStep > 0f ? (run.MaxCorrection / run.ExpectedStep).ToString("F2") : "n/a")}" +
                    CorrectionShapeNote(run) + "\n" +
                $"  distinct positions seen  {run.DistinctPositions}" +
                    (run.DistinctPositions <= 1
                        ? "   <<< the entity NEVER MOVED — server or spawn, not the probe"
                        : "   (the view was told it moved, so the probe can see motion)") + "\n" +
                $"  SetState calls           {run.SetStateCalls}" +
                    (run.SetStateCalls == 0 ? "   <<< no snapshots reached the binder at all" : "") + "\n" +
                $"  send gap burstiness      {run.SendGapBurstiness:F2}" +
                    (run.SendGapBurstiness > 1.5f
                        ? "   <<< sends are CLUMPING; rpg-mmo-server#100 discards clumped inputs"
                        : "   (1.00 is evenly spaced)") + "\n" +
                $"  effective speed          " +
                    (float.IsNaN(run.EffectiveSpeed)
                        ? "not measured (no predictor in this configuration)"
                        : run.EffectiveSpeed.ToString()) + "\n" +
                $"  HOLD WINDOW IN USE       {run.HoldTicksInUse} base ticks" +
                    HoldWindowNote(run) + "\n" +
                $"  TICK RATE IN USE         {run.TickRateInUse} Hz" +
                    (run.TickRateIsFallback ? "  <- FALLBACK, server advertised none" : "  (advertised by the server)") + "\n" +
                $"  tick rate measured       {run.MeasuredTickRate:F1} Hz off the wire" +
                    (run.TickRateDisagrees ? "   <<< DISAGREES with the rate in use" : "   (agrees)") + "\n" +
                $"  --- smoothness (per render frame, while moving) ---\n" +
                $"  frames with NO movement  {run.StillFramePercent:F1}%   <- the stutter; " +
                    "high means the avatar teleports once per input and is frozen between\n" +
                $"  ... excluding at-rest tail {run.StillFramePercentWhileMoving:F1}%   " +
                    $"({run.TailFramesTrimmed} of them were after the input's motion had " +
                    "ended, which is acknowledgement latency rather than rendering)\n" +
                $"  --- what produced the still frames ---\n" +
                $"  integration timestep     {run.IntegrationTimestepMs:F2} ms   " +
                    "(the base tick period; 16.67 at 60 Hz)\n" +
                $"  SMOOTHING SPAN           {run.SmoothingSpanMs:F2} ms" +
                    SpanNote(run) + "\n" +
                $"  observed input interval  {run.InputIntervalMs:F2} ms   " +
                    "(the harness sends at 15 Hz = 66.7 ms)\n" +
                $"  sample window            {(run.SampleSeconds.Count > 0 ? run.SampleSeconds.Average() * 1000f : float.NaN):F0} ms mean, " +
                    $"{(run.SampleSeconds.Count > 0 ? run.SampleSeconds.Max() * 1000f : float.NaN):F0} ms worst" +
                    SampleWindowNote(run) + "\n" +
                $"  ack timeouts             {run.AuthoritativeTimeouts} of {run.Samples.Count}" +
                    (run.AuthoritativeTimeouts > 0
                        ? "   <<< each one is ~3 s of still frames and swamps every " +
                          "frame-derived figure below"
                        : string.Empty) + "\n" +
                $"  base ticks advanced      {run.BaseTicksAdvanced}   (sample windows only)\n" +
                $"  ticks the hold SKIPPED   {run.TicksSkipped}" +
                    (run.BaseTicksAdvanced > 0
                        ? $"   = {100f * run.TicksSkipped / run.BaseTicksAdvanced:F0}% of them. " +
                          "Each one is a still run a whole base tick long."
                        : string.Empty) + "\n" +
                $"    ... expired            {run.SkipExpired}   " +
                    "(baseTick - heldFrom >= HoldTicks)\n" +
                $"    ... nothing held       {run.SkipNothingHeld}   (an explicit stop, or no input yet)\n" +
                $"    ... input already      {run.SkipInputAlreadyStepped}   (rule 1 — not a fault)\n" +
                $"    ... no hold window     {run.SkipNoHoldWindow}   (HoldTicks <= 1)\n" +
                $"    ... model refused      {run.SkipRefusedByMovementModel}\n" +
                $"    ... no displacement    {run.SkipNoDisplacement}\n" +
                $"  held steps applied       {run.HeldStepsApplied}   " +
                    $"= {run.HeldStepsPerBaseTick:F2} per base tick" +
                    (run.HeldStepsPerBaseTick < 0.5f
                        ? "   <<< the render is given a fresh step on fewer than half the " +
                          "base ticks, so a span of one timestep cannot cover the gap"
                        : "") + "\n" +
                $"  still: step SATURATED    {run.StillFramesStepSaturated}   " +
                    "<- render had caught up and had nothing left to show\n" +
                $"  still: hold expired      {run.StillFramesHoldExpired}   " +
                    "<- predictor had legitimately stopped, as the server does\n" +
                $"  RENDERING FAULT          {run.RenderingFaultPercent:F2}% " +
                    $"({run.StillFramesHoldActive} of {run.FramesHoldActive} frames that " +
                    "should have been moving)   <- THE ASSERTED FIGURE\n" +
                $"  fault run lengths        {run.ActiveStillRunHistogram}   " +
                    "<- 1:n means isolated frames; a 4-7 or 8+ bucket means a real freeze\n" +
                $"  zero-duration reads      {run.ZeroDurationFramesSkipped} skipped   " +
                    $"(should equal the {run.Samples.Count} samples exactly)\n" +
                $"  still: hold still ACTIVE {run.StillFramesHoldActive}   " +
                    "<<< these are the rendering fault: the predictor was still " +
                    "integrating and the render did not follow\n" +
                $"  longest still RUN        {run.LongestStillRun} frames   <- THE CADENCE. " +
                    "1-2 = clock granularity; ~fps/tickRate = per-tick rendering; " +
                    "one long run per sample = at-rest tail\n" +
                $"  still runs / mean length {run.StillRunCount} runs, {run.MeanStillRun:F1} frames each\n" +
                $"  non-advancing frames     {run.NonAdvancingFrames}" +
                    (run.NonAdvancingFrames > 0
                        ? "   <<< the HARNESS clock reported a zero delta on these; " +
                          "AdvanceFrame could not move anything and they are counted as still"
                        : "   (every frame got a positive delta, so every still frame is the renderer's)") + "\n" +
                $"  largest AdvanceFrame     {run.LargestAdvanceSeconds * 1000f:F1} ms" +
                    (run.OversizedAdvances > 0
                        ? $"   <<< {run.OversizedAdvances} calls were wider than one base " +
                          "tick, so the predictor stepped several ticks inside one call and " +
                          "the hold window could expire before anything rendered against it"
                        : "   (never wider than a base tick)") + "\n" +
                $"  frames while hold ACTIVE {run.FramesHoldActive}" +
                    (run.FramesHoldActive < run.Samples.Count * 4
                        ? "   <<< far too few: four ticks of hold at this frame rate should " +
                          "give tens of frames per sample. The denominator is empty and the " +
                          "percentage above means nothing."
                        : string.Empty) + "\n" +
                $"  step interval in use     {run.StepIntervalMs:F2} ms   " +
                    $"(NoteStep: {run.StepIntervalSamples} measured, {run.StepIntervalResets} reset by the pause guard)\n" +
                $"  harness clock resolution {run.SmallestFrameSeconds * 1000f:F3} ms" +
                    (run.ObservedFps > 0f && run.SmallestFrameSeconds > 0.5f / run.ObservedFps
                        ? "   <<< comparable to the frame time; frame deltas are QUANTISED by the clock"
                        : "   (well below the frame time)") + "\n" +
                $"  largest single-frame jump {run.MaxFrameDelta:F4} world units\n" +
                $"  mean frame delta         {run.MeanFrameDelta:F4}\n" +
                $"  frame delta std dev      {run.FrameDeltaStdDev:F4}\n" +
                $"  BURSTINESS (max/mean)    {run.FrameDeltaBurstiness:F2}   <- 1.00 is perfectly even; " +
                    "THIS is the comparable number\n" +
                $"  expected jump per input  {run.ExpectedStep:F4}   (speed {run.EffectiveSpeed} / {run.TickRateInUse} Hz)" +
                    ExpectedStepNote(run) + "\n" +
                $"  observed frame rate      {run.ObservedFps:F0} fps   (the raw distances above scale with this)");
        }

        private static void ReportComparison(Run on, Run off)
        {
            var onVisible = on.Samples.Where(s => !s.VisibleTimedOut).Select(s => s.InputToVisibleMs).ToList();
            var offVisible = off.Samples.Where(s => !s.VisibleTimedOut).Select(s => s.InputToVisibleMs).ToList();

            Debug.Log(
                "[Measure] === what prediction removed ===\n" +
                $"  median input -> visible, OFF   {Median(offVisible):F1} ms\n" +
                $"  median input -> visible, ON    {Median(onVisible):F1} ms\n" +
                $"  median interval removed        {Median(offVisible) - Median(onVisible):F1} ms\n" +
                "\n" +
                $"  RENDERING FAULT, ON            {on.RenderingFaultPercent:F2}%   " +
                    $"({on.StillFramesHoldActive}/{on.FramesHoldActive} frames that should " +
                    "have been moving)\n" +
                "  (no equivalent OFF: with no predictor there is no hold window, so there is\n" +
                "   no interval during which the avatar is supposed to be moving. The two\n" +
                "   totals below are still directly comparable.)\n" +
                $"  still frames, OFF              {off.StillFramePercent:F1}%\n" +
                $"  still frames, ON               {on.StillFramePercent:F1}%\n" +
                $"  still frames excl. rest, OFF   {off.StillFramePercentWhileMoving:F1}%\n" +
                $"  still frames excl. rest, ON     {on.StillFramePercentWhileMoving:F1}%\n" +
                $"  longest still run, OFF         {off.LongestStillRun} frames\n" +
                $"  longest still run, ON          {on.LongestStillRun} frames\n" +
                "  (the run length is the cadence. The percentage alone cannot tell a two-frame\n" +
                "   clock artefact from per-tick rendering from an at-rest tail — all three\n" +
                "   produce the same percentage from completely different arrangements.)\n" +
                $"  BURSTINESS, OFF                {off.FrameDeltaBurstiness:F2}   (1.00 = perfectly even)\n" +
                $"  BURSTINESS, ON                 {on.FrameDeltaBurstiness:F2}\n" +
                $"  largest frame jump, OFF        {off.MaxFrameDelta:F4}  at {off.ObservedFps:F0} fps\n" +
                $"  largest frame jump, ON         {on.MaxFrameDelta:F4}  at {on.ObservedFps:F0} fps\n" +
                "  (raw distances scale with frame rate and are NOT comparable between runs;\n" +
                "   burstiness is. Two runs of one build gave 0.0149 and 0.0244 purely because\n" +
                "   they rendered at 336 and 205 fps.)\n" +
                "\n" +
                "  This is input-submitted to avatar-moves, measured in-engine.\n" +
                "  It is NOT keypress-to-visible: the keyboard, OS input stack and display\n" +
                "  pipeline are outside the engine and need external capture. Those legs are\n" +
                "  constant between the two runs, so the DIFFERENCE above is unaffected by\n" +
                "  their absence, but the absolute figures are not a player-felt latency.");
        }

        /// <summary>
        /// Flags a largest-frame-jump that is not a whole number of server steps, or is
        /// more than one.
        /// </summary>
        /// <remarks>
        /// With prediction off the avatar moves only when a snapshot lands, so the largest
        /// frame jump should be the displacement the server produced since the previous
        /// snapshot — one step per input it accepted. A jump that is an exact small
        /// multiple of a step says more inputs were applied per snapshot than expected; one
        /// that is not a multiple at all says the step size itself is wrong. Both are worth
        /// seeing in the output rather than derived afterwards: a run reported 0.2500
        /// against an expected 0.0833 and neither of us could account for the factor of
        /// three from the numbers we had printed.
        /// </remarks>
        /// <summary>
        /// Reads the correction as a count of server steps, which is what separates the
        /// two candidate causes of a persistent sub-threshold correction.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A whole number of steps is a phase problem, not a rate problem.</b> An input
        /// is acknowledged when the server has <i>received</i> it, not when it has finished
        /// integrating it: the hold keeps stepping for up to <c>holdTicks - 1</c> base
        /// ticks afterwards. Replay drops an input as soon as it is acknowledged, so the
        /// steps its hold has not taken yet are dropped with it, and the client differs
        /// from the server by exactly that remainder — a whole number of steps, the same
        /// number every time.
        /// </para>
        /// <para>
        /// <b>A fraction is a rate or arithmetic problem</b>, and would point back at the
        /// timestep, the speed, or the movement model rather than at the hold.
        /// </para>
        /// <para>
        /// A measured 0.1667 at 60/15/5 is 2.00 steps exactly, which is the first case.
        /// Printing the ratio rather than the distance is the difference between a number
        /// that needs arithmetic to interpret and one that names its own cause.
        /// </para>
        /// </remarks>
        private static string CorrectionShapeNote(Run run)
        {
            if (run.ExpectedStep <= 0f || run.MaxCorrection <= 0f) return string.Empty;

            float steps = run.MaxCorrection / run.ExpectedStep;
            float nearest = (float)Math.Round(steps);
            bool whole = nearest >= 1f && Math.Abs(steps - nearest) < 0.05f;

            if (steps < 0.05f)
            {
                return "   (zero — client and server agree, which is the only healthy value)";
            }

            // Read the clock error straight off the correction.
            //
            // A client whose clock is right disagrees with the server by nothing at all;
            // a client whose clock runs fast accrues extra base ticks and disagrees by
            // exactly (factor - 1) * holdTicks steps, linearly, pinned in
            // HeldMovementParityTests.TheCorrectionMeasuresTheClockError. So the
            // correction is not merely a symptom, it is a reading — and this line spares
            // the next person the arithmetic that took several releases to arrive at.
            // Two different faults produce a correction that is a whole number of steps,
            // and this note used to name only one of them — printing "implies a predictor
            // clock at N x real time" as though the clock were established, when it is one
            // candidate of two and the arithmetic does not distinguish them.
            //
            // A clock error accrues extra base ticks and disagrees by
            // (factor - 1) * holdTicks steps, linearly, which is what
            // HeldMovementParityTests pins. But rule 3 — banked movement — caps ONE step
            // at MaxBankedMovementTicks timesteps (15 at 60 Hz, from a 250 ms bound), so a
            // disagreement about how much time an entity had banked while stationary lands
            // at or just under that cap in a single correction, with no clock error at all.
            // A settle phase that leaves the entity still for 400 ms puts both sides
            // squarely against that cap.
            //
            // So both readings are offered and the one that matches is named. 16 steps
            // against a 15-step cap is the banked reading; it is not a 5x clock.
            int hold = run.HoldTicksInUse > 0 ? run.HoldTicksInUse : 0;
            int bankedCap = run.TickRateInUse > 0
                ? GameConstants.MaxBankedMovementTicks(run.TickRateInUse)
                : 0;

            string clock;
            if (bankedCap > 0 && nearest >= bankedCap)
            {
                clock = $" — at or above the {bankedCap}-step banked-movement cap " +
                        "(rule 3, MaxBankedMovementMs), so read this as a disagreement " +
                        "about time banked while stationary, NOT as a clock error";
            }
            else if (hold > 0)
            {
                clock = $" — if this is clock error it implies {1f + steps / hold:F2}x " +
                        $"real time; if it is banked movement the cap is {bankedCap} steps";
            }
            else
            {
                clock = string.Empty;
            }

            return whole
                ? $"   <<< {nearest:F0} whole steps{clock}"
                : $"   <<< {steps:F2} steps{clock}";
        }

        /// <summary>
        /// Flags a hold window that does not match the snapshot cadence the run actually
        /// observed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The window is derived, not advertised: <c>TickRateEstimator.SnapshotTickGap</c>
        /// reports the base ticks between consecutive snapshots and
        /// <c>WorldViewBinder</c> feeds it to <c>LocalMovePredictor.SetHoldTicks</c>. It
        /// should equal the server's <c>WorldEvery</c> — base rate over world rate, 4 at
        /// 60/15.
        /// </para>
        /// <para>
        /// <b>It has to be printed on its own line even when nothing looks wrong.</b> It
        /// was captured and only ever mentioned inside a correction note, so a run with no
        /// correction printed no window at all — and a wrong window produces exactly that
        /// run. Understating the window makes the client predict LESS motion than the
        /// server, and the shortfall lands under <c>SmoothingThreshold</c>, so it smooths
        /// instead of snapping: <c>Snaps</c> stays zero, the corrections budget passes, the
        /// tick rate agrees, and the only visible trace is the avatar freezing part-way
        /// through every tick. Every other assertion in this file is blind to it.
        /// </para>
        /// </remarks>
        private static string HoldWindowNote(Run run)
        {
            if (run.HoldTicksInUse <= 0)
            {
                return "   (no predictor in this configuration)";
            }

            if (run.HoldTicksInUse == 1)
            {
                return "   <<< 1 means NO HOLD AT ALL — ApplyHeld returns false on every " +
                       "base tick, so the predicted position advances only when an input " +
                       "is sent and holds still in between";
            }

            if (run.MeasuredTickRate <= 0f || run.TickRateInUse <= 0)
            {
                return "   (cannot be checked: no measured rate yet)";
            }

            // Snapshots are emitted one world tick apart, so the cadence the estimator saw
            // IS the expected window. Recomputing it from the rates here would just be the
            // same assumption twice; what this compares is the window in use against the
            // 60/15 the server is configured for.
            const int expected = 4;
            return run.HoldTicksInUse == expected
                ? $"   (matches the {expected} expected at 60/15)"
                : $"   <<< expected {expected} at 60/15. The client stops integrating the " +
                  $"held direction {expected - run.HoldTicksInUse} base ticks before the " +
                  "server does, then renders nothing until the next input or snapshot";
        }

        /// <summary>
        /// Flags a smoothing span that cannot cover the interval steps actually arrive at.
        /// </summary>
        /// <remarks>
        /// The span is what one step is spread across. If steps arrive further apart than
        /// the span, <c>StepProgress</c> saturates and the rendered position holds for the
        /// difference — which is a rendering fault invisible to every correction counter,
        /// because the simulated position is right throughout.
        /// </remarks>
        /// <summary>
        /// Compares the observation window against the motion the hold window can produce.
        /// </summary>
        /// <remarks>
        /// One input's motion lasts <c>HoldTicks</c> base ticks and no longer: nothing
        /// re-arms the hold, so every tick after it hits <c>ApplyHeld</c>'s expiry guard
        /// and the rendered position is constant for that tick's whole duration. If the
        /// window is much longer than that, a large still-frame percentage follows from
        /// the ratio alone and says nothing about whether interpolation works.
        /// </remarks>
        private static string SampleWindowNote(Run run)
        {
            if (run.SampleSeconds.Count == 0 || run.HoldTicksInUse <= 0 ||
                run.TickRateInUse <= 0)
            {
                return string.Empty;
            }

            float holdMs = 1000f * run.HoldTicksInUse / run.TickRateInUse;
            float meanMs = run.SampleSeconds.Average() * 1000f;
            float ratio = holdMs > 0f ? meanMs / holdMs : float.NaN;

            return ratio <= 1.5f
                ? $"   (the {holdMs:F0} ms hold window covers it)"
                : $"   <<< {ratio:F1}x the {holdMs:F0} ms of motion one input can produce, " +
                  $"so ~{100f * (1f - 1f / ratio):F0}% of these frames are after the hold " +
                  "expired and the predictor has correctly stopped";
        }

        private static string SpanNote(Run run)
        {
            if (float.IsNaN(run.SmoothingSpanMs) || run.SmoothingSpanMs <= 0f)
            {
                return "   (no predictor in this configuration)";
            }

            if (float.IsNaN(run.HeldStepsPerBaseTick) || run.HeldStepsPerBaseTick <= 0f)
            {
                return "   (no held steps observed; nothing to compare it against)";
            }

            // The interval between fresh steps, in milliseconds: one base tick period
            // divided by how often a tick actually produced a step.
            float stepIntervalMs = run.IntegrationTimestepMs / run.HeldStepsPerBaseTick;

            return run.SmoothingSpanMs >= stepIntervalMs * 0.9f
                ? $"   (steps arrive every {stepIntervalMs:F2} ms, so the span covers them)"
                : $"   <<< steps arrive every {stepIntervalMs:F2} ms but are spread over " +
                  $"only {run.SmoothingSpanMs:F2} ms, so the render finishes each one " +
                  $"after {100f * run.SmoothingSpanMs / stepIntervalMs:F0}% of the gap " +
                  "and holds still for the rest";
        }

        private static string ExpectedStepNote(Run run)
        {
            if (run.ExpectedStep <= 0f || float.IsNaN(run.MaxFrameDelta) || run.MaxFrameDelta <= 0f)
            {
                return string.Empty;
            }

            float ratio = run.MaxFrameDelta / run.ExpectedStep;
            return $"   observed/expected = {ratio:F2}" +
                   (ratio > 1.5f ? "  <<< more movement per frame than one server step" : "");
        }

        /// <summary>
        /// The run whose burstiness is the median, so the reported figures all come from
        /// one coherent run rather than being averaged across runs.
        /// </summary>
        /// <remarks>
        /// Averaging across runs would produce a set of numbers no single run ever
        /// produced, and the relationships between them — visible latency against
        /// authoritative latency, corrections against burstiness — are the point. The
        /// median is chosen on burstiness because that is the metric under investigation.
        /// </remarks>
        /// <summary>
        /// Range of burstiness across runs of one configuration — the resolution floor of
        /// any comparison between configurations.
        /// </summary>
        private static float Spread(List<Run> runs)
        {
            var b = runs.Select(r => r.FrameDeltaBurstiness).Where(x => !float.IsNaN(x)).ToList();
            return b.Count < 2 ? float.NaN : b.Max() - b.Min();
        }

        private static Run Representative(List<Run> runs)
        {
            if (runs.Count == 1) return runs[0];

            var ordered = runs
                .OrderBy(r => float.IsNaN(r.FrameDeltaBurstiness) ? float.MaxValue : r.FrameDeltaBurstiness)
                .ToList();

            return ordered[ordered.Count / 2];
        }

        private static void ReportSpread(string label, List<Run> runs)
        {
            var burst = runs.Select(r => r.FrameDeltaBurstiness).ToList();
            var snaps = runs.Select(r => r.Snaps).ToList();
            var steps = runs.Select(r => r.ExpectedStep > 0f ? r.MaxCorrection / r.ExpectedStep : float.NaN).ToList();
            var visible = runs.Select(r => Median(r.Samples.Where(x => !x.VisibleTimedOut).Select(x => x.InputToVisibleMs).ToList())).ToList();

            float lo = burst.Min(), hi = burst.Max();
            float mid = burst.Average();
            float relative = mid > 0f ? (hi - lo) / mid : float.NaN;

            Debug.Log(
                $"[Measure] SPREAD over {runs.Count} runs — {label}\n" +
                $"  burstiness        {string.Join(", ", burst.Select(b => b.ToString("F2")))}" +
                $"   range {lo:F2}..{hi:F2}, spread {relative:P0} of mean\n" +
                $"  correction steps  {string.Join(", ", steps.Select(x => x.ToString("F2")))}\n" +
                $"  SNAPS PER RUN     {string.Join(", ", snaps)}" +
                    (snaps.Count > 1 && snaps.Min() != snaps.Max()
                        ? "   <<< the repeats DISAGREE. Snaps are a property of one run, and " +
                          "Representative() picks the run by FrameDeltaBurstiness — a RENDERED " +
                          "metric. Any change to rendering reselects which run is reported, so " +
                          "a reported snap count can move with no simulation change at all."
                        : string.Empty) + "\n" +
                $"  input->visible ms {string.Join(", ", visible.Select(x => x.ToString("F1")))}\n" +
                (relative > 0.25f
                    ? "  <<< the runs disagree by more than a quarter of their mean. Do not " +
                      "read a change smaller than this spread as a regression or a fix.\n"
                    : "  (runs agree closely; a change larger than this spread is real)\n"));
        }

        private static string Describe(IReadOnlyCollection<float> xs)
        {
            if (xs.Count == 0) return "no usable samples";
            var sorted = xs.OrderBy(x => x).ToList();
            return $"median {Median(sorted):F1} ms   min {sorted.First():F1}   max {sorted.Last():F1}   " +
                   $"p90 {Percentile(sorted, 0.9f):F1}   mean {sorted.Average():F1}";
        }

        private static float Median(IEnumerable<float> xs)
        {
            var s = xs.OrderBy(x => x).ToList();
            if (s.Count == 0) return float.NaN;
            return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) * 0.5f;
        }

        private static float Percentile(List<float> sorted, float p)
        {
            if (sorted.Count == 0) return float.NaN;
            int i = Mathf.Clamp(Mathf.CeilToInt(p * sorted.Count) - 1, 0, sorted.Count - 1);
            return sorted[i];
        }
    }
}
