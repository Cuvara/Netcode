using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Transport;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Cuvara.Netcode.Samples.GatewayTlsProbe
{
    /// <summary>
    /// Runs the gateway hop's TLS (ADR-23) against listeners this scene starts itself, and
    /// reports what each attempt actually did — no gateway, no network, no configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this scene exists.</b> Every case here except the first is a <i>refusal</i>.
    /// "TLS is on" is believed rather than checked, and a TLS switch that quietly produces
    /// a plaintext link — or a validation callback that returns true — reads exactly like a
    /// working one in every log, every metric and every happy-path test. The four cases
    /// below are the ways that can happen, each one run for real and each one reporting
    /// what happened rather than what was intended.
    /// </para>
    /// <para>
    /// <b>What it does not prove.</b> That your gateway is configured for TLS, or that its
    /// certificate is trusted where the game ships. It proves this client refuses what it
    /// should refuse, on this runtime. The server side of the same question is
    /// <c>backend/docs/tls-probe/</c> in the server repo, which measured a real Windows
    /// IL2CPP player at both Minimal and High stripping.
    /// </para>
    /// </remarks>
    public sealed class GatewayTlsProbe : MonoBehaviour
    {
        // A self-signed certificate for CN=localhost, shared byte-for-byte with the server
        // repo's IL2CPP probe. Embedded rather than generated: whether CertificateRequest
        // works under IL2CPP is a SEPARATE question, and a failure there would look like a
        // TLS failure while being nothing of the kind.
        private static readonly string[] PfxChunks =
        {
            "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"
        };

        private const string PfxPassword = "probe";
        private const string HostName = "localhost";

        [Header("Listeners")]
        [Tooltip("Seconds a single attempt may take before it is called a timeout. Every case here resolves in milliseconds on loopback.")]
        [Range(1f, 30f)]
        [SerializeField] private float attemptTimeoutSeconds = 5f;

        private X509Certificate2 _certificate;
        private byte[] _pin = Array.Empty<byte>();

        private TcpListener _tlsListener;
        private TcpListener _plainListener;
        private int _tlsPort;
        private int _plainPort;

        private CancellationTokenSource _life;

        private Label _certLine;
        private Label _listenersLine;
        private Label _lastCaseLine;
        private Label _outcomeLine;
        private Label _verdict;
        private ScrollView _log;

        private int _asExpected;
        private int _surprises;

        private void Start()
        {
            var document = GetComponent<UIDocument>();
            if (document == null || document.rootVisualElement == null)
            {
                Debug.LogError("GatewayTlsProbe needs a UIDocument with GatewayTlsProbeView.uxml assigned.");
                enabled = false;
                return;
            }

            VisualElement root = document.rootVisualElement;
            _certLine = root.Q<Label>("cert-line");
            _listenersLine = root.Q<Label>("listeners-line");
            _lastCaseLine = root.Q<Label>("last-case");
            _outcomeLine = root.Q<Label>("outcome-line");
            _verdict = root.Q<Label>("verdict");
            _log = root.Q<ScrollView>("log");

            root.Q<Button>("pinned").clicked += () => Run(CasePinnedConnects);
            root.Q<Button>("unpinned").clicked += () => Run(CaseUnpinnedIsRefused);
            root.Q<Button>("downgrade").clicked += () => Run(CaseTlsClientAgainstPlaintextServer);
            root.Q<Button>("plain-to-tls").clicked += () => Run(CasePlaintextClientAgainstTlsServer);
            root.Q<Button>("no-options").clicked += () => Run(CaseFactoryRefusesTlsWithoutOptions);
            root.Q<Button>("all").clicked += () => Run(RunEveryCase);

            _life = new CancellationTokenSource();

            try
            {
                LoadCertificate();
                StartListeners();
            }
            catch (Exception ex)
            {
                Append($"SETUP FAILED: {ex.GetType().Name}: {ex.Message}");
                _verdict.text = "setup failed — no case below has run";
                enabled = false;
                return;
            }

            _verdict.text = "nothing run yet";
        }

        private void OnDestroy()
        {
            _life?.Cancel();
            _life?.Dispose();
            try { _tlsListener?.Stop(); } catch (Exception) { }
            try { _plainListener?.Stop(); } catch (Exception) { }
            _certificate?.Dispose();
        }

        // ---- setup ---------------------------------------------------------------------

        private void LoadCertificate()
        {
            _certificate = new X509Certificate2(
                Convert.FromBase64String(string.Concat(PfxChunks)), PfxPassword);

            // The pin is the PUBLIC leaf of the same certificate the listener presents, so
            // the pinned case cannot pass by accident: if the listener ever served a
            // different certificate, these bytes would not match it.
            _pin = _certificate.RawData;

            _certLine.text = $"{_certificate.Subject}   pin: {_pin.Length} B DER, " +
                             $"sha1 {_certificate.Thumbprint?.Substring(0, 16)}…";
        }

        private void StartListeners()
        {
            _tlsListener = new TcpListener(IPAddress.Loopback, 0);
            _tlsListener.Start();
            _tlsPort = ((IPEndPoint)_tlsListener.LocalEndpoint).Port;

            _plainListener = new TcpListener(IPAddress.Loopback, 0);
            _plainListener.Start();
            _plainPort = ((IPEndPoint)_plainListener.LocalEndpoint).Port;

            _listenersLine.text = $"TLS on 127.0.0.1:{_tlsPort}   plaintext on 127.0.0.1:{_plainPort}";
        }

        /// <summary>
        /// Serves exactly one connection, then returns. Every case dials once, so a
        /// per-case accept keeps one case's leftovers from answering the next one's dial.
        /// </summary>
        private async UniTask ServeOnceAsync(TcpListener listener, bool tls, CancellationToken ct)
        {
            TcpClient peer = null;
            Stream stream = null;
            try
            {
                peer = await listener.AcceptTcpClientAsync().AsUniTask().AttachExternalCancellation(ct);

                if (tls)
                {
                    var ssl = new SslStream(peer.GetStream(), false);
                    // SslProtocols.None = the platform's default set. Pinning a list here
                    // would measure the list rather than the platform.
                    await ssl.AuthenticateAsServerAsync(_certificate, false, SslProtocols.None, false)
                        .AsUniTask().AttachExternalCancellation(ct);
                    stream = ssl;
                }
                else
                {
                    stream = peer.GetStream();
                }

                // Answer one framed message so a successful case proves the link carries
                // traffic, not merely that a handshake completed.
                var body = new byte[] { 0x2A };
                var frame = new byte[WireFraming.HeaderSize + body.Length];
                WireFraming.WriteLength(frame, body.Length);
                Buffer.BlockCopy(body, 0, frame, WireFraming.HeaderSize, body.Length);
                await stream.WriteAsync(frame, 0, frame.Length, ct).AsUniTask();
            }
            catch (Exception)
            {
                // A refused handshake lands here, and that is the expected outcome of three
                // of the five cases — the client's verdict is what the scene reports.
            }
            finally
            {
                stream?.Dispose();
                peer?.Close();
            }
        }

        // ---- the cases -----------------------------------------------------------------

        private async UniTask CasePinnedConnects(CancellationToken ct)
        {
            Case("TLS + pinned certificate", "connects, and carries a frame");

            var serving = ServeOnceAsync(_tlsListener, true, ct);
            var options = new TlsOptions { TargetHost = HostName, PinnedCertificate = _pin };
            var transport = new TcpTransport(options);

            try
            {
                await transport.ConnectAsync("127.0.0.1", _tlsPort, ct);
                var frame = await transport.ReadFrameAsync(ct);

                var ok = frame != null && frame.Length == 1 && frame[0] == 0x2A;
                Outcome(ok,
                    ok
                        ? $"connected over {transport.NegotiatedProtocol} and read the frame back"
                        : "connected, but the frame did not come back intact");
            }
            catch (Exception ex)
            {
                Outcome(false, $"REFUSED, which it should not be: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                transport.Close();
                await serving;
            }
        }

        private async UniTask CaseUnpinnedIsRefused(CancellationToken ct)
        {
            Case("TLS, no pin (platform trust store decides)",
                 "REFUSED — the certificate is self-signed");

            var serving = ServeOnceAsync(_tlsListener, true, ct);

            // No PinnedCertificate: TcpTransport installs no callback at all, so the
            // platform's own validation is the only thing deciding. This is the case that
            // would silently pass if anyone ever added an "accept anything" option.
            var transport = new TcpTransport(new TlsOptions { TargetHost = HostName });

            try
            {
                await transport.ConnectAsync("127.0.0.1", _tlsPort, ct);
                Outcome(false, "CONNECTED — platform validation accepted a self-signed certificate");
            }
            catch (TransportException ex)
            {
                Outcome(true, "refused: " + ex.Message);
            }
            catch (Exception ex)
            {
                Outcome(false, $"refused, but with an unexpected type: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                transport.Close();
                await serving;
            }
        }

        private async UniTask CaseTlsClientAgainstPlaintextServer(CancellationToken ct)
        {
            Case("TLS client → plaintext gateway", "REFUSED — never a silent downgrade");

            var serving = ServeOnceAsync(_plainListener, false, ct);
            var transport = new TcpTransport(new TlsOptions { TargetHost = HostName, PinnedCertificate = _pin });

            try
            {
                await transport.ConnectAsync("127.0.0.1", _plainPort, ct);
                Outcome(false, "CONNECTED — a TLS client accepted a cleartext peer");
            }
            catch (TransportException ex)
            {
                Outcome(true, "refused: " + ex.Message);
            }
            catch (Exception ex)
            {
                Outcome(false, $"refused, but with an unexpected type: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                transport.Close();
                await serving;
            }
        }

        private async UniTask CasePlaintextClientAgainstTlsServer(CancellationToken ct)
        {
            Case("plaintext client → TLS gateway", "NO frame — it stalls, and a timeout is the shape of it");

            var serving = ServeOnceAsync(_tlsListener, true, ct);
            var transport = new TcpTransport();

            // This case gets its OWN short budget, because a stall is the expected result
            // and the scene must not sit on it. Measured in a Unity play-mode run: the TCP
            // connect succeeds -- TLS is above it -- and then both ends wait. The client
            // waits to read a frame; the server waits for a ClientHello that a plaintext
            // client will never send. Nothing refuses anything.
            //
            // This scene originally claimed the mismatch was "loud". It is not. It is
            // BOUNDED, which is a different and weaker property, and it is bounded only
            // because GatewayClient wraps the whole exchange -- connect, send, and the
            // reply read -- in NetworkSettings.ConnectTimeout (10 s by default). A caller
            // driving TcpTransport directly, as this case does, gets no bound at all.
            using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                stall.CancelAfter(TimeSpan.FromSeconds(2));

                try
                {
                    await transport.ConnectAsync("127.0.0.1", _tlsPort, stall.Token);
                    var frame = await transport.ReadFrameAsync(stall.Token);

                    Outcome(frame == null || frame.Length == 0,
                        frame == null
                            ? "connected, then the link closed with no frame"
                            : $"read {frame.Length} bytes of something from a TLS listener");
                }
                catch (OperationCanceledException)
                {
                    Outcome(true, "stalled with no frame until the 2s budget expired — " +
                                  "in the real client this is ConnectTimeout, not a refusal");
                }
                catch (TransportException ex)
                {
                    Outcome(true, "refused at the frame layer: " + ex.Message);
                }
                catch (Exception ex)
                {
                    Outcome(false, $"unexpected: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    transport.Close();
                    await serving;
                }
            }
        }

        private UniTask CaseFactoryRefusesTlsWithoutOptions(CancellationToken ct)
        {
            Case("factory asked for TLS with no TlsOptions", "throws, rather than returning cleartext");

            try
            {
                var transport = new DefaultTransportFactory().Create(TransportKind.TcpTls);
                var tls = transport is TcpTransport tcp && tcp.IsTls;
                transport.Dispose();
                Outcome(false, tls
                    ? "returned a TLS transport out of nowhere"
                    : "returned a PLAINTEXT transport for a TLS request — the silent downgrade");
            }
            catch (TransportException ex)
            {
                Outcome(true, "threw: " + ex.Message);
            }

            return UniTask.CompletedTask;
        }

        private async UniTask RunEveryCase(CancellationToken ct)
        {
            _asExpected = 0;
            _surprises = 0;
            Append("── running every case ──");
            await CasePinnedConnects(ct);
            await CaseUnpinnedIsRefused(ct);
            await CaseTlsClientAgainstPlaintextServer(ct);
            await CasePlaintextClientAgainstTlsServer(ct);
            await CaseFactoryRefusesTlsWithoutOptions(ct);
        }

        // ---- plumbing ------------------------------------------------------------------

        private void Run(Func<CancellationToken, UniTask> body)
        {
            RunAsync(body).Forget();
        }

        private async UniTaskVoid RunAsync(Func<CancellationToken, UniTask> body)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_life.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(attemptTimeoutSeconds));
                try
                {
                    await body(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    Outcome(false, $"TIMED OUT after {attemptTimeoutSeconds:0.#}s — on loopback this means something hung");
                }
                catch (Exception ex)
                {
                    Outcome(false, $"the case itself threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private void Case(string what, string expected)
        {
            _lastCaseLine.text = what;
            _outcomeLine.text = "expected: " + expected;
        }

        private void Outcome(bool asExpected, string detail)
        {
            if (asExpected)
            {
                _asExpected++;
            }
            else
            {
                _surprises++;
            }

            _outcomeLine.text = (asExpected ? "as expected — " : "NOT AS EXPECTED — ") + detail;
            Append($"[{(asExpected ? " ok " : "SURPRISE")}] {_lastCaseLine.text}: {detail}");

            _verdict.text = _surprises == 0
                ? $"{_asExpected} case(s), all as expected"
                : $"{_surprises} SURPRISE(S) out of {_asExpected + _surprises} — read the log";
            // The class name must match the stylesheet exactly: a typo here does not fail,
            // it just leaves the verdict green while it reports a surprise.
            _verdict.EnableInClassList("cuvara-probe__verdict--bad", _surprises != 0);
        }

        private void Append(string line)
        {
            if (_log == null)
            {
                Debug.Log("[gateway-tls] " + line);
                return;
            }

            _log.Add(new Label(line) { pickingMode = PickingMode.Ignore });
            _log.schedule.Execute(() => _log.scrollOffset = new Vector2(0, float.MaxValue));
        }
    }
}
