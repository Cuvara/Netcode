using System;

namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// game server -> client (33): the answer to exactly one <see cref="CommandRequest"/>,
    /// correlated by <see cref="Seq"/> (ADR-30, protocol version 3).
    /// </summary>
    /// <remarks>
    /// Results are reliable and ordered like every other message on the gameplay hop and are
    /// never coalesced: each request gets exactly one.
    /// </remarks>
    public sealed class CommandResult : IWireMessage
    {
        /// <summary>The <see cref="CommandRequest.Seq"/> this answers.</summary>
        public uint Seq { get; set; }

        /// <summary>True when the server executed the command.</summary>
        public bool Ok { get; set; }

        /// <summary>
        /// Machine-readable reason when <see cref="Ok"/> is false -- <c>unknown_opcode</c>,
        /// <c>rate_limited</c>, <c>invalid_payload</c>, or an opcode-specific code. Empty on
        /// success. Failures raised on THIS side (the connection closed, the server is too old)
        /// use the names in <c>Cuvara.Netcode.Client.CommandChannelErrors</c> instead.
        /// </summary>
        public string Error { get; set; } = string.Empty;

        /// <summary>Opcode-specific response payload, possibly empty. Never null once decoded.</summary>
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        public override string ToString() =>
            Ok ? $"CommandResult(seq={Seq}, ok, {Payload?.Length ?? 0} bytes)"
               : $"CommandResult(seq={Seq}, error={Error})";
    }
}
