namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// Implemented by an <see cref="ITransport"/> that wraps another one (a test tap, a latency
    /// injector, a byte counter). Lets netcode find the transport underneath, so its KCP
    /// diagnostics keep working through the wrapper.
    /// </summary>
    /// <remarks>
    /// A wrapper that forwards <see cref="IFlushableTransport"/> still has to implement that
    /// interface itself; this one only exposes what is wrapped.
    /// </remarks>
    public interface ITransportDecorator
    {
        /// <summary>The wrapped transport.</summary>
        ITransport Inner { get; }
    }

    /// <summary>Helpers for walking a chain of <see cref="ITransportDecorator"/> wrappers.</summary>
    public static class TransportDecorators
    {
        // A cycle is a bug in the wrapper, but a diagnostics read must not hang on it.
        private const int MaxDepth = 16;

        /// <summary>
        /// Returns <paramref name="transport"/> or the first transport below it that is a
        /// <typeparamref name="T"/>, or null.
        /// </summary>
        public static T Unwrap<T>(ITransport transport) where T : class, ITransport
        {
            for (var depth = 0; transport != null && depth < MaxDepth; depth++)
            {
                if (transport is T match)
                {
                    return match;
                }

                transport = (transport as ITransportDecorator)?.Inner;
            }

            return null;
        }
    }
}
