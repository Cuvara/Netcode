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

| Field | Default | Purpose |
|-------|---------|---------|
| `TargetDelay` | 0.100s | Render delay behind newest tick (jitter buffer) |
| `MaxSamples` | 8 | Ring buffer capacity per entity |
| `ExtrapolationLimit` | 0.0s | Max time to extrapolate past the newest sample (0 = none) |

### TargetDelay

The jitter buffer. Default 100ms is ~1.5 snapshot intervals at 15 Hz:
- 1 full interval is the minimum (below it, no newer sample to interpolate toward)
- The extra 33ms absorbs RTT spread (~8-13ms one-way) + client scheduling jitter (+/-16ms)

**The local player pays none of this.** Predicted entities carry `PredictedTransform`
and are excluded from interpolation — their response to input is zero-latency.

## InterpolationClock

Advances the render timeline once per frame:

```csharp
clock.Advance(deltaTime, newestReceivedTick, secondsPerTick);
float renderTick = clock.RenderTick;  // fractional tick to interpolate at
```

The clock steers toward `newestTick - TargetDelay/secondsPerTick`. When the client's
clock drifts relative to the server (measured by `SnapshotStalenessEstimator`), the
clock adjusts its advance rate to converge rather than jumping.

## Edge cases

| Situation | Behaviour |
|-----------|-----------|
| Buffer empty | `Evaluate` returns false — nothing honest to draw |
| Single sample | Entity renders at that position, no lerp |
| Dropped snapshot | Interpolates across the gap at unchanged speed |
| Early arrival | Sample waits in the buffer instead of displacing current segment |
| Late arrival | Covered by the TargetDelay margin |
| Reordered tick | Rejected by `InterpolationRing.Accepts` |

## Clock sync

`SnapshotStalenessEstimator` fits the client/server clock ratio from snapshot
arrival patterns alone — no NTP, no explicit clock exchange. It detects when the
client's clock runs fast or slow relative to the server and reports the ratio so the
interpolation clock can steer on it.

Clamped at +/-200,000 ppm to reject pathological readings (the development machine
measured +110,000 ppm, which silently disabled the fit until 0.23.0).
