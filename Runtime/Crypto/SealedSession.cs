using System;

namespace Cuvara.Netcode.Crypto
{
    /// <summary>Why a sealed frame was not accepted.</summary>
    public enum SealedOpenResult
    {
        /// <summary>Accepted.</summary>
        Ok = 0,

        /// <summary>
        /// Not a sealed frame. Distinguished from failure because the caller treats it
        /// differently: during a handshake it is expected; afterwards it is a peer sending
        /// cleartext where a sealed frame is required, which must end the session.
        /// </summary>
        NotSealed,

        /// <summary>
        /// Rejected. Deliberately one value for "tag did not verify" and "replayed": the
        /// caller's response is identical — end the session — and distinguishing them tells an
        /// attacker whether a forged tag reached the replay window.
        /// </summary>
        Rejected,
    }

    /// <summary>
    /// Seals and opens frames for one direction, and is the reason the "check the sequence
    /// only after the tag verifies" rule cannot be got wrong. Mirrors the server's
    /// <c>SealedSession</c> and <c>shared/sealed.Session</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this type exists rather than a documented convention.</b> The rule is easy to
    /// state and easy to lose. The sequence number is cleartext and sits right there in the
    /// header, so the natural-looking implementation reads it, checks the replay window, and
    /// only then spends CPU on the AEAD — which is exactly backwards. That ordering lets an
    /// attacker replay a captured header with garbage after it and advance the peer's window
    /// without holding any key, locking out the real sender. It costs nothing to mount and
    /// would present as a connectivity bug, in the wrong layer, for as long as it took someone
    /// to suspect it.
    /// </para>
    /// <para>
    /// A comment does not survive an optimisation pass. <see cref="Open"/> performs both steps
    /// itself, in the only correct order, and exposes no way to do one without the other.
    /// </para>
    /// <para>
    /// The AEAD must be keyed for ONE direction. One key serving both makes the counter nonce
    /// repeat across them, which is the catastrophic case documented on
    /// <see cref="SealedFrame.WriteNonce"/>.
    /// </para>
    /// </remarks>
    public sealed class SealedSession
    {
        private readonly ISealedAead _aead;
        private readonly ISequenceValidator _validator;
        private ulong _sendSequence;

        /// <summary>Frames whose tag did not verify.</summary>
        public ulong RejectedNotAuthenticated { get; private set; }

        /// <summary>Frames refused as already seen or too old to judge.</summary>
        public ulong RejectedReplayed { get; private set; }

        /// <summary>Frames refused for leaping past <see cref="SequenceValidators.MaxForwardJump"/>.</summary>
        public ulong RejectedForwardJump { get; private set; }

        /// <summary>Bodies that were not sealed frames at all.</summary>
        public ulong RejectedNotSealed { get; private set; }

        /// <summary>Every refusal, by any cause.</summary>
        /// <remarks>
        /// These counters exist because a rejected sealed frame is otherwise
        /// indistinguishable from an ordinary disconnect — both end as a closed socket. A
        /// security check that fires and looks exactly like normal traffic will be assumed
        /// to be working for as long as nobody deliberately breaks it. They are split by
        /// cause for the operator and reported to the peer as one answer, which is the
        /// point: the peer must not learn WHY.
        /// </remarks>
        public ulong RejectedTotal
        {
            get { return RejectedNotAuthenticated + RejectedReplayed + RejectedForwardJump + RejectedNotSealed; }
        }

        /// <summary>Build a session around a one-direction AEAD and a replay validator.</summary>
        /// <param name="aead">Keyed for ONE direction.</param>
        /// <param name="validator">The replay rule.</param>
        /// <param name="orderedDelivery">
        /// Whether the transport underneath cannot reorder. Defaults to <c>true</c> because
        /// both shipped transports guarantee it — TCP by definition, KCP through its
        /// reassembly path. A validator that requires ordering refuses to be paired with a
        /// transport that does not, so a future QUIC-datagram or raw-UDP transport fails
        /// closed on day one instead of silently dropping legitimate frames.
        /// </param>
        public SealedSession(ISealedAead aead, ISequenceValidator validator, bool orderedDelivery = true)
        {
            if (aead == null) throw new ArgumentNullException(nameof(aead));
            if (validator == null) throw new ArgumentNullException(nameof(validator));

            if (validator.RequiresOrderedTransport && !orderedDelivery)
                throw new ArgumentException(
                    validator.GetType().Name + " is only correct on a transport that cannot reorder, " +
                    "and this one can. Use SlidingWindowSequence, or do not claim unordered delivery.",
                    nameof(validator));

            if (aead.NonceSize != SealedFrame.NonceSize)
                throw new ArgumentException(
                    $"AEAD nonce size {aead.NonceSize}, want {SealedFrame.NonceSize}", nameof(aead));
            if (aead.TagSize != SealedFrame.TagSize)
                throw new ArgumentException(
                    $"AEAD tag size {aead.TagSize}, want {SealedFrame.TagSize}", nameof(aead));

            _aead = aead;
            _validator = validator;
        }

