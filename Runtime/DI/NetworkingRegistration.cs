using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Transport;
using VContainer;

namespace Cuvara.Netcode.DI
{
    /// <summary>
    /// Registers the networking layer in a VContainer scope.
    /// </summary>
    /// <remarks>
    /// Call from <c>GameLifetimeScope.Configure</c> so the client outlives scene
    /// loads — a gameplay socket must survive a scene transition, and a scene-scoped
    /// registration would drop it. Nothing here is a singleton in the static sense;
    /// the container owns every instance.
    /// </remarks>
    public static class NetworkingRegistration
    {
        public static IContainerBuilder RegisterNetworking(this IContainerBuilder builder, NetworkSettings settings = null)
        {
            builder.RegisterInstance(settings ?? new NetworkSettings());

            builder.Register<UnityNetLog>(Lifetime.Singleton).As<INetLog>();
            builder.Register<DefaultTransportFactory>(Lifetime.Singleton).As<ITransportFactory>();

            // JSON is the only codec implemented. It is registered as the single
            // IWireCodec so that adding Protobuf is a one-line change here rather
            // than a change to every call site.
            builder.Register<JsonWireCodec>(Lifetime.Singleton).As<IWireCodec>();

            builder.Register<NetworkClient>(Lifetime.Singleton);

            return builder;
        }
    }
}
