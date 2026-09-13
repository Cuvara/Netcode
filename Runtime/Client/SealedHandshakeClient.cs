using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Connection;
using Cuvara.Netcode.Crypto;
using Cuvara.Netcode.Protocol;
using Msg = Cuvara.Netcode.Protocol.Messages;

namespace Cuvara.Netcode.Client
{
    /// <summary>
    /// Runs the sealed-session handshake over a <see cref="WireConnection"/> and installs the
    /// result. The mirror of the server's <c>SealedHandshakeServer</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs immediately after the join reply and before <see cref="WireConnection.Start"/>,
    /// so no frame is ever written half-sealed. Both hellos travel in the clear — there is no
    /// key yet, which is what they exist to establish.
    /// </para>
    /// <para>
    /// <b>Gameplay hop only.</b> The anchor is the join token's <c>jti</c>. The gateway hop
    /// has no jti at the point it would need one and requires a different anchor, which is
    /// still ADR-gated.
    /// </para>
    /// <para>
    /// All the cryptography lives in <see cref="SealedClientExchange"/>, which has no
    /// transport in it. What is left here is two frames and a failure policy.
    /// </para>
    /// </remarks>
    public static class SealedHandshakeClient
    {
        /// <summary>How the client-side handshake ended.</summary>
        public enum Outcome
        {
            /// <summary>Completed; both sessions installed on the connection.</summary>
            Ok = 0,

            /// <summary>The server closed, or answered with something other than a server hello.</summary>
            NoServerHello,

            /// <summary>The server's key was malformed or a low-order point.</summary>
            BadServerKey,

            /// <summary>The server's binding did not verify — a man in the middle, or the wrong secret.</summary>
            BindingRejected,

            /// <summary>The server refused, and said so in <c>SealedServerHello.error</c>.</summary>
            ServerRefused,

            /// <summary>The transport failed while the handshake was in flight.</summary>
            TransportFailed,

            /// <summary>
            /// The server's ADR-25 identity signature was required and did not check out.
            /// </summary>
            IdentityRejected,
        }

        /// <summary>The result, and what it does and does not prove.</summary>
        public readonly struct Result
        {
            internal Result(Outcome outcome, bool bindingVerified, string error)
                : this(outcome, bindingVerified, default(ServerIdentityResult), error)
            {
            }

            internal Result(
                Outcome outcome, bool bindingVerified, ServerIdentityResult identity, string error)
            {
                Outcome = outcome;
                BindingVerified = bindingVerified;
                Identity = identity;
                Error = error ?? string.Empty;
            }

            /// <summary>How it ended.</summary>
            public Outcome Outcome { get; }

            /// <summary>
            /// Whether the server's identity was PROVED, rather than merely encrypted to.
            /// </summary>
            /// <remarks>
            /// False with <see cref="Outcome"/> Ok is a real and expected state today: a
            /// shipped client holds no material to verify the binding with, so it gets
            /// confidentiality against a passive eavesdropper and nothing against an active
            /// one. Distinct from success on purpose, so a caller reporting "connected
            /// securely" has to have looked at it.
            /// </remarks>
            public bool BindingVerified { get; }

            /// <summary>
            /// What the handshake learned about the server's identity (ADR-25).
            /// </summary>
            /// <remarks>
            /// Unlike <see cref="BindingVerified"/>, this one CAN be true in a shipped client —
            /// but only where the gateway hop is authenticated, because that is the hop the
            /// identity key travels over. Read <c>Identity.Verified</c>, not
            /// <c>Identity.Checked</c>: the first is the conjunction that matters and the second
            /// is half of it.
            /// </remarks>
            public ServerIdentityResult Identity { get; }

            /// <summary>Local diagnostic. Never sent to the peer.</summary>
            public string Error { get; }

            /// <summary>Convenience for the common branch.</summary>
            public bool Ok { get { return Outcome == Outcome.Ok; } }
        }

