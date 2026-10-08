# Wire Protocol

The realtime wire protocol shared by the Go gateway, the C# game server, and the
Unity client. Defined in `shared/proto/wire.proto` (single source of truth).

## Framing

Every message travels as:

```
[4-byte big-endian length][Envelope bytes]
```

The framing is identical for both Protobuf and JSON encodings, so the transport
layer (TCP or KCP) is unaffected by the encoding choice.

## Encoding: Protobuf + JSON dual-stack

The protocol supports two encodings, distinguished by the first byte of the body:

| First byte | Encoding | Notes |
|-----------|----------|-------|
| `0x08` | Protobuf | Envelope.type is field 1, always >= 1, so tag byte is always 0x08 |
| `0x7B` (`{`) | JSON | A JSON object always starts with `{` |

These cannot collide, so a peer identifies the encoding from the first body byte
alone — no version negotiation, no extra handshake round trip (ADR-9).

**Protobuf is the primary encoding** (81% smaller than JSON). Legacy JSON is still
accepted for backwards compatibility.

## Envelope

```protobuf
message Envelope {
  uint32 type = 1;    // MsgType enum
  bytes payload = 2;  // Opaque — decoded per type
}
```

`payload` stays opaque bytes (not a oneof) so routing and payload decoding remain
separable — a proxy or relay can dispatch on `type` without linking every payload
schema.

## Message types

```
MsgType         Direction              Purpose
────────────────────────────────────────────────────────────────
AUTH            client → gateway       JWT authentication
AUTH_RESP       gateway → client       Auth result + user_id
ENTER_WORLD     client → gateway       Request map assignment
ENTER_WORLD_RESP gateway → client      {ServerAddr, JoinToken, Transport}
JOIN_TOKEN      client → gameserver    Authenticate with game server
JOIN_TOKEN_RESP gameserver → client    Join result + tick_rate
INPUT           client → gameserver    Per-tick player input
SNAPSHOT        gameserver → client    Per-tick world state (delta/keyframe)
DISCONNECT      either direction       Graceful disconnect
RESYNC          client → gameserver    Request a keyframe
PING            either direction       Heartbeat
PONG            either direction       Heartbeat reply
KICK            server → client        Forced disconnect with reason
TRANSFER_MAP    client → gameserver    Request map transfer
TRANSFER_MAP_RESP gameserver → client  Transfer result
SEALED_CLIENT_HELLO client → gameserver Sealed-session handshake (Protobuf only)
SEALED_SERVER_HELLO gameserver → client Sealed-session handshake (Protobuf only)
COMMAND (32)    client → gameserver    {seq, opcode, payload} — protocol 3
COMMAND_RESULT (33) gameserver → client {seq, ok, error, payload} — protocol 3
SERVER_PUSH (34) gameserver → client   {opcode, payload} — protocol 3
```

18-31 stay reserved for the gateway hop's handshake.

**Numeric values are FROZEN.** Never renumber; only append.

## Connection flow

```
Client ──AUTH──→ Gateway
Client ←AUTH_RESP── Gateway         (OK, user_id)
Client ──ENTER_WORLD──→ Gateway     (map_id)
Client ←ENTER_WORLD_RESP── Gateway  (server_addr, join_token, transport)
  ── client opens second connection to game server ──
Client ──JOIN_TOKEN──→ GameServer   (token)
Client ←JOIN_TOKEN_RESP── GameServer (OK, tick_rate)
  ── gameplay loop ──
Client ──INPUT──→ GameServer        (tick, moveX, moveY, attackTargetId)
Client ←SNAPSHOT── GameServer       (tick, ackTick, full, entities[], removed[])
```

## Snapshots: delta encoding

Snapshots are either **keyframes** (`full=true`) or **deltas** (`full=false`):

| Type | `entities[]` contains | `removed[]` contains |
|------|----------------------|---------------------|
| Keyframe | Complete AOI set | Empty |
| Delta | Only changed entities | IDs that left AOI/world |

Keyframe schedule: on join, on `RESYNC`, every N snapshots (default 30).

`AckTick` is the newest client input tick the server accepted — the reconciliation
anchor for client-side prediction.

`AckAppliedTick` (field 7, `ack_applied_tick`, 0.46.1) is the SERVER tick on which that input
was applied -- the tick whose input drain accepted it. `AckTick` counts on the client's tick line
and `Tick` on the server's; this pairs them, so the prediction layer can compare a snapshot with
the history entry it actually describes (`Documentation~/PREDICTION.md`). Sent to protocol 3
peers only; zero means "not sent", and both codecs decode it (`ack_applied_tick` in JSON). No
protocol version bump: it is additive and ignoring it is always safe.

