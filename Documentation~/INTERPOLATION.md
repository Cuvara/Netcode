# Remote Entity Interpolation

How `com.cuvara.netcode` smoothly renders remote entities between server snapshots.

## The problem

The server sends snapshots at 15 Hz. Without interpolation, a remote entity holds
still for 66ms and jumps to its next position — visible as stuttering. Interpolation
renders between the two most recent positions at frame rate, producing smooth motion.

## How it works

`SnapshotInterpolation.Evaluate` is the single interpolation method, called by both
the GameObject path (`WorldViewBinder.Tick` → `Update`) and the ECS path
(`RemoteInterpolationSystem` → Burst `IJobEntity`). There is no second implementation.

### Tick-based bracketing

Interpolation is by **server tick**, not by wall-clock arrival time:

1. `InterpolationClock` maintains a `RenderTick` — a fractional tick value held
   `TargetDelay` behind the newest received tick
2. `Evaluate` finds the two samples whose ticks straddle `RenderTick`
3. It lerps position by the tick fraction: `t = (renderTick - tickA) / (tickB - tickA)`

A skipped server tick interpolates across two ticks' worth of distance in two ticks'
worth of time, at unchanged speed — where arrival-interval interpolation would divide
doubled distance by a barely-changed interval and render at 1.5x speed before freezing.

### Buffer

Each entity maintains a ring buffer of `InterpolationSample` values:

```
ISampleBuffer / EntitySampleRing (GameObject path)
DynamicBuffer<SnapshotSample>    (ECS path)
```

Samples are keyed by server tick. A duplicate or reordered tick is rejected by
`InterpolationRing.Accepts` — the buffer only moves forward.

## InterpolationConfig

Every tuning parameter in one blittable struct (not a ScriptableObject — Burst jobs
cannot read managed assets):

| Field | Default (`InterpolationConfig.Default`) | Purpose |
|-------|---------|---------|
| `TargetDelay` | 0.100 s | Render delay behind the newest tick (jitter buffer) |
| `MaxExtrapolation` | 0.050 s | Longest extrapolation past the newest sample. 0 = never extrapolate |
| `ExtrapolationHold` | 0.150 s | How long an extrapolated entity is held at the cap before it eases back. `+Infinity` = hold forever (pre-0.48.0) |
| `ExtrapolationSettle` | 0.100 s | How long the ease back onto the newest sample takes. 0 = snap back |
| `MaxExtrapolationSpeed` | 0 (off) | A last segment faster than this (units/s) is a teleport and is not carried |
| `MaxExtrapolationSpan` | 0.250 s | A last segment longer than this is not a velocity and is not carried. 0 = no check |
| `ClockCatchUpRate` | 1.0 | How fast the render clock closes on its target |
| `MaxClockRateAdjust` | 0.10 | Largest dilation of the render clock (+/-10 %). It never jumps |
| `MinInterval` / `MaxInterval` | 0.001 s / 0.500 s | Bounds on the measured seconds per tick |
| `DefaultInterval` | 1/15 s | Snapshot interval assumed before one is measured |
| `IntervalSmoothing` | 0.3 | Smoothing of the measured interval |
| `RingCapacity` | 8 | Samples kept per entity |
| `DeferUntilBracketed` | false | Hold back a new remote entity until it can be interpolated |

`Normalized()` replaces a non-positive field with its default. The exceptions are the fields
for which zero means something (`MaxExtrapolation`, `ExtrapolationHold`,
`ExtrapolationSettle`, `MaxExtrapolationSpan`). A configured zero there is kept, and only a
negative value or NaN takes the default. The all-zero `default(InterpolationConfig)` stands
for "netcode's defaults": since 0.48.0 it gets `Default`'s hold, settle and span. Its
`MaxExtrapolation` stays 0 (no extrapolation), as before.

### TargetDelay

The jitter buffer. Default 100ms is ~1.5 snapshot intervals at 15 Hz:
- 1 full interval is the minimum (below it, no newer sample to interpolate toward)
- The extra 33ms absorbs RTT spread (~8-13ms one-way) + client scheduling jitter (+/-16ms)

**The local player pays none of this.** Predicted entities carry `PredictedTransform`
and are excluded from interpolation — their response to input is zero-latency.

## InterpolationClock

One per connection. `WorldViewBinder` drives it, and `InterpolationClockSystem` drives it in
UnityDots:

```csharp
// Each new snapshot:
clock.NoteSnapshot(world.Tick, nowSeconds, tickRate.SnapshotTickGap, config);
// Each rendered frame:
clock.Advance(deltaSeconds, config);
double renderTick = clock.RenderTick;  // fractional tick to interpolate at
// A new session:
clock.Reset();
```

The clock steers toward `newestTick - TargetDelay / secondsPerTick`. `secondsPerTick` is
measured from the snapshot ticks and their arrival times. The clock never jumps. It dilates
its rate within `MaxClockRateAdjust`, so `RenderTick` strictly increases.
`SnapshotStalenessEstimator` steers the prediction clock, not this one.

**Session boundaries.** `RenderTick` lives in one server's tick space. Call
`WorldViewBinder.Reset()` with `WorldState.Reset()` on every reconnect. Since 0.48.0, a binder
that sees the world tick go backwards restarts its render clock, its sample rings and its
estimators by itself, and counts it in `TickLineRestarts`. A server with a lower tick line is
then interpolated at once. Reset is still the correct call.

## Edge cases

| Situation | Behaviour |
|-----------|-----------|
| Buffer empty | `Evaluate` returns false — nothing honest to draw |
| Single sample | Entity renders at that position, no lerp |
| Dropped snapshot | Interpolates across the gap at unchanged speed |
| Early arrival | Sample waits in the buffer instead of displacing current segment |
| Late arrival | Covered by the TargetDelay margin |
| Reordered tick | Rejected by `InterpolationRing.Accepts`. Upstream, `WorldState` drops a snapshot older than its tick, or a second one at its tick, and counts it (`StaleSnapshotsDropped`) |
| Entity stops | The binder pushes no sample for an unchanged position. Past the newest sample the entity is extrapolated up to `MaxExtrapolation`, held for `ExtrapolationHold`, then eased back over `ExtrapolationSettle` |
| Teleport / respawn | A last segment faster than `MaxExtrapolationSpeed` or longer than `MaxExtrapolationSpan` is not carried past the destination |
| World tick goes backwards (new session) | The binder restarts its render timeline (`TickLineRestarts`) |

## Clock sync

`SnapshotStalenessEstimator` fits the client/server clock ratio from snapshot arrivals
alone. There is no NTP and no explicit clock exchange. The ratio feeds the PREDICTION clock
(`PredictionClockSteering`, see `PREDICTION.md`). The interpolation clock measures its own
seconds per tick and needs no ratio.

A fitted rate outside 0.75..1.33 is refused (`FitsRefused`). The development machine measured
+110,000 ppm, which silently disabled the fit until 0.23.0.
