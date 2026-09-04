using System;

namespace Cuvara.Netcode.Prediction
{
    /// <summary>
    /// Measures how old a snapshot already is by the time the client acts on it, in base
    /// ticks, by fitting the server's clock to the client's — <b>offset and rate</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this is for.</b> The prediction clock is steered onto the tick carried by the
    /// newest snapshot, and that tick is old when it is read: the server produced it, put it
    /// on a socket, it crossed the network, and it then waited for a client frame to pick it
    /// up. Steering onto it with no allowance for that age drags the client's clock behind
    /// the server's real one by exactly the age, and a tick number then stops naming the same
    /// moment on both sides — the client's tick N carries inputs the server will not apply
    /// until its own tick N + age. The reconcile reports that as a correction of one input
    /// interval, on every snapshot, forever.
    /// </para>
    /// <para>
    /// <b>Why the age is measured rather than derived.</b> It used to be taken as one
    /// snapshot interval plus the rounded half round trip. Both terms are whole ticks while
    /// the real age is fractional and depends on where a client's frame loop happens to fall
    /// relative to the server's send cadence — a phase fixed at join that then holds for the
    /// session. Measured with two clients on one machine against one server, an unlucky phase
    /// left one of them with a constant <b>0.3333-unit, 4.00-step</b> correction while the
    /// other sat at 0.0033.
    /// </para>
    ///
    /// <para><b>The model.</b> A snapshot stamped with base tick <c>T</c> was produced on the
    /// server at server time <c>T / hz</c>. Observed on the client at local time <c>t</c>,
    ///
    /// <code>t = offset + skew * (T / hz) + delay,   delay &gt;= 0</code>
    ///
    /// <c>offset</c> absorbs both the arbitrary difference between the two clocks' origins and
    /// the minimum one-way delay — inseparable from one side, and neither needs separating.
    /// <c>skew</c> is the ratio of the two clocks' rates. <c>delay</c> is queueing, jitter and
    /// the wait for a frame, and is what this class exists to report.</para>
    ///
    /// <para>Because <c>delay</c> is never negative, the samples lie <b>above</b> a line and
    /// touch it at their best moments. Fitting that lower envelope gives <c>offset</c> and
    /// <c>skew</c>; the height of a sample above it is that snapshot's age beyond the best the
    /// route has shown. Least squares would be wrong here — it fits the middle of a
    /// distribution whose upper side is unbounded delay, so every bad frame drags the answer.
    /// The envelope is the same "minimum, not mean" argument
    /// <see cref="TickRateEstimator.SnapshotTickGap"/> makes about the send cadence.</para>
    ///
    /// <para><b>Why the rate term is not optional, and what it cost to learn.</b> An earlier
    /// version fitted the offset alone, against a fixed rate. A minimum-filtered offset cannot
    /// see a rate difference, and a rate difference it cannot see appears as an offset that
    /// grows without bound. Wired to the steering target it made the client categorically
    /// worse, twice:</para>
    ///
    /// <list type="bullet">
    ///   <item><description>fed a rate measured off the wire (57.7 Hz for a 60 Hz server), it
    ///   drifted 4 % per second: the reading passed <b>613 ticks</b> with the steering target
    ///   following it, and snaps reached <b>71 per five-second window</b>;</description></item>
    ///   <item><description>fed the server's advertised rate, it still settled around
    ///   <b>205 ticks</b> where two or three was right — the same runaway, slower.</description></item>
    /// </list>
    ///
    /// <para>Both are the same defect: a term the model did not have. Solving for it is what
    /// this class does now, and <c>SkewPpm</c> reports it so a rate that disagrees with the
    /// advertised one is visible rather than absorbed. With the rate fitted, two minutes at
    /// the same 4 % difference reads flat and the difference appears as 40 000 ppm — which is
    /// the point: it is a wrong tick rate, and it should be legible as one rather than
    /// arriving as a client that drifts.</para>
    ///
    /// <para><b>How the envelope is fitted.</b> Two anchors, each the lowest sample of its own
    /// epoch, with the line drawn through them. An anchor is a best-case sample by
    /// construction, so a line through two of them, far apart in time, estimates the rate with
    /// the delay largely divided out — the long baseline is what makes a small rate error
    /// measurable at all. This is the cheap form of the convex-hull method used for clock
    /// synchronisation over paths with unknown delay; it keeps two points rather than a hull,
    /// which is enough when the samples arrive at a fixed cadence and the rate is stable over
    /// a session.</para>
    /// </remarks>
    public sealed class SnapshotStalenessEstimator
    {
        /// <summary>
        /// How long each epoch collects before its lowest sample becomes an anchor.
        /// </summary>
        /// <remarks>
        /// Two seconds is ~30 snapshots at the default rates: enough that the lowest of them
        /// is a genuinely good sample rather than whichever arrived first, and short enough
        /// that a joining client has a fit within a few seconds.
        /// </remarks>
        public const double EpochSeconds = 2.0;

