using System.Collections.Generic;

namespace Cuvara.Netcode.Samples.InterpolationProbe
{
    /// <summary>
    /// A server that does not exist: it produces snapshots of one entity on a fixed tick
    /// rate, delays each one by a latency the caller chooses, and delivers them into the
    /// probe when their arrival time comes round.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why synthetic rather than a real connection.</b> The behaviour this sample
    /// exists to show is the <i>relationship</i> between when a packet arrives and when a
    /// frame is drawn. A real backend produces whatever jitter the machine and the network
    /// happen to produce that minute, which is neither reproducible nor severe enough to
    /// see on demand. Here the perturbation is a button press, the entity's true motion is
    /// known exactly, and every irregularity on screen is therefore an artefact of the
    /// interpolator rather than of the network — which is the same reason
    /// <c>Tests/Editor/RemoteInterpolationContinuityTests.cs</c> drives a hand-written
    /// timeline instead of a socket.
    /// </para>
    /// <para>
    /// <b>The entity travels a circle at a constant speed</b>, one <see cref="RadiansPerTick"/>
    /// per server tick, so it never leaves the camera and never legitimately reverses.
    /// Straight-line motion would have to wrap, and a wrap is a genuine backwards jump that
    /// would be indistinguishable on screen from the defect the scene is about.
    /// </para>
    /// </remarks>
    public sealed class SyntheticSnapshotStream
    {
        /// <summary>What kind of perturbation to apply to a snapshot.</summary>
        public enum Perturbation
        {
            /// <summary>Clean periodic baseline.</summary>
            None = 0,

            /// <summary>Arrives sooner after its predecessor than the tick rate says it should.</summary>
            Early = 1,

            /// <summary>Arrives later. Far enough to run the old algorithm past its extrapolation clamp.</summary>
            Late = 2,

            /// <summary>Never arrives at all. The next one carries a doubled position delta.</summary>
            Skip = 3
        }

        /// <summary>One produced snapshot, waiting for its arrival time.</summary>
        public struct Packet
        {
            public long Tick;
            public float X;
            public float Y;

            /// <summary>Probe-clock time, in seconds, at which this is delivered.</summary>
            public double ArriveAt;
        }

        /// <summary>The package's world rate: 15 Hz, one snapshot per server tick.</summary>
        public const double TickInterval = 1.0 / 15.0;

        /// <summary>One full revolution every 60 ticks — four seconds at 15 Hz.</summary>
        public const double RadiansPerTick = 2.0 * System.Math.PI / 60.0;

        /// <summary>Circle the entity travels, in world units.</summary>
        public const float Radius = 3.6f;

        /// <summary>Baseline one-way latency. Constant: jitter is added on top, per packet.</summary>
        public const double BaseLatencySeconds = 0.040;

        /// <summary>An early packet arrives a quarter of an interval sooner than it should.</summary>
        public const double EarlyShiftSeconds = TickInterval * 0.25;

        /// <summary>
        /// A late packet arrives 45 % of an interval later, so the gap is ~1.45 intervals —
        /// past the <c>t &lt;= 1.2</c> clamp the pre-0.19 algorithm used, which is the
        /// condition its stall-then-step-back needs.
        /// </summary>
        public const double LateShiftSeconds = TickInterval * 0.45;

        private readonly List<Packet> _inFlight = new List<Packet>();
        private readonly System.Random _random;

        private long _nextTick = 1;
        private double _nextProduceAt;
        private double _lastArrivalScheduled = double.NegativeInfinity;
        private bool _repeatDue;

        /// <summary>
        /// Perturbation applied to every <i>other</i> snapshot, so the stream keeps
        /// alternating between a perturbed arrival and a clean one.
        /// <see cref="Perturbation.None"/> is the baseline.
        /// </summary>
        /// <remarks>
        /// Every other, not every one, and the distinction is the whole meaning of the
        /// setting. Shifting <i>every</i> arrival earlier by the same amount is not jitter
        /// at all — it is a constant latency, the gaps between arrivals are unchanged, and
        /// nothing would happen on screen. What perturbs an interpolator is a gap that
        /// differs from its neighbours, which is what alternating produces.
        /// </remarks>
        public Perturbation Repeat = Perturbation.None;

