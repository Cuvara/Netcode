using System;
using System.Collections.Generic;
using Google.Protobuf;
using Shared.GameLogic.Components;

namespace Cuvara.Netcode.Samples.ImportanceIntervalProbe
{
    /// <summary>Population shapes, in the order they stop being convenient.</summary>
    public enum PopulationShape
    {
        /// <summary>Everyone on top of everyone. The shape the published ceiling was measured on.</summary>
        Cluster,
        /// <summary>Players spread over the map, still all players, still all moving.</summary>
        Spread,
        /// <summary>Spread players plus mobs, most of the mobs idle. The shape a game has.</summary>
        Realistic,
    }

    /// <summary>
    /// What an importance-driven replication interval would save, and what it would cost,
    /// measured on real Protobuf snapshot bytes.
    ///
    /// <para><b>This is a MIRROR, and the backend bench is the authority.</b> The thing
    /// being measured is the game server's entity-selection logic, which lives in
    /// <c>GameServer/Snapshot/SnapshotDeltaState.cs</c> and cannot be referenced from a
    /// client. <c>GameServer.Tests/Bench/ImportanceIntervalBench.cs</c> runs the real
    /// encoder and is the number of record; this reimplements enough of it to be explored
    /// with sliders. It reproduces the bench's four reference rows, which is the only
    /// reason to trust it — and if the mirror ever drifts, this scene will keep looking
    /// healthy. Re-check it against the bench before quoting anything from here.</para>
    ///
    /// <para>What is genuinely real: the bytes. Every snapshot is built from the generated
    /// <c>RpgMmo.Wire.V1</c> types and measured with <c>CalculateSize()</c>, the same call
    /// the server's own budget uses, including handle interning and keyframe handle
    /// resets.</para>
    /// </summary>
    public sealed class ImportanceIntervalModel
    {
        public const int KeyframeInterval = 30;

        // ── Tunables ─────────────────────────────────────────────────────────────
        public PopulationShape Shape = PopulationShape.Realistic;
        public int Players = 60;
        public int Mobs = 300;
        /// <summary>Fraction of mobs that move at all. Idle entities are ALREADY free.</summary>
        public float MobMovingFraction = 0.34f;
        public float SpreadUnits = 260f;
        public float AoiRadius = 50f;
        /// <summary>
        /// Retained so the scene's two threshold sliders keep binding; they now move the
        /// SCORE bands rather than distance fractions, which is what the server actually
        /// bands on.
        /// </summary>
        public float NearFraction
        {
            get => ScoreEveryTick;
            set => ScoreEveryTick = value;
        }

        public float MidFraction
        {
            get => ScoreMidBand;
            set => ScoreMidBand = value;
        }

        // ── Results ──────────────────────────────────────────────────────────────
        public double BytesPerObservationToday { get; private set; }
        public double BytesPerObservationTiered { get; private set; }
        public double SavingPercent =>
            BytesPerObservationToday > 0
                ? 100.0 * (BytesPerObservationToday - BytesPerObservationTiered) / BytesPerObservationToday
                : 0.0;
        public int StalenessMaxTicks { get; private set; }
        public int StalenessP99Ticks { get; private set; }
        public long[] TierCounts { get; } = new long[3];   // indexes: interval 1, 2, 4
        public int WorldTick { get; private set; }
        public int ViewerCount { get; private set; }
        public int PopulationCount { get; private set; }

        private sealed class Ent
        {
            public int Key;
            public string Id = "";
            public bool IsPlayer;
            public bool Moves;
            public float X, Y;
            public uint Facing;
            public EntityAction Action;
        }

        private sealed class ViewerState
        {
            // Mirrors SnapshotDeltaState: last values sent, handle table, keyframe counter.
            public readonly Dictionary<int, Ent> LastSent = new();
            public readonly Dictionary<int, uint> Handles = new();
            public readonly Dictionary<int, int> LastSentTick = new();
            public uint NextHandle = 1;
            public int SinceKeyframe;
            public int Phase;
        }

        private readonly List<Ent> _all = new();
        private readonly List<Ent> _viewers = new();
        private ViewerState[] _today = Array.Empty<ViewerState>();
        private ViewerState[] _tiered = Array.Empty<ViewerState>();
        private Random _rng = new Random(1234);

        private long _bytesToday, _bytesTiered, _observations;
        private readonly List<int> _staleness = new();