        /// <summary>
        /// Shortest baseline between the two anchors that may be used to estimate the rate.
        /// </summary>
        /// <remarks>
        /// The rate is a slope, and a slope over a short baseline is mostly the noise of its
        /// two endpoints: over one second, a millisecond of residual jitter reads as 1000 ppm
        /// of rate error, which is twenty times a real one. Four seconds puts that at 250 ppm
        /// and the baseline grows from there, because the older anchor is deliberately kept.
        /// </remarks>
        public const double MinimumBaselineSeconds = 4.0;

        /// <summary>
        /// Longest a baseline is kept before the older anchor is replaced.
        /// </summary>
        /// <remarks>
        /// A long baseline measures the rate well and follows a change in it slowly, because
        /// half its evidence is minutes old. Two minutes keeps the rate estimate tight while
        /// bounding how long a genuine change — a machine's clock being stepped, a server
        /// restarting on a different tick origin — stays half-believed.
        /// </remarks>
        public const double MaximumBaselineSeconds = 120.0;

        /// <summary>
        /// Bounds on the fitted rate, as a ratio of client clock to server tick time.
        /// </summary>
        /// <remarks>
        /// A pair that implies an impossible rate is a bad pair, not a discovery: a slope
        /// fitted through one would steer the simulation somewhere arbitrary. What counts as
        /// impossible has to be chosen against what is merely unusual, and the first attempt
        /// got that wrong.
        ///
        /// <para><b>These were 0.90 and 1.10, and that was too tight to be useful.</b> On the
        /// machine this was developed on the true ratio is about <b>1.103</b> — the Windows
        /// performance counter runs fast against the Linux clock the server ticks on, and the
        /// snapshot stream the client observes advances at 54.4 base ticks per client second
        /// against a nominal 60. So every fit was refused, <c>IsUsable</c> stayed false for a
        /// whole session, and the steering silently fell back to the derived figure. Nothing
        /// reported it: <c>SkewPpm</c> reads 0 when there is no fit, which is indistinguishable
        /// from two clocks that agree.</para>
        ///
        /// <para>A third either way still rejects what this is for. A client predicting at the
        /// wrong tick rate — 60 against a 15 Hz server, the failure the clamp exists to catch —
        /// is off by 300%, not by 10. Between "two ordinary machines" and "a rate that is
        /// simply wrong" there is an order of magnitude, and the bound belongs in the middle of
        /// it rather than at the edge of the first.</para>
        /// </remarks>
        public const double MinimumSkew = 0.75;

        /// <inheritdoc cref="MinimumSkew"/>
        public const double MaximumSkew = 1.33;

        private double _offset;
        private double _skew = 1.0;
        private bool _haveFit;

        // The older anchor: lowest sample of an earlier epoch, and the far end of the
        // baseline the rate is fitted over.
        private double _anchorX, _anchorY;
        private bool _haveAnchor;

        // The current epoch's lowest sample so far.
        private double _bestX, _bestY, _bestResidual;
        private bool _haveBest;
        private double _epochStartedAt;

        /// <summary>Samples taken since construction or <see cref="Reset"/>.</summary>
        public int Samples { get; private set; }

        /// <summary>
        /// The most recent measurement, in base ticks, or 0 before <see cref="IsUsable"/>.
        /// </summary>
        public float StalenessTicks { get; private set; }

        /// <summary>Whether a line has been fitted and <see cref="StalenessTicks"/> means anything.</summary>
        public bool IsUsable => _haveFit;

        /// <summary>
        /// The fitted rate difference between the two clocks, in parts per million, or 0
        /// before <see cref="IsUsable"/>.
        /// </summary>
        /// <remarks>
        /// Positive means the client's clock runs fast relative to the server's tick stream.
        /// A few hundred ppm is two ordinary crystals disagreeing. Tens of thousands is not
        /// skew — it is a tick rate that does not match what the server is actually running,
        /// and it is worth an error rather than a correction.
        /// </remarks>
        public double SkewPpm => _haveFit ? (_skew - 1.0) * 1e6 : 0.0;

        /// <summary>Wall-clock span the current rate estimate was fitted over, in seconds.</summary>
        public double BaselineSeconds { get; private set; }

        /// <summary>Times a new line has been fitted.</summary>
        public int Fits { get; private set; }

        /// <summary>
        /// Times a fit was refused because the pair implied a rate outside
        /// <see cref="MinimumSkew"/>..<see cref="MaximumSkew"/>.
        /// </summary>
        /// <remarks>
        /// Reported because a refusal is otherwise invisible: <see cref="SkewPpm"/> reads 0
        /// without a fit, which looks exactly like two clocks that agree. A bound set too
        /// tightly therefore disables the measurement for a whole session and says nothing —
        /// which is what happened at the original 0.90/1.10, against a machine whose true
        /// ratio is 1.103.
        /// </remarks>
        public int FitsRefused { get; private set; }

