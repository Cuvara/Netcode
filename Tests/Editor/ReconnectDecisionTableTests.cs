using System;
using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Connection;
using Cuvara.Netcode.Protocol;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// <see cref="ReconnectPolicy.ForSessionClose"/> as one exhaustive table: every
    /// <see cref="DisconnectCause"/> against every close reason the servers send, plus an empty and
    /// an unknown one.
    /// </summary>
    /// <remarks>
    /// <see cref="ReconnectPolicyTests"/> pins the same rules case by case, but it also covers
    /// <c>NetworkSettings</c> (UniTask), so it runs only in the Editor. This fixture needs nothing
    /// but the policy and runs headless. Being exhaustive is the point: a new
    /// <see cref="DisconnectCause"/> member falls into the policy's <c>default</c> (Never) silently,
    /// and here it fails until someone writes down what it should do.
    /// </remarks>
    [TestFixture]
    public sealed class ReconnectDecisionTableTests
    {
        private static readonly string[] Reasons =
        {
            "",
            KickReasons.DuplicateLogin,
            KickReasons.ServerShutdown,
            KickReasons.ProtocolVersionMismatch,
            SealedRefusalReason.NoSealedSession,
            SealedRefusalReason.EncodingCannotSeal,
            "some_future_reason",
        };

        private static ReconnectDecision Expected(DisconnectCause cause, string reason)
        {
            switch (cause)
            {
                case DisconnectCause.LocalClose:
                case DisconnectCause.ProtocolError:
                    return ReconnectDecision.Never;
                case DisconnectCause.Kicked:
                    return reason == SealedRefusalReason.NoSealedSession
                        ? ReconnectDecision.Reconnect
                        : ReconnectDecision.Never;
                case DisconnectCause.ServerDisconnect:
                    return reason == KickReasons.ServerShutdown
                        ? ReconnectDecision.ReconnectAfterDelay
                        : ReconnectDecision.Never;
                case DisconnectCause.PeerClosed:
                case DisconnectCause.HeartbeatTimeout:
                case DisconnectCause.TransportError:
                    return ReconnectDecision.Reconnect;
                default:
                    throw new AssertionException(
                        $"DisconnectCause.{cause} has no row in this table: decide whether it reconnects, " +
                        "then add it here, to ReconnectPolicy and to Documentation~/NETCODE.md");
            }
        }

        [Test]
        public void EveryCause_AndEveryReason_HasTheDocumentedDecision()
        {
            var wrong = new List<string>();
            foreach (DisconnectCause cause in Enum.GetValues(typeof(DisconnectCause)))
            {
                foreach (string reason in Reasons)
                {
                    ReconnectDecision expected = Expected(cause, reason);
                    ReconnectDecision actual = ReconnectPolicy.ForSessionClose(new DisconnectInfo(cause, reason));
                    if (actual != expected)
                    {
                        wrong.Add($"{cause} \"{reason}\": {actual}, expected {expected}");
                    }
                }
            }

            Assert.That(wrong, Is.Empty, string.Join("; ", wrong));
        }

        [Test]
        public void AFailedRound_StopsOnlyOnAnAnswerNoRetryCanChange()
        {
            Assert.That(ReconnectPolicy.IsPermanentFailure(new InvalidOperationException("no session")), Is.True);
            Assert.That(ReconnectPolicy.IsPermanentFailure(new TimeoutException()), Is.False);
            Assert.That(ReconnectPolicy.IsPermanentServerError("invalid token"), Is.True);
            Assert.That(ReconnectPolicy.IsPermanentServerError(KickReasons.ProtocolVersionMismatch), Is.True);
            Assert.That(ReconnectPolicy.IsPermanentServerError("server full"), Is.False);
            Assert.That(ReconnectPolicy.IsPermanentServerError(null), Is.False);
        }

        [TestCaseSource(nameof(InitialConnectRetryCases))]
        public void InitialConnectRetry_UsesReconnectClassification(Exception failure, bool retry)
        {
            Assert.That(ReconnectPolicy.ShouldRetryInitialConnect(failure), Is.EqualTo(retry));
        }

        [Test]
        public void TlsPinMismatch_IsPermanentForInitialConnectAndReconnect()
        {
            var failure = new Cuvara.Netcode.Transport.TlsPinMismatchException(
                "pinned certificate mismatch", new System.Security.Authentication.AuthenticationException());

            Assert.That(ReconnectPolicy.IsPermanentFailure(failure), Is.True);
            Assert.That(ReconnectPolicy.ShouldRetryInitialConnect(failure), Is.False);
        }

        private static IEnumerable<TestCaseData> InitialConnectRetryCases()
        {
            yield return new TestCaseData(new Cuvara.Netcode.Transport.TransportException("refused"), true)
                .SetName("InitialConnectRetry_TransportRefusalIsTransient");
            yield return new TestCaseData(new Cuvara.Netcode.Transport.TlsPinMismatchException(
                    "pinned certificate mismatch", new System.Security.Authentication.AuthenticationException()), false)
                .SetName("InitialConnectRetry_TlsPinMismatchIsPermanent");
            yield return new TestCaseData(new NetworkException("invalid token", "invalid token"), false)
                .SetName("InitialConnectRetry_InvalidTokenIsPermanent");
            yield return new TestCaseData(new NetworkException("version mismatch", KickReasons.ProtocolVersionMismatch), false)
                .SetName("InitialConnectRetry_VersionMismatchIsPermanent");
            yield return new TestCaseData(new OperationCanceledException(), true)
                .SetName("InitialConnectRetry_UnclassifiedTimeoutIsTransient");
        }

        [Test]
        public void TheBackoff_DoublesAndCaps()
        {
            TimeSpan b = TimeSpan.FromMilliseconds(500), cap = TimeSpan.FromSeconds(4);
            Assert.That(ReconnectPolicy.Backoff(0, b, cap), Is.EqualTo(TimeSpan.Zero));
            Assert.That(ReconnectPolicy.Backoff(1, b, cap), Is.EqualTo(b));
            Assert.That(ReconnectPolicy.Backoff(3, b, cap), Is.EqualTo(TimeSpan.FromSeconds(2)));
            Assert.That(ReconnectPolicy.Backoff(10, b, cap), Is.EqualTo(cap));
            Assert.That(ReconnectPolicy.Backoff(1000, b, cap), Is.EqualTo(cap), "no overflow past the cap");
        }
    }
}
