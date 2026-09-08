using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Pins the <b>third</b> outcome a reconcile can have — adopting the authoritative
    /// position wholesale — and that <see cref="LocalMovePredictor.Adoptions"/> counts it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this fixture exists.</b> A reconcile was documented as having two outcomes: it
    /// is answered from the history (<c>HistoryHits</c>), or it falls back to replaying the
    /// ticks the server has not seen (<c>ReplayedSteps</c>). There is a third. When the
    /// history misses and the fallback finds nothing to replay — the pending buffer is empty
    /// and the snapshot's tick is not behind the client's clock, so the held-forward branch
    /// of #53 does not fire either — the prediction is simply <i>set</i> to the server's
    /// position. That is the largest correction this class can make, and until
    /// <c>Adoptions</c> existed it moved no counter at all.
    /// </para>
    /// <para>
    /// <b>It was measured, not theorised.</b> A live run reporting
    /// <c>reconciles from history 115 hit, 34 missed</c> reported <c>replayed steps 2</c>.
    /// Thirty-two corrections had been absorbed with every counter in the class standing
    /// still, under a report line that called <c>replayed steps 0</c> the healthy reading.
    /// </para>
    /// <para>
    /// <b>The discrimination this fixture is built for.</b>
    /// <see cref="MissWithNothingToReplay_AdoptsWholesale_AndIsCounted"/> is the test that
    /// fails without the counter; the other three are what stop it from being satisfied by
    /// a counter that simply increments on every reconcile. A hit must never adopt, a miss
    /// with pending input must replay instead, and a miss whose snapshot is behind the
    /// clock must rebuild the held lead (#53) rather than throw it away. All four outcomes
    /// are exercised through the public surface, in the states that produce them.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class ReconcileAdoptionTests
    {
        private const int BaseHz = 60;
        private const float Speed = 5f;
        private const int SendEvery = 4;   // 15 Hz sends against a 60 Hz base tick

        private static float Dt => MovementSystem.DeltaTimeForTickRate(BaseHz);
        private static MapBounds Bounds => MapBounds.Default;

        private static LocalMovePredictor Predictor()
        {
            var p = new LocalMovePredictor(new PredictionSettings(BaseHz, Speed, Bounds));
            p.SetHoldTicks(SendEvery);
            // The first Reconcile only seeds; it counts nothing and corrects nothing.
            p.Reconcile(Vec2.Zero, 0);
            return p;
        }

        /// <summary>Advance the client's own clock by <paramref name="ticks"/> base ticks.</summary>
        private static void AdvanceTicks(LocalMovePredictor p, int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                p.Advance(Dt);
            }
        }

        /// <summary>
        /// THE DISCRIMINATING TEST. A snapshot whose tick the client's clock has not reached
        /// yet misses the history; with nothing pending, nothing is replayed on top of the
        /// server's answer, and the prediction is replaced by it outright.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every existing counter is asserted to stand still here, because that is the
        /// defect: without <see cref="LocalMovePredictor.Adoptions"/> the only trace this
        /// left was <c>HistoryMisses</c>, which says the history did not answer — not that
        /// the prediction was discarded.
        /// </para>
        /// <para>
        /// The adopted position is deliberately a long way from the predicted one, so the
        /// assertion is about the SIZE of what went uncounted, not merely about a branch
        /// being taken.
        /// </para>
        /// </remarks>
        [Test]
        public void MissWithNothingToReplay_AdoptsWholesale_AndIsCounted()
        {
            LocalMovePredictor p = Predictor();
            AdvanceTicks(p, 10);

            Assert.That(p.PendingCount, Is.Zero, "the buffer must be empty for this path");

            int replayedBefore = p.ReplayedSteps;
            Vec2 predictedBefore = p.SimulatedPosition;

            // A tick the client has not lived through: the history ring cannot hold it, and
            // it is not behind the clock, so the held-forward branch of #53 does not fire.
            long aheadOfTheClient = p.BaseTick + 2;
            var authoritative = new Vec2(7f, -3f);

            p.Reconcile(authoritative, 0, aheadOfTheClient);

            Assert.That(p.HistoryMisses, Is.EqualTo(1), "the history cannot answer for a tick the client has not reached");
            Assert.That(p.HistoryHits, Is.Zero, "nothing was compared like for like");
            Assert.That(p.ReplayedSteps, Is.EqualTo(replayedBefore), "nothing was replayed on top of the server's answer");

            Assert.That(p.SimulatedPosition.X, Is.EqualTo(authoritative.X).Within(1e-5f));
            Assert.That(p.SimulatedPosition.Y, Is.EqualTo(authoritative.Y).Within(1e-5f));
            Assert.That(new Vec2(
                    p.SimulatedPosition.X - predictedBefore.X,
                    p.SimulatedPosition.Y - predictedBefore.Y).Magnitude,
                Is.GreaterThan(LocalMovePredictor.SmoothingThreshold),
                "this is a snap-sized correction, which is the point: it is the largest " +
                "move the predictor can make and it moved no counter of its own.");

            Assert.That(p.Adoptions, Is.EqualTo(1),
                "the reconcile replaced the prediction with the server's position and " +
                "rebuilt nothing on top of it. Before Adoptions existed, the ONLY reading " +
                "that changed was HistoryMisses — and a miss is documented as 'fell back to " +
                "replaying', which is exactly what did not happen.");
        }

        /// <summary>
        /// A reconcile answered from the history is a comparison, not a replacement, and
        /// must never be counted as an adoption — <c>Adoptions</c> would otherwise read as
        /// a reconcile count with extra steps.
        /// </summary>
        [Test]
        public void HistoryHit_IsNotAnAdoption()
        {
            LocalMovePredictor p = Predictor();
            AdvanceTicks(p, 10);

            // A tick the client HAS lived through, so the history holds it.
            long known = p.BaseTick;

            p.Reconcile(new Vec2(7f, -3f), 0, known);

            Assert.That(p.HistoryHits, Is.EqualTo(1));
            Assert.That(p.Adoptions, Is.Zero,
                "the history path never reaches the fallback, so it cannot adopt.");
        }

        /// <summary>
        /// A miss WITH unacknowledged input replays the ticks the server has not seen. The
        /// prediction is rebuilt on top of the authoritative position rather than replaced
        /// by it, so this is a replay and not an adoption.
        /// </summary>
        [Test]
        public void MissWithPendingInput_Replays_AndIsNotAnAdoption()
        {
            LocalMovePredictor p = Predictor();
            AdvanceTicks(p, 4);

            p.RecordInput(1, 1f, 0f);
            AdvanceTicks(p, 4);

            Assert.That(p.PendingCount, Is.GreaterThan(0), "the input must still be outstanding");

            // Ack nothing (ackTick 0 keeps the input pending) and name a tick past the clock,
            // so the history misses and the fallback has something to replay.
            p.Reconcile(new Vec2(1f, 0f), 0, p.BaseTick + 2);

            Assert.That(p.HistoryMisses, Is.EqualTo(1));
            Assert.That(p.ReplayedSteps, Is.GreaterThan(0), "the outstanding input was replayed");
            Assert.That(p.Adoptions, Is.Zero,
                "something was rebuilt on top of the server's answer, so nothing was adopted.");
        }

        /// <summary>
        /// THE FOURTH OUTCOME. A miss with an empty buffer is not automatically an adoption:
        /// when the snapshot's tick is BEHIND the client's clock, the held direction is
        /// replayed forward over the intervening ticks and the prediction lead survives —
        /// the fix for #53. This is the case the fallback handles correctly, and it is what
        /// makes "empty buffer + miss" too coarse a definition of an adoption.
        /// </summary>
        /// <remarks>
        /// The history normally answers for a tick behind the clock, so reaching this branch
        /// takes a hole in the ring: <see cref="LocalMovePredictor.SeedBaseTick"/> jumps the
        /// clock to the server's at join, and the ticks jumped over were never recorded.
        /// That is a join, not a contrivance.
        /// </remarks>
        [Test]
        public void MissBehindTheClockWithAHold_ReplaysTheLead_AndIsNotAnAdoption()
        {
            LocalMovePredictor p = Predictor();
            p.SeedBaseTick(1000);          // ticks 2..999 were never lived through
            Assert.That(p.BaseTick, Is.EqualTo(1000));

            p.RecordInput(1, 1f, 0f);      // sets the live hold, and leaves one pending
            AdvanceTicks(p, 2);            // clock now 1002, hold still inside its window

            // ackTick 1 retires the only pending input, emptying the buffer, and tick 997 is
            // behind the clock but was never recorded — a miss with nothing pending.
            p.Reconcile(new Vec2(0.5f, 0f), 1, 997);

            Assert.That(p.HistoryMisses, Is.EqualTo(1));
            Assert.That(p.PendingCount, Is.Zero, "the acknowledgement emptied the buffer");
            Assert.That(p.ReplayedSteps, Is.GreaterThan(0),
                "the held direction was replayed over ticks 998..1002 — the #53 lead.");
            Assert.That(p.Adoptions, Is.Zero,
                "an empty buffer alone does not make an adoption. What makes one is nothing " +
                "being rebuilt, which is why Adoptions is measured off ReplayedSteps rather " +
                "than off the branch conditions.");
        }
    }
}
