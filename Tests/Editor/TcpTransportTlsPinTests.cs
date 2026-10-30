using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Cuvara.Netcode.Transport;
using Cysharp.Threading.Tasks;
using NUnit.Framework;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// <see cref="TcpTransport"/>'s TLS pin handling against a real in-process TLS server on
    /// the runtime the tests run on (Unity Mono in the Editor), not a throwaway .NET probe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assertions that matter are the type mapping: a rejected pin is
    /// <see cref="TlsPinMismatchException"/> (permanent for <c>ReconnectPolicy</c>), while a
    /// handshake that fails for any other reason with a pin configured stays a plain
    /// <see cref="TransportException"/> (transient). Mixing those up either retries a
    /// possible man-in-the-middle forever or gives up on an ordinary network fault.
    /// </para>
    /// <para>
    /// The connect runs on a thread-pool thread whose synchronization context posts back to the
    /// pool, so waiting for it from the test thread cannot deadlock against a continuation
    /// that wants the Unity main thread. Every wait is bounded, every listener is disposed, and the port is
    /// OS-assigned.
    /// </para>
    /// </remarks>
    public sealed class TcpTransportTlsPinTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

        private const string Host = "localhost";

        private X509Certificate2 _serverCert;
        private X509Certificate2 _otherCert;
        private TcpListener _listener;
        private Task _serverTask;
        private CancellationTokenSource _cts;

        [SetUp]
        public void SetUp()
        {
            _serverCert = Load(TlsTestCertificates.ServerPfxBase64);
            _otherCert = Load(TlsTestCertificates.OtherPfxBase64);
            _cts = new CancellationTokenSource();
        }

        [TearDown]
        public void TearDown()
        {
            _cts?.Cancel();
            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
                // Already stopped.
            }

            try
            {
                _serverTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // The server side may fail once the client has gone; the assertions are
                // on the client.
            }

            _cts?.Dispose();
            _serverCert?.Dispose();
            _otherCert?.Dispose();
        }

        // ---- (a) wrong pin ---------------------------------------------------------------

        [Test]
        public void WrongPin_ThrowsTlsPinMismatchException()
        {
            StartServer(ServeTlsThenClose);

            var ex = ConnectExpectingFailure(Pinned(_otherCert.RawData));

            Assert.That(ex, Is.TypeOf<TlsPinMismatchException>(), Describe(ex));
        }

        [Test]
        public void TruncatedPin_ThrowsTlsPinMismatchException()
        {
            StartServer(ServeTlsThenClose);

            var truncated = new byte[_serverCert.RawData.Length - 1];
            Array.Copy(_serverCert.RawData, truncated, truncated.Length);

            var ex = ConnectExpectingFailure(Pinned(truncated));

            Assert.That(ex, Is.TypeOf<TlsPinMismatchException>(), Describe(ex));
        }

        // ---- (b) matching pin ------------------------------------------------------------

        [Test]
        public void MatchingPin_Connects()
        {
            StartServer(async client =>
            {
                using (var ssl = new SslStream(client.GetStream(), false))
                {
                    await ssl.AuthenticateAsServerAsync(_serverCert);
                    // Hold the link open until the test is done with it.
                    await Task.Delay(Timeout.Infinite, _cts.Token);
                }
            });

            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            using (var transport = new TcpTransport(Pinned(_serverCert.RawData)))
            {
                var task = StartConnect(transport, port, _cts.Token);
                Assert.That(WaitSettled(task, Bound), Is.True, "connect did not finish within the bound");
                Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
                Assert.That(transport.IsConnected, Is.True);
                Assert.That(transport.NegotiatedProtocol, Is.Not.EqualTo(SslProtocols.None));
            }
        }

        // ---- (c) non-TLS bytes with a pin configured ------------------------------------

        [Test]
        public void NonTlsServer_WithPin_ThrowsGenericTransportException()
        {
            StartServer(async client =>
            {
                var stream = client.GetStream();
                // Enough plaintext for the TLS record layer to give up rather than wait.
                var junk = new byte[256];
                for (var i = 0; i < junk.Length; i++)
                {
                    junk[i] = (byte)('A' + i % 26);
                }

                await stream.WriteAsync(junk, 0, junk.Length);
                await Task.Delay(500);
            });

            var ex = ConnectExpectingFailure(Pinned(_serverCert.RawData));

            Assert.That(ex, Is.TypeOf<TransportException>(), Describe(ex));
        }

        // ---- (d) reset during the handshake ---------------------------------------------

        [Test]
        public void ResetDuringHandshake_ThrowsGenericTransportException()
        {
            StartServer(client =>
            {
                // RST instead of FIN: linger with a zero timeout, then close.
                client.LingerState = new LingerOption(true, 0);
                client.Close();
                return Task.CompletedTask;
            });

            var ex = ConnectExpectingFailure(Pinned(_serverCert.RawData));

            Assert.That(ex, Is.TypeOf<TransportException>(), Describe(ex));
        }

        // ---- no pin: platform validation decides, and that is not a pin mismatch --------

        [Test]
        public void NoPin_SelfSigned_ThrowsGenericTransportException()
        {
            StartServer(ServeTlsThenClose);

            var ex = ConnectExpectingFailure(new TlsOptions { TargetHost = Host });

            Assert.That(ex, Is.TypeOf<TransportException>(), Describe(ex));
        }

        // ---- helpers ---------------------------------------------------------------------

        private async Task ServeTlsThenClose(TcpClient client)
        {
            using (var ssl = new SslStream(client.GetStream(), false))
            {
                await ssl.AuthenticateAsServerAsync(_serverCert);
            }
        }

        private static TlsOptions Pinned(byte[] pin)
            => new TlsOptions { PinnedCertificate = pin, TargetHost = Host };

        private void StartServer(Func<TcpClient, Task> handle)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var listener = _listener;
            _serverTask = Task.Run(async () =>
            {
                try
                {
                    using (var client = await listener.AcceptTcpClientAsync())
                    {
                        await handle(client);
                    }
                }
                catch (Exception)
                {
                    // The client aborts handshakes on purpose; see TearDown.
                }
            });
        }

        /// <summary>Connects with the given options, bounded, and returns what it threw.</summary>
        private Exception ConnectExpectingFailure(TlsOptions tls)
        {
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            using (var transport = new TcpTransport(tls))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                timeout.CancelAfter(Bound);

                // TargetHost drives SNI and name checks; the dial goes to the loopback address.
                var task = StartConnect(transport, port, timeout.Token);

                // Bounded twice: the token ends the handshake, the wait ends the test.
                Assert.That(WaitSettled(task, Bound + TimeSpan.FromSeconds(5)), Is.True,
                    "connect neither finished nor honoured its cancellation token");
                Assert.That(task.IsFaulted, Is.True,
                    task.IsCanceled ? "connect timed out (cancelled)" : "connect unexpectedly succeeded");
                return task.Exception.InnerException;
            }
        }

        /// <summary>
        /// Runs the connect on a thread-pool thread with a context that posts continuations
        /// back to the pool. <c>Task.AsUniTask()</c> needs a current SynchronizationContext
        /// and, in the EditMode install-probe projects, the player loop is not driven, so
        /// waiting on the Unity context would hang instead of failing.
        /// </summary>
        private static Task StartConnect(TcpTransport transport, int port, CancellationToken token)
            => Task.Run(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new PoolContext());
                return transport.ConnectAsync("127.0.0.1", port, token).AsTask();
            });

        private sealed class PoolContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object state)
                => ThreadPool.QueueUserWorkItem(_ =>
                {
                    // Pool threads start without a context; later awaits in the same flow
                    // need this one to be current again.
                    SetSynchronizationContext(this);
                    d(state);
                });
        }

        /// <summary>Waits for the task to settle; a fault counts as settled (Wait would rethrow it).</summary>
        private static bool WaitSettled(Task task, TimeSpan bound)
        {
            try
            {
                return task.Wait(bound);
            }
            catch (AggregateException)
            {
                return true;
            }
        }

        private static string Describe(Exception ex)
            => ex == null
                ? "no exception"
                : ex.GetType().Name + ": " + ex.Message +
                  (ex.InnerException != null ? " <- " + ex.InnerException.GetType().Name : string.Empty);

        private static X509Certificate2 Load(string pfxBase64)
            => new X509Certificate2(Convert.FromBase64String(pfxBase64), string.Empty);
    }
}
