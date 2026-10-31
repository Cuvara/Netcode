using System;
using System.Security.Authentication;
using NUnit.Framework;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Connection;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The classifier's own table. Decisions are pinned by <see cref="FailureDecisionBaselineTests"/>;
    /// this fixture pins the kinds and the per-context derivation.
    /// </summary>
    [TestFixture]
    public sealed class FailureClassifierTests
    {
        [TestCase("invalid token", "AuthRejected")]
        [TestCase("invalid auth request", "AuthRejected")]
        [TestCase("map is not available", "MapUnavailable")]
        [TestCase(KickReasons.ProtocolVersionMismatch, "ProtocolVersionMismatch")]
        [TestCase(TransportKinds.UnsupportedGameplayTransport, "UnsupportedGameplayTransport")]
        [TestCase("session expired", "SessionExpired")]
        [TestCase("rate limited", "RateLimited")]
        [TestCase("Server is full", "Transient")]
        [TestCase("", "Transient")]
        [TestCase(null, "Transient")]
        public void ServerErrors_AreClassified(string serverError, string kindName)
        {
            Assert.That(FailureClassifier.ClassifyServerError(serverError), Is.EqualTo(Enum.Parse(typeof(FailureKind), kindName)));
        }

        [Test]
        public void Exceptions_AreClassified()
        {
            Assert.That(FailureClassifier.Classify(new TlsPinMismatchException("p", new AuthenticationException())), Is.EqualTo(FailureKind.TlsPinMismatch));
            Assert.That(FailureClassifier.Classify(new InvalidOperationException()), Is.EqualTo(FailureKind.CredentialUnavailable));
            Assert.That(FailureClassifier.Classify(new NetworkException("x", "rate limited")), Is.EqualTo(FailureKind.RateLimited));
            Assert.That(FailureClassifier.Classify(new TransportException("x")), Is.EqualTo(FailureKind.Transient));
            Assert.That(FailureClassifier.Classify((Exception)null), Is.EqualTo(FailureKind.Transient));
        }

        [Test]
        public void Disconnects_AreClassified()
        {
            Assert.That(FailureClassifier.Classify(new DisconnectInfo(DisconnectCause.LocalClose)), Is.EqualTo(FailureKind.LocalClose));
            Assert.That(FailureClassifier.Classify(new DisconnectInfo(DisconnectCause.Kicked, SealedRefusalReason.NoSealedSession)), Is.EqualTo(FailureKind.SealedSessionRequired));
            Assert.That(FailureClassifier.Classify(new DisconnectInfo(DisconnectCause.Kicked, KickReasons.DuplicateLogin)), Is.EqualTo(FailureKind.Evicted));
            Assert.That(FailureClassifier.Classify(new DisconnectInfo(DisconnectCause.ProtocolError)), Is.EqualTo(FailureKind.ProtocolFault));
            Assert.That(FailureClassifier.Classify(new DisconnectInfo(DisconnectCause.ServerDisconnect, KickReasons.ServerShutdown)), Is.EqualTo(FailureKind.ServerDraining));
            Assert.That(FailureClassifier.Classify(new DisconnectInfo(DisconnectCause.ServerDisconnect, KickReasons.DuplicateLogin)), Is.EqualTo(FailureKind.ServerClosed));
            Assert.That(FailureClassifier.Classify(new DisconnectInfo(DisconnectCause.PeerClosed)), Is.EqualTo(FailureKind.LinkLost));
            Assert.That(FailureClassifier.Classify(new DisconnectInfo(DisconnectCause.HeartbeatTimeout)), Is.EqualTo(FailureKind.LinkLost));
            Assert.That(FailureClassifier.Classify(new DisconnectInfo(DisconnectCause.TransportError)), Is.EqualTo(FailureKind.LinkLost));
            Assert.That(FailureClassifier.Classify(new DisconnectInfo((DisconnectCause)99)), Is.EqualTo(FailureKind.ServerClosed));
        }

        [Test]
        public void EveryKind_HasADecisionInBothContexts()
        {
            foreach (FailureKind kind in Enum.GetValues(typeof(FailureKind)))
            {
                Assert.DoesNotThrow(() =>
                {
                    FailureClassifier.IsPermanentForReconnect(kind);
                    FailureClassifier.IsRetryableByJoinLoop(kind);
                    FailureClassifier.ReconnectDecisionFor(kind);
                }, kind.ToString());
            }
        }

        [Test]
        public void Ng21_MapUnavailable_IsPermanentForReconnectButRetriedByTheJoinLoop()
        {
            // NG-21: documents today's behaviour; the fix is a separate, reviewed change.
            Assert.That(FailureClassifier.IsPermanentForReconnect(FailureKind.MapUnavailable), Is.True);
            Assert.That(FailureClassifier.IsRetryableByJoinLoop(FailureKind.MapUnavailable), Is.True);
        }
    }
}
