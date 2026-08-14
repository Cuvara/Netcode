using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace DOTSSample
{
    /// <summary>
    /// Moves entities with <see cref="MoveToward"/> toward their target point at
    /// constant speed. Enemies use this to march toward the center.
    /// </summary>
    [BurstCompile]
    public partial struct EnemyMoveSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;

            foreach (var (transform, move) in
                     SystemAPI.Query<RefRW<LocalTransform>, RefRO<MoveToward>>())
            {
                float3 pos = transform.ValueRO.Position;
                float3 dir = move.ValueRO.Target - pos;
                float dist = math.length(dir);

                if (dist < 0.1f)
                    continue;

                float3 step = math.normalize(dir) * move.ValueRO.Speed * dt;

                // Don't overshoot
                if (math.length(step) > dist)
                    step = dir;

                var lt = transform.ValueRO;
                lt.Position = pos + step;
                transform.ValueRW = lt;
            }
        }
    }
}
