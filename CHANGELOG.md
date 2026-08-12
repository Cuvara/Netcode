# Changelog

All notable changes to the Cuvara Netcode package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.1] - 2026-08-12

### Fixed

- **0.2.0's samples could not compile outside this repository.** Every sample referenced
  `Scripts.Nakama` — `NakamaSessionService` / `NakamaSettings` — which lives in the host
  project's `Assets/`, not in the package. The package's `Runtime/` was and is clean, so
  installing 0.2.0 worked; it broke only on sample import, which is exactly when a new
  consumer first touches it. Affected `NetcodeE2EHarness`, `SoloVisibilityProbe`,
  `TwoClientVisibilityHarness` and `WorldViewDemo`.
  Each sample now carries a `SampleNakamaAuth` that talks to Nakama over plain HTTP with
  `UnityWebRequest` and the package's own `JsonParser` — no Nakama SDK, no new dependency,
  and nothing outside the package. It is duplicated per sample on purpose: Package Manager
  imports samples independently, so a single shared copy outside the sample folders would
  not be imported, and two copies in one namespace would collide for anyone importing
  both. Each copy sits in its own sample's namespace.
  A real application should still implement `Cuvara.Netcode.Auth.IAuthProvider` rather than
  copy this; it is a sample's shortcut, not a pattern.
- Declared `com.unity.modules.unitywebrequest` as a package dependency, which the new
  sample auth needs and which a consumer project may have stripped.

## [Unreleased]

### Added

- **Entity view layer** (`Runtime/View/`) — the world is now renderable.
  - `IEntityView` — three methods: `Spawn(id, isLocal)`, `Despawn(id)`,
    `SetState(id, x, y, hp, maxHp)`. Deliberately narrow, because the point of the seam
    is that a DOTS implementation can replace the GameObject one later without the
    netcode noticing, and a wide interface makes that swap expensive.
  - `GameObjectEntityView` — primitive capsules, green and larger for the local player,
    red for remotes. **No interpolation and no prediction, on purpose**: positions are
    applied exactly as the server sent them, so remote entities visibly step at the tick
    rate. Smoothing here would hide the tick rate and hide dropped snapshots, which are
    the two things this exists to make observable. HP shows as a vertical squash — one
    line, no UI, readable in a screenshot.
  - `WorldViewBinder` — reconciles the view against `WorldState`: present-and-unknown →
    spawn, known-and-absent → despawn, otherwise update. **Polls rather than subscribing
    to snapshots**, because GameObject APIs are main-thread only and a poll driven from
    `Update` is main-thread by construction, `WorldState` is already the merged result so
    a poll loses nothing, and reconciling against the whole world makes despawn fall out
    of absence — which is correct, since the wire does not distinguish an AOI exit from a
    true despawn. `NoteRemovedIds` optionally records ids named in `removed` so the two
    causes can be told apart in diagnostics; it is not load-bearing for the reconcile.
  - The local player is identified by comparing the entity key with `NetworkClient.UserId`
    — the key IS the Nakama user id, so this needs no extra wire field.
  - Chose GameObjects over DOTS deliberately: the project ships the DOTS packages and has
    no ECS code, and the first ECS in the codebase should not be the thing being used to
    debug netcode. If it broke, you could not tell which half broke.

- **World View sample** (`Samples~/WorldView`, displayName "World View"). One client that
  renders what it sees, plus a top-down camera built in code so the scene needs nothing
  configured by hand. Run one in a player build and one in the Editor to watch two
  clients move around each other.
  It captures its own PNG by rendering the camera to a `RenderTexture` rather than calling
  `ScreenCapture.CaptureScreenshot` — that call depends on a presenting surface, so in the
  Editor it silently wrote nothing while the log line still claimed success, from code
  identical to the player build's. Rendering explicitly behaves the same in both processes
  and fails loudly, which is what test evidence has to do.

- **Two-client visibility harness** in the E2E Certification sample
  (`Samples~/E2ECertification/Scripts/TwoClientVisibilityHarness.cs` +
  `Scenes/TwoClientVisibility.unity`). Runs two independent `NetworkClient` instances
  with two distinct Nakama identities in one play session and asserts that each one's
  `WorldState` contains the other. Every prior certification here was single-client, so
  the client had never actually resolved a remote entity — a world of one proves nothing
  about the multiplayer claim.
  The harness documents and guards three false-negative traps, each of which makes a
  working server look broken: the 50-unit area of interest (two clients driven with
  merely *similar* headings separate linearly and fall out of range — they are driven
  with identical vectors and distance is logged as evidence rather than assumed);
  per-user persisted positions (device ids are tagged per run so both users spawn
  fresh); and `NakamaSessionService`'s single PlayerPrefs session key, which would make
  both clients restore the *same* session and silently test one user against itself —
  a false pass, which is worse than a false failure.
  It also reports peak world count alongside the final one, because a run that holds two
  entities for 20 s and then drifts apart ends at one and hides its own success.

