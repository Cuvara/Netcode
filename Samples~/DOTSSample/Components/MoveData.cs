using Unity.Entities;
using Unity.Mathematics;

namespace DOTSSample
{
    public struct MoveData : IComponentData
    {
        public float3 Velocity;
        public float3 BoundsMin;
        public float3 BoundsMax;
    }
}
