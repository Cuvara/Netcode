#if CUVARA_NETCODE_VCONTAINER
using Cuvara.Netcode.Auth;
using Cuvara.Netcode.Client;
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
    }
}
#endif
