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
```

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
  PROJECTILE = 5;    // reserved
}
```

The enum costs 2 bytes vs 8+ for a string type. When `type` is `UNSPECIFIED`,
the `type_name` string field is the fallback — forward compatibility for kinds
this schema does not enumerate yet.

## Kick reasons

`KICK` messages carry a `reason` string:

| Reason | Meaning |
|--------|---------|
| `server_shutdown` | Server is shutting down gracefully |
| `session_superseded` | Another login replaced this session |
| `capacity` | Server is full |
