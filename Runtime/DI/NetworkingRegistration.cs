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
        /// <summary>
        /// Registers the networking layer, optionally choosing the wire encoding.
        /// </summary>
        /// <param name="encoding">
        /// Which codec to register. Defaults to <see cref="WireEncoding.Json"/> so an
        /// existing caller's behaviour does not change on upgrade, even though Protobuf
        /// is the backend's default and is ~81% smaller once interning and the entity
        /// enum are counted. Both servers accept either and mirror the encoding of the
        /// first frame they receive per connection, so this is a client-side choice that
        /// needs no server change.
        /// </param>
        public static IContainerBuilder RegisterNetworking(
            this IContainerBuilder builder,
            NetworkSettings settings = null,
            WireEncoding encoding = WireEncoding.Json)
        {
            builder.RegisterInstance(settings ?? new NetworkSettings());

            builder.Register<UnityNetLog>(Lifetime.Singleton).As<INetLog>();
            builder.Register<DefaultTransportFactory>(Lifetime.Singleton).As<ITransportFactory>();

            switch (encoding)
            {
                case WireEncoding.Protobuf:
                    builder.Register<ProtobufWireCodec>(Lifetime.Singleton).As<IWireCodec>();
                    break;

                case WireEncoding.Json:
                case WireEncoding.Unknown:
                default:
                    // Unknown is treated as the default rather than rejected: it is the
                    // enum's zero value, so a caller that never set it lands here.
                    builder.Register<JsonWireCodec>(Lifetime.Singleton).As<IWireCodec>();
                    break;
            }

            builder.Register<NetworkClient>(Lifetime.Singleton);

            return builder;
        }
    }
}
