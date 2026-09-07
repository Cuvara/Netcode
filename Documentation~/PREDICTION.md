# Client-Side Prediction

How `com.cuvara.netcode` predicts the local player's movement to hide network latency.

## The problem

Without prediction, pressing a key sends input to the server, the server applies it
on the next tick, and the result comes back in a snapshot — a full round trip of
visible lag on your own avatar. Prediction applies the input locally the instant it
is sent and corrects itself against the authoritative answer when it arrives.

## The loop

```
1. Player presses key
2. Client assigns a tick number to the input
3. Input is applied IMMEDIATELY to the predicted position (via Shared.GameLogic)
4. Input is sent to the server AND kept in a local buffer
5. Server processes it, includes AckTick in the next snapshot
6. Client receives snapshot with AckTick = newest accepted input tick
7. Client drops all inputs up to AckTick
8. Client rewinds to the authoritative position from the snapshot
9. Client replays only unacknowledged inputs → new predicted position
10. If the error is small: smooth. If large: snap.
```

## LocalMovePredictor

The predictor. **Movement only, on purpose** — combat has server-side rules the
client cannot reproduce (cooldowns, range validation), so a predicted hit that the
server refuses is worse than showing the hit late.

### Key methods

```csharp
// Called every frame with the local player's input
predictor.PredictAndSend(moveX, moveY, attackTargetId);

// Called when a snapshot arrives
predictor.Reconcile(snapshot);

// The position to render (predicted + smoothing offset)
Vector2 renderPos = predictor.RenderPosition;
```

### Replay through Shared.GameLogic

Replay calls `MovementSystem.TryMove` — the **exact** entry point the server's
`InputHandler` calls. This matters in two specific ways:

1. **`Integrate`** splits `pos += dir * speed * dt` into separate float locals to
   deny the JIT an FMA contraction (which rounds once instead of twice). A hand-written
   multiply-add re-introduces exactly the divergence that split prevents.

2. **`ResolveDirection`** normalizes magnitude above 1, so diagonal input `(1,1)`
   moves at unit speed, not 1.414x. Calling `Integrate` directly with unnormalized
   input would predict diagonal movement 41% too fast.

### Smoothing vs snapping

Every reconcile produces some position error:

| Error magnitude | Behaviour |
|----------------|-----------|
| Below `SmoothingThreshold` (default 2.0 units) | Absorbed into a decaying render offset |
| Above `SmoothingThreshold` | Offset dropped, avatar snaps to correct position |

The render position is always `predictedPosition + smoothingOffset`, where the offset
decays exponentially each frame.

## TickRateEstimator

Measures the server's tick rate from snapshot arrivals. The server sends `tick_rate`
in `JoinTokenResponse` (since #93), but the estimator validates it against what
actually arrives. A mismatch is logged as a warning.

The estimated tick rate drives `secondsPerTick` which the predictor uses to advance
its internal tick counter.

## SnapshotStalenessEstimator

Fits the client/server clock ratio from snapshot inter-arrival times. Reports how
many ppm the client clock runs fast or slow relative to the server, so the prediction
clock can adjust its advance rate to stay synchronized.

**The fit is linear regression over a sliding window.** A single outlier (GC pause,
radio wake) does not move the estimate; it takes sustained drift across the window.

Clamped at +/-200,000 ppm. The development machine measured +110,000 ppm (host
`CLOCK_REALTIME` running 11% fast against `CLOCK_MONOTONIC`), which silently disabled
the fit until 0.23.0 added the clamp and the probe scene.

### The warm-up window, and why the reading is offered in two strengths

A rate is a slope, and a slope over a short baseline is mostly the noise of its two
endpoints, so `IsUsable` — "a line has been fitted" — cannot turn true early and
deliberately does not. Two consecutive `EpochSeconds` epochs cannot span the
`MinimumBaselineSeconds` a fit needs, so against a 15 Hz snapshot stream **the first
fit lands 8.2 s after join**.

For those eight seconds `WorldViewBinder.TargetLeadTicks()` used to fall back to a
derived figure of one snapshot interval. On localhost that is **4 base ticks against
a real age of 0.06**. The lead is not a diagnostic — the clock is steered onto it — so
a four-tick error there means a tick number stops naming the same moment on the two
sides, and `LocalMovePredictor.Reconcile`'s history path, which indexes the client's
own history by the *server's* tick number, reports the whole of it as a positional
correction of **0.3333 world units (4.00 steps at speed 5 / 60 Hz)** at every start
and every stop, for the first eight seconds of every session. A live measurement read
162 reconciles, 36 corrections, max 4.00 steps, with both sides agreeing on 60 Hz and
every other counter clean — which reads as a tick-rate mismatch and is not one.

The **age**, unlike the rate, does not need a long baseline: over a few seconds the
lower envelope's slope is one to within a few hundred ppm, which is 0.02 base ticks
over ten seconds against the four it replaces. So `StalenessTicks` is also offered
*provisionally*, from `MinimumProvisionalSamples` snapshots (~0.2 s) onward, as the
height above a running unit-rate floor. `HasEstimate` is true for either strength;
`IsUsable` still means only "fitted".

**The provisional reading is trusted downwards only.** An unfitted rate drifts the
residual upward — the 1.103 client/server clock ratio above would read as tens of
ticks of "age" inside the warm-up — so the binder takes `min(provisional, derived)`.
Below the derived figure the reading is evidence; above it, it is drift. That makes
the warm-up lead never worse than the old fallback and, on the measured localhost
case, four ticks better.

**What this does not fix.** Two free-running 60 Hz clocks disagree about which tick a
motion transition lands on by plus or minus one, so a start or a stop still costs a
correction of up to one step (0.0833 units). That is quantisation, not disagreement,
and no lead can remove it. A measurement that expects corrections to be rare across
many start/stop transitions is measuring it.

### The clock runs on the server's timebase

`SteerToServerTick` corrects the base-tick clock's **phase**. It is proportional (gain 0.1,
called once per snapshot) and has no integral term, so a constant **rate** difference is not
something it can remove — it settles at a standing offset instead:

