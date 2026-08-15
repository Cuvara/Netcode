# Client netcode — transport, codec, handshake

The `NDC.Scripts.Net` assembly (`Assets/Scripts/Net/`) is everything the client
puts on the wire: framing, encoding, the two-hop handshake, the heartbeat, and
entity-handle resolution. It contains **no game rules** — no movement
integration, no damage, no validation. Those are server-authoritative and live in
`Shared.GameLogic` in the backend repo (ADR-10); a client-side copy would
silently diverge from the server, which is the failure this architecture exists
to prevent.

The wire contract is `backend/shared/proto/wire.proto`, with
`backend/gameserver-dotnet/docs/API.md` and `backend/gateway/docs/API.md` as the
normative references. Where those disagree with the server source, the source
wins; see "Known doc/source disagreements" at the end.

## Two connections, not one

The gateway is a redirector and is **never** in the gameplay data path (ADR-3).

```
1. client -> gateway      auth{token}              -> auth_resp{ok, user_id}
2. client -> gateway      enter_world{map_id}      -> enter_world_resp{server_addr, join_token, transport}
3. client -> game server  join_token{token}        -> join_token_resp{ok, user_id}      (direct, not proxied)
4. client -> game server  input{...}      per tick
   game server -> client  snapshot{...}   per tick
```

| | Gateway connection | Game-server connection |
|---|---|---|
| Purpose | auth + map assignment | the session |
| Lifetime | can be dropped after step 2 | held for the whole time on that map |
| Kept open by default? | yes, see below | yes |
| Class | `Client/GatewayClient` | `Client/GameSessionClient` |

`NetworkSettings.KeepGatewayConnection` defaults to **true** even though nothing
requires it after step 2. Two reasons: eviction (`duplicate_login`) is only ever
pushed on the gateway connection, and the gateway destroys its session record
when the socket closes. Set it false and the client simply never learns it was
displaced by another login.

`Client/NetworkClient` drives both hops and is the type to inject.

## Assembly layout

| Folder | Contents | Engine-free? |
|---|---|---|
| `Protocol/` | `MsgType`, message DTOs, kick reasons | yes |
| `Json/` | minimal JSON parser and writer | yes |
| `Codec/` | encoding sniff, `IWireCodec`, the JSON codec | yes |
| `Snapshot/` | handle table, snapshot resolution | yes |
| `Transport/` | framing, `ITransport`, TCP implementation | framing/endpoint yes; TCP uses UniTask |
| `Connection/` | `WireConnection` — loops, heartbeat, close semantics | UniTask |
| `Client/` | `GatewayClient`, `GameSessionClient`, `NetworkClient`, settings | UniTask |
| `Diagnostics/` | `INetLog` and its Unity implementation | `UnityNetLog` only |
| `DI/` | `IContainerBuilder.RegisterNetworking()` | VContainer |
| `World/` | `WorldState` — adapter onto `Shared.GameLogic`'s `SnapshotMerger` | yes |
| `Bootstrap/` | `NetworkBootstrap`, its config asset, the dev JWT minter | MonoBehaviour + ScriptableObject |

"Engine-free" is load-bearing, not tidiness: `Tools/WireConformance` compiles
those folders with `dotnet` and asserts the wire format outside Unity. Run it
after any change to the codec or to handle resolution:

```bash
cd Tools/WireConformance && dotnet run     # prints ALL CHECKS PASSED, exit 0
```

> **Compiled by the Unity Editor as of 2026-08-11** (6000.3.9f1, Editor Mono).
> `NDC.Scripts.Net.dll`, `Shared.GameLogic.dll` and `NDC.Tests.EditMode.dll` all
> build clean. IL2CPP and the non-Editor platforms are still unverified — and see
> "The client and the server do not agree bit-for-bit yet" below, which is a Mono
> floating-point finding that IL2CPP may or may not share.

## Registration

```csharp
// GameLifetimeScope.Configure — root scope, so the socket survives scene loads.
builder.RegisterNetworking(new NetworkSettings
{
    GatewayHost = "127.0.0.1",
    GatewayPort = 8000,
});
```

```csharp
public sealed class Connector
{
    private readonly NetworkClient _net;

    public Connector(NetworkClient net) => _net = net;

    public async UniTask GoAsync(string jwt, CancellationToken ct)
    {
        _net.SnapshotReceived += s => { /* hand to the merge layer */ };
        _net.GatewayClosed    += i => { /* i.Cause == Kicked -> evicted */ };
        _net.SessionClosed    += i => { /* session over */ };

        await _net.ConnectAsync(jwt, "map_01", ct);
        _net.Session.SendInput(tick: 1, moveX: 1f, moveY: 0f);
    }
}
```

## Where the JWT comes from

Two paths, and it matters which one is live.

| | Host app | The samples |
|---|---|---|
| Source | an `IAuthProvider` registered in the container | `DevJwt.Sign` in `NetworkBootstrap` |
| Call | `ConnectAsync(mapId, ct)` — resolves the token itself | `ConnectAsync(jwt, mapId, ct)` |
| Secret client-side? | **no** | yes, `jwtSecret` in the config asset |
| For | shipping | local development only |

A host app implements `IAuthProvider` (`Runtime/Auth/IAuthProvider.cs`) and registers
it; `NetworkClient` picks it up and `ConnectAsync(mapId, ct)` resolves the token
through it. This project's implementation is `Scripts.Nakama.Auth.NakamaAuthProvider`,
registered by `RegisterNakama()`:

