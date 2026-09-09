using System;
using System.Collections.Generic;
using Google.Protobuf;
using Pb = RpgMmo.Wire.V1;

namespace Cuvara.Netcode.Samples.SendBudgetProbe
{
    /// <summary>
    /// A client-side model of the game server's per-connection downlink budget, so the
    /// thing can be watched instead of read about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is sample code, not production netcode.</b> The budget is a SERVER
    /// decision — it lives in <c>GameServer/Snapshot/SnapshotDeltaState.cs</c> in the
    /// backend repo, and a client neither implements it nor needs to know it happened;
    /// deferral is invisible in the protocol (see <c>docs/API.md</c>, "Downlink budget").
    /// This file mirrors the server's algorithm faithfully enough to be worth looking at,
    /// and nothing in <c>Runtime/</c> depends on it.
    /// </para>
    /// <para>
    /// <b>The bytes are real.</b> Sizes come from the generated
    /// <see cref="Pb.SnapshotMessage"/> — the same protobuf schema the server encodes
    /// with, shipped in this package for the decode path — so a number on screen here is
    /// a number of bytes that would be on the wire, not a proxy for one. That is the
    /// whole reason the probe measures instead of estimating: an estimated meter would
    /// drift from the server's cap silently, which is the class of defect the budget
    /// itself was built to avoid.
    /// </para>
    /// <para>
    /// <b>The invariant being demonstrated.</b> An entity that does not fit is DEFERRED,
    /// never dropped: <see cref="_lastSent"/> and <see cref="_handles"/> are written only
    /// after the entity's bytes are appended, so a deferred entity stays dirty, is
    /// re-offered on the next tick, and never produces a handle the receiver has no
    /// binding for. Watch "handle errors" in the panel stay at zero while "shed/tick"
    /// runs high — that pair is the point of the scene.
    /// </para>
    /// </remarks>
    public sealed class SendBudgetModel
    {
        /// <summary>Visible state of one entity as last sent to this client.</summary>
        private struct SentView
        {
            public float X;
            public float Y;
            public int Hp;

            public bool Matches(in CrowdEntity e) =>
                X.Equals(e.X) && Y.Equals(e.Y) && Hp == e.Hp;
        }

        /// <summary>One candidate's scheduling key. Mirrors the server's comparer.</summary>
        private struct Candidate
        {
            public int Index;
            public int Age;
            public float DistanceSq;
            public bool Self;
        }

        private readonly Dictionary<int, SentView> _lastSent = new Dictionary<int, SentView>();
        private readonly Dictionary<int, uint> _handles = new Dictionary<int, uint>();
        private readonly Dictionary<int, int> _shedAge = new Dictionary<int, int>();
        private readonly HashSet<int> _seen = new HashSet<int>();
        private readonly List<int> _candidates = new List<int>();
        private readonly List<int> _pendingRemovals = new List<int>();
        private readonly List<Candidate> _sort = new List<Candidate>();

        private uint _nextHandle = 1;
        private int _sinceKeyframe;
        private bool _forceFull = true;

        /// <summary>Result of one simulated snapshot.</summary>
        public struct TickResult
        {
            public int PayloadBytes;
            public int VisibleCount;
            public int CarriedCount;
            public int ShedCount;
            public int RemovalsDeferred;
            public bool Full;
            public int MaxShedAge;
        }

        /// <summary>Longest deferral, in snapshots, any entity has reached. High-water mark.</summary>
        public int MaxShedAge { get; private set; }

        /// <summary>Ask for the next snapshot to be a keyframe (the client's MsgResync).</summary>
        public void RequestFull() => _forceFull = true;

        /// <summary>Drop everything, as a fresh connection would.</summary>
        public void Reset()
        {
            _lastSent.Clear();
            _handles.Clear();
            _shedAge.Clear();
            _nextHandle = 1;
            _sinceKeyframe = 0;
            _forceFull = true;
            MaxShedAge = 0;
        }

