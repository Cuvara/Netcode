# Facing and protocol version

Two things the wire gained together, both visible in one scene:

1. **A protocol version that refuses a mismatched peer with a named reason** —
   `protocol_version_mismatch` — instead of admitting it and letting it misparse.
2. **Per-entity facing** a renderer can actually turn a character with.

**No server and no network.** Snapshots are hand-built exactly the way the servers write
them and pushed through the *real* `JsonWireCodec`, `SnapshotResolver`, `WorldState`
(which merges through the shared `Shared.GameLogic.Systems.SnapshotMerger`) and
`WorldViewBinder`. What is on screen is the production decode path, not a mock of it —
but it runs with nothing else installed, which is what makes it a repeatable acceptance
check rather than an integration test. Same shape as `Interpolation Probe` and
`Clock Sync Probe`.

## What to look for

**Facing.** Three capsules orbit a ring, and each one's nose points along its direction
of travel. That direction arrives *only* in `facing_brad` — nothing in the scene derives
it from movement, so what you are watching is the wire field and nothing else.

Press **Stop sending facing**. The capsules keep moving and **stop turning**, holding
their last heading rather than snapping to east. That is the whole "zero means not sent"
rule, made visible:

> `facing_brad` is **16-bit binary radians biased by one**. Wire `0` is reserved for
> "not sent"; a real angle is `(v − 1) × 2π / 65536` radians CCW from +X.
>
> The bias exists because **0.0 radians is a perfectly ordinary facing** — due east — and
> proto3 elides a zero. Encoded as a plain `float`, "facing east" and "this server
> predates the field" would be *identical bytes*, and no receiver rule could separate
> them. `speed` has exactly that ambiguity and has to document its way around it. Facing
> does not, because the ambiguity was designed out.

**The handshake.** Three buttons build a real `JoinTokenResponse` the way a server would
and run it through the client's own version rule:

| Button | What the "server" sends | What you should see |
|---|---|---|
| Join (matching version) | `ok`, its version | `ADMITTED`, versions agree |
| Join (version + 1) | `ok:false`, `error: protocol_version_mismatch` | `REFUSED`, `retry=never` |
| Join (advertise nothing) | `ok`, **no** version field | `ADMITTED, but ... unverified` + a console warning |

The middle row is the point: a **named refusal**, delivered before a single snapshot is
parsed, and marked **permanent** — no number of retries turns a client into a different
build, so the reconnect budget is not spent on it.

The third row is the case the servers cannot report: an old server does not know the
field, ignores the version the client sent, and answers without one. A `0` coming back is
the client's only signal that nobody checked, so it is admitted **on trust** and says so
rather than staying silent.

## Run

Open `Scenes/FacingAndVersion.unity` and press Play. There is nothing to configure and no
backend to start.

Inspector knobs on the `FacingAndVersion` object: `entityCount`, `orbitRadius`,
`orbitSecondsPerTurn`, `snapshotHz` (the *world* rate — the snapshot cadence, not the
server's tick rate).

## Reference

- Normative wire reference: `backend/gameserver-dotnet/docs/API.md`, "Facing and action"
  and "Protocol version".
- Design rationale and the rejected encodings: `backend/shared/docs/DESIGN.md`.
