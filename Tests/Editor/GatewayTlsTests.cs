using System;
using System.Text;
using System.Threading;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Transport;
using Cysharp.Threading.Tasks;
using NUnit.Framework;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The gateway hop's TLS wiring (ADR-23).
    /// </summary>
    /// <remarks>
    /// The assertions that matter here are the <b>negative</b> ones. A TLS switch that
    /// silently produces a plaintext link, or a "TLS" that accepts any certificate, reads
    /// exactly like a working one from every log and every happy-path test — the same
    /// failure shape the IL2CPP probe was built to catch one layer down.
    /// </remarks>
    public sealed class GatewayTlsTests
    {
        /// <summary>Records which kind was asked for, and hands back something inert.</summary>
        private sealed class RecordingFactory : ITransportFactory
        {
            public TransportKind? Requested;

            public ITransport Create(TransportKind kind)
            {
                Requested = kind;
                return new NeverTransport();
            }
        }

        private sealed class SilentLog : INetLog
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception exception = null) { }
        }

        private sealed class NeverTransport : ITransport
        {
            public string RemoteEndPoint => string.Empty;
            public bool IsConnected => false;

            public UniTask ConnectAsync(string host, int port, CancellationToken cancellationToken)
                => throw new TransportException("test transport never connects");

            public UniTask<byte[]> ReadFrameAsync(CancellationToken cancellationToken)
                => throw new TransportException("test transport never reads");

            public UniTask WriteFrameAsync(byte[] body, CancellationToken cancellationToken)
                => throw new TransportException("test transport never writes");

            public void Close()
            {
            }

            public void Dispose()
            {
            }
        }

        // ---- the wiring, from the setting to the kind that is actually requested -------

        [Test]
        public void GatewayAsksForTls_WhenTheSettingIsOn()
        {
            var factory = new RecordingFactory();
            var settings = new NetworkSettings { GatewayUseTls = true };
            var client = new GatewayClient(settings, factory, new JsonWireCodec(), new SilentLog());

            // The dial fails on NeverTransport; the kind has already been requested by
            // then, which is the thing under test.
            Assert.That(async () => await client.AuthenticateAsync("jwt", CancellationToken.None),
                Throws.Exception);

            Assert.That(factory.Requested, Is.EqualTo(TransportKind.TcpTls),
                "GatewayUseTls was on and the gateway still dialled a plaintext transport");
        }

        [Test]
        public void GatewayAsksForPlaintext_WhenTheSettingIsOff()
        {
            var factory = new RecordingFactory();
            var settings = new NetworkSettings();
            var client = new GatewayClient(settings, factory, new JsonWireCodec(), new SilentLog());

            Assert.That(async () => await client.AuthenticateAsync("jwt", CancellationToken.None),
                Throws.Exception);

            Assert.That(factory.Requested, Is.EqualTo(TransportKind.Tcp));
            Assert.That(new NetworkSettings().GatewayUseTls, Is.False,
                "TLS must stay off by default, matching the gateway's own default");
        }

        // ---- the factory refuses to substitute cleartext -------------------------------

        [Test]
        public void Factory_RefusesTls_RatherThanHandingBackCleartext()
        {
            var factory = new DefaultTransportFactory();

            var ex = Assert.Throws<TransportException>(() => factory.Create(TransportKind.TcpTls));
            Assert.That(ex.Message, Does.Contain("TlsOptions"));
        }

        [Test]
        public void Factory_BuildsATlsTransport_WhenGivenOptions()
        {
            var factory = new DefaultTransportFactory(null, new TlsOptions());

            var transport = factory.Create(TransportKind.TcpTls);

            Assert.That(transport, Is.InstanceOf<TcpTransport>());
            Assert.That(((TcpTransport)transport).IsTls, Is.True);
        }

        [Test]
        public void Factory_PlainTcpIsNeverTls_EvenWithOptionsPresent()
        {
            var factory = new DefaultTransportFactory(null, new TlsOptions());

            var transport = (TcpTransport)factory.Create(TransportKind.Tcp);

            // The game-server hop asks for Tcp and is sealed at the message layer instead.
            // Wrapping it in TLS here would double-encrypt and, worse, would mean the
            // sealed-session tests were measuring a link they did not think they had.
            Assert.That(transport.IsTls, Is.False);
        }

        // ---- a server cannot talk the client into this kind ----------------------------

        [Test]
        public void Parse_RefusesTcpTls_BecauseNoServerMayAssignIt()
        {
            Assert.Throws<TransportException>(() => TransportKinds.Parse("tcptls"));
            Assert.Throws<TransportException>(() => TransportKinds.Parse("tls"));

            // The two a server may actually send still work.
            Assert.That(TransportKinds.Parse("tcp"), Is.EqualTo(TransportKind.Tcp));
            Assert.That(TransportKinds.Parse("kcp"), Is.EqualTo(TransportKind.Kcp));
            Assert.That(TransportKinds.Parse(string.Empty), Is.EqualTo(TransportKind.Tcp));
        }

        // ---- settings -> options --------------------------------------------------------

        [Test]
        public void BuildGatewayTlsOptions_IsNullWhenTlsIsOff()
        {
            var settings = new NetworkSettings
            {
                // Set the pin too: a pin without the switch must not turn TLS on by
                // accident, or the switch is not the switch.
                GatewayTlsPinnedCertificate = new byte[] { 1, 2, 3 },
            };

            Assert.That(settings.BuildGatewayTlsOptions(), Is.Null);
        }

        [Test]
        public void BuildGatewayTlsOptions_CarriesThePinAndTheTargetHost()
        {
            var pin = new byte[] { 9, 8, 7 };
            var settings = new NetworkSettings
            {
                GatewayUseTls = true,
                GatewayTlsPinnedCertificate = pin,
                GatewayTlsTargetHost = "gateway.example.com",
            };

            var options = settings.BuildGatewayTlsOptions();

            Assert.That(options, Is.Not.Null);
            Assert.That(options.TargetHost, Is.EqualTo("gateway.example.com"));
            Assert.That(options.PinnedCertificate, Is.SameAs(pin));
            Assert.That(options.IsPinned, Is.True);
        }

        [Test]
        public void TlsOptions_WithoutAPin_IsNotPinned()
        {
            Assert.That(new TlsOptions().IsPinned, Is.False);
            Assert.That(new TlsOptions { PinnedCertificate = Array.Empty<byte>() }.IsPinned, Is.False,
                "an empty array is not a pin; treating it as one would pin nothing while reporting a pin");
        }

        // ---- PEM loading ----------------------------------------------------------------

        [Test]
        public void FromPem_ReadsTheCertificateBody()
        {
            var der = new byte[] { 0x30, 0x82, 0x01, 0x0A, 0xDE, 0xAD, 0xBE, 0xEF };
            var pem = "-----BEGIN CERTIFICATE-----\n" +
                      Convert.ToBase64String(der) + "\n" +
                      "-----END CERTIFICATE-----\n";

            Assert.That(TlsOptions.FromPem(pem), Is.EqualTo(der));
        }

        [Test]
        public void FromPem_ToleratesWrappingAndCarriageReturns()
        {
            var der = new byte[64];
            for (var i = 0; i < der.Length; i++)
            {
                der[i] = (byte)i;
            }

            var b64 = Convert.ToBase64String(der);
            var wrapped = new StringBuilder("-----BEGIN CERTIFICATE-----\r\n");
            for (var i = 0; i < b64.Length; i += 16)
            {
                wrapped.Append(b64, i, Math.Min(16, b64.Length - i)).Append("\r\n");
            }

            wrapped.Append("-----END CERTIFICATE-----\r\n");

            Assert.That(TlsOptions.FromPem(wrapped.ToString()), Is.EqualTo(der));
        }

        [Test]
        public void FromPem_ThrowsRatherThanReturningAnUnpinnedNull()
        {
            // Each of these would otherwise produce a null pin, which silently means
            // "validate against the platform trust store" -- and against a self-signed dev
            // gateway that fails with a message about the certificate, sending the reader
            // to look at the certificate rather than at the pin that never loaded.
            Assert.Throws<ArgumentException>(() => TlsOptions.FromPem(null));
            Assert.Throws<ArgumentException>(() => TlsOptions.FromPem(string.Empty));
            Assert.Throws<ArgumentException>(() => TlsOptions.FromPem("not a certificate at all"));
            Assert.Throws<ArgumentException>(() => TlsOptions.FromPem("-----BEGIN CERTIFICATE-----\nAAAA"));
            Assert.Throws<ArgumentException>(() =>
                TlsOptions.FromPem("-----BEGIN CERTIFICATE-----\n!!!not base64!!!\n-----END CERTIFICATE-----"));
        }
    }
}
