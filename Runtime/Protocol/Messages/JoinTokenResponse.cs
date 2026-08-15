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
    }
}
