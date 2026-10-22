using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// How many datagrams the KCP receive path reads per frame, against real loopback sockets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The finding (hypothesis H-a of T33).</b> After a 2 s client stall the live client
    /// applied the 32-snapshot backlog over 315 to 353 ms, one snapshot about every five frames,
    /// not in the frame it woke in (rpg-factory testlab S13, runs 20261022T194438Z-S13-vdqw and
    /// -q3rr, <c>delivery.csv</c>). <see cref="KcpTransport"/> awaited one
    /// <c>UdpClient.ReceiveAsync()</c> per datagram through <c>AsUniTask()</c>, whose
    /// continuation goes through <c>TaskScheduler.FromCurrentSynchronizationContext()</c>
    /// (UniTask <c>UniTaskExtensions.AsUniTask</c>, <c>useCurrentSynchronizationContext = true</c>):
    /// on the player loop that is Unity's main-thread context, which runs what was posted to it
    /// once per frame.
    /// </para>
    /// <para>
    /// <b>What runs here.</b> <see cref="KcpTransport"/> itself needs UniTask and stays
    /// Editor-only, so its loop is reproduced with the same two ingredients: a real
    /// <c>ReceiveAsync</c> on a loopback socket, continued through
    /// <c>TaskScheduler.FromCurrentSynchronizationContext()</c> over a context that runs its
    /// queue once per "frame", as Unity's does. The old loop body (one datagram per continuation)
    /// reads exactly one datagram per frame; the new one, which calls
    /// <see cref="UdpDatagramDrain.Drain"/> (the production code), reads the whole queue in the
    /// first frame. The session the datagrams feed is the real <see cref="KcpClientSession"/>, and
    /// the datagrams are real KCP segments from a server-side <see cref="Kcp"/>.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class TransportReceivePacingTests
    {
        private const uint Conv = 0x5EED;
        private const int Backlog = 30;

        /// <summary>
        /// A synchronization context that runs what was posted to it only when the "frame" runs,
        /// and only what had been posted before the frame started: Unity's
        /// <c>UnitySynchronizationContext.Exec</c> swaps its queue the same way.
        /// </summary>
        private sealed class FrameContext : SynchronizationContext
        {
            private readonly object _lock = new object();
            private List<(SendOrPostCallback, object)> _queue = new List<(SendOrPostCallback, object)>();

            public override void Post(SendOrPostCallback d, object state)
            {
                lock (_lock) _queue.Add((d, state));
            }

            public override void Send(SendOrPostCallback d, object state) => d(state);

            public int Pending
            {
                get { lock (_lock) return _queue.Count; }
            }

            public void RunFrame()
            {
                List<(SendOrPostCallback, object)> run;
                lock (_lock)
                {
                    run = _queue;
                    _queue = new List<(SendOrPostCallback, object)>();
                }

                foreach (var (d, state) in run) d(state);
            }
        }

        private sealed class Pair : IDisposable
        {
            public readonly UdpClient Client;
            public readonly UdpClient Server;
            public readonly KcpClientSession Session;
            private readonly Kcp _serverKcp;
            private readonly Stopwatch _clock = Stopwatch.StartNew();

            public Pair()
            {
                Client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                Server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                Client.Connect((IPEndPoint)Server.Client.LocalEndPoint);
                Server.Connect((IPEndPoint)Client.Client.LocalEndPoint);

                Session = new KcpClientSession(Conv, null, (b, n) => { }, () => _clock.ElapsedMilliseconds, "loopback");

                _serverKcp = new Kcp(Conv, (buf, size) =>
                {
                    var copy = new byte[size];
                    Buffer.BlockCopy(buf, 0, copy, 0, size);
                    Server.Send(copy, size);
                });
                _serverKcp.Stream = 1;
                _serverKcp.SetNoDelay(1, 10, 2, 1);
                _serverKcp.WndSize(128, 128);
            }

            /// <summary>Sends <paramref name="count"/> frames, one datagram each, and waits until they are queued.</summary>
            public void SendBacklog(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    var body = new byte[] { 1, 2, 3, (byte)i };
                    var frame = new byte[WireFraming.HeaderSize + body.Length];
                    WireFraming.WriteLength(frame, body.Length);
                    Buffer.BlockCopy(body, 0, frame, WireFraming.HeaderSize, body.Length);
                    _serverKcp.Send(frame, 0, frame.Length);
                    _serverKcp.Flush();
                }

                // Loopback delivery is immediate in practice; wait for the last one to be queued.
                var wait = Stopwatch.StartNew();
                while (Client.Available == 0 && wait.ElapsedMilliseconds < 1000) Thread.Sleep(1);
                Thread.Sleep(50);
            }

            public int FramesReadable()
            {
                int n = 0;
                while (Session.TryReadFrame() != null) n++;
                return n;
            }

            public void Dispose()
            {
                Client.Dispose();
                Server.Dispose();
                Session.Dispose();
            }
        }

        [Test]
        public void Drain_ReadsEveryQueuedDatagramInOneCall()
        {
            using (var pair = new Pair())
            {
                pair.SendBacklog(Backlog);
                var buffer = new byte[1500];

                int read = UdpDatagramDrain.Drain(pair.Client.Client, buffer, pair.Session);

                Assert.That(read, Is.EqualTo(Backlog), "every queued datagram, in one call");
                Assert.That(pair.Session.DatagramsReceived, Is.EqualTo(Backlog), "and each one reached the ARQ");
                Assert.That(pair.FramesReadable(), Is.EqualTo(Backlog), "as frames, in order and complete");
                Assert.That(UdpDatagramDrain.Drain(pair.Client.Client, buffer, pair.Session), Is.Zero,
                    "and an empty socket returns at once rather than blocking");
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AReceiveLoopContinuedOnTheFrameContext_ReadsTheBacklog(bool drainEachWake)
        {
            var context = new FrameContext();
            var previous = SynchronizationContext.Current;
            using (var pair = new Pair())
            {
                pair.SendBacklog(Backlog);
                var buffer = new byte[1500];
                var perFrame = new List<int>();
                int consumed = 0;

                try
                {
                    SynchronizationContext.SetSynchronizationContext(context);
                    var scheduler = TaskScheduler.FromCurrentSynchronizationContext();

                    // KcpTransport.ReceiveLoopAsync, with AsUniTask's continuation spelt out.
                    void ReceiveOne()
                    {
                        pair.Client.ReceiveAsync().ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                _ = t.Exception; // the socket was closed under the pending receive
                                return;
                            }

                            if (t.Status != TaskStatus.RanToCompletion) return;
                            pair.Session.Input(t.Result.Buffer, t.Result.Buffer.Length);
                            consumed++;
                            if (drainEachWake)
                            {
                                consumed += UdpDatagramDrain.Drain(pair.Client.Client, buffer, pair.Session);
                            }

                            ReceiveOne();
                        }, CancellationToken.None, TaskContinuationOptions.None, scheduler);
                    }

                    ReceiveOne();

                    for (int frame = 0; frame < Backlog * 2 && consumed < Backlog; frame++)
                    {
                        // The completion arrives from an I/O thread; a frame runs what has been
                        // posted by then. Waiting for it keeps the count independent of timing.
                        var wait = Stopwatch.StartNew();
                        while (context.Pending == 0 && wait.ElapsedMilliseconds < 2000) Thread.Sleep(1);
                        int before = consumed;
                        context.RunFrame();
                        perFrame.Add(consumed - before);
                    }

                    // Close under the receive still pending and let its continuation run, so the
                    // fault is observed here rather than reported later by the finalizer.
                    pair.Client.Close();
                    var drainWait = Stopwatch.StartNew();
                    while (context.Pending == 0 && drainWait.ElapsedMilliseconds < 2000) Thread.Sleep(1);
                    context.RunFrame();
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }

                TestContext.WriteLine($"drain each wake: {drainEachWake}; datagrams per frame: {string.Join(",", perFrame)}");
                Assert.That(consumed, Is.EqualTo(Backlog), "precondition: the backlog was read");
                if (drainEachWake)
                {
                    Assert.That(perFrame[0], Is.EqualTo(Backlog), "the whole backlog in the frame the loop wakes in");
                }
                else
                {
                    // The defect, reproduced: one datagram per frame.
                    Assert.That(perFrame.TrueForAll(n => n <= 1), Is.True, "one datagram per frame");
                    Assert.That(perFrame.Count, Is.EqualTo(Backlog));
                }

                Assert.That(pair.FramesReadable(), Is.EqualTo(Backlog));
            }
        }
    }
}
