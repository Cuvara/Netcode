using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Interpolation;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using Cuvara.Netcode.World;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// An entity removed and added again between two binder ticks is a new incarnation: the view
    /// respawns it and does not interpolate from where the old one left.
    /// </summary>
    /// <remarks>
    /// The binder polls the merged world once per frame. When several snapshots merge in one frame,
    /// a removal and the return of the same id are invisible to it, and the old sample ring carried
    /// over. Live, after a client freeze, the backlog read in one frame did exactly that: entities
    /// re-entering the area of interest were first drawn 0.27 u from where they had left it and 4 u
    /// from where they were (rpg-factory testlab S09 soak 20261023T040945Z-S09-e26z, entity_lifecycle,
    /// all at the freeze-resume frames).
    /// </remarks>
    [TestFixture]
    public sealed class EntityReentryTests
    {
        private const string Id = "enemy-2606";

        private sealed class RecordingView : IEntityView
        {
            public readonly List<string> Events = new List<string>();
            public readonly List<float> X = new List<float>();
            public void Spawn(string id, bool isLocal, string type) => Events.Add("spawn " + id);
            public void Despawn(string id) => Events.Add("despawn " + id);
            public void SetState(string id, float x, float y, int hp, int maxHp)
            {
                if (id == Id) X.Add(x);
            }
        }

        private sealed class ManualClock : IViewClock
        {
            public double NowMs { get; set; }
        }

        private static ResolvedSnapshot At(long tick, bool full, float x, params string[] removed) =>
            new ResolvedSnapshot(tick, 0L, full,
                x >= 0f ? new[] { new ResolvedEntity(Id, "enemy", x, 0f, 16, 16) } : new ResolvedEntity[0],
                removed);

        [Test]
        public void ARemovalAndReturnMergedIntoOneFrame_RespawnsTheEntity()
        {
            var clock = new ManualClock();
            var view = new RecordingView();
            var world = new WorldState();
            var binder = new WorldViewBinder(view, null, clock, InterpolationConfig.Default);

            // Walking along +x at 5 u/s, a snapshot every 66.7 ms, 5 ms frames.
            long tick = 4;
            double now = 0;
            for (int k = 0; k < 30; k++, tick += 4)
            {
                world.Apply(At(tick, k == 0, k * 5f / 15f));
                for (int f = 0; f < 13; f++)
                {
                    now += 5;
                    clock.NowMs = now;
                    binder.Tick(world, "local");
                }
            }

            float left = 29 * 5f / 15f;
            int xBefore = view.X.Count;

            // One frame: out of the area of interest, then back in 40 u away.
            world.Apply(At(tick, false, -1f, Id));
            tick += 4;
            world.Apply(At(tick, false, 40f));
            tick += 4;
            now += 5;
            clock.NowMs = now;
            binder.Tick(world, "local");

            // And on from there.
            for (int k = 1; k < 30; k++, tick += 4)
            {
                world.Apply(At(tick, false, 40f + k * 5f / 15f));
                for (int f = 0; f < 13; f++)
                {
                    now += 5;
                    clock.NowMs = now;
                    binder.Tick(world, "local");
                }
            }

            float minAfter = float.MaxValue;
            for (int i = xBefore; i < view.X.Count; i++) minAfter = System.Math.Min(minAfter, view.X[i]);
            TestContext.WriteLine($"left at {left:F2}, drawn at least at {minAfter:F2} after the return; events {string.Join(", ", view.Events)}");

            Assert.That(view.Events, Is.EqualTo(new[] { "spawn " + Id, "despawn " + Id, "spawn " + Id }),
                "the view ends the old incarnation and spawns the new one");
            Assert.That(minAfter, Is.GreaterThanOrEqualTo(40f - 1e-3f),
                "the new incarnation is never drawn on the way from where the old one left");
            Assert.That(binder.RespawnsWithinAFrame, Is.EqualTo(1));
        }
    }
}
