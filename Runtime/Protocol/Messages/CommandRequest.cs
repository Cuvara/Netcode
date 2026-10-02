using System;

namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// client -> game server (32): one discrete gameplay command on the generic channel
    /// (ADR-30, protocol version 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This package only moves bytes.</b> Each <see cref="Opcode"/> names a payload schema
    /// defined in Shared.GameLogic's <c>gameplay.proto</c> and encoded by that library's codec
    /// (<c>Shared.GameLogic.Gameplay</c>), so a new command never needs a Netcode release.
    /// </para>
    /// <para>
    /// Send it through <c>GameSessionClient.SendCommandAsync</c>, which allocates
    /// <see cref="Seq"/> and correlates the <see cref="CommandResult"/>; building one by hand
    /// is for tests and tools.
    /// </para>
    /// </remarks>
    public sealed class CommandRequest : IWireMessage
    {
        /// <summary>
        /// Client-chosen, unique per connection, echoed in the <see cref="CommandResult"/>.
        /// Starts at 1, so a zero can never be mistaken for a real request.
        /// </summary>
        public uint Seq { get; set; }

        /// <summary>
        /// Opcode from Shared.GameLogic's <c>GameplayOpcodes</c>. Allocated from 1; the server
        /// refuses 0.
        /// </summary>
        public uint Opcode { get; set; }

        /// <summary>Opcode-specific payload, possibly empty. Never null once decoded.</summary>
        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }
}
