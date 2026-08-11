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
        /// Host substituted for a <c>server_addr</c> that carries only a port.
        /// </summary>
        public const string DefaultHost = "127.0.0.1";

        /// <summary>
        /// Parses <c>host:port</c>. Splits on the last colon so a bracketed IPv6
        /// literal (<c>[::1]:9000</c>) parses too; the brackets are stripped,
        /// because <see cref="System.Net.Sockets.TcpClient"/> wants the bare address.
        /// </summary>
        /// <remarks>
        /// A host-less <c>server_addr</c> such as <c>":9200"</c> is normalised to
        /// <see cref="DefaultHost"/> rather than rejected. This is the documented
        /// contract, not leniency for its own sake: the game server advertises
        /// <c>GAMESERVER_PUBLIC_ADDR</c> and the gateway hands it back verbatim, and
        /// `backend/deploy/docker-compose.yml` states that a bare <c>":9200"</c> is
        /// normalised by the client to <c>127.0.0.1:9200</c> — correct for a local
        /// stack, where the published port is on the same machine as the client. Go's
        /// <c>net.Dial</c> does the same thing, so the Go tooling never noticed the
        /// gap this client fell into.
        /// <para>
        /// It is only ever correct locally. A real deployment must set
        /// <c>GAMESERVER_PUBLIC_ADDR=&lt;public-host&gt;:&lt;published-port&gt;</c>; a
        /// host-less address reaching a player's device would send it to its own
        /// loopback. Hence the deliberate choice of loopback over "reuse the gateway
        /// host", which would silently paper over that misconfiguration in production
        /// by connecting to something plausible but unintended.
        /// </para>
        /// </remarks>
        public static NetworkEndpoint Parse(string address)
        {
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

            if (host.Length == 0)
            {
                host = DefaultHost;
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
