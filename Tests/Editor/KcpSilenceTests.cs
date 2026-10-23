using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// A frozen client whose session the server dropped finds out within
    /// <see cref="KcpClientSession.DefaultSilenceTimeoutMs"/> of running time, not on its 40 s
    /// heartbeat.
    /// </summary>
    /// <remarks>
    /// <para>
    /// rpg-factory testlab S09 soak 20261023T040945Z-S09-e26z: three clients froze for about 4.5 s
    /// each, and the server's KCP dropped each session about 3.1 s into the freeze. Clients 1 and 2
    /// failed on their own dead-link counter about 3.5 s after waking up and reconnected. Client 0
    /// kept sending into the dropped session and reconnected only on its heartbeat timeout, 31.8 s
    /// later.
    /// </para>
    /// <para>
    /// Why the dead-link counter is not enough: an acknowledgement that arrives during a freeze is
    /// timed as one long round trip (<see cref="AnAcknowledgementReadAfterAFreeze_InflatesTheRetransmissionTimeout"/>).
    /// The retransmission timeout then reaches seconds, and 20 retransmissions take minutes.
    /// </para>
    /// <para>
    /// The session's own clock is injected here, so the silence check is deterministic. The KCP
    /// core keeps its real clock, which is why the RTO test sleeps briefly.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class KcpSilenceTests
    {
        private const uint Conv = 0x51E7;

        /// <summary>Server-side datagrams, one per frame, produced once.</summary>
        private static List<byte[]> ServerDatagrams(int count)
        {
            var datagrams = new List<byte[]>();
            var server = new Kcp(Conv, (b, n) =>
            {
                var copy = new byte[n];
                Buffer.BlockCopy(b, 0, copy, 0, n);
                datagrams.Add(copy);
            });
            server.Stream = 1;
            server.SetNoDelay(1, 10, 2, 1);
            server.WndSize(128, 128);
            for (int i = 0; i < count; i++)
            {
                var frame = new byte[WireFraming.HeaderSize + 2];
                WireFraming.WriteLength(frame, 2);
                server.Send(frame, 0, frame.Length);
                server.Flush();
            }

            return datagrams;
        }

        private sealed class Client
        {
            public long Now;
            public readonly KcpClientSession Session;
            private readonly List<byte[]> _feed;
            private int _next;

            public Client(int count)
            {
                _feed = ServerDatagrams(count);
                Session = new KcpClientSession(Conv, null, (b, n) => { }, () => Now, "test-server");
            }

            public void Receive()
            {
                // Past the server's send window the feed repeats: a repeated segment is still a
                // datagram from the server, which is all the silence check looks at.
                var d = _feed[_next++ % _feed.Count];
                Session.Input(d, d.Length);
                while (Session.TryReadFrame() != null) { }
            }

            /// <summary>Runs <paramref name="ms"/> of frames at 10 ms, receiving every 66 ms when <paramref name="serverAlive"/>.</summary>
            public bool Run(long ms, bool serverAlive)
            {
                long end = Now + ms;
                long nextReceive = Now + 66;
                while (Now < end)
                {
                    Now += 10;
                    if (serverAlive && Now >= nextReceive)
                    {
                        Receive();
                        nextReceive += 66;
                    }

                    if (!Session.Tick()) return false;
                }

                return true;
            }
        }

        [Test]
        public void AFrozenClient_WhoseSessionWasDropped_FailsWithinTheBound()
        {
            var c = new Client(200);
            Assume.That(c.Run(2000, serverAlive: true), Is.True, "precondition: a healthy session");

            // The freeze: no frames at all for 4.5 s. On waking, the backlog the server sent before
            // it dropped the session is read.
            c.Now += 4500;
            c.Receive();
            c.Receive();
            Assert.That(c.Session.Tick(), Is.True, "the freeze itself is not the server's silence");

            long woke = c.Now;
            bool alive = c.Run(KcpClientSession.DefaultSilenceTimeoutMs + 1000, serverAlive: false);
            long detectedAfter = c.Now - woke;
            TestContext.WriteLine($"failed {detectedAfter} ms after waking: {c.Session.FailureReason}");

            Assert.That(alive, Is.False, "the dead session is detected");
            Assert.That(detectedAfter, Is.InRange(KcpClientSession.DefaultSilenceTimeoutMs, KcpClientSession.DefaultSilenceTimeoutMs + 20));
            Assert.That(c.Session.SilenceFailures, Is.EqualTo(1));
            Assert.That(c.Session.FailureReason, Does.Contain("dead session"));
        }

        [Test]
        public void AFreezeLongerThanTheBound_ThenALiveServer_IsNotADeadSession()
        {
            var c = new Client(300);
            Assume.That(c.Run(1000, serverAlive: true), Is.True);

            c.Now += KcpClientSession.DefaultSilenceTimeoutMs + 2000;
            Assert.That(c.Session.Tick(), Is.True, "a freeze is not counted");
            Assert.That(c.Run(10_000, serverAlive: true), Is.True, "and a server that is still there keeps the session");
            Assert.That(c.Session.SilenceFailures, Is.Zero);
        }

        [Test]
        public void AHealthyStream_NeverReadsAsSilence()
        {
            var c = new Client(1000);
            Assert.That(c.Run(60_000, serverAlive: true), Is.True);
            Assert.That(c.Session.AwakeSilentMs, Is.LessThan(100));
        }

        [Test]
        public void NothingReceivedYet_IsLeftToTheJoinAndIdleTimeouts()
        {
            var c = new Client(1);
            for (int i = 0; i < 1000; i++)
            {
                c.Now += 10;
                Assert.That(c.Session.Tick(), Is.True, $"at {c.Now} ms");
            }
        }

        [Test]
        public void AnAcknowledgementReadAfterAFreeze_InflatesTheRetransmissionTimeout()
        {
            // The mechanism behind the slow client: a segment in flight when the client froze.
            var toServer = new List<byte[]>();
            var session = new KcpClientSession(Conv, null, (b, n) =>
            {
                var copy = new byte[n];
                Buffer.BlockCopy(b, 0, copy, 0, n);
                toServer.Add(copy);
            }, () => 0, "test-server", silenceTimeoutMs: 0);
            var toClient = new List<byte[]>();
            var server = new Kcp(Conv, (b, n) =>
            {
                var copy = new byte[n];
                Buffer.BlockCopy(b, 0, copy, 0, n);
                toClient.Add(copy);
            });
            server.Stream = 1;
            server.SetNoDelay(1, 10, 2, 1);
            server.WndSize(128, 128);

            void Exchange()
            {
                foreach (var d in toServer) server.Input(d, 0, d.Length, ackNoDelay: true);
                toServer.Clear();
                server.Update();
                foreach (var d in toClient) session.Input(d, d.Length);
                toClient.Clear();
            }

            for (int i = 0; i < 20; i++)
            {
                session.WriteFrame(new byte[] { 1, (byte)i });
                Exchange();
                Thread.Sleep(2);
            }

            uint before = session.RtoMs;

            // Sent just before the freeze; the server acknowledges at once, but the client reads that
            // acknowledgement only when it wakes. 0.6 s stands in for the soak's 4.5 s.
            session.WriteFrame(new byte[] { 9, 9 });
            foreach (var d in toServer) server.Input(d, 0, d.Length, ackNoDelay: true);
            toServer.Clear();
            server.Update();
            Thread.Sleep(600);
            foreach (var d in toClient) session.Input(d, d.Length);
            toClient.Clear();

            uint after = session.RtoMs;
            TestContext.WriteLine($"RTO before {before} ms, after one acknowledgement read late {after} ms");
            Assert.That(before, Is.LessThan(100));
            Assert.That(after, Is.GreaterThan(before * 5),
                "one late-read acknowledgement multiplies the RTO, so the dead-link count of 20 " +
                "retransmissions stops being a few seconds");
        }
    }
}
