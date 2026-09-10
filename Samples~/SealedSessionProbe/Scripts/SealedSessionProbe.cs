using System;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;
using Cuvara.Netcode.Crypto;

namespace Cuvara.Netcode.Samples.SealedSessionProbe
{
    /// <summary>
    /// Runs ADR-22's transport crypto between two peers in one process and shows every frame
    /// before and after it is sealed — no server, no network, no sockets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this scene exists.</b> "The link is encrypted" is the kind of claim that is
    /// believed rather than checked, and stays believed after it stops being true. Here the
    /// plaintext the game wrote and the bytes a capture would show sit one above the other,
    /// updated every frame, so the claim is looked at rather than read about.
    /// </para>
    /// <para>
    /// <b>The buttons are the substance.</b> Each is an attack the design says must fail, and
    /// each reports what actually happened rather than what was intended. A refusal that stops
    /// working shows up here as a green verdict turning red, which is the failure mode a unit
    /// test catches only if someone remembered to write it.
    /// </para>
    /// <para>
    /// What this scene does NOT prove: that the shipped client uses any of this. The sealed
    /// framing is not yet wired into <c>GameSessionClient</c> — that is the gateway/game-hop
    /// work tracked separately — and pretending otherwise is exactly the drift this scene is
    /// meant to resist. It proves the primitives and the refusals, on this runtime.
    /// </para>
    /// </remarks>
    public sealed class SealedSessionProbe : MonoBehaviour
    {
        [Header("Synthetic session")]
        [Tooltip("The join-token secret the two honest peers share. Never a real secret: this scene has no server and issues no tokens.")]
        [SerializeField] private string joinTokenSecret = "probe-join-secret";

        [Tooltip("The join token's jti. Salts the key schedule, so two sessions with different jti share nothing.")]
        [SerializeField] private string jti = "probe-jti-0001";

        [Tooltip("Frames sealed per second, roughly the client's input cadence.")]
        [Range(1f, 60f)]
        [SerializeField] private float framesPerSecond = 20f;

        // --- the two honest peers -------------------------------------------------------
        private SealedKeyPair _client;
        private SealedKeyPair _server;
        private SealedSession _sending;    // client -> server, client's end
        private SealedSession _receiving;  // client -> server, server's end
        private string _c2sKeyHex = string.Empty;
        private string _s2cKeyHex = string.Empty;
        private bool _handshakeOk;

        // --- traffic --------------------------------------------------------------------
        private float _accumulator;
        private int _tick;
        private int _sealed;
        private int _opened;
        private int _refusedTag;
        private int _refusedReplay;
        private int _refusedCleartext;
        private byte[] _lastFrame = Array.Empty<byte>();
        private string _lastPlain = string.Empty;

        // --- attack bookkeeping ---------------------------------------------------------
        private bool _tamperNext;
        private string _lastAttack = "none pressed yet";
        private int _attacksRun;
        private int _attacksRefused;

        // --- UI -------------------------------------------------------------------------
        private Label _handshakeLine;
        private Label _keysLine;
        private Label _plainLine;
        private Label _cipherLine;
        private Label _overheadLine;
        private Label _trafficLine;
        private Label _refusedLine;
        private Label _lastAttackLine;
        private Label _verdict;
        private Toggle _pause;

        private void OnEnable()
        {
            var document = GetComponent<UIDocument>();
            if (document == null || document.rootVisualElement == null)
            {
                Debug.LogError("SealedSessionProbe needs a UIDocument with SealedSessionProbeView.uxml assigned.");
                enabled = false;
                return;
            }

            VisualElement root = document.rootVisualElement;

            _handshakeLine = root.Q<Label>("handshake-line");
            _keysLine = root.Q<Label>("keys-line");
            _plainLine = root.Q<Label>("plain-line");
            _cipherLine = root.Q<Label>("cipher-line");
            _overheadLine = root.Q<Label>("overhead-line");
            _trafficLine = root.Q<Label>("traffic-line");
            _refusedLine = root.Q<Label>("refused-line");
            _lastAttackLine = root.Q<Label>("last-attack");
            _verdict = root.Q<Label>("verdict");
            _pause = root.Q<Toggle>("pause");

            root.Q<Button>("tamper").clicked += AttackTamperInFlight;
            root.Q<Button>("replay").clicked += AttackReplayLastFrame;
            root.Q<Button>("forged-header").clicked += AttackForgedHeader;
            root.Q<Button>("cleartext").clicked += AttackSendCleartext;
            root.Q<Button>("wrong-secret").clicked += AttackWrongJoinSecret;
            root.Q<Button>("mitm").clicked += AttackSubstitutedKey;
            root.Q<Button>("rehandshake").clicked += Handshake;

            Handshake();
        }

