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

        /// <summary>
        /// The server's entity kind for a hostile NPC, as it arrives on
        /// <see cref="IEntityView.Spawn"/>. This used to be an <c>"enemy-"</c> id-prefix
        /// test, which was a guess at a fact the snapshot was already carrying.
        /// </summary>
        private const string MobType = "mob";

        private readonly Dictionary<string, Entity> _entities = new Dictionary<string, Entity>();
        private readonly Dictionary<string, int> _playerColorIndex = new Dictionary<string, int>();
        private readonly Dictionary<string, (int hp, int maxHp)> _hpCache = new Dictionary<string, (int, int)>();
        private readonly HashSet<string> _enemyIds = new HashSet<string>();
        private readonly EntityManager _em;
        private readonly Mesh _capsuleMesh;
        private readonly Mesh _sphereMesh;
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
            _capsuleMesh = GetPrimitiveMesh(PrimitiveType.Capsule);
            _sphereMesh = GetPrimitiveMesh(PrimitiveType.Sphere);
            IsValid = true;
        }

        public int Count => _entities.Count;

        public void Spawn(string id, bool isLocal, string type)
        {
            if (!IsValid || string.IsNullOrEmpty(id) || _entities.ContainsKey(id))
                return;

            // The kind comes from the snapshot. '_enemyIds' still caches it, because
            // SetState and the label pass need it per frame and only Spawn is told.
            bool isEnemy = type == MobType;

            // Determine visual
            Color color;
            Mesh mesh;
            float scale;
            int colorIdx;

            if (isEnemy)
            {
                color = EnemyColor;
                mesh = _sphereMesh;
                scale = 0.8f;
                colorIdx = 1; // red slot
                _enemyIds.Add(id);
            }
            else if (isLocal)
            {
                color = Palette[0];
                mesh = _capsuleMesh;
                scale = 1.2f;
                colorIdx = 0;
            }
            else
            {
                if (!_playerColorIndex.TryGetValue(id, out colorIdx))
                {
                    colorIdx = _nextColorIndex;
                    _nextColorIndex = (_nextColorIndex % (Palette.Length - 1)) + 1;
                    _playerColorIndex[id] = colorIdx;
                }
                color = Palette[colorIdx % Palette.Length];
                mesh = _capsuleMesh;
                scale = 1f;
            }

            var material = CreatePlayerMaterial(color);

            var entity = _em.CreateEntity();
            var shortId = id.Substring(0, System.Math.Min(8, id.Length));
            _em.SetName(entity, (isEnemy ? "enemy:" : isLocal ? "local:" : "remote:") + shortId);

            var renderMeshDescription = new RenderMeshDescription(ShadowCastingMode.On);
            var renderMeshArray = new RenderMeshArray(new[] { material }, new[] { mesh });
            RenderMeshUtility.AddComponents(entity, _em, renderMeshDescription, renderMeshArray,
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

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

            _entities[id] = entity;
            _hpCache[id] = (0, 0);
        }

        public void Despawn(string id)
        {
            if (!IsValid || id == null || !_entities.TryGetValue(id, out var entity))
                return;

            _entities.Remove(id);
            _hpCache.Remove(id);
            _enemyIds.Remove(id);
            if (_em.Exists(entity))
                _em.DestroyEntity(entity);
        }

        public void SetState(string id, float x, float y, int hp, int maxHp)
        {
            if (!IsValid || !_entities.TryGetValue(id, out var entity) || !_em.Exists(entity))
                return;

            _em.SetComponentData(entity, new LocalTransform
            {
                Position = new float3(x, 0.5f, y),
                Rotation = quaternion.identity,
                Scale = _em.GetComponentData<LocalTransform>(entity).Scale
            });

            _hpCache[id] = (hp, maxHp);

            // Sync server HP to ECS Health component for enemies
            if (_enemyIds.Contains(id) && _em.HasComponent<Health>(entity))
            {
                _em.SetComponentData(entity, new Health { Current = hp, Max = maxHp });
            }
        }

        /// <summary>
        /// Enumerates all live entities with their current positions and display info.
        /// Used by the OnGUI overlay to draw floating labels.
        /// </summary>
        public void GetEntityLabels(List<EntityLabel> result)
        {
            result.Clear();
            if (!IsValid) return;

            foreach (var kv in _entities)
            {
                var id = kv.Key;
                var entity = kv.Value;
                if (!_em.Exists(entity) || !_em.HasComponent<LocalTransform>(entity))
                    continue;

                var tag = _em.GetComponentData<NetworkEntityTag>(entity);
                var pos = _em.GetComponentData<LocalTransform>(entity).Position;
                bool isEnemy = _enemyIds.Contains(id);
                var color = isEnemy ? EnemyColor : Palette[tag.ColorIndex % Palette.Length];

                // Read HP from ECS Health component (includes client-side prediction)
                // for enemies; fall back to snapshot cache for players
                int hp, maxHp;
                if (isEnemy && _em.HasComponent<Health>(entity))
                {
                    var health = _em.GetComponentData<Health>(entity);
                    hp = health.Current;
                    maxHp = health.Max;
                }
                else
                {
                    var cached = _hpCache.TryGetValue(id, out var hpData) ? hpData : (0, 0);
                    hp = cached.Item1;
                    maxHp = cached.Item2;
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

        private static Material CreatePlayerMaterial(Color color)
        {
            var baseMat = Resources.Load<Material>("DOTSRemoteMaterial");
            if (baseMat == null)
            {
                baseMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            }

            var mat = new Material(baseMat);
            mat.color = color;

            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);

            return mat;
        }
    }
}
