using System;

namespace Cuvara.Netcode.Diagnostics
{
    /// <summary>Invokes each event subscriber independently and records actionable failures.</summary>
    internal static class SubscriberInvoker
    {
        public static void InvokeSafely<T>(Action<T> handlers, T value, string eventName, INetLog log)
        {
            if (handlers == null) return;
            foreach (Action<T> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(value);
                }
                catch (Exception ex)
                {
                    // This exception came from user code, not from the caller's
                    // cancellation token. Treat it as a subscriber fault so it cannot
                    // silently terminate a transport or reconnect loop.
                    log.Warn($"{eventName} subscriber failed ({ex.GetType().FullName}): {ex}");
                }
            }
        }

        public static void InvokeSafely(Action handlers, string eventName, INetLog log)
        {
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    log.Warn($"{eventName} subscriber failed ({ex.GetType().FullName}): {ex}");
                }
            }
        }
    }
}