        /// <summary>
        /// Run the real exchange: fresh ephemerals, X25519, a transcript both peers sign with
        /// join-token-derived material, then HKDF to the two one-direction keys.
        /// </summary>
        private void Handshake()
        {
            _client = SealedKeyPair.Generate();
            _server = SealedKeyPair.Generate();

            byte[] clientSecret;
            byte[] serverSecret;
            _handshakeOk = _client.TryAgree(_server.Public, out clientSecret)
                           && _server.TryAgree(_client.Public, out serverSecret)
                           && Hex(clientSecret) == Hex(serverSecret);

            if (!_handshakeOk)
            {
                _handshakeLine.text = "X25519 agreement FAILED — the two peers did not reach the same secret.";
                return;
            }

            byte[] transcript = SealedHandshake.Transcript(jti, _client.Public, _server.Public);

            // Both peers derive the binding independently and each checks the other's. This is
            // the proof-of-possession: the token itself is readable on the wire and proves
            // nothing, whereas this MAC cannot be produced without the join-token secret.
            var clientSigner = new SealedTranscriptSigner(joinTokenSecret, jti);
            var serverSigner = new SealedTranscriptSigner(joinTokenSecret, jti);
            bool bound = serverSigner.Verify(transcript, clientSigner.Sign(transcript));

            byte[] c2s;
            byte[] s2c;
            SealedCrypto.DeriveDirectionKeys(clientSecret, transcript, out c2s, out s2c);

            _c2sKeyHex = Hex(c2s);
            _s2cKeyHex = Hex(s2c);

            // Both ends of ONE direction. The server's own send direction uses s2c and is not
            // simulated here — one direction is enough to show the frame, and mixing them in a
            // single readout would obscure which key produced which bytes.
            _sending = new SealedSession(new SealedAead(c2s), new StrictMonotonicSequence());
            _receiving = new SealedSession(new SealedAead(c2s), new StrictMonotonicSequence());

            _handshakeOk = bound;
            _handshakeLine.text = bound
                ? $"jti \"{jti}\" — ephemerals agreed, transcript bound ({transcript.Length} B), keys derived."
                : "Transcript binding FAILED — the peers disagree about the exchange.";

            _tick = 0;
            _sealed = 0;
            _opened = 0;
            _refusedTag = 0;
            _refusedReplay = 0;
            _refusedCleartext = 0;
            _lastFrame = Array.Empty<byte>();
            _lastPlain = string.Empty;
            _tamperNext = false;

            Render();
        }

        private void Update()
        {
            if (!_handshakeOk || (_pause != null && _pause.value)) { Render(); return; }

            _accumulator += Time.deltaTime;
            float interval = 1f / Mathf.Max(1f, framesPerSecond);

            while (_accumulator >= interval)
            {
                _accumulator -= interval;
                SendOneFrame();
            }

            Render();
        }

