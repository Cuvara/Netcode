using System;
using UnityEngine;

namespace Cuvara.Netcode.Diagnostics
{
    /// <summary>Routes networking logs to the Unity console under a <c>[Net]</c> prefix.</summary>
    public sealed class UnityNetLog : INetLog
    {
        private const string Prefix = "[Net] ";

        public void Info(string message) => Debug.Log(Prefix + message);

        public void Warn(string message) => Debug.LogWarning(Prefix + message);

        public void Error(string message, Exception exception = null)
        {
            Debug.LogError(Prefix + message);
            if (exception != null)
            {
                Debug.LogException(exception);
            }
        }
    }
}
