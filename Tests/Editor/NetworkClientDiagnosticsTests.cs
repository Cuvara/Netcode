using System;
using NUnit.Framework;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Diagnostics;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// <see cref="NetworkClientDiagnostics"/> as a value, and <see cref="NetworkMetrics.Sample"/>
    /// turning its running totals into windowed metrics. Unity-free, so the headless job runs it.
    /// </summary>
    [TestFixture]
    public sealed class NetworkClientDiagnosticsTests
    {
        private static NetworkClientDiagnostics With(
            long rtt = 0, long bytesSent = 0, long bytesReceived = 0, long snapshots = 0,
            long serverTick = 0, long ackTick = 0, int generation = 1)
        {
            return new NetworkClientDiagnostics(
                NetworkClientState.InWorld, generation, 3u, rtt, 0u, 0L, 0, 0L, 0L, 0L, 0L,
                bytesReceived, bytesSent, 0L, 0L, 0, 0L, snapshots, serverTick, ackTick, 0, 0);
        }

        [Test]
        public void DefaultValue_IsDisconnectedAndEverythingIsZero()
        {
            var d = default(NetworkClientDiagnostics);

            Assert.That(d.State, Is.EqualTo(NetworkClientState.Disconnected));
            Assert.That(d.Generation, Is.EqualTo(0));
            Assert.That(d.RttMs, Is.EqualTo(0L));
            Assert.That(d.BytesSent + d.BytesReceived + d.FramesSent + d.FramesReceived, Is.EqualTo(0L));
            Assert.That(d.SendQueueDrops + d.ResyncRequests + d.UnresolvedSnapshots, Is.EqualTo(0L));
            Assert.That(d.ReconnectAttempts + d.PendingCommands + d.StaleSnapshotsDropped, Is.EqualTo(0));
        }

        [Test]
        public void Constructor_KeepsEveryFieldInItsOwnSlot()
        {
            var d = new NetworkClientDiagnostics(
                NetworkClientState.Reconnecting, 2, 3u, 4L, 5u, 6L, 7, 8L, 9L, 10L, 11L, 12L, 13L,
                14L, 15L, 16, 17L, 18L, 19L, 20L, 21, 22);

            Assert.That(d.State, Is.EqualTo(NetworkClientState.Reconnecting));
            Assert.That(d.Generation, Is.EqualTo(2));
            Assert.That(d.ServerProtocolVersion, Is.EqualTo(3u));
            Assert.That(d.RttMs, Is.EqualTo(4L));
            Assert.That(d.KcpRtoMs, Is.EqualTo(5u));
            Assert.That(d.KcpSilenceMs, Is.EqualTo(6L));
            Assert.That(d.KcpPendingSendSegments, Is.EqualTo(7));
            Assert.That(d.DatagramsReceived, Is.EqualTo(8L));
            Assert.That(d.DatagramsRejected, Is.EqualTo(9L));
            Assert.That(d.FramesReceived, Is.EqualTo(10L));
            Assert.That(d.FramesSent, Is.EqualTo(11L));
            Assert.That(d.BytesReceived, Is.EqualTo(12L));
            Assert.That(d.BytesSent, Is.EqualTo(13L));
            Assert.That(d.SendQueueDrops, Is.EqualTo(14L));
            Assert.That(d.ResyncRequests, Is.EqualTo(15L));
            Assert.That(d.StaleSnapshotsDropped, Is.EqualTo(16));
            Assert.That(d.UnresolvedSnapshots, Is.EqualTo(17L));
            Assert.That(d.SnapshotsReceived, Is.EqualTo(18L));
            Assert.That(d.ServerTick, Is.EqualTo(19L));
            Assert.That(d.AckTick, Is.EqualTo(20L));
            Assert.That(d.PendingCommands, Is.EqualTo(21));
            Assert.That(d.ReconnectAttempts, Is.EqualTo(22));
        }

        [Test]
        public void ReadingTheStruct_AllocatesNothing()
        {
            var d = With(rtt: 30, bytesSent: 100, bytesReceived: 200, snapshots: 5);
            long sink = 0;

            // Warm up so JIT work is not counted.
            sink += d.RttMs + d.BytesSent;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                sink += With(rtt: i, bytesSent: i, bytesReceived: i, snapshots: i).BytesReceived;
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.EqualTo(0L), "building and reading the value type must not allocate (sink " + sink + ")");
        }

        [Test]
        public void Sample_TurnsRunningTotalsIntoPerWindowRates()
        {
            var metrics = new NetworkMetrics(1f);

            metrics.Sample(With(rtt: 40, bytesSent: 1000, bytesReceived: 4000, snapshots: 10, serverTick: 100, ackTick: 98));
            metrics.Sample(With(rtt: 40, bytesSent: 1500, bytesReceived: 6000, snapshots: 20, serverTick: 200, ackTick: 198));
            metrics.Tick(1f);

            var current = metrics.Current;
            Assert.That(current.BytesSentPerSecond, Is.EqualTo(1500f).Within(0.01f));
            Assert.That(current.BytesReceivedPerSecond, Is.EqualTo(6000f).Within(0.01f));
            Assert.That(current.SnapshotRate, Is.EqualTo(20f).Within(0.01f));
            Assert.That(current.ServerTick, Is.EqualTo(200L));
            Assert.That(current.AckTick, Is.EqualTo(198L));
            Assert.That(current.RttMs, Is.EqualTo(40f).Within(0.01f));
        }

        [Test]
        public void Sample_ACounterThatWentBackwards_IsANewSessionAndCountsAsNew()
        {
            var metrics = new NetworkMetrics(1f);

            metrics.Sample(With(bytesReceived: 10_000, snapshots: 50));
            // Reconnect: the session counters restart from a small value.
            metrics.Sample(With(bytesReceived: 300, snapshots: 3, generation: 2));
            metrics.Tick(1f);

            Assert.That(metrics.Current.BytesReceivedPerSecond, Is.EqualTo(10_300f).Within(0.01f));
            Assert.That(metrics.Current.SnapshotRate, Is.EqualTo(53f).Within(0.01f));
        }

        [Test]
        public void Sample_NewGenerationCountsFullTotalsEvenWhenTheyExceedPreviousSession()
        {
            var metrics = new NetworkMetrics(1f);

            metrics.Sample(With(bytesSent: 100, bytesReceived: 200, snapshots: 10, generation: 1));
            metrics.Sample(With(bytesSent: 300, bytesReceived: 500, snapshots: 30, generation: 2));
            metrics.Tick(1f);

            Assert.That(metrics.Current.BytesSentPerSecond, Is.EqualTo(400f).Within(0.01f));
            Assert.That(metrics.Current.BytesReceivedPerSecond, Is.EqualTo(700f).Within(0.01f));
            Assert.That(metrics.Current.SnapshotRate, Is.EqualTo(40f).Within(0.01f));
        }

        [Test]
        public void Sample_WithNoRoundTripYet_DoesNotSeedRtt()
        {
            var metrics = new NetworkMetrics(1f);

            metrics.Sample(With(rtt: 0, bytesSent: 10));
            metrics.Tick(1f);

            Assert.That(metrics.Current.RttMs, Is.EqualTo(0f));
        }

        [Test]
        public void Reset_ForgetsThePreviousTotals()
        {
            var metrics = new NetworkMetrics(1f);
            metrics.Sample(With(bytesSent: 5000));
            metrics.Reset();

            metrics.Sample(With(bytesSent: 5000));
            metrics.Tick(1f);

            Assert.That(metrics.Current.BytesSentPerSecond, Is.EqualTo(5000f).Within(0.01f));
        }
    }
}
