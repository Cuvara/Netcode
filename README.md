# Cuvara Netcode

Client-side networking module for the RPG MMO. Handles wire transport, codec, two-hop handshake (gateway → game server), snapshot resolution and world state management.

## Features

- **Wire transport** — TCP (KCP planned), 4-byte BE length-prefix framing
- **Codec** — JSON wire codec with encoding sniffing (Protobuf planned)
- **Two-hop handshake** — Gateway auth → JoinToken → Game server connect
- **Protocol messages** — Auth, JoinToken, EnterWorld, Ping/Pong, Kick, Disconnect, Snapshot, Input, Resync
- **Snapshot resolution** — Entity handle table, delta resolution
- **World state** — Adapter between wire snapshots and `Shared.GameLogic` simulation types
- **VContainer DI** — One-line registration via `NetworkingRegistration.RegisterNetworking()`

## Installation

### Embedded (recommended for development)

Already embedded in `Packages/com.cuvara.netcode/`.

### Git URL

```json
"com.cuvara.netcode": "https://github.com/Cuvara/Netcode.git#v0.1.0"
```

## Dependencies

- **UniTask** (`com.cysharp.unitask`)
- **VContainer** — DI container (referenced by asmdef, not in package.json to avoid version conflicts)
- **Shared.GameLogic** (`com.rpgmmo.shared-gamelogic`) — deterministic game logic shared with server

## Demo

Import the **Demo Bootstrap** sample from the Package Manager to get a dev harness scene that runs the full connection flow against a local backend.

## Documentation

See `Documentation~/NETCODE.md` for architecture details, wire protocol spec, and handshake sequence.