- **Two-process visibility probe** (`Samples~/E2ECertification/Scripts/SoloVisibilityProbe.cs`
  + `Scenes/SoloVisibility.unity`). One client per process, so running it in a player
  build alongside the Editor tests remote visibility across two OS processes with two
  Unity runtimes and no shared memory — the authoritative shape, where the in-process
  harness is only a proxy. Writes its findings to a file in the temp directory as well
  as the log, because a player build's console cannot be read from outside.
  Two properties of it are load-bearing, and both exist because two processes cannot be
  started at the same instant — gaps of 12 s and 29 s were measured client-side (30 s
  server-side for the latter), and 78 s in an earlier attempt:
  - It **holds position until a peer is actually in view**, then starts moving. Any
    client that moves during the join gap is already displaced when the second arrives;
    at ~5 units/s a 29-second gap is ~145 units, well past the 50-unit AOI, and the pair
    never sees each other while both are behaving correctly.
  - It then drives a **bounded oscillation** rather than a constant heading, so each
    client stays within a few units of spawn indefinitely while its position still
    changes — keeping "the peer is being updated" observable without letting the two
    drift apart. With a constant heading a 78-second offset put one client at x=353
    while the other was still at x=0.
  Together these remove launch timing from the experiment entirely, which is a property
  the in-process harness gets for free (both clients start in the same frame) and so
  could never have surfaced.

### Verified

- **Mutual visibility across two OS processes, on Protobuf.** A StandaloneWindows64
  player build and the Editor, each its own Unity runtime, two distinct Nakama users in
  `map_01`: both reported `WorldCount = 2` for the entire overlap window, each world
  containing the other's user id, and each peer's position updating at every 5-second
  sample. This is the authoritative result; the in-process harness agrees with it.
  A confirmation that fell out of it: when the Editor client exited, the player kept
  reporting the departed entity at a frozen position for the remainder of its run — the
  30-second hold seen from the outside. A held entity is indistinguishable from a live
  one that has stopped moving, which matters to whoever renders remote players.
- No static mutable state exists on the client path (`Client/`, `Connection/`,
  `Snapshot/`, `World/`, `Codec/`, `Transport/`) — every `static` is a pure method, a
  `static readonly` immutable, or a stateless helper class. That is what rules out an
  in-process test passing through shared memory rather than over the wire.
- **Mutual visibility, in-process, on Protobuf.** Two distinct users in
  `map_01`: `WorldCount == 2` on both sides, each world containing the other's user id.
  The remote entity is genuinely tracked, not merely spawned once — A's view of B
  matched B's own reported position at all six samples across 24 s. Snapshots carried
  two entities, so entity-id interning was exercised with more than one binding for the
  first time.
- **Area of interest measured at 50 units.** An earlier run separated the two clients;
  the remote entity was last visible at 50.5 units apart and absent at 62.2, matching
  the documented radius to within half a unit.
- **Entity hold measured at 30 s.** After a deliberate disconnect, the removal reached
  the other client at **30.1 s** — the documented hold, to within 0.15 s. World count
  went 2 → 1 and the departed entity left by id.

## [0.2.0] - 2026-08-12

Minor rather than patch: this adds a wire encoding, a public option, a generated-code
surface and a binary dependency. Nothing breaks — `ConnectAsync(jwt, mapId, ct)` is
untouched and JSON stays the default — but none of that is a patch.

### Added

- **Protobuf wire codec** (`Runtime/Codec/ProtobufWireCodec.cs`), the backend's default
  encoding. Selected via `RegisterNetworking(settings, WireEncoding.Protobuf)`; JSON
  remains the default so an existing caller's behaviour does not change on upgrade.
  Both servers mirror the encoding of the first frame per connection, so this is a
  client-side choice needing no server change.
  - Rejects `MsgType` 0 at **both** ends. proto3 omits a zero field 1, so a type-0
    envelope would not begin with `0x08` — the byte the peer sniffs to tell Protobuf
    from JSON's `{` — and the frame would be parsed as the wrong encoding entirely.
    Decoding rejects it too: a body starting `0x12` is valid Protobuf carrying only
    field 2 with the type defaulted to 0, so arbitrary bytes can otherwise "decode"
    successfully.
  - Entity kind reads the `type` enum first and falls back to `type_name`;
    `ENTITY_TYPE_UNSPECIFIED` means "see `type_name`", not "unknown". Reading only one
    half silently loses either every known kind or every future one.
