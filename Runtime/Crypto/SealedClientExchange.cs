using System;

namespace Cuvara.Netcode.Crypto
{
    /// <summary>How the client half of the sealed handshake ended.</summary>
    public enum SealedExchangeResult
    {
        /// <summary>Completed. Both sessions are installed on the result.</summary>
        Ok = 0,

        /// <summary>The server's public key was the wrong length, or a low-order point.</summary>
        BadServerKey,

        /// <summary>
        /// The server's binding did not verify. A man in the middle who substitutes its own
        /// ephemeral key produces exactly this, so it must close the connection rather than
        /// continue unsealed.
        /// </summary>
        BindingRejected,

        /// <summary>The server answered the hello with an error instead of a key.</summary>
        ServerRefused,

        /// <summary>
        /// The exchange was asked to verify a binding but given no material to verify it
        /// with. A configuration fault, and still a refusal.
        /// </summary>
        NotConfigured,
    }

    /// <summary>
    /// The client half of ADR-22's sealed-session handshake, with no transport in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this has no sockets and no Protobuf.</b> The exchange is two messages and a key
    /// schedule; the part that goes wrong is the key schedule, and the part that is expensive
    /// to test is the transport. Splitting them means the schedule is covered by ordinary
    /// tests against the cross-implementation vector, and the transport adapter that will sit
    /// on top of this has nothing left in it but reading and writing two Envelopes.
    /// </para>
    /// <para>
    /// Mirrors <c>GameServer.Net.Sealed.SealedHandshakeServer</c>. Both hellos travel in the
    /// clear — there is no key yet, which is what they exist to establish.
    /// </para>
    /// <para>
    /// <b>Gameplay hop only.</b> The anchor is the join token's <c>jti</c>. The gateway hop
    /// has no jti at the point it would need one and requires a different anchor, which is
    /// still ADR-gated.
    /// </para>
    /// </remarks>
    public sealed class SealedClientExchange
    {
        private readonly string _jti;
        private readonly SealedTranscriptSigner _verifier;
        private readonly bool _bindingVerifiable;
        private SealedKeyPair _ephemeral;

        /// <summary>
        /// Start an exchange that WILL verify the server's binding.
        /// </summary>
        /// <param name="jti">The join token's <c>jti</c> claim. Salts the key schedule.</param>
        /// <param name="joinTokenSecret">
        /// Material the binding key is derived from. A production Unity client does not hold
        /// this — see <see cref="WithoutBindingVerification"/> and read what it costs before
        /// using it.
        /// </param>
        public SealedClientExchange(string jti, string joinTokenSecret)
        {
            if (string.IsNullOrEmpty(jti)) throw new ArgumentException("no jti", nameof(jti));
            if (string.IsNullOrEmpty(joinTokenSecret))
                throw new ArgumentException("no join-token secret", nameof(joinTokenSecret));

            _jti = jti;
            _verifier = new SealedTranscriptSigner(joinTokenSecret, jti);
            _bindingVerifiable = true;
        }

        private SealedClientExchange(string jti)
        {
            if (string.IsNullOrEmpty(jti)) throw new ArgumentException("no jti", nameof(jti));
            _jti = jti;
            _verifier = null;
            _bindingVerifiable = false;
        }

        /// <summary>
        /// Start an exchange that CANNOT verify the server's binding, and says so.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What this gives up, stated rather than implied.</b> The binding is the only
        /// thing that distinguishes the real server from a man in the middle. Without it the
        /// exchange still produces a working encrypted session — with whoever answered. An
        /// attacker on the path substitutes their own ephemeral key, terminates the session,
        /// and both ends report a healthy sealed connection. Encryption without
        /// authentication buys confidentiality from a passive listener and nothing at all
        /// from an active one.
        /// </para>
        /// <para>
        /// <b>Why it exists at all.</b> A production Unity client cannot hold
        /// <c>JOIN_TOKEN_SECRET</c> — shipping it in a binary is the same mistake as the
        /// pre-shared transport key ADR-22 supersedes. Until the pinned gateway identity key
        /// ADR-22 specifies is delivered, a real client has no material to verify with. This
        /// factory makes that state <b>named and visible on the result</b>
        /// (<see cref="SealedExchange.BindingVerified"/>) rather than reached by passing an
        /// empty string and getting a silent pass.
        /// </para>
        /// <para>
        /// Do not use it to make a failing test pass.
        /// </para>
        /// </remarks>
        public static SealedClientExchange WithoutBindingVerification(string jti)
        {
            return new SealedClientExchange(jti);
        }

        /// <summary>
        /// Generate this connection's ephemeral key pair and return the public half to send
        /// as <c>SealedClientHello.public_key</c>.
        /// </summary>
        /// <remarks>
        /// Ephemeral per connection. Reusing one across sessions forfeits the forward secrecy
        /// that is the entire reason ADR-22 supersedes the derived-key scheme: with a fresh
        /// pair per connection, a long-lived secret obtained later cannot decrypt traffic
        /// recorded earlier. Calling this twice on one exchange replaces the pair, which
        /// would strand any hello already in flight, so it throws instead.
        /// </remarks>
        public byte[] CreateHello()
        {
            if (_ephemeral != null)
                throw new InvalidOperationException(
                    "hello already created; build a new exchange per connection");

            _ephemeral = SealedKeyPair.Generate();
            return _ephemeral.Public;
        }

