namespace Cuvara.Netcode.Interpolation
{
    /// <summary>
    /// One received authoritative state for one entity: where it was, on which server
    /// tick, and when the client heard about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Blittable and free of Unity and ECS types on purpose.</b> The same struct is the
    /// element of the GameObject path's pooled ring and of the DOTS path's
    /// <c>DynamicBuffer</c>, so both paths hand the identical bytes to the identical
    /// <see cref="SnapshotInterpolation.Evaluate{TBuffer}"/>. One copy of the math is the
    /// whole point; see <see cref="Cuvara.Netcode.Prediction.LocalMovePredictor"/>, which
    /// is DOTS-free for the same reason.
    /// </para>
    /// <para>
    /// <b><see cref="Tick"/> is what positions a sample on the timeline, not
    /// <see cref="ReceiveTime"/>.</b> The tick is the server's own statement of when this
    /// state was true; the receive time is a statement about the network, and it carries
    /// every millisecond of queueing, batching and scheduling jitter between the two
    /// machines. Interpolating against receive time makes the rendered speed a function of
    /// packet arrival, which is why a snapshot arriving early used to compress a segment
    /// and one carrying a skipped tick used to render at double speed. Receive time is
    /// kept because the clock needs it to measure how long a tick takes in real seconds —
    /// that is all it is for.
    /// </para>
    /// </remarks>
    public struct InterpolationSample
    {
        /// <summary>Server tick this state was true on. Strictly increasing per entity.</summary>
        public long Tick;

        /// <summary>
        /// Seconds on the caller's monotonic clock when this state was received. Used only
        /// to estimate how many seconds a tick takes; never to place the sample.
        /// </summary>
        public double ReceiveTime;

        /// <summary>Authoritative position on the tick.</summary>
        public float X, Y;
    }
}
