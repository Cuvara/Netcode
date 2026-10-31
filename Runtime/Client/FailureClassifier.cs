using System;
using Cuvara.Netcode.Connection;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Client
{
    /// <summary>What went wrong, independent of what any one caller does about it.</summary>
    internal enum FailureKind
    {
        /// <summary>Nothing recognised: a socket error, a timeout, an unlisted server string. Retried.</summary>
        Transient = 0,

        /// <summary>The pinned TLS certificate did not match.</summary>
        TlsPinMismatch,

        /// <summary>The auth provider produced no credential (<see cref="InvalidOperationException"/>).</summary>
        CredentialUnavailable,

        /// <summary>The gateway refused the JWT or the auth frame itself.</summary>
        AuthRejected,

        /// <summary>No fleet in this deployment hosts the requested map.</summary>
        MapUnavailable,

        /// <summary>Either hop cannot serve this wire protocol version.</summary>
        ProtocolVersionMismatch,

        /// <summary>The join reply named a gameplay transport other than KCP.</summary>
        UnsupportedGameplayTransport,

        /// <summary>The gateway session expired.</summary>
        SessionExpired,

        /// <summary>The gateway rate-limited the client.</summary>
        RateLimited,

        /// <summary>This client closed the connection.</summary>
        LocalClose,

        /// <summary>The server refused a client that did not seal the session.</summary>
        SealedSessionRequired,

        /// <summary>The server evicted the client (<c>duplicate_login</c> or any other kick reason).</summary>
        Evicted,

        /// <summary>The peer sent a frame the client cannot decode.</summary>
        ProtocolFault,

        /// <summary>The server is draining and promises a replacement.</summary>
        ServerDraining,

        /// <summary>The server ended the connection without promising a comeback.</summary>
        ServerClosed,

        /// <summary>The link died without anyone choosing that.</summary>
        LinkLost,
    }

    /// <summary>
    /// The one table behind every retry and permanence decision: <see cref="ReconnectPolicy"/>
    /// and the first-connect join loop in <see cref="NetworkClient"/> both ask here. Pure, no
    /// Unity types, so it compiles into the headless suite.
    /// </summary>
    /// <remarks>
    /// The strings are the servers' own (gateway <c>server.go</c>, game server
    /// <c>GameServer.cs</c>). Anything unrecognised is <see cref="FailureKind.Transient"/>: wrongly
    /// retrying a terminal error costs the rest of the budget, wrongly aborting a transient one
    /// costs the player their body.
    /// </remarks>
    internal static class FailureClassifier
    {
        public static FailureKind Classify(Exception failure)
        {
            if (failure is TlsPinMismatchException)
            {
                return FailureKind.TlsPinMismatch;
            }

            if (failure is NetworkException ex)
            {
                return ClassifyServerError(ex.ServerError);
            }

            // The auth provider could not produce a credential at all (Nakama refused, no
            // session). Retrying the network hop cannot fix a missing credential.
            if (failure is InvalidOperationException)
            {
                return FailureKind.CredentialUnavailable;
            }

            return FailureKind.Transient;
        }

        public static FailureKind ClassifyServerError(string serverError)
        {
            switch (serverError)
            {
                case "invalid token":
                case "invalid auth request":
                    return FailureKind.AuthRejected;
                case "map is not available":
                    return FailureKind.MapUnavailable;
                case KickReasons.ProtocolVersionMismatch:
                    return FailureKind.ProtocolVersionMismatch;
                case TransportKinds.UnsupportedGameplayTransport:
                    return FailureKind.UnsupportedGameplayTransport;
                case "session expired":
                    return FailureKind.SessionExpired;
                case "rate limited":
                    return FailureKind.RateLimited;
                default:
                    return FailureKind.Transient;
            }
        }

        public static FailureKind Classify(DisconnectInfo info)
        {
            switch (info.Cause)
            {
                case DisconnectCause.LocalClose:
                    return FailureKind.LocalClose;
                case DisconnectCause.Kicked:
                    return info.Reason == SealedRefusalReason.NoSealedSession
                        ? FailureKind.SealedSessionRequired
                        : FailureKind.Evicted;
                case DisconnectCause.ProtocolError:
                    return FailureKind.ProtocolFault;
                case DisconnectCause.ServerDisconnect:
                    return info.Reason == KickReasons.ServerShutdown
                        ? FailureKind.ServerDraining
                        : FailureKind.ServerClosed;
                case DisconnectCause.PeerClosed:
                case DisconnectCause.HeartbeatTimeout:
                case DisconnectCause.TransportError:
                    return FailureKind.LinkLost;
                default:
                    return FailureKind.ServerClosed;
            }
        }

        /// <summary>
        /// Whether a reconnect round, or the initial gateway connect, must stop on this failure
        /// because no further round can change the answer.
        /// </summary>
        public static bool IsPermanentForReconnect(FailureKind kind)
        {
            switch (kind)
            {
                case FailureKind.TlsPinMismatch:
                case FailureKind.CredentialUnavailable:
                case FailureKind.AuthRejected:
                case FailureKind.MapUnavailable:
                case FailureKind.ProtocolVersionMismatch:
                case FailureKind.UnsupportedGameplayTransport:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Whether the first-connect join loop tries again on this failure. Differs from
        /// <see cref="IsPermanentForReconnect"/> on purpose, for now:
        /// </summary>
        /// <remarks>
        /// NG-21: <see cref="FailureKind.MapUnavailable"/> and <see cref="FailureKind.AuthRejected"/>
        /// are permanent for a reconnect but retried here, and <see cref="FailureKind.SessionExpired"/>
        /// and <see cref="FailureKind.RateLimited"/> stop here but not a reconnect. This keeps
        /// today's behaviour exactly; unifying them is a separate, reviewed behaviour change.
        /// </remarks>
        public static bool IsRetryableByJoinLoop(FailureKind kind)
        {
            switch (kind)
            {
                case FailureKind.SessionExpired:
                case FailureKind.RateLimited:
                case FailureKind.ProtocolVersionMismatch:
                case FailureKind.UnsupportedGameplayTransport:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>The reconnect decision for how the gameplay socket ended.</summary>
        public static ReconnectDecision ReconnectDecisionFor(FailureKind kind)
        {
            switch (kind)
            {
                case FailureKind.SealedSessionRequired:
                case FailureKind.LinkLost:
                    return ReconnectDecision.Reconnect;
                case FailureKind.ServerDraining:
                    return ReconnectDecision.ReconnectAfterDelay;
                default:
                    return ReconnectDecision.Never;
            }
        }
    }
}