```csharp
// GameLifetimeScope.Configure
builder.RegisterNetworking();
builder.RegisterNakama();   // binds NakamaAuthProvider as IAuthProvider
```

`NetworkBootstrap` prefers the provider whenever one is present
(`NetworkClient.HasAuthProvider`) and falls back to `DevJwt` only when none is, so a
host app that has wired up real auth is never quietly authenticated by the sample's
minting. Which path ran is logged at step 1/5 — check that line before debugging an
identity problem.

> **A `[SerializeField]` secret is a development affordance, not a design.** `DevJwt`
> exists so the samples run against a local backend with one process instead of two.
> Anything shipping must use a provider: with one, no signing secret is on the client
> at all, because the token is minted server-side.

> **Injection is opt-in.** VContainer only injects components it is told about, so a
> `LifetimeScope` in the scene is not by itself enough — the scope must register the
> component (e.g. `RegisterComponentInHierarchy<NetworkBootstrap>()`) for `[Inject]`
> to run. Without that, `NetworkBootstrap` reports "no container found", builds its
> own `NetworkClient`, and falls back to `DevJwt` even though a provider is
> registered.

## Framing

`[4-byte big-endian length][body]`, body at most 1 MiB. A length of zero, a
negative one (the high bit set) or one above the cap is a protocol error, not
something to allocate for.

## Encoding: sniffed, not negotiated (ADR-9)

The body is either Protobuf or legacy JSON, and the receiver tells them apart
from the **first body byte**: `0x08` is Protobuf (proto3 always emits field 1,
`type`, which is >= 1), `0x7B` (`{`) is JSON. No version field, no handshake.

Where the latch lives, and why there are two halves of it:

- **Outbound** — one codec per `WireConnection`, fixed at construction and never
  changed. Both servers latch *their* reply encoding from the first frame we
  send, so switching ours mid-connection would silently switch theirs.
- **Inbound** — sniffed per frame, never assumed to match. Gateway eviction
  frames are written as JSON whatever the connection latched, because the
  gateway builds them off the victim connection's goroutine and may not read its
  latched encoding from there. A per-frame sniff makes that a non-event.

### Choosing the encoding

Both codecs exist. JSON is the **default** so an existing caller does not change
behaviour on upgrade; Protobuf is the backend's default and is what production
should use.

```csharp
builder.RegisterNetworking(settings, WireEncoding.Protobuf);
```

Nothing on the server changes: both servers mirror the encoding of the first frame
they receive, per connection, so a JSON and a Protobuf client can share one server.

|  | JSON | Protobuf |
|---|---|---|
| First body byte | `0x7B` (`{`) | `0x08` |
| Entity id on a delta | always sent | interned away after first mention |
| Entity kind | plain string | `type` enum, `type_name` fallback |
| Measured keyframe / delta | — | 64 B / 24 B for one entity |

Two behaviours ride along with Protobuf that JSON never exercises, and **both
present as gameplay bugs rather than codec bugs**: entity-id interning and the
entity-type enum. A green JSON certification says nothing about either. See
"Snapshots and entity-handle interning" below.

#### Regenerating the schema types

`Runtime/Protocol/Generated/Wire.cs` is generated, committed, and must never be
hand-edited. `wire.proto` in the backend repo stays the single source of truth:

```bash
# protoc nests the output under the csharp_namespace, so it lands at
# Generated/RpgMmo/Wire/V1/Wire.cs and has to be moved to the flat committed path.
# --csharp_opt=base_namespace= does NOT flatten it; the backend's generate.sh passes
# that flag and its output is nested too.
protoc --proto_path=backend/shared/proto \
       --csharp_out=Runtime/Protocol/Generated wire.proto     # libprotoc 29.3
mv Runtime/Protocol/Generated/RpgMmo/Wire/V1/Wire.cs Runtime/Protocol/Generated/Wire.cs
rm -rf Runtime/Protocol/Generated/RpgMmo
```

> **Nothing in CI diffs this file.** The backend has a `proto-generated-up-to-date` job
> over its own two copies; this third copy is regenerated by hand. A stale one decodes
> cleanly and silently ignores any field added since — no error, just a value that is
> always zero. Regenerate it in the same change that touches `wire.proto`.

Use **libprotoc 29.3** so the output is reproducible; it pairs with the vendored
`Google.Protobuf` 3.29.3, which is the version the backend's `GameServer.csproj`
pins. It is committed because Unity cannot run protoc at import time.

`Google.Protobuf.dll` is vendored at `Runtime/Plugins/` — the package's only
third-party binary. The generated code carries 322 references to that runtime and
does not stand alone, so the dependency is unavoidable rather than a preference. It
lives inside the package so the package remains importable on its own, and
`Runtime/link.xml` preserves it from IL2CPP stripping.

## Heartbeat — implemented once

Both hops ping every **10 s** and drop a peer after **30 s** without a pong, so
`WireConnection` implements it once and both use it. Ours replies to a `ping`
regardless of session state, exactly as both servers do, and the delay is
`DelayType.Realtime` so a paused or slowed game is not dropped at 30 s.

The gateway pings from the moment it accepts the socket, so a heartbeat can land
in the middle of the handshake, before the loops start. `GatewayClient` answers
those inline while waiting for the frame it came for.

## Eviction: `kick` then `disconnect` is **one** event

