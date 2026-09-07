using System;
using System.Collections.Generic;
using System.Threading;
using Cuvara.Netcode.Transport;
using Cysharp.Threading.Tasks;

namespace Cuvara.Netcode.Samples.ReconnectPolicyDemo
{
    /// <summary>
    /// An <see cref="ITransportFactory"/> that hands out real transports wrapped so the demo can
    /// break them on purpose: close the newest one under the client's feet, or swallow every
    /// inbound frame so the heartbeat starves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client never learns it is being lied to — that is the point. A killed transport looks
    /// like a NAT expiry or a Wi-Fi hand-off (<c>PeerClosed</c> / <c>TransportError</c>); a
    /// blackholed one looks like a link that still accepts writes but delivers nothing, which is
    /// exactly the case <c>PongTimeout</c> exists for (<c>HeartbeatTimeout</c>). Both are causes
    /// <c>ReconnectPolicy</c> reconnects on; a user close is not, and this factory has no button
    /// for it because <c>NetworkClient.Disconnect()</c> is the real thing.
    /// </para>
    /// <para>
    /// The newest transport is the game-session one: <c>NetworkClient</c> creates the gateway
    /// transport first and the session transport after the assignment. A reconnect creates fresh
    /// transports, so a blackhole set on the old one dies with it and never leaks into the new
    /// session.
    /// </para>
    /// </remarks>
    public sealed class ChaosTransportFactory : ITransportFactory
    {
        private readonly ITransportFactory _inner;
        private readonly List<ChaosTransport> _created = new List<ChaosTransport>();

        public ChaosTransportFactory(ITransportFactory inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <summary>Transports created so far, all kinds. A reconnect adds two more.</summary>
        public int CreatedCount => _created.Count;

        /// <summary>The newest transport, normally the game-session one; null before any connect.</summary>
        public ChaosTransport Newest => _created.Count > 0 ? _created[_created.Count - 1] : null;

        public ITransport Create(TransportKind kind)
        {
            var transport = new ChaosTransport(_inner.Create(kind), kind);
            _created.Add(transport);
            return transport;
        }

        /// <summary>Closes the newest transport: the next read reports end-of-stream or a fault.</summary>
        /// <returns>False when nothing is connected.</returns>
        public bool KillNewest()
        {
            var target = Newest;
            if (target == null || !target.IsConnected) return false;

            target.Kill();
            return true;
        }

        /// <summary>Makes the newest transport deliver no more inbound frames; writes still go out.</summary>
        public bool BlackholeNewest()
        {
            var target = Newest;
            if (target == null || !target.IsConnected) return false;

            target.Blackhole = true;
            return true;
        }
    }

    /// <summary>A real transport with two switches: <see cref="Kill"/> and <see cref="Blackhole"/>.</summary>
    public sealed class ChaosTransport : ITransport
    {
        private readonly ITransport _inner;
        private readonly CancellationTokenSource _blackhole = new CancellationTokenSource();
        private bool _killed;

        public ChaosTransport(ITransport inner, TransportKind kind)
        {
            _inner = inner;
            Kind = kind;
        }

        public TransportKind Kind { get; }

        /// <summary>When set, reads park forever; the heartbeat then sees no pong and closes the link.</summary>
        public bool Blackhole { get; set; }

        /// <summary>True after <see cref="Kill"/>.</summary>
        public bool Killed => _killed;

        public string RemoteEndPoint => _inner.RemoteEndPoint;

        public bool IsConnected => !_killed && _inner.IsConnected;

        public UniTask ConnectAsync(string host, int port, CancellationToken cancellationToken) =>
            _inner.ConnectAsync(host, port, cancellationToken);

        public async UniTask<byte[]> ReadFrameAsync(CancellationToken cancellationToken)
        {
            if (Blackhole)
            {
                // Park until the client gives up on this transport. The real bytes keep arriving
                // underneath and are never read — which is what a dead link looks like from above.
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _blackhole.Token);
                await UniTask.WaitUntilCanceled(linked.Token);
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            return await _inner.ReadFrameAsync(cancellationToken);
        }

        public UniTask WriteFrameAsync(byte[] body, CancellationToken cancellationToken) =>
            _inner.WriteFrameAsync(body, cancellationToken);

        /// <summary>Closes the real transport so the client's reader sees the peer go away.</summary>
        public void Kill()
        {
            _killed = true;
            _inner.Close();
        }

        public void Close() => _inner.Close();

        public void Dispose()
        {
            _blackhole.Cancel();
            _blackhole.Dispose();
            _inner.Dispose();
        }
    }
}
