using System;
using System.Collections.Generic;
using System.Threading;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Transport;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Realtime gameplay is KCP/UDP only: the factory never builds TCP for the gameplay
    /// hop, <see cref="GameSessionClient"/> refuses a non-KCP assignment before it asks any
    /// factory for anything, and that refusal is permanent rather than retried.
    /// </summary>
    /// <remarks>
    /// Editor-only because <see cref="KcpTransport"/> and <see cref="GameSessionClient"/>
    /// await through UniTask. The Unity-free half (parsing, conversation ids, idle timeout,
    /// receive bound, a real UDP loopback) is <see cref="KcpGameplayTransportTests"/>, which
    /// also runs under <c>dotnet test</c> in <c>Tests~/Headless</c>.
    /// </remarks>
    [TestFixture]
    public sealed class GameplayTransportPolicyTests
    {
        private sealed class NullLog : INetLog
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception exception = null) { }
        }

        private sealed class RecordingFactory : ITransportFactory
        {
            public readonly List<TransportKind> Requested = new List<TransportKind>();

            public ITransport Create(TransportKind kind)
            {
                Requested.Add(kind);
                throw new InvalidOperationException("stop here: the test only records the request");
            }
        }

        [Test]
        public void DefaultFactory_BuildsKcpForGameplay_AndNeverTcp()
        {
            var factory = new DefaultTransportFactory();

            Assert.That(factory.CreateGameplay(), Is.InstanceOf<KcpTransport>());
            Assert.That(factory.Create(TransportKind.Kcp), Is.InstanceOf<KcpTransport>());
            Assert.That(factory.CreateGameplay(), Is.Not.InstanceOf<TcpTransport>());
        }

        [Test]
        public void DefaultFactory_GatewayHopIsStillTcp()
        {
            var factory = new DefaultTransportFactory();

            Assert.That(factory.CreateGateway(useTls: false), Is.InstanceOf<TcpTransport>());
            Assert.That(factory.Create(TransportKind.Tcp), Is.InstanceOf<TcpTransport>());
        }

        [Test]
        public void DefaultFactory_CarriesTheTransportKeyIntoKcp()
        {
            var factory = new DefaultTransportFactory(new string('b', 64));

            Assert.That(factory.HasTransportKey, Is.True);
            Assert.That(((KcpTransport)factory.CreateGameplay()).IsEncrypted, Is.True);
            Assert.That(((KcpTransport)new DefaultTransportFactory().CreateGameplay()).IsEncrypted, Is.False);
        }

        [TestCase(TransportKind.Tcp)]
        [TestCase(TransportKind.TcpTls)]
        public void JoinWithATcpAssignment_IsRefused_BeforeAnyFactoryIsAsked(TransportKind kind)
        {
            var factory = new RecordingFactory();
            using (var session = new GameSessionClient(new NetworkSettings(), factory, new ProtobufWireCodec(), new NullLog()))
            {
                var assignment = new MapAssignment(new NetworkEndpoint("127.0.0.1", 9000), "tok", kind);

                var ex = Assert.Throws<NetworkException>(
                    () => session.JoinAsync(assignment, CancellationToken.None).GetAwaiter().GetResult());

                Assert.That(ex.ServerError, Is.EqualTo(TransportKinds.UnsupportedGameplayTransport));
                Assert.That(ex.Message, Does.Contain("KCP/UDP only"));
                Assert.That(factory.Requested, Is.Empty, "no factory may be asked to build a TCP gameplay link");
            }
        }

        [Test]
        public void JoinWithAKcpAssignment_AsksTheFactoryForKcp()
        {
            var factory = new RecordingFactory();
            using (var session = new GameSessionClient(new NetworkSettings(), factory, new ProtobufWireCodec(), new NullLog()))
            {
                var assignment = new MapAssignment(new NetworkEndpoint("127.0.0.1", 9000), "tok", TransportKind.Kcp);

                Assert.Throws<InvalidOperationException>(
                    () => session.JoinAsync(assignment, CancellationToken.None).GetAwaiter().GetResult());

                Assert.That(factory.Requested, Is.EqualTo(new[] { TransportKind.Kcp }));
            }
        }

        [Test]
        public void AnUnsupportedGameplayTransport_IsAPermanentFailure()
        {
            var ex = new NetworkException("x", TransportKinds.UnsupportedGameplayTransport);

            Assert.That(ReconnectPolicy.IsPermanentFailure(ex), Is.True);
        }

        [Test]
        public void AJoinTimeoutWithNoDatagramBack_IsReportedAsAKcpUdpConnectTimeout()
        {
            using (var session = new GameSessionClient(
                       new NetworkSettings { ConnectTimeout = TimeSpan.FromSeconds(10) },
                       new DefaultTransportFactory(), new ProtobufWireCodec(), new NullLog()))
            using (var transport = new KcpTransport())
            {
                var message = session.DescribeJoinTimeout(transport, new NetworkEndpoint("10.1.2.3", 7019));

                Assert.That(message, Does.Contain("KCP/UDP connect timeout to 10.1.2.3:7019"));
                Assert.That(message, Does.Contain("firewall"));
                Assert.That(message, Does.Contain("udp/7019"));
            }
        }

        /// <summary>
        /// A cancelled read on an open KCP transport is a cancellation, never end-of-stream.
        /// It used to return null, so the join's own deadline against an unreachable UDP
        /// port surfaced as "game server closed the connection during the join" instead of
        /// the KCP/UDP connect-timeout message (seen live: a Windows client against a WSL2
        /// NAT stack advertising 127.0.0.1).
        /// </summary>
        [UnityTest]
        public System.Collections.IEnumerator ACancelledKcpRead_Throws_InsteadOfReportingEof() =>
            UniTask.ToCoroutine(async () =>
            {
                int deadPort;
                using (var probe = new System.Net.Sockets.UdpClient(0))
                {
                    deadPort = ((System.Net.IPEndPoint)probe.Client.LocalEndPoint).Port;
                }

                using (var transport = new KcpTransport())
                using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                {
                    await transport.ConnectAsync("127.0.0.1", deadPort, CancellationToken.None);
                    bool cancelled = false;
                    try
                    {
                        var frame = await transport.ReadFrameAsync(cts.Token);
                        Assert.Fail($"ReadFrameAsync returned {(frame == null ? "null (EOF)" : "a frame")} on cancellation");
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }

                    Assert.That(cancelled, Is.True);
                }
            });
    }
}
