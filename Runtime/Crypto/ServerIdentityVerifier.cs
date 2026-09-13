using System;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Cuvara.Netcode.Crypto
{
    /// <summary>
    /// Verifies the game server's Ed25519 signature over a sealed handshake transcript
    /// (ADR-25), and reports what that signature is actually worth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not the ADR-22 binding and does not repair it.</b> The binding is a
    /// symmetric HMAC under <c>JOIN_TOKEN_SECRET</c> — the key the gateway mints join tokens
    /// with — so a client able to verify it is a client able to forge a token for any player
    /// on any server. <c>BindingVerified</c> is therefore permanently false for every shipped
    /// build and is not improved by anything here. The signature sits BESIDE it.
    /// </para>
    /// <para>
    /// <b>A verified signature is worth exactly as much as the hop the key arrived over.</b>
    /// The identity key reaches a client through <c>enter_world_resp</c> on the gateway hop.
    /// Over plaintext, an active attacker substitutes both the key and the signature and the
    /// check passes against the attacker's own key — which is why
    /// <see cref="ServerIdentityResult.Verified"/> is the CONJUNCTION of "the signature
    /// checked out" and "the key came over an authenticated hop", and why the second half is
    /// something the caller asserts rather than something this class can discover.
    /// </para>
    /// <para>
    /// The same three-field shape as the Go client (<c>shared/sealed.ClientResult</c>), so a
    /// report from either says the same thing in the same words.
    /// </para>
    /// </remarks>
    public static class ServerIdentityVerifier
    {
        /// <summary>Bytes of an Ed25519 public key.</summary>
        public const int IdentityKeySize = 32;

        /// <summary>Bytes of an Ed25519 signature.</summary>
        public const int IdentitySignatureSize = 64;

        /// <summary>
        /// Domain separation label, byte-identical to the server's
        /// <c>SealedHandshake.IdentityLabel</c> and Go's <c>sealed.IdentityLabel</c>.
        /// Changing it on one side only makes every signature fail to verify while both
        /// implementations believe they are correct.
        /// </summary>
        public const string IdentityLabel = "cuvara/sealed-identity/v1";

        /// <summary>
        /// The exact bytes the server signs: <c>label || 0x00 || transcript || 0x00 ||
        /// identityPublic</c>.
        /// </summary>
        /// <remarks>
        /// The identity key is inside the signed input, not merely alongside it. That is what
        /// stops a genuine signature being replayed under a different key: an attacker who
        /// substitutes their own key in <c>enter_world_resp</c> cannot reuse the real
        /// server's signature, because the real server signed its OWN key into the message.
        /// </remarks>
        public static byte[] IdentityInput(ReadOnlySpan<byte> transcript, ReadOnlySpan<byte> identityPublic)
        {
            if (transcript.Length == 0)
                throw new ArgumentException("empty transcript", nameof(transcript));
            if (identityPublic.Length != IdentityKeySize)
                throw new ArgumentException(
                    "identity key must be " + IdentityKeySize + " bytes", nameof(identityPublic));

            byte[] label = Encoding.UTF8.GetBytes(IdentityLabel);

            var output = new byte[label.Length + 1 + transcript.Length + 1 + IdentityKeySize];
            int at = 0;
            label.CopyTo(output, at); at += label.Length;
            output[at++] = 0x00;
            transcript.CopyTo(output.AsSpan(at)); at += transcript.Length;
            output[at++] = 0x00;
            identityPublic.CopyTo(output.AsSpan(at));
            return output;
        }

        /// <summary>
        /// Verifies <paramref name="signature"/> over <paramref name="transcript"/> under
        /// <paramref name="identityPublic"/>.
        /// </summary>
        /// <remarks>
        /// Returns false rather than throwing for every wrong-size or malformed input: these
        /// values come off the wire, and an exception on attacker-chosen bytes is a denial of
        /// service with extra steps. A caller that wants to know WHY reads the result's
        /// <see cref="ServerIdentityResult.Error"/>.
        /// </remarks>
        public static bool Verify(
            ReadOnlySpan<byte> transcript, ReadOnlySpan<byte> identityPublic, ReadOnlySpan<byte> signature)
        {
            if (identityPublic.Length != IdentityKeySize) return false;
            if (signature.Length != IdentitySignatureSize) return false;
            if (transcript.Length == 0) return false;

            try
            {
                byte[] input = IdentityInput(transcript, identityPublic);
                var verifier = new Ed25519Signer();
                verifier.Init(false, new Ed25519PublicKeyParameters(identityPublic.ToArray(), 0));
                verifier.BlockUpdate(input, 0, input.Length);
                return verifier.VerifySignature(signature.ToArray());
            }
            catch (Exception)
            {
                // BouncyCastle rejects a malformed point by throwing. A thrown key is a
                // refused key, not a crash.
                return false;
            }
        }

        /// <summary>
        /// Decides what a handshake learned about the server's identity.
        /// </summary>
        /// <param name="identityPublic">
        /// The key from <c>enter_world_resp</c>, or empty when the gateway sent none — which
        /// is what a pre-ADR-25 deployment looks like and must keep working.
        /// </param>
        /// <param name="keyHopAuthenticated">
        /// Whether the hop that delivered the key was authenticated. TLS on the gateway
        /// connection WITH a validated or pinned certificate — not merely a TLS flag being
        /// set, and never true on a plaintext hop.
        /// </param>
        /// <param name="required">
        /// When true, a server that sends no signature is refused rather than accepted as
        /// pre-ADR-25. Off by default so the deployment order is safe.
        /// </param>
        public static ServerIdentityResult Evaluate(
            ReadOnlySpan<byte> transcript,
            ReadOnlySpan<byte> identityPublic,
            ReadOnlySpan<byte> signature,
            bool keyHopAuthenticated,
            bool required)
        {
            bool haveKey = identityPublic.Length == IdentityKeySize;
            bool haveSignature = signature.Length == IdentitySignatureSize;

            if (!haveKey)
            {
                if (required)
                    return ServerIdentityResult.RefuseUnoffered(
                        "identity required but the gateway sent no server key");
                return ServerIdentityResult.NotOffered();
            }

            if (!haveSignature)
            {
                // A key without a signature is a server that either predates this or is
                // being impersonated by something that could copy a key but not sign. Both
                // are refusals when identity is required; otherwise it is reported, never
                // silently treated as verified.
                if (required)
                    return ServerIdentityResult.Refuse("the server sent an identity key but no signature");
                return ServerIdentityResult.SignatureMissing("the server sent an identity key but no signature");
            }

            if (!Verify(transcript, identityPublic, signature))
                return ServerIdentityResult.Refuse("the server's identity signature did not verify");

            return ServerIdentityResult.VerifiedSignature(keyHopAuthenticated);
        }
    }

    /// <summary>What a handshake learned about the server's identity (ADR-25).</summary>
    public readonly struct ServerIdentityResult
    {
        private ServerIdentityResult(bool offered, bool checkedOk, bool hopAuthenticated, bool refused, string error)
        {
            Offered = offered;
            Checked = checkedOk;
            KeyHopAuthenticated = hopAuthenticated;
            Refused = refused;
            Error = error ?? string.Empty;
        }

        /// <summary>The gateway gave us a server identity key at all.</summary>
        public bool Offered { get; }

        /// <summary>A signature verified under the key we were given.</summary>
        public bool Checked { get; }

        /// <summary>That key arrived over an authenticated hop.</summary>
        public bool KeyHopAuthenticated { get; }

        /// <summary>The handshake must not proceed.</summary>
        public bool Refused { get; }

        /// <summary>Local diagnostic. Never sent to the peer.</summary>
        public string Error { get; }

        /// <summary>
        /// The server's identity is PROVED: the signature checked out AND the key arrived
        /// over an authenticated hop.
        /// </summary>
        /// <remarks>
        /// The conjunction lives here, computed once, so no caller can re-derive it and get
        /// it wrong. <b>False with a successful handshake is the expected state today</b>,
        /// because the gateway hop is plaintext on deployments that have not enabled ADR-23's
        /// TLS — which is most of them.
        /// </remarks>
        public bool Verified { get { return Checked && KeyHopAuthenticated; } }

        internal static ServerIdentityResult NotOffered() =>
            new ServerIdentityResult(false, false, false, false, string.Empty);

        internal static ServerIdentityResult SignatureMissing(string why) =>
            new ServerIdentityResult(true, false, false, false, why);

        internal static ServerIdentityResult VerifiedSignature(bool hopAuthenticated) =>
            new ServerIdentityResult(true, true, hopAuthenticated, false, string.Empty);

        internal static ServerIdentityResult Refuse(string why) =>
            new ServerIdentityResult(true, false, false, true, why);

        /// <summary>
        /// Refused because nothing was offered. <see cref="Offered"/> stays false: a reader
        /// asking "did this deployment ship ADR-25?" must not be told yes by a refusal whose
        /// whole cause is that it did not.
        /// </summary>
        internal static ServerIdentityResult RefuseUnoffered(string why) =>
            new ServerIdentityResult(false, false, false, true, why);

        /// <inheritdoc/>
        public override string ToString()
        {
            if (Refused) return "identity REFUSED: " + Error;
            if (!Offered) return "identity not offered by the gateway";
            if (!Checked) return "identity unchecked: " + Error;
            return Verified
                ? "identity VERIFIED (key arrived over an authenticated hop)"
                : "identity signature checked, but the key arrived over an UNAUTHENTICATED hop "
                  + "-- confidential against a passive eavesdropper, nothing against an active one";
        }
    }
}
