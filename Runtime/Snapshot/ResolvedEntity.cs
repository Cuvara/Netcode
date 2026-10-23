using Shared.GameLogic.Components;

namespace Cuvara.Netcode.Snapshot
{
    /// <summary>
    /// One entity from a snapshot after handle resolution: the id is always the
    /// real entity id, never a handle.
    /// </summary>
    public readonly struct ResolvedEntity
    {
        /// <summary>
        /// Constructs a resolved entity with no speed, leaving <see cref="Speed"/> zero —
        /// which consumers read as "the server did not send one".
        /// </summary>
        public ResolvedEntity(string id, string type, float x, float y, int hp, int maxHp)
            : this(id, type, x, y, hp, maxHp, 0f)
        {
        }

        /// <summary>
        /// Constructs a resolved entity with no facing or action, leaving both at their
        /// "not sent" values. Kept for source compatibility, like the overload above.
        /// </summary>
        public ResolvedEntity(string id, string type, float x, float y, int hp, int maxHp, float speed)
            : this(id, type, x, y, hp, maxHp, speed, 0u,
                   Shared.GameLogic.Components.EntityAction.Unspecified)
        {
        }

        /// <summary>
        /// Constructs a resolved entity with no retrigger counter. Kept for source
        /// compatibility, like the overloads above.
        /// </summary>
        public ResolvedEntity(
            string id, string type, float x, float y, int hp, int maxHp, float speed,
            uint facingBrad, Shared.GameLogic.Components.EntityAction action)
            : this(id, type, x, y, hp, maxHp, speed, facingBrad, action, 0u)
        {
        }

        public ResolvedEntity(
            string id, string type, float x, float y, int hp, int maxHp, float speed,
            uint facingBrad, Shared.GameLogic.Components.EntityAction action, uint actionSeq)
            : this(id, type, x, y, hp, maxHp, speed, facingBrad, action, actionSeq, 0u)
        {
        }

        /// <summary>
        /// Full constructor, including the field-level delta mask.
        /// </summary>
        /// <remarks>
        /// The mask is the ELEVENTH parameter deliberately. Adding it as a tenth would have
        /// captured every existing ten-argument call — both trailing parameters are
        /// <c>uint</c>, so overload resolution cannot tell them apart and the compiler would
        /// not complain. That is not hypothetical: Shared.GameLogic 0.5.0 added exactly such
        /// a ten-argument overload and `WorldState.Apply` silently bound `actionSeq` into
        /// `changedFields` (Cuvara/Netcode#159).
        /// </remarks>
        public ResolvedEntity(
            string id, string type, float x, float y, int hp, int maxHp, float speed,
            uint facingBrad, Shared.GameLogic.Components.EntityAction action, uint actionSeq,
            uint changedFields)
        {
            Id = id;
            Type = type;
            X = x;
            Y = y;
            Hp = hp;
            MaxHp = maxHp;
            Speed = speed;
            FacingBrad = facingBrad;
            Action = action;
            ActionSeq = actionSeq;
            ChangedFields = changedFields;
            Z = 0f;
            VelX = 0f;
            VelY = 0f;
            VelZ = 0f;
            OwnerId = null;
            SpawnSeq = 0u;
            Stats = null;
            StatsRemoved = null;
            Statuses = null;
            StatusesRemoved = null;
        }

        /// <summary>
        /// Protocol 3 constructor: the protocol 2 state in <paramref name="core"/> plus every
        /// version 3 field.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why the first parameter is a whole <see cref="ResolvedEntity"/>.</b> The same
        /// reason <c>Shared.GameLogic</c>'s <c>EntitySnapshotData</c> takes one: a 21-argument
        /// positional constructor is the trap this type has already fallen into once (see the
        /// eleven-argument overload). No other overload starts with a
        /// <see cref="ResolvedEntity"/>, so no call written against an older overload can bind
        /// here, and the protocol 2 fields keep being set through the constructors that name
        /// them. Every version 3 field of <paramref name="core"/> is ignored and replaced.
        /// </para>
        /// <para>
        /// The arrays are taken by reference, not copied: the resolver hands over arrays it
        /// allocated for this entity alone, and they are never written again.
        /// </para>
        /// </remarks>
        public ResolvedEntity(
            in ResolvedEntity core,
            float z,
            float velX,
            float velY,
            float velZ,
            string ownerId,
            uint spawnSeq,
            StatValueData[] stats,
            uint[] statsRemoved,
            StatusEffectData[] statuses,
            uint[] statusesRemoved)
        {
            Id = core.Id;
            Type = core.Type;
            X = core.X;
            Y = core.Y;
            Hp = core.Hp;
            MaxHp = core.MaxHp;
            Speed = core.Speed;
            FacingBrad = core.FacingBrad;
            Action = core.Action;
            ActionSeq = core.ActionSeq;
            ChangedFields = core.ChangedFields;
            Z = z;
            VelX = velX;
            VelY = velY;
            VelZ = velZ;
            OwnerId = string.IsNullOrEmpty(ownerId) ? null : ownerId;
            SpawnSeq = spawnSeq;
            Stats = stats;
            StatsRemoved = statsRemoved;
            Statuses = statuses;
            StatusesRemoved = statusesRemoved;
        }

        // --- Protocol version 3 (ADR-28..30). All "not sent" (0 / null) from a version 2
        // server and from every constructor that predates them. ---

        /// <summary>
        /// Height above the ground plane (ADR-28). <see cref="X"/>/<see cref="Y"/> keep their
        /// protocol 2 meaning; a renderer maps <c>(X, Y, Z)</c> to Unity <c>(x, z, y)</c>.
        /// </summary>
        public float Z { get; }

        /// <summary>Velocity along X, world units per second.</summary>
        public float VelX { get; }

        /// <summary>Velocity along Y, world units per second.</summary>
        public float VelY { get; }

        /// <summary>
        /// Velocity along Z, world units per second. Sent for projectiles always and characters
        /// while airborne; all three zero means "stationary or not sent".
        /// </summary>
        public float VelZ { get; }

        /// <summary>
        /// Entity id of the owner (a projectile's caster), resolved from the wire's interned
        /// handle -- or null for none, not visible, or not resolvable. An owner that cannot be
        /// resolved does not abort the snapshot: like a game-event participant it is a reference
        /// to ANOTHER entity, and the entity this describes is still correctly identified.
        /// </summary>
        public string OwnerId { get; }

        /// <summary>
        /// The <c>spawn_seq</c> of the local input that created this projectile -- sent only to
        /// the owner's connection, so a non-zero value means "this is the authoritative copy of
        /// a projectile you predicted". Zero otherwise.
        /// </summary>
        public uint SpawnSeq { get; }

        /// <summary>
        /// Content stat block, or null for none. Complete on a full entity; on a delta with
        /// <c>SnapshotFieldBits.Stats</c> only the stats that changed.
        /// </summary>
        public StatValueData[] Stats { get; }

        /// <summary>Stat ids removed by this delta, or null.</summary>
        public uint[] StatsRemoved { get; }

        /// <summary>
        /// Active status effects with their applier resolved to an entity id (null when none or
        /// unresolvable), or null for none. Same complete-set / delta rule as
        /// <see cref="Stats"/>.
        /// </summary>
        public StatusEffectData[] Statuses { get; }

        /// <summary>Status ids that ended in this delta, or null.</summary>
        public uint[] StatusesRemoved { get; }

        /// <summary>
        /// True when any protocol version 3 field is set. False for everything a version 2
        /// server sends.
        /// </summary>
        public bool HasVersion3Fields =>
            Z != 0f || VelX != 0f || VelY != 0f || VelZ != 0f || OwnerId != null || SpawnSeq != 0u
            || Stats != null || StatsRemoved != null || Statuses != null || StatusesRemoved != null;

        /// <summary>
        /// Field-level delta mask. Zero means every field is present — see
        /// <c>Shared.GameLogic.Systems.SnapshotFieldBits</c> for the bit meanings.
        /// </summary>
        public uint ChangedFields { get; }

        public string Id { get; }

        public string Type { get; }

        public float X { get; }

        public float Y { get; }

        public int Hp { get; }

        public int MaxHp { get; }

        /// <summary>
        /// Movement speed in world units per second, for prediction.
        /// </summary>
        /// <remarks>
        /// <b>Non-positive means "not sent", not "immobile"</b> — proto3 elides a zero
        /// float, so a server predating the field is indistinguishable from a stationary
        /// entity. Fall back to a configured default rather than concluding the entity
        /// cannot move.
        /// </remarks>
        public float Speed { get; }

        /// <summary>
        /// Facing as biased 16-bit binary radians, in the wire's own form. Decode with
        /// <see cref="Cuvara.Netcode.Protocol.FacingCodec"/>.
        /// </summary>
        /// <remarks>
        /// <b>Zero means "not sent", not "facing east"</b> — the +1 bias exists so those
        /// stay distinguishable. Kept in raw wire form rather than as an angle so this
        /// layer makes no decision the view layer should be making: whether to hold the
        /// last facing or derive one from movement is a presentation choice.
        /// </remarks>
        public uint FacingBrad { get; }

        /// <summary>
        /// What the entity is doing.
        /// <see cref="Shared.GameLogic.Components.EntityAction.Unspecified"/> means
        /// "not sent", never "idle".
        /// </summary>
        public Shared.GameLogic.Components.EntityAction Action { get; }

        /// <summary>
        /// Retrigger counter for <see cref="Action"/>. A view retriggers an animation when
        /// this CHANGES, never when it increases, and treats 0 as "not sent".
        /// </summary>
        /// <remarks>
        /// Kept raw, like <see cref="FacingBrad"/>, so this layer decides nothing a view
        /// should decide. The counter wraps and resets, so a greater-than test silently stops
        /// retriggering for four billion actions — see
        /// <see cref="Protocol.Messages.EntitySnapshot.ActionSeq"/>.
        /// </remarks>
        public uint ActionSeq { get; }
    }
}
