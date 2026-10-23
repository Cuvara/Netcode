using NUnit.Framework;
namespace Cuvara.Netcode.Tests.Editor
{
    public sealed class ZZSweepT51
    {
        [Test]
        public void Sweep()
        {
            var sb = new System.Text.StringBuilder();
            var summary = new System.Collections.Generic.SortedDictionary<string, int[]>();
            foreach (var motion in new[] { PredictionHarness.Motion.Circle, PredictionHarness.Motion.StartStop })
            foreach (double stall in new[] { 0.4, 0.5, 0.6, 0.8, 2.0 })
            foreach (string shape in new[] { "full", "sameframe", "paced" })
            for (int seed = 1; seed <= 12; seed++)
            {
                var c = ServerStallRecoveryTests.Scenario(motion);
                c.Seed = seed; c.HitchAtSeconds = 12.0; c.HitchSeconds = stall;
                c.InputBeforeSnapshot = true; c.MaxFrameDeltaSeconds = 1.0 / 3.0; c.DeltaTimeLagsOneFrame = true;
                if (shape == "full") { c.SnapshotsVisibleNextFrame = true; c.FrameSeconds = 1.0 / 500.0; }
                if (shape == "paced") c.BacklogSpacingSeconds = 0.010;
                var r = PredictionHarness.Run(c);
                double end = 12.0 + stall + (shape == "paced" ? stall * 0.15 : 0) + 0.15 + 2.0;
                int big = 0; double maxSteps = 0; var at = new System.Text.StringBuilder();
                foreach (var (t, corr, te) in r.CorrectionLog)
                    if (t >= 12.0 && t < end && corr >= r.StepLength * 1.999f) { big++; maxSteps = System.Math.Max(maxSteps, corr / r.StepLength); at.Append($" {t:F3}:{corr / r.StepLength:F1}/{te}"); }
                string key = $"{motion} {stall:F1}s {shape}";
                if (!summary.TryGetValue(key, out var a)) summary[key] = a = new int[4];
                a[System.Math.Min(big, 3)]++;
                if (big > 1 && (seed <= 2 || shape != "paced")) sb.AppendLine($"  >1: {key} seed={seed} big={big} resyncs={r.StallResyncs} |{at}");
            }
            foreach (var kv in summary) sb.AppendLine($"{kv.Key}: big0={kv.Value[0]} big1={kv.Value[1]} big2={kv.Value[2]} big3+={kv.Value[3]}");
            Assert.Fail("SWEEP51\n" + sb);
        }
    }
}
