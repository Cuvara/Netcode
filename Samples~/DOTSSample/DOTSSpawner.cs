using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace DOTSSample
{
    /// <summary>
    /// Spawns 5 ECS entities with 3D meshes (cube, sphere, cylinder, capsule, cube)
    /// that move and spin using pure DOTS systems. Attach to any GameObject in the scene.
    /// </summary>
    public sealed class DOTSSpawner : MonoBehaviour
    {
        [Header("Spawn")]
        [SerializeField] private int entityCount = 5;
        [SerializeField] private float spawnRadius = 5f;
        [SerializeField] private float moveSpeed = 3f;
        [SerializeField] private float boundsSize = 10f;

        [Header("Rendering")]
        [SerializeField] private Material material;

        private static readonly PrimitiveType[] Primitives =
        {
            PrimitiveType.Cube,
            PrimitiveType.Sphere,
            PrimitiveType.Cylinder,
            PrimitiveType.Capsule,
            PrimitiveType.Cube
        };

        private void Start()
        {
            if (material == null)
            {
                material = CreateDefaultMaterial();
            }

            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;

            float halfBounds = boundsSize * 0.5f;
            var boundsMin = new float3(-halfBounds, 0.5f, -halfBounds);
            var boundsMax = new float3(halfBounds, 0.5f, halfBounds);

            for (int i = 0; i < entityCount; i++)
            {
                SpawnEntity(em, i, boundsMin, boundsMax);
            }

            Debug.Log($"[DOTSSpawner] Spawned {entityCount} ECS entities with Burst systems");
        }

        private void SpawnEntity(EntityManager em, int index, float3 boundsMin, float3 boundsMax)
        {
            var primitiveType = Primitives[index % Primitives.Length];
            var mesh = GetPrimitiveMesh(primitiveType);

            // Create render mesh description
            var renderMeshDescription = new RenderMeshDescription(ShadowCastingMode.On);
            var renderMeshArray = new RenderMeshArray(new[] { material }, new[] { mesh });

            var entity = em.CreateEntity();
            em.SetName(entity, $"DOTS_{primitiveType}_{index}");

            // Add rendering
            RenderMeshUtility.AddComponents(
                entity, em, renderMeshDescription, renderMeshArray,
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

            // Position in a circle
            float angle = (2f * math.PI * index) / entityCount;
            float3 pos = new float3(
                math.cos(angle) * spawnRadius,
                0.5f,
                math.sin(angle) * spawnRadius);

            if (em.HasComponent<LocalTransform>(entity))
                em.SetComponentData(entity, LocalTransform.FromPosition(pos));
            else
                em.AddComponentData(entity, LocalTransform.FromPosition(pos));

            // Spin component — each spins at a different speed
            em.AddComponentData(entity, new SpinSpeed
            {
                RadiansPerSecond = math.radians(90f + index * 45f)
            });

            // Move component — pseudo-random velocity per entity
            var seed = (uint)(index + 1) * 1471u;
            var rng = new Unity.Mathematics.Random(seed);
            float3 vel = rng.NextFloat3Direction() * moveSpeed;
            vel.y = 0; // keep on ground plane

            em.AddComponentData(entity, new MoveData
            {
                Velocity = vel,
                BoundsMin = boundsMin,
                BoundsMax = boundsMax
            });
        }

        private static Mesh GetPrimitiveMesh(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            Destroy(go);
            return mesh;
        }

        private static Material CreateDefaultMaterial()
        {
            // Use URP Lit shader
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                shader = Shader.Find("Standard");

            var mat = new Material(shader)
            {
                color = new Color(0.3f, 0.7f, 1f)
            };
            return mat;
        }
    }
}
