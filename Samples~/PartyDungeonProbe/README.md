# Party & Dungeon Probe

Dungeon entry (ADR-26) asked for by the real `NetworkClient`, against a gateway this scene
starts itself, reporting **the bytes the client actually sent**.

No backend, no Nakama, no configuration. Open the scene, press a button.

## Why this scene exists

The backend half of dungeon instancing was proven with a Go probe that speaks the wire
protocol directly. **That proves a server and proves nothing about whether the shipped client
can reach it** — and for a while it could not, because the client's wire had no `party_id` at
all. The feature was complete, deployed, verified, and unreachable by any player.

So this scene asks the question from the other end.

## The cases

| Case | What it asserts |
|---|---|
| **Map entry** | `party_id` is **absent** from the frame, not sent as `""`. The backend uses `omitempty`, and the golden vectors compare bytes across the two implementations — a client that always sends the field diverges from the Go side without failing anything. |
| **Dungeon entry** | `map_id` carries the content, `party_id` carries the party. |
| **Empty party id** | Throws **before a socket is opened**. It must not quietly fall back to a map entry: a caller that lost its party id wants to know, not to be dropped into the open world while its party is elsewhere. |
| **Reconnect** | Every attempt carries the **same** party id. |

## The case that matters is the reconnect

A dungeon player who drops must come back into the **same instance**. A reconnect that forgets
the party id asks for a map named after the dungeon content. That map does not exist, so the
rejoin fails — and if such a map ever did exist, the player would silently reappear in the open
world while their party carried on without them.

Nothing in a log distinguishes either outcome from a flaky network. That is why it is checked
here rather than assumed.

## What this does not prove

- **That a party exists**, or that Nakama's party RPCs work. Membership is checked by the
  gateway against Nakama; this scene has neither.
- **That your gateway allocates a dungeon.** The fake gateway here refuses every assignment on
  purpose — the request is the subject, so a refusal ends each case without needing a fake game
  server too.
- **Anything about the game server.** Sealing, joining and the instance's own lifecycle are
  covered elsewhere.

It proves the client asks correctly. That was the missing half.
