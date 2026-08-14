namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// One entity's visible state inside a snapshot, exactly as it arrived on the
    /// wire — <see cref="Id"/> may still be empty and only <see cref="Handle"/> set.
    /// Resolution happens in <c>Scripts.Net.Snapshot</c>, never here.
    /// </summary>
    public sealed class EntitySnapshot
    {
        /// <summary>
        /// Entity id. Present only on the message that introduces
        /// <see cref="Handle"/>; empty on every later mention of the same entity.
        /// Always present on the JSON encoding, which never interns.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Entity type name (<c>player</c>, <c>mob</c>, <c>npc</c>, <c>item</c>,
        /// <c>projectile</c>). On the Protobuf encoding this is the resolved name
        /// of the type enum, falling back to the raw <c>type_name</c> field for a
        /// kind the schema does not enumerate yet.
        /// </summary>
        public string Type { get; set; } = string.Empty;

        public float X { get; set; }

        public float Y { get; set; }

        public int Hp { get; set; }

        public int MaxHp { get; set; }

        /// <summary>
        /// Per-connection alias for <see cref="Id"/>, valid until the next
        /// keyframe. Zero means "not interned" — use <see cref="Id"/> directly.
        /// The JSON encoding never sets it.
        /// </summary>
        public uint Handle { get; set; }

        /// <summary>
        /// Movement speed in world units per second, as the server is integrating it
        /// for this entity right now — not the spawn default.
        /// </summary>
        /// <remarks>
        /// <b>Zero means "not sent", not "immobile".</b> proto3 elides a zero float, so a
        /// server predating this field (anything before the field-9 change) is
        /// indistinguishable from a stationary entity. Treat a non-positive value as
        /// absent and fall back to a configured default: trusting it outright means an
        /// old server pins a predicted speed to zero and the local player stops moving.
        /// </remarks>
        public float Speed { get; set; }
    }
}
