using Unity.Entities;
using Unity.Mathematics;

namespace DOTSSample
{
    /// <summary>Moves the entity toward a fixed target point at a constant speed.</summary>
    public struct MoveToward : IComponentData
    {
        public float3 Target;
        public float Speed;
    }
}
