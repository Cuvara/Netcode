namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// One active status effect in <see cref="EntitySnapshot.Statuses"/> (wire
    /// <c>StatusEffect</c>, ADR-30), exactly as it arrived: <see cref="Source"/> is still an
    /// interned HANDLE. <c>Snapshot/SnapshotResolver</c> turns it into an entity id.
    /// </summary>
    public readonly struct StatusEffect
    {
        public StatusEffect(uint effectId, uint stacks, ulong expiresTick, uint source)
        {
            EffectId = effectId;
            Stacks = stacks;
            ExpiresTick = expiresTick;
            Source = source;
        }

        /// <summary>
        /// Content id of the status definition (ADR-19). The definition says whether it is
        /// periodic, a stat modifier or crowd control; the client looks that up rather than
        /// being told.
        /// </summary>
        public uint EffectId { get; }

        /// <summary>Current stack count.</summary>
        public uint Stacks { get; }

        /// <summary>Server tick the effect ends on; 0 means "until removed".</summary>
        public ulong ExpiresTick { get; }

        /// <summary>Interned handle of the entity that applied it; 0 for none or not visible.</summary>
        public uint Source { get; }

        public override string ToString() => $"{EffectId}x{Stacks}@{ExpiresTick}";
    }
}
