using System;
using NUnit.Framework;
using Cuvara.Netcode.Crypto;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The client half of ADR-25: verifying the game server's Ed25519 signature, and
    /// reporting honestly what a verified signature is worth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The interop vector below is the SAME one pinned in the server repo
    /// (<c>shared/sealed/interop_test.go</c>, <c>ServerIdentityInteropTests.cs</c>). It is
    /// copied rather than generated, on purpose: if anyone changes the signed input's layout
    /// on one side, these numbers stop reproducing and the two implementations stop agreeing
    /// in the same test run rather than in production.
    /// </para>
    /// <para>
    /// The assertions that matter are the REFUSALS. A verifier that accepts everything passes
    /// a happy-path test, and so does a correct one.
    /// </para>
    /// </remarks>
    public class ServerIdentityVerifierTests
    {
        // From shared/sealed/interop_test.go -- do not regenerate these locally.
        private const string IdentityPublicHex =
            "79b5562e8fe654f94078b112e8a98ba7901f853ae695bed7e0e3910bad049664";
        private const string IdentityInputHex =
            "6375766172612f7365616c65642d6964656e746974792f7631006375766172612f7365616c6564" +
            "2d68616e647368616b652f763100696e7465726f702d6a74692d30303031008520f0098930a754" +
            "748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6ade9edb7d7b7dc1b4d35b61c2ece435" +
            "373f8343c85b78674dadfc7e146f882b4f0079b5562e8fe654f94078b112e8a98ba7901f853ae6" +
            "95bed7e0e3910bad049664";
        private const string IdentitySignatureHex =
            "73b55ddc68679d0694bb1602c7422a6a98941354a82a37c84fa8fe3181c7467d" +
            "abb9590f380ce33dfe298daa33ea5c9421cead03a6764bb34b37c85c67d37a0e";

        private static byte[] Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        /// <summary>
        /// The signed input the SERVER built, recovered from the vector. The transcript is the
        /// slice between the two 0x00 separators; rebuilding it here rather than hard-coding
        /// it keeps the layout under test instead of assumed.
        /// </summary>
        private static byte[] TranscriptFromVector()
        {
            byte[] input = Hex(IdentityInputHex);
            byte[] label = System.Text.Encoding.UTF8.GetBytes(ServerIdentityVerifier.IdentityLabel);
            int start = label.Length + 1;
            int end = input.Length - 1 - ServerIdentityVerifier.IdentityKeySize;
            var transcript = new byte[end - start];
            Array.Copy(input, start, transcript, 0, transcript.Length);
            return transcript;
        }

        [Test]
        public void TheSignedInputMatchesTheServersByteForByte()
        {
            byte[] built = ServerIdentityVerifier.IdentityInput(
                TranscriptFromVector(), Hex(IdentityPublicHex));

            CollectionAssert.AreEqual(Hex(IdentityInputHex), built,
                "the client and server must sign the SAME bytes; a layout change on one side " +
                "makes every signature fail while both believe they are correct");
        }

        [Test]
        public void TheInteropSignatureVerifies()
        {
            Assert.IsTrue(ServerIdentityVerifier.Verify(
                TranscriptFromVector(), Hex(IdentityPublicHex), Hex(IdentitySignatureHex)));
        }

        // ---- the refusals -----------------------------------------------------------

        [Test]
        public void ATamperedTranscriptIsRefused()
        {
            byte[] transcript = TranscriptFromVector();
            transcript[0] ^= 0x01;

            Assert.IsFalse(ServerIdentityVerifier.Verify(
                transcript, Hex(IdentityPublicHex), Hex(IdentitySignatureHex)));
        }

        [Test]
        public void AFlippedSignatureByteIsRefused()
        {
            byte[] signature = Hex(IdentitySignatureHex);
            signature[63] ^= 0x01;

            Assert.IsFalse(ServerIdentityVerifier.Verify(
                TranscriptFromVector(), Hex(IdentityPublicHex), signature));
        }

        /// <summary>
        /// The attack the signed input's layout exists to stop: a genuine signature replayed
        /// under a substituted key. The server signs its OWN key into the message, so the
        /// swap changes what was signed.
        /// </summary>
        [Test]
        public void AGenuineSignatureUnderASubstitutedKeyIsRefused()
        {
            byte[] otherKey = Hex(IdentityPublicHex);
            otherKey[0] ^= 0x01;

            Assert.IsFalse(ServerIdentityVerifier.Verify(
                TranscriptFromVector(), otherKey, Hex(IdentitySignatureHex)));
        }

        [Test]
        public void AnAllZeroSignatureIsRefused()
        {
            Assert.IsFalse(ServerIdentityVerifier.Verify(
                TranscriptFromVector(), Hex(IdentityPublicHex), new byte[64]));
        }

        [Test]
        public void WrongSizedInputsAreRefusedRatherThanThrowing()
        {
            byte[] transcript = TranscriptFromVector();

            Assert.IsFalse(ServerIdentityVerifier.Verify(transcript, new byte[31], new byte[64]),
                "a 31-byte key must be refused");
            Assert.IsFalse(ServerIdentityVerifier.Verify(transcript, new byte[32], new byte[63]),
                "a 63-byte signature must be refused");
            Assert.IsFalse(ServerIdentityVerifier.Verify(Array.Empty<byte>(), new byte[32], new byte[64]),
                "an empty transcript must be refused");
        }

        // ---- the honest distinction --------------------------------------------------

        [Test]
        public void OverAnUnauthenticatedHop_TheSignatureChecksButIdentityIsNotVerified()
        {
            var result = ServerIdentityVerifier.Evaluate(
                TranscriptFromVector(), Hex(IdentityPublicHex), Hex(IdentitySignatureHex),
                keyHopAuthenticated: false, required: false);

            Assert.IsTrue(result.Offered);
            Assert.IsTrue(result.Checked, "the signature is genuine and must be reported as checked");
            Assert.IsFalse(result.Verified,
                "a key delivered over plaintext proves nothing: an active attacker substitutes " +
                "both the key and the signature and this check passes against their key");
            Assert.IsFalse(result.Refused, "the session still proceeds; only the claim is weaker");
        }

        [Test]
        public void OverAnAuthenticatedHop_IdentityIsVerified()
        {
            var result = ServerIdentityVerifier.Evaluate(
                TranscriptFromVector(), Hex(IdentityPublicHex), Hex(IdentitySignatureHex),
                keyHopAuthenticated: true, required: false);

            Assert.IsTrue(result.Verified,
                "with the key over an authenticated hop this is the strong claim -- and it must be " +
                "REACHABLE, or it is the always-false boolean ADR-25 rejected");
        }

        [Test]
        public void AForgedSignatureRefusesTheHandshakeEvenOverAnAuthenticatedHop()
        {
            byte[] signature = Hex(IdentitySignatureHex);
            signature[0] ^= 0x01;

            var result = ServerIdentityVerifier.Evaluate(
                TranscriptFromVector(), Hex(IdentityPublicHex), signature,
                keyHopAuthenticated: true, required: false);

            Assert.IsTrue(result.Refused);
            Assert.IsFalse(result.Verified);
        }

        [Test]
        public void NoKeyAtAll_ConnectsAsBefore_UnlessRequired()
        {
            byte[] transcript = TranscriptFromVector();

            var tolerant = ServerIdentityVerifier.Evaluate(
                transcript, Array.Empty<byte>(), Array.Empty<byte>(),
                keyHopAuthenticated: false, required: false);
            Assert.IsFalse(tolerant.Offered);
            Assert.IsFalse(tolerant.Refused, "a pre-ADR-25 gateway must keep working -- migration order");

            var strict = ServerIdentityVerifier.Evaluate(
                transcript, Array.Empty<byte>(), Array.Empty<byte>(),
                keyHopAuthenticated: false, required: true);
            Assert.IsTrue(strict.Refused, "with identity required, no key is a refusal");
            Assert.IsFalse(
                strict.Offered,
                "a refusal CAUSED by no key must not report a key as offered -- that is the "
                + "field a deployment audit reads to answer 'did ADR-25 ship here?'");
        }

        [Test]
        public void AKeyWithNoSignatureIsNeverReportedAsChecked()
        {
            var result = ServerIdentityVerifier.Evaluate(
                TranscriptFromVector(), Hex(IdentityPublicHex), Array.Empty<byte>(),
                keyHopAuthenticated: true, required: false);

            Assert.IsTrue(result.Offered);
            Assert.IsFalse(result.Checked);
            Assert.IsFalse(result.Verified);
        }
    }
}
