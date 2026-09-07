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
| **Simulate heartbeat timeout** | `PongTimeout` → 3 s, `PingInterval` → 1 s (**sticky** — see below), the session transport's reads are blackholed (writes still go, nothing comes back) | `HeartbeatTimeout` | Reconnect | the header's `PongTimeout`/`PingInterval` change to `3 s`/`1 s` on the next frame and the line gains `(heartbeat override, sticky)`; `no pong for … ms` in the log within ~4 s, then the same reconnect sequence |
| **User close** | `NetworkClient.Disconnect()` | `LocalClose` | **Never** | state `Ended`, no attempt starts; the verdict turns red if one does within 5 s |
| **Connect again** | a fresh `ConnectAsync(mapId)` through the registered `IAuthProvider` | — | — | a new generation; the cached gateway JWT is reused, so no Nakama traffic |

The **elapsed vs 60 s budget** bar and the `Reconnected in x s` verdict both run from the
close the policy decided to reconnect on — not from the attempt that eventually succeeded —
and are measured on `NetworkSettings.MonotonicClock`, the same clock the client budgets with
(`NetworkSettings.ReconnectBudget`). `attempt n/N` and `next in x s` come from
`ReconnectProgress`; `operation generation` is `NetworkClient.Generation`.

The header's `PongTimeout` and `PingInterval` are re-read from the live `NetworkSettings`
instance every frame, so the heartbeat button's override shows up immediately. It is
**sticky**: the demo never restores the defaults, because the shortened values are what make
a *later* starved heartbeat detectable in seconds too. Press Play again for 30 s / 2 s.

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
    // The chaos factory goes IN to RegisterNetworking, not next to it.
    builder.RegisterNetworking(settings, transports: chaos);
    builder.Register<IAuthProvider>(_ => new DelegateAuthProvider(ct => auth.GetGatewayTokenAsync(device, ct)), Lifetime.Singleton);
});
_client = _scope.Container.Resolve<NetworkClient>();
```

**Do not register `ITransportFactory` again after `RegisterNetworking()`.** It is not an
override and it does not lose quietly — it takes the whole container down at
`LifetimeScope.Awake()`:

```
VContainerException: Conflict implementation type : Registration ITransportFactory
  ContractTypes=[] Singleton VContainer.Internal.FuncInstanceProvider
```

Since 0.31.1 the package registers the default factory through a factory lambda (VContainer
does not honour `DefaultTransportFactory`'s `string transportKey = null` default), and two
lambda registrations of one interface share the implementation type `FuncInstanceProvider`,
which is what VContainer's duplicate check rejects. This is exactly how this scene died in a
player against the live backend on 2026-09-07. The `transports:` parameter (and the matching
`codec:` / `log:` ones) is the supported substitution point, and it keeps exactly one
registration of the interface in the scope.

`IAuthProvider` is a different case and is registered separately on purpose: the package
registers none, and `NetworkClient`'s `IAuthProvider auth = null` constructor default is not
honoured by VContainer, while `ConnectAsync(mapId)` and every automatic reconnect need one.
A single registration of an interface the package never registers is safe.

The panel prints which `ITransportFactory` the container resolved; if it is not the demo's
wrapper, the break buttons are disabled rather than silently doing nothing. If the scope fails
to build at all, `Start()` catches it, logs `[DOTSNet] FATAL: the demo scope failed to start: …`
and leaves the panel showing the error with every button dead — rather than a
`NullReferenceException` per frame out of `Update()`.
