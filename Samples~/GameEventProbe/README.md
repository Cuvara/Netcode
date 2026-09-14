# Game Event Probe

No server, no network. A synthetic snapshot stream drives the **real** codec, resolver,
merger and view binder, so what is on screen is the production decode path — and it re-runs
with no backend, which is what makes it an acceptance check rather than an integration test.

Two capsules: an attacker and a victim. The attacker swings on a cadence. Every swing
carries a `damage` event and advances the attacker's `action_seq`.

## The two buttons that are the point

### Stop sending events

The attacker keeps hitting and the victim's HP keeps dropping — because HP is **state** and
arrives in the snapshot either way. The floating damage numbers stop, because a number is an
**occurrence** and nothing here derives one from an HP delta.

That is the argument for the channel existing, made visible. Press *Heal the victim* while
events are off and watch the HP readout: a heal and a hit inside one tick net out, so there
is no HP delta to read a damage number from. Two entities' worth of state can be perfectly
correct and still not contain the fact that a hit happened.

### Stop sending action_seq

The attacker keeps attacking and the red `SWING` flash fires **once and then stops**, while
`action` reads `Attacking` on every single snapshot.

The flash is driven only by the counter changing. Nothing watches `action` for an edge,
because there is no edge in it to watch: two swings in a row are identical bytes. This is the
repeated-attack problem, reproduced on demand, and the reason the counter had to be
manufactured by the server rather than derived by a client.

Note the readout uses **inequality**, not greater-than. The counter wraps at 2³² and resets
when a server restarts, so a greater-than test would silently stop retriggering for four
billion actions after a single wrap.

## The third button

*Send an event naming an unknown entity* pushes an event whose source this client has never
been told about. Watch the resolver line: `unresolved event participants` increments, the
number still renders with no source, and `unresolved snapshots` stays at zero.

That asymmetry is deliberate. An unresolvable **entity** handle aborts the snapshot and
forces a keyframe, because the alternative is a wrong world. An unresolvable **event**
participant is reported absent and counted, because a missing damage number is not worth a
keyframe's bandwidth for every observer — least of all when the link is already struggling.

## Running it

Open `Scenes/GameEventProbe.unity` and press Play. There is nothing to configure and no
backend to start.

Inspector knobs on the `GameEventProbe` object: `snapshotHz`, `snapshotsPerAttack`,
`damagePerHit`.

## What it does not prove

The stream is JSON, which addresses event participants by **id**. A Protobuf connection
carries per-connection **handles** instead and resolves them against the interning table —
which a scene cannot show, because a handle is a number with no visible consequence when it
is right. That path is covered by `Tests/Editor/GameEventAndActionSeqTests.cs`, including the
case where an event names an entity introduced by the same snapshot and the case where it
names one that is legitimately absent from the entity list.
