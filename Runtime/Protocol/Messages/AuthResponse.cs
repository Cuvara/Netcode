namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// gateway -> client (2). Also the gateway's generic error frame: a failed
    /// precondition on any request other than <c>enter_world</c> comes back here
    /// with <see cref="Ok"/> false.
    /// </summary>
    public sealed class AuthResponse : IWireMessage
    {
        public bool Ok { get; set; }

        public string UserId { get; set; } = string.Empty;

        /// <summary>
        /// One of the gateway's closed error set (<c>invalid token</c>,
        /// <c>session expired</c>, <c>rate limited</c>, <c>internal error</c>, ...).
        /// Never internal error text.
        /// </summary>
        public string Error { get; set; } = string.Empty;

        /// <summary>
        /// The gateway's own wire protocol version.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Echoed so a NEW client can detect an OLD gateway: one predating this field
        /// does not know it, ignores the version the client sent, and replies without
        /// one. A zero coming back is therefore the client's ONLY signal that its
        /// version was never checked — nothing else on the wire reveals it.
        /// </para>
        /// <para>
        /// <b>Zero means "not advertised", not "version zero".</b> Real versions start
        /// at 1 precisely so the two are distinguishable, since proto3 elides a zero.
        /// Same rule as <c>Speed</c> and <c>TickRate</c>, deliberately — a second
        /// convention for the same situation would be a trap.
        /// </para>
        /// <para>
        /// Present on a REJECTION too, unlike <c>TickRate</c>: a client refused for a
        /// version mismatch has to be told which version it failed against, or the
        /// refusal is as opaque as the parse error it replaces.
        /// </para>
        /// </remarks>
        public uint ProtocolVersion { get; set; }
    }
}
