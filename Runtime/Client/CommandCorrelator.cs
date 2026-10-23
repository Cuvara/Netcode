using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;

namespace Cuvara.Netcode.Client
{
    /// <summary>
    /// Matches each <see cref="CommandResult"/> to the <see cref="CommandRequest"/> it answers,
    /// by <c>seq</c>, for one gameplay connection (ADR-30).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One per connection, and seq restarts at 1 for each.</b> The server keys
    /// <c>seq</c> by connection; a reconnect is a new connection, and a result can never
    /// arrive for a request the previous one sent. <see cref="FailAll"/> therefore completes
    /// everything in flight with <see cref="CommandChannelErrors.ConnectionClosed"/> when the
    /// connection ends, and refuses new requests until <see cref="Open"/> starts the next.
    /// </para>
    /// <para>
    /// <b>Zero is never a seq.</b> The wire reserves it ("starts at 1 so a zero can never be
    /// mistaken for a real request"), and the counter skips it on wrap.
    /// </para>
    /// <para>
    /// Thread-safe: requests are begun from the game thread and results completed from the
    /// connection's read loop.
    /// </para>
    /// </remarks>
    internal sealed class CommandCorrelator
    {
        private readonly object _gate = new object();
        private readonly Dictionary<uint, UniTaskCompletionSource<CommandResult>> _pending =
            new Dictionary<uint, UniTaskCompletionSource<CommandResult>>();

        private uint _lastSeq;
        private bool _open;

        /// <summary>Requests sent and not yet answered, failed or cancelled.</summary>
        public int PendingCount
        {
            get
            {
                lock (_gate)
                {
                    return _pending.Count;
                }
            }
        }

        /// <summary>The seq most recently allocated on this connection; 0 before the first.</summary>
        public uint LastSeq
        {
            get
            {
                lock (_gate)
                {
                    return _lastSeq;
                }
            }
        }

        /// <summary>Results that named a seq with nothing waiting for it (late, duplicate, or cancelled).</summary>
        public int UnmatchedResults { get; private set; }

        /// <summary>
        /// Starts a new connection: seq restarts at 1. Anything still pending from the previous
        /// one is failed first -- it can never be answered now.
        /// </summary>
        public void Open()
        {
            FailAll(CommandChannelErrors.ConnectionClosed);
            lock (_gate)
            {
                _lastSeq = 0;
                _open = true;
            }
        }

        /// <summary>
        /// Allocates the next seq and registers a waiter for its result. Returns false, and
        /// allocates nothing, when the connection is not open.
        /// </summary>
        public bool TryBegin(out uint seq, out UniTaskCompletionSource<CommandResult> waiter)
        {
            lock (_gate)
            {
                if (!_open)
                {
                    seq = 0;
                    waiter = null;
                    return false;
                }

                _lastSeq++;
                if (_lastSeq == 0)
                {
                    // Wrapped: zero is reserved on the wire.
                    _lastSeq = 1;
                }

                seq = _lastSeq;
                waiter = new UniTaskCompletionSource<CommandResult>();
                _pending[seq] = waiter;
                return true;
            }
        }

        /// <summary>
        /// Completes the request <paramref name="result"/> answers. Returns false when nothing
        /// was waiting for that seq; the result is then counted in <see cref="UnmatchedResults"/>
        /// and otherwise ignored.
        /// </summary>
        public bool Complete(CommandResult result)
        {
            if (result == null)
            {
                return false;
            }

            UniTaskCompletionSource<CommandResult> waiter;
            lock (_gate)
            {
                if (!_pending.TryGetValue(result.Seq, out waiter))
                {
                    UnmatchedResults++;
                    return false;
                }

                _pending.Remove(result.Seq);
            }

            // Outside the lock: TrySetResult runs the awaiting continuation inline, and that
            // continuation may well send the next command.
            waiter.TrySetResult(result);
            return true;
        }

        /// <summary>Abandons one request on the caller's cancellation. Returns whether it was still pending.</summary>
        public bool Cancel(uint seq, CancellationToken cancellationToken)
        {
            UniTaskCompletionSource<CommandResult> waiter;
            lock (_gate)
            {
                if (!_pending.TryGetValue(seq, out waiter))
                {
                    return false;
                }

                _pending.Remove(seq);
            }

            waiter.TrySetCanceled(cancellationToken);
            return true;
        }

        /// <summary>
        /// Closes the connection's channel and completes every pending request with a local
        /// failure named <paramref name="error"/>. Later <see cref="TryBegin"/> calls fail until
        /// <see cref="Open"/>.
        /// </summary>
        public void FailAll(string error)
        {
            List<KeyValuePair<uint, UniTaskCompletionSource<CommandResult>>> failed = null;
            lock (_gate)
            {
                _open = false;
                if (_pending.Count > 0)
                {
                    failed = new List<KeyValuePair<uint, UniTaskCompletionSource<CommandResult>>>(_pending);
                    _pending.Clear();
                }
            }

            if (failed == null)
            {
                return;
            }

            foreach (var entry in failed)
            {
                entry.Value.TrySetResult(LocalFailure(entry.Key, error));
            }
        }

        /// <summary>
        /// The reason a command cannot be sent at all, or null when it can.
        /// </summary>
        /// <param name="connected">Whether a gameplay connection is up.</param>
        /// <param name="serverProtocolVersion">The version the game server echoed on join.</param>
        public static string Precheck(bool connected, uint serverProtocolVersion)
        {
            if (!connected)
            {
                return CommandChannelErrors.NotConnected;
            }

            return WireProtocolVersion.Supports(serverProtocolVersion, WireProtocolVersion.CommandChannel)
                ? null
                : CommandChannelErrors.ProtocolTooOld;
        }

        /// <summary>A result for a command the server never answered.</summary>
        public static CommandResult LocalFailure(uint seq, string error) => new CommandResult
        {
            Seq = seq,
            Ok = false,
            Error = error,
            Payload = Array.Empty<byte>(),
        };
    }
}
