using System.Collections.Generic;

namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// game server -> client (8), once per server tick per client. Either a
    /// keyframe (<see cref="Full"/>) carrying the complete AOI set, or a delta
    /// carrying only changed entities plus <see cref="Removed"/>.
    /// </summary>
    public sealed class SnapshotMessage : IWireMessage
    {
        /// <summary>Server simulation tick this snapshot describes.</summary>
        public long Tick { get; set; }

        /// <summary>
        /// Highest input tick the server has accepted for this client's own
        /// entity. Zero means "no input accepted yet" — not an ack of tick 0.
        /// The netcode surfaces it and never consumes it; reconciliation is a
        /// separate workstream.
        /// </summary>
        public long AckTick { get; set; }

        /// <summary>
        /// Keyframe marker. True: <see cref="Entities"/> is the complete AOI set
        /// and everything not listed must be discarded.
        /// </summary>
        public bool Full { get; set; }

        public List<EntitySnapshot> Entities { get; } = new List<EntitySnapshot>();

        /// <summary>
        /// Entity ids that left the AOI or the world. Deltas only; never present
        /// on a keyframe. Always plain ids, never handles.
        /// </summary>
        public List<string> Removed { get; } = new List<string>();
    }
}
