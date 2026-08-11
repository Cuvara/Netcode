using System.Threading;
using Cysharp.Threading.Tasks;

namespace Cuvara.Netcode.Auth
{
    /// <summary>
    /// Provides a JWT for gateway authentication. The netcode layer does not care
    /// where the token comes from — Nakama, a dev mint, a test fixture — only that
    /// it is a valid HS256 JWT the gateway will accept.
    /// </summary>
    public interface IAuthProvider
    {
        /// <summary>
        /// Returns a JWT suitable for the gateway's <c>MsgAuth</c> handshake.
        /// Implementations may cache, refresh, or mint on the fly.
        /// </summary>
        UniTask<string> GetJwtAsync(CancellationToken ct);
    }
}
