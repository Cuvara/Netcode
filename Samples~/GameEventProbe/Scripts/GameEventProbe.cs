using System.Collections.Generic;
using System.Text;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using Cuvara.Netcode.World;
using UnityEngine;
using UnityEngine.UIElements;
using SimAction = Shared.GameLogic.Components.EntityAction;

namespace Cuvara.Netcode.Samples.GameEventProbe
{
    /// <summary>
    /// Makes the two gameplay-v2 additions visible: the edge-triggered event channel, and
    /// the <c>action_seq</c> retrigger counter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No server and no network</b>, the same shape as <c>FacingAndVersion</c> and
    /// <c>InterpolationProbe</c>. Frames are hand-built exactly as the servers write them
    /// and pushed through the REAL <see cref="JsonWireCodec"/>,
    /// <see cref="SnapshotResolver"/>, <see cref="WorldState"/> and
    /// <see cref="WorldViewBinder"/>. What is on screen is the production decode path, and
    /// it re-runs with no backend — which is what makes it an acceptance check rather than
    /// an integration test.
    /// </para>
    /// <para>
    /// <b>What to look for — the whole point of the scene is the two "stop sending" buttons.</b>
    /// </para>
    /// <para>
    /// <i>Stop sending events.</i> The attacker keeps hitting and the victim's HP keeps
    /// dropping, because HP is STATE and arrives in the snapshot either way — but the
    /// floating damage numbers stop, because a number is an OCCURRENCE and nothing in this
    /// scene derives one from an HP delta. That is the argument for the channel existing,
    /// made visible: try to reconstruct "25 damage" from the HP readout while a heal lands
    /// in the same tick, and the answer is that you cannot.
    /// </para>
    /// <para>
    /// <i>Stop sending action_seq.</i> The attacker keeps attacking and the red flash fires
    /// ONCE and then stops, while <c>action</c> stays <c>Attacking</c> on every snapshot.
    /// The flash is driven only by the counter changing; nothing watches <c>action</c> for
    /// an edge, because there is no edge in it to watch. That is the repeated-attack
    /// problem, reproduced on demand.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(UIDocument))]
    public sealed class GameEventProbe : MonoBehaviour
    {
        [Header("Synthetic world")]
        [Tooltip("Snapshots per second. The server's world rate, not its tick rate.")]
        [SerializeField] private float snapshotHz = 15f;

        [Tooltip("Snapshots between attacks. At 15 Hz, 12 is roughly one swing per 0.8 s.")]
        [SerializeField] private int snapshotsPerAttack = 12;

        [SerializeField] private int damagePerHit = 25;

        private const string AttackerId = "attacker";
        private const string VictimId = "victim";

        private readonly JsonWireCodec _codec = new JsonWireCodec();
        private readonly SnapshotResolver _resolver = new SnapshotResolver();
        private WorldState _world;
        private WorldViewBinder _binder;
        private GameObjectEntityView _view;

        private Label _eventLine;
        private Label _hpLine;
        private Label _deriveLine;
        private Label _seqLine;
        private Label _retriggerLine;
        private Label _resolverLine;
        private Label _verdict;
        private Button _toggleEvents;
        private Button _toggleSeq;
        private Button _heal;
        private Button _kill;
        private Button _unknownHandle;

        private bool _sendEvents = true;
        private bool _sendActionSeq = true;
        private bool _healNext;
        private bool _killNext;
        private bool _unknownNext;

        private double _nextSnapshotAt;
        private ulong _tick;
        private int _sinceAttack;

        private int _victimHp = 400;
        private const int VictimMaxHp = 400;

        private uint _serverActionSeq;
        private uint _lastSeenActionSeq;
        private int _retriggers;
        private float _flashUntil;

        private readonly List<string> _recentEvents = new List<string>();

        private void Awake()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            _eventLine = Require<Label>(root, "event-line");
            _hpLine = Require<Label>(root, "hp-line");
            _deriveLine = Require<Label>(root, "derive-line");
            _seqLine = Require<Label>(root, "seq-line");
            _retriggerLine = Require<Label>(root, "retrigger-line");
            _resolverLine = Require<Label>(root, "resolver-line");
            _verdict = Require<Label>(root, "verdict");
            _toggleEvents = Require<Button>(root, "toggle-events");
            _toggleSeq = Require<Button>(root, "toggle-seq");
            _heal = Require<Button>(root, "heal");
            _kill = Require<Button>(root, "kill");
            _unknownHandle = Require<Button>(root, "unknown-handle");

            _toggleEvents.clicked += ToggleEvents;
            _toggleSeq.clicked += ToggleActionSeq;
            _heal.clicked += () => _healNext = true;
            _kill.clicked += () => _killNext = true;
            _unknownHandle.clicked += () => _unknownNext = true;

            _world = new WorldState();
            _view = new GameObjectEntityView();
            _binder = new WorldViewBinder(_view);

