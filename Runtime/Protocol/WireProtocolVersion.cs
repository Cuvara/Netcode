namespace Cuvara.Netcode.Protocol
{
    /// <summary>
    /// The wire schema version this client implements, and the rules for reading a
    /// peer's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The number names the SEMANTICS of <c>shared/proto/wire.proto</c> — what the
    /// fields mean — not its shape and not its encoding. Shape is self-describing
    /// (proto3 skips unknown fields) and encoding is sniffed from byte 0; neither
    /// catches two peers that parse every byte and then disagree about what a field
    /// means. That is the failure this exists to make loud, and it is the one this
    /// codebase keeps getting bitten by: it compiles, it connects, and the numbers
    /// are consistent about the wrong thing.
    /// </para>
    /// <para>
    /// <b>Bump it</b> for: reusing or renumbering a field, removing a field a
    /// receiver acts on, changing the meaning/units/reference frame of an existing
    /// field, changing the snapshot state machine (handle lifecycle, keyframe reset,
    /// the delta "changed" rule, the merge algorithm), or adding something a receiver
    /// MUST act on to stay correct. Do NOT bump for a purely additive optional field
    /// covered by a documented "zero means not sent" rule — an old peer ignoring it
    /// is then a supported configuration, which is the point of writing that rule
    /// down.
    /// </para>
    /// <para>
    /// Mirrors <c>shared/messages.WireProtocolVersion</c> (Go) and
    /// <c>GameServer/Net/WireProtocol.ProtocolVersion</c> (C# server). No language
    /// can be authoritative for the other two, so each pins the value and asserts it
    /// on its own side; the full contract lives in <c>wire.proto</c> under "Protocol
    /// version" and normatively in <c>gameserver-dotnet/docs/API.md</c>.
    /// </para>
    /// </remarks>
    public static class WireProtocolVersion
    {
        /// <summary>The version this build speaks. Sent on both handshake hops.</summary>
        public const uint Current = 1;

        /// <summary>
        /// The wire value meaning "this peer does not advertise a version" — a peer
        /// built before the field existed.
        /// </summary>
        /// <remarks>
        /// proto3 elides a zero uint32, so an absent field and an explicit 0 are the
        /// same bytes. Real versions therefore start at 1 and 0 is permanently
        /// reserved for "unknown", exactly as <c>ENTITY_TYPE_UNSPECIFIED</c> reserves
        /// 0 in the entity-type enum. Never read 0 as "version zero".
        /// </remarks>
        public const uint Unversioned = 0;

        /// <summary>
        /// Reports whether a server that answered with <paramref name="serverVersion"/>
        /// is one this client can trust to mean the same things by the same fields.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only ever called on a server that already ACCEPTED the join: the server
        /// side of each hop is what refuses, and this is the client's cross-check for
        /// the one case the server cannot report — an OLD server, which does not know
        /// the field, ignores the client's version and replies without one. A zero
        /// coming back is the client's only signal that its version was never
        /// checked; nothing else on the wire reveals it.
        /// </para>
        /// <para>
        /// An unversioned server is reported as compatible, matching the servers'
        /// own shipping default of admitting unversioned peers. Refusing here while
        /// the servers admit would make the client the strictest party in the system
        /// and lock a working fleet out of itself; the migration is driven from the
        /// server's <c>--min-protocol-version</c>, in one place, not three.
        /// Callers should surface <see cref="IsUnversioned"/> so the trust is
        /// visible rather than silent.
        /// </para>
        /// </remarks>
        public static bool IsCompatible(uint serverVersion)
            => serverVersion == Current || serverVersion == Unversioned;

        /// <summary>
        /// Reports whether a peer advertised no version at all, i.e. it predates the
        /// field. Distinct from incompatible: it is admitted, but on trust.
        /// </summary>
        public static bool IsUnversioned(uint serverVersion) => serverVersion == Unversioned;
    }
}
