using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Shared.GameLogic.Components;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// <see cref="LocalMovePredictor.Reset"/> forgets the history ring, so a new session whose
    /// ticks overlap the old one's cannot be compared against where the OLD session's player was.
    /// </summary>
    /// <remarks>
    /// Before this fix <c>Reset</c> cleared the pending inputs, the seeding and the counters but
    /// not <c>_history</c>/<c>_historyTick</c> (UnityDots audit finding H5). A reconcile on the new
    /// session naming a tick before its first stepped tick (a snapshot tick or an
    /// <c>ack_applied_tick</c> from before the seed) then hit an entry the old session wrote and
    /// "corrected" the new player by the distance between two unrelated positions.
    /// </remarks>
    [TestFixture]
    public sealed class PredictorResetHistoryTests
    {
        private const int Hz = 60;

        [Test]
        public void AfterReset_ATickTheOldSessionRecorded_IsNotAHistoryHit()
        {
            var p = new LocalMovePredictor(new PredictionSettings(Hz, 5f, MapBounds.Default));

            // Old session: walking east from the origin over ticks 4801..5000.
            p.Reconcile(Vec2.Zero, 0L);
            p.SeedBaseTick(4800);
            long input = 0;
            while (p.BaseTick < 5000)
            {
                if (p.BaseTick % 4 == 0) p.RecordInput(++input, 1f, 0f);
                p.Advance(1f / Hz);
            }

            Assume.That(p.SimulatedPosition.X, Is.GreaterThan(10f), "precondition: the old session moved");

            p.Reset();

            // New session, overlapping ticks, standing still somewhere else.
            var spawn = new Vec2(-20f, 3f);
            p.Reconcile(spawn, 0L); // seeds
            p.SeedBaseTick(4900);
            for (int i = 0; i < 10; i++) p.Advance(1f / Hz);
            int hits = p.HistoryHits;

            // A snapshot naming tick 4890: before this session's seed, recorded only by the old one.
            p.Reconcile(spawn, 0L, 4890);

            Assert.That(p.HistoryHits, Is.EqualTo(hits),
                "tick 4890 belongs to the previous session; the history must not answer for it");
            Assert.That(p.LastCorrection, Is.Zero,
                $"the server and the client agree the player is at {spawn}; correcting by " +
                $"{p.LastCorrection:F3} is the old session's position leaking in");
            Assert.That(p.SimulatedPosition.X, Is.EqualTo(spawn.X));
            Assert.That(p.SimulatedPosition.Y, Is.EqualTo(spawn.Y));
        }

        [Test]
        public void Control_WithinOneSession_TheSameTickIsAHit()
        {
            // The history is what answers a tick this session recorded; only the reset forgets it.
            var p = new LocalMovePredictor(new PredictionSettings(Hz, 5f, MapBounds.Default));
            p.Reconcile(Vec2.Zero, 0L);
            p.SeedBaseTick(4800);
            for (int i = 0; i < 100; i++) p.Advance(1f / Hz);
            int hits = p.HistoryHits;

            p.Reconcile(Vec2.Zero, 0L, 4890);

            Assert.That(p.HistoryHits, Is.EqualTo(hits + 1));
            Assert.That(p.LastCorrection, Is.Zero);
        }
    }
}
