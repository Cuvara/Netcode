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
    public sealed class GameObjectEntityView : IEntityView, IEntityPoseView
    {
        private readonly Dictionary<string, GameObject> _objects = new Dictionary<string, GameObject>();

        /// <summary>
        /// Last hp/maxHp applied per id, so the health squash below only writes
        /// <c>localScale</c> when health actually changed. A scale write dirties the
        /// transform hierarchy and rebuilds matrices even when the value is identical,
        /// and HP is snapped, not interpolated — unchanged on the vast majority of
        /// frames (#60).
        /// </summary>
        private readonly Dictionary<string, (int Hp, int MaxHp)> _appliedHp =
            new Dictionary<string, (int, int)>();
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

        public void Spawn(string id, bool isLocal, string type)
        {
            if (string.IsNullOrEmpty(id) || _objects.ContainsKey(id))
            {
                return;
            }

            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);

            // The kind goes in the name and nowhere else. Giving mobs their own mesh or
            // colour here would be presentation policy, and this view exists to be the
            // dumbest thing that can be looked at — but a name makes the value visible
            // in the hierarchy, which is what makes it verifiable.
            go.name = (isLocal ? "local:" : "remote:")
                      + (string.IsNullOrEmpty(type) ? "" : type + ":")
                      + Short(id);

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
            _appliedHp.Remove(id);
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
            // One line, no UI, readable in a screenshot. Written only on change — see
            // _appliedHp.
            if (maxHp > 0 &&
                (!_appliedHp.TryGetValue(id, out var applied) ||
                 applied.Hp != hp || applied.MaxHp != maxHp))
            {
                _appliedHp[id] = (hp, maxHp);
                var health = Mathf.Clamp01((float)hp / maxHp);
                var s = go.transform.localScale;
                go.transform.localScale = new Vector3(s.x, Mathf.Lerp(0.3f, s.x, health), s.z);
            }
        }

        /// <summary>
        /// Applies the entity's facing as a Y rotation. Action is accepted and not yet
        /// rendered — see the remarks.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A zero facing writes nothing at all.</b> Zero is the wire's reserved
        /// "not sent", so the transform keeps whatever rotation it already had. Snapping
        /// to identity instead would point every entity from a server predating the field
        /// the same way, which looks like a content bug rather than a missing field — and
        /// holding the last value is also what makes a character that stops walking keep
        /// looking where it was going.
        /// </para>
        /// <para>
        /// <b>Action is deliberately not rendered here.</b> This view draws primitives
        /// with no animator, so there is nothing to drive with it; inventing a visual for
        /// it — a colour swap, a scale pop — would be this layer guessing at a game's art
        /// direction. The value reaches the view so a real one can use it, and the
        /// parameter is named rather than dropped so that intent is visible.
        /// </para>
        /// </remarks>
        public void SetPose(string id, uint facingBrad, Shared.GameLogic.Components.EntityAction action)
        {
            if (id == null || !_objects.TryGetValue(id, out var go) || go == null) return;

            if (Protocol.FacingCodec.TryToUnityYaw(facingBrad, out float yaw))
            {
                go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
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
            _appliedHp.Clear();
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
