using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace DOTSSample
{
    /// <summary>
    /// Sets up the combat demo: creates prefab entities for bullets and initializes
    /// singletons. Players and enemies come from server snapshots via
    /// <see cref="DOTSEntityView"/>; this bootstrap only provides the bullet prefab
    /// and global combat state.
    /// </summary>
    public sealed class CombatBootstrap : MonoBehaviour
    {
        private void Start()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null)
            {
                Debug.LogWarning("[CombatBootstrap] DOTS World not ready — combat disabled");
                return;
            }

            var em = world.EntityManager;

            // --- Create bullet prefab ---
            var bulletPrefab = CreateBulletPrefab(em);

            // --- Singletons ---
            var prefabsEntity = em.CreateEntity();
            em.SetName(prefabsEntity, "CombatPrefabs");
            em.AddComponentData(prefabsEntity, new CombatPrefabs
            {
                EnemyPrefab = Entity.Null, // enemies come from server snapshots
                BulletPrefab = bulletPrefab
            });

            var statsEntity = em.CreateEntity();
            em.SetName(statsEntity, "CombatStats");
            em.AddComponentData(statsEntity, new CombatStats());

            var zoneEntity = em.CreateEntity();
            em.SetName(zoneEntity, "CenterZoneDamage");
            em.AddComponentData(zoneEntity, new CenterZoneDamage
            {
                Radius = 4f,
                DamagePerSecond = 2f
            });

            Debug.Log("[CombatBootstrap] Combat ready — awaiting server entities");
        }

        private Entity CreateBulletPrefab(EntityManager em)
        {
            var mesh = GetPrimitiveMesh(PrimitiveType.Cube);
            var material = CreateMaterial(new Color(1f, 0.95f, 0.3f));

            var entity = em.CreateEntity();
            em.SetName(entity, "BulletPrefab");
            em.AddComponent<Prefab>(entity);

            var renderDesc = new RenderMeshDescription(ShadowCastingMode.Off);
            var renderArray = new RenderMeshArray(new[] { material }, new[] { mesh });
            RenderMeshUtility.AddComponents(entity, em, renderDesc, renderArray,
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

            em.AddComponentData(entity, LocalTransform.FromPositionRotationScale(
                float3.zero, quaternion.identity, 0.15f));
            em.AddComponentData(entity, new BulletData { Direction = float3.zero, Speed = 18f });
            em.AddComponentData(entity, new Lifetime { Remaining = 1.2f });

            return entity;
        }

        private static Mesh GetPrimitiveMesh(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            Destroy(go);
            return mesh;
        }

        private static Material CreateMaterial(Color color)
        {
            var baseMat = Resources.Load<Material>("DOTSDefaultMaterial");
            Material mat;
            if (baseMat != null)
            {
                mat = new Material(baseMat);
            }
            else
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                mat = new Material(shader != null ? shader : Shader.Find("Standard"));
            }

            mat.color = color;
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);

            return mat;
        }
    }
}
