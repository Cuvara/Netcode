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
| **drag Send Hz to 15** | the phase histogram collapses to one bar and the sweep verdict flips to REFUSED — the send cadence now equals the snapshot rate |
| **drag Send Hz back to 13** | all eight buckets fill within about a second and a floor is offered again |
| **drag Send Hz to 12** | it still sweeps, but only four phases are ever visited: `gcd(12, 15) = 3`. At 60 fps the frame period hides the difference; above it, it shows |

## What is real and what is synthetic

Everything runs on the real classes — the estimator, the predictor, the steering, the
catch-up clamp. The only synthetic parts are the two clocks (one advanced by
`Time.unscaledDeltaTime`, one scaled by the dial) and the delivery list standing in for a
network. The readouts are the same counters `[DOTSNet/health]` prints from a live client,
so a number seen here is directly comparable with one seen there.

## Files

- `Scripts/ClockSyncProbe.cs` — the two clocks, the delivery queue, the send loop, the readout.
- `UI/ClockSyncProbeView.uxml` / `.uss` — UI Toolkit panel; the meter and the phase histogram are the two pictures.
- `UI/ClockSyncProbePanel.asset` — PanelSettings, shared theme.
- `Scenes/ClockSyncProbe.unity` — a camera and a `UIDocument`; everything else is script.

## Send cadence — why the client does not send at 15 Hz

The second panel is about a different measurement: `AckLatencyEstimator` recovers the
pipeline constant `uplink + snapshot age` by timing an input to the first snapshot that
acknowledges it. That interval is `uplink + wait-for-the-next-snapshot + age`, and since the
wait is the only term that varies, its **minimum** converges on the constant.

That argument holds only while the wait sweeps. A client sending at exactly the snapshot rate
locks the two cadences in phase: every observation carries the same fixed wait, and the
minimum reads high by up to a whole snapshot interval. The estimator detects this and refuses
to offer anything — correctly, because a lead that is too large is worse than one that is too
small, and a phase-locked client holds no evidence about its own constant.

**Refusing correctly is not the same as being finished.** With the fallback rounding to zero
on a fast link, the term was unobtainable in principle for such a client. No statistic or
guard could have closed it: they all describe a distribution that was never generated. The
only fix is to generate it, by offsetting the cadence — which is what `InputCadence` does, and
what the slider here lets you undo.

**The nominal-vs-achieved line is the one to watch.** The cadence you set and the cadence that
goes out are different numbers whenever the send loop re-derives its deadline from whenever it
woke: `await Delay(period)` discards the frame-quantisation remainder every iteration, and at
60 fps that collapses every nominal rate in (12, 15] onto 12 Hz. `LocalMovePredictor` has always
measured the achieved interval and nothing read it. This panel reads it, next to the nominal, and
says so loudly when they disagree.

**The frame rate can be the binding constraint, not the cadence.** Acknowledgements are read on a
render frame, so the wait resolves only to a frame period: `k = fps / snapshotHz` caps how many
phases can be *told apart*, however many the cadence *visits*. Against a 15 Hz snapshot rate that
means **45 fps minimum** — below it the occupancy test cannot reach three buckets and no cadence
passes. At 30 fps, 11, 12, 13 and 14 Hz are all refused. The panel says which constraint is
binding rather than leaving you to infer it from a bare REFUSED.

The histogram divides the snapshot interval into the same eight buckets
`AckLatencyEstimator.SweepBuckets` counts occupancy over, so what is on screen is the shape
the guard is judging rather than a restatement of its verdict. Empty buckets are drawn dim
rather than omitted: "three of eight" should be legible as a picture.

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

## Importing this twice

The sample carries its own `ClockSyncProbe.asmdef`, and it needs one. Unity's sample importer
writes each import to `Assets/Samples/<package>/<version>/<sample>/`, so a project that imported
an earlier version and **committed** it ends up with two copies on disk after an update. Without
an assembly definition both compile into the project's default assembly and collide:

```
error CS0101: the namespace 'Cuvara.Netcode.Samples.ClockSyncProbe' already contains
              a definition for 'ClockSyncProbe'
error CS0229: Ambiguity between 'ClockSyncProbe.SnapshotEvery' and 'ClockSyncProbe.SnapshotEvery'
```

The whole assembly fails, so the Editor is dead until one copy is deleted by hand — and because
the two copies sit in different version folders, it happens on the first update after an import,
never to the person who did the import. With the asmdef each copy is its own assembly and the
duplicate is inert.

Delete the older folder anyway; two copies of a probe is not useful. But it should not be a
compile error, and it should not be discovered by the first person to update.
