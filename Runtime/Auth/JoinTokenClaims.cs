using System;
using System.Text;
using Cuvara.Netcode.Json;

namespace Cuvara.Netcode.Auth
{
    /// <summary>
    /// Reads the claims out of a join token WITHOUT verifying it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing here checks a signature, and nothing here may be used to decide anything.</b>
    /// A client holds no key that could verify a join token — the secret lives on the gateway
    /// and the game server — so every value this returns is an unverified claim by whoever
    /// produced the string. Treating one as an authorisation fact is the mistake this whole
    /// file exists to make hard to reach: the class is named for the token, the methods say
    /// so, and the only caller is the sealed handshake.
    /// </para>
    /// <para>
    /// <b>Why reading it unverified is nevertheless safe here.</b> The sealed handshake uses
    /// <c>jti</c> as an HKDF salt and nothing else. If it is wrong — corrupted, or supplied by
    /// an attacker — the two peers derive different keys and no session forms. A wrong jti
    /// cannot grant anything; it can only fail. The value's integrity is enforced by the
    /// handshake succeeding, not by parsing it.
    /// </para>
    /// <para>
    /// The claims are plain base64url, which is exactly why the per-session key of the scheme
    /// ADR-22 supersedes could not live in them: anyone on the wire can read this too.
    /// </para>
    /// </remarks>
    public static class JoinTokenClaims
    {
        /// <summary>
        /// Read the <c>jti</c> claim. Returns false for anything that is not a JWT with a
        /// readable claims segment carrying a non-empty <c>jti</c>.
        /// </summary>
        /// <remarks>
        /// Every failure is the same <c>false</c> with an empty value — there is no partial
        /// success, and a caller that ignores the return gets an empty string rather than a
        /// plausible-looking wrong one.
        /// </remarks>
        public static bool TryReadJti(string joinToken, out string jti)
        {
            jti = string.Empty;
            if (string.IsNullOrEmpty(joinToken)) return false;

            string[] parts = joinToken.Split('.');
            if (parts.Length != 3) return false;

            byte[] claims;
            if (!TryDecodeBase64Url(parts[1], out claims)) return false;

            try
            {
                JsonValue parsed = JsonParser.Parse(Encoding.UTF8.GetString(claims));
                string value = parsed.GetString("jti");
                if (string.IsNullOrEmpty(value)) return false;

                jti = value;
                return true;
            }
            catch (JsonParseException)
            {
                return false;
            }
        }

        /// <summary>Base64url with the padding JWT omits.</summary>
        private static bool TryDecodeBase64Url(string segment, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (string.IsNullOrEmpty(segment)) return false;

            var sb = new StringBuilder(segment.Length + 3);
            foreach (char c in segment)
            {
                if (c == '-') sb.Append('+');
                else if (c == '_') sb.Append('/');
                else sb.Append(c);
            }

            switch (sb.Length % 4)
            {
                case 2: sb.Append("=="); break;
                case 3: sb.Append('='); break;
                case 0: break;
                // A length of 1 mod 4 cannot be valid base64 at all.
                default: return false;
            }

            try
            {
                bytes = Convert.FromBase64String(sb.ToString());
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
