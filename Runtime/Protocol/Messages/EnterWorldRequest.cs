namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>client -> gateway (3). Asks the gateway to assign a game server.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="PartyId"/> selects between the two things this one message can ask for, and
    /// ADR-26 decision 1 is why the protocol has one message rather than two:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>empty</b> -- a MAP server for <see cref="MapId"/>, the flow that has always existed.
    /// </description></item>
    /// <item><description>
    /// <b>non-empty</b> -- a DUNGEON INSTANCE of the content named by <see cref="MapId"/>, for
    /// this party. The first member's request allocates a pod; every later member is handed the
    /// SAME address, because the instance is keyed by the party and not by the content.
    /// </description></item>
    /// </list>
    /// <para>
    /// The gateway verifies the caller really is in the named party, once per entry. Naming a
    /// party you are not in is refused with <c>not a member of that party</c> -- it does not
    /// quietly fall back to a map, so a client that sets this by mistake fails loudly.
    /// </para>
    /// </remarks>
    public sealed class EnterWorldRequest : IWireMessage
    {
        /// <summary>The map to enter, or the dungeon content to instance.</summary>
        public string MapId { get; set; } = string.Empty;

        /// <summary>
        /// Non-empty asks for a dungeon instance for this party. Empty asks for a map server.
        /// </summary>
        public string PartyId { get; set; } = string.Empty;
    }
}
