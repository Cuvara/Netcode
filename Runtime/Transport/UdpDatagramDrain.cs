using System.Net.Sockets;

namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// Reads every datagram already queued on a UDP socket, without waiting, and feeds each one
    /// to the KCP session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the receive loop needs it.</b> <see cref="KcpTransport"/> waits for a datagram with
    /// <c>UdpClient.ReceiveAsync().AsUniTask()</c>. <c>AsUniTask</c> continues through
    /// <c>TaskScheduler.FromCurrentSynchronizationContext()</c> by default, which on the player
    /// loop is Unity's main-thread context: every completion is posted and runs on the next
    /// frame. One await per datagram therefore read at most one datagram per frame. That is
    /// invisible at 15 snapshots a second, and it is what turned a client stall into a paced
    /// backlog: after a 2 s stall the socket held the server's KCP retransmissions as well as the
    /// snapshots, and the 32 snapshots reached the world over 315 to 353 ms, one about every five
    /// frames (rpg-factory testlab S13, runs 20261022T194438Z-S13-vdqw and -q3rr). At a vsynced
    /// 60 fps the same backlog would take seconds.
    /// </para>
    /// <para>
    /// Draining synchronously after each await reads the whole queue in the frame the main thread
    /// wakes in, so a backlog arrives in one frame and is merged as one: the shape every
    /// downstream consumer was designed and tested against.
    /// </para>
    /// <para>
    /// Unity-free, so it compiles in the headless suite, where it runs against real loopback
    /// sockets (<c>TransportReceivePacingTests</c>).
    /// </para>
    /// </remarks>
    internal static class UdpDatagramDrain
    {
        /// <summary>
        /// Upper bound on datagrams read in one call, so a flood cannot hold a frame. Far above a
        /// stall's backlog: 2 s of a 60 Hz stream with every segment retransmitted on each 10 ms
        /// flush is a few hundred datagrams.
        /// </summary>
        public const int DefaultMaxDatagrams = 4096;

        /// <summary>
        /// Feeds every datagram queued on <paramref name="socket"/> to <paramref name="session"/>.
        /// Never blocks: reads only while <see cref="Socket.Available"/> says a datagram is there.
        /// </summary>
        /// <param name="socket">A connected UDP socket.</param>
        /// <param name="buffer">Scratch, at least one MTU. Reused for every datagram.</param>
        /// <param name="session">Takes each datagram; it copies what it keeps.</param>
        /// <param name="maxDatagrams">See <see cref="DefaultMaxDatagrams"/>.</param>
        /// <returns>Datagrams read.</returns>
        public static int Drain(Socket socket, byte[] buffer, KcpClientSession session, int maxDatagrams = DefaultMaxDatagrams)
        {
            int read = 0;
            int errors = 0;
            while (read < maxDatagrams && socket.Available > 0)
            {
                int length;
                try
                {
                    length = socket.Receive(buffer, 0, buffer.Length, SocketFlags.None);
                }
                catch (SocketException)
                {
                    // Per-datagram ICMP errors and oversized datagrams, as the async loop treats
                    // them: skip and go on. Bounded, so a socket that only errors cannot spin.
                    if (++errors >= maxDatagrams) break;
                    continue;
                }

                if (length <= 0)
                {
                    break;
                }

                session.Input(buffer, length);
                read++;
            }

            return read;
        }
    }
}
