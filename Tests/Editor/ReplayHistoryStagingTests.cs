using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The replay fallback writes every tick it re-simulated into the history with the position
    /// the replay produced, not with the old entry shifted by the final correction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ReplayRewindHistoryTests"/> pins the respawn case, where the replay differs from
    /// the old timeline by one constant offset, so "shift every entry" and "write the replayed
    /// positions" agree. They stop agreeing as soon as the replay itself diverges from the old
    /// timeline: here the authoritative position puts the player near the map edge, the replayed
    /// inputs walk it into the edge and are clamped there, and the old timeline (which never
    /// met the edge) is no longer a translation of the new one. An entry of a replayed tick that
    /// was merely shifted then holds a position the replay never had, and the next snapshot
    /// acknowledging that tick corrects by the difference.
    /// </para>
    /// <para>
    /// The truth each follow-up snapshot carries is computed here with
    /// <see cref="MovementSystem.TryMove"/>, the server's own step, independently of the
    /// predictor.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class ReplayHistoryStagingTests
    {
        private const int Hz = 60;
        private const float Speed = 5f;
        private const long FirstTick = 1000;

        /// <summary>
        /// A seeded predictor that walks east for <paramref name="ticks"/> base ticks, sending an
        /// input every fourth tick (the 15 Hz cadence against a 60 Hz base tick) and acknowledging
        /// none of them.
        /// </summary>
        private static LocalMovePredictor WalkEast(MapBounds bounds, int ticks)
        {
            var p = new LocalMovePredictor(new PredictionSettings(Hz, Speed, bounds));
            p.Reconcile(Vec2.Zero, 0L); // seeds
            p.SeedBaseTick(FirstTick);
            long input = 0;
            while (p.BaseTick < FirstTick + ticks)
            {
                if ((p.BaseTick - FirstTick) % 4 == 0)
                {
                    p.RecordInput(++input, 1f, 0f);
                }

                p.Advance(1f / Hz);
            }

            return p;
        }

        /// <summary>
        /// Where the server has the player at the end of <paramref name="tick"/>, when it was at
        /// <paramref name="from"/> at the start of <see cref="FirstTick"/> and stepped east once a
        /// tick from then on.
        /// </summary>
        private static Vec2 ServerAt(MapBounds bounds, Vec2 from, long tick)
        {
            float dt = MovementSystem.DeltaTimeForTickRate(Hz);
            Vec2 position = from;
            for (long t = FirstTick; t <= tick; t++)
            {
                var state = new EntityState { Position = position, Speed = Speed, Dead = false };
                MovementSystem.TryMove(in state, 1f, 0f, dt, in bounds, out Vec2 next);
                position = next;
            }

            return position;
        }

        private static void AssertNoCorrectionAt(LocalMovePredictor p, MapBounds bounds, Vec2 from, long tick)
        {
            int hits = p.HistoryHits;
            Vec2 server = ServerAt(bounds, from, tick);
            p.Reconcile(server, 0L, tick);
            Assume.That(p.HistoryHits, Is.EqualTo(hits + 1), $"precondition: tick {tick} was answered by the history");

            Assert.That(p.LastCorrection, Is.LessThan(1e-4f),
                $"the snapshot for tick {tick} put the player at {server}; the history the replay left " +
                $"behind disagreed by {p.LastCorrection:F4} (replayed ticks must hold their replayed positions)");
        }

        [Test]
        public void AReplayThatMeetsTheMapEdge_LeavesEveryReplayedTickExact()
        {
            MapBounds bounds = MapBounds.FromSize(20f, 20f); // x in [-10, 10]
            LocalMovePredictor p = WalkEast(bounds, 40);
            Assume.That(p.PendingCount, Is.GreaterThan(5), "precondition: the inputs are unacknowledged");
            Assume.That(p.SimulatedPosition.X, Is.LessThan(5f), "precondition: the client never met the edge");

            // No snapshot tick: the reconcile cannot consult the history and replays every pending
            // input from the server's answer, which is 0.1 short of the edge.
            var from = new Vec2(9.9f, 0f);
            int misses = p.HistoryMisses;
            p.Reconcile(from, 0L);
            Assume.That(p.HistoryMisses, Is.EqualTo(misses + 1), "precondition: the replay path ran");
            Assert.That(Vec2.Distance(p.SimulatedPosition, ServerAt(bounds, from, p.BaseTick)), Is.LessThan(1e-4f));

            // Every replayed tick is now at the edge except the first; a merely shifted history
            // has them strung out behind it at the old spacing instead.
            AssertNoCorrectionAt(p, bounds, from, p.BaseTick - 10);
            AssertNoCorrectionAt(p, bounds, from, FirstTick + 1);
            AssertNoCorrectionAt(p, bounds, from, FirstTick);
        }

        [Test]
        public void AReplayLongerThanTheHistoryRing_KeepsTheNewestTicksExact()
        {
            // 400 replayed ticks against a 256-entry ring: the staging has to keep the NEWEST 256,
            // and the ticks that reach the edge partway through must still land where the replay
            // put them.
            MapBounds bounds = MapBounds.FromSize(60f, 60f); // x in [-30, 30]
            LocalMovePredictor p = WalkEast(bounds, 400);
            Assume.That(p.PendingCount, Is.GreaterThan(90).And.LessThan(LocalMovePredictor.Capacity),
                "precondition: every input is still pending, none dropped");

            // From here the replay walks for 300 ticks and then stands at the edge for the last 100.
            float dt = MovementSystem.DeltaTimeForTickRate(Hz);
            var from = new Vec2(30f - 300 * Speed * dt, 0f);
            p.Reconcile(from, 0L);
            Assert.That(Vec2.Distance(p.SimulatedPosition, ServerAt(bounds, from, p.BaseTick)), Is.LessThan(1e-3f));

            AssertNoCorrectionAt(p, bounds, from, p.BaseTick - 50);   // at the edge
            AssertNoCorrectionAt(p, bounds, from, p.BaseTick - 200);  // still walking
            AssertNoCorrectionAt(p, bounds, from, p.BaseTick - 255);  // the oldest entry the ring holds
        }

        [Test]
        public void Control_AReplayThatIsAPureOffset_IsExactEitherWay()
        {
            // Without the edge, the replayed timeline is the old one translated, which is the case
            // shifting alone already handled. Pinned so the edge, and nothing else, is what makes
            // the two tests above discriminate.
            MapBounds bounds = MapBounds.FromSize(200f, 200f);
            LocalMovePredictor p = WalkEast(bounds, 40);
            var from = new Vec2(9.9f, 0f);
            p.Reconcile(from, 0L);

            AssertNoCorrectionAt(p, bounds, from, p.BaseTick - 10);
        }
    }
}
