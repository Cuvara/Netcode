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

        public ResolvedEntity(string id, string type, float x, float y, int hp, int maxHp, float speed)
        {
            Id = id;
            Type = type;
            X = x;
            Y = y;
            Hp = hp;
            MaxHp = maxHp;
            Speed = speed;
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
    }
}
