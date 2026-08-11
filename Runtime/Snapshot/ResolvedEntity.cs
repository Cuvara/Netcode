namespace Cuvara.Netcode.Snapshot
{
    /// <summary>
    /// One entity from a snapshot after handle resolution: the id is always the
    /// real entity id, never a handle.
    /// </summary>
    public readonly struct ResolvedEntity
    {
        public ResolvedEntity(string id, string type, float x, float y, int hp, int maxHp)
        {
            Id = id;
            Type = type;
            X = x;
            Y = y;
            Hp = hp;
            MaxHp = maxHp;
        }

        public string Id { get; }

        public string Type { get; }

        public float X { get; }

        public float Y { get; }

        public int Hp { get; }

        public int MaxHp { get; }
    }
}
