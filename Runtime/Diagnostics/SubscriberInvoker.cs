using System;
using System.Threading;

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
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
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
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log.Warn($"{eventName} subscriber failed ({ex.GetType().FullName}): {ex}");
                }
            }
        }
    }
}
