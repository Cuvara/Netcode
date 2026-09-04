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
