using Unity.Entities;

namespace DOTSSample
{
    /// <summary>
    /// Singleton holding prefab entity references for combat spawning.
    /// Created by <see cref="CombatBootstrap"/> at startup.
    /// </summary>
    public struct CombatPrefabs : IComponentData
    {
        public Entity EnemyPrefab;
        public Entity BulletPrefab;
    }
}
