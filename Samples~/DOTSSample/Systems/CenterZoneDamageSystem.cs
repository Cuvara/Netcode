using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace DOTSSample
{
    /// <summary>
    /// Enemies within the center zone radius take continuous damage each frame.
    /// Uses <see cref="CenterZoneDamage"/> singleton for radius and DPS.
    /// </summary>
    [BurstCompile]
    [UpdateBefore(typeof(HealthDeathSystem))]
    public partial struct CenterZoneDamageSystem : ISystem
    {
        private float _damageAccumulator;

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<CenterZoneDamage>())
                return;

            var zone = SystemAPI.GetSingleton<CenterZoneDamage>();
            float dt = SystemAPI.Time.DeltaTime;
            float radiusSq = zone.Radius * zone.Radius;

            // Accumulate fractional damage — apply 1 HP when accumulator >= 1
            _damageAccumulator += zone.DamagePerSecond * dt;
            int dmg = (int)_damageAccumulator;
            if (dmg <= 0)
                return;
            _damageAccumulator -= dmg;

            foreach (var (transform, health, _) in
                     SystemAPI.Query<RefRO<LocalTransform>, RefRW<Health>, RefRO<EnemyTag>>())
            {
                float3 pos = transform.ValueRO.Position;
                float distSq = pos.x * pos.x + pos.z * pos.z; // ignore Y

                if (distSq <= radiusSq)
                {
                    health.ValueRW.Current -= dmg;
                }
            }
        }
    }
}
