using System;
using System.Security.Cryptography;
using System.Text;

namespace Cuvara.Netcode.Bootstrap
{
    /// <summary>
    /// Mints the HS256 JWT the gateway expects, for <b>local development only</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In the shipped architecture the client never holds the signing secret: Nakama
    /// issues the token over HTTPS and the client only carries it (see the meta
    /// channel in the backend README). This exists so the netcode can be exercised
    /// against a locally run gateway before any meta service is wired up, and so
    /// that "press Play" needs one running backend rather than two.
    /// </para>
    /// <para>
    /// The claim set matches <c>backend/shared/jwt</c>'s <c>Claims</c> exactly —
    /// <c>sub</c>, <c>iat</c>, <c>exp</c>, with <c>sid</c>/<c>jti</c> omitted, which
    /// is what <c>jwt.Sign</c> produces for a plain session token. Join tokens are
    /// minted by the gateway and are never built here.
    /// </para>
    /// </remarks>
    public static class DevJwt
    {
        /// <summary>
        /// Signs a session token for <paramref name="userId"/> valid for
        /// <paramref name="lifetime"/>.
        /// </summary>
        public static string Sign(string userId, string secret, TimeSpan lifetime)
        {
            if (string.IsNullOrEmpty(userId))
            {
                throw new ArgumentException("user id is required", nameof(userId));
            }

            if (string.IsNullOrEmpty(secret))
            {
                throw new ArgumentException("signing secret is required", nameof(secret));
            }

            var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var expiresAt = issuedAt + (long)lifetime.TotalSeconds;

            var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
            var claims = Base64Url(Encoding.UTF8.GetBytes(
                "{\"sub\":\"" + Escape(userId) + "\",\"iat\":" + issuedAt + ",\"exp\":" + expiresAt + "}"));

            var signingInput = header + "." + claims;
            using (var mac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
            {
                var signature = Base64Url(mac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));
                return signingInput + "." + signature;
            }
        }

        /// <summary>
        /// Base64url without padding — <c>base64.RawURLEncoding</c> on the Go side.
        /// Padding characters would make the signature input differ and every token
        /// would be rejected.
        /// </summary>
        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string Escape(string value) =>
            value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
