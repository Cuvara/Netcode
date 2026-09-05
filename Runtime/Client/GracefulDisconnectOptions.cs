using System;

namespace Cuvara.Netcode.Client
{
    /// <summary>
    /// Options for <see cref="NetworkClient.DisconnectGracefullyAsync"/>.
    /// </summary>
    public sealed class GracefulDisconnectOptions
    {
        /// <summary>
        /// Maximum time to wait for the server to acknowledge the last input.
        /// Default 2 seconds — enough for 2 full RTTs on a bad mobile connection.
        /// </summary>
        public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Whether to send a final MsgDisconnect after the ack wait.
        /// Default true — the server can then save immediately rather than
        /// waiting for the hold TTL.
        /// </summary>
        public bool SendDisconnectMessage { get; set; } = true;

        /// <summary>Default options: 2s ack timeout, send disconnect message.</summary>
        public static GracefulDisconnectOptions Default => new();
    }
}
