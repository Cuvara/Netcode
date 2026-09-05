namespace Cuvara.Netcode.Client
{
    /// <summary>
    /// Progress data for a reconnection attempt. Published alongside
    /// <see cref="NetworkClient.ReconnectAttemptStarted"/> so UI can show
    /// "Reconnecting... attempt 2/5 (next in 3s)".
    /// </summary>
    public readonly struct ReconnectionProgress
    {
        /// <summary>Current attempt number (1-based).</summary>
        public readonly int Attempt;

        /// <summary>Maximum attempts before giving up.</summary>
        public readonly int MaxAttempts;

        /// <summary>Seconds until the next attempt (0 on the last attempt).</summary>
        public readonly float DelaySeconds;

        /// <summary>True on the last attempt.</summary>
        public bool IsLastAttempt => Attempt >= MaxAttempts;

        /// <summary>Progress 0–1 across all attempts.</summary>
        public float Progress => MaxAttempts > 0 ? (float)Attempt / MaxAttempts : 0f;

        public ReconnectionProgress(int attempt, int maxAttempts, float delaySeconds)
        {
            Attempt = attempt;
            MaxAttempts = maxAttempts;
            DelaySeconds = delaySeconds;
        }

        public override string ToString() => $"Attempt {Attempt}/{MaxAttempts}" +
            (DelaySeconds > 0 ? $" (next in {DelaySeconds:F0}s)" : " (final)");
    }
}
