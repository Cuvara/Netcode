using System;
using System.Collections.Generic;
using NUnit.Framework;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The ARQ guarantees the gameplay stream relies on, under the network <see cref="KcpCoreTests"/>
    /// does not model: lost datagrams are retransmitted, reordered and duplicated ones are delivered
    /// once and in order, and a full receive window stalls the sender instead of dropping data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The loss, reorder and duplicate cases are deterministic: datagrams are dropped by their
    /// position in the stream (first transmission only), not at random, and both ends are flushed
    /// explicitly each round, so recovery runs through KCP's fast retransmit (a segment overtaken
    /// by two acknowledged later ones, the game profile's <c>resend = 2</c>) with no wall time
    /// passing. The full-window case cannot be: a segment the receiver turned away is resent only
    /// on its retransmission timeout, and <see cref="Kcp"/> reads a real clock, so that test waits
    /// a bounded few milliseconds per round. Its outcome does not depend on how long a round takes.
    /// </para>
    /// <para>
    /// The game profile is the one <see cref="KcpCoreTests"/> uses: stream mode, nodelay, a 10 ms
    /// interval, fast resend after 2, no congestion window.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class KcpReliabilityTests
    {
        private sealed class Link
        {
            public readonly Kcp Client;
            public readonly Kcp Server;
            public readonly List<byte[]> ClientOut = new List<byte[]>();
            public readonly List<byte[]> ServerOut = new List<byte[]>();

            /// <summary>Client-to-server datagrams handed to the network, retransmissions included.</summary>
            public int ClientDatagrams;

            /// <summary>Decides, per client-to-server datagram index, what the network does with it.</summary>
            public Func<int, byte[], IEnumerable<byte[]>> Uplink = (i, d) => new[] { d };

            /// <summary>Reorders a round's client-to-server datagrams before delivery.</summary>
            public bool ReverseEachRound;

            /// <summary>Wall time each round waits first, for the timers that only real time fires.</summary>
            public int RoundSleepMs;

            public Link(int clientRcvWnd = 128, int serverRcvWnd = 128)
            {
                Client = new Kcp(1, (buf, size) => ClientOut.Add(Copy(buf, size)));
                Server = new Kcp(1, (buf, size) => ServerOut.Add(Copy(buf, size)));
                foreach (var k in new[] { Client, Server })
                {
                    k.Stream = 1;
                    k.SetNoDelay(1, 10, 2, 1);
                }

                Client.WndSize(128, clientRcvWnd);
                Server.WndSize(128, serverRcvWnd);
            }

            private static byte[] Copy(byte[] buf, int size)
            {
                var copy = new byte[size];
                Buffer.BlockCopy(buf, 0, copy, 0, size);
                return copy;
            }

            public void Round()
            {
                if (RoundSleepMs > 0) System.Threading.Thread.Sleep(RoundSleepMs);
                Client.Flush();
                var batch = new List<byte[]>();
                foreach (var d in ClientOut)
                {
                    batch.AddRange(Uplink(ClientDatagrams++, d));
                }

                ClientOut.Clear();
                if (ReverseEachRound) batch.Reverse();
                foreach (var d in batch) Server.Input(d, 0, d.Length, ackNoDelay: true);

                Server.Flush();
                foreach (var d in ServerOut) Client.Input(d, 0, d.Length, ackNoDelay: true);
                ServerOut.Clear();
            }

            public byte[] Drain()
            {
                var all = new List<byte>();
                var buf = new byte[64 * 1024];
                int n;
                while ((n = Server.Recv(buf, buf.Length)) > 0)
                {
                    for (int i = 0; i < n; i++) all.Add(buf[i]);
                }

                return all.ToArray();
            }
        }

        /// <summary>Forty 1000-byte writes, each byte a function of its position, so order is checkable.</summary>
        private static byte[] Payload(int bytes = 40_000)
        {
            var p = new byte[bytes];
            for (int i = 0; i < p.Length; i++) p[i] = (byte)((i * 31 + i / 251) & 0xFF);
            return p;
        }

        private static void SendAll(Kcp k, byte[] payload)
        {
            for (int off = 0; off < payload.Length; off += 1000)
            {
                Assert.That(k.Send(payload, off, Math.Min(1000, payload.Length - off)), Is.GreaterThanOrEqualTo(0));
            }
        }

        private static byte[] RunUntilDelivered(Link link, int expected, int maxRounds = 400)
        {
            var got = new List<byte>();
            for (int r = 0; r < maxRounds && got.Count < expected; r++)
            {
                link.Round();
                got.AddRange(link.Drain());
            }

            return got.ToArray();
        }

        [Test]
        public void LostDatagrams_AreRetransmitted_AndTheStreamArrivesWholeAndInOrder()
        {
            var link = new Link();
            // Every fifth datagram the client sends is lost the first time it is sent; its
            // retransmission gets through.
            var seen = new HashSet<string>();
            int lost = 0;
            link.Uplink = (i, d) =>
            {
                bool first = seen.Add(Convert.ToBase64String(d, Kcp.Overhead, d.Length - Kcp.Overhead));
                if (first && i % 5 == 2)
                {
                    lost++;
                    return new byte[0][];
                }

                return new[] { d };
            };
            byte[] sent = Payload();
            SendAll(link.Client, sent);

            byte[] got = RunUntilDelivered(link, sent.Length);

            Assert.That(got.Length, Is.EqualTo(sent.Length), "every byte arrives despite the losses");
            Assert.That(got, Is.EqualTo(sent), "in order");
            Assert.That(link.Client.WaitSnd, Is.Zero, "and every segment was acknowledged");
            Assume.That(lost, Is.GreaterThan(3), "precondition: datagrams were lost");
            Assert.That(link.ClientDatagrams, Is.GreaterThanOrEqualTo(seen.Count + lost),
                "every lost datagram was sent again");
            Assert.That(link.Client.DeadLinkReached, Is.False);
        }

        [Test]
        public void ReorderedAndDuplicatedDatagrams_AreDeliveredOnceInOrder()
        {
            var link = new Link { ReverseEachRound = true };
            link.Uplink = (i, d) => new[] { d, d }; // every datagram twice
            byte[] sent = Payload();
            SendAll(link.Client, sent);

            byte[] got = RunUntilDelivered(link, sent.Length);
            for (int r = 0; r < 5; r++)
            {
                link.Round(); // anything still in flight would show up here as a duplicate
                var late = link.Drain();
                Assert.That(late, Is.Empty, "a duplicated datagram must not deliver its bytes twice");
            }

            Assert.That(got, Is.EqualTo(sent));
        }

        [Test]
        public void AFullReceiveWindow_StallsTheSender_AndLosesNothing()
        {
            // The server's application stops reading. Its receive window is 8 segments, so the
            // client can have at most that much accepted; the rest waits on the client.
            var link = new Link(serverRcvWnd: 8);
            byte[] sent = Payload();
            SendAll(link.Client, sent);

            for (int r = 0; r < 50; r++) link.Round();

            int waiting = link.Client.WaitSnd;
            Assert.That(waiting, Is.GreaterThan(10),
                $"with nobody reading, the sender is held by the window ({waiting} segments waiting)");

            // Segments the full window turned away are resent on their retransmission timeout, which
            // only wall time fires (Kcp's clock is a Stopwatch); 400 rounds of 5 ms bound the wait.
            link.RoundSleepMs = 5;
            byte[] got = RunUntilDelivered(link, sent.Length);

            Assert.That(got, Is.EqualTo(sent), "once the application reads again, everything arrives, in order");
            for (int r = 0; r < 5; r++) link.Round(); // the last acknowledgements
            Assert.That(link.Client.WaitSnd, Is.Zero);
        }
    }
}