            PlaceCamera();
            Redraw();
        }

        private void PlaceCamera()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            cam.transform.position = new Vector3(0f, 6f, -7f);
            cam.transform.rotation = Quaternion.Euler(40f, 0f, 0f);
        }

        /// <summary>
        /// One self-check, once, a few seconds in.
        /// </summary>
        /// <remarks>
        /// A scene whose script failed to bind, or whose UXML lost an element, produces a window
        /// that looks plausible and does nothing — and in a built player there is no console to
        /// notice it in. This line turns the player log into evidence: it reports the counts the
        /// scene has actually observed, so "it ran" and "it worked" stop being the same claim.
        /// </remarks>
        private void SelfCheck()
        {
            _selfChecked = true;
            Debug.Log($"[GameEventProbe] SELFCHECK tick={_tick} eventsSeen={_eventsSeen} " +
                      $"retriggers={_retriggers} victimHp={_victimHp} " +
                      $"unresolvedEventParticipants={_resolver.UnresolvedEventParticipants}");
        }

        private bool _selfChecked;
        private int _eventsSeen;

        private void Update()
        {
            double now = Time.timeAsDouble;
            if (!_selfChecked && now > 8.0) SelfCheck();
            if (now >= _nextSnapshotAt)
            {
                _nextSnapshotAt = now + 1.0 / Mathf.Max(1f, snapshotHz);
                PushSnapshot();
            }

            _binder.AdvanceFrame(Time.deltaTime);
        }

        /// <summary>
        /// Builds one snapshot the way a server builds it and pushes it through the real
        /// decode path.
        /// </summary>
        /// <remarks>
        /// The JSON encoding is used because it is readable, and because it addresses event
        /// participants by id — which is exactly what a non-interning connection does. A
        /// Protobuf connection would carry handles instead and resolve them against the same
        /// table; that path is covered by the EditMode tests rather than here, because
        /// handles are not something a scene can show.
        /// </remarks>
        private void PushSnapshot()
        {
            _tick++;
            _sinceAttack++;

            bool attacking = _sinceAttack >= Mathf.Max(1, snapshotsPerAttack);
            if (attacking)
            {
                _sinceAttack = 0;

                // The server advances the counter every time the entity ENTERS an action,
                // and Attacking is retriggerable — so a second swing moves it even though
                // the action is unchanged. That is the single rule in
                // Shared.GameLogic.Systems.ActionStateLogic, restated here by hand because
                // this scene is standing in for a server.
                _serverActionSeq = _serverActionSeq == uint.MaxValue ? 1u : _serverActionSeq + 1u;
            }

            var events = new List<string>();

            if (attacking)
            {
                int damage = _killNext ? _victimHp : damagePerHit;
                _killNext = false;

                _victimHp = Mathf.Max(0, _victimHp - damage);

                events.Add(EventJson(1, AttackerId, VictimId, damage));
                if (_victimHp == 0)
                {
                    // Not redundant with the Dead action the victim is about to carry: Dead
                    // persists as long as the corpse does, so it cannot say the death
                    // happened NOW.
                    events.Add(EventJson(3, AttackerId, VictimId, 0));
                }
            }

            if (_healNext)
            {
                _healNext = false;
                int healed = Mathf.Min(VictimMaxHp - _victimHp, damagePerHit);
                _victimHp += healed;
                events.Add(EventJson(2, VictimId, VictimId, healed));
            }

            if (_unknownNext)
            {
                _unknownNext = false;
                // A participant this client has never been told about. The resolver reports
                // it as absent and COUNTS it — it must not abort the snapshot the way an
                // unresolvable ENTITY handle does, because a missing damage number is not
                // worth a keyframe for every observer.
                events.Add(EventJson(1, "a-stranger", VictimId, 7));
            }

            bool dead = _victimHp == 0;
            var sb = new StringBuilder();
            sb.Append("{\"tick\":").Append(_tick).Append(",\"full\":true,\"entities\":[");

            AppendEntity(sb, AttackerId, -2f,
                action: attacking ? SimAction.Attacking : SimAction.Idle,
                hp: 400, maxHp: 400,
                // Sent on every mention, not only when it changes: a receiver that resolves
                // an entity expects complete state.
                actionSeq: _sendActionSeq ? _serverActionSeq : 0u);

            sb.Append(',');
            AppendEntity(sb, VictimId, 2f,
                action: dead ? SimAction.Dead : SimAction.Idle,
                hp: _victimHp, maxHp: VictimMaxHp,
                actionSeq: 0u);

            sb.Append(']');

            // Omitted entirely when empty, which is the same statement proto3 makes by
            // eliding an empty repeated field.
            if (_sendEvents && events.Count > 0)
            {
                sb.Append(",\"events\":[").Append(string.Join(",", events)).Append(']');
            }

            sb.Append('}');

            byte[] frame = Encoding.UTF8.GetBytes(
                "{\"type\":" + (int)MsgType.Snapshot + ",\"payload\":" + sb + "}");

            WireFrame decoded = _codec.DecodeBody(frame);
            if (decoded.Payload is SnapshotMessage snapshot &&
                _resolver.TryResolve(snapshot, out ResolvedSnapshot resolved))
            {
                _world.Apply(resolved);
                _binder.Tick(_world, localId: null);
                Consume(resolved);
            }

            Redraw();
        }

        private static string EventJson(int type, string sourceId, string targetId, int amount) =>
            "{\"type\":" + type +
            ",\"source_id\":\"" + sourceId + "\"" +
            ",\"target_id\":\"" + targetId + "\"" +
            ",\"amount\":" + amount + "}";

        private void AppendEntity(
            StringBuilder sb, string id, float x, SimAction action, int hp, int maxHp, uint actionSeq)
        {
            sb.Append("{\"id\":\"").Append(id)
              .Append("\",\"type\":\"player\",\"x\":").Append(x.ToString("R"))
              .Append(",\"y\":0,\"hp\":").Append(hp)
              .Append(",\"max_hp\":").Append(maxHp)
              .Append(",\"speed\":0,\"facing_brad\":1,\"action\":").Append((int)action);

            // Omitted when zero — zero is the reserved "not sent" value, so writing it would
            // assert "this sender has a counter and it is the reserved one".
            if (actionSeq != 0) sb.Append(",\"action_seq\":").Append(actionSeq);

            sb.Append('}');
        }

        /// <summary>
        /// Consumes one resolved snapshot: events ONCE, and the retrigger counter by
        /// inequality.
        /// </summary>
        private void Consume(in ResolvedSnapshot resolved)
        {
            foreach (ResolvedGameEvent e in resolved.Events)
            {
                // Unknown types are ignored rather than guessed — events are presentation,
                // so a dropped one costs a missing number and a guessed one costs a wrong.
                switch (e.Type)
                {
                    case GameEventType.Damage:
                    case GameEventType.Heal:
                    case GameEventType.Death:
                        break;
                    default:
                        continue;
                }

                string source = e.HasSource ? e.SourceId : "(not visible)";
                _eventsSeen++;
                Remember($"t{resolved.Tick} {e.Type} {source} -> {e.TargetId} amount={e.Amount}");
            }

            if (!_world.TryGet(AttackerId, out var attacker)) return;

            // INEQUALITY, never greater-than. The counter wraps at 2^32 and resets on a
            // server restart, so a greater-than test would stop retriggering for four
            // billion actions after a single wrap with nothing reporting an error.
            if (attacker.ActionSeq != 0 && attacker.ActionSeq != _lastSeenActionSeq)
            {
                _lastSeenActionSeq = attacker.ActionSeq;
                _retriggers++;
                _flashUntil = Time.time + 0.12f;
            }
        }

        private void Remember(string line)
        {
            _recentEvents.Insert(0, line);
            if (_recentEvents.Count > 4) _recentEvents.RemoveAt(_recentEvents.Count - 1);
        }

        private void ToggleEvents()
        {
            _sendEvents = !_sendEvents;
            _toggleEvents.text = _sendEvents ? "Stop sending events" : "Resume sending events";
        }

        private void ToggleActionSeq()
        {
            _sendActionSeq = !_sendActionSeq;
            _toggleSeq.text = _sendActionSeq ? "Stop sending action_seq" : "Resume sending action_seq";
        }

        private void Redraw()
        {
            _eventLine.text = _recentEvents.Count == 0
                ? "(no events received)"
                : string.Join("\n", _recentEvents);

            _hpLine.text = $"victim hp {_victimHp} / {VictimMaxHp}   (state — arrives either way)";

            _deriveLine.text = _sendEvents
                ? "Damage numbers above come ONLY from the event channel."
                : "Events off: HP still falls, numbers stop. An occurrence is not derivable from state.";

            _seqLine.text = _sendActionSeq
                ? $"attacker action=Attacking  action_seq={_serverActionSeq}"
                : "attacker action=Attacking  action_seq not sent (0)";

            _retriggerLine.text = _sendActionSeq
                ? $"retriggers seen: {_retriggers}  — one per swing"
                : $"retriggers seen: {_retriggers}  — frozen: action never changes, so there is no edge";

            _resolverLine.text =
                $"unresolved event participants: {_resolver.UnresolvedEventParticipants}   " +
                $"unresolved snapshots: {_resolver.UnresolvedCount}";

            _verdict.text = Time.time < _flashUntil ? "SWING" : string.Empty;
        }

        private static T Require<T>(VisualElement root, string name) where T : VisualElement
        {
            var found = root.Q<T>(name);
            if (found == null)
            {
                throw new MissingReferenceException(
                    $"GameEventProbeView.uxml has no {typeof(T).Name} named '{name}'");
            }

            return found;
        }
    }
}
