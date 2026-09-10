using System;
using System.Text;
using NUnit.Framework;
using Cuvara.Netcode.Crypto;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The client half of ADR-22's transport crypto, pinned against published RFC vectors and
    /// against the SAME cross-implementation vector the Go and C# server sides assert.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why vectors and not round-trips.</b> A round-trip proves an implementation agrees
    /// with itself, which a subtly wrong one also does — silently, and forever. These constants
    /// pin the bytes BETWEEN three implementations: Go's <c>backend/shared/sealed</c>, the C#
    /// server's <c>GameServer.Net.Sealed</c>, and this package. If any two ever disagree, the
    /// production symptom is not an error: it is a handshake that never completes and a session
    /// that never forms, with nothing naming the cause.
    /// </para>
    /// <para>
    /// The frame constant below was produced by the GO implementation. This test opens it and
    /// then reproduces it byte for byte, which is what makes "Go opens what Unity sealed" true
    /// as well — the Go test asserts this exact string.
    /// </para>
    /// <para>
    /// Every value is derived, not chosen: the two private keys are RFC 7748 §6.1's, and
    /// everything else follows from them and the fixed jti.
    /// </para>
    /// </remarks>
    public class SealedCryptoTests
    {
        // --- Cross-implementation vector. Identical constants in shared/sealed/interop_test.go
        //     and GameServer.Tests/Net/SealedInteropTests.cs.
        private const string ClientPrivate = "77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a";
        private const string ServerPrivate = "5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb";
        private const string Jti = "interop-jti-0001";
        private const string JoinSecret = "interop-join-secret";
        private const string Plaintext = "interop payload";
        private const ulong Sequence = 7;

        private const string ClientPublic = "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a";
        private const string ServerPublic = "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f";
        private const string Shared = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742";
        private const string KeyC2S = "9b5cb56cd4dcc26b2cd3a89c34a79feddeac1bee943566cf7bc24e80d76d221e";
        private const string KeyS2C = "54e46a0de4f5e86296813a4dc8e62829cde095d88db19195d061c4611623e31b";
        private const string Binding = "f0788e11400bd7a65954cef957c685f3ff1afebb052964e8b0dc9ad7f3bef2bc";
        private const string Frame = "c1010000000000000007f599297035b016c0f6ecef9fe5c4d1879637da13c7c39676f6f435aed71bd0";

        // Convert.ToHexString / FromHexString are .NET 5+. Unity's profile is netstandard 2.1.
        private static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        private static byte[] Unhex(string s)
        {
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }

        // ---------------------------------------------------------------- RFC vectors

        /// <summary>RFC 8439 §2.8.2 — the AEAD, against the published answer.</summary>
        [Test]
        public void ChaCha20Poly1305_MatchesRfc8439()
        {
            var aead = new SealedAead(Unhex("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f"));
            byte[] nonce = Unhex("070000004041424344454647");
            byte[] aad = Unhex("50515253c0c1c2c3c4c5c6c7");
            byte[] pt = Encoding.ASCII.GetBytes(
                "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");

            var ct = new byte[pt.Length + SealedFrame.TagSize];
            int written = aead.Seal(nonce, pt, aad, ct);

            Assert.AreEqual(pt.Length + SealedFrame.TagSize, written);
            Assert.AreEqual(
                "d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d63dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b3692ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc3ff4def08e4b7a9de576d26586cec64b61161ae10b594f09e26a7e902ecbd0600691",
                Hex(ct));
        }

        /// <summary>RFC 7748 §6.1 — the curve, in both directions.</summary>
        [Test]
        public void X25519_MatchesRfc7748()
        {
            SealedKeyPair client = SealedKeyPair.FromPrivate(Unhex(ClientPrivate));
            SealedKeyPair server = SealedKeyPair.FromPrivate(Unhex(ServerPrivate));

            Assert.AreEqual(ClientPublic, Hex(client.Public));
            Assert.AreEqual(ServerPublic, Hex(server.Public));

            byte[] a, b;
            Assert.IsTrue(client.TryAgree(server.Public, out a));
            Assert.IsTrue(server.TryAgree(client.Public, out b));
            Assert.AreEqual(Shared, Hex(a));
            Assert.AreEqual(Shared, Hex(b), "both sides must reach the same secret — the property the exchange exists for");
        }

        /// <summary>RFC 5869 A.1 — the KDF, against the published answer.</summary>
        /// <remarks>
        /// A.1's <c>info</c> is <c>f0f1..f9</c>, which is not valid UTF-8, so this vector can
        /// only be asserted through the raw-bytes overload. That is why the overload exists.
        /// </remarks>
        [Test]
        public void Hkdf_MatchesRfc5869()
        {
            byte[] okm = SealedCrypto.Hkdf(
                Unhex("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b"),
                Unhex("000102030405060708090a0b0c"),
                Unhex("f0f1f2f3f4f5f6f7f8f9"),
                42);

            Assert.AreEqual(
                "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865",
                Hex(okm));
        }

        // ------------------------------------------------- Cross-implementation vector

        /// <summary>The whole handshake, value by value, so a divergence names the step.</summary>
        [Test]
        public void Handshake_MatchesTheGoAndServerImplementations()
        {
            SealedKeyPair client = SealedKeyPair.FromPrivate(Unhex(ClientPrivate));
            SealedKeyPair server = SealedKeyPair.FromPrivate(Unhex(ServerPrivate));

            byte[] shared;
            Assert.IsTrue(client.TryAgree(server.Public, out shared));
            Assert.AreEqual(Shared, Hex(shared));

            byte[] transcript = SealedHandshake.Transcript(Jti, client.Public, server.Public);

            byte[] c2s, s2c;
            SealedCrypto.DeriveDirectionKeys(shared, transcript, out c2s, out s2c);
            Assert.AreEqual(KeyC2S, Hex(c2s));
            Assert.AreEqual(KeyS2C, Hex(s2c));
            Assert.AreNotEqual(Hex(c2s), Hex(s2c), "one key for both directions would make the counter nonce repeat");

            var signer = new SealedTranscriptSigner(JoinSecret, Jti);
            Assert.AreEqual(Binding, Hex(signer.Sign(transcript)));
            Assert.IsTrue(signer.Verify(transcript, Unhex(Binding)));
        }

        /// <summary>Unity OPENS what Go sealed — the exact byte string from the Go test.</summary>
        [Test]
        public void Frame_SealedByGo_OpensInUnity()
        {
            var session = new SealedSession(new SealedAead(InteropC2S()), new StrictMonotonicSequence());

            byte[] plaintext;
            SealedOpenResult result = session.Open(Unhex(Frame), out plaintext);

            Assert.AreEqual(SealedOpenResult.Ok, result);
            Assert.AreEqual(Plaintext, Encoding.UTF8.GetString(plaintext));
            Assert.AreEqual(Sequence, session.HighestReceived);
        }

        /// <summary>
        /// Unity SEALS the same inputs and must produce the identical byte string — which is
        /// what makes "Go opens what Unity sealed" true, since the Go test opens exactly this
        /// constant.
        /// </summary>
        [Test]
        public void Frame_SealedByUnity_MatchesGoByteForByte()
        {
            var session = new SealedSession(new SealedAead(InteropC2S()), new StrictMonotonicSequence());

            // Reach sequence 7 by advancing the counter the honest way, not by setting it.
            byte[] filler = Encoding.UTF8.GetBytes("filler");
            for (ulong i = 0; i < Sequence; i++) session.Seal(filler);

            Assert.AreEqual(Frame, Hex(session.Seal(Encoding.UTF8.GetBytes(Plaintext))));
        }

        // ----------------------------------------------------------- Refusal behaviour

        /// <summary>
        /// A frame sealed under the wrong direction's key must not open. The two keys exist
        /// precisely so the counter nonce cannot repeat across directions, and this is what
        /// proves they are distinct in USE rather than merely in derivation.
        /// </summary>
        [Test]
        public void Frame_DoesNotOpenUnderTheOtherDirectionsKey()
        {
            var wrongWay = new SealedSession(new SealedAead(Unhex(KeyS2C)), new StrictMonotonicSequence());

            byte[] ignored;
            Assert.AreEqual(SealedOpenResult.Rejected, wrongWay.Open(Unhex(Frame), out ignored));
        }

        /// <summary>Any edit anywhere in the frame is refused — header included, since the whole header is AAD.</summary>
        [Test]
        public void Frame_RefusesEveryOneBitEdit()
        {
            byte[] original = Unhex(Frame);

            for (int i = 0; i < original.Length; i++)
            {
                var tampered = (byte[])original.Clone();
                tampered[i] ^= 0x01;

                // Byte 0 is the marker; flipping it makes the body stop being a sealed frame at
                // all, which is a different answer with a different caller response.
                SealedOpenResult expected = i == 0 ? SealedOpenResult.NotSealed : SealedOpenResult.Rejected;

                var session = new SealedSession(new SealedAead(InteropC2S()), new StrictMonotonicSequence());
                byte[] ignored;
                Assert.AreEqual(expected, session.Open(tampered, out ignored), "byte {0}", i);
            }
        }

        /// <summary>
        /// A replayed frame is refused, and — the part that is easy to get wrong — a REPLAYED
        /// HEADER WITH GARBAGE AFTER IT must not advance the replay counter.
        /// </summary>
        /// <remarks>
        /// If the sequence were checked before the tag, an attacker could take a captured
        /// header, append noise, and push a peer's counter arbitrarily high without holding any
        /// key — locking out the real sender. That is why <see cref="SealedSession.Open"/> owns
        /// the order rather than documenting it.
        /// </remarks>
        [Test]
        public void Open_DoesNotAdvanceTheReplayCounterOnAForgedHeader()
        {
            var session = new SealedSession(new SealedAead(InteropC2S()), new StrictMonotonicSequence());
            byte[] ignored;

            // A high sequence in the header, garbage body. Must be refused AND leave no trace.
            var forged = new byte[SealedFrame.HeaderSize + 32];
            SealedFrame.WriteHeader(forged, 9999);
            Assert.AreEqual(SealedOpenResult.Rejected, session.Open(forged, out ignored));
            Assert.AreEqual(0UL, session.HighestReceived, "a forged header advanced the counter");

            // The genuine frame at sequence 7 still opens, which it could not have done if the
            // forgery had moved the counter to 9999.
            Assert.AreEqual(SealedOpenResult.Ok, session.Open(Unhex(Frame), out ignored));
            Assert.AreEqual(Sequence, session.HighestReceived);

            // And now the genuine frame is spent.
            Assert.AreEqual(SealedOpenResult.Rejected, session.Open(Unhex(Frame), out ignored));
        }

        /// <summary>A cleartext Envelope is reported as NotSealed, not as a failure.</summary>
        [Test]
        public void Open_ReportsACleartextEnvelopeAsNotSealed()
        {
            var session = new SealedSession(new SealedAead(InteropC2S()), new StrictMonotonicSequence());

            byte[] ignored;
            // 0x08 is a Protobuf Envelope's first byte; 0x7B is JSON's '{'.
            Assert.AreEqual(SealedOpenResult.NotSealed, session.Open(new byte[] { 0x08, 0x01 }, out ignored));
            Assert.AreEqual(SealedOpenResult.NotSealed, session.Open(new byte[] { 0x7B, 0x7D }, out ignored));
        }

        /// <summary>Generated key pairs differ, so the RNG is real rather than a stub.</summary>
        [Test]
        public void Generate_ProducesDistinctKeyPairs()
        {
            string a = Hex(SealedKeyPair.Generate().Public);
            string b = Hex(SealedKeyPair.Generate().Public);

            Assert.AreNotEqual(a, b);
            Assert.AreEqual(SealedCrypto.X25519KeySize * 2, a.Length);
        }

        /// <summary>Two live peers agree, seal and open — the end-to-end shape, with fresh keys.</summary>
        [Test]
        public void LiveExchange_BetweenTwoFreshPeers_RoundTrips()
        {
            SealedKeyPair client = SealedKeyPair.Generate();
            SealedKeyPair server = SealedKeyPair.Generate();
            const string jti = "live-jti";

            byte[] clientSecret, serverSecret;
            Assert.IsTrue(client.TryAgree(server.Public, out clientSecret));
            Assert.IsTrue(server.TryAgree(client.Public, out serverSecret));
            Assert.AreEqual(Hex(clientSecret), Hex(serverSecret));

            byte[] transcript = SealedHandshake.Transcript(jti, client.Public, server.Public);

            byte[] c2s, s2c;
            SealedCrypto.DeriveDirectionKeys(clientSecret, transcript, out c2s, out s2c);

            var sending = new SealedSession(new SealedAead(c2s), new StrictMonotonicSequence());
            var receiving = new SealedSession(new SealedAead(c2s), new StrictMonotonicSequence());

            for (int i = 0; i < 100; i++)
            {
                byte[] payload = Encoding.UTF8.GetBytes("input " + i);
                byte[] plaintext;
                Assert.AreEqual(SealedOpenResult.Ok, receiving.Open(sending.Seal(payload), out plaintext));
                Assert.AreEqual("input " + i, Encoding.UTF8.GetString(plaintext));
            }

            Assert.AreEqual(99UL, receiving.HighestReceived);
        }

        /// <summary>
        /// A peer holding the WRONG join-token secret derives a different binding, so the
        /// handshake it offers does not verify. This is the property that makes the exchange
        /// authenticated rather than merely encrypted.
        /// </summary>
        [Test]
        public void Binding_FromTheWrongJoinSecret_DoesNotVerify()
        {
            SealedKeyPair client = SealedKeyPair.FromPrivate(Unhex(ClientPrivate));
            SealedKeyPair server = SealedKeyPair.FromPrivate(Unhex(ServerPrivate));
            byte[] transcript = SealedHandshake.Transcript(Jti, client.Public, server.Public);

            byte[] forged = new SealedTranscriptSigner("not-the-join-secret", Jti).Sign(transcript);

            Assert.IsFalse(new SealedTranscriptSigner(JoinSecret, Jti).Verify(transcript, forged));
        }

        /// <summary>
        /// A man-in-the-middle who substitutes their own ephemeral key changes the transcript,
        /// so the binding they replayed from the real client no longer verifies.
        /// </summary>
        [Test]
        public void Binding_DoesNotSurviveASubstitutedEphemeralKey()
        {
            SealedKeyPair client = SealedKeyPair.FromPrivate(Unhex(ClientPrivate));
            SealedKeyPair server = SealedKeyPair.FromPrivate(Unhex(ServerPrivate));
            var signer = new SealedTranscriptSigner(JoinSecret, Jti);

            byte[] genuine = signer.Sign(SealedHandshake.Transcript(Jti, client.Public, server.Public));

            SealedKeyPair attacker = SealedKeyPair.Generate();
            byte[] substituted = SealedHandshake.Transcript(Jti, attacker.Public, server.Public);

            Assert.IsFalse(signer.Verify(substituted, genuine));
        }

        /// <summary>
        /// The transcript is pinned byte for byte, because it is the salt every derived key
        /// depends on and the input the binding authenticates.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What this replaced, and why.</b> An earlier version of this test asserted that
        /// two different jti values produce different transcripts, and called that proof the
        /// NUL separators close a split-ambiguity attack. It is not. With the current layout
        /// the label is a compile-time constant and both public keys are fixed at 32 bytes, so
        /// the jti is the ONLY variable-length field — there is no second field for it to steal
        /// bytes from, and dropping the separators changes the bytes but creates no collision.
        /// That test passed with the separators removed, which is exactly the kind of assertion
        /// that reads like coverage and provides none.
        /// </para>
        /// <para>
        /// The separators are still correct and stay: they are what keeps the property true the
        /// day a variable-length field is added next to the jti. This test pins the actual
        /// layout instead, which the Go and C# server sides construct independently.
        /// </para>
        /// </remarks>
        [Test]
        public void Transcript_IsPinnedByteForByte()
        {
            SealedKeyPair client = SealedKeyPair.FromPrivate(Unhex(ClientPrivate));
            SealedKeyPair server = SealedKeyPair.FromPrivate(Unhex(ServerPrivate));

            byte[] transcript = SealedHandshake.Transcript(Jti, client.Public, server.Public);

            Assert.AreEqual(
                "6375766172612f7365616c65642d68616e647368616b652f7631" + // "cuvara/sealed-handshake/v1"
                "00" +                                                   // NUL
                "696e7465726f702d6a74692d30303031" +                     // "interop-jti-0001"
                "00" +                                                   // NUL
                ClientPublic +
                ServerPublic,
                Hex(transcript));
            Assert.AreEqual(108, transcript.Length);
        }

        /// <summary>A jti of the wrong length must not be able to imitate another exchange.</summary>
        [Test]
        public void Transcript_VariesWithEveryInput()
        {
            SealedKeyPair client = SealedKeyPair.FromPrivate(Unhex(ClientPrivate));
            SealedKeyPair server = SealedKeyPair.FromPrivate(Unhex(ServerPrivate));

            string a = Hex(SealedHandshake.Transcript(Jti, client.Public, server.Public));
            Assert.AreNotEqual(a, Hex(SealedHandshake.Transcript("other-jti", client.Public, server.Public)));
            Assert.AreNotEqual(a, Hex(SealedHandshake.Transcript(Jti, server.Public, client.Public)),
                "the two publics are not interchangeable — direction is part of what is authenticated");
        }

        private static byte[] InteropC2S()
        {
            return Unhex(KeyC2S);
        }
    }

    /// <summary>The replay validators, independent of any cipher.</summary>
    public class SequenceValidatorTests
    {
        [Test]
        public void Strict_AcceptsOnlyIncreasing()
        {
            var v = new StrictMonotonicSequence();

            Assert.IsTrue(v.Accept(0), "the first sequence a session sends is 0, and must be accepted");
            Assert.IsFalse(v.Accept(0));
            Assert.IsTrue(v.Accept(1));
            Assert.IsFalse(v.Accept(1));
            Assert.IsTrue(v.Accept(1000), "a forward jump is legal — a strict counter bounds replay, not loss");
            Assert.IsFalse(v.Accept(999));
            Assert.AreEqual(1000UL, v.Highest);
        }

        [Test]
        public void Window_AcceptsOutOfOrderOnceEach()
        {
            var v = new SlidingWindowSequence();

            Assert.IsTrue(v.Accept(10));
            Assert.IsTrue(v.Accept(12));
            Assert.IsTrue(v.Accept(11), "in-window reordering is what this validator exists for");
            Assert.IsFalse(v.Accept(11));
            Assert.IsFalse(v.Accept(12));
        }

        [Test]
        public void Window_RefusesAnythingOlderThanItCanJudge()
        {
            var v = new SlidingWindowSequence(8);

            Assert.IsTrue(v.Accept(100));
            Assert.IsFalse(v.Accept(92), "8 behind is outside a width-8 window");
            Assert.IsFalse(v.Accept(1), "a validator that cannot prove freshness must not claim it");
            Assert.IsTrue(v.Accept(99));
        }

        [Test]
        public void Window_ClearsWhenItAdvancesPastItsOwnWidth()
        {
            var v = new SlidingWindowSequence(8);

            Assert.IsTrue(v.Accept(1));
            Assert.IsTrue(v.Accept(100));
            Assert.IsFalse(v.Accept(1), "already seen, and now far outside the window");
            Assert.IsTrue(v.Accept(99));
        }
    }
}
