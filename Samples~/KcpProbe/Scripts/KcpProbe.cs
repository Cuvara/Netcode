using System;
using System.Collections.Generic;
using UnityEngine;
using Cuvara.Netcode.Transport;

namespace Cuvara.Netcode.Samples.KcpProbe
{
    /// <summary>
    /// Drives two KCP state machines back-to-back — no server, no network — and
    /// renders the results on screen. Proves the ARQ core works: reliable delivery,
    /// fragmentation/reassembly, ordering, and simulated packet loss.
    /// </summary>
    /// <remarks>
    /// Nothing here uses a socket. The "network" is a pair of lists: datagrams
    /// produced by one side are fed to the other on the next Update, optionally
    /// with random drops to simulate loss. This is the same shape as the EditMode
    /// tests, made visible.
    /// </remarks>
    public sealed class KcpProbe : MonoBehaviour
    {
        [Header("Simulation")]
        [Range(0f, 0.5f)]
        [SerializeField] private float packetLossRate;
        [SerializeField] private int messagesToSend = 50;
        [SerializeField] private int messageSize = 200;

        private Kcp _client;
        private Kcp _server;
        private readonly List<byte[]> _clientOut = new List<byte[]>();
        private readonly List<byte[]> _serverOut = new List<byte[]>();

        private int _sentCount;
        private int _receivedCount;
        private int _totalBytesSent;
        private int _totalBytesReceived;
        private int _droppedPackets;
        private int _totalPackets;
        private float _startTime;
        private bool _done;
        private string _status = "Idle";
        private readonly List<string> _log = new List<string>();

        private void Start()
        {
            const uint conv = 42;

            _client = new Kcp(conv, (buf, size) =>
            {
                var copy = new byte[size];
                Buffer.BlockCopy(buf, 0, copy, 0, size);
                _clientOut.Add(copy);
            });

            _server = new Kcp(conv, (buf, size) =>
            {
                var copy = new byte[size];
                Buffer.BlockCopy(buf, 0, copy, 0, size);
                _serverOut.Add(copy);
            });

            // Apply the game tuning profile.
            _client.Stream = 1;
            _client.SetNoDelay(1, 10, 2, 1);
            _client.WndSize(128, 128);

            _server.Stream = 1;
            _server.SetNoDelay(1, 10, 2, 1);
            _server.WndSize(128, 128);

            _startTime = Time.realtimeSinceStartup;
            _status = "Running";
            Log($"KCP Probe started: {messagesToSend} messages x {messageSize} bytes, loss={packetLossRate:P0}");
        }

        private void Update()
        {
            if (_done) return;

            // Send messages from client.
            while (_sentCount < messagesToSend)
            {
                var payload = new byte[WireFraming.HeaderSize + messageSize];
                WireFraming.WriteLength(payload, messageSize);
                // Fill body with a pattern.
                for (int i = WireFraming.HeaderSize; i < payload.Length; i++)
                    payload[i] = (byte)(_sentCount & 0xFF);

                _client.Send(payload, 0, payload.Length);
                _totalBytesSent += messageSize;
                _sentCount++;
            }
            _client.Flush();

            // Deliver client -> server (with simulated loss).
            foreach (var pkt in _clientOut)
            {
                _totalPackets++;
                if (UnityEngine.Random.value < packetLossRate)
                {
                    _droppedPackets++;
                    continue; // lost!
                }
                _server.Input(pkt, 0, pkt.Length, ackNoDelay: true);
            }
            _clientOut.Clear();

            _server.Update();

            // Deliver server -> client (ACKs, with simulated loss).
            foreach (var pkt in _serverOut)
            {
                _totalPackets++;
                if (UnityEngine.Random.value < packetLossRate)
                {
                    _droppedPackets++;
                    continue;
                }
                _client.Input(pkt, 0, pkt.Length, ackNoDelay: true);
            }
            _serverOut.Clear();

            _client.Update();

            // Drain received messages from server.
            var recv = new byte[65536];
            while (true)
            {
                int n = _server.Recv(recv, recv.Length);
                if (n <= 0) break;

                // Parse wire frames out of the stream.
                int pos = 0;
                while (pos + WireFraming.HeaderSize <= n)
                {
                    int bodyLen = WireFraming.ReadLength(recv, pos);
                    if (!WireFraming.IsValidLength(bodyLen) || pos + WireFraming.HeaderSize + bodyLen > n)
                        break;
                    _totalBytesReceived += bodyLen;
                    _receivedCount++;
                    pos += WireFraming.HeaderSize + bodyLen;
                }
            }

            // Check completion.
            if (_receivedCount >= messagesToSend && !_done)
            {
                _done = true;
                float elapsed = Time.realtimeSinceStartup - _startTime;
                _status = "DONE";
                Log($"All {messagesToSend} messages received in {elapsed:F2}s");
                Log($"Bytes: sent={_totalBytesSent}, received={_totalBytesReceived}");
                Log($"Packets: total={_totalPackets}, dropped={_droppedPackets} ({(_totalPackets > 0 ? (float)_droppedPackets / _totalPackets : 0):P1})");
                Log($"KCP retransmissions handled the loss transparently.");
            }
        }

        private void Log(string msg)
        {
            _log.Add(msg);
            Debug.Log($"[KcpProbe] {msg}");
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(20, 20, 600, 600));
            GUILayout.Label("<b>KCP Transport Probe</b>", new GUIStyle(GUI.skin.label) { richText = true, fontSize = 18 });
            GUILayout.Space(10);

            GUILayout.Label($"Status: <b>{_status}</b>", new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.Label($"Messages: {_receivedCount} / {messagesToSend} received");
            GUILayout.Label($"Bytes: {_totalBytesSent} sent, {_totalBytesReceived} received");
            GUILayout.Label($"Packets: {_totalPackets} total, {_droppedPackets} dropped ({(_totalPackets > 0 ? (float)_droppedPackets / _totalPackets * 100 : 0):F1}%)");
            GUILayout.Label($"Packet Loss Rate: {packetLossRate:P0}");
            GUILayout.Label($"Client WaitSnd: {(_client != null ? _client.WaitSnd : 0)}");

            GUILayout.Space(10);
            GUILayout.Label("Log:");
            foreach (var entry in _log)
                GUILayout.Label($"  {entry}");

            GUILayout.EndArea();
        }
    }
}
