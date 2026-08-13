using Unity.Collections;
using Unity.Entities;

namespace DOTSSample
{
    /// <summary>
    /// Tags an ECS entity as a replicated network entity. <see cref="IsLocal"/> is
    /// true for the player this client controls.
    /// </summary>
    public struct NetworkEntityTag : IComponentData
    {
        public bool IsLocal;

        /// <summary>Short player identifier (first 8 chars of Nakama user id).</summary>
        public FixedString64Bytes PlayerId;

        /// <summary>Index into the colour palette (0 = local player).</summary>
        public int ColorIndex;
    }
}