- **`Google.Protobuf` 3.29.3**, vendored at `Runtime/Plugins/Google.Protobuf.dll`.
  **The package's first third-party binary.** It is unavoidable rather than chosen:
  the code generated from `wire.proto` carries 322 references to the Protobuf runtime
  and does not stand alone, and hand-writing the types would create a second
  definition of a schema that already exists. Vendored inside the package rather than
  in `Assets/` so the package stays importable on its own. The version deliberately
  matches the backend's pin (`GameServer.csproj`) and the generator used.
- **Generated schema types** at `Runtime/Protocol/Generated/Wire.cs`, namespace
  `RpgMmo.Wire.V1`. Regenerate identically with:
  `protoc --proto_path=backend/shared/proto --csharp_out=Runtime/Protocol/Generated wire.proto`
  using **libprotoc 29.3**. Committed because Unity cannot run protoc at import;
  `wire.proto` remains the single source, so this is one definition, not two.
- **`Runtime/link.xml`** preserving `Google.Protobuf` and `RpgMmo.Wire.V1` from IL2CPP
  managed-code stripping. Protobuf registers message types through static parsers and
  reaches properties reflectively, which the stripper cannot see. The failure would be
  runtime-only in a player while the Editor — which does not strip — stayed green.
- 13 interning tests (`Tests/Editor/SnapshotResolverInterningTests.cs`) covering the
  branches only a Protobuf connection can reach: unknown handle on a delta, bare
  handle on a keyframe, an aborted snapshot leaving the table untouched, handle
  rebinding across a keyframe including a double rebind, removals not releasing a
  binding, and both zero sentinels.

### Fixed

- `WireConnection` threw `"received a Protobuf frame, which this client cannot decode
  yet"` on any inbound Protobuf frame. Inbound is sniffed per frame rather than assumed
  to mirror the outbound codec, so **both** codecs are now held ready: JSON because the
  gateway writes eviction frames as JSON whatever the connection latched, and Protobuf
  because it is now implemented.
- `SnapshotResolver` cleared the handle table *before* resolving on a keyframe. That
  mutated state before validating it, so a malformed keyframe wiped the table and then
  aborted, leaving the client with no bindings and an empty world until a resync
  completed. Resolution now runs first and the clear happens only once every entity has
  resolved, restoring the all-or-nothing guarantee. A keyframe carrying a bare handle is
  rejected **without consulting the table** — the previous interval's binding for that
  number belongs to a different entity, so a successful lookup is the dangerous
  outcome, not the safe one.

### Verified

- Certified against a live Nakama + gateway + game server stack on **both encodings**,
  70 s per run so the server's 10 s ping / 30 s pong-timeout heartbeat is actually
  exercised — every earlier run sat inside that window and proved nothing about it.
  Protobuf: 881 snapshots, 851 deltas. JSON: 1029 snapshots, 995 deltas. Both forced a
  keyframe on resync, reconnected inside the 30 s entity hold with position preserved,
  and finished with zero errors. The 851 Protobuf deltas are the interning coverage JSON
  structurally cannot provide.
- **IL2CPP with managed stripping at High**, Android, arm64: `Google.Protobuf.dll`
  survives the stripping pass and reaches IL2CPP conversion; 2137 objects compiled and
  the build succeeded. This is what `link.xml` exists to guarantee and the one thing an
  Editor (Mono) run can never show. Note the project's Android default is stripping
  *Minimal*, the least aggressive level, so the test was run at High deliberately —
  a green build at Minimal would be weaker evidence than it looks.

## [0.1.2] - 2026-08-12

### Added

- **E2E Certification sample** (`Samples~/E2ECertification`, displayName "E2E Certification").
  A client-driven certification rig that drives the whole flow from inside Unity with
  no pasted token and no signing secret: Nakama device auth, the `gateway_token` RPC,
  both handshake hops, the input/snapshot loop, `RequestResync`, and a reconnect inside
  the server's 30 s entity hold. Results are exposed as static fields so an automated
  harness can assert on them without scraping the console.
  Shipped as a second sample rather than folded into Demo Bootstrap: the two want
  different scene setups, and merging them would make the minimal "does it connect"
  demo ship with its `NetworkBootstrap` disabled.

- `NetworkClient.HasAuthProvider` — reports whether an `IAuthProvider` was supplied,
  so a caller can choose the real auth path when one is wired up and fall back to a
  development credential when it is not, without throwing to find out which it is.

### Fixed

