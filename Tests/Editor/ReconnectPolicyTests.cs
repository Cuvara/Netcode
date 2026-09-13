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
        public void DefaultSchedule_CoversTheEntityHoldFromTheServersClock()
        {
            // The budget is anchored to when the SERVER notices the drop, not the
            // client: on a client-side loss the server's own 30 s heartbeat timeout
            // runs before its 30 s hold starts, so the hold can end ~60 s after our
            // close. Anything shorter gives up while the body is still being held —
            // measured live 2026-09-07 with a 25 s budget. Anything much longer just
            // retries into a hold that has already expired.
            var s = new NetworkSettings();
            Assert.That(s.ReconnectBudget, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(60)),
                "must outlast heartbeat timeout (30 s) + entity hold (30 s) from the server's clock");
            Assert.That(s.ReconnectBudget, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(90)),
                "past the hold a fresh login is the honest path");

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
    
        /// <summary>
        /// A `require` server refuses a client that did not seal and NAMES the reason. That
        /// is a configuration answer, not an eviction, and the same client sealing is
        /// accepted — so it must be retried, or a client whose sealing flag is off simply
        /// cannot play and the operator has to know to pass a flag.
        /// </summary>
        [Test]
        public void AKickForNoSealedSession_IsRetried()
        {
            var info = new DisconnectInfo(DisconnectCause.Kicked, SealedRefusalReason.NoSealedSession);

            Assert.That(ReconnectPolicy.ForSessionClose(info),
                Is.EqualTo(ReconnectDecision.Reconnect),
                "a sealing refusal is fixable by retrying with sealing on; refusing to retry " +
                "leaves the client unable to play against its own server");
        }

        /// <summary>
        /// And every other kick still means never. This is the half that matters most:
        /// duplicate_login must not be retried, because coming back evicts the newer login.
        /// </summary>
        [Test]
        public void EveryOtherKick_IsStillNever()
        {
            foreach (var reason in new[] { "duplicate_login", "", "banned", "no_sealed_session_x" })
            {
                Assert.That(ReconnectPolicy.ForSessionClose(new DisconnectInfo(DisconnectCause.Kicked, reason)),
                    Is.EqualTo(ReconnectDecision.Never),
                    $"kick reason '{reason}' must not be retried");
            }
        }

        /// <summary>
        /// The escalation is one-way by construction, and this pins the construction rather
        /// than the intent: nothing in the runtime assigns RequireSealedSession = false, so
        /// a hostile peer can ask the client for MORE protection and never for less. If a
        /// future change adds such an assignment, this test is the thing that should have
        /// stopped it — so it reads the source rather than the behaviour.
        /// </summary>
        [Test]
        public void NothingInTheRuntimeTurnsSealingOff()
        {
            var runtime = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(UnityEngine.Application.dataPath, "..",
                    "Packages", "com.cuvara.netcode", "Runtime"));
            if (!System.IO.Directory.Exists(runtime))
            {
                // Running from a source checkout rather than a resolved package.
                runtime = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "Runtime"));
            }

            Assert.That(System.IO.Directory.Exists(runtime), Is.True,
                $"cannot find the Runtime tree to scan (looked at {runtime}); a scan that " +
                "finds no files is not a pass");

            var offending = new System.Collections.Generic.List<string>();
            var scanned = 0;
            foreach (var file in System.IO.Directory.GetFiles(runtime, "*.cs", System.IO.SearchOption.AllDirectories))
            {
                scanned++;
                var text = System.IO.File.ReadAllText(file);
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        text, @"RequireSealedSession\s*=\s*false"))
                {
                    offending.Add(file);
                }
            }

            Assert.That(scanned, Is.GreaterThan(0), "scanned zero files, so this proves nothing");
            Assert.That(offending, Is.Empty,
                "something assigns RequireSealedSession = false, which turns the one-way " +
                "escalation into a negotiated downgrade: " + string.Join(", ", offending));
        }
}
}
