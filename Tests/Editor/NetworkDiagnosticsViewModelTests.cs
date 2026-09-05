using Cuvara.Netcode.Diagnostics;
using NUnit.Framework;

namespace Cuvara.Netcode.Tests.Editor
{
    public sealed class NetworkDiagnosticsViewModelTests
    {
        [Test]
        public void ViewModel_StoresAllFields()
        {
            var vm = new NetworkDiagnosticsViewModel(
                rttMs: 45.5f, downKBps: 12.3f, upKBps: 1.5f, snapshotHz: 15.0f,
                entityCount: 50, serverTick: 12345, serverTickRate: 15f,
                packetLoss: 0.01f, uptimeSeconds: 120f);

            Assert.AreEqual(45.5f, vm.RttMs, 0.01f);
            Assert.AreEqual(12.3f, vm.DownstreamKBps, 0.01f);
            Assert.AreEqual(1.5f, vm.UpstreamKBps, 0.01f);
            Assert.AreEqual(15.0f, vm.SnapshotHz, 0.01f);
            Assert.AreEqual(50, vm.EntityCount);
            Assert.AreEqual(12345UL, vm.ServerTick);
            Assert.AreEqual(15f, vm.ServerTickRate, 0.01f);
            Assert.AreEqual(0.01f, vm.PacketLoss, 0.001f);
            Assert.AreEqual(120f, vm.UptimeSeconds, 0.01f);
        }

        [Test]
        public void ViewModel_ToString_IsReadable()
        {
            var vm = new NetworkDiagnosticsViewModel(
                rttMs: 45f, downKBps: 12f, upKBps: 1.5f, snapshotHz: 15f,
                entityCount: 50, serverTick: 12345, serverTickRate: 15f,
                packetLoss: 0f, uptimeSeconds: 60f);

            var s = vm.ToString();
            Assert.That(s, Does.Contain("RTT=45ms"));
            Assert.That(s, Does.Contain("50 entities"));
            Assert.That(s, Does.Contain("tick 12345"));
        }
    }
}
