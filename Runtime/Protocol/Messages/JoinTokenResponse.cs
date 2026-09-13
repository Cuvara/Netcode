namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// game server -> client (6). On failure the game server closes the socket
    /// straight after this frame.
    /// </summary>
    public sealed class JoinTokenResponse : IWireMessage
    {
        public bool Ok { get; set; }

        public string UserId { get; set; } = string.Empty;

        /// <summary>
        /// <c>Invalid or expired token</c>, <c>Token is for a different server</c>,
        /// <c>Token already used</c>, or <c>Expected JoinToken message</c>.
        /// </summary>
        public string Error { get; set; } = string.Empty;

        /// <summary>
        /// The server's simulation tick rate in Hz — the cadence its movement integration
        /// runs at, and therefore the <c>dt</c> a client must predict with.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Zero means "not sent", not "no ticks".</b> proto3 elides a zero, so a server
        /// predating this field is indistinguishable from one advertising nothing. Treat a
        /// non-positive value as absent and fall back to a configured default — the same
        /// rule as <c>EntitySnapshot.Speed</c>, deliberately, because it is the same
        /// situation and a second convention for it would be a trap.
        /// </para>
        /// <para>
        /// <b>Why this exists.</b> Tick rate was a constant shared by convention across two
        /// repositories. When the server moved its movement integration to a 60 Hz critical
        /// group while the client still assumed 15, the client predicted four times the
        /// distance the server applied — and at the default speed that is 0.25 world units
        /// per input, which sits *under* the correction-smoothing threshold and so produces
        /// no visible snap at all. It feels soft and slightly wrong rather than broken,
        /// which is the hardest kind of wrong to find.
        /// </para>
        /// </remarks>
        public uint TickRate { get; set; }

        /// <summary>
        /// The game server's own wire protocol version.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Echoed so a NEW client can detect an OLD game server: one predating this field
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
