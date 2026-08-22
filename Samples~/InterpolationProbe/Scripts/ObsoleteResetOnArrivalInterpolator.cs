namespace Cuvara.Netcode.Samples.InterpolationProbe
{
    // =====================================================================================
    //  DO NOT COPY THIS FILE INTO ANY CLIENT.  IT IS THE DEFECT, KEPT ON PURPOSE.
    // =====================================================================================
    //
    //  This is a deliberate, sample-only re-implementation of the interpolation algorithm
    //  `WorldViewBinder` used BEFORE netcode 0.19.0. It was deleted from the runtime in
    //  that release because it produced three visible defects; it is reproduced here, in
    //  Samples~/ and nowhere else, for exactly one reason: so the scene can render the same
    //  synthetic stream through the old algorithm and the new one at the same time, and a
    //  viewer can SEE the pop as a difference between two dots rather than read about it in
    //  a paragraph.
    //
    //  It is not referenced by anything under Runtime/. It is not tested. It must never be
    //  fixed, extended or reused — improving it would destroy the only thing it is for.
    //  The production path is Cuvara.Netcode.Interpolation: InterpolationClock plus
    //  SnapshotInterpolation.Evaluate, and Documentation~/NETCODE.md, "Remote entity
    //  interpolation", is its description.
    //
    //  The three defects this file exists to exhibit, all reproduced faithfully:
    //    1. The phase is RESET TO ZERO on every arrival, whatever the previous frame drew.
    //       An early snapshot therefore discards the unrendered remainder of the current
    //       segment and lurches forward — measured in the Editor at 4.3x a normal frame.
    //    2. The phase is clamped at t <= 1.2, so a late snapshot stalls the entity and then
    //       steps it BACKWARDS by the extrapolated fifth of a segment when it lands.
    //    3. The interval is an EMA of ARRIVAL GAPS, not of per-tick spacing, so one dropped
    //       snapshot divides a doubled position delta by a barely-changed interval and
    //       renders at ~1.54x true speed before freezing.
    //
    // =====================================================================================

    /// <summary>
    /// <b>Obsolete by design — the pre-0.19.0 algorithm, kept only so the probe scene can
    /// show what it looked like.</b> See the banner at the top of this file.
    /// </summary>
    public sealed class ObsoleteResetOnArrivalInterpolator
    {
        /// <summary>Nominal 15 Hz seed, matching the interval the old binder assumed.</summary>
        public const double DefaultIntervalSeconds = 1.0 / 15.0;

        /// <summary>The extrapolation clamp the old algorithm used, in phase units.</summary>
        public const double PhaseClamp = 1.2;

        private float _fromX, _fromY;
        private float _toX, _toY;
        private bool _hasFrom;
        private bool _hasTo;
        private double _lastArrival;

        /// <summary>EMA of arrival gaps. The divisor a dropped packet moves only 30 % of the way.</summary>
        public double IntervalSeconds { get; private set; } = DefaultIntervalSeconds;

        /// <summary>Phase used by the most recent <see cref="Evaluate"/>, after the clamp.</summary>
        public double LastPhase { get; private set; }

        /// <summary>Whether anything can be drawn yet.</summary>
        public bool HasState => _hasTo;

        /// <summary>Forgets everything.</summary>
        public void Reset()
        {
            _hasFrom = false;
            _hasTo = false;
            _lastArrival = 0.0;
            IntervalSeconds = DefaultIntervalSeconds;
            LastPhase = 0.0;
        }

        /// <summary>
        /// A snapshot arrived. Shifts the pair along and — the defect — stamps the arrival
        /// that <see cref="Evaluate"/> measures the phase from, so the phase restarts at
        /// zero here on every single arrival.
        /// </summary>
        public void NoteSnapshot(float x, float y, double nowSeconds)
        {
            if (_hasTo)
            {
                // The sanity-bounded EMA, reproduced exactly: 1 ms < measured < 500 ms,
                // alpha 0.3. Per SNAPSHOT, not per tick — which is why a skipped tick
                // breaks it.
                double measured = nowSeconds - _lastArrival;
                if (measured > 0.001 && measured < 0.500)
                {
                    IntervalSeconds = IntervalSeconds * 0.7 + measured * 0.3;
                }

                _fromX = _toX;
                _fromY = _toY;
                _hasFrom = true;
            }

            _toX = x;
            _toY = y;
            _hasTo = true;
            _lastArrival = nowSeconds;
        }

        /// <summary>
        /// Where the old algorithm would draw the entity at <paramref name="nowSeconds"/>.
        /// </summary>
        public bool Evaluate(double nowSeconds, out float x, out float y)
        {
            x = _toX;
            y = _toY;

            if (!_hasTo)
            {
                return false;
            }

            if (!_hasFrom || IntervalSeconds <= 0.0)
            {
                LastPhase = 0.0;
                return true;
            }

            double t = (nowSeconds - _lastArrival) / IntervalSeconds;
            if (t < 0.0) t = 0.0;
            if (t > PhaseClamp) t = PhaseClamp;
            LastPhase = t;

            // Unclamped lerp: for t between 1.0 and 1.2 this extrapolates past the newest
            // sample, which is the distance the next arrival then undoes.
            x = (float)(_fromX + (_toX - _fromX) * t);
            y = (float)(_fromY + (_toY - _fromY) * t);
            return true;
        }
    }
}