        /// <summary>One synthetic input frame, sealed, carried, and opened.</summary>
        private void SendOneFrame()
        {
            _tick++;

            // Stands in for an encoded Envelope. Its shape does not matter to the AEAD; what
            // matters is that the reader can see it in the clear on one line and not on the
            // next.
            string plain = $"input tick={_tick} move=({Mathf.Sin(_tick * 0.08f):F2},{Mathf.Cos(_tick * 0.08f):F2})";
            byte[] frame = _sending.Seal(Encoding.UTF8.GetBytes(plain));
            _sealed++;

            byte[] onTheWire = frame;
            bool expectRefusal = false;

            if (_tamperNext)
            {
                // The attack: one bit, in the middle of the ciphertext, in flight.
                _tamperNext = false;
                expectRefusal = true;
                onTheWire = (byte[])frame.Clone();
                onTheWire[SealedFrame.HeaderSize + (onTheWire.Length - SealedFrame.HeaderSize) / 2] ^= 0x01;
            }

            byte[] plaintext;
            SealedOpenResult result = _receiving.Open(onTheWire, out plaintext);

            switch (result)
            {
                case SealedOpenResult.Ok:
                    _opened++;
                    break;
                case SealedOpenResult.Rejected:
                    _refusedTag++;
                    break;
                case SealedOpenResult.NotSealed:
                    _refusedCleartext++;
                    break;
            }

            if (expectRefusal) RecordAttack("flipped one bit of ciphertext in flight", result != SealedOpenResult.Ok, result);

            _lastFrame = frame;
            _lastPlain = plain;
        }

        // ------------------------------------------------------------------ the attacks

        private void AttackTamperInFlight()
        {
            _tamperNext = true;
            _lastAttack = "armed: the next frame will have one bit flipped in flight";
        }

        /// <summary>
        /// A byte-for-byte replay of a frame the receiver already accepted. Refused by the
        /// replay counter, not by the AEAD — the tag on a replayed frame is perfectly valid,
        /// which is exactly why a counter is needed as well as a cipher.
        /// </summary>
        private void AttackReplayLastFrame()
        {
            if (_lastFrame.Length == 0) { _lastAttack = "nothing sent yet to replay"; return; }

            byte[] ignored;
            SealedOpenResult result = _receiving.Open(_lastFrame, out ignored);
            if (result == SealedOpenResult.Rejected) _refusedReplay++;

            RecordAttack("replayed a frame the receiver had already accepted", result != SealedOpenResult.Ok, result);
        }

        /// <summary>
        /// A captured header at a high sequence with noise behind it. Refused — and, the part
        /// worth watching, the replay counter must NOT move. If the sequence were checked
        /// before the tag, this would push the counter arbitrarily high and lock out the real
        /// sender without the attacker holding any key.
        /// </summary>
        private void AttackForgedHeader()
        {
            ulong before = _receiving.HighestReceived;

            var forged = new byte[SealedFrame.HeaderSize + 48];
            SealedFrame.WriteHeader(forged, before + 100000);
            for (int i = SealedFrame.HeaderSize; i < forged.Length; i++) forged[i] = (byte)(i * 31);

            byte[] ignored;
            SealedOpenResult result = _receiving.Open(forged, out ignored);
            bool counterHeld = _receiving.HighestReceived == before;
            if (result == SealedOpenResult.Rejected) _refusedTag++;

            RecordAttack(
                counterHeld
                    ? "replayed a header at sequence +100000 with a garbage body — counter did NOT move"
                    : "replayed a header at sequence +100000 — THE COUNTER MOVED, which locks out the real sender",
                result != SealedOpenResult.Ok && counterHeld,
                result);
        }

        /// <summary>
        /// A peer sending a plain Envelope where a sealed frame is required. Reported as
        /// NotSealed rather than as a failure, because the caller's response differs: during a
        /// handshake cleartext is expected; afterwards it must end the session.
        /// </summary>
        private void AttackSendCleartext()
        {
            byte[] ignored;
            // 0x08 is a Protobuf Envelope's first byte — field 1, type.
            SealedOpenResult result = _receiving.Open(new byte[] { 0x08, 0x01, 0x12, 0x04 }, out ignored);
            if (result == SealedOpenResult.NotSealed) _refusedCleartext++;

            RecordAttack("sent a cleartext Envelope where a sealed frame is required",
                         result != SealedOpenResult.Ok, result);
        }

        /// <summary>
        /// A peer that holds the join token but not <c>JOIN_TOKEN_SECRET</c>. It can read the
        /// token — the claims are base64 — and it can run X25519. What it cannot do is produce
        /// a binding the honest side accepts.
        /// </summary>
        private void AttackWrongJoinSecret()
        {
            byte[] transcript = SealedHandshake.Transcript(jti, _client.Public, _server.Public);
            byte[] forged = new SealedTranscriptSigner(joinTokenSecret + "-wrong", jti).Sign(transcript);

            bool accepted = new SealedTranscriptSigner(joinTokenSecret, jti).Verify(transcript, forged);

            RecordAttack("offered a handshake binding computed from the wrong JOIN_TOKEN_SECRET",
                         !accepted, accepted ? SealedOpenResult.Ok : SealedOpenResult.Rejected);
        }

