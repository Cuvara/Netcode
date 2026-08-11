using NUnit.Framework;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Parsing of the <c>server_addr</c> the gateway returns from
    /// <c>enter_world</c>.
    /// </summary>
    /// <remarks>
    /// The host-less case is the one with history: the local Docker stack
    /// advertises <c>GAMESERVER_PUBLIC_ADDR=":9200"</c>, the gateway hands it back
    /// verbatim, and this client used to reject it outright — which stopped the
    /// bootstrap dead one step after <c>enter_world</c>. Go's <c>net.Dial</c>
    /// accepts a host-less address, so no Go-side test ever covered it.
    /// </remarks>
    [TestFixture]
    public sealed class NetworkEndpointTests
    {
        [TestCase("127.0.0.1:9000", "127.0.0.1", 9000)]
        [TestCase("gameserver:9000", "gameserver", 9000)]
        [TestCase("10.0.0.5:65535", "10.0.0.5", 65535)]
        public void ParsesHostAndPort(string address, string host, int port)
        {
            var endpoint = NetworkEndpoint.Parse(address);
            Assert.AreEqual(host, endpoint.Host);
            Assert.AreEqual(port, endpoint.Port);
        }

        [TestCase("[::1]:9000", "::1", 9000)]
        [TestCase("[fe80::1]:9200", "fe80::1", 9200)]
        public void StripsIpv6Brackets(string address, string host, int port)
        {
            var endpoint = NetworkEndpoint.Parse(address);
            Assert.AreEqual(host, endpoint.Host);
            Assert.AreEqual(port, endpoint.Port);
        }

        [TestCase(":9200", 9200)]
        [TestCase(":9000", 9000)]
        public void HostlessAddressFallsBackToLoopback(string address, int port)
        {
            var endpoint = NetworkEndpoint.Parse(address);
            Assert.AreEqual(NetworkEndpoint.DefaultHost, endpoint.Host);
            Assert.AreEqual("127.0.0.1", endpoint.Host);
            Assert.AreEqual(port, endpoint.Port);
        }

        [TestCase("")]
        [TestCase("9200")]
        [TestCase("127.0.0.1:")]
        [TestCase(":")]
        [TestCase("127.0.0.1:0")]
        [TestCase("127.0.0.1:65536")]
        [TestCase("127.0.0.1:-1")]
        [TestCase("127.0.0.1:abc")]
        public void RejectsMalformedAddresses(string address)
        {
            Assert.Throws<TransportException>(() => NetworkEndpoint.Parse(address));
        }

        [Test]
        public void RoundTripsThroughToString()
        {
            Assert.AreEqual("127.0.0.1:9200", NetworkEndpoint.Parse(":9200").ToString());
        }
    }
}
