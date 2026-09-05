using System.Collections.Generic;
using Cuvara.Netcode.Protocol.Messages;

namespace Cuvara.Netcode.Snapshot
{
    /// <summary>
    /// Extends resolved snapshot data with status effects and ability events per entity.
    /// Separate from <see cref="ResolvedEntity"/> to keep the hot struct small — effects
    /// and events are looked up by id when needed, not carried on every entity every frame.
    /// </summary>
    public sealed class SnapshotExtensions
    {
        private readonly Dictionary<string, List<StatusEffect>> _effects = new();
        private readonly List<AbilityEvent> _abilityEvents = new();

        /// <summary>Active status effects per entity id.</summary>
        public IReadOnlyDictionary<string, List<StatusEffect>> Effects => _effects;

        /// <summary>Ability events that fired this tick (one-shot, cleared next tick).</summary>
        public IReadOnlyList<AbilityEvent> AbilityEvents => _abilityEvents;

        /// <summary>Sets the effects for an entity (from snapshot delta).</summary>
        public void SetEffects(string entityId, List<StatusEffect> effects)
        {
            if (effects == null || effects.Count == 0)
                _effects.Remove(entityId);
            else
                _effects[entityId] = effects;
        }

        /// <summary>Gets the effects on an entity. Null if none.</summary>
        public List<StatusEffect> GetEffects(string entityId)
        {
            return _effects.TryGetValue(entityId, out var list) ? list : null;
        }

        /// <summary>Removes all effects for an entity (despawned or left AOI).</summary>
        public void RemoveEntity(string entityId) => _effects.Remove(entityId);

        /// <summary>Adds an ability event for this tick.</summary>
        public void AddAbilityEvent(AbilityEvent evt) => _abilityEvents.Add(evt);

        /// <summary>Clears one-shot ability events (call at start of each tick).</summary>
        public void ClearAbilityEvents() => _abilityEvents.Clear();

        /// <summary>Clears everything (map transfer, reconnect).</summary>
        public void Reset()
        {
            _effects.Clear();
            _abilityEvents.Clear();
        }
    }
}
