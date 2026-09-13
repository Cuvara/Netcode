using System;
using System.Collections.Generic;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;
using UnityEngine;
using UnityEngine.UIElements;

namespace Cuvara.Netcode.Samples.AoiVisibilityProbe
{
    /// <summary>
    /// Area-of-interest made visible: a synthetic world of entities, one observer with a
    /// radius, and the visible set drawn as the player would experience it — no server, no
    /// network.
    /// </summary>
    /// <remarks>
    /// <para><b>What this exists to show.</b> The server gained a spatial index for its AOI
    /// query. An index is a server-side data structure and cannot be rendered, so what is
    /// rendered here is the thing a player can actually observe: <i>which entities are in
    /// view</i>. The claim being demonstrated is that narrowing the search does not change
    /// the answer — so this probe runs <b>both</b> strategies every frame over the same
    /// entities and reports whether their results differ. The mismatch counter is the whole
    /// point, and it should never leave zero.</para>
    ///
    /// <para><b>The scan is the oracle.</b> The brute-force arm is
    /// <see cref="AoiLogic.GetNearbyEntities(IReadOnlyList{EntityState}, in Vec2, float, Span{EntityState})"/>
    /// from <c>Shared.GameLogic</c> — the same function the server evaluates and the same
    /// one the client predicts with, not a copy written for this scene. The indexed arm is a
    /// uniform grid mirroring the server's <c>SpatialGrid</c>: same cell size (the AOI
    /// radius), same floor-based cell assignment, same inclusive boundary. It lives here
    /// only to be compared against the scan; nothing in the package depends on it.</para>
    ///
    /// <para><b>Why "candidates examined" and not milliseconds.</b> A frame in the Editor is
    /// dominated by rendering and by whatever else the machine is doing, so a millisecond
    /// figure from this scene would measure the host, not the algorithm — the same trap
    /// BENCHMARK.md documents for the server's own numbers. What this scene reports instead
    /// is the quantity the index actually changes and that is exact, reproducible and
    /// frame-rate independent: <b>how many entities each strategy had to look at</b> to
    /// produce the identical answer. Real timings belong in the committed server benchmark
    /// (<c>AoiIndexBench</c>), not in a rendered scene.</para>
    ///
    /// <para><b>The occupancy gate.</b> The server only queries through its index when the
    /// population spans enough cells to be worth it, and takes the plain scan otherwise.
    /// That threshold is mirrored in <see cref="MinOccupiedCellsToQuery"/> and shown live,
    /// so the "spread the world out and watch it switch on" behaviour is visible too: drag
    /// the world size down until everyone clumps and the gate turns the index off, which is
    /// the correct decision, not a failure.</para>
    /// </remarks>
    public sealed class AoiVisibilityProbe : MonoBehaviour
    {
        /// <summary>
        /// Mirrors <c>SpatialGrid.MinOccupiedCellsToQuery</c> on the server. Measured there,
        /// restated here so the scene shows the same decision the server would take.
        /// </summary>
        private const int MinOccupiedCellsToQuery = 96;

        [Header("World")]
        [SerializeField, Range(50, 4000)] private int entityCount = 600;
        [SerializeField, Range(200f, 2000f)] private float worldSize = 1000f;
        [SerializeField, Range(5f, 200f)] private float aoiRadius = GameConstants.DefaultAoiRadius;
        [SerializeField, Range(0f, 60f)] private float driftSpeed = 12f;

        [Header("Colours")]
        [SerializeField] private Color visibleColor = new Color(0.36f, 0.85f, 0.55f, 1f);
        [SerializeField] private Color hiddenColor = new Color(0.30f, 0.33f, 0.39f, 1f);
        [SerializeField] private Color observerColor = new Color(0.95f, 0.75f, 0.25f, 1f);

        // ── World state ──────────────────────────────────────────────────────

        private readonly List<EntityState> _entities = new List<EntityState>();
        private Vector2[] _velocities = Array.Empty<Vector2>();
        private Vec2 _observer;
        private Vector2 _observerVelocity = new Vector2(1f, 0.6f);

        private EntityState[] _scanResult = Array.Empty<EntityState>();
        private EntityState[] _indexResult = Array.Empty<EntityState>();
        private int _scanCount;
        private int _indexCount;

        private readonly UniformGrid _grid = new UniformGrid();
        private int _scanCandidates;
        private int _indexCandidates;
        private int _mismatches;
        private int _worstMismatch;
        private bool _paused;

        // ── UI ───────────────────────────────────────────────────────────────

