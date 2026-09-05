namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// Server → client (in snapshot stream): an ability was cast this tick.
    /// One-shot event, not persistent state — the client renders VFX/animation
    /// for one frame and discards.
    /// </summary>
    public sealed class AbilityEvent
    {
        /// <summary>Ability identifier.</summary>
        public string AbilityId { get; set; } = "";

        /// <summary>Entity id of the caster.</summary>
        public string CasterId { get; set; } = "";

        /// <summary>Entity id of the target (empty for AoE / self).</summary>
        public string TargetId { get; set; } = "";

        /// <summary>Server tick when the ability resolved.</summary>
        public ulong Tick { get; set; }

        /// <summary>Result of the cast attempt.</summary>
        public AbilityCastResult Result { get; set; }

        /// <summary>Position X where the ability landed (for AoE).</summary>
        public float PositionX { get; set; }

        /// <summary>Position Y where the ability landed (for AoE).</summary>
        public float PositionY { get; set; }

        /// <summary>Damage dealt (0 if miss/blocked/out of range).</summary>
        public int DamageDealt { get; set; }
    }

    /// <summary>Result of a cast attempt.</summary>
    public enum AbilityCastResult
    {
        /// <summary>Ability landed successfully.</summary>
        Hit = 0,
        /// <summary>Ability missed the target.</summary>
        Miss = 1,
        /// <summary>Target out of range when the server validated.</summary>
        OutOfRange = 2,
        /// <summary>Ability still on cooldown.</summary>
        OnCooldown = 3,
        /// <summary>Target is dead or invalid.</summary>
        InvalidTarget = 4,
        /// <summary>Caster is dead or stunned.</summary>
        CasterIncapacitated = 5,
        /// <summary>Not enough resource (mana, energy).</summary>
        InsufficientResource = 6,
    }
}
