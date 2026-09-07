using System;
using NUnit.Framework;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// <see cref="NetworkClient.Generation"/> is the operation counter a diagnostics overlay reads;
    /// it starts at zero and every public entry point that changes what the client is connected
    /// to advances it — including the two that need no backend.
    /// </summary>
    [TestFixture]
    public sealed class NetworkClientGenerationTests
    {
        private sealed class NeverFactory : ITransportFactory
        {
            public ITransport Create(TransportKind kind) => throw new InvalidOperationException("no transport in this test");
        }

        private sealed class SilentLog : INetLog
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception exception = null) { }
        }

        [Test]
        public void Generation_StartsAtZero_AndDisconnectAndDisposeEachAdvanceIt()
        {
            var client = new NetworkClient(new NetworkSettings(), new NeverFactory(), new JsonWireCodec(), new SilentLog());
            Assert.That(client.Generation, Is.EqualTo(0));

            client.Disconnect();
            Assert.That(client.Generation, Is.EqualTo(1), "a user close is an operation");
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Ended));

            client.Disconnect();
            Assert.That(client.Generation, Is.EqualTo(2), "every call, even a redundant one, is a new operation");

            client.Dispose();
            Assert.That(client.Generation, Is.EqualTo(3));
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Disconnected));
        }
    }
}