```
gateway -> client   kick(15)        {reason}
gateway -> client   disconnect(9)   {same reason}
                    <FIN>
```

`WireConnection` reports `DisconnectCause.Kicked` on the `kick`, sets an
`_evicted` flag, and **ignores** the `disconnect` that follows. Without that flag
every eviction is reported twice. The subsequent FIN is ignored too: `Closed` is
raised exactly once per connection, guarded by an interlocked flag, and the first
cause recorded wins.

An unpaired `disconnect` — a game-server drain (`server_shutdown`), or an
eviction from a gateway build that predates `kick` — is reported as
`DisconnectCause.ServerDisconnect` with its reason. `duplicate_login` and
`server_shutdown` are the only reasons emitted today; anything else is handled
generically.

## Join tokens are single-use

30-second TTL, one `jti` the game server consumes exactly once, and a `sid`
pinned to one server. A retry must call `enter_world` again for a **fresh**
token; replaying one is rejected with `Token already used`, which would turn a
transient failure into a permanent one. `NetworkClient` retries that way, up to
`NetworkSettings.JoinAttempts`.

## Snapshots and entity-handle interning

Snapshots are delta-encoded. `full = true` is a keyframe: the complete AOI set,
and everything not listed is discarded. `full = false` carries only changed
entities plus a `removed` list of ids.

On the Protobuf wire, entity ids are **interned**: `id` appears only on the
message introducing a `handle`, and later mentions carry the handle alone.
Handles are allocated from 1, reset at every keyframe, and never reused within an
interval. The JSON encoding never interns, so on today's JSON path every entity
carries its id — the resolver is still in the path so that turning Protobuf on
changes nothing above it.

`SnapshotResolver` implements the rules, and one of them decides the design:

> **If a handle does not resolve, do not guess.** Apply nothing from that
> snapshot and send `resync`.

Not "skip the entity", not "use the last one seen". A wrong resolution renders a
real entity in the wrong place and nothing detects it; absent state is loud and
self-repairing. A resolve failure therefore rejects the **whole** snapshot,
records no new bindings, and triggers one `resync` — repeated requests are
suppressed until a keyframe arrives, because a resync costs a full AOI snapshot.

On a keyframe the handle table is cleared **before** resolving, so a handle-only
entity on a keyframe is unresolvable rather than resolved against the previous
interval's bindings. (The Go reference implementation clears it *after*; see
below.)

`ack_tick` is surfaced on `GameSessionClient.AckTick` and on every
`ResolvedSnapshot`, monotonically — a snapshot that omits it carries zero and
must never lower it. Nothing here consumes it: it is the reconciliation anchor
for the prediction workstream.

**The merge is deliberately not implemented here.** `ResolvedSnapshot` is handed
out with ids resolved, and reconstructing world state from keyframes and deltas
is `Shared.GameLogic.Systems.SnapshotMerger`'s job — the same code the server was
diffed against. A second copy in the client is the divergence ADR-10 exists to
prevent.

## Prediction and reconciliation (0.5.0)

Local player **movement** is predicted. Nothing else is.

### The loop

```
on input:     tick it, send it, buffer it, apply it to the predicted position NOW
on snapshot:  drop inputs with tick <= AckTick
              rewind to the server's authoritative local position
              replay every buffered input that remains
```

`AckTick` is "the newest input tick the server accepted for this player". It has been
on the wire and surfaced on `WorldState` since 0.3.0; **the server needed no change for
any of this.**

### Wiring it up

```csharp
var predictor = new LocalMovePredictor(
    new PredictionSettings(tickRate: 15, speed: 5f, MapBounds.Default));

var binder = new WorldViewBinder(view, predictor);

// per input, immediately after sending it, same tick and same vector
session.SendInput(tick, moveX, moveY, attackTarget);
predictor.RecordInput(tick, moveX, moveY);
```

The binder calls `Reconcile` and `Advance` itself. Pass no predictor and the local
entity renders at the newest received position, which is 0.4.0's behaviour.

**That wiring is for views that render whatever `SetState` hands them** —
`GameObjectEntityView`, the WorldView sample, and this package's own DOTS sample, which
uses its own view rather than the adapter.

### Do NOT pass a predictor to the binder when using the `com.cuvara.dots` adapter

That adapter treats the position from `SetState` as **authoritative** and stores it in a
`ReconciliationAnchor` component — "what the server said", the value a predictor rewinds
to. Hand it a predicted position and the anchor holds a predicted value under a name that
promises authority. **Nothing detects this.** The entity renders correctly; the damage
appears only when something reads the anchor and rewinds to a position its own prediction
produced, which reads as float divergence and gets debugged as one, in the wrong package.

There, the predictor is driven from the DOTS side instead:

```csharp
// one-argument constructor — the binder must not touch the local position
var binder = new WorldViewBinder(view);

// in a DOTS system, per snapshot, for the local entity:
var anchor = em.GetComponentData<ReconciliationAnchor>(entity);

predictor.Reconcile(anchor.ServerPosition.ToVec2(), world.AckTick);  // position + tick, paired here
predictor.Advance(SystemAPI.Time.DeltaTime);

localTransform.Position = mapping.ToWorld(predictor.Position.X, predictor.Position.Y);
```

`ServerPosition` is the raw `(x, y)` the server sent, stored verbatim by the adapter before
any arithmetic. `Position` on the same component is the world-space projection, which is
what `LocalTransform` wants and **not** what the predictor wants — see below.
`ToVec2()` is `SimConversions` in `Cuvara.DOTS.GameLogic`, the one place `float2` and
`Vec2` meet.

