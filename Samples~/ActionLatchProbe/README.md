# Action Latch Probe

No server, no network. One synthetic attacker, written at the server's **critical** rate
and sampled at its **world** rate, encoded as real Protobuf snapshots and merged by the
real resolver and world state — **twice**, once with the server's one-shot latch and once
without it.

## What it shows

`EntityAction` is level-triggered: it says what state an entity is *in*, never that a state
was *entered*. That produces two separate defects, and this scene separates them.

**1. The sampling gap.** Actions are written on the critical group (60 Hz by default) and
sampled by the snapshot gather on the world group (15 Hz), so only one base tick in four is
ever observable by a client. An attack sets `Attacking` on one tick and the next tick of
movement overwrites it. Press nothing and watch the right-hand column: roughly three
quarters of attacks never reach the client at all. That column is not a hypothetical — it
is the server as it behaved before the latch shipped.

**2. The missing edge.** Even when an attack *is* sampled, two attacks in a row are
identical bytes, so the delta encoder classifies the second as unchanged and drops it.
`action_seq` is what makes them different, and the retrigger rule the scene applies is the
documented one: **inequality, never increase**, because the counter wraps and resets.

Both arms receive the same number of snapshots and the same number of bytes. The counter is
a varint on a message that was being sent anyway. What differs is how many of the attacks
were in one.

## What is real, and what is not

| Part | Real? |
|---|---|
| The counter rule | **Real.** `Shared.GameLogic.Systems.ActionStateLogic.Advance`, the same function the server calls, out of the same package. |
| Wire bytes | **Real.** Built as the server builds them — the generated `RpgMmo.Wire.V1` types serialized into an `Envelope`. The client codec deliberately cannot *encode* a snapshot (a client never sends one), so encoding through it would have meant a second writer and bytes no server produces. |
| Decode, resolve, merge | **Real.** `ProtobufWireCodec`, `SnapshotResolver`, `WorldState`. |
| The retrigger decision | **Real rule, applied here.** It belongs to a view (`IEntityPoseView`), i.e. to a game, so the probe applies the documented rule itself rather than pretending to own it. |
| The **latch** | **Mirrored, not imported.** It lives in the server's `GameServer/World/ActionTransitions.cs` and cannot be referenced from a client — a client has no tick schedule to apply it to. |

That last row is the scene's honest limit: **if the mirror ever drifts from the server,
this scene will keep looking healthy.** The backend's `GameServer.Tests/Input/ActionSeqTests.cs`
is what pins the real one, and it carries its own control arm for the same reason this
scene has a second column.

## What it does NOT prove

- That your server is configured at 60/15. Move the sliders and the numbers move with them.
- That any animation system retriggers correctly. The probe counts the edges a view *would*
  be given; what a given `IEntityPoseView` does with them is the game's business.
- Anything about the transport. There is no socket here.

## Reading the numbers

- **attacks issued** — what the simulation did.
- **swings rendered** — what a client applying the retrigger rule would have played.
- **never seen** — the difference. The number the scene exists to show.
- **action / action_seq** — what the merged client world holds right now.

The attack tick is **jittered** by a fixed-seed LCG. A fixed cadence against a fixed world
period is not a coin flip but a fixed phase, so without jitter the control arm would read
0% or 100% depending on two numbers rather than on the defect. A real player presses at an
arbitrary phase; the control converges on `(WorldEvery - 1) / WorldEvery`.

Setting `SIM_WORLD_HZ` so it does not divide `SIM_CRITICAL_HZ` is a **startup failure** on
the real server (`SimulationRates.TryCreate`), not something it rounds. The hint under the
sliders says so; the probe floors it only so the sliders stay usable.
