using System;

namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// How the client authenticates the server on a TLS link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There is deliberately no "accept any certificate" option.</b> That switch is the
    /// single most effective way to make a TLS deployment worthless while every log line
    /// still says TLS: validation that accepts anything is indistinguishable from
    /// validation that works, if only a good certificate is ever tested. It is not offered
    /// here, and it should not be added — a developer who needs to talk to a self-signed
    /// gateway pins that certificate with <see cref="PinnedCertificate"/>, which is
    /// stricter than the public trust store, not looser.
    /// </para>
    /// <para>
    /// The two modes, and nothing between them:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>No pin</b> — the platform's own validation decides, with no callback installed.
    /// The IL2CPP probe in the server repo measured that this genuinely refuses an
    /// untrusted certificate in a real Windows player at both Minimal and High stripping,
    /// which is why this is the default rather than something to be nervous about.
    /// </description></item>
    /// <item><description>
    /// <b>Pinned</b> — the leaf the server presents must be byte-for-byte the certificate
    /// pinned here. Chain, expiry and name are then irrelevant *because a stronger check
    /// already passed*: an attacker must present this exact certificate, which they cannot
    /// without its private key. This is the mode for a dev gateway with a self-signed
    /// certificate.
    /// </description></item>
    /// </list>
    /// </remarks>
    public sealed class TlsOptions
    {
        /// <summary>
        /// Name to validate the certificate against, and the SNI sent to the server. Empty
        /// means "the host passed to <see cref="ITransport.ConnectAsync"/>".
        /// </summary>
        /// <remarks>
        /// Needed when the dial address is not the certificate's name — connecting to
        /// <c>127.0.0.1</c> a certificate issued for <c>gateway.example.com</c>. Setting it
        /// does not weaken anything: the name still has to match, it is just a different
        /// name than the one dialled.
        /// </remarks>
        public string TargetHost { get; set; } = string.Empty;

        /// <summary>
        /// DER bytes of the one certificate the server is allowed to present. Null means
        /// platform validation instead. See the remarks on <see cref="TlsOptions"/>.
        /// </summary>
        /// <remarks>
        /// This is the leaf's raw data — <c>X509Certificate2.RawData</c>, or the bytes of a
        /// <c>.cer</c>/DER file. For a PEM file, strip the header and footer lines and
        /// Base64-decode the middle; <see cref="FromPem"/> does that.
        /// </remarks>
        public byte[] PinnedCertificate { get; set; }

        /// <summary>True when a pin is configured, i.e. the platform trust store is bypassed.</summary>
        public bool IsPinned => PinnedCertificate != null && PinnedCertificate.Length > 0;

        /// <summary>
        /// Reads the first certificate out of a PEM string into <see cref="PinnedCertificate"/>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The text carries no <c>-----BEGIN CERTIFICATE-----</c> block, or its Base64 body
        /// does not decode. Failing here is the point: a pin that silently ends up null
        /// falls back to platform validation, which for a self-signed dev gateway means the
        /// connection is refused with a message about the certificate rather than about the
        /// pin that was not loaded.
        /// </exception>
        public static byte[] FromPem(string pem)
        {
            if (string.IsNullOrEmpty(pem))
            {
                throw new ArgumentException("certificate PEM is empty", nameof(pem));
            }

            const string begin = "-----BEGIN CERTIFICATE-----";
            const string end = "-----END CERTIFICATE-----";

            var start = pem.IndexOf(begin, StringComparison.Ordinal);
            if (start < 0)
            {
                throw new ArgumentException("no BEGIN CERTIFICATE block in the PEM text", nameof(pem));
            }

            start += begin.Length;
            var stop = pem.IndexOf(end, start, StringComparison.Ordinal);
            if (stop < 0)
            {
                throw new ArgumentException("no END CERTIFICATE line in the PEM text", nameof(pem));
            }

            var body = pem.Substring(start, stop - start)
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty)
                .Replace(" ", string.Empty);

            try
            {
                return Convert.FromBase64String(body);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException("the PEM body is not valid Base64: " + ex.Message, nameof(pem), ex);
            }
        }
    }
}