        public void Rebuild()
        {
            _rng = new Random(1234);
            _all.Clear();
            _viewers.Clear();
            Array.Clear(TierCounts, 0, TierCounts.Length);
            _staleness.Clear();
            _bytesToday = _bytesTiered = _observations = 0;
            WorldTick = 0;
            StalenessMaxTicks = StalenessP99Ticks = 0;

            float spread = Shape == PopulationShape.Cluster ? 8f : SpreadUnits;
            int key = 0;

            for (int i = 0; i < Players; i++)
            {
                var e = new Ent
                {
                    Key = key++,
                    Id = $"p{i}",
                    IsPlayer = true,
                    Moves = true,
                    X = (float)((_rng.NextDouble() - 0.5) * spread),
                    Y = (float)((_rng.NextDouble() - 0.5) * spread),
                    Facing = (uint)(1 + _rng.Next(65535)),
                    Action = EntityAction.Moving,
                };
                _all.Add(e);
                _viewers.Add(e);
            }

            if (Shape == PopulationShape.Realistic)
            {
                for (int i = 0; i < Mobs; i++)
                {
                    bool moving = _rng.NextDouble() < MobMovingFraction;
                    _all.Add(new Ent
                    {
                        Key = key++,
                        Id = $"m{i}",
                        IsPlayer = false,
                        Moves = moving,
                        X = (float)((_rng.NextDouble() - 0.5) * spread),
                        Y = (float)((_rng.NextDouble() - 0.5) * spread),
                        Facing = (uint)(1 + _rng.Next(65535)),
                        Action = moving ? EntityAction.Moving : EntityAction.Idle,
                    });
                }
            }

            ViewerCount = Math.Min(_viewers.Count, 60);
            PopulationCount = _all.Count;
            _today = new ViewerState[ViewerCount];
            _tiered = new ViewerState[ViewerCount];
            for (int v = 0; v < ViewerCount; v++)
            {
                _today[v] = new ViewerState { Phase = PhaseFor(_viewers[v].Id) };
                _tiered[v] = new ViewerState { Phase = PhaseFor(_viewers[v].Id) };
            }
        }

        /// <summary>Same FNV-1a keyframe phase the server derives from a user id, so the
        /// two arms stagger their keyframes the way real connections do.</summary>
        private static int PhaseFor(string userId)
        {
            const uint offsetBasis = 2166136261, prime = 16777619;
            uint hash = offsetBasis;
            foreach (char c in userId)
            {
                hash = (hash ^ (byte)c) * prime;
                hash = (hash ^ (byte)(c >> 8)) * prime;
            }
            return (int)(hash & 0x7FFFFFFF);
        }

        private int IntervalFor(Ent e, Ent observer)
        {
            if (ReferenceEquals(e, observer)) return 1;
            if (e.Action == EntityAction.Attacking || e.Action == EntityAction.Dead) return 1;

            // The server's own shape: a weighted score, then bands. Mirrored rather than
            // referenced, because ReplicationImportance lives in the game server.
            //
            // The numbers are GAMESERVER_IMPORTANCE=balanced and
            // GAMESERVER_REPLICATION_SCHEDULE=tiered, and they matter: a merely-moving
            // PLAYER scores distance + type = 5, UNDER the 8 that buys every-tick
            // treatment, so players land in the middle band too. An earlier version of this
            // scene gave near players interval 1 and therefore reported 0.0% on Cluster,
            // while the real server measured -47.3% on the same population. See
            // BENCHMARK.md Part XIV §42 -- a mirror that models a policy nobody runs
            // answers a question nobody asked.
            float dx = e.X - observer.X, dy = e.Y - observer.Y;
            float d2 = (dx * dx) + (dy * dy);
            float r = AoiRadius > 0f ? AoiRadius : 1f;

            float score = (WeightDistance * (1f / (1f + (d2 / (r * r)))))
                        + (e.IsPlayer ? WeightType : 0f);

            if (score >= ScoreEveryTick) return 1;
            if (score >= ScoreMidBand) return 2;
            return 4;
        }

        /// <summary>GAMESERVER_IMPORTANCE=balanced, distance weight.</summary>
        public float WeightDistance = 2f;

        /// <summary>GAMESERVER_IMPORTANCE=balanced, entity-type weight.</summary>
        public float WeightType = 3f;

        /// <summary>Top band of GAMESERVER_REPLICATION_SCHEDULE=tiered: every world tick.</summary>
        public float ScoreEveryTick = 8f;

        /// <summary>Middle band; below it an entity waits four world ticks.</summary>
        public float ScoreMidBand = 3f;