The system claims `LocalTransform` with a `PredictedTransform` marker so the adapter stops
writing it, and **removes the marker when it stops predicting** — spectate, death, or
`IsEnabled == false` — otherwise the transform has no writer at all and the entity freezes.
That is the marker's own failure mode reached from the opposite direction, and it shows up
in a build rather than in CI.

**Why the anchor carries the server-space position at all, rather than the system
projecting the world one back.** `LocalMovePredictor` works in the server's 2D space:
`MovementSystem.TryMove` clamps to `MapBounds`, which the server expresses in its own
coordinates, and that clamp is part of the arithmetic the golden vectors pin. A float round
trip through `SnapshotSpaceMapping` is **not bit-exact**, so recovering the anchor by
inverse projection would integrate from a position the server never held — a sub-ULP error
in the one system whose entire justification is bit-exactness, and one that presents as FMA
contraction and gets debugged as such, in the wrong package. `SnapshotSpaceMapping`
deliberately has no inverse, so the round trip is out of reach rather than merely
discouraged, and there is a test on the dots side with a `1e7` origin offset asserting
`ServerPosition` survives the mapping that would visibly lose precision.

### One predictor instance, owned at the composition root

`RecordInput` is called by whatever sends input; `Reconcile` by whatever consumes snapshots.
**They must be the same object.** Two instances is silent: the recording one is never
reconciled and drifts, the reconciling one has an empty buffer, replays nothing, and returns
the authoritative position every time. Nothing throws and no counter looks wrong — prediction
simply appears to do nothing, and the search starts in the replay arithmetic, which is fine.
Register it in the DI scope and inject it into both.

### Replay runs the server's code, not a copy of it

`MovementSystem.TryMove` — the exact entry point the server's `InputHandler` calls.
It runs `ResolveDirection` then `Integrate` internally, and **skipping either is a
silent bug**:

- `Integrate` splits its multiply-add into separate float locals to deny the JIT an FMA
  contraction, which rounds once instead of twice. A hand-written
  `pos += dir * speed * dt` re-introduces exactly the divergence that split prevents,
  in the last place — it drifts rather than fails.
- `ResolveDirection` normalizes magnitudes above 1, so raw diagonal `(1,1)` moves at
  unit speed. Calling `Integrate` directly with it predicts **41% too fast**.

`LocalMovePredictorTests` asserts **exact** float equality against a reference walk
built from the same `TryMove`. A tolerance would hide the bug being guarded against.

### Refusing is a feature

`PredictionSettings` defaults nothing. Tick rate, speed and bounds must all be stated,
and unusable values produce a predictor whose `IsEnabled` is false — it predicts
nothing and the binder falls back to rendering server positions.

Prediction against a wrong speed does not fail. It produces a position wrong by a
little every tick, corrected by every snapshot, which a player reads as rubber-banding
and a developer reads as a network problem. An absence is diagnosable; an approximation
is not.