        /// <summary>
        /// Build one snapshot for <paramref name="crowd"/> under
        /// <paramref name="budgetBytes"/> (0 = unbudgeted) and hand it to
        /// <paramref name="receiver"/>, which plays the client.
        /// </summary>
        public TickResult Tick(
            ulong tick, CrowdEntity[] crowd, int visibleCount, int budgetBytes,
            int keyframeInterval, float observerX, float observerY, int selfIndex,
            FakeClient receiver)
        {
            bool full = _forceFull || keyframeInterval <= 0 || _sinceKeyframe >= keyframeInterval;
            _forceFull = false;
            if (full)
            {
                _sinceKeyframe = 0;
                _lastSent.Clear();
                // A keyframe is the synchronisation point for the handle space: both sides
                // drop every binding and start again from 1.
                _handles.Clear();
                _nextHandle = 1;
            }
            else
            {
                _sinceKeyframe++;
            }

            var msg = new Pb.SnapshotMessage { Tick = tick, AckTick = tick, Full = full };

            _candidates.Clear();
            _pendingRemovals.Clear();
            _seen.Clear();

            for (int i = 0; i < visibleCount; i++)
            {
                _seen.Add(crowd[i].Key);
                if (!full && _lastSent.TryGetValue(crowd[i].Key, out SentView prev) && prev.Matches(in crowd[i]))
                {
                    _shedAge.Remove(crowd[i].Key);
                    continue;
                }
                _candidates.Add(i);
            }

            if (!full && _lastSent.Count != _seen.Count)
            {
                foreach (KeyValuePair<int, SentView> kv in _lastSent)
                {
                    if (!_seen.Contains(kv.Key)) _pendingRemovals.Add(kv.Key);
                }
            }

            int used = HeaderBytes(tick, tick, full);
            int shed = 0;
            int removalsDeferred = 0;

            // Priority order, and it is the server's: the observer's own entity first (the
            // reconciliation anchor, which the player would see rubber-band), then longest
            // deferral first (strict aging, so nothing starves), then nearest first (error
            // is most visible closest to the camera), then index as a deterministic
            // tie-break.
            _sort.Clear();
            for (int c = 0; c < _candidates.Count; c++)
            {
                int index = _candidates[c];
                float dx = crowd[index].X - observerX;
                float dy = crowd[index].Y - observerY;
                int age;
                _shedAge.TryGetValue(crowd[index].Key, out age);
                _sort.Add(new Candidate
                {
                    Index = index,
                    Age = age,
                    DistanceSq = (dx * dx) + (dy * dy),
                    Self = index == selfIndex,
                });
            }
            _sort.Sort(Compare);

            for (int i = 0; i < _sort.Count; i++)
            {
                int index = _sort[i].Index;
                Pb.EntitySnapshot ent = Build(in crowd[index], out bool introduced, out uint handle);
                int size = EntryBytes(ent);

                // The floor: the top-priority candidate is emitted whatever it costs. It is
                // what makes the cap soft in exactly one place — without it an entity larger
                // than the whole budget would be deferred for ever — and it is the premise
                // the starvation bound rests on, because it guarantees the oldest-waiting
                // entity is always admitted.
                bool forced = i == 0;
                if (!forced && budgetBytes > 0 && used + size > budgetBytes)
                {
                    // Everything from here down is deferred: strictly by priority, so a
                    // cheap far entity cannot overtake an expensive near one every tick.
                    for (int j = i; j < _sort.Count; j++)
                    {
                        int key = crowd[_sort[j].Index].Key;
                        int age;
                        _shedAge.TryGetValue(key, out age);
                        age++;
                        _shedAge[key] = age;
                        if (age > MaxShedAge) MaxShedAge = age;
                        shed++;
                    }
                    break;
                }

                // Commit: the bytes first, the bookkeeping immediately after, and nowhere
                // else. This ordering is the entire safety argument — see the class remarks.
                msg.Entities.Add(ent);
                if (introduced) _handles[crowd[index].Key] = handle;
                _lastSent[crowd[index].Key] = new SentView
                {
                    X = crowd[index].X,
                    Y = crowd[index].Y,
                    Hp = crowd[index].Hp,
                };
                _shedAge.Remove(crowd[index].Key);
                used += size;
            }

            // Despawns rank above every non-self update: a deferred despawn leaves a ghost,
            // which is WRONG state rather than stale state. A key leaves _lastSent only if
            // its id actually reached the wire.
            for (int r = 0; r < _pendingRemovals.Count; r++)
            {
                int key = _pendingRemovals[r];
                string id = IdFor(key);
                int cost = RemovedBytes(id);
                if (budgetBytes > 0 && used + cost > budgetBytes)
                {
                    removalsDeferred = _pendingRemovals.Count - r;
                    break;
                }
                msg.Removed.Add(id);
                _lastSent.Remove(key);
                _handles.Remove(key);
                _shedAge.Remove(key);
                used += cost;
            }

            receiver.Receive(msg);

            return new TickResult
            {
                PayloadBytes = msg.CalculateSize(),
                VisibleCount = visibleCount,
                CarriedCount = msg.Entities.Count,
                ShedCount = shed,
                RemovalsDeferred = removalsDeferred,
                Full = full,
                MaxShedAge = MaxShedAge,
            };
        }

        private static int Compare(Candidate a, Candidate b)
        {
            if (a.Self != b.Self) return a.Self ? -1 : 1;
            if (a.Age != b.Age) return b.Age.CompareTo(a.Age); // older first
            int d = a.DistanceSq.CompareTo(b.DistanceSq);      // nearer first
            return d != 0 ? d : a.Index.CompareTo(b.Index);
        }

        private Pb.EntitySnapshot Build(in CrowdEntity e, out bool introduced, out uint handle)
        {
            introduced = !_handles.TryGetValue(e.Key, out handle);
            if (introduced) handle = _nextHandle++;

            return new Pb.EntitySnapshot
            {
                // The id travels ONLY on the message that introduces the handle. Every
                // later mention costs a varint instead of ~17 bytes, which is most of why
                // a delta is small enough for the budget to be a tail cap rather than a
                // constant constraint.
                Id = introduced ? IdFor(e.Key) : string.Empty,
                Handle = handle,
                X = e.X,
                Y = e.Y,
                Hp = e.Hp,
                MaxHp = 100,
                Speed = 3f,
                Type = Pb.EntityType.Player,
            };
        }

        internal static string IdFor(int key) => "e" + key.ToString("D4");

        private static int EntryBytes(Pb.EntitySnapshot e)
        {
            int size = e.CalculateSize();
            return 1 + CodedOutputStream.ComputeLengthSize(size) + size;
        }

        private static int RemovedBytes(string id)
            => 1 + CodedOutputStream.ComputeStringSize(id);

        private static int HeaderBytes(ulong tick, ulong ackTick, bool full)
            => (tick != 0 ? 1 + CodedOutputStream.ComputeUInt64Size(tick) : 0)
             + (ackTick != 0 ? 1 + CodedOutputStream.ComputeUInt64Size(ackTick) : 0)
             + (full ? 2 : 0);
    }
}