        /// <summary>
        /// Consume <c>SealedServerHello</c> and, on success, produce the two sessions.
        /// </summary>
        /// <param name="serverPublic">The server's ephemeral public key, 32 bytes.</param>
        /// <param name="binding">
        /// The server's binding over the transcript, 32 bytes. Ignored — and required to be
        /// absent-tolerant — only when this exchange was built by
        /// <see cref="WithoutBindingVerification"/>.
        /// </param>
        /// <param name="serverError">
        /// <c>SealedServerHello.error</c>, if the server set one. Non-empty means refusal.
        /// </param>
        /// <remarks>
        /// <b>There is no cleartext fallback on any path.</b> Every failure returns without
        /// sessions and the caller must close the connection. A protocol that can be talked
        /// down to cleartext will be.
        /// </remarks>
        public SealedExchange AcceptServerHello(
            ReadOnlySpan<byte> serverPublic, ReadOnlySpan<byte> binding, string serverError)
        {
            if (_ephemeral == null)
                throw new InvalidOperationException("CreateHello must be called first");

            if (!string.IsNullOrEmpty(serverError))
                return SealedExchange.Failed(SealedExchangeResult.ServerRefused, serverError);

            if (serverPublic.Length != SealedHandshake.PublicKeySize)
                return SealedExchange.Failed(SealedExchangeResult.BadServerKey,
                    "server public key was " + serverPublic.Length + " bytes, want " + SealedHandshake.PublicKeySize);

            byte[] shared;
            if (!_ephemeral.TryAgree(serverPublic, out shared))
            {
                // Includes low-order points, which would force a shared secret the attacker
                // knows and both sides agree on — a complete break wearing the appearance of
                // a successful handshake.
                return SealedExchange.Failed(SealedExchangeResult.BadServerKey,
                    "X25519 agreement refused the server's key");
            }

            byte[] transcript = SealedHandshake.Transcript(_jti, _ephemeral.Public, serverPublic);

            if (_bindingVerifiable)
            {
                if (!_verifier.Verify(transcript, binding))
                    return SealedExchange.Failed(SealedExchangeResult.BindingRejected,
                        "the server's binding did not verify over this transcript");
            }

            byte[] c2s;
            byte[] s2c;
            SealedCrypto.DeriveDirectionKeys(shared, transcript, out c2s, out s2c);

            // Outbound is client-to-server, inbound is server-to-client. Swapping these
            // produces two sessions that each work against themselves and neither of which
            // can talk to the server — which is why the interop vector pins both keys.
            return SealedExchange.Succeeded(
                outbound: new SealedSession(new SealedAead(c2s), new StrictMonotonicSequence()),
                inbound: new SealedSession(new SealedAead(s2c), new StrictMonotonicSequence()),
                bindingVerified: _bindingVerifiable,
                transcript: transcript);
        }
    }

    /// <summary>The outcome of <see cref="SealedClientExchange.AcceptServerHello"/>.</summary>
    public sealed class SealedExchange
    {
        private SealedExchange() { }

        /// <summary>How it ended.</summary>
        public SealedExchangeResult Result { get; private set; }

        /// <summary>Seals frames the client sends. Null unless <see cref="Result"/> is Ok.</summary>
        public SealedSession Outbound { get; private set; }

        /// <summary>Opens frames the server sent. Null unless <see cref="Result"/> is Ok.</summary>
        public SealedSession Inbound { get; private set; }

        /// <summary>
        /// Whether the server's identity was actually PROVED, rather than merely encrypted to.
        /// </summary>
        /// <remarks>
        /// False means the session is confidential against a passive listener and offers
        /// nothing against an active one. It is a distinct property from <see cref="Result"/>
        /// being Ok, and it is surfaced separately so that a caller reporting "connected
        /// securely" has to have looked at it.
        /// </remarks>
        public bool BindingVerified { get; private set; }

        /// <summary>The bytes both peers authenticated. Diagnostics; never logged whole.</summary>
        public byte[] Transcript { get; private set; }

        /// <summary>Why it failed, for the local log. Never sent to the peer.</summary>
        public string Error { get; private set; }

        internal static SealedExchange Succeeded(
            SealedSession outbound, SealedSession inbound, bool bindingVerified, byte[] transcript)
        {
            return new SealedExchange
            {
                Result = SealedExchangeResult.Ok,
                Outbound = outbound,
                Inbound = inbound,
                BindingVerified = bindingVerified,
                Transcript = transcript,
                Error = string.Empty,
            };
        }

        internal static SealedExchange Failed(SealedExchangeResult result, string error)
        {
            return new SealedExchange
            {
                Result = result,
                BindingVerified = false,
                Transcript = Array.Empty<byte>(),
                Error = error,
            };
        }
    }
}