**Speed was the weak joint, and the wire now carries it.** Tick rate and bounds are
per-map constants a caller can know; speed is a per-entity server stat
(`Locomotion.Speed`), and until `wire.proto` field 9 nothing on the wire carried it, so
the client kept a hand-maintained copy of the spawn default. Anything changing a
player's speed at runtime — a buff, a mount, a slow — desynced prediction with no error
on either side (rpg-mmo-server#91).

`EntitySnapshot.Speed` and `ResolvedEntity.Speed` now carry it, on every mention
including handle-only ones.

> **`speed <= 0` means "not sent", not "immobile".** proto3 elides a zero float, so a
> server predating field 9 is indistinguishable from a stationary entity. The decode
> path deliberately does **not** substitute a default — it passes the zero through, and
> the fallback decision belongs to the prediction layer, where the configured default
> actually lives. Trusting the wire value unconditionally means an old server pins the
> predicted speed to zero and the local player stops moving.

**Consumed since 0.8.0.** `WorldState` carries speed into
`Shared.GameLogic.EntitySnapshotData` (needs `sgl-v0.1.7` or newer), the binder calls
`LocalMovePredictor.SetServerSpeed` for the local entity on every snapshot, and replay
integrates at the server's value. `PredictionSettings.Speed` is now the **fallback**: it
governs before the first snapshot and against a server predating field 9, and is
superseded as soon as a positive speed arrives. `LocalMovePredictor.EffectiveSpeed`
reports which is live.

### Rendering: the step is spread across the interval

`_predicted` only advances inside `RecordInput`, at the input rate. At 15 Hz input and a
high frame rate that is ~23 identical frames then a jump of a whole step — the avatar
arrives correctly and stutters getting there. Prediction fixed *where*, not *how often*.

`Position` walks back the unshown fraction of the latest step, so motion is continuous
between inputs. `SimulatedPosition` is the unsmoothed value and is what replay and the
server agree on bit-for-bit; rendering never perturbs it.

**Interpolation within the step, never past it.** The rendered position is bounded by a
step actually taken from an input actually submitted. When input stops the avatar arrives
at the predicted position and stops — carrying motion forward on the last direction would
move it somewhere the player never asked for, and the snap back would land exactly as they
released the key.

It costs a frame of onset, not an interval: motion begins on the frame after the input
instead of teleporting on it, so `input -> visible` is ~1 frame rather than ~0.1 ms. That
is not the round trip 0.4.0 removed.

### Corrections: smoothed small, snapped large

Threshold is **0.5 world units**, derived from the movement model rather than taste:
one tick at 5 u/s and 15 Hz is 0.33 units, so this is 1.5 ticks' worth. Below it the
error becomes a render offset decaying as `pow(base, dt)` (frame-rate independent, and
settling at exactly zero); above it the offset is dropped and the avatar snaps —
gliding in from a place the server has already ruled out is worse than one honest jump.

`LocalMovePredictor.Snaps` climbing steadily is the signal that client and server
disagree about speed, tick rate or bounds.

### Movement only, and why

Combat is **not** predicted, including in the sample, which has an HP-prediction path
that this does not touch. Cooldowns are counted in server ticks, range is checked
against positions the client only has a stale copy of, and validation can reject
outright. A predicted hit the server refuses shows damage that never happened and then
takes it back — worse than showing it late. Movement is predictable because it is a
pure function of `(position, direction, speed, dt)` computed by the same code on both
sides.

### Known divergence: superseded inputs

When two inputs reach the server inside one simulation tick, **only the newest moves
the entity** — the server refuses to let packet rate buy speed (`applyMovement: false`
for the rest). The client predicted both, so it runs one step ahead until the next
snapshot pulls it back. Bounded, self-correcting, and the reason a client's input rate
should **match** the server tick rate rather than exceed it.

## Measuring what prediction removes

`Tests/Runtime/PredictionLatencyMeasurement.cs` is a **PlayMode** test that connects to a
live backend and measures the interval prediction removes, running the same scenario with
the predictor enabled and disabled and reporting both.

```bash
# backend up first: gateway :8000, game server :9000, Nakama :7350
# then run PlayMode tests, category LiveBackend
```

Every endpoint is overridable by environment variable (`CUVARA_GATEWAY_HOST`,
`CUVARA_TICK_RATE`, `CUVARA_PLAYER_SPEED`, …) so a run can be pointed elsewhere without
editing code. Defaults match the local compose stack.

### What it measures, and what it does not

It measures **input-submitted → local avatar moves on screen**: from the frame the client
hands an input to the network layer, to the frame the view is told a changed position for
the local entity.

**It is not keypress-to-visible.** That figure includes the keyboard, the OS input stack
and the display pipeline; measuring it honestly needs external capture — a high-speed
camera or a hardware probe — and nothing inside the engine can see those legs. A number
that quietly folded them in would be a guess wearing a measurement's clothes. Those legs
are constant between the two configurations, so the **difference** the test reports is
unaffected by their absence; the absolute figures are not a player-felt latency and
should not be quoted as one.

### Why the guards matter more than the timings

A predictor that never reconciles is **indistinguishable by position alone** from one
that is perfectly accurate — both simply look right. So the test fails unless:

| Guard | What its absence would mean |
|---|---|
| `PendingCount > 0` | inputs never reached the buffer; nothing ran ahead of the server |
| `ReplayedSteps > 0` | prediction ran open-loop, never rewound to an authoritative position |
| a forced-divergence run corrects | corrections are stuck at zero regardless of disagreement |
| `EffectiveSpeed == server speed` | replay integrated at the wrong speed, so every step is wrong by the ratio |

Without those, a green result would prove only that the numbers were collected.

**A correction of `0.000` on the healthy run is not a fault.** On localhost, with no loss
and `Shared.GameLogic` bit-exact on both sides, zero divergence is the designed outcome —
it is what ADR-10, the FMA-denying split in `Integrate` and the golden vectors are for.
`ReplayedSteps` answers "is reconciliation alive?"; `LastCorrection` answers "do the two
sides disagree?", and the healthy answer to the second is *no*. An earlier version of this
harness conflated them and failed a correct run.

That is why a **third configuration deliberately diverges** — it predicts a sample input
and never sends it, so the server cannot have applied it — and asserts a correction
appears. Note a wrong *speed* would not work: the wire carries speed and the binder feeds
it to `SetServerSpeed` every snapshot, so the error is corrected away within one snapshot.
`ReconciliationDivergenceTests` pins the same four readings in EditMode, without a
backend.

### It cannot run in CI, and it does not skip

The CI job runs `testMode: EditMode` and never executes PlayMode tests, so this assembly
is **compiled** there — worth having, it catches breakage — but nothing in it runs. That
is a deliberate gap, stated rather than hidden.

**With no backend the test is IGNORED with a reason, not failed and not silently passed.**
A cheap bounded TCP probe checks the gateway and Nakama first; if either is unreachable it
calls `Assert.Ignore` naming which one and where it looked.

The distinction matters in both directions. Failing would break any consumer who runs the
whole PlayMode suite — which is what happened, because `[Category]` only helps a runner
that filters by it, and a package cannot assume that. Passing silently would be worse
still: a suite reporting success while executing nothing is the failure mode this
repository has paid for repeatedly. **An Ignore with a reason is visible in the report and
says what is missing**, which is the only honest option of the three.

## Not implemented

| | Status |
|---|---|
| KCP transport | `DefaultTransportFactory` throws rather than silently downgrading to TCP — a KCP server is not listening on TCP at all, so a fallback would surface as an unexplained connection refusal |
| WebGL | `System.Net.Sockets` is unavailable there; needs a WebSocket `ITransport`, which the gateway does not speak today either |
| Map transfer (13/14) | `transfer_map` is not sent; an inbound `transfer_map_resp` decodes to a null payload and is logged |
| Reconnect / resume | none. A closed session is reported, not retried; the server holds the entity 30 s (60 s in a dungeon) |
| Prediction of anything but movement | deliberate — see below |

Three rows were removed from this table because they had stopped being true and nothing
failed when they stopped: **"Protobuf codec — interface and sniff in place, no
implementation"** (`Runtime/Codec/ProtobufWireCodec.cs` has existed since 0.2.0 and the
DOTS sample constructs it), **"Protobuf-side world merge — only what the JSON codec
decodes"** (`WorldState.Apply` takes a `ResolvedSnapshot`, codec-agnostic by
construction), and **"Prediction, reconciliation — out of scope by design"** (0.5.0).
A "not implemented" row describing a shipped feature is the row a reader trusts, and it
costs them the feature.

