using System;
using System.Collections.Generic;
using System.Security.Authentication;
using NUnit.Framework;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Connection;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Every retry/permanence decision the client makes today, written down from the code as it
    /// was before the failure classifier replaced the scattered lists. The expected values are
    /// literals, never derived from the classifier, so this fixture proves the refactor changed no
    /// decision. Do not edit a row to make a change pass: a changed row is a behaviour change and
    /// needs its own reviewed PR.
    /// </summary>
    [TestFixture]
    public sealed class FailureDecisionBaselineTests
    {
        // server error string -> (permanent for reconnect rounds and the gateway connect, retried by the join loop)
        internal static readonly KeyValuePair<string, (bool Permanent, bool JoinRetries)>[] ServerErrors =
        {
            Row("invalid token", true, true),
            Row("invalid auth request", true, true),
            Row("map is not available", true, true), // NG-21: permanent for reconnect, retried by the join loop
            Row(KickReasons.ProtocolVersionMismatch, true, false),
            Row(TransportKinds.UnsupportedGameplayTransport, true, false),
            Row("session expired", false, false),
            Row("rate limited", false, false),
            Row("not authenticated", false, true),
            Row("server is starting, retry shortly", false, true),
            Row("no server available for map", false, true),
            Row("Server is full", false, true),
            Row("Token already used", false, true),
            Row("something new", false, true),
            Row("", false, true),
        };

        private static KeyValuePair<string, (bool Permanent, bool JoinRetries)> Row(string error, bool permanent, bool joinRetries)
        {
            return new KeyValuePair<string, (bool Permanent, bool JoinRetries)>(error, (permanent, joinRetries));
        }

        // (failure, permanent for reconnect rounds and the gateway connect)
        private static IEnumerable<TestCaseData> ExceptionCases()
        {
            yield return Case(new TransportException("refused"), false);
            yield return Case(new TlsPinMismatchException("pin", new AuthenticationException()), true);
            yield return Case(new InvalidOperationException("no session"), true);
            yield return Case(new ObjectDisposedException("x"), true); // subclass of InvalidOperationException
            yield return Case(new TimeoutException(), false);
            yield return Case(new OperationCanceledException(), false);
            yield return Case(new ArgumentException("x"), false);
            yield return Case(new NetworkException("plain"), false);

            foreach (var pair in ServerErrors)
            {
                yield return Case(new NetworkException("x", pair.Key), pair.Value.Permanent);
            }
        }

        private static TestCaseData Case(Exception failure, bool permanent)
        {
            string detail = failure is NetworkException n ? n.ServerError : "-";
            return new TestCaseData(failure, permanent).SetName("Failure_" + failure.GetType().Name + "_" + detail);
        }

        [TestCaseSource(nameof(ExceptionCases))]
        public void IsPermanentFailure_MatchesTheBaseline(Exception failure, bool permanent)
        {
            Assert.That(ReconnectPolicy.IsPermanentFailure(failure), Is.EqualTo(permanent));
            Assert.That(ReconnectPolicy.ShouldRetryInitialConnect(failure), Is.EqualTo(!permanent));
        }

        [Test]
        public void IsPermanentServerError_MatchesTheBaseline()
        {
            foreach (var pair in ServerErrors)
            {
                Assert.That(ReconnectPolicy.IsPermanentServerError(pair.Key), Is.EqualTo(pair.Value.Permanent), pair.Key);
            }

            Assert.That(ReconnectPolicy.IsPermanentServerError(null), Is.False);
        }

        [Test]
        public void NullFailure_IsNotRetriedByTheInitialConnect()
        {
            Assert.That(ReconnectPolicy.ShouldRetryInitialConnect(null), Is.False);
        }

        [Test]
        public void ForSessionClose_MatchesTheBaseline_ForEveryCauseAndReason()
        {
            string[] reasons =
            {
                "", "duplicate_login", "server_shutdown", "protocol_version_mismatch",
                "no_sealed_session", "encoding_cannot_seal", "future",
            };

            foreach (DisconnectCause cause in Enum.GetValues(typeof(DisconnectCause)))
            {
                foreach (string reason in reasons)
                {
                    ReconnectDecision expected = ReconnectDecision.Never;
                    if (cause == DisconnectCause.PeerClosed
                        || cause == DisconnectCause.HeartbeatTimeout
                        || cause == DisconnectCause.TransportError)
                    {
                        expected = ReconnectDecision.Reconnect;
                    }
                    else if (cause == DisconnectCause.Kicked && reason == "no_sealed_session")
                    {
                        expected = ReconnectDecision.Reconnect;
                    }
                    else if (cause == DisconnectCause.ServerDisconnect && reason == "server_shutdown")
                    {
                        expected = ReconnectDecision.ReconnectAfterDelay;
                    }

                    Assert.That(
                        ReconnectPolicy.ForSessionClose(new DisconnectInfo(cause, reason)),
                        Is.EqualTo(expected),
                        cause + " " + reason);
                }
            }

            Assert.That(
                ReconnectPolicy.ForSessionClose(new DisconnectInfo((DisconnectCause)99, "")),
                Is.EqualTo(ReconnectDecision.Never));
        }
    }
}
