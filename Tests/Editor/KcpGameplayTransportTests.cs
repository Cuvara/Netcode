using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Cuvara.Netcode.Transport;
using NUnit.Framework;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The Unity-free half of the KCP-only gameplay hop: the transport string is parsed
    /// strictly, a session's conversation id is random and non-zero, silence closes it with
    /// a reason, its receive buffer is bounded without dropping bytes, and a real KCP/UDP
    /// exchange over loopback works with and without a transport key.
    /// </summary>
    /// <remarks>
    /// Plain C#: runs in the EditMode suite and under <c>dotnet test</c> in
    /// <c>Tests~/Headless</c>. <see cref="KcpClientSession"/> is the same session
    /// <see cref="KcpTransport"/> drives; what this does not cover is the UniTask shell
    /// around it (see <see cref="GameplayTransportPolicyTests"/>).
    /// </remarks>
    [TestFixture]
    public sealed class KcpGameplayTransportTests
    {
        private const string Key = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

        // ---- transport string ------------------------------------------------------------

        [TestCase("kcp")]
        [TestCase("KCP")]
        [TestCase(" Kcp ")]
        public void ParseGameplay_AcceptsKcp(string value)
        {
            Assert.That(TransportKinds.ParseGameplay(value), Is.EqualTo(TransportKind.Kcp));
        }

        [TestCase("", "''")]
        [TestCase(null, "<null>")]
        [TestCase("tcp", "'tcp'")]
        [TestCase("TCP", "'TCP'")]
        [TestCase("tcptls", "'tcptls'")]
        [TestCase("udp", "'udp'")]
        public void ParseGameplay_RefusesEverythingElse_AndNamesTheValue(string value, string shown)
        {
            var ex = Assert.Throws<TransportException>(() => TransportKinds.ParseGameplay(value));
            Assert.That(ex.Message, Does.Contain(shown));
            Assert.That(ex.Message, Does.Contain("KCP/UDP only"));

            Assert.That(TransportKinds.TryParseGameplay(value, out _, out var error), Is.False);
            Assert.That(error, Does.Contain(shown));
        }

        [Test]
        public void RequireGameplay_OnlyAdmitsKcp()
        {
            Assert.DoesNotThrow(() => TransportKinds.RequireGameplay(TransportKind.Kcp));
            Assert.Throws<TransportException>(() => TransportKinds.RequireGameplay(TransportKind.Tcp));
            Assert.Throws<TransportException>(() => TransportKinds.RequireGameplay(TransportKind.TcpTls));
        }

        // ---- conversation id --------------------------------------------------------------

        [Test]
        public void ConversationIds_AreNonZeroAndDifferAcrossSessions()
        {
            const int n = 10_000;
            var seen = new HashSet<uint>();
            for (var i = 0; i < n; i++)
            {
                var conv = KcpClientSession.NewConversationId();
                Assert.That(conv, Is.Not.Zero);
                seen.Add(conv);
            }

            // 10k draws from 2^32: the expected number of collisions is ~0.012. A counter
            // would also pass "distinct", so the next assertion is the one that bites.
            Assert.That(seen.Count, Is.GreaterThanOrEqualTo(n - 2));

            var a = new KcpClientSession(KcpClientSession.NewConversationId(), null, (_, __) => { }, () => 0, "a");
            var b = new KcpClientSession(KcpClientSession.NewConversationId(), null, (_, __) => { }, () => 0, "b");
            Assert.That(a.Conv, Is.Not.EqualTo(b.Conv));
            Assert.That(Math.Abs((long)a.Conv - b.Conv), Is.Not.EqualTo(1L),
                "consecutive sessions must not get consecutive ids (the old static counter did)");
        }

        [Test]
        public void ASessionRefusesConversationZero()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new KcpClientSession(0, null, (_, __) => { }, () => 0, "x"));
        }

        // ---- address selection ----------------------------------------------------------

        [Test]
        public void PreferIPv4_PicksTheIPv4AddressWhenThereIsOne()
        {
            var picked = KcpClientSession.PreferIPv4(new[] { IPAddress.IPv6Loopback, IPAddress.Loopback });
            Assert.That(picked, Is.EqualTo(IPAddress.Loopback));

            Assert.That(KcpClientSession.PreferIPv4(new[] { IPAddress.IPv6Loopback }), Is.EqualTo(IPAddress.IPv6Loopback));
            Assert.That(KcpClientSession.PreferIPv4(new IPAddress[0]), Is.Null);
        }

        // ---- idle timeout -----------------------------------------------------------------

        [Test]
        public void Silence_ClosesTheSessionWithAReason()
        {
            long now = 0;
            var session = new KcpClientSession(7, null, (_, __) => { }, () => now, "10.0.0.1:9000", idleTimeoutMs: 5_000);

            now = 4_999;
            Assert.That(session.Tick(), Is.True);
            Assert.That(session.IsFailed, Is.False);

            now = 5_000;
            Assert.That(session.Tick(), Is.False);
            Assert.That(session.FailureReason, Does.Contain("KCP idle timeout"));
            Assert.That(session.FailureReason, Does.Contain("10.0.0.1:9000"));
            Assert.That(session.FailureReason, Does.Contain("firewall"), "nothing was ever received: say where to look");

            var read = Assert.Throws<TransportException>(() => session.TryReadFrame());
            Assert.That(read.Message, Does.Contain("idle timeout"));
            Assert.Throws<TransportException>(() => session.WriteFrame(new byte[] { 1 }));
        }

        [Test]
        public void InboundDatagrams_KeepTheSessionAlive()
        {
            long now = 0;
            var pair = new Pair(null, null, () => now, idleTimeoutMs: 1_000);

            for (var i = 0; i < 5; i++)
            {
                pair.Client.WriteFrame(new byte[] { (byte)i });
                pair.Pump(); // the peer acks, so the client hears something
                now += 900;  // never 1 s of silence
                Assert.That(pair.Client.Tick(), Is.True, $"round {i}: {pair.Client.FailureReason}");
            }

            Assert.That(pair.Client.DatagramsReceived, Is.GreaterThan(0));
        }

        // ---- bounded receive buffer -----------------------------------------------------

        [Test]
        public void AFrameLargerThanTheBound_FailsTheSession_RatherThanDroppingBytes()
        {
            const int bound = 4 * 1024;
            var pair = new Pair(null, null, () => 0, maxStreamBytes: bound);

            pair.Peer.WriteFrame(new byte[16 * 1024]);
            var ex = Assert.Throws<TransportException>(() => pair.ReadOnClient(TimeSpan.FromSeconds(5)));

            Assert.That(ex.Message, Does.Contain("receive buffer"));
            Assert.That(pair.Client.IsFailed, Is.True);
            Assert.That(pair.Client.BufferedStreamBytes, Is.LessThanOrEqualTo(bound));
        }

        [Test]
        public void TheDefaultBound_StillCarriesALargeFrameIntact_AndHoldsAtMostOnePartialFrame()
        {
            var pair = new Pair(null, null, () => 0);
            var body = new byte[300 * 1024];
            new Random(1).NextBytes(body);

            pair.Peer.WriteFrame(body);
            pair.Peer.WriteFrame(Encoding.UTF8.GetBytes("after"));

            Assert.That(pair.ReadOnClient(TimeSpan.FromSeconds(10)), Is.EqualTo(body));
            Assert.That(Encoding.UTF8.GetString(pair.ReadOnClient(TimeSpan.FromSeconds(5))), Is.EqualTo("after"));
            Assert.That(KcpClientSession.MaxStreamBytes,
                Is.EqualTo(WireFraming.HeaderSize + WireFraming.MaxBodySize + KcpClientSession.TuningMtu));
        }

        [Test]
        public void AnInvalidLengthPrefix_FailsTheSession()
        {
            var pair = new Pair(null, null, () => 0);

            // A raw stream write that is not a frame: length prefix 0xFFFFFFFF.
            pair.PeerRawSend(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3 });

            var ex = Assert.Throws<TransportException>(() => pair.ReadOnClient(TimeSpan.FromSeconds(5)));
            Assert.That(ex.Message, Does.Contain("invalid frame length"));
        }

        // ---- crypto -----------------------------------------------------------------------

        [Test]
        public void AMatchingKey_Delivers_AndAWrongKey_IsDroppedNotDelivered()
        {
            var good = new Pair(Key, Key, () => 0);
            good.Peer.WriteFrame(Encoding.UTF8.GetBytes("sealed"));
            Assert.That(Encoding.UTF8.GetString(good.ReadOnClient(TimeSpan.FromSeconds(5))), Is.EqualTo("sealed"));
            Assert.That(good.Client.IsEncrypted, Is.True);

            var bad = new Pair(Key, Key.Replace('0', '9'), () => 0);
            bad.Peer.WriteFrame(Encoding.UTF8.GetBytes("sealed"));
            Assert.That(bad.ReadOnClient(TimeSpan.FromMilliseconds(300), throwOnTimeout: false), Is.Null);
            Assert.That(bad.Client.DatagramsReceived, Is.Zero);
            Assert.That(bad.Client.DatagramsRejected, Is.GreaterThan(0));
        }

        // ---- real UDP loopback ----------------------------------------------------------

        [TestCase(null)]
        [TestCase(Key)]
        public void LoopbackEcho_OverRealUdp(string key)
        {
            using (var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            using (var client = new UdpClient(AddressFamily.InterNetwork))
            {
                var serverEp = (IPEndPoint)server.Client.LocalEndPoint;
                client.Connect(serverEp);

                var clientSession = new KcpClientSession(
                    KcpClientSession.NewConversationId(), key,
                    (p, n) => client.Send(p, n), NowMs, serverEp.ToString());

                KcpClientSession peer = null;
                IPEndPoint peerRemote = null;
                var echoed = new List<string>();
                var sent = new[] { "join_token", "input-1", new string('x', 5000) };
                foreach (var s in sent)
                {
                    clientSession.WriteFrame(Encoding.UTF8.GetBytes(s));
                }

                var deadline = Stopwatch.StartNew();
                while (echoed.Count < sent.Length && deadline.Elapsed < TimeSpan.FromSeconds(5))
                {
                    while (server.Available > 0)
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        var datagram = server.Receive(ref from);
                        if (peer == null)
                        {
                            // What the game server does: adopt the conversation id from the
                            // first datagram of an unknown endpoint.
                            peerRemote = from;
                            var conv = ReadConv(datagram, key);
                            var to = from;
                            peer = new KcpClientSession(conv, key, (p, n) => server.Send(p, n, to), NowMs, "client");
                        }

                        peer.Input(datagram, datagram.Length);
                    }

                    while (client.Available > 0)
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        var datagram = client.Receive(ref from);
                        clientSession.Input(datagram, datagram.Length);
                    }

                    byte[] frame;
                    while (peer != null && (frame = peer.TryReadFrame()) != null)
                    {
                        peer.WriteFrame(frame);
                    }

                    while ((frame = clientSession.TryReadFrame()) != null)
                    {
                        echoed.Add(Encoding.UTF8.GetString(frame));
                    }

                    clientSession.Tick();
                    peer?.Tick();
                    Thread.Sleep(1);
                }

                Assert.That(echoed, Is.EqualTo(sent));
                Assert.That(peer.Conv, Is.EqualTo(clientSession.Conv));
                Assert.That(peerRemote.Port, Is.EqualTo(((IPEndPoint)client.Client.LocalEndPoint).Port));
            }
        }

        [Test]
        public void ADeadUdpPort_SurfacesAsAnIdleFailure_NotAHang()
        {
            int deadPort;
            using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                deadPort = ((IPEndPoint)probe.Client.LocalEndPoint).Port;
            }

            using (var client = new UdpClient(AddressFamily.InterNetwork))
            {
                client.Connect(new IPEndPoint(IPAddress.Loopback, deadPort));
                var session = new KcpClientSession(
                    KcpClientSession.NewConversationId(), null,
                    (p, n) => { try { client.Send(p, n); } catch (SocketException) { } },
                    NowMs, "127.0.0.1:" + deadPort, idleTimeoutMs: 300);

                session.WriteFrame(Encoding.UTF8.GetBytes("join_token"));
                var sw = Stopwatch.StartNew();
                while (session.Tick() && sw.ElapsedMilliseconds < 3_000)
                {
                    Thread.Sleep(5);
                }

                Assert.That(session.IsFailed, Is.True);
                Assert.That(session.DatagramsReceived, Is.Zero);
                Assert.That(session.FailureReason, Does.Contain("127.0.0.1:" + deadPort));
                Assert.That(session.FailureReason, Does.Contain("UDP port mapping"));
            }
        }

        // ---- helpers ----------------------------------------------------------------------

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private static long NowMs() => Clock.ElapsedMilliseconds;

        private static uint ReadConv(byte[] datagram, string key)
        {
            var offset = 0;
            if (!string.IsNullOrEmpty(key))
            {
                var copy = (byte[])datagram.Clone();
                using (var crypto = KcpCrypto.TryCreate(key))
                {
                    Assert.That(crypto.Open(copy, 0, copy.Length), Is.GreaterThan(0));
                }

                datagram = copy;
                offset = KcpCrypto.HeaderSize;
            }

            return (uint)(datagram[offset] | (datagram[offset + 1] << 8) |
                          (datagram[offset + 2] << 16) | (datagram[offset + 3] << 24));
        }

        /// <summary>Two sessions on one conversation, wired back to back in memory.</summary>
        private sealed class Pair
        {
            private readonly Queue<byte[]> _toClient = new Queue<byte[]>();
            private readonly Queue<byte[]> _toPeer = new Queue<byte[]>();
            private readonly Kcp _rawPeer;

            public Pair(string clientKey, string peerKey, Func<long> clock,
                int idleTimeoutMs = KcpClientSession.DefaultIdleTimeoutMs,
                int maxStreamBytes = KcpClientSession.MaxStreamBytes)
            {
                const uint conv = 0x5EED;
                Client = new KcpClientSession(conv, clientKey, (p, n) => _toPeer.Enqueue(Copy(p, n)), clock, "peer",
                    idleTimeoutMs, maxStreamBytes);
                Peer = new KcpClientSession(conv, peerKey, (p, n) => _toClient.Enqueue(Copy(p, n)), clock, "client");

                _rawPeer = new Kcp(conv, (p, n) => _toClient.Enqueue(Copy(p, n)));
                _rawPeer.Stream = 1;
                _rawPeer.SetNoDelay(1, 10, 2, 1);
                _rawPeer.WndSize(128, 128);
            }

            public KcpClientSession Client { get; }

            public KcpClientSession Peer { get; }

            /// <summary>Sends raw stream bytes from an unframed peer, for malformed-stream cases.</summary>
            public void PeerRawSend(byte[] bytes)
            {
                _rawPeer.Send(bytes, 0, bytes.Length);
                _rawPeer.Flush();
            }

            public void Pump()
            {
                while (_toPeer.Count > 0 || _toClient.Count > 0)
                {
                    while (_toPeer.Count > 0)
                    {
                        var d = _toPeer.Dequeue();
                        Peer.Input(d, d.Length);
                        _rawPeer.Input(d, 0, d.Length, true);
                    }

                    while (_toClient.Count > 0)
                    {
                        var d = _toClient.Dequeue();
                        Client.Input(d, d.Length);
                    }
                }
            }

            public byte[] ReadOnClient(TimeSpan timeout, bool throwOnTimeout = true)
            {
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < timeout)
                {
                    Pump();
                    var frame = Client.TryReadFrame();
                    if (frame != null)
                    {
                        return frame;
                    }

                    Client.Tick();
                    Peer.Tick();
                    _rawPeer.Update();
                    Thread.Sleep(1);
                }

                if (throwOnTimeout)
                {
                    Assert.Fail("no frame reached the client within " + timeout);
                }

                return null;
            }

            private static byte[] Copy(byte[] p, int n)
            {
                var c = new byte[n];
                Buffer.BlockCopy(p, 0, c, 0, n);
                return c;
            }
        }
    }
}
