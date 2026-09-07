using System;
using NUnit.Framework;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Connection;
using Cuvara.Netcode.Protocol;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Pins the cause → action table in <see cref="ReconnectPolicy"/>. The table is
    /// also written out in <c>Documentation~/NETCODE.md</c>; a change to either
    /// must land in both.
    /// </summary>
    [TestFixture]
    public sealed class ReconnectPolicyTests
    {
        [TestCase(DisconnectCause.PeerClosed)]
        [TestCase(DisconnectCause.HeartbeatTimeout)]
        [TestCase(DisconnectCause.TransportError)]
        public void OrdinaryLinkLoss_ReconnectsImmediately(DisconnectCause cause)
        {
            Assert.That(ReconnectPolicy.ForSessionClose(new DisconnectInfo(cause)),
                Is.EqualTo(ReconnectDecision.Reconnect));
        }

        [Test]
        public void ServerShutdown_ReconnectsAfterABackoffPause_ToSpreadTheStorm()
        {
            var info = new DisconnectInfo(DisconnectCause.ServerDisconnect, KickReasons.ServerShutdown);
            Assert.That(ReconnectPolicy.ForSessionClose(info), Is.EqualTo(ReconnectDecision.ReconnectAfterDelay));
        }

        [Test]
        public void UserClose_NeverReconnects()
        {
            Assert.That(ReconnectPolicy.ForSessionClose(new DisconnectInfo(DisconnectCause.LocalClose)),
                Is.EqualTo(ReconnectDecision.Never));
        }

        [TestCase(KickReasons.DuplicateLogin)]
        [TestCase("")]
        [TestCase("some_future_reason")]
        public void Kick_NeverReconnects_WhateverTheReason(string reason)
        {
            Assert.That(ReconnectPolicy.ForSessionClose(new DisconnectInfo(DisconnectCause.Kicked, reason)),
                Is.EqualTo(ReconnectDecision.Never));
        }

        [TestCase(KickReasons.DuplicateLogin)]
        [TestCase("")]
        [TestCase("maintenance")]
        public void UnpairedDisconnect_WithAnyReasonButShutdown_NeverReconnects(string reason)
        {
            // A legacy-build eviction, or a server decision we do not know: the
            // server chose to end it, and only server_shutdown promises a comeback.
            Assert.That(ReconnectPolicy.ForSessionClose(new DisconnectInfo(DisconnectCause.ServerDisconnect, reason)),
                Is.EqualTo(ReconnectDecision.Never));
        }

        [Test]
        public void ProtocolError_NeverReconnects()
        {
            Assert.That(ReconnectPolicy.ForSessionClose(new DisconnectInfo(DisconnectCause.ProtocolError)),
                Is.EqualTo(ReconnectDecision.Never));
        }

        [TestCase("invalid token")]
        [TestCase("invalid auth request")]
        [TestCase("map is not available")]
        public void PermanentServerErrors_StopTheLoopEarly(string serverError)
        {
            Assert.That(ReconnectPolicy.IsPermanentServerError(serverError), Is.True);
            Assert.That(ReconnectPolicy.IsPermanentFailure(new NetworkException("x", serverError)), Is.True);
        }

        [TestCase("session expired")]
        [TestCase("rate limited")]
        [TestCase("not authenticated")]
        [TestCase("server is starting, retry shortly")]
        [TestCase("no server available for map")]
        [TestCase("Server is full")]
        [TestCase("Token already used")]
        [TestCase("")]
        [TestCase("something new")]
        public void TransientAndUnknownServerErrors_KeepRetrying(string serverError)
        {
            Assert.That(ReconnectPolicy.IsPermanentServerError(serverError), Is.False);
            Assert.That(ReconnectPolicy.IsPermanentFailure(new NetworkException("x", serverError)), Is.False);
        }

        [Test]
        public void AProviderThatCannotProduceACredential_IsPermanent()
        {
            // NakamaAuthProvider throws InvalidOperationException when Nakama
            // refuses or the RPC yields no token; another network round cannot fix it.
            Assert.That(ReconnectPolicy.IsPermanentFailure(new InvalidOperationException("no session")), Is.True);
            Assert.That(ReconnectPolicy.IsPermanentFailure(new TimeoutException()), Is.False);
        }

        [Test]
        public void Backoff_DoublesFromTheBase_AndCapsAtTheMaximum()
        {
            var one = TimeSpan.FromSeconds(1);
            var cap = TimeSpan.FromSeconds(8);
            Assert.That(ReconnectPolicy.Backoff(0, one, cap), Is.EqualTo(TimeSpan.Zero), "round 0 is 'now'");
            Assert.That(ReconnectPolicy.Backoff(1, one, cap), Is.EqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(ReconnectPolicy.Backoff(2, one, cap), Is.EqualTo(TimeSpan.FromSeconds(2)));
            Assert.That(ReconnectPolicy.Backoff(3, one, cap), Is.EqualTo(TimeSpan.FromSeconds(4)));
            Assert.That(ReconnectPolicy.Backoff(4, one, cap), Is.EqualTo(TimeSpan.FromSeconds(8)));
            Assert.That(ReconnectPolicy.Backoff(5, one, cap), Is.EqualTo(TimeSpan.FromSeconds(8)));
            Assert.That(ReconnectPolicy.Backoff(60, one, cap), Is.EqualTo(TimeSpan.FromSeconds(8)), "no overflow");
        }

        [Test]
        public void DefaultSchedule_FitsInsideTheEntityHold()
        {
            // The whole point of the budget: pauses of the rounds that fit inside it
            // must sum to less than the server's 30 s hold, with the last round's
            // own dial + join still ahead of it.
            var s = new NetworkSettings();
            Assert.That(s.ReconnectBudget, Is.LessThan(TimeSpan.FromSeconds(30)));

            var total = TimeSpan.Zero;
            var rounds = 0;
            for (var round = 1; round <= s.ReconnectAttempts; round++)
            {
                var pause = ReconnectPolicy.Backoff(round, s.ReconnectDelay, s.ReconnectMaxDelay);
                if (total + pause > s.ReconnectBudget)
                {
                    break;
                }

                total += pause;
                rounds++;
            }

            Assert.That(rounds, Is.GreaterThanOrEqualTo(4), "a drop must get several tries");
            Assert.That(total, Is.LessThanOrEqualTo(s.ReconnectBudget));
        }

        [Test]
        public void ExhaustedException_ReportsWhetherTheStopWasPermanent()
        {
            var budget = new ReconnectExhaustedException("budget", 3, TimeSpan.FromSeconds(25), new TimeoutException());
            Assert.That(budget.Permanent, Is.False);
            Assert.That(budget.Attempts, Is.EqualTo(3));

            var refused = new ReconnectExhaustedException("refused", 1, TimeSpan.FromSeconds(1),
                new NetworkException("gateway rejected authentication: invalid token", "invalid token"));
            Assert.That(refused.Permanent, Is.True);
        }
    }
}
