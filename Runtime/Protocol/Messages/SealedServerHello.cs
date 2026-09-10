using System;

namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>game server -> client (17). Answers <see cref="SealedClientHello"/>.</summary>
    public sealed class SealedServerHello : IWireMessage
    {
        /// <summary>The server's ephemeral X25519 public key, 32 bytes.</summary>
        public byte[] PublicKey { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// HMAC-SHA256 over the handshake transcript, keyed by join-token-derived material.
        /// 32 bytes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is what lets the client detect a man in the middle: the transcript covers
        /// BOTH ephemeral public keys, so an attacker who substitutes its own key changes the
        /// transcript and the binding it read off the wire no longer verifies.
        /// </para>
        /// <para>
        /// <b>A shipped Unity client cannot verify it yet.</b> Verification needs material
        /// derived from <c>JOIN_TOKEN_SECRET</c>, which a client binary must not carry — see
        /// <c>SealedClientExchange.WithoutBindingVerification</c>, which names that state
        /// rather than hiding it, and ADR-22's pinned gateway identity key, which closes it.
        /// </para>
        /// </remarks>
        public byte[] Binding { get; set; } = Array.Empty<byte>();

        /// <summary>Non-empty when the server refused. The client must close, never continue unsealed.</summary>
        public string Error { get; set; } = string.Empty;
    }
}
