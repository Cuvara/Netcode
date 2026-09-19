using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace DOTSSample
{
    /// <summary>
    /// Measures how evenly replicated entities are actually DRAWN, separately for the local
    /// player, remote players and enemies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the existing counters cannot answer this.</b> <c>[DOTSNet/health]</c> reports
    /// <c>lastCorrection</c>, <c>snaps</c> and <c>reconciles</c>, and all three come from
    /// <c>LocalMovePredictor</c> — they describe the avatar this client predicts and say
    /// nothing at all about a mob. "Is the enemy smooth" was being answered with a number
    /// about the player, which is a different object that happens to be nearby.
    /// </para>
    /// <para>
    /// <b>What it measures.</b> The per-frame displacement of each entity's rendered
    /// <see cref="LocalTransform"/>, which is what the view last wrote and therefore what
    /// the player saw. Stutter is not a low average — an entity rendered in lurches travels
    /// exactly as far as one rendered smoothly. It is the SPREAD: the worst frame step
    /// against the median. A ratio near 1 is even motion; 2 means half the frames moved at
    /// double speed, which is the shape a stall-then-jump makes.
    /// </para>
    /// <para>
    /// Read in <c>LateUpdate</c> so the view's writes for this frame have already landed,
    /// and gated behind <c>-cuvara-motion-probe</c> so an ordinary run is untouched.
    /// </para>
    /// </remarks>
    public sealed class RenderMotionProbe : MonoBehaviour
    {
        private const string Flag = "-cuvara-motion-probe";
        private const float ReportSeconds = 5f;

        /// <summary>Below this, a step is float noise rather than motion.</summary>
        private const float NoiseUnits = 1e-5f;

        /// <summary>
        /// Frames rendered within this many seconds of an entity FIRST being seen are
        /// counted as "fresh" and reported apart from steady-state ones.
        /// </summary>
        /// <remarks>
        /// A freshly spawned entity starts with a single interpolation sample, and one
        /// sample cannot be interpolated — the view holds the entity still until the second
        /// snapshot arrives, one send interval later, and the buffer fills to its 2–3 sample
        /// depth. Those held frames are real chop, but they are the chop of ARRIVING, not of
        /// steady replication. An entity that churns — spawn, cross the map, get reaped,
        /// respawn — pays that hold every few seconds; a persistent one pays it once and
        /// amortises it to nothing over a long run. Pooling the two makes a high-churn class
        /// (enemies) look like it stutters in steady state when it does not. 0.25s covers the
        /// buffer warm-up at the sample's 15Hz send rate (≈3 intervals) with slack.
        /// </remarks>
        private const float WarmupSeconds = 0.25f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var args = System.Environment.GetCommandLineArgs();
            var on = false;
            foreach (var a in args)
            {
                if (a == Flag) { on = true; break; }
            }

            if (!on) return;

            var go = new GameObject("RenderMotionProbe");
            go.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(go);
            go.AddComponent<RenderMotionProbe>();
            Debug.Log("[motion-probe] on");
        }

        private sealed class Bucket
        {
            public readonly List<float> Steps = new List<float>();
            public int Frozen;         // frames where a MOVING entity rendered no movement
            public int FrozenFresh;    // ...of those, within WarmupSeconds of the entity's first sighting
            public int FrozenSteady;   // ...of those, after it
            public int NFresh;         // moving frames classified fresh
            public int NSteady;        // moving frames classified steady-state
            public int Parked;         // entities that did not move at all this window
            public int Moving;         // entities that did
        }

        /// <summary>One entity's frames within the current report window.</summary>
        private sealed class Track
        {
            public readonly List<float> Steps = new List<float>();
            // Parallel to Steps: was this frame within WarmupSeconds of the entity's first
            // sighting. A List<bool> rather than an age list because that is all Report needs.
            public readonly List<bool> Fresh = new List<bool>();
            public string Class;
            public float Travel;
        }

        private readonly Dictionary<Entity, float3> _last = new Dictionary<Entity, float3>();
        // First time each live entity was seen, in realtimeSinceStartup. Persists across
        // report windows (an entity's lifetime spans them) and is pruned when the entity
        // despawns, so a reused entity slot is correctly treated as freshly born.
        private readonly Dictionary<Entity, float> _seenAt = new Dictionary<Entity, float>();
        private readonly Dictionary<Entity, Track> _tracks = new Dictionary<Entity, Track>();
        private readonly Dictionary<string, Bucket> _buckets = new Dictionary<string, Bucket>();
        // Reused across frames to prune despawned entities from _last / _seenAt without
        // allocating. Without the prune both dictionaries grow unbounded under enemy churn.
        private readonly HashSet<Entity> _live = new HashSet<Entity>();
        private readonly List<Entity> _stale = new List<Entity>();
        private EntityQuery _query;
        private EntityManager _em;
        private float _nextReport;
        private int _frames;
        private float _fpsAccum;

        private void Start()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null)
            {
                Debug.LogWarning("[motion-probe] no default world; probe disabled");
                enabled = false;
                return;
            }

            _em = world.EntityManager;
            _query = _em.CreateEntityQuery(
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<NetworkEntityTag>());
            _nextReport = Time.realtimeSinceStartup + ReportSeconds;
        }

        private void LateUpdate()
        {
            _frames++;
            _fpsAccum += Time.unscaledDeltaTime;

            var now = Time.realtimeSinceStartup;
            _live.Clear();

            var entities = _query.ToEntityArray(Unity.Collections.Allocator.Temp);
            for (var i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                _live.Add(e);
                var pos = _em.GetComponentData<LocalTransform>(e).Position;

                if (_last.TryGetValue(e, out var prev))
                {
                    var step = math.length(pos - prev);
                    var tag = _em.GetComponentData<NetworkEntityTag>(e);
                    var name = tag.IsLocal ? "local-player"
                        : _em.HasComponent<EnemyTag>(e) ? "enemy"
                        : "remote-player";

                    // Age from first sighting. An entity already present when the probe
                    // started reads as fresh for its first WarmupSeconds — we cannot know
                    // its real age, and over a long run the one warm-up per persistent
                    // entity is negligible against a churning one's many.
                    var fresh = !_seenAt.TryGetValue(e, out var seen) || now - seen < WarmupSeconds;

                    // Per ENTITY, not straight into the class bucket. An enemy that has
                    // reached the centre is stopped by EnemyMoveSystem on the server
                    // (distSq <= 0.01f -> continue), and a stopped entity renders zero
                    // movement every frame -- which is correct, and which pooled straight
                    // into the bucket reads exactly like the stutter this probe exists to
                    // find. The first version of this probe reported 21.9% "frozen" frames
                    // for enemies and could not say how much of it was mobs parked at the
                    // centre. Entities are classified at report time instead.
                    if (!_tracks.TryGetValue(e, out var t))
                    {
                        _tracks[e] = t = new Track();
                    }

                    t.Class = name;
                    t.Steps.Add(step);
                    t.Fresh.Add(fresh);
                    t.Travel += step;
                }
                else
                {
                    // First sighting: stamp its birth so later frames can age against it.
                    _seenAt[e] = now;
                }

                _last[e] = pos;
            }

            entities.Dispose();

            // Prune despawned entities so _last / _seenAt do not grow unbounded under
            // churn, and so a reused entity slot starts fresh rather than inheriting an age.
            _stale.Clear();
            foreach (var kv in _last)
            {
                if (!_live.Contains(kv.Key)) _stale.Add(kv.Key);
            }
            for (var i = 0; i < _stale.Count; i++)
            {
                _last.Remove(_stale[i]);
                _seenAt.Remove(_stale[i]);
            }

            if (Time.realtimeSinceStartup < _nextReport) return;
            _nextReport = Time.realtimeSinceStartup + ReportSeconds;
            Report();
        }

        /// <summary>
        /// Total travel below this over a whole window means the entity was not moving,
        /// so its still frames are its own and not the network's.
        /// </summary>
        private const float ParkedTravel = 0.05f;

        private void Report()
        {
            var fps = _frames / math.max(_fpsAccum, 1e-6f);
            _frames = 0;
            _fpsAccum = 0f;

            foreach (var kv in _tracks)
            {
                var t = kv.Value;
                if (t.Class == null || t.Steps.Count == 0) continue;

                if (!_buckets.TryGetValue(t.Class, out var b))
                {
                    _buckets[t.Class] = b = new Bucket();
                }

                if (t.Travel < ParkedTravel)
                {
                    b.Parked++;
                    continue;   // its stillness is the simulation's, not the wire's
                }

                b.Moving++;
                for (var i = 0; i < t.Steps.Count; i++)
                {
                    b.Steps.Add(t.Steps[i]);
                    var fresh = t.Fresh[i];
                    if (fresh) b.NFresh++; else b.NSteady++;
                    if (t.Steps[i] < NoiseUnits)
                    {
                        b.Frozen++;
                        if (fresh) b.FrozenFresh++; else b.FrozenSteady++;
                    }
                }
            }

            _tracks.Clear();

            foreach (var kv in _buckets)
            {
                var steps = kv.Value.Steps;
                if (steps.Count < 8) continue;

                steps.Sort();
                var median = steps[steps.Count / 2];
                var p99 = steps[(int)(steps.Count * 0.99f)];
                var worst = steps[steps.Count - 1];

                // The ratio, not the raw numbers, is the verdict: it is scale-free, so it
                // compares an enemy walking at one speed against a player sprinting at
                // another without either needing a calibration.
                var ratio = median > NoiseUnits ? worst / median : -1f;
                var frozenPct = 100f * kv.Value.Frozen / steps.Count;

                // Split the frozen share by lifetime. If a class's chop is the spawn
                // warm-up, frozenFresh dominates and frozenSteady is near zero; if it is
                // genuine steady-state stutter, the reverse. This is the line that tells a
                // churning class (enemies) apart from a stuttering one.
                var fresh = kv.Value.NFresh;
                var steady = kv.Value.NSteady;
                var frozenFreshPct = fresh > 0 ? 100f * kv.Value.FrozenFresh / fresh : 0f;
                var frozenSteadyPct = steady > 0 ? 100f * kv.Value.FrozenSteady / steady : 0f;

                Debug.Log(
                    $"[motion-probe] {kv.Key,-14} moving={kv.Value.Moving,2} parked={kv.Value.Parked,2} " +
                    $"n={steps.Count,5} fps={fps,6:F1} " +
                    $"median={median:F5} p99={p99:F5} worst={worst:F5} " +
                    $"worst/median={ratio,6:F2} frozenFrames={frozenPct,5:F1}% " +
                    $"(fresh={frozenFreshPct,5:F1}% n={fresh,5} | steady={frozenSteadyPct,5:F1}% n={steady,5})");

                steps.Clear();
                kv.Value.Frozen = 0;
                kv.Value.FrozenFresh = 0;
                kv.Value.FrozenSteady = 0;
                kv.Value.NFresh = 0;
                kv.Value.NSteady = 0;
                kv.Value.Parked = 0;
                kv.Value.Moving = 0;
            }
        }
    }
}
