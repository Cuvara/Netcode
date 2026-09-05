namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// An active status effect on an entity, carried in the snapshot stream.
    /// Part of entity state delta — only changed effects sent per tick.
    /// </summary>
    public sealed class StatusEffect
    {
        /// <summary>Effect identifier (from ContentDatabase / effect definitions).</summary>
        public string EffectId { get; set; } = "";

        /// <summary>Remaining duration in server ticks. 0 = permanent until explicitly removed.</summary>
        public uint RemainingTicks { get; set; }

        /// <summary>Number of stacks. 1 for non-stackable effects.</summary>
        public int Stacks { get; set; } = 1;

        /// <summary>Entity id of whoever applied this effect (for kill attribution).</summary>
        public string SourceId { get; set; } = "";
    }
}
