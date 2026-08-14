using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace DOTSSample
{
    [BurstCompile]
    public partial struct MoveBounceSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;

            foreach (var (transform, move) in
                     SystemAPI.Query<RefRW<LocalTransform>, RefRW<MoveData>>())
            {
                float3 pos = transform.ValueRO.Position + move.ValueRO.Velocity * dt;

                // Bounce off bounds
                float3 vel = move.ValueRO.Velocity;

                if (pos.x < move.ValueRO.BoundsMin.x || pos.x > move.ValueRO.BoundsMax.x)
                {
                    vel.x = -vel.x;
                    pos.x = math.clamp(pos.x, move.ValueRO.BoundsMin.x, move.ValueRO.BoundsMax.x);
                }

                if (pos.y < move.ValueRO.BoundsMin.y || pos.y > move.ValueRO.BoundsMax.y)
                {
                    vel.y = -vel.y;
                    pos.y = math.clamp(pos.y, move.ValueRO.BoundsMin.y, move.ValueRO.BoundsMax.y);
                }

                if (pos.z < move.ValueRO.BoundsMin.z || pos.z > move.ValueRO.BoundsMax.z)
                {
                    vel.z = -vel.z;
                    pos.z = math.clamp(pos.z, move.ValueRO.BoundsMin.z, move.ValueRO.BoundsMax.z);
                }

                move.ValueRW.Velocity = vel;
                transform.ValueRW.Position = pos;
            }
        }
    }
}
