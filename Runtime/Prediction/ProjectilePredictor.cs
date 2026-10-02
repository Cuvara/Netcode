using System;
using System.Collections.Generic;
using Cuvara.Netcode.Snapshot;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;
using Shared.GameLogic.World;

namespace Cuvara.Netcode.Prediction
{
    /// <summary>
    /// One projectile the local player fired and is predicting, as
    /// <see cref="ProjectilePredictor"/> reports it.
    /// </summary>
    public readonly struct PredictedProjectile
    {
        public PredictedProjectile(
            uint spawnSeq, in ProjectileState state, in Vec3 renderPosition, bool flying, float ageSeconds)
        {
            SpawnSeq = spawnSeq;
            State = state;
            RenderPosition = renderPosition;
            Flying = flying;
            AgeSeconds = ageSeconds;
        }

        /// <summary>The <c>InputMessage.spawn_seq</c> the firing input carried.</summary>
        public uint SpawnSeq { get; }

        /// <summary>Simulation state after the newest whole tick.</summary>
        public ProjectileState State { get; }

        /// <summary>
        /// Where to draw it this frame: between the last two simulated ticks, by the fraction of
        /// a tick elapsed since -- server (x, y, z) space; map to Unity as (x, z, y).
        /// </summary>
        public Vec3 RenderPosition { get; }

        /// <summary>
        /// False once the prediction says it stopped -- hit the world, ran out of range, left the
        /// map. It is still held, motionless, until the server's entity confirms or the handover
        /// times out: the server may disagree about the hit.
        /// </summary>
        public bool Flying { get; }

        /// <summary>Seconds since it was fired.</summary>
        public float AgeSeconds { get; }
    }

    /// <summary>
    /// Predicts the local player's own skillshot projectiles from the moment the input fires
    /// until the server's authoritative projectile entity -- the one carrying the same
    /// <c>spawn_seq</c> -- appears in a snapshot, then hands over to it (ADR-29 decision 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why predict at all.</b> Without it a skillshot appears a full round trip plus the
    /// interpolation delay after the button press -- 150-300 ms in which the player sees
    /// nothing leave their hand. With it the projectile leaves at once and the authoritative one
    /// takes its place when it arrives.
    /// </para>
    /// <para>
    /// <b>Same arithmetic as the server.</b> Spawn and flight are
    /// <see cref="ProjectileLogic.Spawn"/> and <see cref="ProjectileLogic.Step"/> over the same
    /// <see cref="MapGeometry"/>, one step per server tick, so the predicted path is the
    /// server's wherever their inputs agree. They do not agree exactly on the origin: the server
    /// launches from the caster's AUTHORITATIVE position (ADR-29 decision 5) and the client can
    /// only use its predicted one, so a small offset at handover is normal and is the game's to
    /// blend.
    /// </para>
    /// <para>
    /// <b>The loop.</b> <see cref="Fire"/> allocates the <c>spawn_seq</c> to put on the input
    /// (<c>InputMessage.SpawnSeq</c>) and starts the prediction; <see cref="Advance"/> runs it
    /// once per frame; <see cref="ApplySnapshot"/> looks for entities carrying a
    /// <c>spawn_seq</c> this predictor issued -- the server sends that field only to the owner's
    /// connection -- and raises <see cref="HandedOver"/> with the entity id. A prediction no
    /// entity ever claims (the server refused the cast, or the projectile lived less than one
    /// snapshot interval) is dropped after <see cref="HandoverTimeoutSeconds"/> and reported
    /// through <see cref="Unconfirmed"/>, never kept forever.
    /// </para>
    /// <para>
    /// Hits are NOT predicted -- damage is the server's, and arrives as
    /// <c>GAME_EVENT_TYPE_PROJECTILE_HIT</c> / <c>DAMAGE</c>. Only flight is.
    /// </para>
    /// <para>Not thread-safe. Drive it from the thread that sends input and consumes snapshots.</para>
    /// </remarks>
    public sealed class ProjectilePredictor
    {
        private struct Entry
        {
            public uint SpawnSeq;
            public ProjectileState State;
            public Vec3 Previous;
            public bool Flying;
            public float Age;
        }

