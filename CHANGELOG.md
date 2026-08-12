# Changelog

All notable changes to the Cuvara Netcode package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
