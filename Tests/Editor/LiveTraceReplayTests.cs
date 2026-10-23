using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using NUnit.Framework;
using Cuvara.Netcode.Json;
using Cuvara.Netcode.Prediction;
using Shared.GameLogic.Components;
using Shared.GameLogic.World;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Three live S13 sessions, recorded by the predictor trace, replayed headless: first exactly as
    /// they ran, then with the stall-frame fix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three earlier stall fixes passed the synthetic harness and not the live client, because the
    /// harness modelled the client's frame order instead of recording it. These fixtures are the
    /// client's own calls (<c>Fixtures/S13Traces/PROVENANCE.md</c>).
    /// </para>
    /// <para>
    /// <see cref="TheRecordedSessions_ReplayExactly"/> replays with the change switched off. Every
    /// reconcile result must match the recording bit for bit, which validates the replay.
    /// <see cref="WithTheFix_EachStallCostsAtMostOneCorrection"/> then judges the change on the same
    /// calls.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class LiveTraceReplayTests
    {
        private struct Line
        {
            public double T;
            public bool IsEvent;
            public PredictorTraceEvent Event;
            public bool IsLongFrame;
        }

        private sealed class Trace
        {
            public readonly List<Line> Lines = new List<Line>();
            public int TickRate = 15;
            public float Speed = 5f;
            public MapBounds Bounds = MapBounds.Default;
        }

        private static string FixtureDir([CallerFilePath] string here = "") =>
            Path.Combine(Path.GetDirectoryName(here) ?? ".", "Fixtures", "S13Traces");

        private static readonly Dictionary<string, Trace> Cache = new Dictionary<string, Trace>();

        private static Trace Load(string name)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;

            var trace = new Trace();
            double t = 0;
            using (var file = File.OpenRead(Path.Combine(FixtureDir(), name + ".trace.ndjson.gz")))
            using (var gz = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(gz, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("{\"frame\"", StringComparison.Ordinal))
                    {
                        JsonValue frame = JsonParser.Parse(line);
                        t = frame.GetFloat("t");
                        // The stall frame: its unscaled delta carries the freeze. Start-up frames
                        // (before 10 s) are excluded.
                        bool longFrame = frame.GetFloat("udt") > 0.4f && t > 10.0;
                        trace.Lines.Add(new Line { T = t, IsLongFrame = longFrame });
                        continue;
                    }

                    if (line.Contains("\"kind\":\"header\""))
                    {
                        // Tolerant of its position: on line 2 in this build, after the first marker.
                        JsonValue header = JsonParser.Parse(line);
                        trace.TickRate = header.GetInt("tick_rate", trace.TickRate);
                        trace.Speed = header.GetFloat("speed", trace.Speed);
                        IReadOnlyList<JsonValue> b = header.GetArray("bounds");
                        if (b.Count == 4)
                        {
                            trace.Bounds = new MapBounds(
                                (float)b[0].AsNumber(), (float)b[1].AsNumber(), (float)b[2].AsNumber(), (float)b[3].AsNumber());
                        }

                        continue;
                    }

                    if (PredictorTraceEvent.TryParseJson(line, out PredictorTraceEvent e))
                    {
                        trace.Lines.Add(new Line { T = t, IsEvent = true, Event = e });
                    }
                }
            }

            Cache[name] = trace;
            return trace;
        }

        /// <summary>dev_arena, as the session ran on it (the predictor clamps against its walls and boxes).</summary>
        private static MapGeometry LoadMap()
        {
            JsonValue map = JsonParser.Parse(File.ReadAllText(Path.Combine(FixtureDir(), "dev_arena.json")));
            map.TryGetMember("bounds", out JsonValue b);
            var bounds = new MapBounds(b.GetFloat("minX"), b.GetFloat("minY"), b.GetFloat("maxX"), b.GetFloat("maxY"));

            map.TryGetMember("heightfield", out JsonValue hf);
            IReadOnlyList<JsonValue> h = hf.GetArray("heights");
            var heights = new float[h.Count];
            for (int i = 0; i < h.Count; i++) heights[i] = (float)h[i].AsNumber();
            var field = new HeightField(
                hf.GetFloat("originX"), hf.GetFloat("originY"), hf.GetFloat("cellSize"),
                hf.GetInt("columns"), hf.GetInt("rows"), heights);

            IReadOnlyList<JsonValue> bx = map.GetArray("boxes");
            var boxes = new StaticBox[bx.Count];
            for (int i = 0; i < bx.Count; i++)
            {
                boxes[i] = new StaticBox(
                    bx[i].GetFloat("minX"), bx[i].GetFloat("minY"), bx[i].GetFloat("minZ"),
                    bx[i].GetFloat("maxX"), bx[i].GetFloat("maxY"), bx[i].GetFloat("maxZ"));
            }

            return new MapGeometry(bounds, field, boxes, null, null);
        }

        private struct Result
        {
            public double T;
            public PredictorTraceEvent Event;
        }

        private static List<Result> Recorded(Trace trace)
        {
            var results = new List<Result>();
            foreach (var l in trace.Lines)
            {
                if (l.IsEvent && l.Event.Call == PredictorCall.ReconcileResult) results.Add(new Result { T = l.T, Event = l.Event });
            }

            return results;
        }

        private static List<Result> Replay(Trace trace, bool fix)
        {
            var predictor = new LocalMovePredictor(new PredictionSettings(trace.TickRate, trace.Speed, trace.Bounds))
            {
                MoveStallFrameInputs = fix,
            };
            var results = new List<Result>();
            double t = 0;
            predictor.Trace = (in PredictorTraceEvent e) =>
            {
                if (e.Call == PredictorCall.ReconcileResult) results.Add(new Result { T = t, Event = e });
            };
            var replayer = new PredictorTraceReplayer(predictor) { Geometry = LoadMap() };
            foreach (var l in trace.Lines)
            {
                t = l.T;
                if (l.IsEvent) replayer.Apply(in l.Event);
            }

            return results;
        }

        private static List<double> StallFrames(Trace trace)
        {
            var stalls = new List<double>();
            foreach (var l in trace.Lines)
            {
                if (!l.IsEvent && l.IsLongFrame) stalls.Add(l.T);
            }

            return stalls;
        }

        private const float SnapUnits = LocalMovePredictor.SmoothingThreshold;

        [TestCase("2v6e")]
        [TestCase("me2y")]
        [TestCase("wqel")]
        public void TheRecordedSessions_ReplayExactly(string name)
        {
            Trace trace = Load(name);
            List<Result> recorded = Recorded(trace);
            List<Result> replayed = Replay(trace, fix: false);

            Assume.That(recorded.Count, Is.GreaterThan(300), "precondition: the session reconciled");
            Assert.That(replayed.Count, Is.EqualTo(recorded.Count), "reconcile count");
            for (int i = 0; i < recorded.Count; i++)
            {
                PredictorTraceEvent a = recorded[i].Event, b = replayed[i].Event;
                Assert.That(
                    a.A == b.A && a.B == b.B && a.C == b.C && a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.W == b.W,
                    Is.True,
                    $"{name} reconcile #{i} at t={recorded[i].T:F3}: recorded base {a.A} tickError {a.B} correction {a.X} " +
                    $"position ({a.Y},{a.Z}) snaps {a.W}; replayed base {b.A} tickError {b.B} correction {b.X} " +
                    $"position ({b.Y},{b.Z}) snaps {b.W}");
            }
        }

        [TestCase("2v6e")]
        [TestCase("me2y")]
        [TestCase("wqel")]
        public void WithTheFix_EachStallCostsAtMostOneCorrection(string name)
        {
            Trace trace = Load(name);
            List<double> stalls = StallFrames(trace);
            Assume.That(stalls.Count, Is.EqualTo(2), "precondition: the 500 ms and the 2 s stall");

            List<Result> before = Recorded(trace);
            List<Result> after = Replay(trace, fix: true);

            var report = new StringBuilder();
            for (int s = 0; s < stalls.Count; s++)
            {
                int snapsBefore = 0, snapsAfter = 0;
                foreach (var r in before) if (r.T >= stalls[s] && r.T < stalls[s] + 2.0 && r.Event.X > SnapUnits) snapsBefore++;
                foreach (var r in after)
                {
                    if (r.T >= stalls[s] && r.T < stalls[s] + 2.0 && r.Event.X > SnapUnits)
                    {
                        snapsAfter++;
                        report.Append($" t={r.T:F3} {r.Event.X:F3}u te={r.Event.B};");
                    }
                }

                TestContext.WriteLine($"{name} stall at t={stalls[s]:F3}: snaps live {snapsBefore}, with the fix {snapsAfter}.{report}");
                report.Clear();
                Assert.That(snapsAfter, Is.LessThanOrEqualTo(1), $"{name}: stall at t={stalls[s]:F3}");
                Assert.That(snapsAfter, Is.LessThanOrEqualTo(snapsBefore), $"{name}: the fix may not add a correction");
            }

            // Outside the stalls the fix changes nothing that shows: the same snaps as recorded.
            int outsideBefore = 0, outsideAfter = 0;
            foreach (var r in before) if (r.Event.X > SnapUnits && !Near(stalls, r.T)) outsideBefore++;
            foreach (var r in after) if (r.Event.X > SnapUnits && !Near(stalls, r.T)) outsideAfter++;
            Assert.That(outsideAfter, Is.EqualTo(outsideBefore), name + ": snaps outside the stalls");
        }

        private static bool Near(List<double> stalls, double t)
        {
            foreach (var s in stalls) if (t >= s && t < s + 2.0) return true;
            return false;
        }
    }
}
