using System;
using Cuvara.Netcode.Connection;

namespace Cuvara.Netcode.Client
{
    /// <summary>What <see cref="NetworkClient"/> does after the gameplay connection ends.</summary>
    public enum ReconnectDecision
    {
        /// <summary>
        /// Stay down and hand the close to the caller. Either the close was ours,
        /// or the server told us not to come back (an eviction, a protocol fault).
        /// </summary>
        Never = 0,

        /// <summary>
        /// Try again at once, then back off. The link died without anyone
        /// choosing that — a NAT expiry, a Wi-Fi hand-off, a dead socket — and
        /// the server is holding the entity for 30 s.
        /// </summary>
        Reconnect = 1,

        /// <summary>
        /// Wait a backoff round <i>before</i> the first try. A draining server
        /// reaches every client in the same instant; an immediate retry is a
        /// synchronized storm at a gateway that is likely still allocating the
        /// replacement.
        /// </summary>
        ReconnectAfterDelay = 2
    }

    /// <summary>
    /// The recovery policy: cause of disconnect → what the client does about it.
    /// Pure, so the table can be tested without a socket and read without the
    /// state machine around it. <c>Documentation~/NETCODE.md</c> carries the same
    /// table in prose; keep the two in step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The policy answers two questions. <see cref="ForSessionClose"/> is asked
    /// once, when the gameplay socket ends: is a comeback worth trying at all?
    /// <see cref="IsPermanentFailure"/> is asked on every failed comeback: did the
    /// server just tell us that asking again cannot change the answer?
    /// </para>
    /// <para>
    /// The backend holds a disconnected player's entity for 30 s (API.md,
    /// "Heartbeat"), and a join token is single-use — so every round must go back
    /// through the gateway for a fresh credential, and the whole loop must fit
    /// inside that hold. <see cref="NetworkSettings.ReconnectBudget"/> is that fit.
    /// </para>
    /// </remarks>
    public static class ReconnectPolicy
    {
        /// <summary>
        /// Decides whether the close of the gameplay connection warrants an
        /// automatic reconnect. Does not consult settings: a caller that turned
        /// reconnects off checks that before asking.
        /// </summary>
        public static ReconnectDecision ForSessionClose(DisconnectInfo info)
        {
            // The table lives in FailureClassifier. Two of its rows deserve a note here:
            // a `no_sealed_session` kick is a configuration answer, not an eviction, so it
            // reconnects (NetworkClient escalates RequireSealedSession first, see
            // SealedRefusalReason); only `server_shutdown` earns the delayed first try.
            return FailureClassifier.ReconnectDecisionFor(FailureClassifier.Classify(info));
        }

        /// <summary>
        /// Whether a failed reconnect round reported an answer that no further
        /// round can change, so the loop must stop before its budget runs out and
        /// surface the real error instead of "could not join".
        /// </summary>
        /// <remarks>
        /// Delegates to <see cref="FailureClassifier"/>, which holds the servers' own strings.
        /// Anything unrecognised is treated as transient.
        /// </remarks>
        public static bool IsPermanentFailure(Exception failure)
        {
            return FailureClassifier.IsPermanentForReconnect(FailureClassifier.Classify(failure));
        }

        /// <summary>Whether an initial gateway connect failure may succeed on a fresh connection.</summary>
        public static bool ShouldRetryInitialConnect(Exception failure)
        {
            return failure != null && !IsPermanentFailure(failure);
        }

        /// <summary>The server error strings after which a reconnect is pointless.</summary>
        public static bool IsPermanentServerError(string serverError)
        {
            return FailureClassifier.IsPermanentForReconnect(FailureClassifier.ClassifyServerError(serverError));
        }

        /// <summary>
        /// Pause before reconnect round <paramref name="round"/> (1-based):
        /// exponential from <paramref name="baseDelay"/>, capped at
        /// <paramref name="maxDelay"/>. Jitter is added by the caller.
        /// </summary>
        public static TimeSpan Backoff(int round, TimeSpan baseDelay, TimeSpan maxDelay)
        {
            if (round <= 0 || baseDelay <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            // Shift, not Math.Pow: no floating point, and the cap makes any round
            // past ~30 irrelevant anyway.
            var shift = Math.Min(round - 1, 30);
            var ticks = baseDelay.Ticks * (1L << shift);
            if (ticks < 0 || ticks > maxDelay.Ticks)
            {
                ticks = maxDelay.Ticks;
            }

            return TimeSpan.FromTicks(ticks);
        }
    }

    /// <summary>
    /// Raised through <see cref="NetworkClient.ReconnectFailed"/> when the
    /// automatic reconnect stopped without landing: the budget or the attempt
    /// count ran out, or a round reported a permanent refusal.
    /// </summary>
    public sealed class ReconnectExhaustedException : Exception
    {
        public ReconnectExhaustedException(string message, int attempts, TimeSpan elapsed, Exception lastFailure)
            : base(message, lastFailure)
        {
            Attempts = attempts;
            Elapsed = elapsed;
        }

        /// <summary>Rounds that were actually started.</summary>
        public int Attempts { get; }

        /// <summary>Time from the close to giving up.</summary>
        public TimeSpan Elapsed { get; }

        /// <summary>
        /// True when the loop stopped early because the server's answer was
        /// terminal (see <see cref="ReconnectPolicy.IsPermanentFailure"/>), false
        /// when it simply ran out of budget or rounds.
        /// </summary>
        public bool Permanent => InnerException != null && ReconnectPolicy.IsPermanentFailure(InnerException);
    }
}
