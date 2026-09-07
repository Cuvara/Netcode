# Reconnect Policy Demo

`NetworkClient` built through `RegisterNetworking()` in a VContainer scope, a real session
against a live backend, and three buttons that break it on purpose — so the reconnect policy
that shipped in 0.31.0 (reconnect by disconnect cause, 60 s budget, operation-generation
guard) and the DI fix in 0.31.1 can be *watched* rather than read about.

Requires `jp.hadashikick.vcontainer` (the sample assembly is excluded without it) and a
running backend (gateway + game server + Nakama).

## Run

Import the sample, open `Scenes/ReconnectPolicyDemo.unity`, press Play. Backend addresses come
from the same flags the multi-client harness passes (`Tools/run-clients.sh` in the game repo):

```
-cuvara-gateway-host -cuvara-gateway-port -cuvara-nakama-scheme -cuvara-nakama-host
-cuvara-nakama-port -cuvara-nakama-key -cuvara-map -cuvara-device -cuvara-instance
```

or the `CUVARA_*` environment variables, else the inspector defaults (localhost). The log
carries the harness markers: `[DOTSNet] Auth OK, user_id=<id>` and `[DOTSNet] IN WORLD as <id>`.

## What each button does, and what the policy must do

| Button | Mechanism | Disconnect cause | Policy | What you should see |
|---|---|---|---|---|
| **Kill transport** | `ChaosTransportFactory.KillNewest()` closes the live game-session transport | `PeerClosed` (or `TransportError`) | Reconnect | `Session closed → Reconnect`, `attempt 1/N` immediately, then backoff; `RECONNECTED after x s`; generation +1 |
| **Simulate heartbeat timeout** | `PongTimeout` → 3 s, `PingInterval` → 1 s, the session transport's reads are blackholed (writes still go, nothing comes back) | `HeartbeatTimeout` | Reconnect | `no pong for … ms` in the log within ~4 s, then the same reconnect sequence |
| **User close** | `NetworkClient.Disconnect()` | `LocalClose` | **Never** | state `Ended`, no attempt starts; the verdict turns red if one does within 5 s |
| **Connect again** | a fresh `ConnectAsync(mapId)` through the registered `IAuthProvider` | — | — | a new generation; the cached gateway JWT is reused, so no Nakama traffic |

The **elapsed vs 60 s budget** bar runs from the close the policy decided to reconnect on
(`NetworkSettings.ReconnectBudget`); `attempt n/N` and `next in x s` come from
`ReconnectProgress`; `operation generation` is `NetworkClient.Generation`.

## Why the heartbeat button starves reads rather than "stops answering pings"

The client sends the pings and the *server* answers; the client cannot stop answering
anything. What the client can do is stop *hearing*: `WireConnection` declares the link dead
when no pong has arrived within `PongTimeout`, checked every `PingInterval`. Blackholing the
transport's inbound frames while the server keeps answering is exactly a link that accepts
writes and delivers nothing — the case `PongTimeout` exists for. Both settings are live
properties on the `NetworkSettings` instance the scope registered, so the override takes
effect on the next heartbeat tick with no API added to the runtime.

## The DI path

```csharp
_scope = LifetimeScope.Create(builder =>
{
    builder.RegisterNetworking(settings);                       // the package's registration
    builder.Register<ITransportFactory>(_ => chaos, Lifetime.Singleton);   // the demo's wrapper
    builder.Register<IAuthProvider>(_ => new DelegateAuthProvider(ct => auth.GetGatewayTokenAsync(device, ct)), Lifetime.Singleton);
});
_client = _scope.Container.Resolve<NetworkClient>();
```

`IAuthProvider` is required: `NetworkClient` takes it by constructor and VContainer does not
honour C# default parameter values, and `ConnectAsync(mapId)` plus every automatic reconnect
go through it. The panel prints which `ITransportFactory` the container resolved; if it is not
the demo's wrapper, the break buttons are disabled rather than silently doing nothing.
