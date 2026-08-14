using System;
using System.Globalization;

namespace Cuvara.Netcode.Transport
{
    /// <summary>A parsed <c>host:port</c> pair, as carried by <c>server_addr</c>.</summary>
    public readonly struct NetworkEndpoint
    {
        public NetworkEndpoint(string host, int port)
        {
            Host = host;
            Port = port;
        }

        public string Host { get; }

        public int Port { get; }

        public override string ToString() =>
            Host + ":" + Port.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Last-resort host for a listen-style <c>server_addr</c> when the caller
        /// supplies no better fallback. Prefer the overload that takes the gateway
        /// host — see <see cref="Parse(string, string, out bool)"/>.
        /// </summary>
        public const string DefaultHost = "127.0.0.1";

        /// <summary>
        /// True for the listen-style hosts a server may advertise but no client can
        /// dial. Mirrors <c>NormalizeDialAddr</c> in
        /// <c>backend/smoketest/smoke/helpers.go</c> so both ends agree on the set.
        /// </summary>
        /// <remarks>
        /// <c>"[::]"</c> is absent deliberately: <see cref="Parse(string)"/> strips
        /// the brackets before this is consulted, so it arrives as <c>"::"</c>.
        /// </remarks>
        public static bool IsListenStyleHost(string host) =>
            string.IsNullOrEmpty(host) || host == "0.0.0.0" || host == "::";

        /// <summary>
        /// Parses <c>host:port</c>. Splits on the last colon so a bracketed IPv6
        /// literal (<c>[::1]:9000</c>) parses too; the brackets are stripped,
        /// because <see cref="System.Net.Sockets.TcpClient"/> wants the bare address.
        /// </summary>
        /// <remarks>
        /// A listen-style <c>server_addr</c> such as <c>":9200"</c> or
        /// <c>"0.0.0.0:9200"</c> is normalised rather than rejected — see
        /// <see cref="Parse(string, string, out bool)"/>. This is <b>hardening, not
        /// the contract</b>. The contract is the server's: <c>GameServer</c> requires
        /// the address it advertises through <c>GAMESERVER_PUBLIC_ADDR</c> to be
        /// dialable by the client, and the wire protocol specifies no format for
        /// <c>server_addr</c> at all. A server that advertises a listen-style address
        /// is misconfigured; this only keeps a local stack usable instead of failing
        /// on something a human can trivially misread. Go's <c>net.Dial</c> resolves
        /// such addresses implicitly, which is why the Go tooling never surfaced the
        /// difference.
        /// <para>
        /// Because it masks a server-side misconfiguration, every rewrite is reported
        /// through the <c>normalised</c> out-parameter so the caller can warn.
        /// </para>
        /// </remarks>
        public static NetworkEndpoint Parse(string address) =>
            Parse(address, DefaultHost, out _);

        /// <summary>
        /// Parses <c>host:port</c>, substituting <paramref name="fallbackHost"/> when
        /// the address carries a listen-style host no client can dial.
        /// </summary>
        /// <param name="address">The <c>server_addr</c> as received.</param>
        /// <param name="fallbackHost">
        /// Host to substitute. Callers should pass the gateway host they already
        /// reached, not a loopback literal: a device talking to a LAN or remote
        /// gateway must fall back to that gateway's host, never to its own loopback.
        /// </param>
        /// <param name="normalised">
        /// True when the host was rewritten, so the caller can log a warning — a
        /// rewrite always means the server advertised something undialable.
        /// </param>
        public static NetworkEndpoint Parse(string address, string fallbackHost, out bool normalised)
        {
            normalised = false;
            if (string.IsNullOrEmpty(address))
            {
                throw new TransportException("server address is empty");
            }

            var separator = address.LastIndexOf(':');
            if (separator < 0 || separator == address.Length - 1)
            {
                throw new TransportException($"server address '{address}' is not host:port");
            }

            var host = address.Substring(0, separator);
            var portText = address.Substring(separator + 1);

            if (host.Length > 1 && host[0] == '[' && host[host.Length - 1] == ']')
            {
                host = host.Substring(1, host.Length - 2);
            }

            if (IsListenStyleHost(host))
            {
                host = string.IsNullOrEmpty(fallbackHost) ? DefaultHost : fallbackHost;
                normalised = true;
            }

            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
                port <= 0 || port > 65535)
            {
                throw new TransportException($"server address '{address}' has an invalid port");
            }

            return new NetworkEndpoint(host, port);
        }
    }
}
