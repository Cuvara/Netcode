using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// A graceful close over KCP: the last frame written (the disconnect) is delivered only if the
    /// session stays alive until it is acknowledged, and the wait for that is bounded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both rpg-factory testlab S08G runs ended on the server's KCP dead-link timeout although the
    /// client left gracefully: <c>WireConnection.Leave</c> queued the disconnect frame and closed
    /// the socket in the same call. These tests pin the transport half of the fix with the real
    /// <see cref="KcpClientSession"/> against a server-side <see cref="Kcp"/>, in memory: the
    /// frame survives a lost first datagram when the session is kept alive for
    /// <see cref="KcpClientSession.CloseFlushBudgetMs"/>, and is lost when it is closed at once.
    /// The <c>WireConnection</c> half (write, then flush, then close) needs UniTask and is
    /// <c>WireConnectionLeaveTests</c>, in the Editor.
    /// </para>
    /// <para>
    /// <see cref="Kcp"/> reads a real clock for its retransmission timer, so the loss case runs in
    /// real time, ticking every 10 ms as the transport's ARQ loop does; it is bounded by the
    /// budget under test, at most 500 ms.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class KcpCloseFlushTests
    {
        private const uint Conv = 0xC105E;

        private sealed class Link
        {
            public readonly KcpClientSession Session;
            public readonly Kcp Server;
            public readonly List<byte[]> Uplink = new List<byte[]>();
            public readonly List<byte[]> Downlink = new List<byte[]>();
            public int UplinkSent;
            public Func<int, bool> DropUplink = i => false;
            private readonly Stopwatch _clock = Stopwatch.StartNew();

            public Link()
            {
                Session = new KcpClientSession(Conv, null, (b, n) => Uplink.Add(Copy(b, n)),
                    () => _clock.ElapsedMilliseconds, "memory");
                Server = new Kcp(Conv, (b, n) => Downlink.Add(Copy(b, n)));
                Server.Stream = 1;
                Server.SetNoDelay(1, 10, 2, 1);
                Server.WndSize(128, 128);
            }

            private static byte[] Copy(byte[] b, int n)
            {
                var c = new byte[n];
                Buffer.BlockCopy(b, 0, c, 0, n);
                return c;
            }

            /// <summary>One 10 ms round: the client's ARQ timer, the network both ways, the server.</summary>
            public void Round()
            {
                Session.Tick();
                foreach (var d in Uplink)
                {
                    if (DropUplink(UplinkSent++)) continue;
                    Server.Input(d, 0, d.Length, ackNoDelay: true);
                }

                Uplink.Clear();
                Server.Update();
                foreach (var d in Downlink) Session.Input(d, d.Length);
                Downlink.Clear();
            }

            public byte[] ServerFrame()
            {
                int size = Server.PeekSize();
                if (size <= 0) return null;
                var buf = new byte[size];
                int n = Server.Recv(buf, buf.Length);
                return n > 0 ? buf : null;
            }

            /// <summary>Warms the RTT estimate up the way a session that has been running would.</summary>
            public void Warm()
            {
                for (int i = 0; i < 5; i++)
                {
                    Session.WriteFrame(new byte[] { 9, (byte)i });
                    for (int r = 0; r < 3; r++)
                    {
                        Round();
                        Thread.Sleep(2);
                    }

                    while (ServerFrame() != null) { }
                }
            }
        }

        private static readonly byte[] Bye = { 0x7B, 0x22, 0x62, 0x79, 0x65, 0x22, 0x7D }; // {"bye"}

        [TestCase(30, 100)]
        [TestCase(80, 160)]
        [TestCase(200, 400)]
        [TestCase(5000, 500)]
        public void TheBudget_IsTwoRetransmissionTimeouts_Bounded(int rtoMs, int expectedMs)
        {
            Assert.That(KcpClientSession.CloseFlushBudget(rtoMs), Is.EqualTo(expectedMs));
        }

        [Test]
        public void AFrameWhoseFirstDatagramIsLost_IsAcknowledgedWithinTheBudget()
        {
            var link = new Link();
            link.Warm();
            int budget = link.Session.CloseFlushBudgetMs;
            int first = link.UplinkSent;
            link.DropUplink = i => i == first; // the disconnect's first transmission

            link.Session.WriteFrame(Bye);
            var waited = Stopwatch.StartNew();
            while (link.Session.PendingSendSegments > 0 && waited.ElapsedMilliseconds < budget)
            {
                link.Round();
                Thread.Sleep(10);
            }

            long took = waited.ElapsedMilliseconds;
            TestContext.WriteLine($"budget {budget} ms, acknowledged after {took} ms, uplink datagrams {link.UplinkSent - first}");
            Assert.That(link.Session.PendingSendSegments, Is.Zero, $"acknowledged within the {budget} ms budget");
            Assert.That(link.UplinkSent - first, Is.GreaterThanOrEqualTo(2), "precondition: it took a resend");

            var frame = link.ServerFrame();
            Assert.That(frame, Is.Not.Null, "the server has the frame");
            Assert.That(WireFraming.ReadLength(frame, 0), Is.EqualTo(Bye.Length));
        }

        [Test]
        public void ClosingAtOnce_LosesAFrameWhoseFirstDatagramIsLost()
        {
            // The old Leave: write, then close in the same call. The first transmission is the only
            // one there will ever be.
            var link = new Link();
            link.Warm();
            int first = link.UplinkSent;
            link.DropUplink = i => i == first;

            link.Session.WriteFrame(Bye);
            link.Round(); // what was already flushed reaches the network; then the socket is gone

            Assert.That(link.ServerFrame(), Is.Null);
            Assert.That(link.Session.PendingSendSegments, Is.GreaterThan(0));
        }
    }
}
