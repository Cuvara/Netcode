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

        /// <summary>
        /// Ed25519 signature, 64 bytes, over the server's per-pod identity key (ADR-25), or
        /// empty from a server that predates it.
        /// </summary>
        /// <remarks>
        /// <b>This is the replacement for <see cref="Binding"/>, not a second copy of it.</b>
        /// The binding is a symmetric MAC under <c>JOIN_TOKEN_SECRET</c> — the key the gateway
        /// mints join tokens with — so a client able to verify it could forge a token for any
        /// player on any server, which is why no shipped client verifies it. An asymmetric
        /// signature has no such problem: the client needs only the public half, and that
        /// arrives separately in <c>EnterWorldResponse.server_public_key</c>.
        /// <para>
        /// The signed input is NOT the transcript. See
        /// <c>Cuvara.Netcode.Crypto.ServerIdentityVerifier.IdentityInput</c>, which is the only
        /// place that layout is written down on this side.
        /// </para>
        /// </remarks>
        public byte[] ServerSignature { get; set; } = Array.Empty<byte>();
    }
}
