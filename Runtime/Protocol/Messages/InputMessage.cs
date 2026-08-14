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
    }
}