## `Shared.GameLogic` — wired up

The simulation logic the client shares with the server arrives as a UPM package,
pinned to a **tag, never a branch**:

```json
"com.rpgmmo.shared-gamelogic": "https://github.com/Cuvara/rpg-mmo-server.git?path=/backend/gameserver-dotnet/Shared.GameLogic#sgl-v0.1.8"
```

`sgl-v0.1.0` resolved but produced **no assembly**. Unity treats a git package as
immutable and will not generate `.meta` files inside one, so an asmdef that ships
without its own `.meta` is never registered and its sources are silently ignored
— no error, no assembly, and a `references` entry naming it fails to resolve.
`sgl-v0.1.1` ships the 19 `.meta` files and `Shared.GameLogic.dll` now appears in
`Library/ScriptAssemblies`. **If you ever bump this package, check for the DLL,
not for a green compile** — the netcode compiled green throughout the period the
package was producing nothing.

`sgl-v0.1.3` gave `package.json` and `Shared.GameLogic.csproj` their own `.meta`
files, so the package now imports with **zero** console errors. Unity logs one for
every meta-less asset in an immutable package regardless of whether it would
import the file, so "Unity ignores it anyway" is not a reason to leave one out.

`NDC.Scripts.Net.asmdef` references `Shared.GameLogic`. The dependency is
one-way: the shared asmdef sets `noEngineReferences`, so it cannot see
`UnityEngine` and could not reference back even by accident.

### What the client uses it for

`World/WorldState` merges the snapshot stream by delegating to
`Shared.GameLogic.Systems.SnapshotMerger` — the same type the server was diffed
against. `WorldState` itself holds no merge rule; it is only the adapter from the
wire-facing `ResolvedSnapshot` to the simulation type `SnapshotData`, because the
two sit on opposite sides of the interning boundary (the shared merger keys by
real entity id and knows nothing about handles, so `SnapshotResolver` has to run
first — see disagreement 1 below).

`NetworkClient.World` is merged **before** `SnapshotReceived` fires, so a
subscriber can read either the delta it was handed or the whole reconstructed
world.

```csharp
_net.SnapshotReceived += s =>
{
    var world = _net.World;               // already merged
    if (world.TryGet(_net.UserId, out var me))
        Debug.Log($"tick {world.Tick} ack {world.AckTick} at ({me.X}, {me.Y})");
};
```

`GameConstants` and `MovementSystem.DeltaTimeForTickRate` supply the tick rate
and timestep, so no number in this repo can drift from the server's.

Still not written client-side, deliberately: movement integration, damage,
validation, cooldowns. All of it exists in the package already.

## Golden vectors — the conformance gate

`Assets/Tests/EditMode/` replays the `GoldenVectors/*.json` fixtures that ship
inside the package through `Shared.GameLogic`, and compares every float
**bit-for-bit** with `BitConverter.SingleToInt32Bits`. The server's
`GameServer.Tests/Golden/GoldenVectorTests.cs` replays the same files.

Compiling the same source is a claim about the build, not about the result. Only
these vectors show that Unity's compilation of the shared logic computes the same
numbers the server's does. A tolerance comparison would defeat the point exactly:
`0f` and `-0f`, and `NaN` and `NaN`, compare equal under any epsilon, and a
one-ULP drift per tick is invisible per frame and obvious after a minute.

The fixtures are read with the built-in `JsonUtility` — no extra package — which
is why the schema is one top-level object, one `cases` array, flat public fields.
One wrapper class per fixture, not a generic one: Unity's serializer does not
bind open generic types and returns a null array instead of failing.

Run them from the Test Runner (EditMode, assembly `NDC.Tests.EditMode`).

### The client and the server agree bit-for-bit (since `sgl-v0.1.2`)

**95 of 95 EditMode tests pass.** (The Test Runner reports `TotalTests: 96`; 95
are real tests and the extra one is a container node, which is why the pre-fix
run read 92 passed + 3 failed against the same 96.)

They did not at first. On the gate's first real run, `sgl-v0.1.1` produced three
failures:

| Test | Server fixture | Unity |
|---|---|---|
| `sqrt_irrational_small.sqrMagnitude` | `0x3DCCCCCD` | `0x3DCCCCCE` |
| `sqrt_negative_components.sqrMagnitude` | `0x4203EB84` | `0x4203EB85` |
| `clamped_asymmetric.x` | `0x3EA1E89C` | `0x3EA1E89B` |

