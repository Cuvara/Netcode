using System;

namespace Cuvara.Netcode.Transport
{
    /// <summary>Parsing and policy for the <b>gameplay</b> transport (the game-server hop).</summary>
    /// <remarks>
    /// Realtime gameplay is KCP over UDP only. The <c>transport</c> field of
    /// <c>enter_world_resp</c> must be <c>"kcp"</c> (case-insensitive); an empty field,
    /// <c>"tcp"</c> or anything else is a hard failure that names the value. Empty no longer
    /// means TCP, and nothing here ever falls back to TCP: TCP/TLS is the gateway hop's
    /// transport and nothing else.
    /// </remarks>
    public static class TransportKinds
    {
        /// <summary>The one gameplay transport string the gateway may send.</summary>
        public const string GameplayKcp = "kcp";

        /// <summary>
        /// The <c>NetworkException.ServerError</c> attached when the assigned game server
        /// advertises a gameplay transport this client does not speak. Permanent: asking the
        /// same gateway again returns the same answer, so it is neither retried nor reconnected.
        /// </summary>
        public const string UnsupportedGameplayTransport = "unsupported_gameplay_transport";

        /// <summary>
        /// Maps the <c>enter_world_resp</c> transport string onto the gameplay transport, or
        /// returns false with a message naming the refused value.
        /// </summary>
        public static bool TryParseGameplay(string value, out TransportKind kind, out string error)
        {
            if (value != null && string.Equals(value.Trim(), GameplayKcp, StringComparison.OrdinalIgnoreCase))
            {
                kind = TransportKind.Kcp;
                error = null;
                return true;
            }

            kind = default;
            error = Describe(value);
            return false;
        }

        /// <summary>
        /// Maps the <c>enter_world_resp</c> transport string onto the gameplay transport.
        /// </summary>
        /// <exception cref="TransportException">The value is not <c>"kcp"</c>.</exception>
        public static TransportKind ParseGameplay(string value)
        {
            if (!TryParseGameplay(value, out var kind, out var error))
            {
                throw new TransportException(error);
            }

            return kind;
        }

        /// <summary>
        /// Throws unless <paramref name="kind"/> is a transport the gameplay hop may use —
        /// which is <see cref="TransportKind.Kcp"/> and nothing else.
        /// </summary>
        /// <exception cref="TransportException"><paramref name="kind"/> is TCP, TLS or unknown.</exception>
        public static void RequireGameplay(TransportKind kind)
        {
            if (kind != TransportKind.Kcp)
            {
                throw new TransportException(
                    $"gameplay transport {kind} refused: realtime gameplay is KCP/UDP only " +
                    "(TCP and TLS are gateway-hop transports)");
            }
        }

        private static string Describe(string value)
        {
            var shown = value == null ? "<null>" : "'" + value + "'";
            return $"enter_world_resp advertised gameplay transport {shown}, but this client speaks " +
                   $"KCP/UDP only (\"{GameplayKcp}\"); TCP gameplay was removed and an empty value no " +
                   "longer means TCP. The gateway or game server is older than this client, or misconfigured.";
        }
    }
}
