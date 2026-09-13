# AOI Visibility Probe

A synthetic world of entities, one observer with a radius, no server and no network. The
visible set is computed **twice every frame** over the same entities — once by the plain
scan that tests every entity, once by narrowing with a uniform grid first — and the two
answers are compared on screen.

## Why this scene exists

The game server gained a spatial index for its area-of-interest query. An index is a
server-side data structure: it cannot be rendered, and there is nothing about it a player
could ever see. What a player *can* see is the thing it must not change — **which entities
are in view**.

That is the risk worth showing. A spatial index that drops one entity does not throw, does
not log, and does not fail a smoke test. It renders a monster that is not there, or removes
a player who is. So the scene's headline number is not a speed-up, it is the **mismatch
counter**, and the only acceptable value for it is zero — including the worst value seen
since the world was built, which is tracked separately so a single bad frame cannot scroll
past unnoticed.

## What is on screen

| Readout | What it tells you |
|---|---|
| `in view N of M (x%)` — and the radius | The AOI itself: how much of the world one observer actually receives |
| `entities examined — scan A, index B` | What the narrowing bought, for the *same* answer |
| `grid occupancy N cells of 96 needed` | Whether the server would query through its index at this density, or take the plain scan |
| verdict banner | Whether the two visible sets are identical. Green is the expected state |

Drag **World size** down until everyone clumps into a few cells and the occupancy line
flips to `PLAIN SCAN` — that is the server's gate working, not a failure. Drag it up, or
push **Entities** to a few thousand, and the index switches on and the examined-entity gap
opens up.

## Why it counts entities examined rather than milliseconds

A frame in the Editor is dominated by rendering and by whatever else the machine is doing,
so a millisecond figure measured here would describe the host, not the algorithm — the same
trap `backend/docs/BENCHMARK.md` documents at length for the server's own numbers. The
quantity this scene reports instead is exact, reproducible and frame-rate independent: how
many entities each strategy had to look at.

Real timings live in the committed server benchmark, `AoiIndexBench`, where both arms run
back to back in one process and only the within-run ratio is quoted.

## What the two arms are

- **Scan** — `AoiLogic.GetNearbyEntities` from `Shared.GameLogic`. The same function the
  server evaluates and the same one the client predicts with, not a copy written for this
  scene. It is the oracle.
- **Index** — a uniform grid mirroring the server's `SpatialGrid`: cell size is the AOI
  radius, cells are assigned by flooring (never truncating — truncation folds `-0.4` and
  `0.4` into one cell and loses entities on the negative half of the map), the covering cell
  range is derived with the same function that assigns cells, and the boundary stays
  inclusive. It narrows the search and never decides membership; the distance test does.

The grid here exists only to be compared against the scan. Nothing in the package depends on
it, and the server's own equivalence is pinned separately and far more strictly by
`AoiIndexDifferentialTests`, which asserts identical **order** as well as identical
membership — order is wire-visible on the server, because the delta encoder interns entity
ids in AOI arrival order.

## Running it

Import the sample, open `Scenes/AoiVisibilityProbe.unity`, press Play. Needs
`com.rpgmmo.shared-gamelogic` (a manual dependency of this package) for `AoiLogic` and
`Vec2`.
