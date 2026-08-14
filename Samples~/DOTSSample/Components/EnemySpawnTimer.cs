using Unity.Entities;

namespace DOTSSample
{
    /// <summary>Singleton controlling enemy wave spawning.</summary>
    public struct EnemySpawnTimer : IComponentData
    {
        public float Timer;
        public float Interval;
        public int BatchSize;
        public uint Seed;
    }
}
