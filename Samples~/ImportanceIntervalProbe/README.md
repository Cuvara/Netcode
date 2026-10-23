# Importance Interval Probe

No server, no network. A synthetic population encoded twice into **real Protobuf
snapshots** — once the way the game server sends today, once with distance- and
type-tiered send intervals — so the saving can be seen against the shape of world
that produces it.

## The one thing this scene is for

**The saving is a property of the population AND of the thresholds, and the second
one is easy to get wrong.** Press *Cluster* — 200 players standing on each other,
the shape the published 200-player ceiling was measured on — and the shipped
policy still halves the wire, because a merely-moving player does not clear the
every-tick threshold either. Press *Realistic* and it is 56–58%.

An earlier version of this scene answered `0.0%` to the first question, because it
mirrored thresholds nobody runs. Two sliders are provided so the next reader can
attack the number rather than inherit it.

## Measured, against the authority

`GameServer.Tests/Bench/ImportanceIntervalBench.cs` runs the **real**
`SnapshotDeltaState` and is the number of record. This scene reproduces its four
reference rows:

| shape | pop | B/ent/snap today | tiered | saving | stale max | tier mix 1/2/4 |
|---|---|---|---|---|---|---|
| Cluster | 200 | 31.3 | 16.4 | **47.8 %** | 1 | 0/100/0 |
| Spread | 200 | 31.2 | 17.1 | **45.4 %** | 1 | 5/95/0 |
| Realistic | 360 | 14.5 | 6.4 | **55.9 %** | 3 | 3/16/82 |
| Realistic | 720 | 15.0 | 6.3 | **58.2 %** | 3 | 1/17/82 |

A live 200-player sweep on the real server measured **−47.3 %** on cluster and **−47.2 %**
on spread (`BENCHMARK.md` Part XIV), so scene, bench and server agree to within two points.

> **Cluster used to read 0.0 % here, and that was this scene's fault.** It mirrored a
> hand-written policy that gave a near player interval 1. The shipped one scores a
> merely-moving player at distance 2 + type 3 = **5**, under the 8 that buys every-tick
> treatment, so players land in the middle band too. That is where most of the saving comes
> from — and it means **player positions replicate at 7.5 Hz**. Drop the top threshold to 5
> and watch the saving collapse; that slider is the honest way to see how much of the 47 %
> is players rather than mobs.

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
