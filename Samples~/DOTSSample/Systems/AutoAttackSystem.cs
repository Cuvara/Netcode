using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace DOTSSample
{
    /// <summary>
    /// Players with <see cref="AutoAttack"/> target the nearest enemy and fire a
    /// bullet toward it when their cooldown expires. Also creates an
    /// <see cref="AttackRequest"/> entity so <see cref="DOTSNetworkBridge"/> can
    /// forward the attack to the server.
    /// </summary>
    public partial struct AutoAttackSystem : ISystem
    {
        private struct EnemyTarget
        {
            public float3 Position;
            public FixedString64Bytes NetworkId;
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<CombatPrefabs>())
                return;

            var prefabs = SystemAPI.GetSingleton<CombatPrefabs>();
            if (prefabs.BulletPrefab == Entity.Null)
                return;

            float dt = SystemAPI.Time.DeltaTime;

            // Pass 1: collect enemy positions + network IDs
            var enemies = new NativeList<EnemyTarget>(64, Allocator.Temp);
            foreach (var (transform, tag, _) in
                     SystemAPI.Query<RefRO<LocalTransform>, RefRO<NetworkEntityTag>, RefRO<EnemyTag>>())
            {
                enemies.Add(new EnemyTarget
                {
                    Position = transform.ValueRO.Position,
                    NetworkId = tag.ValueRO.PlayerId
                });
            }

            if (enemies.Length == 0)
            {
                enemies.Dispose();
                return;
            }

            var bulletEcb = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);

            // Use EndSimulation ECB for AttackRequests — ensures entity persists
            // through full simulation frame and is visible to MonoBehaviour.Update
            var attackEcb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);

            // Pass 2: each player targets nearest enemy and fires
            foreach (var (transform, attack, _) in
                     SystemAPI.Query<RefRO<LocalTransform>, RefRW<AutoAttack>, RefRO<PlayerCombatTag>>())
            {
                attack.ValueRW.Timer -= dt;

                if (attack.ValueRO.Timer > 0f)
                    continue;

                float3 playerPos = transform.ValueRO.Position;
                float bestDist = float.MaxValue;
                float3 bestTarget = float3.zero;
                FixedString64Bytes bestId = default;
                bool found = false;

                for (int i = 0; i < enemies.Length; i++)
                {
                    float dist = math.distance(playerPos, enemies[i].Position);
                    if (dist < bestDist && dist <= attack.ValueRO.Range)
                    {
                        bestDist = dist;
                        bestTarget = enemies[i].Position;
                        bestId = enemies[i].NetworkId;
                        found = true;
                    }
                }

                if (!found)
                    continue;

                attack.ValueRW.Timer = attack.ValueRO.Cooldown;

                // Spawn visual bullet toward target
                float3 dir = math.normalize(bestTarget - playerPos);
                float3 bulletSpawn = playerPos + dir * 0.6f;

                var bullet = bulletEcb.Instantiate(prefabs.BulletPrefab);
                bulletEcb.SetComponent(bullet, LocalTransform.FromPositionRotationScale(
                    bulletSpawn, quaternion.identity, 0.15f));
                bulletEcb.SetComponent(bullet, new BulletData
                {
                    Direction = dir,
                    Speed = 18f
                });
                bulletEcb.SetComponent(bullet, new Lifetime { Remaining = 1.2f });

                // Create attack event for DOTSNetworkBridge → server
                var attackEvent = attackEcb.CreateEntity();
                attackEcb.AddComponent(attackEvent, new AttackRequest { TargetId = bestId });

                // No per-fire log. Auto-attack fires several times a second by design, so
                // this line alone was ~700 log lines per minute on a running client -- the
                // largest remaining source after the poll and counter spam was gated. The
                // bridge's verboseLogging diagnostics already cover the attack path, and a
                // DOTS system has no clean reach into that MonoBehaviour toggle; a log that
                // cannot be turned off does not belong at this rate.
            }

            enemies.Dispose();
        }
    }
}
