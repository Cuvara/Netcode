using System;

namespace Cuvara.Netcode.Client
{
    /// <summary>
    /// A handshake or session failure reported by a server: a rejected auth, a
    /// refused map assignment, a rejected join token.
    /// </summary>
    /// <remarks>
    /// <see cref="ServerError"/> carries the server's own error string, which comes
    /// from a closed set on the gateway side. Match on it if you must, but treat an
    /// unknown value generically — the set is the server's to extend.
    /// </remarks>
    public sealed class NetworkException : Exception
    {
        public NetworkException(string message, string serverError = "")
            : base(message)
        {
            ServerError = serverError ?? string.Empty;
        }

        public string ServerError { get; }
    }
}
