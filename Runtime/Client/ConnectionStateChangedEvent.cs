using System;

namespace Cuvara.Netcode.Client
{
    /// <summary>
    /// Raised by <see cref="NetworkClient"/> whenever the connection state changes.
    /// UI systems subscribe to this to show connecting spinners, disconnected overlays,
    /// reconnecting progress, etc.
    /// </summary>
    public readonly struct ConnectionStateChangedEvent
    {
        /// <summary>The state we just left.</summary>
        public readonly NetworkClientState Previous;

        /// <summary>The state we just entered.</summary>
        public readonly NetworkClientState Current;

        /// <summary>
        /// Human-readable reason for the transition, when available.
        /// Empty for normal transitions (Authenticating → Assigning).
        /// Populated for error transitions (InWorld → Ended: "server_shutdown").
        /// </summary>
        public readonly string Reason;

        public ConnectionStateChangedEvent(NetworkClientState previous, NetworkClientState current, string reason = "")
        {
            Previous = previous;
            Current = current;
            Reason = reason ?? "";
        }

        public override string ToString() => $"{Previous} → {Current}" + (string.IsNullOrEmpty(Reason) ? "" : $" ({Reason})");
    }

    /// <summary>
    /// Observable connection state. <see cref="NetworkClient"/> exposes this so UI and
    /// game systems can react to state transitions without polling.
    /// </summary>
    public interface IConnectionStateObserver
    {
        /// <summary>Current connection state.</summary>
        NetworkClientState State { get; }

        /// <summary>Fired on every state transition, on the thread that caused it.</summary>
        event Action<ConnectionStateChangedEvent> StateChanged;
    }
}
