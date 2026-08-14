using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Bootstrap;

namespace Cuvara.Netcode.Auth
{
    /// <summary>
    /// Development-only auth provider that mints a JWT locally using
    /// <see cref="DevJwt"/>. In production Nakama issues the token and the
    /// client never holds the signing secret.
    /// </summary>
    public sealed class DevAuthProvider : IAuthProvider
    {
        readonly string _userId;
        readonly string _secret;
        readonly TimeSpan _lifetime;

        public DevAuthProvider(string userId, string secret, TimeSpan lifetime)
        {
            _userId = userId;
            _secret = secret;
            _lifetime = lifetime;
        }

        public UniTask<string> GetJwtAsync(CancellationToken ct)
        {
            var jwt = DevJwt.Sign(_userId, _secret, _lifetime);
            return UniTask.FromResult(jwt);
        }
    }
}
