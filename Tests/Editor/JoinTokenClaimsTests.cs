using NUnit.Framework;
using Cuvara.Netcode.Auth;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Reading the <c>jti</c> out of a join token, against a token the REAL backend signer
    /// produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fixture below was minted by <c>backend/shared/jwt.SignWithServer</c>, not written
    /// by hand. A hand-written JWT tests the parser against the author's idea of the format;
    /// this one catches the things that actually differ — Go's <c>RawURLEncoding</c> emits no
    /// padding, so the claims segment is padded back here, and getting that wrong is a silent
    /// <c>FormatException</c> rather than a wrong answer.
    /// </para>
    /// <para>
    /// It is expired, which does not matter: nothing here verifies anything, and that is the
    /// point of the class under test.
    /// </para>
    /// </remarks>
    public class JoinTokenClaimsTests
    {
        // Minted 2026-09-10 by jwt.SignWithServer("probe-player", "map_01", ..., 30s).
        private const string RealJoinToken =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9." +
            "eyJzdWIiOiJwcm9iZS1wbGF5ZXIiLCJzaWQiOiJtYXBfMDEiLCJqdGkiOiJiZWUxMjU4YS1jMjE4LTQ3OWQtOTg4My02MGQ0MzhiNmU2MGQiLCJpYXQiOjE3ODkwMjAwMTUsImV4cCI6MTc4OTAyMDA0NX0." +
            "zh3db30fMry-UuUhPaUrJtaGH-K_JF5bM5jtIexTNM4";

        private const string RealJti = "bee1258a-c218-479d-9883-60d438b6e60d";

        [Test]
        public void ReadsTheJtiFromARealJoinToken()
        {
            string jti;
            Assert.IsTrue(JoinTokenClaims.TryReadJti(RealJoinToken, out jti));
            Assert.AreEqual(RealJti, jti);
        }

        /// <summary>
        /// The claims segment of that token is not a multiple of four characters, so the
        /// padding path is exercised by the test above rather than only by a synthetic case.
        /// </summary>
        [Test]
        public void TheRealTokensClaimsSegmentActuallyNeedsRepadding()
        {
            string claims = RealJoinToken.Split('.')[1];
            Assert.AreNotEqual(0, claims.Length % 4,
                "fixture check: if this token happened to be a multiple of 4, the repadding "
                + "path would be untested and this test says so instead of passing quietly");
        }

        /// <summary>
        /// The base64url alphabet substitution, which NO REAL JOIN TOKEN EXERCISES.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This test is synthetic on purpose, and the reason is worth stating because it is a
        /// live gap in the fixture above rather than a stylistic choice.
        /// </para>
        /// <para>
        /// <b>Measured:</b> across 20,000 join tokens minted by the real backend signer, the
        /// claims segment contained <c>-</c> zero times and <c>_</c> zero times. The signature
        /// segment contained one or the other in 720 of 1,000 — but the client never decodes
        /// that segment.
        /// </para>
        /// <para>
        /// <b>And it is structural, not luck.</b> Base64 emits <c>+</c> or <c>/</c> — the two
        /// characters base64url replaces — only from a 6-bit group of value 62 or 63, and with
        /// pure-ASCII input that can only arise from a byte at index ≡ 2 (mod 3) being one of
        /// <c>&gt;</c>, <c>?</c> or <c>~</c>. Verified exhaustively over every printable ASCII
        /// byte at all three positions. JWT claims JSON contains none of those characters, so
        /// a real token can never exercise the substitution.
        /// </para>
        /// <para>
        /// <b>Which is exactly why this test exists.</b> A mutation deleting the substitution
        /// entirely left all eleven other tests passing. The code is still correct and still
        /// required — RFC 7515 mandates base64url and a claim shape could change — but it was
        /// untested, and "no test fails" was not evidence that it worked.
        /// </para>
        /// </remarks>
        [Test]
        public void TheBase64UrlSubstitutionIsExercised()
        {
            // jti ">yy?" places two of those bytes at index 2 (mod 3), producing one '-' and
            // one '_' in the encoded claims. Not a shape any signer here emits.
            const string synthetic =
                "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9." +
                "eyJzdWIiOiJwcm9iZS1wbGF5ZXIiLCJzaWQiOiJtYXBfMDEiLCJqdGkiOiI-eXk_IiwiaWF0IjoxNzg5MDIwMDE1LCJleHAiOjE3ODkwMjAwNDV9." +
                "c2ln";

            string claims = synthetic.Split('.')[1];
            Assert.IsTrue(claims.Contains("-") && claims.Contains("_"),
                "fixture check: this token must contain BOTH substitutions or the test proves nothing");

            string jti;
            Assert.IsTrue(JoinTokenClaims.TryReadJti(synthetic, out jti));
            Assert.AreEqual(">yy?", jti);
        }

        /// <summary>
        /// A session token has no <c>jti</c> — only join tokens get one — so this must fail
        /// rather than return something plausible.
        /// </summary>
        [Test]
        public void ATokenWithoutAJtiIsRefused()
        {
            // sub/iat/exp only, which is what jwt.Sign produces for a session token.
            const string noJti =
                "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9." +
                "eyJzdWIiOiJwcm9iZS1wbGF5ZXIiLCJpYXQiOjE3ODkwMjAwMTUsImV4cCI6MTc4OTAyMDA0NX0." +
                "c2lnbmF0dXJl";

            string jti;
            Assert.IsFalse(JoinTokenClaims.TryReadJti(noJti, out jti));
            Assert.AreEqual(string.Empty, jti);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not-a-jwt")]
        [TestCase("only.two")]
        [TestCase("a.b.c.d")]
        [TestCase("header.!!!not-base64!!!.sig")]
        [TestCase("header.bm90IGpzb24.sig")]
        public void MalformedInputIsRefusedWithAnEmptyValue(string token)
        {
            string jti;
            Assert.IsFalse(JoinTokenClaims.TryReadJti(token, out jti), token ?? "null");
            Assert.AreEqual(string.Empty, jti,
                "a caller that ignores the return must get an empty string, never a plausible wrong one");
        }

        /// <summary>
        /// A tampered signature changes nothing, and that is deliberate: this reads unverified
        /// claims and must not pretend otherwise.
        /// </summary>
        /// <remarks>
        /// It is safe because the jti is used only as an HKDF salt — a wrong one derives keys
        /// the server cannot match, so no session forms. It can only fail, never grant. This
        /// test exists so nobody later "fixes" the class by adding a verification it has no
        /// key to perform.
        /// </remarks>
        [Test]
        public void AnInvalidSignatureIsNotDetected_AndThatIsBySignature()
        {
            string tampered = RealJoinToken.Substring(0, RealJoinToken.LastIndexOf('.') + 1) + "AAAAAAAA";

            string jti;
            Assert.IsTrue(JoinTokenClaims.TryReadJti(tampered, out jti));
            Assert.AreEqual(RealJti, jti);
        }
    }
}
