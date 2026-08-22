namespace Cuvara.Netcode.Interpolation
{
    /// <summary>
    /// Read-only view of one entity's retained samples, oldest first, so that
    /// <see cref="SnapshotInterpolation.Evaluate{TBuffer}"/> can be written once and used
    /// over storage as different as a pooled managed array and a DOTS
    /// <c>DynamicBuffer</c>.
    /// </summary>
    /// <remarks>
    /// <b>Always constrain to <c>where TBuffer : struct, ISampleBuffer</c>, never take this
    /// as an interface-typed parameter.</b> A generic struct constraint gives a constrained
    /// call, which the JIT devirtualises and Burst specialises per concrete buffer; an
    /// interface-typed parameter boxes the struct on every call and defeats both. That
    /// distinction is the reason this type exists in this shape.
    /// </remarks>
    public interface ISampleBuffer
    {
        /// <summary>Number of samples held. Zero is legal and means "nothing to render".</summary>
        int Length { get; }

        /// <summary>Sample by age, <c>0</c> oldest, <c>Length - 1</c> newest.</summary>
        InterpolationSample this[int index] { get; }
    }
}