        /// <summary>Applied to the next snapshot only, then cleared. Overrides <see cref="Repeat"/>.</summary>
        public Perturbation Pending = Perturbation.None;

        /// <summary>Uniform arrival jitter, plus or minus, in seconds.</summary>
        public double JitterSeconds;

        /// <summary>Snapshots produced since the last reset, including dropped ones.</summary>
        public long Produced { get; private set; }

        /// <summary>Snapshots delivered to the interpolators.</summary>
        public long Delivered { get; private set; }

        /// <summary>Snapshots deliberately dropped by a skip.</summary>
        public long Dropped { get; private set; }

        /// <summary>Seed fixed so two runs of the scene show the same stream.</summary>
        public SyntheticSnapshotStream(int seed = 12345)
        {
            _random = new System.Random(seed);
        }

        /// <summary>Server position on <paramref name="tick"/>. The truth every readout is measured against.</summary>
        public static void PositionAt(double tick, out float x, out float y)
        {
            double a = tick * RadiansPerTick;
            x = (float)(System.Math.Cos(a) * Radius);
            y = (float)(System.Math.Sin(a) * Radius);
        }

        /// <summary>Forgets every in-flight packet and starts the tick counter again.</summary>
        public void Reset(double nowSeconds)
        {
            _inFlight.Clear();
            _nextTick = 1;
            _nextProduceAt = nowSeconds;
            _lastArrivalScheduled = double.NegativeInfinity;
            _repeatDue = false;
            Produced = 0;
            Delivered = 0;
            Dropped = 0;
        }

        /// <summary>
        /// Produces every snapshot due by <paramref name="nowSeconds"/> and appends every
        /// one whose arrival time has come to <paramref name="arrivals"/>, in tick order.
        /// </summary>
        public void Pump(double nowSeconds, List<Packet> arrivals)
        {
            while (_nextProduceAt <= nowSeconds)
            {
                Produce(_nextTick, _nextProduceAt);
                _nextTick++;
                _nextProduceAt += TickInterval;
            }

            // Delivered from the front only. The wire is TCP and therefore ordered, so a
            // packet whose jittered arrival time landed before its predecessor's must still
            // wait for it — see the clamp in Schedule.
            while (_inFlight.Count > 0 && _inFlight[0].ArriveAt <= nowSeconds)
            {
                arrivals.Add(_inFlight[0]);
                _inFlight.RemoveAt(0);
                Delivered++;
            }
        }

        private void Produce(long tick, double producedAt)
        {
            Produced++;

            Perturbation kind;
            if (Pending != Perturbation.None)
            {
                // A one-shot injection always fires, whatever the repeat setting is doing.
                kind = Pending;
                Pending = Perturbation.None;
            }
            else
            {
                _repeatDue = !_repeatDue;
                kind = _repeatDue ? Repeat : Perturbation.None;
            }

            if (kind == Perturbation.Skip)
            {
                // Never enqueued. The next tick turns up at its own natural time carrying
                // twice the position delta — the entity did not speed up, one statement
                // about it was simply lost.
                Dropped++;
                return;
            }

            double arriveAt = producedAt + BaseLatencySeconds;

            if (kind == Perturbation.Early) arriveAt -= EarlyShiftSeconds;
            if (kind == Perturbation.Late) arriveAt += LateShiftSeconds;

            if (JitterSeconds > 0.0)
            {
                arriveAt += (_random.NextDouble() * 2.0 - 1.0) * JitterSeconds;
            }

            PositionAt(tick, out var x, out var y);
            Schedule(new Packet { Tick = tick, X = x, Y = y, ArriveAt = arriveAt });
        }

        private void Schedule(Packet packet)
        {
            // Ordered delivery. Without this a large jitter setting would reorder packets,
            // and the ring's admission rule would then drop the older one — correct
            // behaviour, but it would show up on screen as extra packet loss the viewer did
            // not ask for and would read as a defect in the interpolator.
            if (packet.ArriveAt <= _lastArrivalScheduled)
            {
                packet.ArriveAt = _lastArrivalScheduled + 0.001;
            }

            _lastArrivalScheduled = packet.ArriveAt;
            _inFlight.Add(packet);
        }
    }
}
