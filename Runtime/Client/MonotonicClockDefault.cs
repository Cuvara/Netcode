using System.Diagnostics;

namespace Cuvara.Netcode.Client
{
    /// <summary>
    /// The production source behind <see cref="NetworkSettings.MonotonicClock"/>:
    /// one process-wide <see cref="Stopwatch"/>. Immune to wall-clock steps (NTP
    /// sync, time-zone change, suspend/resume correction), which is the whole
    /// reason elapsed-time maths must not use <c>DateTime.UtcNow</c>.
    /// </summary>
    public static class MonotonicClockDefault
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>Milliseconds since the first use in this process.</summary>
        public static long NowMs() => Clock.ElapsedMilliseconds;
    }
}
