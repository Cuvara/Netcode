using System;

namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>client -> game server (16). Opens the sealed-session handshake.</summary>
    /// <remarks>
    /// Travels in the clear. There is no key yet — establishing one is what this message is
    /// for. The public half of an X25519 pair is safe to send in the clear by construction.
    /// </remarks>
    public sealed class SealedClientHello : IWireMessage
    {
        /// <summary>Ephemeral X25519 public key, 32 bytes.</summary>
        /// <remarks>
        /// EPHEMERAL PER CONNECTION. Reusing one across sessions forfeits the forward secrecy
        /// that is the entire reason ADR-22 supersedes the earlier derived-key scheme: with a
        /// fresh pair per connection, a long-lived secret obtained later cannot decrypt
        /// traffic recorded earlier.
        /// </remarks>
        public byte[] PublicKey { get; set; } = Array.Empty<byte>();
    }
}
