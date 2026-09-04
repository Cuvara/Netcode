using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Measures the prediction lead that <see cref="LocalMovePredictor.Reconcile"/> keeps or
    /// discards, and pins what it does today (issue #53).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read this before trusting a number out of here.</b> An earlier fixture of this name
    /// was shipped <c>[Ignore]</c>d by its own author because every configuration it ran —
    /// zero latency included, and both the anchored and unanchored paths — returned exactly
    /// <c>1.000</c> step. A constant is not a measurement, and a fix evaluated against it
    /// would have been evaluated against nothing. That fixture never reached <c>develop</c>
    /// and does not exist any more.
    /// </para>
    /// <para>
    /// <b>What makes this one trustworthy is not that it produces numbers — it is
    /// <see cref="ZeroLead_CorrectsByNothing"/> and
    /// <see cref="PendingInputSurvives_TheLeadIsRebuilt"/>.</b> The first pins the harness
    /// to a known-good reading: with no lead there is nothing to lose, and the answer must be
    /// <c>0</c> — the same answer <c>HeldMovementParityTests</c> gives under zero-latency
    /// conditions. The second proves the harness can tell the two paths apart, which is
    /// exactly what the old one could not do. Without both, a reading here means nothing.
    /// </para>
    /// <para>
    /// <b>The defect.</b> <c>Advance</c> integrates the held direction on every base tick,
    /// including ticks with no input, so the client legitimately runs ahead of the last tick
    /// the server simulated. <c>Reconcile</c> rebuilds that lead by replaying the base-tick
    /// timeline — but the replay reads its start tick from <c>_pending[_head]</c>, so when a
    /// snapshot acknowledges *every* outstanding input the buffer empties, the replay is
    /// skipped, and the lead is dropped. The resulting correction is not a disagreement about
    /// anything; it is exactly the lead the client is supposed to hold.
    /// </para>
    /// <para>
    /// Inputs go at 15 Hz against a 60 Hz base tick, so after the server acknowledges the
    /// newest input there is a window of up to four base ticks before the next one is
    /// recorded. A snapshot landing in that window empties the buffer. This is ordinary play,
    /// not an edge case.
    /// </para>
    /// <para>
    /// <b>These tests pass today, and several of them assert the defect.</b> They are
    /// characterization, not aspiration: a fixture that failed would be reverted or ignored,
    /// and an ignored fixture is how the last one died. Each such assertion says so at the
    /// call site and states what it becomes when #53 is fixed.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class ReconcileLeadTests
    {
        private const int BaseHz = 60;
        private const int SendHz = 15;
        /// <summary>Base ticks between two sends at <see cref="SendHz"/> — the cadence.</summary>
        private const int SendEvery = BaseHz / SendHz;   // 4

        /// <summary>
        /// Base ticks a held direction survives after the input that set it — the silence
        /// timeout both sides read from <see cref="GameConstants.MaxBankedMovementMs"/>.
        /// Fifteen at 60Hz, and no longer the same number as <see cref="SendEvery"/>: the
        /// window stopped being inferred from the client's own send cadence.
        /// </summary>
        private static readonly int HoldWindow = GameConstants.MaxBankedMovementTicks(BaseHz);
        private const float Speed = 5f;

        private static float Dt => MovementSystem.DeltaTimeForTickRate(BaseHz);
        private static MapBounds Bounds => MapBounds.Default;

        /// <summary>One base-tick step at full stick, in world units. The unit every
        /// measurement below is reported in, so a reading is legible without arithmetic.</summary>
        private static float StepLength
        {
            get
            {
                var probe = new EntityState { Position = Vec2.Zero, Speed = Speed, Dead = false };
                MapBounds bounds = Bounds;
                MovementSystem.TryMove(in probe, 1f, 0f, Dt, in bounds, out Vec2 moved);
                return moved.X;
            }
        }

        private static LocalMovePredictor Predictor()
        {
            var p = new LocalMovePredictor(new PredictionSettings(BaseHz, Speed, Bounds));
            p.SetHoldTicks(SendEvery);
            // The first Reconcile only seeds; it does not measure. Every test starts past it.
            p.Reconcile(Vec2.Zero, 0);
            return p;
        }

        /// <summary>
        /// The server's schedule, restated for comparison only. It drives the same
        /// <see cref="MovementSystem.TryMove"/> the client does, so the arithmetic is shared
        /// and only the scheduling differs — the same approach
        /// <c>HeldMovementParityTests</c> takes, and for the same reason: asserting against a
        /// constant is how a harness ends up measuring itself.
        /// </summary>
        private static Vec2 ServerAfter(int inputs, float moveX, float moveY)
        {
            var position = Vec2.Zero;
            long heldFrom = 0;
            long lastMove = 0;
            long tick = 1;                 // the base tick the first input lands on

            for (var i = 0; i < inputs; i++)
            {
                // Rule 1 on the input's own tick: a tick the hold already stepped coalesces
                // into it rather than stepping twice. Unreachable under the old window,
                // where the hold expired on exactly the tick the next input arrived on --
                // which is why this model could count the input tick as one of the
                // interval's four and still agree.
                if (lastMove != tick && StepOnce(ref position, moveX, moveY))
                {
                    heldFrom = tick;
                    lastMove = tick;
                }

                for (var k = 0; k < SendEvery; k++)
                {
                    tick++;

                    if (heldFrom != 0 && tick != heldFrom && tick - heldFrom <= HoldWindow
                        && StepOnce(ref position, moveX, moveY))
                    {
                        lastMove = tick;
                    }
                }
            }

            return position;
        }

        private static bool StepOnce(ref Vec2 position, float moveX, float moveY)
        {
            var probe = new EntityState { Position = position, Speed = Speed, Dead = false };
            MapBounds bounds = Bounds;
            MoveResult result = MovementSystem.TryMove(
                in probe, moveX, moveY, Dt, in bounds, out Vec2 moved);

            if (result is MoveResult.Accepted or MoveResult.Clamped)
            {
                position = moved;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Drives the client the way the live path does: record input <c>i</c>, then
        /// <see cref="LocalMovePredictor.Advance"/> once per base tick in the send interval.
        /// The same driver <c>HeldMovementParityTests</c> uses, so the state it produces is
        /// the one that fixture proves equals <see cref="ServerAfter"/>.
        /// </summary>
        private static void SendInputs(LocalMovePredictor p, int inputs, float x, float y)
        {
            // Input tick is the input's SEQUENCE number, not a base tick. RecordInput takes
            // the tick handed to SendInput — the server's monotonic InputCursor — while the
            // local base tick is a separate counter that Advance drives. PendingInput stores
            // both, and Reconcile's ackTick is the input tick.
            //
            // Conflating the two is not a hypothetical: the first draft of this fixture
            // passed base ticks as input ticks and read a correction that was off by exactly
            // one step at every lead, including a lead of zero. That is the same shape of
            // error that made the previous ReconcileLeadTests return a constant.
            for (var i = 1; i <= inputs; i++)
            {
                p.RecordInput(i, x, y);
                for (var k = 0; k < SendEvery; k++)
                {
                    p.Advance(1f / BaseHz);
                }
            }
        }

        /// <summary>
        /// Advances base ticks with no input recorded — the held motion that runs between
        /// sends, and the lead this fixture is about.
        /// </summary>
        /// <remarks>
        /// Only produces motion while the hold window is open, which is
        /// <see cref="HoldWindow"/> base ticks from the last input. Advancing past that moves
        /// nothing, so a lead has to be built inside the window — the first draft of this
        /// fixture advanced after a fully-consumed window and measured a correction of
        /// exactly zero at every lead, which looked like the defect being absent.
        /// </remarks>
        private static void HoldFor(LocalMovePredictor p, int baseTicks)
        {
            for (var k = 0; k < baseTicks; k++)
            {
                p.Advance(1f / BaseHz);
            }
        }

        /// <summary>
        /// Builds the state the issue describes: the server has acknowledged every input and
        /// produced its snapshot at the tick of the newest one, while the client has held its
        /// direction forward for <paramref name="leadTicks"/> base ticks since.
        /// </summary>
        /// <remarks>
        /// The authoritative position is the client's own position at the instant the newest
        /// input was applied. That is legitimate rather than circular: under this driver
        /// <c>HeldMovementParityTests</c> proves the client and the server agree step for
        /// step, so the client's position at that tick is the server's. Building it that way
        /// avoids restating the server's schedule a second time and getting it subtly wrong,
        /// which is how the first draft of this fixture read one step off at every lead.
        /// </remarks>
        private static (Vec2 Authoritative, long AckTick) HoldPast(
            LocalMovePredictor p, int inputs, int leadTicks, float x, float y)
        {
            SendInputs(p, inputs - 1, x, y);

            long ackTick = inputs;
            p.RecordInput(ackTick, x, y);

            // What the server has after processing that input, before the client holds on.
            Vec2 authoritative = p.SimulatedPosition;

            HoldFor(p, leadTicks);
            return (authoritative, ackTick);
        }

        /// <summary>Correction in steps rather than world units, so a reading is legible.</summary>
        private static float CorrectionInSteps(LocalMovePredictor p) => p.LastCorrection / StepLength;

        // ── The gap between the snapshot and the first unacknowledged input ──

        /// <summary>
        /// The ticks between the snapshot's own tick and the first input the server has not
        /// seen are replayed, not skipped.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>authoritative</c> is where the entity stood on <c>serverBaseTick</c>. Starting
        /// the replay at the first pending input's tick — which is what it did — silently
        /// drops every tick in between, and with them the held motion the server took over
        /// that stretch.
        /// </para>
        /// <para>
        /// <b>It is not an edge case.</b> Inputs go at 15 Hz against a 60 Hz base tick and an
        /// acknowledgement normally rides the snapshot that carries it, so the gap is one
        /// full input interval on EVERY reconcile. Measured against a live server it read as
        /// a correction of exactly <b>4.01 steps</b>, snapshot after snapshot, with the
        /// prediction clock in step and every other counter clean: a backward tug fifteen
        /// times a second, which is what a player calls jerky and what no counter names.
        /// </para>
        /// </remarks>
        [Test]
        public void TheGapBetweenTheSnapshotAndTheOldestPendingInputIsReplayed()
        {
            var p = Predictor();

            // Six inputs, a tick apart, so the buffer holds a real timeline.
            SendInputs(p, Inputs, 1f, 0f);

            // The server has acknowledged everything up to the last input and produced its
            // snapshot at that moment; the client has held on for part of an interval since.
            long ackTick = Inputs;
            Vec2 authoritative = p.SimulatedPosition;
            long snapshotTick = p.BaseTick;

            HoldFor(p, 2);

            // One more input, unacknowledged, a further two ticks on -- so there are ticks
            // between the snapshot and it that only the hold covers.
            HoldFor(p, 2);
            p.RecordInput(ackTick + 1, 1f, 0f);

            Vec2 expected = p.SimulatedPosition;

            p.Reconcile(authoritative, ackTick, snapshotTick);

            float error = System.Math.Abs(p.SimulatedPosition.X - expected.X);

            Assert.That(p.PendingCount, Is.GreaterThan(0),
                "the unacknowledged input must survive, or this is not the path under test");

            Assert.That(error, Is.LessThan(StepLength * 0.5f),
                $"reconciling moved the prediction {error / StepLength:F2} steps. The ticks " +
                "between the snapshot and the oldest pending input carry held motion the " +
                "server took; skipping them subtracts them from the client on every " +
                "snapshot.");
        }

        // ── Calibration ───────────────────────────────────────────────────────────────
        //
        // Everything below rests on these two. They are the reason a number out of this
        // fixture is a reading rather than an artefact.

        private const int Inputs = 6;

        /// <summary>
        /// With no lead to lose, the correction must be zero — on the very path the defect
        /// lives on, an empty pending buffer.
        /// </summary>
        /// <remarks>
        /// This is the anchor, and it is calibrated by construction rather than by assertion:
        /// the state it reconciles from is the one <c>HeldMovementParityTests</c> already
        /// proves equals the server's, reached by the same driver. If this reads non-zero the
        /// harness is measuring itself — which is exactly how the previous fixture of this
        /// name died, returning a constant 1.000 for every case including this one.
        /// </remarks>
        [Test]
        public void ZeroLead_CorrectsByNothing()
        {
            var p = Predictor();
            SendInputs(p, Inputs, 1f, 0f);

            // Acknowledges every outstanding input, so the buffer empties — the defect's
            // precondition — with the server having simulated exactly as far as the client.
            p.Reconcile(ServerAfter(Inputs, 1f, 0f), Inputs);

            Assert.That(p.PendingCount, Is.Zero,
                "the acknowledgement should have emptied the buffer; without that this test " +
                "is not exercising the path the defect lives on");
            Assert.That(CorrectionInSteps(p), Is.EqualTo(0f).Within(0.01f),
                "with no lead to lose the correction must be zero");
        }

        /// <summary>
        /// The harness must read differently for the anchored and unanchored paths. If it
        /// cannot, it cannot evaluate a fix, and every number it produces is noise.
        /// </summary>
        /// <remarks>
        /// Both readings are taken here and compared against each other rather than against a
        /// fixed threshold, because the comparison is the property that matters: the previous
        /// fixture of this name returned the same value on both paths, and a threshold either
        /// reading happened to satisfy would have hidden that.
        ///
        /// <para>The anchor has to PREDATE the lead. An input recorded after the held ticks
        /// starts the replay window at the current tick, so it rebuilds nothing — measured at
        /// 2.000 steps, identical to the unanchored path. That is a real property of the
        /// replay, not a quirk of the fixture, and it is why the anchored case here sends its
        /// extra input in the middle of the lead rather than at the end.</para>
        /// </remarks>
        [Test]
        public void PendingInputSurvives_TheLeadIsRebuilt()
        {
            const int lead = 3;

            var unanchored = Predictor();
            var (authA, ackA) = HoldPast(unanchored, Inputs, lead, 1f, 0f);
            unanchored.Reconcile(authA, ackA);
            float unanchoredSteps = CorrectionInSteps(unanchored);

            var anchored = Predictor();
            SendInputs(anchored, Inputs - 1, 1f, 0f);
            long ackTick = Inputs;
            anchored.RecordInput(ackTick, 1f, 0f);
            Vec2 authoritative = anchored.SimulatedPosition;

            // The extra input lands INSIDE the lead, so the replay window starts before the
            // held motion it has to rebuild.
            HoldFor(anchored, 1);
            anchored.RecordInput(ackTick + 1, 1f, 0f);
            HoldFor(anchored, lead - 1);

            anchored.Reconcile(authoritative, ackTick);
            float anchoredSteps = CorrectionInSteps(anchored);

            Assert.That(anchored.PendingCount, Is.GreaterThan(0),
                "the unacknowledged input must survive, or this is the empty-buffer path again");
            Assert.That(anchored.ReplayedSteps, Is.GreaterThan(0),
                "with an anchor present the replay must run — the behaviour this test exists " +
                "to distinguish from the empty-buffer path");
            Assert.That(unanchored.ReplayedSteps, Is.Zero,
                "the unanchored path must skip the replay, or the two are not being compared");

            Assert.That(anchoredSteps, Is.LessThan(unanchoredSteps),
                $"the anchored path ({anchoredSteps:F3} steps) must correct by LESS than the " +
                $"unanchored one ({unanchoredSteps:F3}). Equal readings mean the harness " +
                "cannot tell the paths apart, which is precisely why the previous fixture of " +
                "this name was shipped [Ignore]d and then deleted.");
        }

        // ── The defect, pinned ────────────────────────────────────────────────────────

        /// <summary>
        /// An acknowledgement that empties the buffer discards the held motion accumulated
        /// since the acknowledged tick. The size tracks the acknowledgement interval, not any
        /// disagreement between client and server.
        /// </summary>
        /// <remarks>
        /// <b>Asserts the defect.</b> When #53 is fixed the expected value becomes <c>0</c> —
        /// the client is entitled to that lead, and a correct <c>Reconcile</c> keeps it.
        /// </remarks>
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void EmptyBuffer_DiscardsTheLead_ScalingWithTheAcknowledgementInterval(int leadTicks)
        {
            var p = Predictor();

            // The window between an acknowledgement and the next send: up to four base ticks
            // at 15 Hz on a 60 Hz base tick. Ordinary play, not an edge case.
            var (authoritative, ackTick) = HoldPast(p, Inputs, leadTicks, 1f, 0f);

            p.Reconcile(authoritative, ackTick);

            Assert.That(p.PendingCount, Is.Zero, "precondition: the buffer must be empty");
            Assert.That(p.ReplayedSteps, Is.Zero,
                "the replay is skipped with no anchor — that skip is the defect");
            Assert.That(CorrectionInSteps(p), Is.EqualTo(leadTicks).Within(0.25f),
                $"expected the discarded lead to be about {leadTicks} step(s). This pins the " +
                "DEFECT (#53); when it is fixed the expected value becomes 0.");
        }

        /// <summary>
        /// The correction grows with the lead rather than sitting at a constant — asserted, so
        /// a regression into constancy fails instead of being believed.
        /// </summary>
        [Test]
        public void TheReadingScales_ItIsNotAConstant()
        {
            float Measure(int leadTicks)
            {
                var p = Predictor();
                var (authoritative, ackTick) = HoldPast(p, Inputs, leadTicks, 1f, 0f);
                p.Reconcile(authoritative, ackTick);
                return CorrectionInSteps(p);
            }

            float one = Measure(1);
            float three = Measure(3);

            Assert.That(three, Is.GreaterThan(one + 1f),
                $"a three-tick lead ({three:F3}) must read materially larger than a one-tick " +
                $"lead ({one:F3}). Equal readings mean the harness returns a constant, the " +
                "failure that made the previous fixture worthless.");
        }

        // ── The three-argument overload: the lead survives ────────────────────────────

        /// <summary>
        /// Given the tick the snapshot was produced on, an acknowledgement that empties the
        /// buffer keeps the lead instead of discarding it.
        /// </summary>
        /// <remarks>
        /// The same construction as
        /// <see cref="EmptyBuffer_DiscardsTheLead_ScalingWithTheAcknowledgementInterval"/>,
        /// differing only in which overload is called — so the pair isolates the parameter
        /// rather than comparing two differently-built situations.
        /// </remarks>
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void WithTheSnapshotTick_TheLeadSurvives(int leadTicks)
        {
            var p = Predictor();
            var (authoritative, ackTick) = HoldPast(p, Inputs, leadTicks, 1f, 0f);

            // The snapshot is from the tick the acknowledged input was applied on; the client
            // has held forward for `leadTicks` since.
            long serverBaseTick = p.BaseTick - leadTicks;

            int replayedBefore = p.ReplayedSteps;
            p.Reconcile(authoritative, ackTick, serverBaseTick);

            Assert.That(p.PendingCount, Is.Zero, "precondition: the buffer must be empty");

            // THE OUTCOME, not the mechanism. This asserted `ReplayedSteps == leadTicks`,
            // which pinned how the lead was preserved rather than that it was: the reconcile
            // rebuilt the held motion since the snapshot by replaying it forward from the
            // server's answer. It does not replay any more. It compares the snapshot against
            // where the client believed it was AT THAT SNAPSHOT'S TICK, from a recorded
            // history, and everything the client did since is kept by construction rather
            // than reconstructed.
            //
            // Why that replaced it: extrapolating forward is only right when the tick lead
            // is travel the server has not seen. When it is clock phase -- ticks advanced
            // before the first input, or ticks an input had already stepped -- the replay
            // adds motion the client never made. Measured live at an 8-tick lead it added
            // six steps per snapshot, fifteen times a second, with every counter clean.
            Assert.That(CorrectionInSteps(p), Is.EqualTo(0f).Within(0.05f),
                "the lead was discarded: the client was pulled back to a position it had " +
                "already moved on from, which is the backward tug at snapshot rate that " +
                "#53 is about.");

            Assert.That(p.ReplayedSteps, Is.EqualTo(replayedBefore),
                "the history answered, so nothing needed replaying. A replay here means the " +
                "history did not reach the snapshot's tick, which for a lead of " +
                $"{leadTicks} tick(s) would be a bug in the ring rather than in the lead.");
        }

        /// <summary>
        /// With no lead, the overload changes nothing. This is the case that rejected the
        /// cheaper fix, and it is here so a regression to that shape fails immediately.
        /// </summary>
        /// <remarks>
        /// Anchoring the replay to the acknowledged input's base tick — available locally,
        /// no parameter needed — rebuilt the lead correctly and then over-replayed whenever
        /// the snapshot already covered the hold window, reading <b>2.99999 steps</b> here
        /// where the answer is zero. The anchor gives a start; only the snapshot's tick
        /// gives the end.
        /// </remarks>
        [Test]
        public void WithTheSnapshotTick_ZeroLeadStillCorrectsByNothing()
        {
            var p = Predictor();
            SendInputs(p, Inputs, 1f, 0f);

            p.Reconcile(ServerAfter(Inputs, 1f, 0f), Inputs, p.BaseTick);

            Assert.That(p.PendingCount, Is.Zero);
            Assert.That(p.ReplayedSteps, Is.Zero,
                "the snapshot is current, so there is nothing after it to replay");
            Assert.That(CorrectionInSteps(p), Is.EqualTo(0f).Within(0.01f),
                "no lead, no correction");
        }

        /// <summary>
        /// The two-argument form is untouched, which is the contract
        /// <c>com.cuvara.dots</c> depends on.
        /// </summary>
        [Test]
        public void TheTwoArgumentFormIsUnchanged()
        {
            var withoutTick = Predictor();
            var (authA, ackA) = HoldPast(withoutTick, Inputs, 3, 1f, 0f);
            withoutTick.Reconcile(authA, ackA);

            Assert.That(withoutTick.ReplayedSteps, Is.Zero,
                "without the tick there is still no anchor, so the replay is still skipped");
            Assert.That(CorrectionInSteps(withoutTick), Is.EqualTo(3).Within(0.25f),
                "and the lead is still discarded — callers that cannot supply the tick keep " +
                "today's behaviour exactly, including today's defect");
        }

        /// <summary>
        /// The discarded lead is smoothed rather than snapped at these sizes, which is why the
        /// defect survived: it reads as ordinary netcode softness, not as a teleport.
        /// </summary>
        [Test]
        public void TheDiscardedLeadIsSmoothed_WhichIsWhyItWentUnnoticed()
        {
            var p = Predictor();
            var (authoritative, ackTick) = HoldPast(p, Inputs, 2, 1f, 0f);

            p.Reconcile(authoritative, ackTick);

            Assert.That(p.Snaps, Is.Zero,
                "a correction this size goes through the render offset, not a snap — the " +
                "reason this reads as softness rather than as a bug, and why no counter showed it");
            Assert.That(p.SmoothedCorrections, Is.GreaterThan(0),
                "it should have been absorbed as a smoothed correction");
        }
    }
}
