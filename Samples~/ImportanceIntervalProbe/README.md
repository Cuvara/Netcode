# Importance Interval Probe

No server, no network. A synthetic population encoded twice into **real Protobuf
snapshots** — once the way the game server sends today, once with distance- and
type-tiered send intervals — so the saving can be seen against the shape of world
that produces it.

## The one thing this scene is for

**The saving is not a property of the feature. It is a property of the
population.** Press *Cluster* and the answer is `0.0%`: 200 players standing on
each other are all near players, all tier 1, and no weighting demotes any of them.
That is the shape the published 200-player ceiling was measured on, so the
headline bandwidth figure is the one number an interval policy cannot move.

Press *Realistic* and it is 44–46%.

Both are true. Which one you quote decides whether the feature is worth building,
and that is the decision this scene exists to inform.

## Measured, against the authority

`GameServer.Tests/Bench/ImportanceIntervalBench.cs` runs the **real**
`SnapshotDeltaState` and is the number of record. This scene reproduces its four
reference rows:

| shape | pop | B/ent/snap today | tiered | saving | stale max | tier mix 1/2/4 |
|---|---|---|---|---|---|---|
| Cluster | 200 | 31.3 | 31.3 | **0.0 %** | 0–1 | 100/0/0 |
| Spread | 200 | 31.2 | 21.7 | **30.6 %** | 1 | 36/64/0 |
| Realistic | 360 | 14.5 | 8.1 | **44.1 %** | 3 | 14/30/56 |
| Realistic | 720 | 15.0 | 8.0 | **46.5 %** | 3 | 12/31/56 |

Bytes agree with the bench to within 2 %, savings to within 0.4 points, and the
tier histograms match. The one visible disagreement is Cluster staleness — the
bench reads 0 and this reads 1, because a handful of random-walking players drift
past a tier boundary that the bench's own geometry keeps them inside. It does not
move the byte columns.

## What is real and what is mirrored

| Part | Real? |
|---|---|
| Snapshot bytes | **Real.** Built from the generated `RpgMmo.Wire.V1` types and measured with `CalculateSize()` — the same call the server's own downlink budget uses — including handle interning and the handle reset at every keyframe. |
| Delta suppression | **Mirrored.** An entity whose visible state did not change is omitted, as `SentView.Equals` decides on the server. |
| Keyframe phase | **Mirrored**, using the server's own FNV-1a-of-user-id, so the two arms stagger keyframes the way real connections do. |
| The interval policy | **Does not exist yet anywhere.** It is proposed here and in the bench, in one function, so the thresholds can be argued about with a number attached. |

**If the mirror drifts from the server, this scene will keep looking healthy.**
Re-run the bench before quoting anything from here.

## Reading it

- **today / tiered** — bytes per entity **per snapshot**, averaged over every
  entity observed in every viewer's AOI. Entities that were in the AOI but not
  sent still count in the denominator; that is deliberate, because "how much does
  one entity in view cost me" is the question a fleet is sized on.
- **staleness** — in **world ticks**. At 15 Hz one tick is 66.7 ms. Shown next to
  the saving on purpose: halving the bytes by letting an entity go a second stale
  has not bought anything.
- **tier mix** — the share of observations that landed in each interval, counted
  over *every* observation rather than only the ones that changed. The bench counts
  it the same way; counting only changed entities describes where the savings came
  from and reads far rosier than the population actually is.

## Two knobs worth turning before you believe the 44 %

1. **Fraction of mobs moving → 1.** An idle entity is *already* free: the delta
   encoder omits anything unchanged. Much of the Realistic saving is the tiering
   taking credit next to a population that gives it work for free. At 1.0 the
   tiering has to earn it.
2. **AOI radius.** The competing lever, and it already ships
   (`GAMESERVER_AOI_RADIUS`). Population inside a circle grows with the *square*
   of the radius, so 50 → 35 is a 51 % cut with no scheduler, no new per-connection
   state and no staleness at all. Move it before concluding the tiering is what
   you need.

## What it does NOT prove

- That this game's population looks like *Realistic*. It does not: the server's
  enemy AI is scaffolding — 30 mobs walking to the origin — so the shape is a
  guess, and the number is conditional on it.
- That the thresholds are right. They are one proposal, editable on two sliders.
- Anything about CPU, the transport, or whether deferral is safe. Starvation
  bounds are the backend's problem and are asserted there.
