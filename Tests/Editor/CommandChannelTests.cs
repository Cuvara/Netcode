using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The command channel (ADR-30, protocol version 3): seq correlation per connection, the
    /// protocol gate, and the rule that an in-flight command FAILS rather than hangs when its
    /// connection goes away.
    /// </summary>
    /// <remarks>
    /// Driven through <see cref="CommandCorrelator"/>, the part of
    /// <see cref="GameSessionClient"/> that owns the bookkeeping, so every completion here is
    /// synchronous and no socket is needed. The session-level gate is exercised on a session
    /// that never joined, which is the one state reachable without a server.
    /// </remarks>
    public class CommandChannelTests
    {
        private static CommandResult Answer(uint seq, bool ok = true, string error = "") =>
            new CommandResult { Seq = seq, Ok = ok, Error = error, Payload = new byte[] { (byte)seq } };

        private static CommandResult Completed(UniTaskCompletionSource<CommandResult> waiter)
        {
            var task = waiter.Task;
            Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Succeeded), "the command is still pending");
            return task.GetAwaiter().GetResult();
        }

        // ── seq ──────────────────────────────────────────────────────────────────

        [Test]
        public void SeqStartsAtOneAndIncrementsPerCommand()
        {
            var c = new CommandCorrelator();
            c.Open();

            Assert.That(c.TryBegin(out var a, out _), Is.True);
            Assert.That(c.TryBegin(out var b, out _), Is.True);
            Assert.That(c.TryBegin(out var d, out _), Is.True);

            Assert.That(new[] { a, b, d }, Is.EqualTo(new[] { 1u, 2u, 3u }),
                "zero is reserved on the wire, so the first command is 1");
            Assert.That(c.PendingCount, Is.EqualTo(3));
        }

        [Test]
        public void ResultsAreMatchedBySeq_InWhateverOrderTheyArrive()
        {
            var c = new CommandCorrelator();
            c.Open();
            c.TryBegin(out var first, out var firstWaiter);
            c.TryBegin(out var second, out var secondWaiter);

            Assert.That(c.Complete(Answer(second, ok: false, error: "rate_limited")), Is.True);
            Assert.That(firstWaiter.Task.Status, Is.EqualTo(UniTaskStatus.Pending),
                "the second result must not complete the first command");
            Assert.That(c.Complete(Answer(first)), Is.True);

            var r1 = Completed(firstWaiter);
            var r2 = Completed(secondWaiter);
            Assert.That(r1.Seq, Is.EqualTo(first));
            Assert.That(r1.Ok, Is.True);
            Assert.That(r1.Payload, Is.EqualTo(new[] { (byte)first }));
            Assert.That(r2.Seq, Is.EqualTo(second));
            Assert.That(r2.Error, Is.EqualTo("rate_limited"));
            Assert.That(c.PendingCount, Is.Zero);
        }

        [Test]
        public void AResultForNoPendingCommandIsCountedAndIgnored()
        {
            var c = new CommandCorrelator();
            c.Open();
            c.TryBegin(out var seq, out var waiter);

            Assert.That(c.Complete(Answer(seq + 41)), Is.False);
            Assert.That(c.UnmatchedResults, Is.EqualTo(1));
            Assert.That(waiter.Task.Status, Is.EqualTo(UniTaskStatus.Pending));

            Assert.That(c.Complete(Answer(seq)), Is.True);
            Assert.That(c.Complete(Answer(seq)), Is.False, "a duplicate answer matches nothing");
            Assert.That(c.UnmatchedResults, Is.EqualTo(2));
        }

        // ── connection lifetime ──────────────────────────────────────────────────

        [Test]
        public void ClosingTheConnectionFailsEveryInFlightCommandWithANamedError()
        {
            var c = new CommandCorrelator();
            c.Open();
            c.TryBegin(out var a, out var aWaiter);
            c.TryBegin(out var b, out var bWaiter);

            c.FailAll(CommandChannelErrors.ConnectionClosed);

            var ra = Completed(aWaiter);
            var rb = Completed(bWaiter);
            Assert.That(ra.Ok, Is.False);
            Assert.That(ra.Error, Is.EqualTo(CommandChannelErrors.ConnectionClosed));
            Assert.That(ra.Seq, Is.EqualTo(a));
            Assert.That(rb.Error, Is.EqualTo(CommandChannelErrors.ConnectionClosed));
            Assert.That(rb.Seq, Is.EqualTo(b));
            Assert.That(CommandChannelErrors.IsLocal(ra.Error), Is.True);
            Assert.That(c.PendingCount, Is.Zero);

            // A late answer from the dead connection completes nothing.
            Assert.That(c.Complete(Answer(a)), Is.False);
        }

        [Test]
        public void AClosedChannelRefusesNewCommandsUntilTheNextConnectionOpens()
        {
            var c = new CommandCorrelator();
            Assert.That(c.TryBegin(out _, out _), Is.False, "never opened");

            c.Open();
            c.FailAll(CommandChannelErrors.ConnectionClosed);
            Assert.That(c.TryBegin(out _, out _), Is.False, "closed");
        }

        /// <summary>
        /// A reconnect is a new connection: seq restarts at 1, and anything the old one still
        /// had in flight has been failed rather than carried over to be matched against the
        /// new connection's seq space.
        /// </summary>
        [Test]
        public void ANewConnectionRestartsSeqAtOne_AndFailsWhatTheOldOneLeftPending()
        {
            var c = new CommandCorrelator();
            c.Open();
            c.TryBegin(out _, out _);
            c.TryBegin(out _, out var stranded);

            c.Open();

            Assert.That(Completed(stranded).Error, Is.EqualTo(CommandChannelErrors.ConnectionClosed));
            Assert.That(c.TryBegin(out var seq, out _), Is.True);
            Assert.That(seq, Is.EqualTo(1u));
        }

        [Test]
        public void CancellingStopsTheWaitAndALateAnswerIsUnmatched()
        {
            var c = new CommandCorrelator();
            c.Open();
            c.TryBegin(out var seq, out var waiter);

            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.That(c.Cancel(seq, cts.Token), Is.True);
            }

            Assert.That(waiter.Task.Status, Is.EqualTo(UniTaskStatus.Canceled));
            Assert.That(c.Complete(Answer(seq)), Is.False);
            Assert.That(c.PendingCount, Is.Zero);
        }

        // ── the protocol gate ────────────────────────────────────────────────────

        [Test]
        public void PrecheckRefusesAServerBelowProtocol3ByName()
        {
            Assert.That(CommandCorrelator.Precheck(true, 3u), Is.Null);
            Assert.That(CommandCorrelator.Precheck(true, 2u), Is.EqualTo(CommandChannelErrors.ProtocolTooOld));
            Assert.That(CommandCorrelator.Precheck(true, WireProtocolVersion.Unversioned),
                Is.EqualTo(CommandChannelErrors.ProtocolTooOld),
                "an unversioned server predates every versioned feature");
            Assert.That(CommandCorrelator.Precheck(false, 3u), Is.EqualTo(CommandChannelErrors.NotConnected));
        }

        [Test]
        public void VersionSupportIsAThresholdAndUnversionedSupportsNothing()
        {
            Assert.That(WireProtocolVersion.Supports(3u, WireProtocolVersion.CommandChannel), Is.True);
            Assert.That(WireProtocolVersion.Supports(2u, WireProtocolVersion.CommandChannel), Is.False);
            Assert.That(WireProtocolVersion.Supports(0u, WireProtocolVersion.CommandChannel), Is.False);
            Assert.That(WireProtocolVersion.Supports(3u, WireProtocolVersion.Motor3D), Is.True);
            Assert.That(WireProtocolVersion.Supports(2u, WireProtocolVersion.Motor3D), Is.False);
        }

        [Test]
        public void AVersion2ServerIsStillCompatible()
        {
            Assert.That(WireProtocolVersion.IsCompatible(2u), Is.True,
                "a version 3 server keeps serving version 2; a version 2 server must stay reachable");
            Assert.That(WireProtocolVersion.IsCompatible(3u), Is.True);
            Assert.That(WireProtocolVersion.IsCompatible(1u), Is.False);
        }

        private static GameSessionClient UnjoinedSession() =>
            new GameSessionClient(new NetworkSettings(), new DefaultTransportFactory(), new ProtobufWireCodec(), new NullLog());

        [Test]
        public void ASessionThatNeverJoinedAnswersNotConnected_WithoutSending()
        {
            using (var session = UnjoinedSession())
            {
                Assert.That(session.SupportsCommands, Is.False);
                var task = session.SendCommandAsync(1u, new byte[] { 1 });
                Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Succeeded), "must fail fast, not hang");
                var result = task.GetAwaiter().GetResult();
                Assert.That(result.Ok, Is.False);
                Assert.That(result.Error, Is.EqualTo(CommandChannelErrors.NotConnected));
                Assert.That(session.PendingCommandCount, Is.Zero);
            }
        }

        [Test]
        public void OpcodeZeroIsRefusedBeforeAnythingElse()
        {
            using (var session = UnjoinedSession())
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => session.SendCommandAsync(0u, null));
            }
        }

        [Test]
        public void ANetworkClientWithNoSessionAnswersNotConnected()
        {
            using (var client = new NetworkClient(
                new NetworkSettings(), new DefaultTransportFactory(), new ProtobufWireCodec(), new NullLog()))
            {
                var task = client.SendCommandAsync(1u, null);
                Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Succeeded));
                Assert.That(task.GetAwaiter().GetResult().Error, Is.EqualTo(CommandChannelErrors.NotConnected));
                Assert.That(client.ActiveCharacterId, Is.Empty);
                Assert.That(client.ServerProtocolVersion, Is.EqualTo(0u));
            }
        }

        [Test]
        public void LocalErrorNamesNeverCollideWithServerCodes()
        {
            foreach (var server in new[] { "unknown_opcode", "rate_limited", "invalid_payload", "" })
            {
                Assert.That(CommandChannelErrors.IsLocal(server), Is.False, server);
            }

            Assert.That(CommandChannelErrors.IsLocal(CommandChannelErrors.NotConnected), Is.True);
            Assert.That(CommandChannelErrors.IsLocal(CommandChannelErrors.ProtocolTooOld), Is.True);
            Assert.That(CommandChannelErrors.IsLocal(CommandChannelErrors.ConnectionClosed), Is.True);
        }

        private sealed class NullLog : INetLog
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception exception = null) { }
        }
    }
}
