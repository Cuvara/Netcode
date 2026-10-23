using System;

namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// game server -> client (34): an unsolicited gameplay message (inventory changed, quest
    /// progress, chat line), addressed by <see cref="Opcode"/> in the same space as
    /// <see cref="CommandRequest.Opcode"/> (ADR-30, protocol version 3).
    /// </summary>
    public sealed class ServerPush : IWireMessage
    {
        /// <summary>Opcode from Shared.GameLogic's <c>GameplayOpcodes</c>.</summary>
        public uint Opcode { get; set; }

        /// <summary>Opcode-specific payload, possibly empty. Never null once decoded.</summary>
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        public override string ToString() => $"ServerPush(opcode={Opcode}, {Payload?.Length ?? 0} bytes)";
    }
}
