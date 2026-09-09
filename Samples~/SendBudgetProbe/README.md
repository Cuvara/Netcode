# Snapshot send budget

A synthetic crowd driven through the game server's **per-connection downlink budget**, so
the trade it makes can be watched instead of read about. No server, no network, no
backend required.

Open `Scenes/SendBudgetProbe.unity` and press Play.

## What it is showing

The game server caps how many bytes of snapshot payload one connection may be sent per
snapshot (`GAMESERVER_MAX_SNAPSHOT_BYTES`, default 8192). Before it existed, the AOI
radius was the only bound on a snapshot — and a radius bounds **area, not population**, so
a crowd inside one observer's circle produced a frame as large as the crowd, per client,
per tick.

When more has changed than fits, the server **defers** the lowest-priority entities to a
later snapshot. It does not drop them. The priority order is:

1. **The observer's own entity** — never deferred. It is the reconciliation anchor, and a
   stale one reads as rubber-banding, the most-noticed netcode artefact there is.
2. **Despawns** — a deferred despawn leaves a ghost, which is *wrong* state rather than
   stale state.
3. **Longest deferral first** — strict aging, so the deferred set drains before the fresh
   set and nothing starves.
4. **Nearest first**, then a deterministic tie-break.

## What to do with it

Push **Entities in AOI** and **Fraction moving / tick** up until `shed / tick` leaves
zero, then watch four things:

| Readout | What it should do |
|---|---|
| `payload` and the bar strip | flattens against the red budget line and stays there |
| `max deferral` | rises, then **stops**. The scheduler is strictly oldest-first, so the wait is bounded by the size of the dirty set — not by how long the scene has been running |
| `stale on client` | climbs to a plateau. This is the cost being paid: entities a beat behind, not entities missing |
| `handle errors` | **stays at 0** |

That last one is the whole point. `wire.proto` is explicit that a receiver seeing an
entity handle it has no binding for must not guess — it has lost state the sender assumed
it had, and its only correct move is `MsgResync`. If shedding and the delta encoder's
"what does this client already have" bookkeeping ever drifted apart, an entity would be
dropped from the wire while recorded as delivered, and the first visible symptom would be
a handle arriving unbound. The readout is on screen because a check nobody reads is not a
check.

Set **Budget** to `0` to see the unbounded behaviour the budget replaced, and press
**Force keyframe** to watch a keyframe get budgeted too — the client's set is replaced
outright, so an entity a keyframe omits disappears and is re-introduced by a following
delta. A visible pop at the edge of the circle, never wrong state.

## What is real and what is not

- **Real**: the byte figures. Snapshots are built as `RpgMmo.Wire.V1.SnapshotMessage` —
  the same generated Protobuf schema the server encodes with, shipped in this package for
  the decode path — and measured, never estimated. Entity-id interning is modelled
  faithfully: the id travels only on the message that introduces its handle, and handles
  reset at every keyframe.
- **Not production netcode**: `SendBudgetModel` is a sample-only mirror of the server's
  `SnapshotDeltaState`. The budget is a **server** decision; a client neither implements
  it nor needs to know it happened, because deferral is invisible in the protocol. See
  "Downlink budget" in the backend's `docs/API.md`. Nothing in `Runtime/` depends on this
  folder.
- **Not a benchmark**: the crowd is synthetic and the movement is arbitrary. It shows the
  shape of the trade, not the numbers your world will produce.

## Files

| File | Role |
|---|---|
| `Scripts/SendBudgetProbe.cs` | Scene driver and UI binding, 15 Hz snapshot cadence |
| `Scripts/SendBudgetModel.cs` | Mirror of the server's budgeted delta encoder |
| `Scripts/SyntheticCrowd.cs` | The load dial — population and churn, deterministic |
| `Scripts/FakeClient.cs` | Handle resolution and the keyframe/delta merge rule, strictly |
| `UI/SendBudgetProbeView.uxml` / `.uss` | The panel. UI Toolkit, like every sample here |