All three traced to one expression, `x * x + y * y` — `Vec2.SqrMagnitude` and the
identical `magSq` in `MovementSystem.ResolveDirection`. C# permits a float
expression to be evaluated at higher precision (ECMA-334 §11.3.7) and the two
runtimes take different options: .NET 10's RyuJIT evaluates strictly in float32,
Unity's Editor Mono JIT keeps double-precision intermediates and rounds once at
the end. Both conform; they disagreed by one ULP. `clamped_asymmetric` was that
same ULP propagating through `MathF.Sqrt` and the divide into a position.

Fixed in `Shared.GameLogic` at `sgl-v0.1.2` by casting every intermediate back to
`float` — an explicit cast is the one construct the spec requires to round.
`MovementSystem.Integrate` got the same treatment for a related hazard the vectors
had not yet reached: `position + direction * step` is a multiply-add, and a JIT may
contract it into a single FMA that rounds once instead of twice, a third possible
answer. The server's results were unchanged by the fix, so Unity moved onto the
server's numbers rather than the reverse — the right direction, since the server
is authoritative.

### Does Unity's Mono JIT contract multiply-add into FMA? No — it widens instead

`sgl-v0.1.4` added `fma_multiply_add_discriminator`, a movement vector built to
fail if `position + direction * step` is evaluated as anything other than strict
float32. **It passes** (`0x401B4740`), and running the *unfixed* expression shape
directly under the Editor's Mono JIT settles what would happen without the fix:

| Expression, same inputs | Result |
|---|---|
| `_posX + _dirX * step` (the pre-`v0.1.2` shape) | `0x401B473F` |
| `dx = (float)(_dirX * step); (float)(_posX + dx)` (what `Integrate` does now) | `0x401B4740` |
| `(double)_posX + (double)_dirX * (double)step` | `0x401B473F` |

So the split-multiply fix is **load-bearing, not precautionary**: without it Unity
computes a different position from the server on those inputs, and the vector
would fail.

**But the mechanism is not FMA contraction — it is the same double-precision
widening that caused the original `SqrMagnitude` bug.** The two hypotheses
predict identical bits on this vector, so it cannot tell them apart. The case that
can is `sqrt_negative_components` from the original finding, where they diverge:

| | `x * x + y * y` for `(-3.3, -4.7)` |
|---|---|
| strict float32 | `0x4203EB84` |
| FMA-contracted | `0x4203EB84` |
| double intermediates | `0x4203EB85` |
| **Unity observed** | **`0x4203EB85`** |

Only the double-intermediate hypothesis predicts what Unity produced. **FMA
contraction remains unobserved in Unity's Mono JIT.** That does not weaken the
fix — splitting the multiply denies both widening and contraction, and IL2CPP or
a future runtime may well contract where Mono does not. It does mean the vector's
name oversells it: it discriminates *strict from wide*, not *fused from wide*.

Do not "simplify" those expressions back. The casts are load-bearing, and ADR-10
rule 5 was amended to cover intermediate rounding and FMA contraction explicitly
because choosing IEEE-exact *operations* turned out to be necessary but not
sufficient.

Still unverified: this was measured under **Editor Mono**. Whether IL2CPP/ARM64
agrees is untested, and IL2CPP is what ships to devices.

## Press Play: `Assets/Scenes/NetcodeBootstrap.unity`

A development harness that runs the whole core flow against a local backend and
narrates it: mint a dev JWT, gateway auth, `enter_world`, dial the assigned game
server, join, then input up and snapshots down. It draws nothing and implements
no rules.

Settings live in `Assets/Settings/NetworkBootstrapConfig.asset` — gateway host
and port, user id, HS256 secret, map id, input rate. Defaults are the backend's
own: `127.0.0.1:8000`, `dev-secret-change-me`, `map_01`, 15 Hz. **The game server
address is deliberately not configurable**: the gateway hands it back from
`enter_world`, and hardcoding it would bypass the assignment step the harness
exists to exercise (ADR-3).

`Bootstrap/DevJwt` mints the HS256 token, matching `backend/shared/jwt`'s claim
set exactly (`sub`, `iat`, `exp`; `sid`/`jti` omitted). **This is a development
shortcut and not the architecture** — in the shipped design Nakama issues the
token over HTTPS and the client never holds a signing secret. It exists so the
netcode can be exercised before any meta service is wired up.

The component prefers a `NetworkClient` injected by VContainer; with no scope in
the scene it constructs one itself and logs that it did.

### What running it actually proved

Observed end to end against the local Docker stack on 2026-08-11, gateway on
`127.0.0.1:8000`:

```
[bootstrap] step 1/5 — minting a development JWT for 'dev-player'
[bootstrap] step 2/5 — connecting to gateway 127.0.0.1:8000, map 'map_01'
[bootstrap]   → auth: sending the JWT to the gateway
[Net] authenticated with the gateway as 'dev-player'
[bootstrap] step 3/5 — enter_world 'map_01': asking the gateway which game server to dial
[Net] map 'map_01' assigned to 127.0.0.1:9200 over Tcp
[bootstrap]   → dialing the game server directly and spending the join token
[Net] joined 127.0.0.1:9200 as 'dev-player'
[bootstrap]   → join accepted
[bootstrap] step 4/5 — in world as 'dev-player'
[bootstrap] step 5/5 — streaming input at 15 Hz (dt 0.0667s)
[bootstrap] snapshot #15  tick 15391 delta    sent 15  ack 15  rtt 0ms — 1 entities (1 keyframes, 14 deltas), me=(5.81, 1.14)
[bootstrap] snapshot #60  tick 15436 delta    sent 62  ack 62  rtt 0ms — 1 entities (3 keyframes, 57 deltas), me=(9.84, 14.10)
[bootstrap] snapshot #150 tick 15526 keyframe sent 154 ack 154 rtt 8ms — 1 entities (6 keyframes, 144 deltas), me=(-7.68, 6.06)
```

