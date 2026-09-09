using System;
using System.Text;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using Cuvara.Netcode.World;
using UnityEngine;
using UnityEngine.UIElements;
using SimAction = Shared.GameLogic.Components.EntityAction;

namespace Cuvara.Netcode.Samples.FacingAndVersion
{
    /// <summary>
    /// Demonstrates the two things the wire gained together: a protocol version that
    /// refuses a mismatched peer with a NAMED REASON, and per-entity facing that a
    /// renderer can actually turn a character with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No server and no network</b>, deliberately — the same shape as
    /// <c>InterpolationProbe</c> and <c>ClockSyncProbe</c>. Frames are hand-built exactly
    /// the way the servers write them and pushed through the REAL
    /// <see cref="JsonWireCodec"/>, <see cref="Cuvara.Netcode.Snapshot.SnapshotResolver"/>,
    /// <see cref="WorldState"/> (which merges through the shared
    /// <c>Shared.GameLogic.Systems.SnapshotMerger</c>) and
    /// <see cref="WorldViewBinder"/>. So what is on screen is the production decode path,
    /// not a mock of it — but it can be run and re-run with no backend, which is what
    /// makes it a usable acceptance check rather than an integration test.
    /// </para>
    /// <para>
    /// <b>What to look for.</b> Three capsules orbit a ring. Each one's nose points along
    /// its direction of travel, and that direction arrives only in
    /// <c>facing_brad</c> — nothing in the scene derives it from movement. Press
    /// <i>Stop sending facing</i> and the server stops populating the field: the capsules
    /// keep moving and STOP TURNING, holding their last heading rather than snapping to
    /// east. That held heading is the whole "zero means not sent" rule, visible.
    /// </para>
    /// <para>
    /// The handshake buttons build a real <see cref="JoinTokenResponse"/> the way a server
    /// would and run it through the client's own version check. The mismatch button
    /// produces the refusal a real server sends — reason
    /// <c>protocol_version_mismatch</c> — and the third button produces what an OLD
    /// server sends, which is nothing at all, and is admitted on trust with a warning.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(UIDocument))]
    public sealed class FacingAndVersion : MonoBehaviour
    {
        private const string Tag = "[DOTSNet]";

        [Header("Synthetic world")]
        [SerializeField] private int entityCount = 3;
        [SerializeField] private float orbitRadius = 4f;
        [SerializeField] private float orbitSecondsPerTurn = 6f;

        [Tooltip("Snapshots per second. The server's world rate, not its tick rate.")]
        [SerializeField] private float snapshotHz = 15f;

        private readonly JsonWireCodec _codec = new JsonWireCodec();
        private readonly SnapshotResolver _resolver = new SnapshotResolver();
        private WorldState _world;
        private WorldViewBinder _binder;
        private GameObjectEntityView _view;

        private Label _versionLine;
        private Label _refusalLine;
        private Label _facingLine;
        private Label _actionLine;
        private Label _encodingLine;
        private Label _verdict;
        private Button _joinMatch;
        private Button _joinMismatch;
        private Button _joinUnversioned;
        private Button _toggleFacing;
        private Button _reverse;

        private bool _sendFacing = true;
        private float _direction = 1f;
        private double _nextSnapshotAt;
        private ulong _tick;
        private uint _lastFacingBrad;

        private void Awake()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            _versionLine = Require<Label>(root, "version-line");
            _refusalLine = Require<Label>(root, "refusal-line");
            _facingLine = Require<Label>(root, "facing-line");
            _actionLine = Require<Label>(root, "action-line");
            _encodingLine = Require<Label>(root, "encoding-line");
            _verdict = Require<Label>(root, "verdict");
            _joinMatch = Require<Button>(root, "join-match");
            _joinMismatch = Require<Button>(root, "join-mismatch");
            _joinUnversioned = Require<Button>(root, "join-unversioned");
            _toggleFacing = Require<Button>(root, "toggle-facing");
            _reverse = Require<Button>(root, "reverse");

            _joinMatch.clicked += () => Join(WireProtocolVersion.Current);
            _joinMismatch.clicked += () => Join(WireProtocolVersion.Current + 1);
            _joinUnversioned.clicked += () => Join(WireProtocolVersion.Unversioned);
            _toggleFacing.clicked += ToggleFacing;
            _reverse.clicked += () => _direction = -_direction;

