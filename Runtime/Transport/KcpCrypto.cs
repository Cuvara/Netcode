using System;
using System.Security.Cryptography;
using System.Text;

namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// Packet-level encryption compatible with <c>github.com/xtaci/kcp-go/v5</c>'s
    /// <c>NewAESBlockCrypt</c>, as used by
    /// <c>backend/shared/transport/crypto.go</c> and
    /// <c>GameServer.Net.Transport.KcpCrypto</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every outgoing datagram is laid out as
    /// <c>| nonce (16B, random) | crc32-IEEE (4B, LE) | KCP bytes |</c>
    /// then the WHOLE buffer is AES-CFB encrypted with a fixed IV.
    /// </para>
    /// <para>
    /// A peer with the wrong key produces bytes that decrypt to noise, fail the
    /// CRC, and are dropped — "fail closed" behaviour.
    /// </para>
    /// </remarks>
    internal sealed class KcpCrypto : IDisposable
    {
        public const int NonceSize = 16;
        public const int CrcSize = 4;
        public const int HeaderSize = NonceSize + CrcSize;

        private const int KeySize = 32;
        private const string HkdfInfo = "rpg-mmo/transport/kcp/aes-256";

        private static readonly byte[] InitialVector =
        {
            167, 115, 79, 156, 18, 172, 27, 1, 164, 21, 242, 193, 252, 120, 230, 107
        };

        private readonly Aes _aes;

        private KcpCrypto(byte[] key)
        {
            _aes = Aes.Create();
            _aes.Key = key;
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
        }

        /// <summary>
        /// Builds the crypto layer, or null when the key is empty (plaintext dev default).
        /// </summary>
        public static KcpCrypto TryCreate(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            return new KcpCrypto(DeriveKey(key));
        }

        /// <summary>
        /// Derives the 32-byte AES-256 key from an operator-supplied transport key.
        /// 64 hex characters are decoded verbatim; anything else is stretched with
        /// HKDF-SHA256 (no salt, fixed info string).
        /// </summary>
        internal static byte[] DeriveKey(string key)
        {
            string k = (key ?? "").Trim();
            if (k.Length == 0)
                throw new ArgumentException("derive transport key: empty key", nameof(key));

            if (k.Length == 2 * KeySize)
            {
                byte[] hex = TryDecodeHex(k);
                if (hex != null) return hex;
            }

            return HkdfSha256(Encoding.UTF8.GetBytes(k), Encoding.UTF8.GetBytes(HkdfInfo), KeySize);
        }

        /// <summary>
        /// Encrypts <paramref name="packet"/> in place. The caller must have reserved
        /// <see cref="HeaderSize"/> bytes at the front for the nonce and CRC.
        /// </summary>
        public void Seal(byte[] packet, int offset, int length)
        {
            // Fill nonce with random bytes.
            var rng = RandomNumberGenerator.Create();
            var nonce = new byte[NonceSize];
            rng.GetBytes(nonce);
            Buffer.BlockCopy(nonce, 0, packet, offset, NonceSize);

            // CRC32 of the KCP payload.
            uint crc = Crc32.Compute(packet, offset + HeaderSize, length - HeaderSize);
            packet[offset + NonceSize] = (byte)(crc & 0xFF);
            packet[offset + NonceSize + 1] = (byte)((crc >> 8) & 0xFF);
            packet[offset + NonceSize + 2] = (byte)((crc >> 16) & 0xFF);
            packet[offset + NonceSize + 3] = (byte)((crc >> 24) & 0xFF);

            CfbEncrypt(packet, offset, length);
        }

        /// <summary>
        /// Decrypts <paramref name="packet"/> in place and returns the length of the
        /// KCP byte range (starting at <paramref name="offset"/> + <see cref="HeaderSize"/>),
        /// or 0 if the datagram is too short or fails the checksum.
        /// </summary>
        public int Open(byte[] packet, int offset, int length)
        {
            if (length < HeaderSize) return 0;

            CfbDecrypt(packet, offset, length);

            // Verify CRC32 of the KCP payload.
            int bodyOffset = offset + HeaderSize;
            int bodyLength = length - HeaderSize;
            uint want = (uint)packet[offset + NonceSize]
                      | ((uint)packet[offset + NonceSize + 1] << 8)
                      | ((uint)packet[offset + NonceSize + 2] << 16)
                      | ((uint)packet[offset + NonceSize + 3] << 24);

            return Crc32.Compute(packet, bodyOffset, bodyLength) == want ? bodyLength : 0;
        }

        private void CfbEncrypt(byte[] buf, int offset, int length)
        {
            byte[] tbl = new byte[16];
            byte[] tmp = new byte[16];

            using var enc = _aes.CreateEncryptor(_aes.Key, null);
            enc.TransformBlock(InitialVector, 0, 16, tbl, 0);

            int i = 0;
            for (; i + 16 <= length; i += 16)
            {
                int pos = offset + i;
                for (int j = 0; j < 16; j++) buf[pos + j] ^= tbl[j];
                enc.TransformBlock(buf, pos, 16, tmp, 0);
                Buffer.BlockCopy(tmp, 0, tbl, 0, 16);
            }
            for (int j = 0; i + j < length; j++) buf[offset + i + j] ^= tbl[j];
        }

        private void CfbDecrypt(byte[] buf, int offset, int length)
        {
            byte[] tbl = new byte[16];
            byte[] next = new byte[16];

            using var enc = _aes.CreateEncryptor(_aes.Key, null);
            enc.TransformBlock(InitialVector, 0, 16, tbl, 0);

            int i = 0;
            for (; i + 16 <= length; i += 16)
            {
                int pos = offset + i;
                enc.TransformBlock(buf, pos, 16, next, 0);
                for (int j = 0; j < 16; j++) buf[pos + j] ^= tbl[j];
                Buffer.BlockCopy(next, 0, tbl, 0, 16);
            }
            for (int j = 0; i + j < length; j++) buf[offset + i + j] ^= tbl[j];
        }

        private static byte[] TryDecodeHex(string hex)
        {
            if (hex.Length % 2 != 0) return null;
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int hi = HexVal(hex[i * 2]);
                int lo = HexVal(hex[i * 2 + 1]);
                if (hi < 0 || lo < 0) return null;
                bytes[i] = (byte)((hi << 4) | lo);
            }
            return bytes;
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// <summary>
        /// Minimal HKDF-SHA256 (extract + expand, no salt) for .NET Standard 2.1
        /// compatibility. Unity does not ship <c>System.Security.Cryptography.HKDF</c>.
        /// </summary>
        private static byte[] HkdfSha256(byte[] ikm, byte[] info, int outputLength)
        {
            // Extract: PRK = HMAC-SHA256(salt=empty, IKM)
            byte[] salt = new byte[32]; // zero salt
            byte[] prk;
            using (var hmac = new HMACSHA256(salt))
            {
                prk = hmac.ComputeHash(ikm);
            }

            // Expand: OKM = T(1) || T(2) || ...
            var output = new byte[outputLength];
            int offset = 0;
            byte counter = 1;
            byte[] prev = Array.Empty<byte>();

            using (var hmac = new HMACSHA256(prk))
            {
                while (offset < outputLength)
                {
                    // T(i) = HMAC-SHA256(PRK, T(i-1) || info || counter)
                    var input = new byte[prev.Length + info.Length + 1];
                    Buffer.BlockCopy(prev, 0, input, 0, prev.Length);
                    Buffer.BlockCopy(info, 0, input, prev.Length, info.Length);
                    input[input.Length - 1] = counter;

                    prev = hmac.ComputeHash(input);
                    int toCopy = Math.Min(prev.Length, outputLength - offset);
                    Buffer.BlockCopy(prev, 0, output, offset, toCopy);
                    offset += toCopy;
                    counter++;
                }
            }

            return output;
        }

        public void Dispose()
        {
            _aes?.Dispose();
        }
    }

    /// <summary>
    /// CRC32-IEEE, the checksum kcp-go puts in the crypt header.
    /// </summary>
    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public static uint Compute(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < length; i++)
                crc = Table[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
