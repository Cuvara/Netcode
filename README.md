# Cuvara Netcode

Client-side networking module for the RPG MMO. Handles wire transport, codec, two-hop handshake (gateway → game server), snapshot resolution and world state management.

## Features

- **Wire transport** — TCP (KCP planned), 4-byte BE length-prefix framing
- **Codec** — JSON and Protobuf wire codecs, distinguished inbound by a one-byte sniff
- **Two-hop handshake** — Gateway auth → JoinToken → Game server connect
- **Protocol messages** — Auth, JoinToken, EnterWorld, Ping/Pong, Kick, Disconnect, Snapshot, Input, Resync
- **Snapshot resolution** — Entity handle table, delta resolution
- **World state** — Adapter between wire snapshots and `Shared.GameLogic` simulation types
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

Resolved automatically via `package.json`:
- **UniTask** (`com.cysharp.unitask`) — requires OpenUPM scoped registry

Must be added manually to your project's `Packages/manifest.json`:
- **VContainer** (`jp.hadashikick.vcontainer`) — DI container (OpenUPM scoped registry)
- **Shared.GameLogic** (`com.rpgmmo.shared-gamelogic`) — deterministic game logic shared with server

```json
"com.rpgmmo.shared-gamelogic": "https://github.com/Cuvara/rpg-mmo-server.git?path=/backend/gameserver-dotnet/Shared.GameLogic#sgl-v0.1.6",
"jp.hadashikick.vcontainer": "1.16.8"
```

## Samples

Both are imported from the Package Manager and both need a running backend.

| Sample | What it is |
|---|---|
| **Demo Bootstrap** | Minimal dev harness scene: press Play and the full connection flow runs against a local backend, logging every step. Mints its own development JWT from a shared secret in the config asset. |
| **E2E Certification** | Certification rig that drives the whole flow from the client with **no signing secret**: Nakama device auth, the `gateway_token` RPC, both handshake hops, the input/snapshot loop, resync, and a reconnect inside the server's 30 s entity hold. Exposes its results as static fields so they can be asserted on rather than read off the console. |

## Documentation

See `Documentation~/NETCODE.md` for architecture details, wire protocol spec, and handshake sequence.
