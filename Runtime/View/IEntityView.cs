namespace Cuvara.Netcode.View
{
    /// <summary>
    /// Presents replicated entities. The netcode never talks to Unity objects; it hands
    /// ids and state to this, and something else decides what that looks like.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately three methods. The seam exists so a DOTS implementation can replace
    /// <see cref="GameObjectEntityView"/> later without the netcode noticing, and a
    /// narrow interface is what makes that swap cheap. Anything richer — animation,
    /// interpolation, name plates, culling — belongs above this, not in it.
    /// </para>
    /// <para>
    /// Entity ids are Nakama user ids, and the local player's id is
    /// <c>NetworkClient.UserId</c>, so <c>isLocal</c> is decidable without any extra
    /// wire field.
    /// </para>
    /// </remarks>
    public interface IEntityView
    {
        /// <summary>An id appeared in the world for the first time.</summary>
        void Spawn(string id, bool isLocal);

        /// <summary>An id is gone from the world.</summary>
        void Despawn(string id);

        /// <summary>
        /// Latest authoritative state for an already-spawned id. Called once per
        /// reconcile, so implementations should be cheap and must not assume a fixed
        /// cadence — snapshots arrive at the server's tick rate, not the frame rate.
        /// </summary>
        void SetState(string id, float x, float y, int hp, int maxHp);
    }
}
