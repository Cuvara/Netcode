using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Shared.GameLogic.Components;
using Shared.GameLogic.World;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// A correction made by the replay fallback must carry into the history, exactly as one made
    /// by the history path does (<c>ShiftHistory</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect</b> (rpg-factory testlab, run 20261022T091437Z-S07-c5hc, client 2). A client
    /// stalled for 5 s; meanwhile the server respawned its player 2.24 units away. The first
    /// snapshot after the stall acknowledged a tick older than the 256-tick history ring, so the
    /// reconcile fell back to replaying and correctly moved the prediction onto the respawn
    /// point - recording only the CURRENT tick. Every older history entry still held the
    /// pre-respawn position. The next snapshot acknowledged one of those ticks, the history path
    /// measured the respawn as an error a second time, and applied it on top: the local player
    /// was drawn 2.24 units past where the server had it, then snapped back one snapshot later.
    /// Two visible 2.24-unit jumps from one server event.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class ReplayRewindHistoryTests
    {
        private const int Hz = 60;
        private static readonly Vec2 BeforeRespawn = new Vec2(2.69f, -0.59f);
        private static readonly Vec2 AfterRespawn = new Vec2(0.53f, 0f);

        private static LocalMovePredictor IdleFor(int ticks)
        {
            var p = new LocalMovePredictor(new PredictionSettings(Hz, 5f, MapBounds.Default));
            p.Reconcile(BeforeRespawn, 0L, 0L); // seeds
            p.SeedBaseTick(1000);
            for (int i = 0; i < ticks; i++)
            {
                p.Advance(1f / Hz);
            }

            return p;
        }

        [Test]
        public void AReplayCorrection_IsNotMeasuredAgainByTheNextSnapshot()
        {
            LocalMovePredictor p = IdleFor(600); // the ring now covers ~1345..1600
            Assume.That(p.BaseTick, Is.GreaterThan(1500L));
            int hitsBefore = p.HistoryHits;

            // The stale snapshot: its tick has left the ring, so this is the replay fallback.
            p.Reconcile(AfterRespawn, 0L, 1100L);
            Assume.That(p.HistoryHits, Is.EqualTo(hitsBefore), "precondition: the replay path ran");
            Assert.That(Vec2.Distance(p.SimulatedPosition, AfterRespawn), Is.LessThan(1e-4f));

            // The next snapshot describes a tick the client recorded BEFORE it knew of the respawn.
            p.Reconcile(AfterRespawn, 0L, p.BaseTick - 50);
            Assume.That(p.HistoryHits, Is.EqualTo(hitsBefore + 1), "precondition: the history path ran");

            Assert.That(Vec2.Distance(p.SimulatedPosition, AfterRespawn), Is.LessThan(1e-4f),
                $"the respawn was applied twice: predicted {p.SimulatedPosition}, server {AfterRespawn}");
            Assert.That(p.LastCorrection, Is.LessThan(1e-4f));
        }

        [Test]
        public void Control_WithoutTheStaleSnapshot_TheHistoryPathCorrectsOnce()
        {
            // The same respawn delivered on a tick still in the ring: one correction, by the history
            // path, and the following snapshot finds nothing left to correct. This is the behaviour
            // the replay path must match.
            LocalMovePredictor p = IdleFor(600);
            p.Reconcile(AfterRespawn, 0L, p.BaseTick - 100);
            Assert.That(Vec2.Distance(p.SimulatedPosition, AfterRespawn), Is.LessThan(1e-4f));

            p.Reconcile(AfterRespawn, 0L, p.BaseTick - 50);
            Assert.That(Vec2.Distance(p.SimulatedPosition, AfterRespawn), Is.LessThan(1e-4f));
            Assert.That(p.LastCorrection, Is.LessThan(1e-4f));
        }
    }
}
