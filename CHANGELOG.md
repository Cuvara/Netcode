# Changelog

All notable changes to the Cuvara Netcode package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
