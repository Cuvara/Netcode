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
            public int Frozen;    // frames where a MOVING entity rendered no movement
            public int Parked;    // entities that did not move at all this window
            public int Moving;    // entities that did
        }

        /// <summary>One entity's frames within the current report window.</summary>
        private sealed class Track
        {
            public readonly List<float> Steps = new List<float>();
            public string Class;
            public float Travel;
        }

        private readonly Dictionary<Entity, float3> _last = new Dictionary<Entity, float3>();
        private readonly Dictionary<Entity, Track> _tracks = new Dictionary<Entity, Track>();
        private readonly Dictionary<string, Bucket> _buckets = new Dictionary<string, Bucket>();
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

            var entities = _query.ToEntityArray(Unity.Collections.Allocator.Temp);
            for (var i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                var pos = _em.GetComponentData<LocalTransform>(e).Position;

                if (_last.TryGetValue(e, out var prev))
                {
                    var step = math.length(pos - prev);
                    var tag = _em.GetComponentData<NetworkEntityTag>(e);
                    var name = tag.IsLocal ? "local-player"
                        : _em.HasComponent<EnemyTag>(e) ? "enemy"
                        : "remote-player";

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
                    t.Travel += step;
                }

                _last[e] = pos;
            }

            entities.Dispose();

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
                    if (t.Steps[i] < NoiseUnits) b.Frozen++;
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

                Debug.Log(
                    $"[motion-probe] {kv.Key,-14} moving={kv.Value.Moving,2} parked={kv.Value.Parked,2} " +
                    $"n={steps.Count,5} fps={fps,6:F1} " +
                    $"median={median:F5} p99={p99:F5} worst={worst:F5} " +
                    $"worst/median={ratio,6:F2} frozenFrames={frozenPct,5:F1}%");

                steps.Clear();
                kv.Value.Frozen = 0;
                kv.Value.Parked = 0;
                kv.Value.Moving = 0;
            }
        }
    }
}
