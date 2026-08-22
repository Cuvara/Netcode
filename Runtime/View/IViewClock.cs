namespace Cuvara.Netcode.View
{
    /// <summary>
    /// The clock <see cref="WorldViewBinder"/> derives remote interpolation from, so that
    /// the interpolation curve can be sampled at chosen instants instead of at whatever
    /// instants a test happened to execute at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a seam exists at all.</b> The binder's interpolation factor is
    /// <c>(now − arrival) / measuredInterval</c>. Both terms come from this clock, so
    /// every property worth asserting about the rendered motion — that it never steps
    /// backwards, that a skipped server tick does not double the rendered speed — is a
    /// property of the curve <i>between</i> arrivals. A test that cannot choose "now"
    /// can only ever sample the instant it arrived, which is <c>t ≈ 0</c>, which is the
    /// one point on the curve where every one of those properties is trivially true.
    /// </para>
    /// <para>
    /// <b>Arrival time and frame time are both chosen through this one value</b>, and
    /// that is deliberate rather than a limitation: the binder stamps an arrival with
    /// whatever this reads on the pass that carries a new snapshot, and computes the
    /// render phase with whatever it reads on every other pass. Setting it before each
    /// call therefore places arrivals and frames independently on the same timeline —
    /// which is what the defect needs, because the defect is precisely the relationship
    /// between the two.
    /// </para>
    /// <para>
    /// Production uses <see cref="StopwatchViewClock"/> and nothing else. This exists for
    /// the same reason <see cref="Cuvara.Netcode.Diagnostics.INetLog"/> does: to keep the
    /// thing being tested free of an untestable source of truth.
    /// </para>
    /// </remarks>
    public interface IViewClock
    {
        /// <summary>
        /// Milliseconds since an arbitrary but fixed origin. Only differences are read,
        /// so the origin is free; it must be monotonic and must not wrap.
        /// </summary>
        double NowMs { get; }
    }
}
