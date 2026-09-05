using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Diagnostics;

namespace Cuvara.Netcode.Tests.Editor
{
    [TestFixture]
    public sealed class NetworkMetricsTests
    {
        [Test]
        public void NoTicksProduceNoSnapshot()
        {
            var metrics = new NetworkMetrics(5f);
            var published = new List<NetworkMetricsSnapshot>();
            metrics.Updated += s => published.Add(s);

            metrics.RecordSnapshot(1, 0);
            metrics.RecordRtt(20);

            Assert.That(published, Has.Count.EqualTo(0));
            Assert.That(metrics.Current.SnapshotRate, Is.EqualTo(0f));
        }

        [Test]
        public void WindowPublishesAfterEnoughTicks()
        {
            var metrics = new NetworkMetrics(1f);
            var published = new List<NetworkMetricsSnapshot>();
            metrics.Updated += s => published.Add(s);

            for (int i = 0; i < 15; i++)
                metrics.RecordSnapshot(i + 1, i);

            metrics.RecordRtt(25);

            // Tick 1.1 seconds total -> window should close.
            for (int i = 0; i < 11; i++)
                metrics.Tick(0.1f);

            Assert.That(published, Has.Count.EqualTo(1));
            Assert.That(published[0].SnapshotRate, Is.GreaterThan(13f).And.LessThan(15f));
            Assert.That(published[0].ServerTick, Is.EqualTo(15));
            Assert.That(published[0].AckTick, Is.EqualTo(14));
            Assert.That(published[0].RttMs, Is.EqualTo(25f));
        }

        [Test]
        public void RttSmoothing()
        {
            var metrics = new NetworkMetrics(10f);

            metrics.RecordRtt(100);
            metrics.RecordRtt(100);
            metrics.RecordRtt(100);
            metrics.RecordRtt(200); // spike

            // Tick to publish.
            metrics.Tick(10f);

            // The EMA should be closer to 100 than to 200.
            Assert.That(metrics.Current.RttMs, Is.GreaterThan(100f).And.LessThan(150f));
            Assert.That(metrics.Current.RttJitterMs, Is.GreaterThan(0f));
        }

        [Test]
        public void BytesTracking()
        {
            var metrics = new NetworkMetrics(2f);

            metrics.RecordBytes(1000, 5000);
            metrics.RecordBytes(1000, 5000);
            metrics.Tick(2f);

            Assert.That(metrics.Current.BytesSentPerSecond, Is.EqualTo(1000f));
            Assert.That(metrics.Current.BytesReceivedPerSecond, Is.EqualTo(5000f));
        }

        [Test]
        public void ReconciliationTracking()
        {
            var metrics = new NetworkMetrics(1f);

            metrics.RecordReconciliation(0.05f);
            metrics.RecordReconciliation(0.10f);
            metrics.RecordReconciliation(0.15f);
            metrics.Tick(1f);

            Assert.That(metrics.Current.Reconciliations, Is.EqualTo(3));
            Assert.That(metrics.Current.MeanCorrectionUnits, Is.EqualTo(0.1f).Within(0.001f));
        }

        [Test]
        public void ResetClearsEverything()
        {
            var metrics = new NetworkMetrics(1f);

            metrics.RecordSnapshot(100, 99);
            metrics.RecordRtt(50);
            metrics.RecordBytes(500, 1000);
            metrics.RecordReconciliation(0.1f);
            metrics.Tick(1f);

            Assert.That(metrics.Current.ServerTick, Is.EqualTo(100));

            metrics.Reset();

            Assert.That(metrics.Current.ServerTick, Is.EqualTo(0));
            Assert.That(metrics.Current.RttMs, Is.EqualTo(0f));
        }

        [Test]
        public void MultipleWindowsAccumulateIndependently()
        {
            var metrics = new NetworkMetrics(1f);
            var published = new List<NetworkMetricsSnapshot>();
            metrics.Updated += s => published.Add(s);

            // Window 1: 10 snapshots.
            for (int i = 0; i < 10; i++) metrics.RecordSnapshot(i, 0);
            metrics.Tick(1f);

            // Window 2: 5 snapshots.
            for (int i = 0; i < 5; i++) metrics.RecordSnapshot(10 + i, 0);
            metrics.Tick(1f);

            Assert.That(published, Has.Count.EqualTo(2));
            Assert.That(published[0].SnapshotRate, Is.EqualTo(10f));
            Assert.That(published[1].SnapshotRate, Is.EqualTo(5f));
        }

        [Test]
        public void ZeroReconciliationsMeanZeroCorrection()
        {
            var metrics = new NetworkMetrics(1f);
            metrics.Tick(1f);

            Assert.That(metrics.Current.Reconciliations, Is.EqualTo(0));
            Assert.That(metrics.Current.MeanCorrectionUnits, Is.EqualTo(0f));
        }
    }
}
