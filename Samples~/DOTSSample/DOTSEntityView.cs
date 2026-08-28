using System.Collections.Generic;
using Cuvara.Netcode.View;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace DOTSSample
{
    /// <summary>
    /// <see cref="IEntityView"/> backed by ECS entities. Spawns/despawns/updates
    /// replicated entities as DOTS entities with 3D meshes rendered via Entities.Graphics.
    /// </summary>
    /// <remarks>
    /// Server coordinates are (x, y) on a 2D plane. These map to Unity (X, 0.5, Z)
    /// so a top-down camera sees the world as the server lays it out.
    /// Each player gets a unique colour from a fixed palette so multiple clients are
    /// visually distinguishable. Entities the server types as "mob" are rendered
    /// as red spheres with <see cref="EnemyTag"/> and <see cref="Health"/>; all other
    /// entities are players and receive <see cref="PlayerCombatTag"/> +
    /// <see cref="AutoAttack"/> so the combat systems target them automatically.
    /// <para>
    /// All entities share ONE <see cref="RenderMeshArray"/> built in the constructor —
    /// nine materials (the palette plus the enemy colour) by two meshes, indexed per
    /// entity with <see cref="MaterialMeshInfo"/>. The previous shape built a fresh
    /// <c>Material</c> and a fresh array per spawn: the materials were never destroyed
    /// (AOI churn made that a steady leak for the whole session) and every entity was
    /// its own render batch — one draw call per capsule (#60).
    /// </para>
    /// </remarks>
    public sealed class DOTSEntityView : IEntityView
    {
        /// <summary>Per-entity display info exposed for the overlay.</summary>
        public readonly struct EntityLabel
        {
            public readonly string Id;
            public readonly bool IsLocal;
            public readonly float3 WorldPos;
            public readonly Color Color;
            public readonly int Hp;
            public readonly int MaxHp;

            public EntityLabel(string id, bool isLocal, float3 worldPos, Color color, int hp, int maxHp)
            {
                Id = id;
                IsLocal = isLocal;
                WorldPos = worldPos;
                Color = color;
                Hp = hp;
                MaxHp = maxHp;
            }
        }

        private static readonly Color[] Palette =
        {
            new Color(0.2f, 0.8f, 1f),    // 0: cyan — local player
            new Color(1f,   0.4f, 0.4f),   // 1: red
            new Color(0.4f, 1f,   0.4f),   // 2: green
            new Color(1f,   0.8f, 0.2f),   // 3: yellow
            new Color(0.8f, 0.4f, 1f),     // 4: purple
            new Color(1f,   0.6f, 0.2f),   // 5: orange
            new Color(0.4f, 0.8f, 0.8f),   // 6: teal
            new Color(1f,   0.4f, 0.8f),   // 7: pink
        };

        private static readonly Color EnemyColor = new Color(0.9f, 0.15f, 0.1f);

        /// <summary>Material slot of the enemy colour in the shared array.</summary>
        private const int EnemyMaterialIndex = 8;

        private const int CapsuleMeshIndex = 0;
        private const int SphereMeshIndex = 1;

        /// <summary>
        /// The server's entity kind for a hostile NPC, as it arrives on
        /// <see cref="IEntityView.Spawn"/>. This used to be an <c>"enemy-"</c> id-prefix
        /// test, which was a guess at a fact the snapshot was already carrying.
        /// </summary>
        private const string MobType = "mob";

        /// <summary>
        /// Everything this view knows about one live entity, resolved once at spawn so
        /// the per-frame path does dictionary work and EntityManager round trips only
        /// when something actually changed (#60).
        /// </summary>
        private sealed class EntityRec
        {
            public Entity Entity;
            public bool IsEnemy;
            public int ColorIndex;
            public int Hp;
            public int MaxHp;
            public float LastX;
            public float LastY;
            public bool HasPos;
        }

        private readonly Dictionary<string, EntityRec> _entities = new Dictionary<string, EntityRec>();
        private readonly Dictionary<string, int> _playerColorIndex = new Dictionary<string, int>();
        private readonly EntityManager _em;
        private readonly RenderMeshArray _renderMeshArray;
        private int _nextColorIndex = 1; // 0 reserved for local

        public bool IsValid { get; }

        public DOTSEntityView()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null)
            {
                Debug.LogWarning("[DOTSEntityView] DOTS World not ready — entity rendering disabled");
                IsValid = false;
                return;
            }

            _em = world.EntityManager;

            var capsule = GetPrimitiveMesh(PrimitiveType.Capsule);
            var sphere = GetPrimitiveMesh(PrimitiveType.Sphere);

            // One material per palette slot plus one enemy material, built once. Every
            // spawned entity indexes into this single shared array, so entities with the
            // same colour and mesh land in the same render batch.
            var baseMat = Resources.Load<Material>("DOTSRemoteMaterial");
            if (baseMat == null)
            {
                baseMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            }

            var materials = new Material[Palette.Length + 1];
            for (int i = 0; i < Palette.Length; i++)
            {
                materials[i] = CreateTintedMaterial(baseMat, Palette[i]);
            }
            materials[EnemyMaterialIndex] = CreateTintedMaterial(baseMat, EnemyColor);

            _renderMeshArray = new RenderMeshArray(materials, new[] { capsule, sphere });
            IsValid = true;
        }

        public int Count => _entities.Count;

        public void Spawn(string id, bool isLocal, string type)
        {
            if (!IsValid || string.IsNullOrEmpty(id) || _entities.ContainsKey(id))
                return;

            // The kind comes from the snapshot. The record caches it, because
            // SetState and the label pass need it per frame and only Spawn is told.
            bool isEnemy = type == MobType;

            float scale;
            int colorIdx;
            int meshIdx;
            int materialIdx;

            if (isEnemy)
            {
                scale = 0.8f;
                colorIdx = 1; // red slot, for label tint
                meshIdx = SphereMeshIndex;
                materialIdx = EnemyMaterialIndex;
            }
            else if (isLocal)
            {
                scale = 1.2f;
                colorIdx = 0;
                meshIdx = CapsuleMeshIndex;
                materialIdx = 0;
            }
            else
            {
                if (!_playerColorIndex.TryGetValue(id, out colorIdx))
                {
                    colorIdx = _nextColorIndex;
                    _nextColorIndex = (_nextColorIndex % (Palette.Length - 1)) + 1;
                    _playerColorIndex[id] = colorIdx;
                }
                scale = 1f;
                meshIdx = CapsuleMeshIndex;
                materialIdx = colorIdx % Palette.Length;
            }

            var entity = _em.CreateEntity();
#if UNITY_EDITOR
            // Editor-only: names are debug niceties, and building them allocates two
            // strings per spawn.
            var shortId = id.Substring(0, System.Math.Min(8, id.Length));
            _em.SetName(entity, (isEnemy ? "enemy:" : isLocal ? "local:" : "remote:") + shortId);
#endif

            var renderMeshDescription = new RenderMeshDescription(ShadowCastingMode.On);
            RenderMeshUtility.AddComponents(entity, _em, renderMeshDescription, _renderMeshArray,
                MaterialMeshInfo.FromRenderMeshArrayIndices(materialIdx, meshIdx));

            _em.AddComponentData(entity, new LocalTransform
            {
                Position = float3.zero,
                Rotation = quaternion.identity,
                Scale = scale
            });

            // Store full network ID (not truncated) for attack targeting
            _em.AddComponentData(entity, new NetworkEntityTag
            {
                IsLocal = isLocal,
                PlayerId = new FixedString64Bytes(id),
                ColorIndex = colorIdx
            });

            if (isEnemy)
            {
                _em.AddComponentData(entity, new EnemyTag());
                _em.AddComponentData(entity, new Health { Current = 30, Max = 30 });
            }
            else
            {
                _em.AddComponentData(entity, new PlayerCombatTag());

                // LOCAL player only. Remote players got AutoAttack too for one release,
                // and every attack their ghosts fired was forwarded to the server AS THE
                // LOCAL PLAYER'S INPUT — aimed from a position up to a map away. Live
                // /status counters made it visible: 345 of 364 attacks rejected, the
                // breadcrumb reading "distance 18.42 exceeds 3.00" on a client whose own
                // firing range check was 10.
                if (isLocal)
                {
                    _em.AddComponentData(entity, new AutoAttack
                    {
                        Cooldown = 0.3f,
                        Timer = 0f,
                        // The server's validator, not a number of our own: the sample
                        // compiles against Shared.GameLogic precisely so client and
                        // server cannot disagree on a rule. The old hardcoded 10f made
                        // the client fire (and render bullets) at targets the server
                        // rejects at anything past 3.0 — visually "attacking", silently
                        // doing nothing.
                        Range = Shared.GameLogic.Components.GameConstants.AttackRange,
                        Damage = 1
                    });
                }
            }

            _entities[id] = new EntityRec
            {
                Entity = entity,
                IsEnemy = isEnemy,
                ColorIndex = colorIdx,
            };
        }

        public void Despawn(string id)
        {
            if (!IsValid || id == null || !_entities.TryGetValue(id, out var rec))
                return;

            _entities.Remove(id);
            if (_em.Exists(rec.Entity))
                _em.DestroyEntity(rec.Entity);
        }

        public void SetState(string id, float x, float y, int hp, int maxHp)
        {
            if (!IsValid || !_entities.TryGetValue(id, out var rec))
                return;

            // Change-gated: WorldViewBinder calls this for every entity every frame,
            // not only when a snapshot landed, and each EntityManager access is a
            // main-thread random chunk access that can also sync outstanding jobs on
            // that component type. Position writes preserve Rotation — the old
            // unconditional write reset it to identity every frame, silently erasing
            // any rotation another system applied (#60).
            bool hpChanged = hp != rec.Hp || maxHp != rec.MaxHp;
            bool posChanged = !rec.HasPos || x != rec.LastX || y != rec.LastY;
            if (!hpChanged && !posChanged)
                return;

            if (!_em.Exists(rec.Entity))
                return;

            if (posChanged)
            {
                var lt = _em.GetComponentData<LocalTransform>(rec.Entity);
                lt.Position = new float3(x, 0.5f, y);
                _em.SetComponentData(rec.Entity, lt);
                rec.LastX = x;
                rec.LastY = y;
                rec.HasPos = true;
            }

            if (hpChanged)
            {
                rec.Hp = hp;
                rec.MaxHp = maxHp;

                // Sync server HP to ECS Health component for enemies
                if (rec.IsEnemy && _em.HasComponent<Health>(rec.Entity))
                {
                    _em.SetComponentData(rec.Entity, new Health { Current = hp, Max = maxHp });
                }
            }
        }

        /// <summary>
        /// Enumerates all live entities with their current positions and display info.
        /// Used by the OnGUI overlay to draw floating labels. Call once per frame and
        /// cache — IMGUI raises OnGUI at least twice per frame, and this walks every
        /// entity with several EntityManager accesses each (#60).
        /// </summary>
        public void GetEntityLabels(List<EntityLabel> result)
        {
            result.Clear();
            if (!IsValid) return;

            foreach (var kv in _entities)
            {
                var id = kv.Key;
                var rec = kv.Value;
                var entity = rec.Entity;
                if (!_em.Exists(entity) || !_em.HasComponent<LocalTransform>(entity))
                    continue;

                var tag = _em.GetComponentData<NetworkEntityTag>(entity);
                var pos = _em.GetComponentData<LocalTransform>(entity).Position;
                var color = rec.IsEnemy ? EnemyColor : Palette[tag.ColorIndex % Palette.Length];

                // Read HP from ECS Health component (includes client-side prediction)
                // for enemies; fall back to snapshot state for players
                int hp, maxHp;
                if (rec.IsEnemy && _em.HasComponent<Health>(entity))
                {
                    var health = _em.GetComponentData<Health>(entity);
                    hp = health.Current;
                    maxHp = health.Max;
                }
                else
                {
                    hp = rec.Hp;
                    maxHp = rec.MaxHp;
                }

                result.Add(new EntityLabel(id, tag.IsLocal, pos, color, hp, maxHp));
            }
        }

        private static Mesh GetPrimitiveMesh(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(go);
            return mesh;
        }

        private static Material CreateTintedMaterial(Material baseMat, Color color)
        {
            var mat = new Material(baseMat);
            mat.color = color;

            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);

            return mat;
        }
    }
}
