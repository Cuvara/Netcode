using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;
using Google.Protobuf;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Connection;
using Cuvara.Netcode.Crypto;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Transport;
using Msg = Cuvara.Netcode.Protocol.Messages;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The sealed handshake and the sealing seam on <see cref="WireConnection"/>, driven
    /// through a scripted transport so the BYTES ON THE WIRE are what gets asserted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserting through the API — "the connection says it is sealed" — would pass against a
    /// connection that sets a flag and writes cleartext. Every claim here is checked against
    /// what the transport actually received.
    /// </para>
    /// <para>
    /// The server side is reproduced inline from the same primitives rather than mocked. A
    /// mock agrees with whatever the client does, which is the one thing a handshake test
    /// must not do.
    /// </para>
    /// </remarks>
    public class SealedTransportTests
    {
        private const string Jti = "transport-jti-0001";
        private const string Secret = "transport-join-secret";

        // ------------------------------------------------------------------ fixtures

        /// <summary>
        /// A transport that plays the SERVER, not a script. When the client writes its hello
        /// it computes the real reply from the same primitives and queues it immediately.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Answering inline is what lets every test here run without yielding. EditMode does
        /// not pump the player loop, so a test that needs a continuation to resume is a test
        /// about the runner rather than about the connection — the trap this package already
        /// documented for UniTask realtime delays.
        /// </para>
        /// <para>
        /// It is also not a mock. A mock agrees with whatever the client does, which is the
        /// one thing a handshake test must not do; this derives its keys independently and
        /// the test opens the client's traffic with them.
        /// </para>
        /// </remarks>
        private sealed class RespondingTransport : ITransport
        {
            private readonly Queue<byte[]> _inbound = new Queue<byte[]>();

            private readonly string _secret;
            private readonly string _jti;
            private readonly string _error;
            private readonly bool _substituteKey;
            private readonly bool _closeInsteadOfAnswering;

            public readonly List<byte[]> Written = new List<byte[]>();

            /// <summary>The server's client-to-server key, once the handshake has run.</summary>
            public byte[] ClientToServer { get; private set; }

            /// <summary>The server's server-to-client key, once the handshake has run.</summary>
            public byte[] ServerToClient { get; private set; }

            public RespondingTransport(string secret, string jti, string error = null,
                                       bool substituteKey = false, bool closeInsteadOfAnswering = false)
            {
                _secret = secret;
                _jti = jti;
                _error = error;
                _substituteKey = substituteKey;
                _closeInsteadOfAnswering = closeInsteadOfAnswering;
            }

            public string RemoteEndPoint { get { return "responding"; } }
            public bool IsConnected { get { return true; } }

            public void Enqueue(byte[] body) { _inbound.Enqueue(body); }

            public UniTask ConnectAsync(string host, int port, CancellationToken ct)
            {
                return UniTask.CompletedTask;
            }

            public UniTask<byte[]> ReadFrameAsync(CancellationToken ct)
            {
                // A clean EOF when there is nothing staged. Every test here stages what it
                // needs first, so this never stalls the suite.
                return UniTask.FromResult(_inbound.Count > 0 ? _inbound.Dequeue() : null);
            }

            public UniTask WriteFrameAsync(byte[] body, CancellationToken ct)
            {
                Written.Add(body);

                if (body.Length > 0 && body[0] == 0x08)
                {
                    var envelope = RpgMmo.Wire.V1.Envelope.Parser.ParseFrom(body);
                    if (envelope.Type == (uint)MsgType.SealedClientHello) Answer(envelope.Payload);
                }

                return UniTask.CompletedTask;
            }

            private void Answer(Google.Protobuf.ByteString payload)
            {
                if (_closeInsteadOfAnswering) return;

                byte[] clientPublic = RpgMmo.Wire.V1.SealedClientHello.Parser
                                          .ParseFrom(payload).PublicKey.ToByteArray();

                SealedKeyPair server = SealedKeyPair.Generate();

                byte[] shared;
                if (!server.TryAgree(clientPublic, out shared))
                    throw new InvalidOperationException("fixture could not agree with the client's key");

                byte[] transcript = SealedHandshake.Transcript(_jti, clientPublic, server.Public);

                byte[] c2s, s2c;
                SealedCrypto.DeriveDirectionKeys(shared, transcript, out c2s, out s2c);
                ClientToServer = c2s;
                ServerToClient = s2c;

                // Built from the Protobuf types directly, exactly as the real server does.
                // The CLIENT codec has no encoder for a server hello — it only ever decodes
                // one — and adding one so a fixture could call it would be production code
                // whose only caller is a test.
                var hello = new RpgMmo.Wire.V1.SealedServerHello
                {
                    // A man in the middle advertises a key it did not use, and forwards the
                    // binding it read off the wire.
                    PublicKey = Google.Protobuf.ByteString.CopyFrom(
                        _substituteKey ? SealedKeyPair.Generate().Public : server.Public),
                    Binding = Google.Protobuf.ByteString.CopyFrom(
                        new SealedTranscriptSigner(_secret, _jti).Sign(transcript)),
                    Error = _error ?? string.Empty,
                };

                var envelope = new RpgMmo.Wire.V1.Envelope
                {
                    Type = (uint)MsgType.SealedServerHello,
                    Payload = Google.Protobuf.ByteString.CopyFrom(hello.ToByteArray()),
                };

                _inbound.Enqueue(envelope.ToByteArray());
            }

            public void Close() { }
            public void Dispose() { }
        }

        private sealed class SilentLog : INetLog
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception exception = null) { }
        }

        private static WireConnection NewConnection(RespondingTransport transport)
        {
            return new WireConnection("test", transport, new ProtobufWireCodec(),
                                      new NetworkSettings(), new SilentLog());
        }

        /// <summary>
        /// Build a frame the SERVER would send. The client codec has encoders only for the
        /// types a client sends, so a fixture playing the server cannot go through it — which
        /// is correct, and is why this builds the Envelope directly.
        /// </summary>
        private static byte[] ServerEnvelope(MsgType type, Google.Protobuf.IMessage payload)
        {
            return new RpgMmo.Wire.V1.Envelope
            {
                Type = (uint)type,
                Payload = ByteString.CopyFrom(payload.ToByteArray()),
            }.ToByteArray();
        }

        // ------------------------------------------------------------------- the tests

        /// <summary>
        /// The whole thing: hello out, real reply back, sessions installed, and then a frame
        /// the SERVER'S OWN KEY opens. Nothing here trusts the connection's own report.
        /// </summary>
        /// <remarks>
        /// Asserting through the API — "the connection says it is sealed" — would pass against
        /// a connection that sets a flag and writes cleartext. The claim is checked against
        /// the bytes the transport received.
        /// </remarks>
        [UnityTest]
        public IEnumerator Handshake_CompletesAndEverythingAfterwardsIsSealed()
        {
            var transport = new RespondingTransport(Secret, Jti);
            WireConnection connection = NewConnection(transport);

            var handshake = SealedHandshakeClient.RunAsync(connection, Jti, Secret, CancellationToken.None).Preserve();
            yield return Await(handshake);

            SealedHandshakeClient.Result result = handshake.GetAwaiter().GetResult();
            Assert.AreEqual(SealedHandshakeClient.Outcome.Ok, result.Outcome, result.Error);
            Assert.IsTrue(result.BindingVerified, "this test supplied the secret, so it must have verified");
            Assert.IsTrue(connection.IsSealed);

            var send = connection.SendFrameAsync(
                MsgType.Input, new Msg.InputMessage { Tick = 1, MoveX = 0.5f }, CancellationToken.None).Preserve();
            yield return Await(send);

            byte[] onTheWire = transport.Written[transport.Written.Count - 1];
            Assert.AreEqual(SealedFrame.Marker, onTheWire[0],
                "the frame after the handshake is still cleartext — sealing is not on the write path");

            var serverSide = new SealedSession(new SealedAead(transport.ClientToServer), new StrictMonotonicSequence());
            byte[] plaintext;
            Assert.AreEqual(SealedOpenResult.Ok, serverSide.Open(onTheWire, out plaintext),
                "the server's own key must open it — which a self-consistent client would also satisfy, "
                + "so the key here is derived independently by the fixture");

            WireFrame decoded = new ProtobufWireCodec().DecodeBody(plaintext);
            Assert.AreEqual(MsgType.Input, decoded.Type);
        }

        /// <summary>
        /// A cleartext frame arriving after the handshake must be REFUSED. This is the
        /// downgrade defence, and it is the one that silently does nothing if it is missing.
        /// </summary>
        [UnityTest]
        public IEnumerator ACleartextFrameOnASealedConnectionIsRefused()
        {
            var transport = new RespondingTransport(Secret, Jti);
            WireConnection connection = NewConnection(transport);

            var handshake = SealedHandshakeClient.RunAsync(connection, Jti, Secret, CancellationToken.None).Preserve();
            yield return Await(handshake);
            Assert.IsTrue(handshake.GetAwaiter().GetResult().Ok);

            // A perfectly well-formed cleartext Envelope. Accepting it would let anyone who
            // can inject one frame speak to this client unauthenticated, while the session
            // still looks healthy from both ends.
            byte[] cleartext = ServerEnvelope(
                MsgType.Kick, new RpgMmo.Wire.V1.KickMessage { Reason = "injected" });
            Assert.AreEqual(0x08, cleartext[0], "fixture check: this really is a cleartext Envelope");

            transport.Enqueue(cleartext);

            var ex = Assert.Throws<WireCodecException>(
                () => connection.ReceiveFrameAsync(CancellationToken.None).GetAwaiter().GetResult());

            StringAssert.Contains("downgrade", ex.Message);
        }

        /// <summary>A replayed sealed frame is refused, and the connection counts it.</summary>
        [UnityTest]
        public IEnumerator AReplayedFrameIsRefused()
        {
            var transport = new RespondingTransport(Secret, Jti);
            WireConnection connection = NewConnection(transport);

            var handshake = SealedHandshakeClient.RunAsync(connection, Jti, Secret, CancellationToken.None).Preserve();
            yield return Await(handshake);
            Assert.IsTrue(handshake.GetAwaiter().GetResult().Ok);

            var serverOutbound = new SealedSession(
                new SealedAead(transport.ServerToClient), new StrictMonotonicSequence());
            byte[] frame = serverOutbound.Seal(
                ServerEnvelope(MsgType.Kick, new RpgMmo.Wire.V1.KickMessage { Reason = "bye" }));

            transport.Enqueue(frame);
            WireFrame? first = connection.ReceiveFrameAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(MsgType.Kick, first.Value.Type);
            Assert.AreEqual(0UL, connection.SealedRejectedTotal);

            transport.Enqueue(frame);
            Assert.Throws<WireCodecException>(
                () => connection.ReceiveFrameAsync(CancellationToken.None).GetAwaiter().GetResult());

            Assert.AreEqual(1UL, connection.SealedRejectedTotal);
        }

        /// <summary>
        /// A man in the middle who advertises a key it did not use is caught — and no session
        /// is installed, so the connection cannot carry on unsealed.
        /// </summary>
        [UnityTest]
        public IEnumerator ASubstitutedServerKeyIsRefusedAndInstallsNothing()
        {
            var transport = new RespondingTransport(Secret, Jti, substituteKey: true);
            WireConnection connection = NewConnection(transport);

            var handshake = SealedHandshakeClient.RunAsync(connection, Jti, Secret, CancellationToken.None).Preserve();
            yield return Await(handshake);

            SealedHandshakeClient.Result result = handshake.GetAwaiter().GetResult();

            Assert.AreEqual(SealedHandshakeClient.Outcome.BindingRejected, result.Outcome);
            Assert.IsFalse(connection.IsSealed, "a refused handshake must leave the connection unsealed");
        }

        /// <summary>
        /// Without a secret the exchange completes and reports that it proved nothing. This is
        /// what a shipped client does today.
        /// </summary>
        [UnityTest]
        public IEnumerator WithoutASecret_ItCompletesAndReportsBindingUnverified()
        {
            var transport = new RespondingTransport(Secret, Jti);
            WireConnection connection = NewConnection(transport);

            // null secret — what a shipped client passes, because it must not carry one.
            var handshake = SealedHandshakeClient.RunAsync(connection, Jti, null, CancellationToken.None).Preserve();
            yield return Await(handshake);

            SealedHandshakeClient.Result result = handshake.GetAwaiter().GetResult();

            Assert.AreEqual(SealedHandshakeClient.Outcome.Ok, result.Outcome);
            Assert.IsFalse(result.BindingVerified,
                "a client that cannot verify must say so, or a caller will report 'connected securely'");
            Assert.IsTrue(connection.IsSealed);
        }

        /// <summary>The server's own refusal is carried through, not swallowed.</summary>
        [UnityTest]
        public IEnumerator AServerRefusalIsReported()
        {
            var transport = new RespondingTransport(Secret, Jti, error: "not_configured");
            WireConnection connection = NewConnection(transport);

            var handshake = SealedHandshakeClient.RunAsync(connection, Jti, Secret, CancellationToken.None).Preserve();
            yield return Await(handshake);

            SealedHandshakeClient.Result result = handshake.GetAwaiter().GetResult();

            Assert.AreEqual(SealedHandshakeClient.Outcome.ServerRefused, result.Outcome);
            Assert.AreEqual("not_configured", result.Error);
            Assert.IsFalse(connection.IsSealed);
        }

        /// <summary>A server that closes instead of answering is reported, not hung on.</summary>
        [UnityTest]
        public IEnumerator AClosedConnectionDuringTheHandshakeIsReported()
        {
            var transport = new RespondingTransport(Secret, Jti, closeInsteadOfAnswering: true);
            WireConnection connection = NewConnection(transport);

            var handshake = SealedHandshakeClient.RunAsync(connection, Jti, Secret, CancellationToken.None).Preserve();
            yield return Await(handshake);

            SealedHandshakeClient.Result result = handshake.GetAwaiter().GetResult();

            Assert.AreEqual(SealedHandshakeClient.Outcome.NoServerHello, result.Outcome);
            Assert.IsFalse(connection.IsSealed);
        }

        /// <summary>
        /// There is no uninstall and no second install. A connection that can revert to
        /// cleartext is a connection an attacker can talk down to cleartext.
        /// </summary>
        [Test]
        public void ASealedSessionCannotBeInstalledTwice()
        {
            WireConnection connection = NewConnection(new RespondingTransport(Secret, Jti));

            var key = new byte[SealedCrypto.KeySize];
            Func<SealedSession> session =
                () => new SealedSession(new SealedAead(key), new StrictMonotonicSequence());

            connection.InstallSealedSession(session(), session());

            Assert.Throws<InvalidOperationException>(
                () => connection.InstallSealedSession(session(), session()));
        }

        /// <summary>
        /// Drain a UniTask that the RespondingTransport completes synchronously, with a
        /// bounded yield loop rather than a blocking wait.
        /// </summary>
        /// <remarks>
        /// EditMode does not pump the player loop, so anything needing a real continuation
        /// would hang the suite rather than fail it — the trap this package already
        /// documented for UniTask realtime delays. The bound turns a genuine stall into a
        /// failure in frames.
        /// </remarks>
        private static IEnumerator Await<T>(UniTask<T> task)
        {
            for (int i = 0; i < 120 && task.Status == UniTaskStatus.Pending; i++) yield return null;
            Assert.AreNotEqual(UniTaskStatus.Pending, task.Status,
                "the handshake did not complete; the fixture answers inline, so a pending task "
                + "means something is genuinely waiting rather than that the loop is unpumped");
        }

        private static IEnumerator Await(UniTask task)
        {
            for (int i = 0; i < 120 && task.Status == UniTaskStatus.Pending; i++) yield return null;
            Assert.AreNotEqual(UniTaskStatus.Pending, task.Status, "the send did not complete");
        }
    }
}