        private VisualElement _canvas;
        private Label _visibilityLine;
        private Label _workLine;
        private Label _gateLine;
        private Label _verdict;
        private Slider _countSlider;
        private Label _countValue;
        private Slider _sizeSlider;
        private Label _sizeValue;
        private Slider _radiusSlider;
        private Label _radiusValue;

        private void OnEnable()
        {
            var document = GetComponent<UIDocument>();
            if (document == null)
            {
                Debug.LogError("AoiVisibilityProbe needs a UIDocument on the same GameObject.");
                enabled = false;
                return;
            }

            VisualElement root = document.rootVisualElement;

            _canvas = root.Q<VisualElement>("world-canvas");
            _visibilityLine = root.Q<Label>("visibility-line");
            _workLine = root.Q<Label>("work-line");
            _gateLine = root.Q<Label>("gate-line");
            _verdict = root.Q<Label>("verdict");

            _countSlider = root.Q<Slider>("entity-count");
            _countValue = root.Q<Label>("entity-count-value");
            _sizeSlider = root.Q<Slider>("world-size");
            _sizeValue = root.Q<Label>("world-size-value");
            _radiusSlider = root.Q<Slider>("aoi-radius");
            _radiusValue = root.Q<Label>("aoi-radius-value");

            if (_countSlider != null)
            {
                _countSlider.lowValue = 50f;
                _countSlider.highValue = 4000f;
                _countSlider.value = entityCount;
                _countSlider.RegisterValueChangedCallback(evt =>
                {
                    entityCount = Mathf.RoundToInt(evt.newValue);
                    Rebuild();
                });
            }

            if (_sizeSlider != null)
            {
                _sizeSlider.lowValue = 200f;
                _sizeSlider.highValue = 2000f;
                _sizeSlider.value = worldSize;
                _sizeSlider.RegisterValueChangedCallback(evt =>
                {
                    worldSize = evt.newValue;
                    Rebuild();
                });
            }

            if (_radiusSlider != null)
            {
                _radiusSlider.lowValue = 5f;
                _radiusSlider.highValue = 200f;
                _radiusSlider.value = aoiRadius;
                _radiusSlider.RegisterValueChangedCallback(evt => aoiRadius = evt.newValue);
            }

            Button reset = root.Q<Button>("reset");
            if (reset != null) reset.clicked += Rebuild;

            Toggle pause = root.Q<Toggle>("pause");
            if (pause != null) pause.RegisterValueChangedCallback(evt => _paused = evt.newValue);

            if (_canvas != null) _canvas.generateVisualContent += DrawWorld;

            Rebuild();
        }

        private void OnDisable()
        {
            if (_canvas != null) _canvas.generateVisualContent -= DrawWorld;
        }