        /// <summary>The rate the last refused pair implied, in ppm, or 0 if none was refused.</summary>
        /// <remarks>
        /// The number that was rejected, so a bound that is wrong can be seen to be wrong
        /// rather than inferred from an absence.
        /// </remarks>
        public double RefusedSkewPpm { get; private set; }

        /// <summary>
        /// Record a snapshot and return how old it was when it was acted on, in base ticks.
        /// </summary>
        /// <param name="snapshotTick">Base tick the snapshot was produced on.</param>
        /// <param name="nowSeconds">
        /// Local time the client is acting on it — the moment of use, not of arrival, so the
        /// wait for a frame is inside the measurement. Must come from one monotonic clock;
        /// mixing sources makes the differences meaningless.
        /// </param>
        /// <param name="baseHz">
        /// The rate the SERVER stamps ticks at, as advertised by it. Used only to turn a tick
        /// into a server-clock time, and any error in it is absorbed by the fitted rate rather
        /// than accumulating — which is the whole reason the rate is fitted.
        /// </param>
        /// <returns>
        /// Age in base ticks, or 0 before a line has been fitted. Never negative: a snapshot
        /// cannot be read before it was produced, so a sample below the line means the line is
        /// stale, not that time ran backwards.
        /// </returns>
        public float Sample(long snapshotTick, double nowSeconds, float baseHz)
        {
            if (snapshotTick <= 0 || baseHz <= 0f || double.IsNaN(nowSeconds))
            {
                return StalenessTicks;
            }

            double x = snapshotTick / (double)baseHz;   // server time this tick was produced
            double y = nowSeconds;                      // client time it was acted on

            if (Samples < int.MaxValue) Samples++;

            // Height above the current line, or above a unit-rate line through the origin of
            // the first sample while there is no fit yet. Either way it ranks samples within
            // the epoch consistently, which is all the anchor selection needs.
            double residual = _haveFit ? y - (_offset + _skew * x) : y - x;

            if (!_haveBest || residual < _bestResidual)
            {
                _bestX = x;
                _bestY = y;
                _bestResidual = residual;
                _haveBest = true;
            }

            if (_epochStartedAt == 0.0)
            {
                _epochStartedAt = nowSeconds;
            }
            else if (nowSeconds - _epochStartedAt >= EpochSeconds)
            {
                CloseEpoch();
                _epochStartedAt = nowSeconds;
            }

            if (_haveFit)
            {
                double above = y - (_offset + _skew * x);
                if (above < 0) above = 0;
                StalenessTicks = (float)(above * baseHz);
            }

            return StalenessTicks;
        }

        /// <summary>
        /// Promote the epoch's lowest sample to an anchor, and refit if the baseline is long
        /// enough to say anything about the rate.
        /// </summary>
        private void CloseEpoch()
        {
            if (!_haveBest)
            {
                return;
            }

            if (!_haveAnchor)
            {
                _anchorX = _bestX;
                _anchorY = _bestY;
                _haveAnchor = true;
                _haveBest = false;
                return;
            }

            double span = _bestX - _anchorX;
            if (span >= MinimumBaselineSeconds)
            {
                double skew = (_bestY - _anchorY) / span;

                // A pair that implies an impossible rate is a bad pair, not a discovery. Keep
                // the line that stands and let the next epoch try again.
                if (skew >= MinimumSkew && skew <= MaximumSkew)
                {
                    _skew = skew;
                    _offset = _anchorY - _skew * _anchorX;
                    _haveFit = true;
                    BaselineSeconds = span;
                    Fits++;
                }
                else
                {
                    FitsRefused++;
                    RefusedSkewPpm = (skew - 1.0) * 1e6;
                }

                // The older anchor is kept so the baseline keeps growing and the rate estimate
                // keeps tightening -- until it is old enough that half the evidence is stale,
                // at which point the newer sample becomes the far end of a fresh baseline.
                if (span >= MaximumBaselineSeconds)
                {
                    _anchorX = _bestX;
                    _anchorY = _bestY;
                }
            }
            else if (_bestY - _anchorY < 0)
            {
                // Too short to fit a rate over, but lower than the anchor: the anchor was not
                // a best case after all, so it is replaced rather than kept as one.
                _anchorX = _bestX;
                _anchorY = _bestY;
            }

            _haveBest = false;
        }

        /// <summary>
        /// Forget the fit. Call on a session boundary: the line describes one route to one
        /// server, and carrying it across a reconnect measures the new connection against the
        /// old one's clock.
        /// </summary>
        public void Reset()
        {
            _offset = 0;
            _skew = 1.0;
            _haveFit = false;
            _anchorX = _anchorY = 0;
            _haveAnchor = false;
            _bestX = _bestY = _bestResidual = 0;
            _haveBest = false;
            _epochStartedAt = 0;
            Samples = 0;
            StalenessTicks = 0f;
            BaselineSeconds = 0;
            Fits = 0;
            FitsRefused = 0;
            RefusedSkewPpm = 0;
        }
    }
}
