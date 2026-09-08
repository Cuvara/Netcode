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
residual upward — a 10% client/server clock ratio would read as tens of
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

### The lead must cover the pipeline, not just the snapshot's age

The client applies an input at its **own** tick T. The server applies it at the tick its
packet is drained on — `InputHandler` uses the stamped tick only for ordering and the ack,
never to place the step in time. So the two label the same input with the same tick number
only if the client's clock leads the server's by the uplink delay. Steering to
`snapshotTick + lead` puts the client `lead - age` ticks ahead of the server, so:

```
required lead  =  uplink  +  snapshot age
residual correction (steps)  =  1  +  (uplink + age - lead)
```

where the 1 is the ±1 base tick two free-running clocks cost at a transition.

**Neither term is visible to `SnapshotStalenessEstimator`.** It fits a *lower envelope*, so
it reports only the variable delay above the floor; the constant part is absorbed into the
fitted offset along with the clocks' origins. `StalenessTicks` reads ~0.02 whether the
pipeline constant is 0 or 2 ticks, and the steering error reads 0 throughout. That is why a
residual here survives with every other counter clean.

`TargetLeadTicks` therefore takes the constant from the caller, as `RoundTripMs * 0.5`. Set
`binder.RoundTripMs` every frame — `DOTSNetworkBridge` does — or that term is zero. Be aware
that the heartbeat round trip measures the **socket**, not the server's staged snapshot write
or the input drain quantisation, so it under-reports the constant: on localhost it is ~1 ms
where the missing term measures ~17 ms.

`AckLatencyEstimator` measures the constant properly. The client knows when it sent input
tick N and when it first saw a snapshot with `ack_tick >= N`; that interval is
`uplink + wait for the next snapshot + age`, the wait is what varies, and its **low quantile**
converges on `uplink + age` — the exact quantity, no new wire traffic.

It is a quantile and not a minimum, and that distinction was bought with a defect. The
minimum-filter argument the staleness estimator makes — a mean would measure the jitter sitting
on top of the floor — holds when the observations are a constant plus a sweeping wait. It does
not hold when the constant itself has a loaded and an unloaded mode, and under load it has
exactly that: a loaded run measured 23 ms typical with a p90 of 32, and an extremum over ~140
observations found the two or three that had caught a gather immediately and reported **0.17
base ticks while the same wire measured 1.54**. A tenth-percentile floor keeps the argument —
still far below the mean, so stalls above it are still ignored — and cannot be defined by one
lucky observation. See `AckLatencyEstimator.FloorPercentile`. Call `WorldViewBinder.NoteInputSent(tick)`
beside `LocalMovePredictor.RecordInput`, or the estimator only has one end of the interval and
offers nothing.

### The lead arithmetic, in full

```
lead = age                                  Staleness: fitted line, provisional, or gap
     + (HasEstimate ? ConservativeFloorTicks : rttTicks * 0.5)
ceiling = SnapshotTickGap * 2 + rttTicks + ceil(ConservativeFloorTicks)
TargetLeadTicks = min(round(lead), ceiling)
```

Three things about that are load-bearing and were each got wrong once.

**The floor displaces the round trip, it is not added to it.** They measure the same pipeline
by different means, so adding them counts it twice. The floor wins because it times the real
path end to end — the input drain, the server's staged snapshot write, the wire, the wait for a
client frame — while the heartbeat round trip sees none of the staging and half of it is not the
quantity anyway. A consumer that supplies `RoundTripMs` and never calls `NoteInputSent` keeps
exactly the behaviour it had before the estimator existed.

**`rttTicks` is computed whether or not there is a floor.** It used to live on the `else`
branch, so a floor appearing removed the round trip from the *ceiling* too. A clamp that
tightens because a measurement arrived is a second, accidental steer — and, with the
truncation below, it is the whole explanation for the clock error moving from −1 to −2/−4 the
first time this estimator was wired in. Nothing was steering on the floor; the round trip had
simply stopped steering.

**The contribution is fractional, biased low by its own measurement uncertainty.** The bias is
not optional: an over-lead pushes the client past the server, which is the original defect
arriving from the other side. But the first attempt produced it with `Math.Floor` on whole base
ticks, and that is a guard which fires as a total loss — the two live readings were 0.14 and
0.68 base ticks and both truncated to zero, so the estimator contributed nothing in exactly the
regime it exists for. A sub-tick deficit is not a sub-tick problem either: the tick *label* is
an integer, so a lead 0.68 ticks short carries the wrong tick number for most of every tick and
the reconcile returns a whole step for it. `ConservativeFloorTicks` keeps the bias in the units
that are genuinely uncertain — `FloorSeconds - UnsweptSeconds`, where `UnsweptSeconds` is the
measured part of the wait's range never sampled — so it shrinks to nothing as the sweep
completes rather than whenever the link is fast.

### What makes a floor safe to steer on

- **No provisional reading.** This term *adds* lead. Staleness can offer a provisional figure
  because it can be clamped downward onto one already in use; there is no equivalent safe
  direction here, so nothing is offered until `MinimumSamples` observations have been folded in.
