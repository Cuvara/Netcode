using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Prediction;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using Cuvara.Netcode.World;
using Shared.GameLogic.Components;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// A reconnect is a new session: what a binder learnt on the old one (the render clock, the
    /// interpolation buffers, the prediction) must not decide what is drawn on the new one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case that tells a real reset from a cosmetic one is a server whose tick line restarts
    /// LOWER (a restarted or different game server): every tick of the new session is below every
    /// tick the old one left behind, so anything carried across reads the new session as stale.
    /// <c>NetworkClient</c> resets its <see cref="WorldState"/> on every join and the DOTS rejoin
    /// path resets the binder; this pins what the two resets together guarantee.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class SessionBoundaryResetTests
    {
        private const string LocalId = "local-user";
        private const string RemoteId = "remote-user";
        private const double IntervalMs = 1000.0 / 15.0;

        // The newest snapshot puts the remote at 12.25; the render clock runs the target delay
        // behind it, so an interpolated remote is drawn short of it and a raw one exactly at it.
        private const float InterpolatedBelow = 12.2f;

        private sealed class RecordingView : IEntityView
        {
            public readonly Dictionary<string, float> X = new Dictionary<string, float>();
            public readonly List<string> Spawned = new List<string>();
            public void Spawn(string id, bool isLocal, string type) => Spawned.Add(id);
            public void Despawn(string id) => X.Remove(id);
            public void SetState(string id, float x, float y, int hp, int maxHp) => X[id] = x;
        }

        private sealed class ManualClock : IViewClock
        {
            public double NowMs { get; set; }
        }

        private sealed class Session
        {
            public readonly ManualClock Clock = new ManualClock();
            public readonly RecordingView View = new RecordingView();
            public readonly WorldState World = new WorldState();
            public readonly LocalMovePredictor Predictor =
                new LocalMovePredictor(new PredictionSettings(60, 5f, MapBounds.Default));
            public readonly WorldViewBinder Binder;

            public Session() => Binder = new WorldViewBinder(View, Predictor, Clock);

            /// <summary>
            /// <paramref name="count"/> keyframes 4 ticks apart from <paramref name="firstTick"/>, the
            /// remote walking east from <paramref name="remoteFrom"/> at 0.25 per snapshot and the local
            /// player standing at <paramref name="localAt"/>, with 5 ms frames in between.
            /// </summary>
            public void Play(long firstTick, int count, float remoteFrom, float localAt)
            {
                for (int k = 0; k < count; k++)
                {
                    double due = Clock.NowMs + IntervalMs;
                    World.Apply(new ResolvedSnapshot(firstTick + 4 * k, 0L, true, new[]
                    {
                        new ResolvedEntity(RemoteId, "player", remoteFrom + 0.25f * k, 0f, 100, 100),
                        new ResolvedEntity(LocalId, "player", localAt, 0f, 100, 100),
                    }, new string[0]));
                    Binder.Tick(World, LocalId);
                    for (; Clock.NowMs < due; Clock.NowMs += 5.0)
                    {
                        Binder.AdvanceFrame(0.005f);
                        Binder.Tick(World, LocalId);
                    }
                }
            }

            public void NewSession()
            {
                World.Reset();
                Binder.Reset();
            }
        }

        [Test]
        public void ANewSessionOnALowerTickLine_IsDrawnAndInterpolated()
        {
            var s = new Session();
            s.Play(firstTick: 100_000, count: 60, remoteFrom: -20f, localAt: -10f);
            Assume.That(s.Binder.RenderTick, Is.GreaterThan(100_000.0));

            s.NewSession();
            s.Play(firstTick: 500, count: 30, remoteFrom: 5f, localAt: 8f);

            Assert.That(s.Binder.LastServerTick, Is.EqualTo(500L + 4 * 29));
            Assert.That(s.Binder.RenderTick, Is.LessThan(1000.0),
                "the render clock belongs to the new session's tick line, not the old one's");

            // Interpolated a little behind the newest snapshot (5 + 0.25 * 29 = 12.25), never at
            // the old session's positions and never frozen at the new session's first one.
            float remote = s.View.X[RemoteId];
            Assert.That(remote, Is.InRange(11.0f, InterpolatedBelow), $"remote drawn at {remote}");
        }

        [Test]
        public void ANewSession_SeedsThePredictionWithoutACorrection()
        {
            var s = new Session();
            s.Play(firstTick: 100_000, count: 60, remoteFrom: -20f, localAt: -10f);
            s.Predictor.RecordInput(1, 1f, 0f); // an input the old server never acknowledged

            s.NewSession();
            Assert.That(s.Predictor.PendingCount, Is.Zero, "the old session's inputs are gone");

            s.Play(firstTick: 500, count: 30, remoteFrom: 5f, localAt: 8f);

            Assert.That(s.View.X[LocalId], Is.EqualTo(8f).Within(1e-4),
                "the local player is drawn where the new server put it");
            Assert.That(s.Predictor.Snaps, Is.Zero, "seeding a new session is not a correction");
            Assert.That(s.Predictor.HardResyncs, Is.Zero,
                "the clock was seeded on the new tick line, not steered there from the old one");
            Assert.That(s.Predictor.BaseTick, Is.LessThan(1000L));
        }

        [Test]
        public void WithoutTheBinderReset_ALowerTickLine_IsStillInterpolated()
        {
            // A caller that resets only the world (the client's reconnect path at 47ec933, UnityDots
            // audit finding F2). The binder sees the world tick go backwards and treats it as the
            // session boundary it is. Before this, nothing reached the interpolator until the new
            // ticks passed the old ones: every remote was drawn at its raw snapshot position and
            // RenderTick stayed in the old session's tick space (~100 000 here, ~216 000 after an
            // hour at 60 Hz).
            var s = new Session();
            s.Play(firstTick: 100_000, count: 60, remoteFrom: -20f, localAt: -10f);
            int boundaries = s.Binder.TickLineRestarts;

            s.World.Reset();
            s.Play(firstTick: 500, count: 30, remoteFrom: 5f, localAt: 8f);

            Assert.That(s.Binder.TickLineRestarts, Is.EqualTo(boundaries + 1));
            Assert.That(s.Binder.LastServerTick, Is.EqualTo(500L + 4 * 29));
            Assert.That(s.Binder.RenderTick, Is.LessThan(1000.0),
                "the render clock belongs to the new session's tick line, not the old one's");
            float remote = s.View.X[RemoteId];
            Assert.That(remote, Is.InRange(11.0f, InterpolatedBelow),
                $"remote drawn at {remote}: interpolated behind the newest snapshot (12.25), not at it");
        }
    }
}