        /// <summary>Repopulate the world. Deterministic, so a run can be repeated.</summary>
        private void Rebuild()
        {
            UnityEngine.Random.InitState(20260909);

            _entities.Clear();
            _velocities = new Vector2[entityCount];
            float half = worldSize * 0.5f;

            for (int i = 0; i < entityCount; i++)
            {
                _entities.Add(new EntityState
                {
                    Id = "e" + i,
                    Type = i % 4 == 0 ? "mob" : "player",
                    Position = new Vec2(
                        UnityEngine.Random.Range(-half, half),
                        UnityEngine.Random.Range(-half, half)),
                    Hp = 100,
                    MaxHp = 100,
                    Speed = 4f,
                });

                float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                _velocities[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            }

            _scanResult = new EntityState[entityCount];
            _indexResult = new EntityState[entityCount];
            _observer = new Vec2(0f, 0f);
            _worstMismatch = 0;
        }

        private void Update()
        {
            if (_entities.Count == 0) return;

            if (!_paused)
            {
                Drift(Time.deltaTime);
            }

            RunBothStrategies();

            if (_canvas != null) _canvas.MarkDirtyRepaint();
            UpdateReadouts();
        }

        /// <summary>Move everything, so the visible set is genuinely changing frame to frame.</summary>
        private void Drift(float dt)
        {
            float half = worldSize * 0.5f;

            for (int i = 0; i < _entities.Count; i++)
            {
                EntityState e = _entities[i];
                Vector2 v = _velocities[i];
                float x = e.Position.X + v.x * driftSpeed * dt;
                float y = e.Position.Y + v.y * driftSpeed * dt;

                // Bounce, so the population stays inside the world and occupancy is stable.
                if (x < -half || x > half) { v.x = -v.x; x = Mathf.Clamp(x, -half, half); }
                if (y < -half || y > half) { v.y = -v.y; y = Mathf.Clamp(y, -half, half); }

                _velocities[i] = v;
                e.Position = new Vec2(x, y);
                _entities[i] = e;
            }

            float ox = _observer.X + _observerVelocity.x * driftSpeed * 1.5f * dt;
            float oy = _observer.Y + _observerVelocity.y * driftSpeed * 1.5f * dt;
            if (ox < -half || ox > half) { _observerVelocity.x = -_observerVelocity.x; ox = Mathf.Clamp(ox, -half, half); }
            if (oy < -half || oy > half) { _observerVelocity.y = -_observerVelocity.y; oy = Mathf.Clamp(oy, -half, half); }
            _observer = new Vec2(ox, oy);
        }

        /// <summary>
        /// The comparison. Both arms produce a visible set for the same observer over the
        /// same entities; the sets must be identical, and the candidate counts show what the
        /// index bought to get there.
        /// </summary>
        private void RunBothStrategies()
        {
            // Arm A — the shared rule, unchanged. Every entity is tested.
            _scanCount = AoiLogic.GetNearbyEntities(_entities, in _observer, aoiRadius, _scanResult);
            _scanCandidates = _entities.Count;

            // Arm B — narrow with the grid, then apply the identical predicate.
            _grid.Rebuild(_entities, aoiRadius);
            _indexCount = _grid.Query(in _observer, aoiRadius, _indexResult, out _indexCandidates);

            // Set comparison. Order is not asserted here — the server pins that separately,
            // in AoiIndexDifferentialTests, where it is wire-visible; what a player can
            // observe is membership.
            _mismatches = CountMismatches();
            if (_mismatches > _worstMismatch) _worstMismatch = _mismatches;
        }

        private int CountMismatches()
        {
            if (_scanCount != _indexCount) return Mathf.Abs(_scanCount - _indexCount);

            var seen = new HashSet<string>();
            for (int i = 0; i < _scanCount; i++) seen.Add(_scanResult[i].Id);

            int missing = 0;
            for (int i = 0; i < _indexCount; i++)
            {
                if (!seen.Remove(_indexResult[i].Id)) missing++;
            }

            return missing + seen.Count;
        }

        private void UpdateReadouts()
        {
            if (_countValue != null) _countValue.text = entityCount.ToString();
            if (_sizeValue != null) _sizeValue.text = worldSize.ToString("F0");
            if (_radiusValue != null) _radiusValue.text = aoiRadius.ToString("F0");

            float percent = _entities.Count == 0 ? 0f : 100f * _scanCount / _entities.Count;
            if (_visibilityLine != null)
            {
                _visibilityLine.text =
                    $"in view {_scanCount} of {_entities.Count}  ({percent:F1}%)   radius {aoiRadius:F0}";
            }

            if (_workLine != null)
            {
                float saved = _scanCandidates == 0
                    ? 0f
                    : 100f * (1f - (float)_indexCandidates / _scanCandidates);
                _workLine.text =
                    $"entities examined — scan {_scanCandidates}, index {_indexCandidates} " +
                    $"({saved:F0}% fewer) for the same {_scanCount} visible";
            }

            if (_gateLine != null)
            {
                bool worth = _grid.OccupiedCells >= MinOccupiedCellsToQuery;
                _gateLine.text =
                    $"grid occupancy {_grid.OccupiedCells} cells of {MinOccupiedCellsToQuery} needed — " +
                    (worth
                        ? "server would QUERY THROUGH THE INDEX here"
                        : "server would take the PLAIN SCAN here (too clustered to pay off)");
            }

            if (_verdict != null)
            {
                bool ok = _mismatches == 0 && _worstMismatch == 0;
                _verdict.text = ok
                    ? $"visible sets identical — 0 mismatches, worst seen 0"
                    : $"MISMATCH: {_mismatches} now, {_worstMismatch} worst. An entity would vanish for this player.";
                _verdict.EnableInClassList("cuvara-probe__verdict--bad", !ok);
            }
        }

        // ── Drawing ──────────────────────────────────────────────────────────

        private void DrawWorld(MeshGenerationContext ctx)
        {
            Rect rect = _canvas.contentRect;
            if (rect.width <= 1f || rect.height <= 1f || _entities.Count == 0) return;

            Painter2D p = ctx.painter2D;
            float scale = Mathf.Min(rect.width, rect.height) / worldSize;
            Vector2 origin = new Vector2(rect.width * 0.5f, rect.height * 0.5f);

            Vector2 ToScreen(Vec2 w) => origin + new Vector2(w.X * scale, -w.Y * scale);

            // Hidden entities first, so visible ones draw over them.
            p.fillColor = hiddenColor;
            p.BeginPath();
            for (int i = 0; i < _entities.Count; i++)
            {
                Vector2 s = ToScreen(_entities[i].Position);
                p.MoveTo(s + new Vector2(1.5f, 0f));
                p.Arc(s, 1.5f, 0f, 360f);
            }
            p.Fill();

            // The AOI circle — the observer's radius, drawn in world scale.
            p.strokeColor = observerColor;
            p.lineWidth = 1.5f;
            p.BeginPath();
            Vector2 centre = ToScreen(_observer);
            p.MoveTo(centre + new Vector2(aoiRadius * scale, 0f));
            p.Arc(centre, aoiRadius * scale, 0f, 360f);
            p.Stroke();

            // Visible entities.
            p.fillColor = visibleColor;
            p.BeginPath();
            for (int i = 0; i < _scanCount; i++)
            {
                Vector2 s = ToScreen(_scanResult[i].Position);
                p.MoveTo(s + new Vector2(2.5f, 0f));
                p.Arc(s, 2.5f, 0f, 360f);
            }
            p.Fill();

            // The observer itself.
            p.fillColor = observerColor;
            p.BeginPath();
            p.MoveTo(centre + new Vector2(4f, 0f));
            p.Arc(centre, 4f, 0f, 360f);
            p.Fill();
        }

        /// <summary>
        /// A uniform spatial hash mirroring the server's <c>SpatialGrid</c>: cell size is the
        /// AOI radius, cells are assigned by <b>flooring</b> (never truncating — truncation
        /// folds -0.4 and 0.4 into one cell and loses entities on the negative half of the
        /// map), and the covering cell range is derived with the same function that assigns
        /// cells, which is what guarantees no in-radius entity can fall outside the search
        /// box. It never decides membership: the caller's distance test does.
        ///
        /// <para>Present only so this scene has something to compare the scan against.</para>
        /// </summary>
        private sealed class UniformGrid
        {
            private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
            private readonly Stack<List<int>> _pool = new Stack<List<int>>();
            private IReadOnlyList<EntityState> _source = Array.Empty<EntityState>();
            private float _cellSize = 1f;
            private float _invCellSize = 1f;

            public int OccupiedCells => _cells.Count;

            public void Rebuild(IReadOnlyList<EntityState> entities, float cellSize)
            {
                _source = entities;
                _cellSize = Mathf.Max(0.0001f, cellSize);
                _invCellSize = 1f / _cellSize;

                foreach (KeyValuePair<long, List<int>> kv in _cells)
                {
                    kv.Value.Clear();
                    _pool.Push(kv.Value);
                }
                _cells.Clear();

                for (int i = 0; i < entities.Count; i++)
                {
                    long key = CellKey(entities[i].Position);
                    if (!_cells.TryGetValue(key, out List<int> bucket))
                    {
                        bucket = _pool.Count > 0 ? _pool.Pop() : new List<int>();
                        _cells[key] = bucket;
                    }
                    bucket.Add(i);
                }
            }

            public int Query(in Vec2 centre, float radius, EntityState[] destination, out int candidates)
            {
                candidates = 0;
                int matches = 0;
                float radiusSq = radius * radius;

                int minX = CellCoord(centre.X - radius);
                int maxX = CellCoord(centre.X + radius);
                int minY = CellCoord(centre.Y - radius);
                int maxY = CellCoord(centre.Y + radius);

                for (int cx = minX; cx <= maxX; cx++)
                {
                    for (int cy = minY; cy <= maxY; cy++)
                    {
                        if (!_cells.TryGetValue(Pack(cx, cy), out List<int> bucket)) continue;

                        for (int b = 0; b < bucket.Count; b++)
                        {
                            candidates++;
                            EntityState e = _source[bucket[b]];
                            // Inclusive at the boundary, exactly as AoiLogic is.
                            if (Vec2.DistanceSq(centre, e.Position) > radiusSq) continue;
                            if (matches < destination.Length) destination[matches] = e;
                            matches++;
                        }
                    }
                }

                return matches;
            }

            private int CellCoord(float v) => Mathf.FloorToInt(v * _invCellSize);

            private long CellKey(in Vec2 p) => Pack(CellCoord(p.X), CellCoord(p.Y));

            private static long Pack(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;
        }
    }
}
