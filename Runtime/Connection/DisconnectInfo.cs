using System;

namespace Cuvara.Netcode.Connection
{
    /// <summary>What ended a connection, and what the server said about it.</summary>
    public readonly struct DisconnectInfo
    {
        public DisconnectInfo(DisconnectCause cause, string reason = "", Exception exception = null)
        {
            Cause = cause;
            Reason = reason ?? string.Empty;
            Exception = exception;
        }

        public DisconnectCause Cause { get; }

        /// <summary>
        /// The machine-readable reason from a <c>kick</c> or <c>disconnect</c>
        /// frame, or empty. <c>duplicate_login</c> and <c>server_shutdown</c> are
        /// the only values emitted today; treat anything else generically.
        /// </summary>
        public string Reason { get; }

        /// <summary>The underlying failure, for a transport or protocol error.</summary>
        public Exception Exception { get; }

        public override string ToString()
        {
            var reason = string.IsNullOrEmpty(Reason) ? "" : " (" + Reason + ")";
            return Cause + reason;
        }
    }
}
