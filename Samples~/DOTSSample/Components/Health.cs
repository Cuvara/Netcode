using Unity.Entities;

namespace DOTSSample
{
    /// <summary>Hit points for damageable entities (enemies).</summary>
    public struct Health : IComponentData
    {
        public int Current;
        public int Max;
    }
}
