# Content Pipeline sample

Fetches the game's content set from a running game server and lists every item it got.

## What this demonstrates

Content is **not** shipped in the client build and **not** carried by the
`Shared.GameLogic` package. It lives as JSON on the game server and is served over HTTP, so
a content change is a server restart rather than a client build
([ADR-19](https://github.com/Cuvara/rpg-mmo-server/blob/develop/backend/docs/ARCHITECTURE-DECISIONS.md)).

## Running it

Start a game server with content:

```bash
cd rpg-mmo-server/backend/gameserver-dotnet
JOIN_TOKEN_SECRET=dev JWT_SECRET=dev dotnet run --project GameServer -- \
  --addr=:9000 --metrics-addr=:9100 --content-dir=../content
```

Open `Scenes/ContentPipeline.unity` and press play.

## What to watch

The chip under the URL field is the point of the scene.

| Run | Chip | What it means |
|---|---|---|
| First | **NETWORK** | Full download |
| Second | **CACHE** | Server answered `304 Not Modified` — no body crossed the wire |
| After **Clear cache** | **NETWORK** | Cache key removed, full download again |
| With no server | **LOCAL** | Offline fallback — see the caveat below |

## The offline fallback is a scene affordance, not a pattern

With no server reachable the probe loads an inline stub so the scene still shows something.
**A real client must not do this.** Content it invented is content the server never
validated, and every number in it would be a guess presented to a player as fact.

## If the UI renders unstyled after import

`UI/ContentPipelinePanel.asset` references a theme by GUID, and a theme's GUID differs per
project — Unity generates `Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss` fresh
in each one. After importing this sample, select the panel asset and assign your project's
theme if the reference came through empty.

This is a general limitation of shipping `PanelSettings` in a UPM sample, not specific to
this one.
