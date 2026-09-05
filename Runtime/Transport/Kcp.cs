using System;
using System.Collections.Generic;

namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// A KCP (ARQ) state machine, ported from the game server's <c>Kcp.cs</c> which
    /// itself is a port of <c>github.com/xtaci/kcp-go/v5</c>'s <c>kcp.go</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is pure protocol: no sockets, no threads. Bytes arrive via
    /// <see cref="Input"/>, leave via the <c>output</c> callback, and the caller is
    /// responsible for driving <see cref="Update"/> on a timer.
    /// </para>
    /// <para>
    /// Wire-compatible with the server's <c>GameServer.Net.Transport.Kcp</c> and
    /// with kcp-go configured by <c>backend/shared/transport</c>. The two sides
    /// MUST produce identical bytes on the wire.
    /// </para>
    /// </remarks>
    public sealed class Kcp
    {
        /// <summary>Per-segment header size: conv(4) cmd(1) frg(1) wnd(2) ts(4) sn(4) una(4) len(4).</summary>
        public const int Overhead = 24;

        private const byte CmdPush = 81;
        private const byte CmdAck = 82;
        private const byte CmdWask = 83;
        private const byte CmdWins = 84;

        private const uint AskSend = 1;
        private const uint AskTell = 2;

        private const uint RtoNdl = 30;
        private const uint RtoMin = 100;
        private const uint RtoDef = 200;
        private const uint RtoMax = 60000;

        private const uint WndSnd = 32;
        private const uint WndRcv = 32;
        private const int MtuDef = 1400;
        private const uint Interval0 = 100;
        private const uint DeadLink = 20;
        private const uint ThreshInit = 2;
        private const uint ThreshMin = 2;
        private const uint ProbeInit = 500;
        private const uint ProbeLimit = 120000;

        private enum FlushType { AckOnly, Full }

        private sealed class Segment
        {
            public uint Conv;
            public byte Cmd;
            public byte Frg;
            public ushort Wnd;
            public uint Ts;
            public uint Sn;
            public uint Una;
            public uint Rto;
            public uint Xmit;
            public uint Resendts;
            public uint Fastack;
            public bool Acked;
            public byte[] Data = Array.Empty<byte>();

            public void EncodeHeader(byte[] ptr, int offset)
            {
                WriteUInt32LE(ptr, offset, Conv);
                ptr[offset + 4] = Cmd;
                ptr[offset + 5] = Frg;
                WriteUInt16LE(ptr, offset + 6, Wnd);
                WriteUInt32LE(ptr, offset + 8, Ts);
                WriteUInt32LE(ptr, offset + 12, Sn);
                WriteUInt32LE(ptr, offset + 16, Una);
                WriteUInt32LE(ptr, offset + 20, (uint)Data.Length);
            }
        }

        private struct AckItem
        {
            public uint Sn;
            public uint Ts;

            public AckItem(uint sn, uint ts) { Sn = sn; Ts = ts; }
        }

        private readonly uint _conv;
        private readonly Action<byte[], int> _output;

        private uint _mtu = MtuDef;
        private uint _mss = MtuDef - Overhead;
        private uint _state;

        private uint _sndUna, _sndNxt, _rcvNxt;
        private uint _ssthresh = ThreshInit;
        private int _rxRttvar, _rxSrtt;
        private uint _rxRto = RtoDef, _rxMinrto = RtoMin;
        private uint _sndWnd = WndSnd, _rcvWnd = WndRcv, _rmtWnd = WndRcv;
        private uint _cwnd, _incr;
        private uint _probe, _tsProbe, _probeWait;
        private uint _interval = Interval0, _tsFlush = Interval0;
        private uint _nodelay, _updated;
        private readonly uint _deadLink = DeadLink;
        private int _fastresend;
        private int _nocwnd;

        private readonly List<Segment> _sndQueue = new List<Segment>();
        private readonly List<Segment> _sndBuf = new List<Segment>();
        private readonly List<Segment> _rcvQueue = new List<Segment>();
        private readonly List<Segment> _rcvBuf = new List<Segment>();
        private readonly List<AckItem> _acklist = new List<AckItem>();

        private byte[] _buffer;

        /// <summary>Stream mode: 1 concatenates writes across segments, 0 preserves message boundaries.</summary>
        public int Stream { get; set; }

        /// <summary>True once the peer has failed <see cref="DeadLink"/> retransmissions of a segment.</summary>
        public bool DeadLinkReached => _state == 0xFFFFFFFFu;

        public uint Conv => _conv;

        public uint IntervalMs => _interval;

        public Kcp(uint conv, Action<byte[], int> output)
        {
            _conv = conv;
            _output = output;
            _buffer = new byte[(_mtu + Overhead) * 3];
        }

        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        internal static uint CurrentMs() => (uint)Clock.ElapsedMilliseconds;

        private static int Diff(uint later, uint earlier) => (int)(later - earlier);

        // ── Configuration ────────────────────────────────────────────────────────

        public void SetNoDelay(int nodelay, int interval, int resend, int nc)
        {
            if (nodelay >= 0)
            {
                _nodelay = (uint)nodelay;
                _rxMinrto = nodelay != 0 ? RtoNdl : RtoMin;
            }
            if (interval >= 0)
            {
                if (interval > 5000) interval = 5000;
                else if (interval < 10) interval = 10;
                _interval = (uint)interval;
            }
            if (resend >= 0) _fastresend = resend;
            if (nc >= 0) _nocwnd = nc;
        }

        public void WndSize(int sndwnd, int rcvwnd)
        {
            if (sndwnd > 0) _sndWnd = (uint)sndwnd;
            if (rcvwnd > 0) _rcvWnd = (uint)rcvwnd;
        }

        public bool SetMtu(int mtu)
        {
            if (mtu <= Overhead) return false;
            _mtu = (uint)mtu;
            _mss = _mtu - Overhead;
            _buffer = new byte[(mtu + Overhead) * 3];
            return true;
        }

        public int WaitSnd => _sndBuf.Count + _sndQueue.Count;

        // ── Application data in and out ──────────────────────────────────────────

        public int PeekSize()
        {
            if (_rcvQueue.Count == 0) return -1;
            var seg = _rcvQueue[0];
            if (seg.Frg == 0) return seg.Data.Length;
            if (_rcvQueue.Count < seg.Frg + 1) return -1;

            int length = 0;
            foreach (var s in _rcvQueue)
            {
                length += s.Data.Length;
                if (s.Frg == 0) break;
            }
            return length;
        }

        public int Recv(byte[] buffer, int bufferLength)
        {
            int peeksize = PeekSize();
            if (peeksize < 0) return -1;
            if (peeksize > bufferLength) return -2;

            bool fastRecover = _rcvQueue.Count >= (int)_rcvWnd;

            int n = 0;
            while (_rcvQueue.Count > 0)
            {
                var seg = _rcvQueue[0];
                _rcvQueue.RemoveAt(0);
                Buffer.BlockCopy(seg.Data, 0, buffer, n, seg.Data.Length);
                n += seg.Data.Length;
                if (seg.Frg == 0) break;
            }

            MoveRcvBufToQueue();

            if (_rcvQueue.Count < (int)_rcvWnd && fastRecover) _probe |= AskTell;
            return n;
        }

        public int Send(byte[] data, int offset, int length)
        {
            if (length == 0) return -1;

            if (Stream != 0 && _sndQueue.Count > 0)
            {
                var last = _sndQueue[_sndQueue.Count - 1];
                if (last.Data.Length < (int)_mss)
                {
                    int capacity = (int)_mss - last.Data.Length;
                    int extend = Math.Min(length, capacity);
                    var grown = new byte[last.Data.Length + extend];
                    Buffer.BlockCopy(last.Data, 0, grown, 0, last.Data.Length);
                    Buffer.BlockCopy(data, offset, grown, last.Data.Length, extend);
                    last.Data = grown;
                    offset += extend;
                    length -= extend;
                }
                if (length == 0) return 0;
            }

            int count = length <= (int)_mss
                ? 1
                : (length + (int)_mss - 1) / (int)_mss;
            if (count > 255) return -2;
            if (count == 0) count = 1;

            for (int i = 0; i < count; i++)
            {
                int size = Math.Min(length, (int)_mss);
                var seg = new Segment();
                seg.Data = new byte[size];
                Buffer.BlockCopy(data, offset, seg.Data, 0, size);
                seg.Frg = Stream == 0 ? (byte)(count - i - 1) : (byte)0;
                _sndQueue.Add(seg);
                offset += size;
                length -= size;
            }
            return 0;
        }

        // ── ACK / RTT bookkeeping ────────────────────────────────────────────────

        private void UpdateAck(int rtt)
        {
            if (_rxSrtt == 0)
            {
                _rxSrtt = rtt;
                _rxRttvar = rtt >> 1;
            }
            else
            {
                int delta = rtt - _rxSrtt;
                _rxSrtt += delta >> 3;
                if (delta < 0) delta = -delta;
                if (rtt < _rxSrtt - _rxRttvar) _rxRttvar += (delta - _rxRttvar) >> 5;
                else _rxRttvar += (delta - _rxRttvar) >> 2;
            }
            uint rto = (uint)_rxSrtt + Math.Max(_interval, (uint)_rxRttvar << 2);
            _rxRto = Math.Min(Math.Max(_rxMinrto, rto), RtoMax);
        }

        private void ShrinkBuf() => _sndUna = _sndBuf.Count > 0 ? _sndBuf[0].Sn : _sndNxt;

        private void ParseAck(uint sn)
        {
            if (Diff(sn, _sndUna) < 0 || Diff(sn, _sndNxt) >= 0) return;
            foreach (var seg in _sndBuf)
            {
                if (sn == seg.Sn) { seg.Acked = true; seg.Data = Array.Empty<byte>(); break; }
                if (Diff(sn, seg.Sn) < 0) break;
            }
        }

        private bool ParseFastack(uint sn, uint ts)
        {
            bool shouldFastAck = false;
            if (Diff(sn, _sndUna) < 0 || Diff(sn, _sndNxt) >= 0) return false;

            foreach (var seg in _sndBuf)
            {
                if (Diff(sn, seg.Sn) < 0) break;
                if (sn != seg.Sn && Diff(seg.Ts, ts) <= 0)
                {
                    if (seg.Fastack != 0xFFFFFFFFu)
                    {
                        seg.Fastack++;
                        if (seg.Fastack >= (uint)_fastresend) shouldFastAck = true;
                    }
                }
            }
            return shouldFastAck;
        }

        private int ParseUna(uint una)
        {
            int count = 0;
            foreach (var seg in _sndBuf)
            {
                if (Diff(una, seg.Sn) > 0) count++;
                else break;
            }
            if (count > 0) _sndBuf.RemoveRange(0, count);
            return count;
        }

        private void MoveRcvBufToQueue()
        {
            while (_rcvBuf.Count > 0)
            {
                var seg = _rcvBuf[0];
                if (seg.Sn != _rcvNxt || _rcvQueue.Count >= (int)_rcvWnd) break;
                _rcvBuf.RemoveAt(0);
                _rcvQueue.Add(seg);
                _rcvNxt++;
            }
        }

        private bool ParseData(Segment newseg)
        {
            uint sn = newseg.Sn;
            if (Diff(sn, _rcvNxt + _rcvWnd) >= 0 || Diff(sn, _rcvNxt) < 0) return true;

            int insertAt = _rcvBuf.Count;
            bool repeat = false;
            for (int i = _rcvBuf.Count - 1; i >= 0; i--)
            {
                if (_rcvBuf[i].Sn == sn) { repeat = true; break; }
                if (Diff(sn, _rcvBuf[i].Sn) > 0) { insertAt = i + 1; break; }
                insertAt = i;
            }

            if (!repeat) _rcvBuf.Insert(insertAt, newseg);
            MoveRcvBufToQueue();
            return repeat;
        }

        // ── Wire input ───────────────────────────────────────────────────────────

        public int Input(byte[] data, int dataOffset, int dataLength, bool ackNoDelay)
        {
            uint prevUna = _sndUna;
            if (dataLength < Overhead) return -1;

            uint latest = 0;
            bool updateRtt = false;
            bool flushSegments = false;
            int pos = dataOffset;
            int end = dataOffset + dataLength;

            while (true)
            {
                if (end - pos < Overhead) break;

                uint conv = ReadUInt32LE(data, pos);
                byte cmd = data[pos + 4];
                byte frg = data[pos + 5];
                ushort wnd = ReadUInt16LE(data, pos + 6);
                uint ts = ReadUInt32LE(data, pos + 8);
                uint sn = ReadUInt32LE(data, pos + 12);
                uint una = ReadUInt32LE(data, pos + 16);
                uint length = ReadUInt32LE(data, pos + 20);
                pos += Overhead;

                if (conv != _conv) return -1;
                if ((uint)(end - pos) < length) return -2;
                if (cmd != CmdPush && cmd != CmdAck && cmd != CmdWask && cmd != CmdWins) return -3;

                _rmtWnd = wnd;
                if (ParseUna(una) > 0) flushSegments = true;
                ShrinkBuf();

                switch (cmd)
                {
                    case CmdAck:
                        ParseAck(sn);
                        if (ParseFastack(sn, ts)) flushSegments = true;
                        updateRtt = true;
                        latest = ts;
                        break;

                    case CmdPush:
                        if (Diff(sn, _rcvNxt + _rcvWnd) < 0)
                        {
                            _acklist.Add(new AckItem(sn, ts));
                            if (Diff(sn, _rcvNxt) >= 0)
                            {
                                var segData = new byte[(int)length];
                                Buffer.BlockCopy(data, pos, segData, 0, (int)length);
                                ParseData(new Segment
                                {
                                    Conv = conv, Cmd = cmd, Frg = frg, Wnd = wnd,
                                    Ts = ts, Sn = sn, Una = una,
                                    Data = segData
                                });
                            }
                        }
                        break;

                    case CmdWask:
                        _probe |= AskTell;
                        break;

                    case CmdWins:
                        break;
                }

                pos += (int)length;
            }

            if (updateRtt)
            {
                uint current = CurrentMs();
                if (Diff(current, latest) >= 0) UpdateAck(Diff(current, latest));
            }

            if (_nocwnd == 0 && Diff(_sndUna, prevUna) > 0 && _cwnd < _rmtWnd)
            {
                uint mss = _mss;
                if (_cwnd < _ssthresh)
                {
                    _cwnd++;
                    _incr += mss;
                }
                else
                {
                    if (_incr < mss) _incr = mss;
                    _incr += (mss * mss) / _incr + (mss / 16);
                    if ((_cwnd + 1) * mss <= _incr) _cwnd = mss > 0 ? (_incr + mss - 1) / mss : _incr + mss - 1;
                }
                if (_cwnd > _rmtWnd)
                {
                    _cwnd = _rmtWnd;
                    _incr = _rmtWnd * mss;
                }
            }

            if (flushSegments) Flush(FlushType.Full);
            else if (_acklist.Count >= (int)(_mtu / Overhead)) Flush(FlushType.AckOnly);
            else if (ackNoDelay && _acklist.Count > 0) Flush(FlushType.AckOnly);

            return 0;
        }

        private ushort WndUnused() =>
            _rcvQueue.Count < (int)_rcvWnd ? (ushort)((int)_rcvWnd - _rcvQueue.Count) : (ushort)0;

        // ── Flush ────────────────────────────────────────────────────────────────

        public uint Flush() => Flush(FlushType.Full);

        private uint Flush(FlushType flushType)
        {
            var seg = new Segment
            {
                Conv = _conv,
                Cmd = CmdAck,
                Wnd = WndUnused(),
                Una = _rcvNxt
            };

            var buffer = _buffer;
            int used = 0;

            var pending = new List<byte[]>(2);

            void MakeSpace(int space)
            {
                if (used + space > (int)_mtu)
                {
                    var datagram = new byte[used];
                    Buffer.BlockCopy(buffer, 0, datagram, 0, used);
                    pending.Add(datagram);
                    used = 0;
                }
            }

            // Phase 1: pending ACKs.
            if (flushType == FlushType.AckOnly || flushType == FlushType.Full)
            {
                for (int i = 0; i < _acklist.Count; i++)
                {
                    MakeSpace(Overhead);
                    if (Diff(_acklist[i].Sn, _rcvNxt) >= 0 || i == _acklist.Count - 1)
                    {
                        seg.Sn = _acklist[i].Sn;
                        seg.Ts = _acklist[i].Ts;
                        seg.EncodeHeader(buffer, used);
                        used += Overhead;
                    }
                }
                _acklist.Clear();
            }

            // Phase 2: window probes.
            if (_rmtWnd == 0)
            {
                uint now = CurrentMs();
                if (_probeWait == 0)
                {
                    _probeWait = ProbeInit;
                    _tsProbe = now + _probeWait;
                }
                else if (Diff(now, _tsProbe) >= 0)
                {
                    if (_probeWait < ProbeInit) _probeWait = ProbeInit;
                    _probeWait += _probeWait / 2;
                    if (_probeWait > ProbeLimit) _probeWait = ProbeLimit;
                    _tsProbe = now + _probeWait;
                    _probe |= AskSend;
                }
            }
            else
            {
                _tsProbe = 0;
                _probeWait = 0;
            }

            // Phase 3: probe commands.
            if ((_probe & AskSend) != 0)
            {
                seg.Cmd = CmdWask;
                MakeSpace(Overhead);
                seg.EncodeHeader(buffer, used);
                used += Overhead;
            }
            if ((_probe & AskTell) != 0)
            {
                seg.Cmd = CmdWins;
                MakeSpace(Overhead);
                seg.EncodeHeader(buffer, used);
                used += Overhead;
            }
            _probe = 0;

            // Phase 4: slide the send window.
            uint cwnd = Math.Min(_sndWnd, _rmtWnd);
            if (_nocwnd == 0) cwnd = Math.Min(_cwnd, cwnd);

            int newSegsCount = 0;
            while (Diff(_sndNxt, _sndUna + cwnd) < 0 && _sndQueue.Count > 0)
            {
                var newseg = _sndQueue[0];
                _sndQueue.RemoveAt(0);
                newseg.Conv = _conv;
                newseg.Cmd = CmdPush;
                newseg.Sn = _sndNxt;
                _sndBuf.Add(newseg);
                _sndNxt++;
                newSegsCount++;
            }

            uint resent = _fastresend > 0 ? (uint)_fastresend : 0xFFFFFFFFu;

            // Phase 5: (re)transmit from the send buffer.
            uint current = CurrentMs();
            ulong change = 0, lostSegs = 0;
            uint nextUpdate = _interval;

            if (flushType == FlushType.Full)
            {
                foreach (var segment in _sndBuf)
                {
                    if (segment.Acked) continue;

                    bool needsend = false;
                    if (segment.Xmit == 0)
                    {
                        needsend = true;
                        segment.Rto = _rxRto;
                        segment.Resendts = current + segment.Rto;
                    }
                    else if (segment.Fastack >= resent && segment.Fastack != 0xFFFFFFFFu)
                    {
                        needsend = true;
                        segment.Fastack = 0xFFFFFFFFu;
                        segment.Rto = _rxRto;
                        segment.Resendts = current + segment.Rto;
                        change++;
                    }
                    else if (segment.Fastack > 0 && segment.Fastack != 0xFFFFFFFFu && newSegsCount == 0)
                    {
                        needsend = true;
                        segment.Fastack = 0xFFFFFFFFu;
                        segment.Rto = _rxRto;
                        segment.Resendts = current + segment.Rto;
                        change++;
                    }
                    else if (Diff(current, segment.Resendts) >= 0)
                    {
                        needsend = true;
                        segment.Rto += _nodelay == 0 ? _rxRto : _rxRto / 2;
                        segment.Fastack = 0;
                        segment.Resendts = current + segment.Rto;
                        lostSegs++;
                    }

                    if (needsend)
                    {
                        current = CurrentMs();
                        segment.Xmit++;
                        segment.Ts = current;
                        segment.Wnd = seg.Wnd;
                        segment.Una = seg.Una;

                        MakeSpace(Overhead + segment.Data.Length);
                        segment.EncodeHeader(buffer, used);
                        used += Overhead;
                        Buffer.BlockCopy(segment.Data, 0, buffer, used, segment.Data.Length);
                        used += segment.Data.Length;

                        if (segment.Xmit >= _deadLink) _state = 0xFFFFFFFFu;
                    }

                    int rto = Diff(segment.Resendts, current);
                    if (rto > 0 && (uint)rto < nextUpdate) nextUpdate = (uint)rto;
                }
            }

            if (used > 0)
            {
                var datagram = new byte[used];
                Buffer.BlockCopy(buffer, 0, datagram, 0, used);
                pending.Add(datagram);
            }

            // Phase 6: congestion window response to loss.
            if (_nocwnd == 0)
            {
                if (change > 0)
                {
                    uint inflight = _sndNxt - _sndUna;
                    _ssthresh = Math.Max(inflight / 2, ThreshMin);
                    _cwnd = _ssthresh + resent;
                    _incr = _cwnd * _mss;
                }
                if (lostSegs > 0)
                {
                    _ssthresh = Math.Max(cwnd / 2, ThreshMin);
                    _cwnd = 1;
                    _incr = _mss;
                }
                if (_cwnd < 1)
                {
                    _cwnd = 1;
                    _incr = _mss;
                }
            }

            foreach (var datagram in pending) _output(datagram, datagram.Length);

            return nextUpdate;
        }

        public void Update()
        {
            uint current = CurrentMs();
            if (_updated == 0)
            {
                _updated = 1;
                _tsFlush = current;
            }

            int slap = Diff(current, _tsFlush);
            if (slap >= 10000 || slap < -10000)
            {
                _tsFlush = current;
                slap = 0;
            }

            if (slap >= 0)
            {
                _tsFlush += _interval;
                if (Diff(current, _tsFlush) >= 0) _tsFlush = current + _interval;
                Flush(FlushType.Full);
            }
        }

        // ── Binary helpers (replace BinaryPrimitives for byte[] compatibility) ──

        private static uint ReadUInt32LE(byte[] buf, int offset)
        {
            return (uint)buf[offset]
                 | ((uint)buf[offset + 1] << 8)
                 | ((uint)buf[offset + 2] << 16)
                 | ((uint)buf[offset + 3] << 24);
        }

        private static ushort ReadUInt16LE(byte[] buf, int offset)
        {
            return (ushort)(buf[offset] | (buf[offset + 1] << 8));
        }

        private static void WriteUInt32LE(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteUInt16LE(byte[] buf, int offset, ushort value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }
    }
}