- `NetworkBootstrap` leaked a project-wide setting. It set
  `Application.runInBackground = true` and never put it back; in the Editor that setter
  writes through to `PlayerSettings` and survives play mode, so merely running the
  sample permanently rewrote `ProjectSettings.asset` in whatever project imported it
  (it surfaced as an unexplained `runInBackground: 0 -> 1` diff). The previous value is
  now captured and restored in `OnDestroy`. The override itself is unchanged and still
  applies for the whole session — it is load-bearing, because an unfocused Editor stops
  ticking the player loop and would silently stop sending input and heartbeats while
  snapshots kept arriving. Restored in `OnDestroy` rather than `OnDisable` on purpose:
  disabling the component does not end the session, and restoring the flag mid-session
  would cause the exact stall the override prevents.

- `NetworkBootstrap` never used `IAuthProvider`. It minted a development JWT via
  `DevJwt.Sign` unconditionally, which meant `NetworkClient.ConnectAsync(mapId, ct)`
  — the DI overload — was dead code, and a host app that had correctly registered a
  provider was still silently authenticated by the sample's local minting. It now
  resolves the token through the registered provider when the container supplies one.
  `DevJwt` remains the fallback when no provider is present, so the sample still runs
  with zero DI setup, and the chosen path is logged so the live identity source is
  never ambiguous. The connect-failure hint is now specific to the path in use rather
  than always blaming `JWT_SECRET`.

- Demo Bootstrap sample: `NetworkBootstrapConfig.asset` shipped `gatewayPort: 8100`,
  overriding the `8000` default in `NetworkBootstrapConfig.cs` and contradicting the
  class documentation. Importing the sample and pressing Play failed with
  `dial 127.0.0.1:8100 failed: ... actively refused it` against a default backend.
  The serialized asset now matches the code default.

### Changed

- `NetworkEndpoint.Parse` now recognises every listen-style host a server may
  advertise but no client can dial — `""`, `"0.0.0.0"` and `"::"` (`"[::]"` reduces
  to `"::"` once brackets are stripped) — via the new public
  `NetworkEndpoint.IsListenStyleHost`. This matches `NormalizeDialAddr` in
  `backend/smoketest/smoke/helpers.go` so both ends agree on the set. Previously only
  a completely empty host was handled.
- The substituted host is now **the gateway host the client already reached** rather
  than a hardcoded loopback, via the new
  `NetworkEndpoint.Parse(address, fallbackHost, out bool normalised)` overload. A
  device talking to a LAN or remote gateway must not be redirected to its own
  loopback. The single-argument `Parse` overload is unchanged and still falls back to
  `DefaultHost`.
- `GatewayClient.EnterWorldAsync` logs a warning naming the misconfiguration whenever
  the address is rewritten, so this fallback cannot silently mask a server that
  advertises an undialable `GAMESERVER_PUBLIC_ADDR`.

  This normalisation is **hardening, not the contract**. The contract is the
  server's: `GameServer/Program.cs` requires the advertised address to be dialable by
  the client, and the wire protocol specifies no format for `server_addr`.

## [0.1.1] - 2026-08-11

### Changed

- Migrate `Shared.GameLogic` git dependency URL from `dyCuong03/rpg-mmo-server` to `Cuvara/rpg-mmo-server`
- Bump `Shared.GameLogic` to `sgl-v0.1.6`
- CI test project updated to match new dependency URL

## [0.1.0] - 2026-08-11

### Added

- TCP wire transport with 4-byte big-endian length-prefix framing
- JSON wire codec with encoding sniffing (Protobuf-ready)
- Two-hop handshake flow: Gateway auth → JoinToken → Game server connect
- Full protocol message set: Auth, JoinToken, EnterWorld, Ping/Pong, Kick, Disconnect, Snapshot, Input, Resync
- `NetworkClient` facade orchestrating the gateway → game server flow
- `GatewayClient` for gateway authentication and join-token acquisition
- `GameSessionClient` for game server session management and input/snapshot streaming
- `WireConnection` managing framed, codec-aware TCP connections
- Snapshot resolution pipeline: `SnapshotResolver`, `EntityHandleTable`, `ResolvedSnapshot`
- `WorldState` adapter bridging wire snapshots to `Shared.GameLogic.SnapshotData`
- VContainer DI registration via `NetworkingRegistration.RegisterNetworking()`
- `NetworkBootstrap` dev harness MonoBehaviour (in Demo Bootstrap sample)
- `NetworkBootstrapConfig` ScriptableObject for dev configuration
- Dev JWT minting (`DevJwt`) for local backend testing
- Golden vector conformance tests against `Shared.GameLogic`
- `WorldState` and `NetworkEndpoint` unit tests
- Wire conformance tool (`Tools/WireConformance/`)
- Package extracted from `Assets/Scripts/Net/` into standalone UPM package