```
standing tick error  =  drift / (gain x snapshotHz)
                     =  (clockRatio - 1) x baseHz / (0.1 x snapshotHz)
```

At 60/15 that is `drift / 1.5`, so a client clock 9% fast sits 3.6 base ticks ahead of the
server forever. Because `Reconcile` compares at the snapshot's own tick *number*, an offset
of n ticks makes the two sides label different moments with the same number and the whole of
it is returned as position — a correction at every start and stop.

So the rate is corrected separately, and by feed-forward rather than by an integrator:
`SnapshotStalenessEstimator` already fits it (`SkewPpm`), and `WorldViewBinder` hands it to
`LocalMovePredictor.SetClockRateScale` before each steer. Only when the line is **fitted** —
the provisional warm-up reading carries no rate at all — and only within the reciprocals of
the estimator's own skew bounds, outside which a value is refused rather than clamped and
counted in `RefusedClockRateScales`.

A ratio near 1.10 is not exotic: it is the Windows performance counter against the Linux
clock the server ticks on, and it is what the development machine measures.

### Reading a correction figure

Size a correction by the tick rate **measured off the wire**, never by the one the client
believes it is predicting at: a client on the wrong rate sizes its own yardstick by that
same wrong rate, so four real ticks of error print as "1.00 steps" and look healthy.
`PredictionLatencyMeasurement` exposes both as `ExpectedStepFromWire` and `ExpectedStep`
and prints them on adjacent lines for exactly this reason.

Then read the magnitude, not the count. `LocalMovePredictor.SmoothedCorrections`
increments on *any* nonzero error, so on a stimulus of N start/stop transitions it lands
near N however correct both sides are — it is a floor, not a fault. What separates the
causes is how big each correction is:

| Max correction | Meaning |
|---|---|
| ~1 step | the ±1 base tick two free-running clocks cost at a transition. The floor. |
| ~`SnapshotTickGap` steps (4 at 60/15) | the prediction clock is steered to the wrong offset — compare `TARGET LEAD` against `SNAPSHOT AGE` |
| a ratio of the two rates | a genuine tick-rate mismatch; `TickRateEstimator.Disagrees` should be true as well |
| `clock error` steps, with `clock rate difference` large | proportional droop against a clock-rate difference — check `clock rate correction` is not 1.0 |

`replayed steps 0` is a **healthy** reading, not an open loop: the history path is the
accurate one and replaying is its fallback, so a client whose clock tracks the server hits
the history every time and replays nothing. Read `reconciles from history` for whether the
loop closed.

The `[Measure]` block prints `SNAPSHOT AGE measured`, `TARGET LEAD in use`, `snapshot gap
measured` and `clock error (last steer)` for every run, prediction-OFF included, because
the first three are properties of the binder and the link rather than of the predictor —
so the two columns are comparable and a difference between them is itself a finding.

## PredictionSettings

| Field | Default | Purpose |
|-------|---------|---------|
| `SmoothingThreshold` | 2.0 | Error above this snaps instead of smoothing |
| `SmoothingDecay` | 0.85 | Per-frame decay factor for the smoothing offset |
| `MaxUnackedInputs` | 120 | Buffer size for unacknowledged inputs |

## Debugging

The DOTS sample's HUD shows:
- `Predict … err 0.000` — the reconciliation error (should be near zero)
- `ack` — the server's acknowledged input tick
- `pending` — number of unacknowledged inputs in the replay buffer

A non-zero error that does not decay indicates a determinism divergence between
client and server — usually a float operation that rounds differently under
NativeAOT vs IL2CPP.
