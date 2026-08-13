using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace DOTSSample
{
    /// <summary>
    /// Bullet-enemy collision with client-side HP prediction. On hit: reduces enemy
    /// <see cref="Health.Current"/> immediately for instant visual feedback, then
    /// destroys the bullet. Server snapshot reconciles the predicted HP value.
    /// </summary>
    [BurstCompile]
    [UpdateAfter(typeof(BulletMoveSystem))]
    public partial struct BulletHitSystem : ISystem
    {
        private struct EnemyInfo
        {
            public Entity Entity;
            public float3 Position;
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // Pass 1: collect enemies
            var enemies = new NativeList<EnemyInfo>(64, Allocator.Temp);
            foreach (var (transform, _, entity) in
                     SystemAPI.Query<RefRO<LocalTransform>, RefRO<EnemyTag>>()
                         .WithEntityAccess())
            {
                enemies.Add(new EnemyInfo
                {
                    Entity = entity,
                    Position = transform.ValueRO.Position
                });
            }

            if (enemies.Length == 0)
            {
                enemies.Dispose();
                return;
            }

            var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);

            const float hitRadius = 0.6f;
            const int bulletDamage = 10;

            var hitEnemies = new NativeHashSet<Entity>(32, Allocator.Temp);

            // Pass 2: each bullet checks against all enemies
            foreach (var (transform, _, bulletEntity) in
                     SystemAPI.Query<RefRO<LocalTransform>, RefRO<BulletData>>()
                         .WithEntityAccess())
            {
                float3 bulletPos = transform.ValueRO.Position;

                for (int i = 0; i < enemies.Length; i++)
                {
                    if (hitEnemies.Contains(enemies[i].Entity))
                        continue;

                    float dist = math.distance(bulletPos, enemies[i].Position);
                    if (dist <= hitRadius)
                    {
                        // Client-side HP prediction — instant visual feedback
                        if (SystemAPI.HasComponent<Health>(enemies[i].Entity))
                        {
                            var hp = SystemAPI.GetComponentRW<Health>(enemies[i].Entity);
                            hp.ValueRW.Current -= bulletDamage;
                        }

                        hitEnemies.Add(enemies[i].Entity);
                        ecb.DestroyEntity(bulletEntity);
                        break;
                    }
                }
            }

            hitEnemies.Dispose();
            enemies.Dispose();
        }
    }
}
