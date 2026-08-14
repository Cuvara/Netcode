using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace DOTSSample
{
    [BurstCompile]
    public partial struct SpinSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;

            foreach (var (transform, spin) in
                     SystemAPI.Query<RefRW<LocalTransform>, RefRO<SpinSpeed>>())
            {
                transform.ValueRW = transform.ValueRO.RotateY(spin.ValueRO.RadiansPerSecond * dt);
            }
        }
    }
}
