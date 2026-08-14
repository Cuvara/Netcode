using Unity.Entities;

namespace DOTSSample
{
    /// <summary>Marks an ECS entity as a local combat player (auto-attacks enemies).</summary>
    public struct PlayerCombatTag : IComponentData { }
}