        /// <summary>
        /// A man in the middle who substitutes their own ephemeral key and replays the real
        /// client's binding. The binding covers both publics, so substituting one changes the
        /// transcript and the replayed tag stops verifying — which is what makes the exchange
        /// authenticated rather than merely encrypted.
        /// </summary>
        private void AttackSubstitutedKey()
        {
            var signer = new SealedTranscriptSigner(joinTokenSecret, jti);
            byte[] genuine = signer.Sign(SealedHandshake.Transcript(jti, _client.Public, _server.Public));

            SealedKeyPair attacker = SealedKeyPair.Generate();
            byte[] substituted = SealedHandshake.Transcript(jti, attacker.Public, _server.Public);

            bool accepted = signer.Verify(substituted, genuine);

            RecordAttack("substituted an ephemeral key and replayed the real client's binding",
                         !accepted, accepted ? SealedOpenResult.Ok : SealedOpenResult.Rejected);
        }

        private void RecordAttack(string what, bool refused, SealedOpenResult result)
        {
            _attacksRun++;
            if (refused) _attacksRefused++;
            _lastAttack = $"{(refused ? "REFUSED" : "ACCEPTED — THIS IS A DEFECT")}: {what} [{result}]";
        }

        // ------------------------------------------------------------------------ render

        private void Render()
        {
            if (_keysLine == null) return;

            _keysLine.text = $"c2s {Short(_c2sKeyHex)}   s2c {Short(_s2cKeyHex)}   " +
                             "— two keys, which is what makes the counter nonce safe";

            _plainLine.text = _lastPlain.Length == 0 ? "(nothing sent yet)" : _lastPlain;
            _cipherLine.text = _lastFrame.Length == 0 ? "(nothing sent yet)" : Wrap(Hex(_lastFrame));

            if (_lastFrame.Length > 0)
            {
                int payload = _lastFrame.Length - SealedFrame.HeaderSize - SealedFrame.TagSize;
                int overhead = SealedFrame.HeaderSize + SealedFrame.TagSize;
                _overheadLine.text =
                    $"{payload} B payload + {SealedFrame.HeaderSize} B header + {SealedFrame.TagSize} B tag " +
                    $"= {_lastFrame.Length} B  ({100f * overhead / payload:F1}% overhead at this size). " +
                    $"The first byte is 0x{SealedFrame.Marker:X2}, which no Envelope can begin with.";
            }

            _trafficLine.text = $"sealed {_sealed}   opened {_opened}   next sequence {_sending.NextSendSequence}   highest accepted {_receiving.HighestReceived}";
            _refusedLine.text = $"refused: {_refusedTag} bad tag or forged header, {_refusedReplay} replay, {_refusedCleartext} cleartext";
            _lastAttackLine.text = _lastAttack;

            bool allRefused = _attacksRun == 0 || _attacksRun == _attacksRefused;
            _verdict.text = _attacksRun == 0
                ? "No attack pressed yet. Traffic is flowing and every frame is opening — press a button."
                : $"{_attacksRefused} of {_attacksRun} attacks refused." +
                  (allRefused
                      ? " Every one of them, which is the claim this scene exists to check."
                      : " ONE OR MORE WAS ACCEPTED. That is a defect, not a display bug.");

            _verdict.EnableInClassList("cuvara-probe__verdict--bad", !allRefused);
        }

        private static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        private static string Short(string hex)
        {
            return hex.Length <= 16 ? hex : hex.Substring(0, 8) + "…" + hex.Substring(hex.Length - 8);
        }

        /// <summary>Break a hex string into 32-byte lines so offsets stay findable by eye.</summary>
        private static string Wrap(string hex)
        {
            var sb = new StringBuilder(hex.Length + hex.Length / 64 + 2);
            for (int i = 0; i < hex.Length; i += 64)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(hex, i, Mathf.Min(64, hex.Length - i));
            }
            return sb.ToString();
        }
    }
}
