using Unity.Entities;

namespace DOTSSample
{
    /// <summary>Singleton tracking combat statistics for the HUD.</summary>
    public struct CombatStats : IComponentData
    {
        public int Kills;
        public int ActiveEnemies;
        public int BulletsFired;
    }
}