            _versionLine.text = $"This client speaks wire protocol version {WireProtocolVersion.Current}.";
            _refusalLine.text = "press a join button";

            _world = new WorldState();
            _view = new GameObjectEntityView();
            _binder = new WorldViewBinder(_view);

            PlaceCamera();
        }

        /// <summary>
        /// Frames the ring from above and slightly to the side. Done from script rather
        /// than baked into the scene so the framing follows <c>orbitRadius</c> if it is
        /// changed in the inspector.
        /// </summary>
        private void PlaceCamera()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            float d = orbitRadius * 2.6f;
            cam.transform.position = new Vector3(0f, d, -d * 0.75f);
            cam.transform.rotation = Quaternion.Euler(50f, 0f, 0f);
        }

        private void Update()
        {
            double now = Time.timeAsDouble;
            if (now >= _nextSnapshotAt)
            {
                _nextSnapshotAt = now + 1.0 / Mathf.Max(1f, snapshotHz);
                PushSnapshot(now);
            }

            // Between snapshots the binder still drives the view, exactly as it does
            // against a real server.
            _binder.AdvanceFrame(Time.deltaTime);
        }

        /// <summary>
        /// Build one snapshot the way the servers build it, encode it, and push it
        /// through the client's real decode path.
        /// </summary>
        private void PushSnapshot(double now)
        {
            _tick++;

            var sb = new StringBuilder();
            sb.Append("{\"tick\":").Append(_tick).Append(",\"full\":true,\"entities\":[");

            for (int i = 0; i < entityCount; i++)
            {
                // Position on the ring.
                float phase = (float)(now / Mathf.Max(0.01f, orbitSecondsPerTurn)) * _direction;
                float theta = (phase + i / (float)Mathf.Max(1, entityCount)) * Mathf.PI * 2f;
                float x = Mathf.Cos(theta) * orbitRadius;
                float y = Mathf.Sin(theta) * orbitRadius;

                // The tangent to a circle is the heading. This is the ONLY place a
                // direction is computed; nothing downstream re-derives it, which is what
                // makes the field itself observable.
                float headingRad = theta + Mathf.PI * 0.5f * _direction;

                // Encoded exactly as the server encodes it: biased 16-bit binary radians,
                // where 0 is reserved for "not sent". The client has no encoder (it never
                // produces a facing), so the bias is applied here by hand, which is also
                // a readable statement of the format.
                uint brad = 0;
                if (_sendFacing)
                {
                    float wrapped = Mathf.Repeat(headingRad, Mathf.PI * 2f);
                    int step = Mathf.RoundToInt(wrapped / (Mathf.PI * 2f) * FacingCodec.BradSteps);
                    if (step >= FacingCodec.BradSteps) step = 0;
                    brad = (uint)step + 1;
                    if (i == 0) _lastFacingBrad = brad;
                }

                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":\"e").Append(i)
                  .Append("\",\"type\":\"player\",\"x\":").Append(x.ToString("R"))
                  .Append(",\"y\":").Append(y.ToString("R"))
                  .Append(",\"hp\":100,\"max_hp\":100,\"speed\":4");

                // Omitted entirely when not sent - the same statement Protobuf makes by
                // eliding the field, and the reason zero had to be reserved.
                if (brad != 0) sb.Append(",\"facing_brad\":").Append(brad);
                sb.Append(",\"action\":").Append((int)SimAction.Moving);
                sb.Append('}');
            }

            sb.Append("],\"removed\":[]}");

            string payload = sb.ToString();
            byte[] frame = System.Text.Encoding.UTF8.GetBytes(
                "{\"type\":" + (int)MsgType.Snapshot + ",\"payload\":" + payload + "}");

            // The real codec, the real resolver, the real merger, the real binder.
            // Resolution comes before the merge because handles are a wire concern the
            // merger deliberately knows nothing about (see the API.md merge algorithm);
            // this stream never interns, so the resolver is a pass-through here — it is
            // in the chain so the sample exercises the same path a Protobuf connection
            // would.
            WireFrame decoded = _codec.DecodeBody(frame);
            if (decoded.Payload is SnapshotMessage snapshot &&
                _resolver.TryResolve(snapshot, out ResolvedSnapshot resolved))
            {
                _world.Apply(resolved);
                _binder.Tick(_world, localId: null);
            }

            UpdateFacingReadout();
        }

        private void UpdateFacingReadout()
        {
            if (FacingCodec.TryToRadians(_lastFacingBrad, out float radians) && _sendFacing)
            {
                _facingLine.text =
                    $"e0 facing_brad = {_lastFacingBrad}  ->  {radians * Mathf.Rad2Deg:F1} deg CCW from +X";
                _encodingLine.text =
                    $"wire 0 is reserved; {FacingCodec.BradSteps} steps, {360f / FacingCodec.BradSteps:F4} deg each";
            }
            else
            {
                _facingLine.text = "facing_brad = 0 (not sent) - the view HOLDS its last heading";
                _encodingLine.text = "a float would have made this indistinguishable from 'facing east'";
            }

            _actionLine.text = $"action = {SimAction.Moving} ({(int)SimAction.Moving})  -  idle is 1, never 0";
        }

        private void ToggleFacing()
        {
            _sendFacing = !_sendFacing;
            _toggleFacing.text = _sendFacing ? "Stop sending facing" : "Send facing again";

            if (!_sendFacing)
            {
                _lastFacingBrad = 0;
                Verdict("facing withheld - capsules keep moving and stop turning, holding the last heading", ok: true);
            }
            else
            {
                Verdict("facing restored", ok: true);
            }
        }

        /// <summary>
        /// Run a server's join response through the client's own version rule and report
        /// what a real client would do with it.
        /// </summary>
        private void Join(uint serverVersion)
        {
            // Exactly what a server puts on the wire: a matching or unversioned server
            // accepts, a mismatched one refuses with the named reason and still states
            // its own version so the client learns which build it needed.
            bool serverAccepts =
                serverVersion == WireProtocolVersion.Current ||
                serverVersion == WireProtocolVersion.Unversioned;

            var response = serverAccepts
                ? new JoinTokenResponse
                {
                    Ok = true, UserId = "u-demo", TickRate = 60, ProtocolVersion = serverVersion,
                }
                : new JoinTokenResponse
                {
                    Ok = false,
                    Error = KickReasons.ProtocolVersionMismatch,
                    ProtocolVersion = serverVersion,
                };

            if (!response.Ok)
            {
                // The point of the whole mechanism: a NAMED reason, not a parse error and
                // not a silent close. And it is permanent - retrying cannot make this
                // client a different build, so the reconnect budget must not be spent.
                bool permanent = ReconnectPolicy.IsPermanentServerError(response.Error);
                _refusalLine.text =
                    $"REFUSED  error=\"{response.Error}\"  server={response.ProtocolVersion} " +
                    $"client={WireProtocolVersion.Current}  retry={(permanent ? "never" : "yes")}";
                Verdict("refused with a named reason, before a single snapshot was parsed", ok: true);
                Debug.Log($"{Tag} join refused: {response.Error} " +
                          $"(server {response.ProtocolVersion}, client {WireProtocolVersion.Current})");
                return;
            }

            if (WireProtocolVersion.IsUnversioned(response.ProtocolVersion))
            {
                _refusalLine.text =
                    "ADMITTED, but the server advertised NO version - agreement is unverified";
                Verdict("an old server is admitted on trust, and says so; it is never silent", ok: true);
                Debug.LogWarning($"{Tag} server did not advertise a wire protocol version");
                return;
            }

            _refusalLine.text = $"ADMITTED  server={response.ProtocolVersion} tick_rate={response.TickRate}";
            Verdict("versions agree", ok: true);
        }

        private void Verdict(string text, bool ok)
        {
            _verdict.text = text;
            _verdict.RemoveFromClassList("cuvara-probe__verdict--ok");
            _verdict.RemoveFromClassList("cuvara-probe__verdict--bad");
            _verdict.AddToClassList(ok ? "cuvara-probe__verdict--ok" : "cuvara-probe__verdict--bad");
        }

        /// <summary>
        /// Fetch a named element or throw. A missing name is a broken UXML, and a
        /// NullReferenceException three frames later names the wrong thing.
        /// </summary>
        private static T Require<T>(VisualElement root, string name) where T : VisualElement
        {
            var found = root.Q<T>(name);
            if (found == null)
            {
                throw new InvalidOperationException(
                    $"FacingAndVersionView.uxml has no {typeof(T).Name} named '{name}'");
            }

            return found;
        }
    }
}
