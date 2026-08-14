using Unity.Entities;

namespace DOTSSample
{
    /// <summary>
    /// Singleton — enemies within <see cref="Radius"/> of the world origin take
    /// <see cref="DamagePerSecond"/> continuous damage.
    /// </summary>
    public struct CenterZoneDamage : IComponentData
    {
        public float Radius;
        public float DamagePerSecond;
    }
}
