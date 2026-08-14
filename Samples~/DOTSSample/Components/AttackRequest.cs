using Unity.Collections;
using Unity.Entities;

namespace DOTSSample
{
    /// <summary>
    /// Ephemeral event entity created by <see cref="AutoAttackSystem"/> when a player
    /// fires at an enemy. <see cref="DOTSNetworkBridge"/> polls these each frame,
    /// forwards the target ID to the server via <c>SendInput</c>, and destroys them.
    /// </summary>
    public struct AttackRequest : IComponentData
    {
        /// <summary>Full network entity ID of the attack target (e.g. "enemy-abc123").</summary>
        public FixedString64Bytes TargetId;
    }
}