- **The sweep is verified, not assumed.** A client sending at the world rate sends at exactly
  the snapshot rate. If the two stay in phase, every observation carries the same fixed wait and
  the minimum reads high by up to a whole interval. A floor is offered only once the
  observations span `MinimumSweepFraction` of a snapshot interval, that interval measured as the
  smallest gap between acknowledgements.
- **Only the newest input an acknowledgement retires is timed.** An older input waited for an
  acknowledgement a *later* one had already earned. Folding those in cannot lower the floor — a
  minimum is monotone — but it stretches the observed span by however far the send cadence runs
  ahead of the acknowledgement cadence, and the span is the entire evidence the sweep check
  rests on. A client sending four inputs per snapshot in phase read "swept" on that alone.
  Superseded observations are drained and counted as `Superseded`.
- **Out-of-band observations are refused, not clamped.** Past `MaximumFloorSeconds` an
  observation is a stall, a reconnect or a suspended process, none of which describe a steady
  pipeline. A clamped bad observation is still wrong and now looks plausible.

### Reading the two ACK FLOOR lines

`PredictionLatencyMeasurement` prints both, and they are **not** the same statistic:

| Line | What it is |
|---|---|
| `ACK FLOOR (harness)` | the smallest input→ack seen across the ~20 *sample* inputs, one per sample iteration. An upper bound on `uplink + age`. |
| `ACK FLOOR (estimator)` | the estimator's tenth-percentile floor over every observation, sample and settle inputs alike — several times as many, at a different phase against the snapshot cadence. |

So the estimator's line is expected to sit **at or a little below** the harness's, in *both* an
idle and a loaded context — and the pair diverging is the signal to read first. Two ways it has
actually diverged, each with its own counter beside it:

- the estimator far **below** the harness (0.17 against 1.54): either a rare best-case defining
  the floor, which `FloorPercentile` now prevents, or acknowledgements from a session the server
  has not reaped, which `ack floor ack-ahead` names;
- the estimator **above** the harness: a phase lock, in which case nothing should be offered at
  all and `ACK FLOOR (estimator) … NOT OFFERED` is what prints.

### Reading the diagnostics: what a zero means here

Several counters in this system return **0** both when the quantity is genuinely zero and when
it was never measured, and every prediction defect found so far has hidden behind one of them
at some point.

| Reading | Means "all is well" | Also means |
|---|---|---|
| `clock rate difference 0 ppm` | the two clocks agree | **no line was ever fitted** — check `staleness fit` |
| `clock error 0` | the clock is in step | **the predictor is off**, so the steer never ran |
| `ACK FLOOR (estimator) 0.00` | — | not offered: too few observations, or the wait never swept |
| `tick rate ... (agrees)` | the rates match | they differ by up to 15%, which is the tolerance |
| `max correction 0.0000` | prediction and server agree | nothing moved, so nothing could disagree |

The rule that follows: **never read a zero as evidence without the counter beside it that says a
measurement happened.** `staleness fit` prints fits/refused/baseline for that reason, the
wire-rate gap prints as a percentage rather than a verdict, and the clock error prints a band
rather than a latched sample. A run that failed to measure and a run that measured agreement
must not print the same thing.

### The fitted rate, and the assumption underneath it

`SnapshotStalenessEstimator` fits the server's clock to the client's — offset *and rate* — by
drawing a line through two anchors, each the lowest sample of its epoch. The rate is fed to the
base-tick clock through `LocalMovePredictor.SetClockRateScale`, which is what stops the
proportional steer drooping against a real clock difference.

**That line is a rate only if the minimum achievable delay is the same at both anchors.** It is
the whole of the envelope argument and it is not free: if the delay floor rises between them — a
starved frame loop, a machine that got busy — the later anchor sits above the true line and the
slope absorbs the displacement as rate. The same machine measured minutes apart read 220 ppm
idle and **90 636 ppm** inside a loaded test suite; over the 4 s minimum baseline that is a floor
step of 362 ms, and the client ran its clock 8.3% slow on the strength of it.

The discriminator is baseline, not magnitude. A rate reads the same over any span; a floor step
fakes `step / baseline` and halves when the baseline doubles. So `RateCorroborated` is true only
once the reading has reproduced over a **doubled** baseline, and only then does the rate reach
the clock. Comparing consecutive fits does not work — a decay's change between neighbours falls
under any fixed tolerance once the baseline is long enough, and a 300 ms step self-corroborates
at about 25 s on a reading still 12 000 ppm wrong.

Note the split: `IsUsable` gates the **age**, `RateCorroborated` gates the **rate**. A wrong
slope perturbs a residual slightly, once per snapshot; it perturbs a clock rate every second,
forever. Those are different evidence bars and they used to share one gate.

In the measurement report, read `rate corroborated` before `clock rate difference`. `NO` beside a
large ppm is the artefact being caught; `NO` beside a small one, early in a session, is simply a
baseline that has not doubled yet.

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
| ~1 + ACK FLOOR steps | the pipeline constant above — the lead is not covering uplink + age |
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
