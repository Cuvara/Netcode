using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Cuvara.Netcode.Auth
{
    /// <summary>
    /// Adapts a delegate to <see cref="IAuthProvider"/>, for callers whose token
    /// source is a method on an existing object rather than a class of its own —
    /// the DOTS sample's Nakama helper, a test fixture handing out a canned JWT.
    /// </summary>
    public sealed class DelegateAuthProvider : IAuthProvider
    {
        private readonly Func<CancellationToken, UniTask<string>> _getJwt;

        public DelegateAuthProvider(Func<CancellationToken, UniTask<string>> getJwt)
        {
            _getJwt = getJwt ?? throw new ArgumentNullException(nameof(getJwt));
        }

        public UniTask<string> GetJwtAsync(CancellationToken ct) => _getJwt(ct);
    }
}
