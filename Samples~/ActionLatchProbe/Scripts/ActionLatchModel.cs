using System.Collections.Generic;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Protocol;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.World;
using Google.Protobuf;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace Cuvara.Netcode.Samples.ActionLatchProbe
{
    /// <summary>
    /// One arm of the probe: a synthetic entity whose action is written at the server's
    /// CRITICAL rate and sampled at its WORLD rate, encoded through the real Protobuf
    /// codec, and merged by the real resolver and world state.
    ///
    /// <para><b>What is real and what is mirrored.</b> The counter rule is real: this calls
    /// <see cref="ActionStateLogic.Advance"/>, the same function the server calls, out of
    /// the same package. The codec, the resolver and the merger are real. What is MIRRORED
    /// is the latch — a dozen lines that live in the server's <c>ActionTransitions</c> and
    /// cannot be referenced from here, because a client has no tick schedule to apply them
    /// to. If that mirror ever drifts from the server, this scene will keep looking
    /// healthy; the backend's <c>ActionSeqTests</c> is what pins the real one.</para>
    /// </summary>
    public sealed class ActionLatchArm
    {
        private const string EntityId = "attacker";

        private readonly int _holdTicks;
        // Decode only: the client half of the wire, which is the half this scene is about.
        private readonly IWireCodec _codec = new ProtobufWireCodec();
        private readonly SnapshotResolver _resolver = new SnapshotResolver();
        private readonly WorldState _world = new WorldState();

        // Server-side state: the two Locomotion fields that matter, plus the latch.
        private EntityAction _action = EntityAction.Idle;
        private uint _seq;
        private long _holdUntil;

        // Client-side state: the last counter this client was shown. The retrigger rule is
        // INEQUALITY, never increase — the counter wraps and resets, and a greater-than
        // test would stop retriggering for four billion actions after a single wrap.
        private uint _lastRenderedSeq;
        private bool _hasRendered;

        public ActionLatchArm(string label, int holdTicks)
        {
            Label = label;
            _holdTicks = holdTicks;
        }

        public string Label { get; }

        /// <summary>Attacks the simulation performed.</summary>
        public int AttacksIssued { get; private set; }

        /// <summary>Snapshots this client received.</summary>
        public int SnapshotsReceived { get; private set; }

        /// <summary>Swings the client actually played, by the documented retrigger rule.</summary>
        public int SwingsRendered { get; private set; }

        /// <summary>Attacks the player never saw. The number the scene exists to show.</summary>
        public int AttacksLost => AttacksIssued - SwingsRendered;

        /// <summary>Bytes of snapshot body, so the cost of the field is visible too.</summary>
        public int BytesSent { get; private set; }

        /// <summary>What `action` read on the most recent snapshot.</summary>
        public EntityAction LastSampledAction { get; private set; } = EntityAction.Unspecified;

        /// <summary>What the merged client world holds right now.</summary>
        public uint LastSampledSeq { get; private set; }

        /// <summary>One CRITICAL tick: the simulation writes.</summary>
        public void Simulate(long baseTick, bool attacking)
        {
            if (attacking)
            {
                AttacksIssued++;
                Enter(EntityAction.Attacking, baseTick);
            }
            else
            {
                // The overwrite. This is the ordinary case — a player who attacks is almost
                // always also moving — and it is what makes the attack disappear without a
                // latch.
                Enter(EntityAction.Moving, baseTick);
            }
        }

        /// <summary>
        /// One WORLD tick: the gather samples, the wire carries, the client merges.
        /// </summary>
        public void Replicate(long tick)
        {
            // Built as the SERVER builds it: the generated Protobuf types, serialized into
            // an Envelope. The client codec deliberately cannot encode a SnapshotMessage --
            // a client never sends one -- so encoding through it would have meant inventing
            // a second writer, and a probe whose bytes were produced by code no server runs
            // is a probe that proves nothing about the wire.
            var wire = new RpgMmo.Wire.V1.SnapshotMessage
            {
                Tick = (ulong)tick,
                AckTick = (ulong)tick,
                Full = false,
            };
            wire.Entities.Add(new RpgMmo.Wire.V1.EntitySnapshot
            {
                Id = EntityId,
                Type = RpgMmo.Wire.V1.EntityType.Player,
                X = 0f,
                Y = 0f,
                Hp = 100,
                MaxHp = 100,
                Speed = 5f,
                FacingBrad = 1u,
                Action = (RpgMmo.Wire.V1.EntityAction)_action,
                ActionSeq = _seq,
            });

            byte[] body = new RpgMmo.Wire.V1.Envelope
            {
                Type = (uint)RpgMmo.Wire.V1.MsgType.Snapshot,
                Payload = wire.ToByteString(),
            }.ToByteArray();
            BytesSent += body.Length;
            SnapshotsReceived++;

            WireFrame frame = _codec.DecodeBody(body);
            var decoded = (SnapshotMessage)frame.Payload;
            if (!_resolver.TryResolve(decoded, out ResolvedSnapshot resolved)) return;
            _world.Apply(resolved);

            if (!_world.TryGet(EntityId, out EntitySnapshotData e)) return;

            LastSampledAction = e.Action;
            LastSampledSeq = e.ActionSeq;

            // The client's rule, applied exactly as documented: a retrigger on inequality,
            // and zero means "not sent" rather than an edge.
            if (e.ActionSeq != 0u && (!_hasRendered || e.ActionSeq != _lastRenderedSeq))
            {
                _hasRendered = true;
                _lastRenderedSeq = e.ActionSeq;
                if (e.Action == EntityAction.Attacking) SwingsRendered++;
            }
        }

        public void Reset()
        {
            _action = EntityAction.Idle;
            _seq = 0;
            _holdUntil = 0;
            _lastRenderedSeq = 0;
            _hasRendered = false;
            AttacksIssued = 0;
            SnapshotsReceived = 0;
            SwingsRendered = 0;
            BytesSent = 0;
            LastSampledAction = EntityAction.Unspecified;
            LastSampledSeq = 0;
            _resolver.Reset();
            _world.Reset();
        }

        /// <summary>
        /// Mirror of the server's <c>ActionTransitions.Enter</c>. The counter half is the
        /// shared function; only the latch is local. See the class remarks.
        /// </summary>
        private void Enter(EntityAction next, long baseTick)
        {
            bool terminal = next == EntityAction.Dead;
            if (!terminal && !ActionStateLogic.IsRetriggerable(next) && baseTick < _holdUntil)
            {
                return;
            }

            ActionStateLogic.Advance(ref _action, ref _seq, next);

            _holdUntil = ActionStateLogic.IsRetriggerable(next) && _holdTicks > 1
                ? baseTick + _holdTicks
                : 0L;
        }
    }

    /// <summary>
    /// Both arms driven from one input script, so the only difference between them is the
    /// latch. Running them side by side is the point: a single arm proves nothing, because
    /// "3 of 4 attacks lost" and "this is just how it works" look identical.
    /// </summary>
    public sealed class ActionLatchModel
    {
        private long _baseTick;
        private long _nextAttackTick;
        private uint _rng;

        public ActionLatchModel()
        {
            BuildArms();
            Reset();
        }

        public IReadOnlyList<ActionLatchArm> Arms => _arms;

        private List<ActionLatchArm> _arms = new List<ActionLatchArm>();

        /// <summary>
        /// The latched arm's hold is WorldEvery, not a constant: the hold has to be one
        /// sampling period, so a scene that let you change the world rate while pinning the
        /// hold at 4 would be demonstrating a server nobody runs.
        /// </summary>
        private void BuildArms()
        {
            _arms = new List<ActionLatchArm>
            {
                new ActionLatchArm($"latched (hold = WorldEvery = {WorldEvery})", WorldEvery),
                new ActionLatchArm("no latch (pre-fix control)", holdTicks: 1),
            };
        }

        /// <summary>Base ticks per world tick. 4 at the 60/15 default.</summary>
        public int WorldEvery { get; set; } = 4;

        /// <summary>Base ticks between attacks. 30 is the 500 ms cooldown at 60 Hz.</summary>
        public int AttackEveryTicks { get; set; } = 30;

        public long BaseTick => _baseTick;

        public void Reconfigure(int worldEvery, int attackEveryTicks)
        {
            WorldEvery = worldEvery < 1 ? 1 : worldEvery;
            AttackEveryTicks = attackEveryTicks < 1 ? 1 : attackEveryTicks;
            BuildArms();
            Reset();
        }

        /// <summary>
        /// Advance one CRITICAL tick, replicating on the world ones.
        /// </summary>
        /// <remarks>
        /// <b>The attack tick is jittered, and it has to be.</b> A fixed cadence against a
        /// fixed world period is not a coin flip, it is a fixed phase: every attack lands
        /// either always on a sampled tick or never on one, so the unlatched arm would read
        /// 0% lost or 100% lost depending on two numbers, and the scene would be showing an
        /// artefact of its own arithmetic. A real player presses at an arbitrary phase. The
        /// jitter is a deterministic LCG so the run is still reproducible, and the unlatched
        /// arm converges on the honest figure: roughly (WorldEvery - 1) / WorldEvery of
        /// attacks never reach a client.
        /// </remarks>
        public void Step()
        {
            _baseTick++;

            bool attacking = _baseTick >= _nextAttackTick;
            if (attacking)
            {
                _rng = (_rng * 1664525u) + 1013904223u;          // Numerical Recipes LCG
                int phase = (int)(_rng >> 16) % WorldEvery;
                _nextAttackTick = _baseTick + AttackEveryTicks + phase;
            }

            for (int i = 0; i < Arms.Count; i++) Arms[i].Simulate(_baseTick, attacking);

            // Same predicate the server uses: SimulationRates.RunsOn.
            if ((_baseTick - 1) % WorldEvery != 0) return;

            for (int i = 0; i < Arms.Count; i++) Arms[i].Replicate(_baseTick);
        }

        public void Reset()
        {
            _baseTick = 0;
            _rng = 2463534242u;                                   // fixed seed: reproducible
            _nextAttackTick = AttackEveryTicks;
            for (int i = 0; i < _arms.Count; i++) _arms[i].Reset();
        }
    }
}