## Entity interning

Entity IDs are **interned** to reduce wire cost:

1. On a keyframe, each entity is sent with its full `id` string and assigned a
   numeric `handle` (1-based, varint-encoded)
2. On subsequent deltas, only the `handle` is sent (1-2 bytes vs ~17 bytes for an id)
3. Handles **reset at every keyframe** — this bounds how long a disagreement persists
4. Handles are **never reused within an interval** — a missed despawn produces absent
   state rather than wrong state

If a receiver sees a handle it has no binding for, it **must not guess** — it has
lost state and must request a keyframe (`RESYNC`).

## EntityType enum

```protobuf
enum EntityType {
  UNSPECIFIED = 0;   // see type_name string fallback
  PLAYER     = 1;
  MOB        = 2;
  NPC        = 3;    // reserved, not yet produced
  ITEM       = 4;    // reserved
  PROJECTILE = 5;    // produced from protocol 3 (ADR-29)
}
```

The enum costs 2 bytes vs 8+ for a string type. When `type` is `UNSPECIFIED`,
the `type_name` string field is the fallback — forward compatibility for kinds
this schema does not enumerate yet.

## Protocol version 3 (Netcode 0.46.0)

`WireProtocolVersion.Current = 3` (ADR-28..31). A version 3 server keeps serving version 2 peers
the version 2 shape, and this client accepts a server echoing 2 or 3 (or nothing); every v3
feature is gated on the game server's echoed `protocol_version` being at least 3.

| Area | Wire | Netcode surface |
|---|---|---|
| 3D (ADR-28) | `EntitySnapshot.z` (14), `vel_x/y/z` (15-17); `InputMessage.aim_z` (9), `jump` (11) | `EntitySnapshot.Z/VelX/VelY/VelZ`, `ResolvedEntity.Z/...`, `InputMessage.AimZ/Jump` |
| Projectiles (ADR-29) | `EntitySnapshot.owner` (18, handle) / `owner_id` (24, JSON), `spawn_seq` (19); `InputMessage.spawn_seq` (12), `render_tick` (10), `render_alpha` (13) | `ResolvedEntity.OwnerId/SpawnSeq`, `InputMessage.SpawnSeq`, `InputMessage.SetRenderTime`, `ProjectilePredictor` |
| Stat block / statuses (ADR-30) | `stats` (20), `stats_removed` (21), `statuses` (22), `statuses_removed` (23); `StatValue`, `StatusEffect` | `ResolvedEntity.Stats/Statuses` (SGL `StatValueData`/`StatusEffectData`), merged by `SnapshotMerger` |
| Events | `GameEvent.effect_id` (9); types `STATUS_APPLIED` (7), `STATUS_REMOVED` (8), `PROJECTILE_HIT` (9) | `ResolvedGameEvent.EffectId`, `GameEventType` |
| Commands (ADR-30) | `CommandRequest`, `CommandResult`, `ServerPush` (MsgType 32-34) | `SendCommandAsync`, `CommandResultReceived`, `ServerPushReceived` |
| Characters (ADR-31) | `EnterWorldRequest.character_id` (3), `JoinTokenResponse.character_id` (6) | `NetworkClient.CharacterId`, `ActiveCharacterId` |

`changed_fields` bits added in version 3: `0x0200` z, `0x0400` velocity (all three axes),
`0x0800` owner / owner_id / spawn_seq, `0x1000` stats (+ removed), `0x2000` statuses (+ removed).
On a delta with `0x1000`/`0x2000` the lists carry only changed entries; ids in the `_removed`
list are dropped and everything else keeps its last-known value.

Legacy JSON uses the same snake_case names as `wire.proto` (and the Go struct tags). `owner_id`
is the JSON twin of the interned `owner` handle; `payload` is padded standard base64. Every v3
field is omitted when zero, so a peer using none of them produces version 2 bytes.

See `NETCODE.md` → "Wire protocol version 3" for the client API and `PREDICTION.md` → "3D
movement" for prediction.

## Kick reasons

`KICK` messages carry a `reason` string:

| Reason | Meaning |
|--------|---------|
| `server_shutdown` | Server is shutting down gracefully |
| `session_superseded` | Another login replaced this session |
| `capacity` | Server is full |
