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
