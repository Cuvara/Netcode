using Unity.Burst;
using Unity.Entities;
using Unity.Transforms;

namespace DOTSSample
{
    /// <summary>Moves bullets along their direction at constant speed.</summary>
    [BurstCompile]
    public partial struct BulletMoveSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;

            foreach (var (transform, bullet) in
                     SystemAPI.Query<RefRW<LocalTransform>, RefRO<BulletData>>())
            {
                var lt = transform.ValueRO;
                lt.Position += bullet.ValueRO.Direction * bullet.ValueRO.Speed * dt;
                transform.ValueRW = lt;
            }
        }
    }
}
