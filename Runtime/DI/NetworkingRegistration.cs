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
    /// <para>
    /// Call from <c>GameLifetimeScope.Configure</c> so the client outlives scene
    /// loads — a gameplay socket must survive a scene transition, and a scene-scoped
    /// registration would drop it. Nothing here is a singleton in the static sense;
    /// the container owns every instance.
    /// </para>
    /// <para>
    /// <b>Substitute a dependency through the parameters of this method, never by
    /// registering it again afterwards.</b> See the <paramref name="transports"/>
    /// remarks for why a second registration cannot work.
    /// </para>
    /// </remarks>
    public static class NetworkingRegistration
    {
        /// <summary>
        /// Registers the networking layer: <see cref="NetworkSettings"/>, an
        /// <see cref="INetLog"/>, an <see cref="ITransportFactory"/>, an
        /// <see cref="IWireCodec"/> and <see cref="NetworkClient"/> — exactly one
        /// registration of each.
        /// </summary>
        /// <param name="settings">
        /// The settings instance the client reads. A fresh <see cref="NetworkSettings"/>
        /// when null. Registered as an instance, so a caller that keeps the reference can
        /// mutate it at runtime and the client sees the change.
        /// </param>
        /// <param name="encoding">
        /// Which codec to register. Defaults to <see cref="WireEncoding.Json"/> so an
        /// existing caller's behaviour does not change on upgrade, even though Protobuf
        /// is the backend's default and is ~81% smaller once interning and the entity
        /// enum are counted. Both servers accept either and mirror the encoding of the
        /// first frame they receive per connection, so this is a client-side choice that
        /// needs no server change. Ignored when <paramref name="codec"/> is given.
        /// </param>
        /// <param name="transports">
        /// The transport factory the client creates sockets with. Null registers
        /// <see cref="DefaultTransportFactory"/>. Pass an instance to wrap or replace it —
        /// a fault-injecting factory in a sample, a fake in a test, a factory carrying a
        /// per-session transport key in a shipping build.
        /// <para>
        /// <b>Passing it here is the only supported way to substitute one.</b> Registering
        /// <see cref="ITransportFactory"/> again after calling this method does not
        /// override it and does not silently win: since 0.31.1 the default is registered
        /// through a factory lambda (<c>Register&lt;ITransportFactory&gt;(_ =&gt; …)</c>),
        /// because VContainer does not honour the C# default value on
        /// <see cref="DefaultTransportFactory"/>'s <c>transportKey</c> constructor
        /// parameter and would fail to resolve a <c>System.String</c>. Two lambda
        /// registrations of the same interface share the implementation type
        /// <c>VContainer.Internal.FuncInstanceProvider</c>, so VContainer's duplicate check
        /// fires and the <i>whole container fails to build</i>:
        /// <c>VContainerException: Conflict implementation type : Registration
        /// ITransportFactory ContractTypes=[] Singleton
        /// VContainer.Internal.FuncInstanceProvider</c>. The caller's own registration is
        /// what triggers it, so the failure looks like a bug in the caller's scope rather
        /// than in this method.
        /// </para>
        /// </param>
        /// <param name="codec">
        /// The wire codec. Null selects one from <paramref name="encoding"/>; a non-null
        /// value wins over it. Same rule as <paramref name="transports"/>: substitute
        /// here, never with a second registration.
        /// </param>
        /// <param name="log">
        /// The diagnostics sink. Null registers <see cref="UnityNetLog"/>. Same rule as
        /// <paramref name="transports"/>.
        /// </param>
        public static IContainerBuilder RegisterNetworking(
            this IContainerBuilder builder,
            NetworkSettings settings = null,
            WireEncoding encoding = WireEncoding.Json,
            ITransportFactory transports = null,
            IWireCodec codec = null,
            INetLog log = null)
        {
            builder.RegisterInstance(settings ?? new NetworkSettings());

            if (log != null)
            {
                // RegisterInstance, not a lambda: the implementation type is the caller's
                // concrete type, so it can never collide with another registration the way
                // two FuncInstanceProvider registrations do.
                builder.RegisterInstance<INetLog>(log);
            }
            else
            {
                builder.Register<UnityNetLog>(Lifetime.Singleton).As<INetLog>();
            }

            if (transports != null)
            {
                builder.RegisterInstance<ITransportFactory>(transports);
            }
            else
            {
                // A factory lambda, not Register<DefaultTransportFactory>: its constructor
                // takes `string transportKey = null`, and VContainer does not honour C#
                // default values — it tried to resolve a `string` and failed with
                // "No such registration of type: System.String" the first time a scope
                // resolved NetworkClient through this registration (MainScene,
                // 2026-09-07). Every sample built the client by hand, so it never showed.
                builder.Register<ITransportFactory>(_ => new DefaultTransportFactory(), Lifetime.Singleton);
            }

            if (codec != null)
            {
                builder.RegisterInstance<IWireCodec>(codec);
            }
            else
            {
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
            }

            builder.Register<NetworkClient>(Lifetime.Singleton);

            return builder;
        }
    }
}
