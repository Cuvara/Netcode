using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Covers spreading a predicted step across the frames of an input interval, instead
    /// of applying all of it on the one frame the input landed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The problem.</b> <c>_predicted</c> only advances inside
    /// <see cref="LocalMovePredictor.RecordInput"/>, which runs at the input rate. At
    /// 15 Hz input and 350 fps that is ~23 identical frames followed by a jump of a whole
    /// step — the avatar arrives in the right place at the right time and visibly stutters
    /// getting there. Prediction fixed <i>where</i> the avatar is; it did nothing for how
    /// often that is updated.
    /// </para>
    /// <para>
    /// <b>Interpolation within the step, never past it.</b> The rendered position walks
    /// back the unshown fraction of the latest step, so it travels from the pre-step
    /// position to the post-step one across the interval. It is bounded by the step that
    /// was actually taken from an input that was actually submitted, so it is
    /// <b>interpolation, not extrapolation</b> — and that distinction is the whole safety
    /// argument. Carrying motion forward past the last known step would move the avatar
    /// somewhere the player never asked for, and the correction would land exactly when
    /// they released the key and were watching.
    /// </para>
    /// <para>
    /// <b>This does not re-introduce the latency 0.4.0 removed.</b> The avatar begins
    /// moving on the frame after the input rather than teleporting on it; what it does not
    /// do is wait a round trip. Motion onset is a frame, not an interval.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class RenderSmoothingTests
    {
        private const int TickRate = 15;
        private const float Speed = 5f;
        private static float Dt => MovementSystem.DeltaTimeForTickRate(TickRate);
        private static MapBounds Bounds => MapBounds.Default;

        private static LocalMovePredictor Seeded()
        {
            var p = new LocalMovePredictor(new PredictionSettings(TickRate, Speed, Bounds));
            p.Reconcile(Vec2.Zero, 0);
            return p;
        }

        [Test]
        public void AStepIsNotAppliedAllAtOnceOnTheInputFrame()
        {
            var p = Seeded();

            p.RecordInput(1, 1f, 0f);

            Assert.That(p.Position.X, Is.LessThan(p.SimulatedPosition.X),
                "the whole step landed on the input frame — that is the stutter this " +
                "exists to remove");
            Assert.That(p.Position.X, Is.EqualTo(0f).Within(1e-5f),
                "no time has passed since the input, so none of the step has been shown");
        }

        [Test]
        public void ThePositionAdvancesBetweenInputs()
        {
            var p = Seeded();
            p.RecordInput(1, 1f, 0f);

            float previous = p.Position.X;
            var seen = 0;

            // A handful of render frames inside one input interval.
            for (var i = 0; i < 10; i++)
            {
                p.Advance(Dt / 10f);
                float now = p.Position.X;
                if (now > previous) seen++;
                previous = now;
            }

            Assert.That(seen, Is.EqualTo(10),
                "every render frame inside the interval must move the avatar; identical " +
                "frames between inputs are precisely the reported jerkiness");
        }

        [Test]
        public void TheStepIsFullyShownAfterOneInputInterval()
        {
            var p = Seeded();
            p.RecordInput(1, 1f, 0f);

            p.Advance(Dt);

            Assert.That(p.Position.X, Is.EqualTo(p.SimulatedPosition.X).Within(1e-5f),
                "after one interval the rendered position must have caught up exactly");
        }

        // ── The case that decides whether this is safe ──

        /// <summary>
        /// Input stops mid-step: the avatar must arrive at the predicted position and
        /// <b>stop</b>.
        /// </summary>
        /// <remarks>
        /// If the rendered position kept moving on the last known direction it would drift
        /// past where the player released the key and then be snapped back. That is a
        /// worse artefact than the one being fixed, and it happens at the moment the player
        /// is most likely to notice.
        /// </remarks>
        [Test]
        public void WhenInputStopsThePositionConvergesAndDoesNotOvershoot()
        {
            var p = Seeded();
            p.RecordInput(1, 1f, 0f);

            float target = p.SimulatedPosition.X;

            // Ten intervals of silence — far past the one the step covers.
            for (var i = 0; i < 100; i++)
            {
                p.Advance(Dt / 10f);

                Assert.That(p.Position.X, Is.LessThanOrEqualTo(target + 1e-5f),
                    "the rendered position ran past the step the player actually asked " +
                    "for — that is extrapolation, and the snap back is worse than the " +
                    "stutter it was trying to hide");
            }

            Assert.That(p.Position.X, Is.EqualTo(target).Within(1e-5f),
                "it must settle exactly on the predicted position, not near it");
        }

        [Test]
        public void StoppingInputLeavesThePositionCompletelyStill()
        {
            var p = Seeded();
            p.RecordInput(1, 1f, 0f);
            p.Advance(Dt * 2f);

            float settled = p.Position.X;
            for (var i = 0; i < 20; i++)
            {
                p.Advance(Dt);
                Assert.That(p.Position.X, Is.EqualTo(settled).Within(1e-6f),
                    "a stationary avatar must be perfectly stationary");
            }
        }

        // ── Continuity ──

        [Test]
        public void AnInputBoundaryDoesNotJumpTheVisiblePosition()
        {
            var p = Seeded();
            p.RecordInput(1, 1f, 0f);

            // Only nine tenths of the interval elapses before the next input — jitter.
            p.Advance(Dt * 0.9f);
            float before = p.Position.X;

            p.RecordInput(2, 1f, 0f);
            float after = p.Position.X;

            Assert.That(after, Is.EqualTo(before).Within(1e-5f),
                "the unshown remainder of the previous step was discarded, which is a " +
                "visible jump on every input boundary that does not land exactly on time");
        }

        // ── The guarantee this must not break ──

        [Test]
        public void SmoothingDoesNotTouchTheSimulatedPosition()
        {
            var smoothed = Seeded();
            var reference = Seeded();

            var walk = new[] { (1f, 0f), (0f, 1f), (1f, 1f), (-1f, 0.5f) };
            for (var i = 0; i < walk.Length; i++)
            {
                smoothed.RecordInput(i + 1, walk[i].Item1, walk[i].Item2);
                smoothed.Advance(Dt * 0.37f);   // arbitrary partial frames
                reference.RecordInput(i + 1, walk[i].Item1, walk[i].Item2);
            }

            Assert.That(smoothed.SimulatedPosition.X, Is.EqualTo(reference.SimulatedPosition.X),
                "rendering must not perturb the simulation — SimulatedPosition is what " +
                "replay and the server agree on bit-for-bit, and it is bit-exactness that " +
                "the whole shared-logic boundary exists to protect");
            Assert.That(smoothed.SimulatedPosition.Y, Is.EqualTo(reference.SimulatedPosition.Y));
        }

        [Test]
        public void ASnapClearsTheUnshownRemainder()
        {
            var p = Seeded();
            p.RecordInput(1, 1f, 0f);
            p.Advance(Dt * 0.2f);

            // A correction far beyond the smoothing threshold.
            p.Reconcile(new Vec2(50f, 50f), 1);

            Assert.That(p.Snaps, Is.EqualTo(1));
            Assert.That(p.Position, Is.EqualTo(p.SimulatedPosition),
                "after a snap the avatar must be exactly where the server says. A leftover " +
                "step fragment would play out from the corrected position as a second, " +
                "smaller wrong movement.");
        }

        [Test]
        public void ResetClearsSmoothingState()
        {
            var p = Seeded();
            p.RecordInput(1, 1f, 0f);
            p.Advance(Dt * 0.5f);

            p.Reset();

            Assert.That(p.Position, Is.EqualTo(Vec2.Zero));
            Assert.That(p.SimulatedPosition, Is.EqualTo(Vec2.Zero));
        }
    }
}
