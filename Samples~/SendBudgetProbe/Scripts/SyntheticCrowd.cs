using System;

namespace Cuvara.Netcode.Samples.SendBudgetProbe
{
    /// <summary>One entity in the synthetic crowd.</summary>
    public struct CrowdEntity
    {
        /// <summary>Stable key. The server's delta encoder keys on an int for the same reason.</summary>
        public int Key;

        public float X;
        public float Y;
        public int Hp;
    }

    /// <summary>
    /// The load dial: a crowd of entities orbiting a point, of which a settable fraction
    /// moves on any given tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deterministic, from a seeded <see cref="Random"/>, so two runs of the scene with
    /// the same dial positions produce the same stream. A probe whose numbers moved on
    /// their own would be unusable for the comparison it exists to support — the same
    /// reasoning that makes the server derive keyframe phase from the user id rather than
    /// randomising it.
    /// </para>
    /// <para>
    /// <b>Why churn is a separate dial from population.</b> The budget spends bytes on
    /// entities that CHANGED, not on entities that are visible, so a dense but still crowd
    /// costs almost nothing after its first keyframe. Two hundred entities of which five
    /// per cent move is a plaza; two hundred of which all move is a raid — and only the
    /// second one makes the cap bite. Collapsing the two into one "load" slider would hide
    /// exactly that.
    /// </para>
    /// </remarks>
    public sealed class SyntheticCrowd
    {
        private const int MaxEntities = 512;

        private readonly CrowdEntity[] _entities = new CrowdEntity[MaxEntities];
        private readonly Random _random = new Random(20260909);

        private int _count;

        /// <summary>The crowd's backing array. Only the first <see cref="Count"/> are live.</summary>
        public CrowdEntity[] Entities => _entities;

        /// <summary>Entities currently visible.</summary>
        public int Count => _count;

        /// <summary>Index of the observer's own entity, or -1 when the crowd is empty.</summary>
        public int SelfIndex => _count > 0 ? 0 : -1;

        public float ObserverX => _count > 0 ? _entities[0].X : 0f;

        public float ObserverY => _count > 0 ? _entities[0].Y : 0f;

        /// <summary>Grow or shrink to <paramref name="target"/> entities.</summary>
        public void Resize(int target)
        {
            target = Math.Max(1, Math.Min(MaxEntities, target));
            for (int i = _count; i < target; i++)
            {
                double angle = i * 0.61803;
                double radius = 2.0 + ((i % 48) * 0.5);
                _entities[i] = new CrowdEntity
                {
                    // Keys never reused as the crowd shrinks and grows, exactly as the
                    // server's world-stable keys are never reused: a reused key would let
                    // a client that missed a despawn attribute an update to the wrong
                    // entity, which is wrong state rather than absent state.
                    Key = _nextKey++,
                    X = (float)(radius * Math.Cos(angle)),
                    Y = (float)(radius * Math.Sin(angle)),
                    Hp = 100,
                };
            }
            _count = target;
        }

        private int _nextKey = 1;

        /// <summary>
        /// Advance one simulation tick. <paramref name="churn"/> is the fraction of the
        /// crowd that moves, 0..1.
        /// </summary>
        public void Step(float churn, float speed)
        {
            if (_count == 0) return;

            // The observer always moves. Its own entity is the one the budget must never
            // defer, so a probe in which it sometimes stands still would let the "self is
            // never shed" claim pass without being tested.
            Move(0, speed);

            int moving = (int)Math.Round(churn * (_count - 1));
            for (int n = 0; n < moving; n++)
            {
                Move(1 + _random.Next(_count - 1), speed);
            }
        }

        private void Move(int index, float speed)
        {
            _entities[index].X += (float)((_random.NextDouble() - 0.5) * speed);
            _entities[index].Y += (float)((_random.NextDouble() - 0.5) * speed);
            if (_random.Next(16) == 0)
            {
                _entities[index].Hp = 20 + _random.Next(81);
            }
        }
    }
}
