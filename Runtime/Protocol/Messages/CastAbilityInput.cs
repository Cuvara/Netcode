namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// Client → server: the player wants to cast an ability.
    /// Tick-stamped like <see cref="InputMessage"/> so the server can validate
    /// cooldowns and range against the correct simulation state.
    /// </summary>
    public sealed class CastAbilityInput
    {
        /// <summary>Client tick when the cast was initiated.</summary>
        public ulong Tick { get; set; }

        /// <summary>Ability identifier (from ContentDatabase / ability definitions).</summary>
        public string AbilityId { get; set; } = "";

        /// <summary>
        /// Target entity id for targeted abilities. Empty for AoE / self-cast.
        /// </summary>
        public string TargetId { get; set; } = "";

        /// <summary>Target position X for ground-targeted abilities. 0 for targeted/self.</summary>
        public float TargetX { get; set; }

        /// <summary>Target position Y for ground-targeted abilities. 0 for targeted/self.</summary>
        public float TargetY { get; set; }
    }
}