Every hop works: auth, assignment, the direct dial, the join token, input up,
keyframes and deltas down, `ack_tick` tracking the input tick exactly, and the
position walking the synthetic circle.

Two client bugs had to be fixed to get here, both found by running it.

**1. A host-less `server_addr` was rejected.** The stack advertised
`GAMESERVER_PUBLIC_ADDR=":9200"` and the gateway returned it verbatim;
`NetworkEndpoint.Parse` required a host and threw
`server address ':9200' is not host:port`, one step after `enter_world`. Go's
`net.Dial` resolves such an address natively, which is why no Go-side test ever
covered it.

Where the contract actually lives was settled afterwards, and it is **not** in
the client: `GameServer/Program.cs` requires the address advertised through
`GAMESERVER_PUBLIC_ADDR` to be dialable *by the client*, and the wire protocol
specifies no format for `server_addr` at all. A server advertising a
listen-style address is therefore misconfigured, and the deploy was fixed to
advertise `127.0.0.1:9200`.

The client still normalises, as **hardening** rather than as the fix — it keeps
a misconfigured local stack usable. `NetworkEndpoint.IsListenStyleHost` covers
`""`, `"0.0.0.0"` and `"::"` (`"[::]"` arrives as `"::"` once the brackets are
stripped), matching `NormalizeDialAddr` in
`backend/smoketest/smoke/helpers.go` so both ends agree on the set. The
substitute is **the gateway host the client already reached**, not a loopback
literal: a device talking to a LAN or remote gateway must not be redirected to
its own loopback. Every rewrite logs a warning naming the misconfiguration, so
the fallback cannot hide the server-side problem it is compensating for.

**2. Input and heartbeat stalled after exactly one frame when unfocused.** The
symptom was `sent 1 ack 1 rtt 0ms` forever while snapshots kept arriving, which
reads like a broken send path. It was not: `Application.runInBackground` is off
project-wide, so an unfocused player loop stops ticking — `frameCount` stayed at
**1** across six seconds of wall clock while `Time.realtimeSinceStartup`
advanced normally. Snapshots kept coming because socket reads are on background
threads, so the session looks healthy from the outside while no input and no pong
leaves the client, and the server drops it 30 s later for no visible reason. The
bootstrap now sets `Application.runInBackground = true`.

That second one is worth generalising: **any diagnostic that only prints the
server's `ack_tick` cannot distinguish "our input is not landing" from "we are not
sending".** The snapshot log prints `sent N ack M` side by side for exactly that
reason.

**Whether the shipping player should set `runInBackground` is not decided here.**
It is off in `ProjectSettings`, which is a fine default for a single-player game
and the wrong one for a server-authoritative client with a 30 s pong timeout.
That belongs to whoever owns the player settings; the bootstrap only sets it on
itself.

## Known doc/source disagreements

Found while implementing, against `develop` of the backend repo. None of them are
worked around silently; the code follows the source.

1. **`Shared.GameLogic.Systems.SnapshotMerger` does not implement interning.**
   `gameserver-dotnet/docs/API.md` calls it the normative C#/Unity reference for
   the merge, and in the same section says a client MUST implement interning to
   read Protobuf snapshots. It keys entities by `EntitySnapshotData.Id`, and that
   type has no `handle` field at all — so a Unity client that used it against a
   Protobuf connection would key every non-introducing mention under the empty
   string. That is why resolution lives in this assembly, ahead of any merge.
2. **The keyframe clear happens in the other order in the Go reference.**
   `messages.SnapshotState.Apply` resolves handles *before* clearing the table on
   a keyframe, so a handle-only entity on a keyframe resolves against the
   previous interval's bindings — the one path by which a handle can resolve to
   the wrong entity. The C# doc's rule 4 ("clear before applying") is the safe
   one and is what this client does.
3. **`MsgKick` is not on `develop`.** The kick/disconnect pair is implemented
   only on the unmerged branch `docs/wire-protocol-accuracy-and-kick`; on
   `develop` and `main`, `kickLocalUser` sends `disconnect{duplicate_login}`
   alone, and `main`'s `shared/messages` has no type 15 at all. The client
   handles both shapes.
4. **The game-server API doc omits the heartbeat.** Its message table stops at
   type 10, but the server implements ping/pong (11/12) and its
   `HeartbeatLoopAsync` closes a connection after 30 s without a pong. A client
   built strictly from that table is dropped every 30 s with no explanation.
5. **The game server does send a reason on shutdown.** The same doc lists
   `disconnect` as `{}`, and the gateway doc says shutdown "closes sockets
   without a frame" — true of the gateway, but the game server's
   `DrainClientsAsync` sends `disconnect{reason:"server_shutdown"}` to every
   connection before tearing them down.
6. **`boss` is not an entity type.** The doc gives `entities[].type` as
   `player | npc | mob | boss`. `GameServer/Net/EntityTypes.cs` and the schema
   enum know `player`, `mob`, `npc`, `item`, `projectile` — no `boss`. (The
   string survives in a `Shared.GameLogic` comment.) A `boss` would arrive in
   `type_name`, not `type`.
