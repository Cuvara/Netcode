namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// Which transport a link uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Realtime gameplay is KCP over UDP only.</b> <see cref="Kcp"/> is the one value the
    /// game-server hop accepts: it is what the gateway's <c>enter_world_resp</c> carries
    /// (<c>"kcp"</c>), and <see cref="TransportKinds.ParseGameplay"/> refuses anything else —
    /// an empty field and <c>"tcp"</c> included. There is no TCP fallback for gameplay.
    /// </para>
    /// <para>
    /// <see cref="Tcp"/> and <see cref="TcpTls"/> exist for the <b>gateway hop only</b>
    /// (auth + map assignment, ADR-3). Whether that hop is wrapped in TLS is a property of
    /// the gateway deployment the client dials into (ADR-23), known before the first byte;
    /// nothing on the wire names either value, and <c>GameSessionClient</c> refuses them.
    /// </para>
    /// </remarks>
    public enum TransportKind
    {
        /// <summary>
        /// Plaintext TCP. <b>Gateway hop only</b> — never a gameplay transport, and not a
        /// value <c>enter_world_resp</c> can select.
        /// </summary>
        Tcp = 0,

        /// <summary>
        /// KCP over UDP: the only gameplay (game-server hop) transport. What a <c>"kcp"</c>
        /// transport field means.
        /// </summary>
        Kcp = 1,

        /// <summary>
        /// TCP wrapped in TLS, for a gateway that terminates TLS itself (ADR-23).
        /// <b>Gateway hop only</b> — the game-server hop is KCP and is sealed at the message
        /// layer instead (ADR-22).
        /// </summary>
        TcpTls = 2
    }
}
