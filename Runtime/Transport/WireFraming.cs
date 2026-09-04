namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// Framing constants shared by every transport:
    /// <c>[4-byte big-endian length][body]</c>.
    /// </summary>
    public static class WireFraming
    {
        /// <summary>Length prefix width in bytes.</summary>
        public const int HeaderSize = 4;

        /// <summary>
        /// Maximum body length, 1 MiB, matching <c>WireProtocol.MaxMessageSize</c>
        /// on the game server and the gateway's frame cap. A larger declared length
        /// is a protocol error, not something to allocate for.
        /// </summary>
        public const int MaxBodySize = 1 << 20;

        /// <summary>Writes a big-endian length prefix into the first four bytes of <paramref name="destination"/>.</summary>
        public static void WriteLength(byte[] destination, int length)
        {
            destination[0] = (byte)((length >> 24) & 0xFF);
            destination[1] = (byte)((length >> 16) & 0xFF);
            destination[2] = (byte)((length >> 8) & 0xFF);
            destination[3] = (byte)(length & 0xFF);
        }

        /// <summary>
        /// Reads a big-endian length prefix. The result is returned as a signed int
        /// because that is what both servers write and read; a value with the high
        /// bit set therefore comes back negative and is rejected by the caller
        /// rather than silently becoming a huge allocation.
        /// </summary>
        public static int ReadLength(byte[] source)
        {
            return ReadLength(source, 0);
        }

        /// <summary>
        /// As <see cref="ReadLength(byte[])"/>, but reading the prefix at
        /// <paramref name="offset"/>. A buffering reader parses several frames out of
        /// one receive buffer and must not copy the header out first just to decode it.
        /// </summary>
        public static int ReadLength(byte[] source, int offset)
        {
            return (source[offset] << 24) | (source[offset + 1] << 16) |
                   (source[offset + 2] << 8) | source[offset + 3];
        }

        /// <summary>True when a decoded length prefix is usable.</summary>
        public static bool IsValidLength(int length)
        {
            return length > 0 && length <= MaxBodySize;
        }
    }
}
