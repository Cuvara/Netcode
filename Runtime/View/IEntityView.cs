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
        /// <param name="id">The entity id. For players this is the Nakama user id.</param>
        /// <param name="isLocal">Whether this id is the local player's.</param>
        /// <param name="type">
        /// The server's entity kind — <c>"player"</c>, <c>"mob"</c>, <c>"npc"</c>,
        /// <c>"item"</c>, <c>"projectile"</c>, or whatever a newer simulation sends that
        /// this build's schema does not name yet. Never null; empty when the server sent
        /// no type at all.
        /// </param>
        /// <remarks>
        /// <para>
        /// <b>Why this is a parameter and not something the view infers.</b> The kind
        /// crosses the wire on every snapshot, keyframe and delta alike, so a view that
        /// guesses it from the shape of the id is re-deriving a fact it was already
        /// given. Two separate implementations did exactly that before this parameter
        /// existed — both keyed on an <c>"enemy-"</c> id prefix — which is a decoding
        /// rule invented by the presentation layer, silently coupled to how the server
        /// happens to name things, and wrong the moment it stops.
        /// </para>
        /// <para>
        /// Passed at spawn rather than on every <c>SetState</c> because kind does not
        /// change over an entity's lifetime; a view that needs it later should keep it.
        /// </para>
        /// </remarks>
        void Spawn(string id, bool isLocal, string type);

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
