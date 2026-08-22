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
scoped registry (it lives there, not on Unity's registry):

- **UniTask** (`com.cysharp.unitask`) — required; used throughout the transport

**Optional**, and not declared, as of 0.6.0:

- **VContainer** (`jp.hadashikick.vcontainer`) — enables `Cuvara.Netcode.DI`
  (`NetworkingRegistration`) and `Cuvara.Netcode.Bootstrap` (`NetworkBootstrap`). Without
  it those two assemblies are excluded by a `versionDefines` constraint and the rest of the
  package compiles and works unchanged. DI registration is a convenience; the transport is
  the product. Add it to your manifest if you want them.

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
"com.rpgmmo.shared-gamelogic": "https://github.com/Cuvara/rpg-mmo-server.git?path=/backend/gameserver-dotnet/Shared.GameLogic#sgl-v0.1.9"
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
| **DOTS Sample** | The full client presented with DOTS/ECS: auth, both handshake hops, replicated entities, combat, economy, a map selector and a HUD. **WASD moves the local player**, with prediction on by default — this is the one to press Play on to judge how movement feels. A built player can be aimed at any backend with `-cuvara-gateway-host` / `-cuvara-nakama-host` (or `CUVARA_*` environment variables), and several instances authenticate as several distinct Nakama users — see "Pointing a build at a backend" in `Documentation~/NETCODE.md`. |
| **E2E Certification** | Certification rig that drives the whole flow from the client with **no signing secret**: Nakama device auth, the `gateway_token` RPC, both handshake hops, the input/snapshot loop, resync, and a reconnect inside the server's 30 s entity hold. Exposes its results as static fields so they can be asserted on rather than read off the console. |

## Documentation

See `Documentation~/NETCODE.md` for architecture details, wire protocol spec, and handshake sequence.

## Branching and releases

**`develop` is the integration branch.** Pull requests target it, release tags are cut on
it, and the tags the Unity client pins point at commits reachable from it.

`main` still exists and CI still builds a push to it, but nothing targets it by default.

### Cutting a release

1. Land the work on `develop`.
2. Bump `version` in `package.json` **in the commit the tag will point at** — the release
   workflow refuses to publish when `package.json` and the tag disagree.
3. Add the matching `## [x.y.z]` heading to `CHANGELOG.md`; the workflow extracts the
   release notes from it by heading.
4. Tag `vx.y.z` on `develop` and push the tag.

`release.yml` triggers on the **tag**, not on a branch, so a tag cut anywhere runs it. The
branch matters for where the work lives, not for whether the release fires.

`release-reminder.yml` watches `develop` and says so when the version there has no tag yet.
It never tags and never publishes: pushing a `v*` tag is the last gate before `npm publish`,
which cannot be undone — a bad version can only be superseded, never withdrawn.

### `main` syncs itself

`sync-main.yml` runs on the tag push and opens a pull request moving `main` to the tagged
commit, set to auto-merge. Nothing to remember and nothing to do by hand.

It opens a PR rather than pushing because `main` requires one plus four passing checks, and
a workflow that bypassed that would be quietly removing the gate from the branch other
people read. When the tag is already reachable from `main` it does nothing and says so; when
the move would not be a fast-forward it opens the PR anyway and warns, rather than choosing
for you.

`workflow_dispatch` takes a tag, for when a tag was pushed while the workflow was broken or
a sync PR was closed.

### Why this is written down

`develop` fell two releases behind `main` (`v0.16.3` and `v0.17.0` were both tagged on
`main`) because the reminder watched a branch nothing was merging into, so nothing noticed.
Anyone branching from `develop` started without those releases. One integration branch, with
the reminder pointed at it, is what stops that recurring.
