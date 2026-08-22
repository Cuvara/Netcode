using System.Diagnostics;

namespace Cuvara.Netcode.View
{
    /// <summary>
    /// The production <see cref="IViewClock"/>: a <see cref="Stopwatch"/> started when the
    /// clock is constructed. This is what <see cref="WorldViewBinder"/> used directly
    /// before the clock became injectable, and it is what it still uses when no clock is
    /// supplied.
    /// </summary>
    /// <remarks>
    /// <see cref="Stopwatch"/> rather than <c>DateTime</c> or <c>Time.time</c>: the
    /// interpolator reads only differences over spans of tens of milliseconds, where a
    /// wall clock can step backwards over an NTP correction and Unity's clock is
    /// unavailable outside the player loop. It also keeps this assembly free of engine
    /// types, which is what lets the binder be exercised in Edit Mode at all.
    /// </remarks>
    public sealed class StopwatchViewClock : IViewClock
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        /// <inheritdoc />
        public double NowMs => _stopwatch.Elapsed.TotalMilliseconds;
    }
}
