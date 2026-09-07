#if CUVARA_NETCODE_VCONTAINER
using Cuvara.Netcode.Auth;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.DI;
using Cuvara.Netcode.Transport;
using NUnit.Framework;
using VContainer;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The registration must resolve the whole client graph from a bare container.
    /// This is the path a game scope takes when it injects <see cref="NetworkClient"/>
    /// into a scene component; the samples construct the client by hand and never
    /// exercised it, which is how a constructor default parameter VContainer does not
    /// honour went unnoticed until a scene resolved it.
    /// </summary>
    public sealed class NetworkingRegistrationTests
    {
        private sealed class NoAuth : IAuthProvider
        {
            public Cysharp.Threading.Tasks.UniTask<string> GetJwtAsync(System.Threading.CancellationToken ct) =>
                Cysharp.Threading.Tasks.UniTask.FromResult("jwt");
        }

        [Test]
        public void RegisterNetworking_ResolvesTransportFactoryAndClient_FromABareContainer()
        {
            var builder = new ContainerBuilder();
            builder.RegisterNetworking();
            builder.Register<NoAuth>(Lifetime.Singleton).As<IAuthProvider>();

            using var container = builder.Build();

            Assert.That(container.Resolve<ITransportFactory>(), Is.InstanceOf<DefaultTransportFactory>());
            Assert.That(container.Resolve<NetworkClient>(), Is.Not.Null);
        }

        [Test]
        public void RegisterNetworking_Protobuf_ResolvesTheProtobufCodec()
        {
            var builder = new ContainerBuilder();
            builder.RegisterNetworking(encoding: WireEncoding.Protobuf);
            builder.Register<NoAuth>(Lifetime.Singleton).As<IAuthProvider>();

            using var container = builder.Build();

            Assert.That(container.Resolve<Cuvara.Netcode.Codec.IWireCodec>(),
                Is.InstanceOf<Cuvara.Netcode.Codec.ProtobufWireCodec>());
        }
        /// <summary>
        /// A fault-injecting / test double factory, the shape every consumer that wants to
        /// substitute a transport writes. It never creates anything: the tests resolve it,
        /// they do not connect.
        /// </summary>
        private sealed class FakeTransportFactory : ITransportFactory
        {
            public ITransport Create(TransportKind kind) =>
                throw new System.NotSupportedException("the tests never open a transport");
        }

        private sealed class RecordingLog : Cuvara.Netcode.Diagnostics.INetLog
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, System.Exception exception = null) { }
        }

        /// <summary>
        /// The regression: a consumer supplying its own transport factory must get a
        /// container that BUILDS. Before the <c>transports</c> parameter existed the only
        /// way to try was a second <c>Register&lt;ITransportFactory&gt;</c> lambda, which
        /// made <c>ContainerBuilder.Build()</c> throw
        /// <c>VContainerException: Conflict implementation type … FuncInstanceProvider</c>
        /// and took the whole scene down at <c>LifetimeScope.Awake</c> (Reconnect Policy
        /// Demo player against the live backend, 2026-09-07).
        /// </summary>
        [Test]
        public void RegisterNetworking_WithACustomTransportFactory_BuildsTheContainer()
        {
            var builder = new ContainerBuilder();
            builder.RegisterNetworking(transports: new FakeTransportFactory());
            builder.Register<NoAuth>(Lifetime.Singleton).As<IAuthProvider>();

            Assert.That(() => builder.Build(), Throws.Nothing);
        }

        [Test]
        public void RegisterNetworking_WithACustomTransportFactory_ResolvesThatFactoryAndTheClient()
        {
            var custom = new FakeTransportFactory();

            var builder = new ContainerBuilder();
            builder.RegisterNetworking(transports: custom);
            builder.Register<NoAuth>(Lifetime.Singleton).As<IAuthProvider>();

            using var container = builder.Build();

            Assert.That(container.Resolve<ITransportFactory>(), Is.SameAs(custom));
            Assert.That(container.Resolve<NetworkClient>(), Is.Not.Null);
        }

        [Test]
        public void RegisterNetworking_WithoutATransportFactory_StillResolvesTheDefaultOne()
        {
            var builder = new ContainerBuilder();
            builder.RegisterNetworking();
            builder.Register<NoAuth>(Lifetime.Singleton).As<IAuthProvider>();

            using var container = builder.Build();

            Assert.That(container.Resolve<ITransportFactory>(), Is.InstanceOf<DefaultTransportFactory>());
            Assert.That(container.Resolve<NetworkClient>(), Is.Not.Null);
        }

        /// <summary>
        /// The codec and the log take the same route, so the same substitution works and
        /// the same second-registration trap applies to them.
        /// </summary>
        [Test]
        public void RegisterNetworking_WithACustomCodecAndLog_ResolvesBoth()
        {
            var codec = new ProtobufWireCodec();
            var log = new RecordingLog();

            var builder = new ContainerBuilder();
            // encoding says Json and codec says Protobuf: the explicit instance wins.
            builder.RegisterNetworking(encoding: WireEncoding.Json, codec: codec, log: log);
            builder.Register<NoAuth>(Lifetime.Singleton).As<IAuthProvider>();

            using var container = builder.Build();

            Assert.That(container.Resolve<IWireCodec>(), Is.SameAs(codec));
            Assert.That(container.Resolve<Cuvara.Netcode.Diagnostics.INetLog>(), Is.SameAs(log));
            Assert.That(container.Resolve<NetworkClient>(), Is.Not.Null);
        }

        /// <summary>
        /// Pins the reason the parameter exists: registering the interface a second time
        /// is not an override, it is a build failure. If a future VContainer stops
        /// throwing here, the XML docs on <c>RegisterNetworking</c> need rewriting.
        /// </summary>
        [Test]
        public void RegisteringTheTransportFactoryAgainAfterwards_FailsTheContainerBuild()
        {
            var custom = new FakeTransportFactory();

            var builder = new ContainerBuilder();
            builder.RegisterNetworking();
            builder.Register<ITransportFactory>(_ => custom, Lifetime.Singleton);
            builder.Register<NoAuth>(Lifetime.Singleton).As<IAuthProvider>();

            Assert.That(() => builder.Build(), Throws.TypeOf<VContainerException>());
        }
    }
}
#endif
