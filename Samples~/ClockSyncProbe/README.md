# Clock Sync Probe

Two synthetic clocks, no server, no network. The dial sets how fast the client's clock runs
against the server's; `SnapshotStalenessEstimator` fits the ratio from snapshot arrivals
alone, and `LocalMovePredictor.SteerToServerTick` steers the prediction clock on the result.
The scene renders the fit converging on the dial — or being refused, visibly, when the dial
is past the clamp.

## Why a scene, when the estimator has seventeen tests

The tests state the behaviour in numbers. What none of them carries is the two facts a
person setting up a new machine actually needs to see:

- a client whose clock disagrees with the server's by **eight percent converges anyway**,
  and stays converged, with `Snaps` at zero;
- when the disagreement is past `MinimumSkew..MaximumSkew` the fit is **refused**, and what
  that looks like — because for one release it looked like nothing at all.

Both took a live two-machine investigation to learn the first time. The default dial
position, **+110 ×10³ ppm**, is that investigation: it is the measured ratio between the
Windows performance counter and the Linux monotonic clock on the machine this package was
developed on — the value that sat just past the original 0.90/1.10 clamp, so every fit was
silently rejected, `SkewPpm` read 0 (indistinguishable from two agreeing clocks), and the
steering fell back to a derived figure for the whole session. 0.23.0 widened the clamp and
made refusals a counter; this scene makes them a colour.

## What to try

| Action | What it shows |
|---|---|
| leave it alone | the fill converges on the mark within a few epochs; verdict flips to CONVERGED |
| drag skew past ±100 ×10³ | fills pins at measured value while usable; past the clamp the fit refuses and says so |
| jitter up to 100 ms | the envelope fit shrugs it off — the lower envelope is exactly the samples jitter cannot push down |
| **Stall a frame** | one 250 ms `deltaTime`; `ClampedFrames` ticks up instead of the clock burst-advancing |
| **Step server clock +5 s** | a restart on a new tick origin; `HardResyncs` moves, `Snaps` does not |

## What is real and what is synthetic

Everything runs on the real classes — the estimator, the predictor, the steering, the
catch-up clamp. The only synthetic parts are the two clocks (one advanced by
`Time.unscaledDeltaTime`, one scaled by the dial) and the delivery list standing in for a
network. The readouts are the same counters `[DOTSNet/health]` prints from a live client,
so a number seen here is directly comparable with one seen there.

## Files

- `Scripts/ClockSyncProbe.cs` — the two clocks, the delivery queue, the readout.
- `UI/ClockSyncProbeView.uxml` / `.uss` — UI Toolkit panel; the meter is the one picture.
- `UI/ClockSyncProbePanel.asset` — PanelSettings, shared theme.
- `Scenes/ClockSyncProbe.unity` — a camera and a `UIDocument`; everything else is script.

## Raise delivery floor — the defect v0.34.0 closes

Press it and nothing about the two clocks changes: they stay in whatever agreement the dial
sets, and at the default they agree exactly. All it does is add a sustained 300 ms to every
delivery — not the one-off "Stall a frame", a floor that rises and stays risen, which is what a
loaded machine or a transient server hiccup produces.

The fit reports tens of thousands of ppm anyway. That is not noise and it is not a bug in the
arithmetic: the envelope is a line through two best-case samples, and it is a *rate* only if the
minimum achievable delay was the same at both anchors. When the floor rises between them the
later anchor sits above the true line and the slope absorbs the displacement. Measured live, one
machine minutes apart read 220 ppm idle and 90 636 ppm under load, and the client obediently ran
its base-tick clock 8.3% slow.

What the readout now shows is the guard refusing it:

- `corroborated NO` — a rate reads the same over any baseline; a floor step fakes
  `step / baseline` and halves when the baseline doubles, so it never reproduces.
- `age from the unit-rate floor` — the age is the height above that same tilted line, so gating
  only the clock left the slope steering the lead through the residual. It falls back to a
  reading that carries no slope at all.
- The steering lead stays put instead of climbing with the baseline.

Press it again to clear the floor and watch corroboration return once two fits agree across a
doubled baseline.

**On the dial's +110,000 ppm default.** It was recorded as this machine's *measured* ratio and
used to justify widening the estimator's clamp. It has since been falsified — the same
Windows-Editor/Linux-container pair measures **220 ppm** idle — and the six-figure readings only
appear under load, which is this button's mechanism. The default is kept because the clamp must
still admit such a ratio and the refusal boundary is worth being able to see, but it is a
synthetic stress case now, not a machine's fingerprint.
