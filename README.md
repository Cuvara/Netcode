# Cuvara Netcode

Client-side networking module for the RPG MMO. Handles wire transport, codec, two-hop handshake (gateway → game server), snapshot resolution and world state management.

## Features

- **Wire transport** — TCP (KCP planned), 4-byte BE length-prefix framing
- **Codec** — JSON and Protobuf wire codecs, distinguished inbound by a one-byte sniff
- **Two-hop handshake** — Gateway auth → JoinToken → Game server connect
- **Protocol messages** — Auth, JoinToken, EnterWorld, Ping/Pong, Kick, Disconnect, Snapshot, Input, Resync
- **Snapshot resolution** — Entity handle table, delta resolution
- **World state** — Adapter between wire snapshots and `Shared.GameLogic` simulation types
- **Prediction** — Local player movement predicted on input and reconciled against the server's `AckTick`, replaying through `Shared.GameLogic` so client and server agree bit-for-bit. Refuses to run rather than approximate when it cannot match the server. Movement only — combat stays server-authoritative
- **VContainer DI** — One-line registration via `NetworkingRegistration.RegisterNetworking()`
- **Two wire encodings** — Protobuf (the backend default, with entity-id interning and the entity-type enum) and legacy JSON. JSON is the registration default so upgrades do not change behaviour; pass `WireEncoding.Protobuf` to opt in. Ships one vendored third-party binary, `Google.Protobuf` — see `Documentation~/NETCODE.md` for why it is unavoidable.

## Installation

### Embedded (recommended for development)

Already embedded in `Packages/com.cuvara.netcode/`.

### Git URL

```json
"com.cuvara.netcode": "https://github.com/Cuvara/Netcode.git#v0.1.0"
```

## Dependencies

`Cuvara.Netcode.Runtime` references three assemblies — `UniTask`, `VContainer` and
`Shared.GameLogic` — and all three are required. There is no configuration in which the
package compiles without them.

**Resolved automatically via `package.json`**, provided your project has the OpenUPM
scoped registry (both live there, not on Unity's registry):

- **UniTask** (`com.cysharp.unitask`)
- **VContainer** (`jp.hadashikick.vcontainer`)

```json
"scopedRegistries": [
  { "name": "OpenUPM", "url": "https://package.openupm.com",
    "scopes": ["com.cysharp", "jp.hadashikick"] }
]
```

Without that registry these fail to *resolve*, which at least names the missing package.
Before 0.4.1 VContainer was undeclared, so the same project instead failed to *compile*
with `CS0246: The type or namespace name 'VContainer' could not be found`.

**Must be added manually to your project's `Packages/manifest.json`:**

- **Shared.GameLogic** (`com.rpgmmo.shared-gamelogic`) — deterministic game logic shared
  with the server.

```json
"com.rpgmmo.shared-gamelogic": "https://github.com/Cuvara/rpg-mmo-server.git?path=/backend/gameserver-dotnet/Shared.GameLogic#sgl-v0.1.6"
```

This one cannot be declared by the package. A UPM package's `dependencies` accepts
registry version ranges only — a git URL is valid in a *project* manifest and nowhere
else — and this is a git subpath, not a published package. `package.json` records it
under **`x-manualDependencies`**, which is informational: the `x-` prefix marks it as not
a UPM key, because nothing resolves it. It was previously called `gitDependencies`, which
read like a declaration Unity would honour and is not.

## Samples

All four are imported from the Package Manager and all four need a running backend.

| Sample | What it is |
|---|---|
| **Demo Bootstrap** | Minimal dev harness scene: press Play and the full connection flow runs against a local backend, logging every step. Mints its own development JWT from a shared secret in the config asset. |
| **World View** | Renders replicated entities as primitive GameObjects so the world can be looked at rather than read from logs. Run one in a player build and one in the Editor to see two clients move around each other. |
| **DOTS Sample** | The full client presented with DOTS/ECS: auth, both handshake hops, replicated entities, combat, economy, a map selector and a HUD. **WASD moves the local player**, with prediction on by default — this is the one to press Play on to judge how movement feels. |
| **E2E Certification** | Certification rig that drives the whole flow from the client with **no signing secret**: Nakama device auth, the `gateway_token` RPC, both handshake hops, the input/snapshot loop, resync, and a reconnect inside the server's 30 s entity hold. Exposes its results as static fields so they can be asserted on rather than read off the console. |

## Documentation

See `Documentation~/NETCODE.md` for architecture details, wire protocol spec, and handshake sequence.
