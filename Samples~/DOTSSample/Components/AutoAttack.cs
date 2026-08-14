using Unity.Entities;

namespace DOTSSample
{
    /// <summary>Auto-attack state for player combat entities.</summary>
    public struct AutoAttack : IComponentData
    {
        /// <summary>Seconds between shots.</summary>
        public float Cooldown;

        /// <summary>Countdown timer — fires when &lt;= 0.</summary>
        public float Timer;

        /// <summary>Max distance to acquire a target.</summary>
        public float Range;

        /// <summary>Damage per bullet hit.</summary>
        public int Damage;
    }
}
