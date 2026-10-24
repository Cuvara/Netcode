using System;
using NUnit.Framework;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Exercises the kcp-go-compatible AES-256-CFB packet encryption.
    /// </summary>
    [TestFixture]
    public sealed class KcpCryptoTests
    {
        [Test]
        public void NullOrEmptyKey_ReturnsNull()
        {
            Assert.That(KcpCrypto.TryCreate(null), Is.Null);
            Assert.That(KcpCrypto.TryCreate(""), Is.Null);
            Assert.That(KcpCrypto.TryCreate("   "), Is.Null);
        }

        [Test]
        public void SealAndOpenRoundTrip()
        {
            using var crypto = KcpCrypto.TryCreate("test-key-for-roundtrip");
            Assert.That(crypto, Is.Not.Null);

            // Simulate a KCP payload.
            var payload = System.Text.Encoding.UTF8.GetBytes("hello encrypted KCP");

            // Build a packet with header room.
            var packet = new byte[KcpCrypto.HeaderSize + payload.Length];
            Buffer.BlockCopy(payload, 0, packet, KcpCrypto.HeaderSize, payload.Length);

            // Seal encrypts in place.
            crypto.Seal(packet, 0, packet.Length);

            // The encrypted bytes should differ from the plaintext.
            var encrypted = new byte[payload.Length];
            Buffer.BlockCopy(packet, KcpCrypto.HeaderSize, encrypted, 0, payload.Length);
            Assert.That(encrypted, Is.Not.EqualTo(payload),
                "seal should have changed the payload bytes");

            // Open decrypts in place and returns the KCP body length.
            int bodyLen = crypto.Open(packet, 0, packet.Length);
            Assert.That(bodyLen, Is.EqualTo(payload.Length));

            // Verify the decrypted content matches.
            var decrypted = new byte[bodyLen];
            Buffer.BlockCopy(packet, KcpCrypto.HeaderSize, decrypted, 0, bodyLen);
            Assert.That(
                System.Text.Encoding.UTF8.GetString(decrypted),
                Is.EqualTo("hello encrypted KCP"));
        }

        [Test]
        public void SteadyStateSealAndOpen_AllocatesUnder32BytesPerDatagram()
        {
            const int payloadSize = 1200;
            const int warmup = 2000;
            const int measuredPairs = 10000;
            using var crypto = KcpCrypto.TryCreate("allocation-probe-key");
            var packet = new byte[KcpCrypto.HeaderSize + payloadSize];

            for (int i = 0; i < warmup; i++)
            {
                crypto.Seal(packet, 0, packet.Length);
                Assert.That(crypto.Open(packet, 0, packet.Length), Is.EqualTo(payloadSize));
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < measuredPairs; i++)
            {
                crypto.Seal(packet, 0, packet.Length);
                if (crypto.Open(packet, 0, packet.Length) != payloadSize)
                    Assert.Fail("steady-state packet failed checksum");
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            double bytesPerPair = (double)allocated / measuredPairs;

            TestContext.WriteLine($"Seal+Open allocations: {allocated} B / {measuredPairs} pairs = {bytesPerPair:F2} B/pair");
            Assert.That(bytesPerPair, Is.LessThan(32d), "steady-state Seal+Open must stay below 32 B per datagram pair");
        }

        [Test]
        public void WrongKey_FailsChecksum()
        {
            using var sender = KcpCrypto.TryCreate("key-A");
            using var receiver = KcpCrypto.TryCreate("key-B");

            var payload = System.Text.Encoding.UTF8.GetBytes("secret");
            var packet = new byte[KcpCrypto.HeaderSize + payload.Length];
            Buffer.BlockCopy(payload, 0, packet, KcpCrypto.HeaderSize, payload.Length);

            sender.Seal(packet, 0, packet.Length);

            int bodyLen = receiver.Open(packet, 0, packet.Length);
            Assert.That(bodyLen, Is.EqualTo(0),
                "decrypting with the wrong key should fail the CRC check");
        }

        [Test]
        public void SameKey_DifferentInstances_RoundTrips()
        {
            const string key = "shared-transport-key";
            using var sender = KcpCrypto.TryCreate(key);
            using var receiver = KcpCrypto.TryCreate(key);

            var payload = System.Text.Encoding.UTF8.GetBytes("inter-instance");
            var packet = new byte[KcpCrypto.HeaderSize + payload.Length];
            Buffer.BlockCopy(payload, 0, packet, KcpCrypto.HeaderSize, payload.Length);

            sender.Seal(packet, 0, packet.Length);

            int bodyLen = receiver.Open(packet, 0, packet.Length);
            Assert.That(bodyLen, Is.EqualTo(payload.Length));

            var decrypted = new byte[bodyLen];
            Buffer.BlockCopy(packet, KcpCrypto.HeaderSize, decrypted, 0, bodyLen);
            Assert.That(
                System.Text.Encoding.UTF8.GetString(decrypted),
                Is.EqualTo("inter-instance"));
        }

        [Test]
        public void HexKey_64Chars_IsDecodedVerbatim()
        {
            // 64 hex chars = 32 bytes = AES-256 key length.
            const string hexKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            var derived = KcpCrypto.DeriveKey(hexKey);
            Assert.That(derived.Length, Is.EqualTo(32));

            // Verify the first few bytes match the hex.
            Assert.That(derived[0], Is.EqualTo(0x01));
            Assert.That(derived[1], Is.EqualTo(0x23));
            Assert.That(derived[2], Is.EqualTo(0x45));
        }

        [Test]
        public void DisposedCrypto_ReturnsClosedResultsWithoutTouchingTheTransform()
        {
            var crypto = KcpCrypto.TryCreate("test-key-for-dispose");
            var packet = new byte[KcpCrypto.HeaderSize + 32];
            crypto.Dispose();

            Assert.That(crypto.Seal(packet, 0, packet.Length), Is.False);
            Assert.That(crypto.Open(packet, 0, packet.Length), Is.EqualTo(-1));
            Assert.DoesNotThrow(() => crypto.Dispose());
        }

        [Test]
        public void NonHexKey_IsStretchedWithHkdf()
        {
            var key1 = KcpCrypto.DeriveKey("my-passphrase");
            Assert.That(key1.Length, Is.EqualTo(32));

            // Same passphrase should produce the same key.
            var key2 = KcpCrypto.DeriveKey("my-passphrase");
            Assert.That(key1, Is.EqualTo(key2));

            // Different passphrase should produce a different key.
            var key3 = KcpCrypto.DeriveKey("other-passphrase");
            Assert.That(key1, Is.Not.EqualTo(key3));
        }

        [Test]
        public void TooShortPacket_ReturnsZero()
        {
            using var crypto = KcpCrypto.TryCreate("key");

            int bodyLen = crypto.Open(new byte[5], 0, 5);
            Assert.That(bodyLen, Is.EqualTo(0),
                "a packet shorter than the header should be rejected");
        }

        [Test]
        public void Crc32_ComputesCorrectly()
        {
            // Known CRC32-IEEE for "123456789" is 0xCBF43926.
            var data = System.Text.Encoding.ASCII.GetBytes("123456789");
            uint crc = Crc32.Compute(data, 0, data.Length);
            Assert.That(crc, Is.EqualTo(0xCBF43926u));
        }
    }
}
