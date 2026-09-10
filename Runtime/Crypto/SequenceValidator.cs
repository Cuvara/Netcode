namespace Cuvara.Netcode.Crypto
{
    /// <summary>
    /// Decides whether a frame's sequence number is fresh. Mirrors the server's
    /// <c>ISequenceValidator</c>.
    /// </summary>
    /// <remarks>
    /// <b>Call this only after the tag verifies.</b> The sequence is cleartext, so accepting
    /// it before the AEAD succeeds lets an attacker advance a peer's window with forged frames
    /// and lock out the real sender — denial of service that costs nothing to mount.
    /// <see cref="SealedSession.Open"/> enforces the order so no call site can get it wrong.
    /// </remarks>
    public interface ISequenceValidator
    {
        /// <summary>
        /// Record <paramref name="sequence"/> as seen and report whether it was fresh. False
        /// means drop the frame and end the session: there is no benign replay on this
        /// protocol.
        /// </summary>
        bool Accept(ulong sequence);

        /// <summary>Largest sequence accepted so far. Diagnostics only.</summary>
        ulong Highest { get; }
    }

    /// <summary>
    /// Accepts strictly increasing sequences and nothing else. Correct when the transport
    /// below cannot reorder: TCP by definition, and KCP in the reliable ordered mode this
    /// project configures.
    /// </summary>
    /// <remarks>
    /// This is the shipped default, and the choice came from a measurement rather than from
    /// whichever was written first — ADR-22 / BENCHMARK.md Part XII recorded 0 reordered
    /// frames in 22,374 over both transports.
    /// </remarks>
    public sealed class StrictMonotonicSequence : ISequenceValidator
    {
        private ulong _highest;
        private bool _seen;

        /// <inheritdoc />
        public ulong Highest { get { return _highest; } }

        /// <inheritdoc />
        public bool Accept(ulong sequence)
        {
            if (_seen && sequence <= _highest) return false;
            _highest = sequence;
            _seen = true;
            return true;
        }
    }

    /// <summary>
    /// Accepts any sequence not already seen and not older than the window behind the highest
    /// accepted — the rule IPsec and DTLS use. Kept so that a transport change is a one-line
    /// edit at the call site rather than a rewrite.
    /// </summary>
    public sealed class SlidingWindowSequence : ISequenceValidator
    {
        /// <summary>
        /// Default width in frames. 64 is one machine word, so the bitmap is a single
        /// <see cref="ulong"/> and membership is two instructions.
        /// </summary>
        public const int DefaultWidth = 64;

        private readonly int _width;
        private ulong _highest;
        private ulong _bitmap; // bit i set => (highest - i) accepted
        private bool _seen;

        /// <summary>A window of the default width.</summary>
        public SlidingWindowSequence() : this(DefaultWidth) { }

        /// <summary>A window of an explicit width, capped at 64 because the bitmap is one word.</summary>
        public SlidingWindowSequence(int width)
        {
            _width = (width <= 0 || width > DefaultWidth) ? DefaultWidth : width;
        }

        /// <inheritdoc />
        public ulong Highest { get { return _highest; } }

        /// <inheritdoc />
        public bool Accept(ulong sequence)
        {
            if (!_seen)
            {
                _seen = true;
                _highest = sequence;
                _bitmap = 1;
                return true;
            }

            if (sequence > _highest)
            {
                // Advance. Frames between the old and new high water are still acceptable if
                // they arrive later, so the bitmap shifts rather than clearing — that is the
                // whole difference from a strict counter.
                ulong shift = sequence - _highest;
                _bitmap = shift >= (ulong)_width ? 0 : _bitmap << (int)shift;
                _bitmap |= 1;
                _highest = sequence;
                return true;
            }

            if (sequence == _highest) return false;

            ulong behind = _highest - sequence;
            // Too old to judge. Refused rather than accepted: a validator that cannot prove a
            // frame is fresh must not claim that it is.
            if (behind >= (ulong)_width) return false;

            ulong mask = 1UL << (int)behind;
            if ((_bitmap & mask) != 0) return false;

            _bitmap |= mask;
            return true;
        }
    }
}
