using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace DOTSSample
{
    /// <summary>
    /// Periodically spawns enemy entities from the edges of the arena, moving toward
    /// the center. Uses <see cref="EnemySpawnTimer"/> singleton for timing and
    /// <see cref="CombatPrefabs"/> singleton for the prefab reference.
    /// </summary>
    [BurstCompile]
    public partial struct EnemySpawnSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<EnemySpawnTimer>() ||
                !SystemAPI.HasSingleton<CombatPrefabs>())
                return;

            float dt = SystemAPI.Time.DeltaTime;
            var timer = SystemAPI.GetSingletonRW<EnemySpawnTimer>();

            timer.ValueRW.Timer -= dt;
            if (timer.ValueRW.Timer > 0f)
                return;

            timer.ValueRW.Timer = timer.ValueRO.Interval;

            var prefabs = SystemAPI.GetSingleton<CombatPrefabs>();
            if (prefabs.EnemyPrefab == Entity.Null)
                return;

            var ecb = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);

            var rng = new Random(timer.ValueRO.Seed);
            timer.ValueRW.Seed = rng.NextUInt();

            const float edge = 12f;
            int batch = timer.ValueRO.BatchSize;

            for (int i = 0; i < batch; i++)
            {
                float3 spawnPos;
                int side = rng.NextInt(0, 4);
                float along = rng.NextFloat(-edge, edge);

                switch (side)
                {
                    case 0: spawnPos = new float3(along, 0.5f, edge); break;    // north
                    case 1: spawnPos = new float3(along, 0.5f, -edge); break;   // south
                    case 2: spawnPos = new float3(edge, 0.5f, along); break;    // east
                    default: spawnPos = new float3(-edge, 0.5f, along); break;  // west
                }

                var enemy = ecb.Instantiate(prefabs.EnemyPrefab);
                ecb.SetComponent(enemy, LocalTransform.FromPositionRotationScale(
                    spawnPos, quaternion.identity, 0.8f));

                // Slight offset from dead center so enemies spread out
                float3 target = new float3(
                    rng.NextFloat(-2f, 2f),
                    0.5f,
                    rng.NextFloat(-2f, 2f));

                ecb.SetComponent(enemy, new MoveToward
                {
                    Target = target,
                    Speed = rng.NextFloat(1.5f, 3.5f)
                });

                ecb.SetComponent(enemy, new Health { Current = 3, Max = 3 });

                rng = new Random(rng.NextUInt());
            }
        }
    }
}
