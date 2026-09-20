namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// client -> game server (7), once per client input tick.
    /// </summary>
    /// <remarks>
    /// This type only carries the fields; it applies no rules. Direction
    /// normalisation, speed, range and cooldown are server-authoritative and live
    /// in <c>Shared.GameLogic</c> — a client-side copy would silently diverge.
    /// </remarks>
    public sealed class InputMessage : IWireMessage
    {
        /// <summary>
        /// The client's own monotonically increasing input sequence number, not a
        /// server tick. The server drops any input whose tick is not strictly
        /// greater than the last it accepted, and echoes the newest accepted value
        /// back as <c>ack_tick</c>.
        /// </summary>
        public long Tick { get; set; }

        /// <summary>Movement direction on X, not a displacement.</summary>
        public float MoveX { get; set; }

        /// <summary>Movement direction on Y, not a displacement.</summary>
        public float MoveY { get; set; }

        /// <summary>Optional entity id to attack this tick. Empty when not attacking.</summary>
        public string AttackTargetId { get; set; } = string.Empty;

        /// <summary>
        /// Content id of the ability to use this tick, or 0 for none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Zero means "no ability".</b> Ability ids are allocated from 1 by the content
        /// validator for exactly this reason: proto3 elides a zero, so id 0 and "sent no
        /// ability" would be identical bytes.
        /// </para>
        /// <para>
        /// <b>This is a request, and it is not predicted.</b> Prediction covers movement
        /// only, because movement is a pure function of input the client already has while
        /// an ability outcome depends on cooldowns, content and other entities' state. The
        /// only report of the outcome is the snapshot: a
        /// <see cref="GameEventType.AbilityCast"/> event if it resolved, nothing if it did
        /// not. Do not move a cooldown bar or play a cast animation off this field — play
        /// them off the event.
        /// </para>
        /// </remarks>
        public uint AbilityId { get; set; }

        /// <summary>
        /// Target entity for an entity-targeted ability, empty otherwise. A server-side
        /// entity ID, in the same space as <see cref="AttackTargetId"/> — never a
        /// <see cref="EntitySnapshot.Handle"/>, which the server allocates for its own
        /// outbound snapshots and would not recognise coming back.
        /// </summary>
        public string AbilityTargetId { get; set; } = string.Empty;

        /// <summary>
        /// Aim POINT in world coordinates for a ground-targeted ability, unlike
        /// <see cref="MoveX"/>/<see cref="MoveY"/> which are a direction. Only read by the
        /// server when <see cref="AbilityId"/> is non-zero; the world origin is a
        /// legitimate aim point, so (0,0) does not mean "not aimed".
        /// </summary>
        public float AimX { get; set; }

        /// <inheritdoc cref="AimX"/>
        public float AimY { get; set; }
    }
}
