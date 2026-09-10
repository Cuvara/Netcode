using System;
using System.Text;

namespace Cuvara.Netcode.Crypto
{
    /// <summary>
    /// Shape of the authenticated X25519 exchange, and the transcript both peers sign.
    /// Mirrors the server's <c>SealedHandshake</c> and <c>backend/shared/sealed</c>.
    /// </summary>
    public static class SealedHandshake
    {
        /// <summary>Length of an X25519 public key.</summary>
        public const int PublicKeySize = 32;

        /// <summary>Length of the handshake binding tag (HMAC-SHA256).</summary>
        public const int BindingSize = 32;

        /// <summary>
        /// Domain-separation label mixed into every transcript. Part of the wire contract,
        /// byte for byte: two peers that disagree derive different bindings and the handshake
        /// fails with no indication of why.
        /// </summary>
        public const string TranscriptLabel = "cuvara/sealed-handshake/v1";

        /// <summary>
        /// Build the bytes both peers authenticate:
        /// <c>label || 0x00 || jti || 0x00 || clientPublic(32) || serverPublic(32)</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The NUL separators, stated exactly.</b> Concatenating variable-length fields
        /// without delimiters is a real attack: the pieces can be re-split, so two different
        /// (jti, key) pairs produce identical bytes and a MAC over them authenticates both
        /// readings equally. <i>In this specific layout that attack is not reachable</i> — the
        /// label is a constant and both publics are fixed at 32 bytes, leaving the jti as the
        /// only variable-length field, with nothing adjacent to steal bytes from. The
        /// separators are here so the property survives the day a variable-length field is
        /// added next to it, and because two bytes is not a price worth arguing about. Do not
        /// remove them on the grounds that no current test fails: they are cheap insurance
        /// against a future edit, not a fix for a live hole.
        /// </para>
        /// <para>
        /// <b>Why the binding is over this and not over the join token.</b> The token is
        /// readable by anyone on the wire — its claims are base64, not encrypted — so
        /// "present the token" proves nothing: an attacker replays what they read. What an
        /// attacker cannot do is compute a MAC keyed by material derived from
        /// <c>JOIN_TOKEN_SECRET</c>. Binding that MAC to the two EPHEMERAL public keys is what
        /// defeats the man-in-the-middle: an attacker who substitutes their own key changes
        /// the transcript, so a replayed binding no longer verifies.
        /// </para>
        /// </remarks>
        public static byte[] Transcript(string jti, ReadOnlySpan<byte> clientPublic, ReadOnlySpan<byte> serverPublic)
        {
            if (string.IsNullOrEmpty(jti))
                throw new ArgumentException("handshake needs the join token's jti", nameof(jti));
            if (clientPublic.Length != PublicKeySize)
                throw new ArgumentException($"public key must be {PublicKeySize} bytes", nameof(clientPublic));
            if (serverPublic.Length != PublicKeySize)
                throw new ArgumentException($"public key must be {PublicKeySize} bytes", nameof(serverPublic));

            byte[] label = Encoding.UTF8.GetBytes(TranscriptLabel);
            byte[] id = Encoding.UTF8.GetBytes(jti);

            var output = new byte[label.Length + 1 + id.Length + 1 + (2 * PublicKeySize)];
            int at = 0;
            label.CopyTo(output, at); at += label.Length;
            output[at++] = 0x00;
            id.CopyTo(output, at); at += id.Length;
            output[at++] = 0x00;
            clientPublic.CopyTo(output.AsSpan(at)); at += PublicKeySize;
            serverPublic.CopyTo(output.AsSpan(at));

            return output;
        }
    }
}