        /// <summary>
        /// Perform the exchange and, on success, install both sessions on
        /// <paramref name="connection"/>.
        /// </summary>
        /// <param name="connection">In its handshake phase. Not yet started.</param>
        /// <param name="jti">The join token's <c>jti</c> claim.</param>
        /// <param name="joinTokenSecret">
        /// Material to verify the server's binding with, or null/empty to run without
        /// verification. <b>A shipped client passes null</b> — carrying this secret in a
        /// binary is the pre-shared-key mistake ADR-22 supersedes. Passing null is a
        /// deliberate, reported state, not a silent fallback: the result's
        /// <see cref="Result.BindingVerified"/> says so.
        /// </param>
        /// <remarks>
        /// <b>There is no cleartext fallback on any path.</b> Every failure returns without
        /// installing a session, and the caller must close the connection rather than
        /// continue. A protocol that can be talked down to cleartext will be.
        /// </remarks>
        public static UniTask<Result> RunAsync(
            WireConnection connection, string jti, string joinTokenSecret, CancellationToken cancellationToken)
        {
            return RunAsync(
                connection, jti, joinTokenSecret,
                serverIdentityKey: null,
                identityKeyHopAuthenticated: false,
                requireServerIdentity: false,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// As above, additionally checking the server's ADR-25 identity signature.
        /// </summary>
        /// <param name="serverIdentityKey">
        /// The identity key from <c>EnterWorldResponse.server_public_key</c>, or null from a
        /// gateway that predates ADR-25.
        /// </param>
        /// <param name="identityKeyHopAuthenticated">
        /// Whether the gateway hop that delivered that key was authenticated. <b>Not the
        /// gameplay hop</b> — this one is sealed by definition, and sealing it is not what makes
        /// the identity key trustworthy.
        /// </param>
        /// <param name="requireServerIdentity">
        /// Refuse rather than continue when no usable signature is offered.
        /// </param>
        public static async UniTask<Result> RunAsync(
            WireConnection connection,
            string jti,
            string joinTokenSecret,
            byte[] serverIdentityKey,
            bool identityKeyHopAuthenticated,
            bool requireServerIdentity,
            CancellationToken cancellationToken)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (string.IsNullOrEmpty(jti)) throw new ArgumentException("no jti", nameof(jti));

            SealedClientExchange exchange = string.IsNullOrEmpty(joinTokenSecret)
                ? SealedClientExchange.WithoutBindingVerification(jti)
                : new SealedClientExchange(jti, joinTokenSecret);

            WireFrame? reply;
            try
            {
                await connection.SendFrameAsync(
                    MsgType.SealedClientHello,
                    new Msg.SealedClientHello { PublicKey = exchange.CreateHello() },
                    cancellationToken);

                reply = await connection.ReceiveFrameAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new Result(Outcome.TransportFailed, false, ex.Message);
            }

            if (reply == null || reply.Value.Type != MsgType.SealedServerHello)
            {
                // Includes a clean EOF, which is what a server refusing on policy grounds
                // looks like from here — it closes rather than explaining.
                return new Result(Outcome.NoServerHello, false,
                    reply == null ? "connection closed during the sealed handshake"
                                  : "expected SealedServerHello, got " + reply.Value.Type);
            }

            var hello = reply.Value.Payload as Msg.SealedServerHello;
            if (hello == null)
                return new Result(Outcome.NoServerHello, false, "SealedServerHello carried no payload");

            SealedExchange result = exchange.AcceptServerHello(
                hello.PublicKey ?? Array.Empty<byte>(),
                hello.Binding ?? Array.Empty<byte>(),
                hello.Error,
                hello.ServerSignature ?? Array.Empty<byte>(),
                serverIdentityKey ?? Array.Empty<byte>(),
                identityKeyHopAuthenticated,
                requireServerIdentity);

            switch (result.Result)
            {
                case SealedExchangeResult.Ok:
                    connection.InstallSealedSession(inbound: result.Inbound, outbound: result.Outbound);
                    return new Result(Outcome.Ok, result.BindingVerified, result.Identity, string.Empty);

                case SealedExchangeResult.IdentityRejected:
                    return new Result(Outcome.IdentityRejected, false, result.Error);

                case SealedExchangeResult.BadServerKey:
                    return new Result(Outcome.BadServerKey, false, result.Error);

                case SealedExchangeResult.BindingRejected:
                    return new Result(Outcome.BindingRejected, false, result.Error);

                case SealedExchangeResult.ServerRefused:
                    return new Result(Outcome.ServerRefused, false, result.Error);

                default:
                    return new Result(Outcome.NoServerHello, false, result.Error);
            }
        }
    }
}
