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

        public ResolvedEntity(
            string id, string type, float x, float y, int hp, int maxHp, float speed,
            uint facingBrad, Shared.GameLogic.Components.EntityAction action)
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
        }

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
    }
}