        /// <summary>Sequence the next <see cref="Seal"/> will use. Diagnostics.</summary>
        public ulong NextSendSequence { get { return _sendSequence; } }

        /// <summary>Highest sequence accepted. Diagnostics.</summary>
        public ulong HighestReceived { get { return _validator.Highest; } }

        /// <summary>Wrap one Envelope's bytes as a sealed frame body.</summary>
        public byte[] Seal(ReadOnlySpan<byte> envelope)
        {
            ulong sequence = _sendSequence;
            // Incremented before use, deliberately: an early return must not leave the counter
            // reusable. A nonce reused after a failed send is the same catastrophe as one
            // reused on purpose.
            _sendSequence++;

            var frame = new byte[SealedFrame.HeaderSize + envelope.Length + SealedFrame.TagSize];
            SealedFrame.WriteHeader(frame, sequence);

            Span<byte> nonce = stackalloc byte[SealedFrame.NonceSize];
            SealedFrame.WriteNonce(nonce, sequence);

            int written = _aead.Seal(
                nonce, envelope, frame.AsSpan(0, SealedFrame.HeaderSize),
                frame.AsSpan(SealedFrame.HeaderSize));

            if (written != envelope.Length + SealedFrame.TagSize)
                throw new InvalidOperationException("AEAD wrote an unexpected number of bytes");

            return frame;
        }

        /// <summary>
        /// Verify and unwrap a sealed frame body, then check it for replay.
        /// </summary>
        /// <remarks>
        /// The order is the whole point of this type and must not be rearranged: the AEAD runs
        /// FIRST, and the sequence reaches the validator only once the tag has proved the
        /// header was not forged.
        /// </remarks>
        public SealedOpenResult Open(ReadOnlySpan<byte> body, out byte[] plaintext)
        {
            plaintext = Array.Empty<byte>();

            SealedFrameError headerError;
            ulong sequence;
            if (!SealedFrame.TryReadHeader(body, out sequence, out headerError))
            {
                if (headerError == SealedFrameError.NotSealed)
                {
                    RejectedNotSealed++;
                    return SealedOpenResult.NotSealed;
                }
                RejectedNotAuthenticated++;
                return SealedOpenResult.Rejected;
            }

            ReadOnlySpan<byte> aad = body.Slice(0, SealedFrame.HeaderSize);
            ReadOnlySpan<byte> ciphertext = body.Slice(SealedFrame.HeaderSize);

            Span<byte> nonce = stackalloc byte[SealedFrame.NonceSize];
            SealedFrame.WriteNonce(nonce, sequence);

            var buffer = new byte[ciphertext.Length - SealedFrame.TagSize];

            // STEP 1: authenticate. Nothing below may act on anything the header claimed until
            // this succeeds.
            int written;
            if (!_aead.TryOpen(nonce, ciphertext, aad, buffer, out written))
            {
                RejectedNotAuthenticated++;
                return SealedOpenResult.Rejected;
            }

            // STEP 2: only now is the sequence a fact rather than a claim.
            SequenceResult sequenceResult = _validator.Accept(sequence);
            if (sequenceResult != SequenceResult.Accepted)
            {
                if (sequenceResult == SequenceResult.ForwardJump) RejectedForwardJump++;
                else RejectedReplayed++;
                return SealedOpenResult.Rejected;
            }

            plaintext = written == buffer.Length ? buffer : buffer.AsSpan(0, written).ToArray();
            return SealedOpenResult.Ok;
        }
    }
}
