using System.Collections.Generic;
using UnityEngine;

namespace Cuvara.Netcode.View
{
    /// <summary>
    /// Renders replicated entities as primitive GameObjects. Deliberately the dumbest
    /// implementation that can be looked at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No interpolation, on purpose.</b> Positions are applied exactly as the server
    /// sent them, so remote entities visibly step at the tick rate. Smoothing here would
    /// hide the tick rate and hide dropped snapshots — the two things this is meant to
    /// make observable. Interpolation belongs in a layer above, added when someone is
    /// actually judging how it feels.
    /// </para>
    /// <para>
    /// <b>No prediction.</b> Even the local player moves only when the server says so, so
    /// what is on screen is the authoritative state and nothing else. Mixing prediction in
    /// would make a wrong position ambiguous between a netcode fault and a reconciliation
    /// fault.
    /// </para>
    /// <para>
    /// The server simulates on a 2D plane (x, y); those map to Unity's X and Z so a camera
    /// looking down sees the world as the server lays it out.
    /// </para>
    /// </remarks>
    public sealed class GameObjectEntityView : IEntityView
    {
        private readonly Dictionary<string, GameObject> _objects = new Dictionary<string, GameObject>();
        private readonly Transform _root;
        private readonly Material _localMaterial;
        private readonly Material _remoteMaterial;

        public GameObjectEntityView(Transform root = null)
        {
            _root = root;

            // Built-in URP-agnostic unlit colours so this works without any project art
            // or render-pipeline assumptions.
            _localMaterial = MakeMaterial(new Color(0.20f, 0.85f, 0.30f));   // local: green
            _remoteMaterial = MakeMaterial(new Color(0.95f, 0.30f, 0.25f));  // remote: red
        }

        /// <summary>Live view count, for assertions in tests.</summary>
        public int Count => _objects.Count;

        public void Spawn(string id, bool isLocal)
        {
            if (string.IsNullOrEmpty(id) || _objects.ContainsKey(id))
            {
                return;
            }

            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = (isLocal ? "local:" : "remote:") + Short(id);

            // A collider would let the two capsules shove each other around locally,
            // which would be client-side physics quietly disagreeing with the server.
            var collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                Object.Destroy(collider);
            }

            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = isLocal ? _localMaterial : _remoteMaterial;
            }

            if (_root != null)
            {
                go.transform.SetParent(_root, false);
            }

            // Local is slightly larger as a second cue, so the two are distinguishable
            // even in a greyscale screenshot or for a colour-blind reader.
            go.transform.localScale = isLocal ? new Vector3(1.2f, 1.2f, 1.2f) : Vector3.one;

            _objects[id] = go;
        }

        public void Despawn(string id)
        {
            if (id == null || !_objects.TryGetValue(id, out var go))
            {
                return;
            }

            _objects.Remove(id);
            if (go != null)
            {
                Object.Destroy(go);
            }
        }

        public void SetState(string id, float x, float y, int hp, int maxHp)
        {
            if (id == null || !_objects.TryGetValue(id, out var go) || go == null)
            {
                return;
            }

            // Server 2D (x, y) -> Unity (x, _, z). Y is left at the capsule's half height
            // so it sits on the ground plane rather than through it.
            go.transform.position = new Vector3(x, 1f, y);

            // HP as vertical squash: full health is upright, near-death is flattened.
            // One line, no UI, readable in a screenshot.
            if (maxHp > 0)
            {
                var health = Mathf.Clamp01((float)hp / maxHp);
                var s = go.transform.localScale;
                go.transform.localScale = new Vector3(s.x, Mathf.Lerp(0.3f, s.x, health), s.z);
            }
        }

        /// <summary>Destroys every view. For teardown between sessions.</summary>
        public void Clear()
        {
            foreach (var kv in _objects)
            {
                if (kv.Value != null)
                {
                    Object.Destroy(kv.Value);
                }
            }

            _objects.Clear();
        }

        private static Material MakeMaterial(Color colour)
        {
            // Unlit avoids depending on a light being present in whatever scene hosts this.
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Sprites/Default");

            var material = new Material(shader);
            material.color = colour;
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", colour);
            }

            return material;
        }

        private static string Short(string id) => id.Length <= 8 ? id : id.Substring(0, 8);
    }
}
