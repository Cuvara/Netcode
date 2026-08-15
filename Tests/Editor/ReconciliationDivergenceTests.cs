using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Establishes what a correction of exactly <c>0.000</c> means, and proves the
    /// reconciliation mechanism produces a non-zero one when — and only when — the client
    /// and the server actually disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this fixture exists.</b> The first live run of
    /// <c>PredictionLatencyMeasurement</c> reported <c>max correction 0.0000</c> across
    /// every sample, and an assertion there called that "the signature of the predictor
    /// reconciling against its own output". <b>That assertion was wrong</b>, and the same
    /// run disproved it: <c>replayed steps 3</c> means <c>Reconcile</c> fired and replay
    /// ran, which is precisely what open-loop cannot do.
    /// </para>
    /// <para>
    /// On localhost, with no loss and <c>Shared.GameLogic</c> bit-exact on both sides,
    /// <b>zero divergence is the designed outcome</b> — it is what ADR-10, the
    /// FMA-denying split in <c>Integrate</c>, and the golden vectors are all for. A
    /// correction of zero is the system working, not failing.
    /// </para>
    /// <para>
    /// So "is reconciliation alive?" cannot be answered by the size of the correction.
    /// <see cref="LocalMovePredictor.ReplayedSteps"/> answers it; <c>LastCorrection</c>
    /// answers a different question — "do the two sides disagree?" — whose healthy answer
    /// on a lossless link is *no*. These tests pin both readings so the distinction cannot
    /// be lost again.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class ReconciliationDivergenceTests
    {
        private const int TickRate = 15;
        private const float ServerSpeed = 5f;

        private static MapBounds Bounds => MapBounds.Default;

        private static readonly (float x, float y)[] Walk =
        {
            (1f, 0f), (1f, 0f), (1f, 0f), (0.5f, 0.5f),
        };

        /// <summary>
        /// A stand-in for the server: one <see cref="MovementSystem.TryMove"/> per accepted
        /// input, at the speed the server is using.
        /// </summary>
        private static Vec2 ServerWalk(Vec2 from, (float x, float y)[] inputs, float speed)
        {
            float dt = MovementSystem.DeltaTimeForTickRate(TickRate);
            Vec2 pos = from;

            foreach (var (x, y) in inputs)
            {
                var probe = new EntityState { Position = pos, Speed = speed, Dead = false };
                if (MovementSystem.TryMove(in probe, x, y, dt, Bounds, out var moved)
                    is MoveResult.Accepted or MoveResult.Clamped)
                {
                    pos = moved;
                }
            }

            return pos;
        }

        private static LocalMovePredictor Seeded(float clientSpeed)
        {
            var p = new LocalMovePredictor(new PredictionSettings(TickRate, clientSpeed, Bounds));
            p.Reconcile(Vec2.Zero, 0);
            return p;
        }

        private static LocalMovePredictor AfterWalk(float clientSpeed)
        {
            var p = Seeded(clientSpeed);
            for (var i = 0; i < Walk.Length; i++)
            {
                p.RecordInput(i + 1, Walk[i].x, Walk[i].y);
            }
            return p;
        }

        // ── Agreement: zero is correct ──

        [Test]
        public void MatchedSpeedAndFullAcknowledgementProducesExactlyZeroCorrection()
        {
            var predictor = AfterWalk(ServerSpeed);

            predictor.Reconcile(ServerWalk(Vec2.Zero, Walk, ServerSpeed), Walk.Length);

            Assert.That(predictor.LastCorrection, Is.Zero,
                "with the same code, the same speed and nothing lost, the client's " +
                "prediction and the server's result are the same bits. A non-zero " +
                "correction here would mean the shared-logic guarantee had broken.");
        }

        /// <summary>
        /// The exact shape the live run produced: replay ran <b>and</b> the correction was
        /// zero. This is the case the old assertion mistook for open-loop.
        /// </summary>
        [Test]
        public void ReplayCanRunAndStillProduceZeroCorrection()
        {
            var predictor = AfterWalk(ServerSpeed);

            // Server has acknowledged only the first two of four; the rest must replay.
            predictor.Reconcile(ServerWalk(Vec2.Zero, new[] { Walk[0], Walk[1] }, ServerSpeed), 2);

            Assert.That(predictor.ReplayedSteps, Is.GreaterThan(0),
                "the premise of this test is that replay actually ran");
            Assert.That(predictor.PendingCount, Is.EqualTo(2));
            Assert.That(predictor.LastCorrection, Is.Zero,
                "replay running and the correction being zero are not in tension — that " +
                "combination is what a healthy client on a lossless link looks like, and " +
                "reading it as a fault is what this fixture exists to prevent.");
        }

        // ── Disagreement: the mechanism produces a correction ──

        /// <summary>
        /// The failure <c>rpg-mmo-server#91</c> was about: the client integrating at a
        /// speed the server is not using.
        /// </summary>
        [Test]
        public void AWrongSpeedProducesANonZeroCorrection()
        {
            var predictor = AfterWalk(clientSpeed: 4f);

            predictor.Reconcile(ServerWalk(Vec2.Zero, Walk, ServerSpeed), Walk.Length);

            Assert.That(predictor.LastCorrection, Is.GreaterThan(0f),
                "a client predicting at 4 against a server integrating at 5 must be " +
                "corrected. If this is zero the reconcile is not comparing against the " +
                "server's position at all.");
        }

        /// <summary>
        /// The superseded-input case: several inputs land in one server tick and only the
        /// newest moves the entity, so the client is briefly a step ahead of the server.
        /// </summary>
        [Test]
        public void ASupersededInputProducesANonZeroCorrection()
        {
            var predictor = AfterWalk(ServerSpeed);

            // The server acknowledged all four but only applied three.
            predictor.Reconcile(
                ServerWalk(Vec2.Zero, new[] { Walk[0], Walk[1], Walk[2] }, ServerSpeed),
                Walk.Length);

            Assert.That(predictor.LastCorrection, Is.GreaterThan(0f));
        }

        /// <summary>
        /// Both disagreement cases must also be visible to a caller watching for trouble,
        /// not merely absorbed.
        /// </summary>
        [Test]
        public void ADisagreementIsCountedAsSmoothedOrSnapped()
        {
            var predictor = AfterWalk(clientSpeed: 4f);

            predictor.Reconcile(ServerWalk(Vec2.Zero, Walk, ServerSpeed), Walk.Length);

            Assert.That(predictor.SmoothedCorrections + predictor.Snaps, Is.GreaterThan(0),
                "a correction that is neither smoothed nor snapped was silently dropped");
        }
    }
}
