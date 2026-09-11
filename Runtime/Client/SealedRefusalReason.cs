namespace Cuvara.Netcode.Client
{
    /// <summary>
    /// Reasons a game server gives when it refuses a session for lack of encryption.
    /// </summary>
    /// <remarks>
    /// These strings are a wire contract with the C# game server
    /// (<c>GameServer/Net/Sealed/SealedPolicy.cs</c>). They arrive as the reason on a kick
    /// or in a rejected join reply, and they exist so a refusal can be acted on rather than
    /// only logged — a refusal nobody can attribute presents as a flaky network.
    /// </remarks>
    public static class SealedRefusalReason
    {
        /// <summary>
        /// The peer never completed the sealed handshake. Arrives as a kick reason, after
        /// the join was accepted: the server cannot know until it has waited, because the
        /// client will not run the key exchange before it knows the join succeeded.
        /// </summary>
        public const string NoSealedSession = "no_sealed_session";

        /// <summary>
        /// The client's wire encoding cannot carry a sealed session at all. Arrives in the
        /// join reply's error, not as a kick, because no handshake can change the answer.
        /// <b>Nothing at runtime fixes this</b> — the JSON message set has no sealed frame,
        /// so the only fix is a client that speaks Protobuf.
        /// </summary>
        public const string EncodingCannotSeal = "encoding_cannot_seal";
    }
}
