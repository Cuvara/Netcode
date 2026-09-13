namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// Which transport a link uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not all of these can come off the wire.</b> <see cref="Tcp"/> and
    /// <see cref="Kcp"/> are what a game server can advertise in
    /// <c>enter_world_resp</c>, and <see cref="TransportKinds.Parse"/> maps those two
    /// strings. <see cref="TcpTls"/> is a <i>client-side</i> choice about the gateway hop:
    /// nothing on the wire names it, <c>Parse</c> refuses it like any other unknown value,
    /// and it never reaches <c>GameSessionClient</c>.
    /// </para>
    /// <para>
    /// The asymmetry is real rather than an oversight. Whether the gateway terminates TLS
    /// is a property of the deployment the client is dialling into, known before the first
    /// byte is sent; whether the game server speaks TCP or KCP is a property of the server
    /// the gateway assigns, known only after it answers.
    /// </para>
    /// </remarks>
    public enum TransportKind
    {
        /// <summary>Plaintext TCP. What an empty or <c>"tcp"</c> transport field means.</summary>
        Tcp = 0,

        /// <summary>KCP over UDP. What a <c>"kcp"</c> transport field means.</summary>
        Kcp = 1,

        /// <summary>
        /// TCP wrapped in TLS, for a gateway that terminates TLS itself (ADR-23).
        /// Client-side only — a server never asks for this, and the game-server hop never
        /// uses it, because that hop is sealed at the message layer instead (ADR-22).
        /// </summary>
        TcpTls = 2
    }
}
