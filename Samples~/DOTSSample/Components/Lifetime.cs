using Unity.Entities;

namespace DOTSSample
{
    /// <summary>Auto-destroy timer. Entity is destroyed when Remaining reaches zero.</summary>
    public struct Lifetime : IComponentData
    {
        public float Remaining;
    }
}
