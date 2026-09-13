namespace Cuvara.Netcode.Crypto
{
    /// <summary>Why a sequence number was or was not accepted.</summary>
    public enum SequenceResult
    {
        /// <summary>Fresh.</summary>
        Accepted = 0,

        /// <summary>
        /// Already seen, or too old to judge. One value for both, so a peer cannot learn
        /// where the window edge is.
        /// </summary>
        Replayed,

        /// <summary>
        /// Leapt further ahead than <see cref="SequenceValidators.MaxForwardJump"/> allows.
        /// </summary>
        ForwardJump,
    }

    /// <summary>Shared limits for the sequence validators.</summary>
    public static class SequenceValidators
    {
        /// <summary>How far a sequence may advance in a single frame.</summary>
        /// <remarks>
        /// <para>
        /// <b>Why the backward half of the rule is not the whole rule.</b> "Reject anything at
        /// or below the highest seen" stops replays and says nothing about a leap FORWARD. A
        /// peer whose counter is corrupted — or is being steered by anything that can
        /// influence it — can jump to near the top of the space in one frame. Two consequences
        /// follow, and neither is a replay: it burns nonce space, forcing a rekey far earlier
        /// than the traffic justifies; and with a strict counter it is <b>irreversible</b>,
        /// because every legitimate frame after it carries a lower sequence and is refused for
        /// ever. The session is then dead and the symptom is a connection that authenticated
        /// fine and then went quiet.
        /// </para>
        /// <para>
        /// The exposure is bounded to begin with — a forged frame cannot advance anything,
        /// because it does not authenticate — so this defends against a confused or
        /// compromised peer rather than an outsider. Worth having anyway: the bound costs
        /// nothing and the failure without it is silent.
        /// </para>
        /// <para>
        /// 1024 is slack, not a budget. On an ordered reliable transport the expected delta
        /// between consecutive frames is exactly 1.
        /// </para>
        /// </remarks>
        public const ulong MaxForwardJump = 1024;
    }

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
        /// Record <paramref name="sequence"/> as seen and report whether it was fresh.
        /// Anything but <see cref="SequenceResult.Accepted"/> means drop the frame and end
        /// the session: there is no benign replay on this protocol.
        /// </summary>
        SequenceResult Accept(ulong sequence);

        /// <summary>Largest sequence accepted so far. Diagnostics only.</summary>
        ulong Highest { get; }

        /// <summary>Whether this validator is only correct on a transport that cannot reorder.</summary>
        /// <remarks>
        /// It exists so the requirement is ASSERTED rather than assumed. The ordering this
        /// design rests on is inherited, not owned: TCP guarantees it, and KCP gets it from a
        /// reassembly path of roughly ten lines. A future QUIC-datagram or raw-UDP transport
        /// would quietly violate it, and with a strict counter the symptom is dropped
        /// legitimate frames rather than an error naming the cause.
        /// <see cref="SealedSession"/> refuses to construct in that case, so a new transport
        /// fails closed on day one instead of degrading.
        /// </remarks>
        bool RequiresOrderedTransport { get; }
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
        public bool RequiresOrderedTransport { get { return true; } }

        /// <inheritdoc />
        public SequenceResult Accept(ulong sequence)
        {
            if (_seen)
            {
                if (sequence <= _highest) return SequenceResult.Replayed;
                if (sequence - _highest > SequenceValidators.MaxForwardJump) return SequenceResult.ForwardJump;
            }
            _highest = sequence;
            _seen = true;
            return SequenceResult.Accepted;
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
        public bool RequiresOrderedTransport { get { return false; } }

        /// <inheritdoc />
        public SequenceResult Accept(ulong sequence)
        {
            if (!_seen)
            {
                _seen = true;
                _highest = sequence;
                _bitmap = 1;
                return SequenceResult.Accepted;
            }

            if (sequence > _highest && sequence - _highest > SequenceValidators.MaxForwardJump)
                return SequenceResult.ForwardJump;

            if (sequence > _highest)
            {
                // Advance. Frames between the old and new high water are still acceptable if
                // they arrive later, so the bitmap shifts rather than clearing — that is the
                // whole difference from a strict counter.
                ulong shift = sequence - _highest;
                _bitmap = shift >= (ulong)_width ? 0 : _bitmap << (int)shift;
                _bitmap |= 1;
                _highest = sequence;
                return SequenceResult.Accepted;
            }

            if (sequence == _highest) return SequenceResult.Replayed;

            ulong behind = _highest - sequence;
            // Too old to judge. Refused rather than accepted: a validator that cannot prove a
            // frame is fresh must not claim that it is.
            if (behind >= (ulong)_width) return SequenceResult.Replayed;

            ulong mask = 1UL << (int)behind;
            if ((_bitmap & mask) != 0) return SequenceResult.Replayed;

            _bitmap |= mask;
            return SequenceResult.Accepted;
        }
    }
}
