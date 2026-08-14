using Unity.Burst;
using Unity.Entities;

namespace DOTSSample
{
    /// <summary>Counts down <see cref="Lifetime"/> and destroys expired entities.</summary>
    [BurstCompile]
    public partial struct LifetimeSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;

            var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);

            foreach (var (lifetime, entity) in
                     SystemAPI.Query<RefRW<Lifetime>>()
                         .WithEntityAccess())
            {
                lifetime.ValueRW.Remaining -= dt;
                if (lifetime.ValueRO.Remaining <= 0f)
                {
                    ecb.DestroyEntity(entity);
                }
            }
        }
    }
}