        /// <summary>Advance one WORLD tick and accumulate both arms.</summary>
        public void Step()
        {
            WorldTick++;
            foreach (Ent e in _all)
            {
                if (!e.Moves) continue;
                double ang = _rng.NextDouble() * Math.PI * 2;
                e.X += (float)Math.Cos(ang) * 0.33f;
                e.Y += (float)Math.Sin(ang) * 0.33f;
            }

            float r2 = AoiRadius * AoiRadius;
            var nearby = new List<Ent>(_all.Count);

            for (int v = 0; v < ViewerCount; v++)
            {
                Ent obs = _viewers[v];
                nearby.Clear();
                foreach (Ent e in _all)
                {
                    float dx = e.X - obs.X, dy = e.Y - obs.Y;
                    if ((dx * dx) + (dy * dy) <= r2) nearby.Add(e);
                }
                _observations += nearby.Count;

                _bytesToday += Encode(_today[v], nearby, null, obs);
                _bytesTiered += Encode(_tiered[v], nearby, obs, obs);
            }

            BytesPerObservationToday = _observations > 0 ? (double)_bytesToday / _observations : 0;
            BytesPerObservationTiered = _observations > 0 ? (double)_bytesTiered / _observations : 0;

            if (_staleness.Count > 0)
            {
                _staleness.Sort();
                StalenessMaxTicks = _staleness[_staleness.Count - 1];
                StalenessP99Ticks = _staleness[(int)(_staleness.Count * 0.99)];
            }
        }

        /// <summary>
        /// One snapshot for one viewer, returning its exact Protobuf payload size.
        /// <paramref name="tierObserver"/> non-null turns the interval policy on.
        /// </summary>
        private int Encode(ViewerState st, List<Ent> nearby, Ent tierObserver, Ent observer)
        {
            bool full = st.SinceKeyframe >= KeyframeInterval || st.LastSent.Count == 0;
            if (full)
            {
                st.LastSent.Clear();
                st.Handles.Clear();
                st.NextHandle = 1;
                st.SinceKeyframe = st.LastSent.Count == 0 && WorldTick == 1
                    ? st.Phase % KeyframeInterval
                    : 0;
            }
            else
            {
                st.SinceKeyframe++;
            }

            var msg = new RpgMmo.Wire.V1.SnapshotMessage
            {
                Tick = (ulong)WorldTick,
                AckTick = (ulong)WorldTick,
                Full = full,
            };

            var seen = new HashSet<int>();
            foreach (Ent e in nearby)
            {
                seen.Add(e.Key);

                if (!full)
                {
                    // Tier is counted for EVERY observation, before the unchanged check,
                    // to match how the backend bench counts it. Counting only the entities
                    // that changed would describe where the savings came from rather than
                    // what the population looks like -- a different and much rosier
                    // histogram, and not the one the authority prints.
                    int interval = tierObserver != null ? IntervalFor(e, tierObserver) : 1;
                    if (tierObserver != null && interval >= 1 && interval <= 4)
                    {
                        TierCounts[interval == 4 ? 2 : interval - 1]++;
                    }

                    bool known = st.LastSent.TryGetValue(e.Key, out Ent prev);
                    if (known && Same(prev, e)) continue;   // unchanged: already free today

                    if (known && tierObserver != null && interval > 1 &&
                        WorldTick - st.LastSentTick[e.Key] < interval)
                    {
                        _staleness.Add(WorldTick - st.LastSentTick[e.Key]);
                        continue;                            // deferred, NOT removed
                    }
                }

                bool introduce = !st.Handles.TryGetValue(e.Key, out uint handle);
                if (introduce)
                {
                    handle = st.NextHandle++;
                    st.Handles[e.Key] = handle;
                }
                msg.Entities.Add(new RpgMmo.Wire.V1.EntitySnapshot
                {
                    Id = introduce ? e.Id : "",
                    Type = e.IsPlayer ? RpgMmo.Wire.V1.EntityType.Player : RpgMmo.Wire.V1.EntityType.Mob,
                    Handle = handle,
                    X = e.X,
                    Y = e.Y,
                    Hp = 100,
                    MaxHp = 100,
                    Speed = e.IsPlayer ? 5f : 2.5f,
                    FacingBrad = e.Facing,
                    Action = (RpgMmo.Wire.V1.EntityAction)e.Action,
                });
                st.LastSent[e.Key] = Clone(e);
                st.LastSentTick[e.Key] = WorldTick;
            }

            if (!full && st.LastSent.Count != seen.Count)
            {
                var gone = new List<int>();
                foreach (var kv in st.LastSent)
                {
                    if (!seen.Contains(kv.Key)) { msg.Removed.Add(kv.Value.Id); gone.Add(kv.Key); }
                }
                foreach (int k in gone)
                {
                    st.LastSent.Remove(k);
                    st.Handles.Remove(k);
                    st.LastSentTick.Remove(k);
                }
            }

            return msg.CalculateSize();
        }

        private static bool Same(Ent a, Ent b) =>
            a.X.Equals(b.X) && a.Y.Equals(b.Y) && a.Facing == b.Facing && a.Action == b.Action;

        private static Ent Clone(Ent e) => new Ent
        {
            Key = e.Key, Id = e.Id, IsPlayer = e.IsPlayer, Moves = e.Moves,
            X = e.X, Y = e.Y, Facing = e.Facing, Action = e.Action,
        };
    }
}
