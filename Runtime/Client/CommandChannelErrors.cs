namespace Cuvara.Netcode.Client
{
    /// <summary>
    /// <see cref="Protocol.Messages.CommandResult.Error"/> values raised on THIS side of the
    /// command channel, when a command never got an answer from the server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A command never throws for a channel failure; it completes with one of these.</b> A
    /// caller already has to handle <c>Ok == false</c> for the server's own refusals
    /// (<c>unknown_opcode</c>, <c>rate_limited</c>, <c>invalid_payload</c>, opcode-specific
    /// codes), so a disconnect arriving as an exception instead would be a second failure path
    /// for the same outcome -- "the command did not happen" -- and the one a caller forgets.
    /// Cancellation through the caller's token is the exception to the rule and surfaces as
    /// <see cref="System.OperationCanceledException"/>, because the caller asked for it.
    /// </para>
    /// <para>
    /// Every name here is distinct from every server code, so <see cref="IsLocal"/> can tell
    /// "the server said no" from "the server never heard it". The difference matters for a
    /// retry: a local failure means the command may or may not have executed
    /// (<see cref="ConnectionClosed"/>) or certainly did not (the others).
    /// </para>
    /// </remarks>
    public static class CommandChannelErrors
    {
        /// <summary>
        /// There is no gameplay connection to send on. The command was not sent.
        /// </summary>
        public const string NotConnected = "client_not_connected";

        /// <summary>
        /// The game server speaks a wire protocol older than version 3 (or advertised none), so
        /// it has no command channel (ADR-30). The command was not sent: a version 2 server
        /// would drop MsgType 32 as unknown and the result would never come.
        /// </summary>
        public const string ProtocolTooOld = "client_protocol_too_old";

        /// <summary>
        /// The connection ended -- a drop, a kick, a transfer, a reconnect, a disconnect --
        /// while the command was in flight. <b>It may or may not have executed</b>: the request
        /// may have reached the server and only the result been lost. A command that must not
        /// run twice needs an idempotency key in its payload before it is retried.
        /// </summary>
        public const string ConnectionClosed = "client_connection_closed";

        /// <summary>
        /// Whether <paramref name="error"/> is one of the client-side names above rather than a
        /// code the server sent.
        /// </summary>
        public static bool IsLocal(string error) =>
            error == NotConnected || error == ProtocolTooOld || error == ConnectionClosed;
    }
}
