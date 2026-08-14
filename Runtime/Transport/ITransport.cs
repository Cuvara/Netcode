using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// A reliable, ordered, message-framed link to one peer.
    /// </summary>
    /// <remarks>
    /// The codec only needs framed messages in order, which TCP and KCP both
    /// provide, so nothing above this interface knows which transport it is on —
    /// mirroring <c>ITransportConnection</c> on the game server.
    /// </remarks>
    public interface ITransport : IDisposable
    {
        /// <summary>Peer address, for logging. Empty before <see cref="ConnectAsync"/>.</summary>
        string RemoteEndPoint { get; }

        bool IsConnected { get; }

        UniTask ConnectAsync(string host, int port, CancellationToken cancellationToken);

        /// <summary>
        /// Reads the next frame body, without the length prefix. Returns null on a
        /// clean EOF (the peer half-closed), which is the normal end of a session
        /// and not an error.
        /// </summary>
        UniTask<byte[]> ReadFrameAsync(CancellationToken cancellationToken);

        UniTask WriteFrameAsync(byte[] body, CancellationToken cancellationToken);

        /// <summary>Closes the link. Idempotent.</summary>
        void Close();
    }
}
