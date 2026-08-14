using Unity.Burst;
using Unity.Entities;

namespace DOTSSample
{
    /// <summary>
    /// Destroys entities whose <see cref="Health.Current"/> has reached zero or below.
    /// Updates <see cref="CombatStats"/> kill counter.
    /// </summary>
    [BurstCompile]
    [UpdateAfter(typeof(BulletHitSystem))]
    public partial struct HealthDeathSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);

            int kills = 0;

            foreach (var (health, entity) in
                     SystemAPI.Query<RefRO<Health>>()
                         .WithAll<EnemyTag>()
                         .WithEntityAccess())
            {
                if (health.ValueRO.Current <= 0)
                {
                    ecb.DestroyEntity(entity);
                    kills++;
                }
            }

            if (kills > 0 && SystemAPI.HasSingleton<CombatStats>())
            {
                var stats = SystemAPI.GetSingletonRW<CombatStats>();
                stats.ValueRW.Kills += kills;
            }
        }
    }
}
