using Unity.Entities;
using Unity.Mathematics;

namespace DOTSSample
{
    /// <summary>Bullet flight data — direction and speed set at spawn time.</summary>
    public struct BulletData : IComponentData
    {
        public float3 Direction;
        public float Speed;
    }
}