        /// <summary>Default seconds a prediction waits for its authoritative entity.</summary>
        public const float DefaultHandoverTimeoutSeconds = 1.0f;

        /// <summary>Ticks one <see cref="Advance"/> may simulate before the rest is discarded.</summary>
        public const int MaxCatchUpTicks = 15;

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly float _dt;
        private MapGeometry _geometry;
        private float _accumulator;
        private uint _lastSpawnSeq;

        /// <param name="tickRate">
        /// The server's simulation rate in Hz (<c>GameSessionClient.TickRate</c>, or the
        /// prediction settings' rate) -- projectiles are stepped once per server tick.
        /// </param>
        /// <param name="geometry">
        /// The map's collision world; null for a flat plane over <see cref="MapBounds.Default"/>.
        /// Must be the geometry the server loaded, or walls stop the two in different places.
        /// </param>
        public ProjectilePredictor(int tickRate, MapGeometry geometry = null)
        {
            if (tickRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tickRate), "tick rate must be positive");
            }

            _dt = MovementSystem.DeltaTimeForTickRate(tickRate);
            _geometry = geometry ?? MapGeometry.Flat(MapBounds.Default);
        }

        /// <summary>
        /// Raised when a snapshot carries the authoritative entity for a prediction: the
        /// predicted projectile with that spawn seq, and the entity id that replaces it. The
        /// prediction is gone from <see cref="Projectiles"/> when this fires.
        /// </summary>
        public event Action<PredictedProjectile, string> HandedOver;

        /// <summary>
        /// Raised when a prediction is dropped because no entity claimed it within
        /// <see cref="HandoverTimeoutSeconds"/> -- the cast was refused, or the projectile ended
        /// before any snapshot sampled it.
        /// </summary>
        public event Action<PredictedProjectile> Unconfirmed;

        /// <summary>Seconds a prediction waits for its authoritative entity before it is dropped.</summary>
        public float HandoverTimeoutSeconds { get; set; } = DefaultHandoverTimeoutSeconds;

        /// <summary>Fixed timestep: one server tick, in seconds.</summary>
        public float TickSeconds => _dt;

        /// <summary>Predictions currently held.</summary>
        public int Count => _entries.Count;

        /// <summary>Predictions handed over to an authoritative entity so far.</summary>
        public int HandOvers { get; private set; }

        /// <summary>Predictions dropped unclaimed so far.</summary>
        public int Unconfirmations { get; private set; }

        /// <summary>The collision world flights are stepped against.</summary>
        public MapGeometry Geometry => _geometry;

        /// <summary>Replaces the collision world; null restores a flat plane over <see cref="MapBounds.Default"/>.</summary>
        public void SetMapGeometry(MapGeometry geometry)
        {
            _geometry = geometry ?? MapGeometry.Flat(MapBounds.Default);
        }

        /// <summary>
        /// Allocates the next spawn seq: from 1, unique among those issued by this predictor, 0
        /// skipped on wrap because the wire reads 0 as "fires nothing".
        /// </summary>
        public uint NextSpawnSeq()
        {
            _lastSpawnSeq++;
            if (_lastSpawnSeq == 0)
            {
                _lastSpawnSeq = 1;
            }

            return _lastSpawnSeq;
        }

        /// <summary>
        /// Starts predicting a projectile and returns the spawn seq to send on the firing input,
        /// or 0 -- send nothing, predict nothing -- when <see cref="ProjectileLogic.Spawn"/>
        /// refuses (aim point on the origin, or an unusable number).
        /// </summary>
        /// <param name="origin">Launch point, from the local player's predicted position.</param>
        /// <param name="aimPoint">The point aimed at; the same values go in <c>aim_x/y/z</c>.</param>
        /// <param name="speed">Projectile speed, from the ability's content definition.</param>
        /// <param name="radius">Sphere radius, from the content definition.</param>
        /// <param name="range">Maximum travel, from the content definition.</param>
        public uint Fire(in Vec3 origin, in Vec3 aimPoint, float speed, float radius, float range)
        {
            if (!ProjectileLogic.Spawn(origin, aimPoint, speed, radius, range, out ProjectileState state))
            {
                return 0u;
            }

            uint seq = NextSpawnSeq();
            _entries.Add(new Entry
            {
                SpawnSeq = seq,
                State = state,
                Previous = state.Position,
                Flying = true,
                Age = 0f,
            });
            return seq;
        }

        /// <summary>
        /// Advances every prediction by <paramref name="deltaTime"/> seconds of wall time, in
        /// whole server ticks, and drops the ones that have waited too long for their entity.
        /// </summary>
        public void Advance(float deltaTime)
        {
            if (!(deltaTime > 0f) || float.IsInfinity(deltaTime))
            {
                return;
            }

            _accumulator += deltaTime;
            float max = _dt * MaxCatchUpTicks;
            if (_accumulator > max)
            {
                _accumulator = max;
            }

            int ticks = 0;
            while (_accumulator >= _dt)
            {
                _accumulator -= _dt;
                ticks++;
            }

            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry e = _entries[i];
                e.Age += deltaTime;

                for (int t = 0; t < ticks && e.Flying; t++)
                {
                    e.Previous = e.State.Position;
                    e.Flying = ProjectileLogic.Step(e.State, _dt, _geometry, out ProjectileState next, out _);
                    e.State = next;
                }

                if (!e.Flying)
                {
                    // Nothing more to interpolate: hold it where it stopped.
                    e.Previous = e.State.Position;
                }

                if (e.Age > HandoverTimeoutSeconds)
                {
                    _entries.RemoveAt(i);
                    Unconfirmations++;
                    Unconfirmed?.Invoke(ToPublic(e));
                    continue;
                }

                _entries[i] = e;
            }
        }

        /// <summary>
        /// Hands over every prediction whose authoritative entity <paramref name="snapshot"/>
        /// carries (an entity with a non-zero <c>SpawnSeq</c> this predictor issued).
        /// </summary>
        /// <returns>How many predictions were handed over.</returns>
        public int ApplySnapshot(in ResolvedSnapshot snapshot)
        {
            var entities = snapshot.Entities;
            if (entities == null || _entries.Count == 0)
            {
                return 0;
            }

            int handed = 0;
            for (int i = 0; i < entities.Count; i++)
            {
                uint seq = entities[i].SpawnSeq;
                if (seq != 0u && TryHandOver(seq, entities[i].Id))
                {
                    handed++;
                }
            }

            return handed;
        }

        /// <summary>
        /// Hands over the prediction for <paramref name="spawnSeq"/> to
        /// <paramref name="entityId"/>, for a caller that finds the authoritative entity some
        /// other way (from <c>WorldState</c>, say). Returns false when no such prediction is held.
        /// </summary>
        public bool TryHandOver(uint spawnSeq, string entityId)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].SpawnSeq != spawnSeq)
                {
                    continue;
                }

                Entry e = _entries[i];
                _entries.RemoveAt(i);
                HandOvers++;
                HandedOver?.Invoke(ToPublic(e), entityId);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Copies every held prediction into <paramref name="into"/> (cleared first), with its
        /// render position for this frame. Allocation-free once the list has grown.
        /// </summary>
        public void GetProjectiles(List<PredictedProjectile> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }

            into.Clear();
            for (int i = 0; i < _entries.Count; i++)
            {
                into.Add(ToPublic(_entries[i]));
            }
        }

        /// <summary>Looks up one held prediction by spawn seq.</summary>
        public bool TryGet(uint spawnSeq, out PredictedProjectile projectile)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].SpawnSeq == spawnSeq)
                {
                    projectile = ToPublic(_entries[i]);
                    return true;
                }
            }

            projectile = default;
            return false;
        }

        /// <summary>Forgets every prediction and restarts the spawn seq -- a new session.</summary>
        public void Reset()
        {
            _entries.Clear();
            _accumulator = 0f;
            _lastSpawnSeq = 0;
        }

        private PredictedProjectile ToPublic(in Entry e)
        {
            float alpha = _dt > 0f ? _accumulator / _dt : 1f;
            if (alpha > 1f) alpha = 1f;
            Vec3 render = e.Flying ? Vec3.Lerp(e.Previous, e.State.Position, alpha) : e.State.Position;
            return new PredictedProjectile(e.SpawnSeq, e.State, render, e.Flying, e.Age);
        }
    }
}
