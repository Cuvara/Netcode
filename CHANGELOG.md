# Changelog

All notable changes to the Cuvara Netcode package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.6.1] - 2026-08-14

Documentation only. No behaviour change, no API change — but the thing being documented
is a way to use the prediction shipped in 0.5.0 that is wrong and produces no symptom, so
it is worth a release rather than a comment.

### Documentation

- **`WorldViewBinder(view, predictor)` must not be used with `com.cuvara.dots`' adapter,
  and now says so at the call site.** 0.5.0 drives prediction from the binder, which
  pushes the *predicted* position out through `IEntityView.SetState`. As of
  `com.cuvara.dots` 0.10.0 that adapter reads the position from `SetState` as
  **authoritative** and stores it in a `ReconciliationAnchor` component — explicitly "the
  value a predictor rewinds to". Combining the two puts a predicted position in the anchor
  under a name that promises authority.

  **Nothing detects it.** The adapter skips writing `LocalTransform` only when a
  `PredictedTransform` marker is present; netcode never adds that marker, so the transform
  is still written, the avatar moves correctly, and every test passes. The damage surfaces
  when something finally reads the anchor and rewinds to a position its own prediction
  produced — which presents as float divergence and gets debugged as one, in the other
  package.

  The warning is on the constructor's XML docs, the class remarks, `LocalMovePredictor`
  and `NETCODE.md`, because the CHANGELOG is not where the next person will be standing.

- **The DOTS path is documented as driving `LocalMovePredictor` directly**: read
  `ReconciliationAnchor`, pair it with `WorldState.AckTick`, write `LocalTransform`, claim
  it with `PredictedTransform`, and release the marker when prediction stops — otherwise
  the transform ends up with no writer at all. The predictor is free of DOTS types
  precisely so one implementation of the algorithm serves both paths; only the driving
  side differs.

- **`LocalMovePredictor` works in the server's 2D space, not the client's world space** —
  now stated, because it was implicit and it is a trap. `MovementSystem.TryMove` clamps to
  `MapBounds`, which the server expresses in its own coordinates, so a caller holding a
  world-space anchor must recover the server-space position rather than project back: a
  round trip through a projection is not bit-exact, and bit-exactness is the entire reason
  replay goes through the shared library at all. `SnapshotSpaceMapping` deliberately has
  no inverse, so this is a real gap in the cross-package contract and is being settled
  with the DOTS side rather than papered over here.

- **The `Locomotion.Speed` wire gap is now a backend ticket** —
  [rpg-mmo-server#91](https://github.com/Cuvara/rpg-mmo-server/issues/91). No snapshot
  field carries per-entity speed, so the client predicts against a hand-maintained copy of
  the server's spawn default and desyncs silently the first time anything changes a
  player's speed. Recorded there so it outlives the release that discovered it.

## [0.6.0] - 2026-08-14

Minor rather than patch because the runtime assembly is split: consumers referencing
`Cuvara.Netcode.Runtime` for `NetworkingRegistration` or `NetworkBootstrap` must add a
reference to `Cuvara.Netcode.DI` or `Cuvara.Netcode.Bootstrap`. One line per asmdef.

### Changed

- **BREAKING: VContainer is optional, and the two assemblies that need it are gated.**
  `Runtime/DI/` and `Runtime/Bootstrap/` are now `Cuvara.Netcode.DI` and
  `Cuvara.Netcode.Bootstrap`, each carrying a `versionDefines` entry on
  `jp.hadashikick.vcontainer` and a matching `defineConstraints`. A consumer without
  VContainer loses those two assemblies and keeps a working transport, instead of a
  package that does not compile. `jp.hadashikick.vcontainer` is therefore no longer
  declared in `dependencies`; it is recorded under `x-optionalDependencies`.

  VContainer was used in exactly two files — `NetworkingRegistration.cs` and
  `NetworkBootstrap.cs` — so the split cost is small and the boundary is real: DI
  registration is a convenience, the transport is the product.

  **What this does not do, measured rather than assumed.** It does not make the package
  installable without the OpenUPM scoped registry. The `bare` install probe shows
  `com.cysharp.unitask` failing to resolve alongside VContainer, and UniTask is used
  across Auth, Client, Connection and Transport — it is not gateable. The benefit is
  narrower than "absent beats broken" suggests: it helps a consumer who *has* OpenUPM but
  uses a different DI container, or none. That is a real consumer and the change is worth
  making; it is not a standalone-install fix.

  `DevJwt.cs` moved from `Runtime/Bootstrap/` to `Runtime/Auth/`, its only consumer.
  Without that move the core assembly would have had to reference the gated one, which is
  the wrong direction and would have defeated the gating.

### Added

- **An install probe row for the gating.** `no-vcontainer` runs the documented install with
  the `jp.hadashikick` scope withheld from the registry entirely, so nothing can satisfy
  VContainer transitively. It is a required row: if the gating is wrong, the package stops
  compiling there rather than in a consumer's project.

## [0.5.0] - 2026-08-14

Client-side prediction and reconciliation for local player **movement**. Minor rather
than patch because `WorldViewBinder` gains a constructor overload and a new
`Cuvara.Netcode.Prediction` namespace; nothing existing breaks, and a caller that passes
no predictor gets 0.4.1's behaviour byte for byte.

### Added

- **`LocalMovePredictor` — predict on input, reconcile on snapshot.** Each input is
  given a tick, sent, buffered, and applied to the predicted position immediately. Each
  snapshot carries `AckTick` — the newest input tick the server accepted — so the client
  drops everything up to it, rewinds to the authoritative position, and replays only what
  the server has not seen. **The server needed no change:** `AckTick` has been on the wire
  and surfaced on `WorldState` since 0.3.0 with nothing consuming it.

  **Replay goes through `MovementSystem.TryMove`** — the exact entry point the server's
  `InputHandler` calls — which runs `ResolveDirection` and then `Integrate` internally.
  Both halves matter and skipping either is a silent bug:

  | Skipped | What breaks |
  |---|---|
  | `Integrate`'s split multiply-add | the JIT may contract it into one FMA, rounding once instead of twice — a last-place divergence that drifts instead of failing |
  | `ResolveDirection`'s normalization | raw diagonal input `(1,1)` predicts **41% too fast**; correct arithmetic on the wrong input |

  Pinned by tests comparing against a reference walk built from the same `TryMove`,
  asserting **exact** float equality rather than a tolerance — a tolerance would hide
  precisely the class of bug the split exists to prevent. Swapping the predictor for a
  hand-rolled `pos += dir * speed * dt` turns three of them red.

- **`PredictionSettings` — tick rate, speed, bounds, none of them defaulted.** Each has a
  plausible default and taking any silently is the failure this type exists to prevent:
  prediction against the wrong speed does not fail, it produces a position wrong by a
  little every tick, corrected by every snapshot, which reads as rubber-banding rather
  than as a misconfiguration. Unusable settings produce a predictor whose `IsEnabled` is
  false, which **refuses to predict** and leaves the caller on the previous path. An
  approximation drifts silently; an absence is diagnosable.

  **The weakest joint, stated rather than hidden:** speed is a per-entity server stat
  (`Locomotion.Speed`) that **no wire message carries**, so the client keeps a
  hand-maintained copy of the server's spawn default. A buff, mount or slow desyncs
  prediction until the next snapshot and neither side reports an error. This is the same
  shape as 0.4.1's lesson — something outside the package supplying what the package
  needs — and a `speed` field on the snapshot would close it properly.

- **`WorldViewBinder(IEntityView, LocalMovePredictor)`** and `IsPredicting`. A predictor
  reporting `IsEnabled == false` is treated exactly like `null`, so the fallback is a real
  code path rather than something each caller must remember to write.

- **Keyboard input in the DOTS sample** (`useKeyboardInput`, default on). The sample sent
  `sin(Time.time * 1.5)` / `cos(Time.time * 0.8)` — an autopilot, kept behind the flag for
  unattended soak runs. It makes the question the sample exists to answer unanswerable:
  "does moving feel responsive?" is meaningless when nothing is pressing anything, and
  **keypress-to-visible latency cannot be measured without a keypress**. Raw axes, not
  smoothed — `GetAxis`'s acceleration curve would put a second client-only easing in front
  of a change whose purpose is removing delay.

- **A prediction line in the sample HUD**, shown even when prediction is off, because a
  silently-absent predictor looks exactly like a working one with nothing to do. `snaps`
  is the number to watch: a steady climb means client and server disagree about speed,
  tick rate or bounds.

- **`WorldViewBinderTests` and `LocalMovePredictorTests` — 39 cases.** The binder had none
  before this.

- **`WorldViewBinder.Relocalizations`** — see *Fixed*.

### Changed

- **Corrections are smoothed below 0.5 world units and snapped above it.** Every reconcile
  produces some error, mostly float noise, and hard-setting on each is visible as jitter;
  blending all of them is worse in the other direction, because a real correction then
  arrives as a slow glide from a place the server has already ruled out. The threshold is
  derived from the movement model, not taste: one tick at 5 u/s and 15 Hz is 0.33 units,
  so this is 1.5 ticks' worth. Decay is `pow(base, dt)` — frame-rate independent, because
  a correction must not resolve faster on a faster machine — and settles at exactly zero.

### Fixed

- **`package.json` never declared `com.unity.modules.physics`, which the runtime
  requires.** `GameObjectEntityView` destroys the `Collider` on the primitive it spawns
  (client-side physics would quietly disagree with the server), so `UnityEngine.Collider`
  is a hard dependency of `Cuvara.Netcode.Runtime`. It resolved anyway because Physics is
  on by default — **the same defect 0.4.1 fixed twice over** (`Unsafe`, VContainer): the
  package relying on its consumers' defaults instead of declaring what it needs. Surfaced
  as `CS1069` in a project that did not happen to include it.

- **The DOTS sample's asmdef did not reference `Shared.GameLogic`.** Latent until now
  because nothing in the sample named a shared type.

- **`WorldViewBinder` now survives `localId` changing under a live entity.** 0.4.0 fixed
  this at the caller (the sample resets on a session boundary, which is correct and makes
  this path unreachable from there). This is the backstop, because the failure is silent:
  `isLocal` is handed to a view once at `Spawn` and the view is entitled to keep it, but
  *which id is local* is a session fact, and a client rejoining as a different user while
  the server still holds the previous session's entity would leave the old avatar
  presenting itself as the local player forever, with no error. The binder despawns and
  respawns the at-most-two entities whose locality flipped, reusing the existing three
  interface methods rather than widening `IEntityView` again so soon after 0.4.0 broke
  every implementation of it. Counted in `Relocalizations`, deliberately **not** in
  `DespawnsFromAbsence` — the entity did not leave, and folding them in would make an
  AOI-churn diagnostic lie.

### Documentation

- New **Prediction and reconciliation** section in `NETCODE.md`: the loop, the wiring, why
  replay runs the server's code, why refusing is a feature, the correction policy, why
  combat is excluded, and the superseded-input divergence.
- **Three rows deleted from the "Not implemented" table because they describe shipped
  features** — "Protobuf codec — interface and sniff in place, no implementation" (wrong
  since 0.2.0), "Protobuf-side world merge — only what the JSON codec decodes" (never true
  of `WorldState.Apply`, which takes a codec-agnostic `ResolvedSnapshot`), and
  "Prediction, reconciliation — out of scope by design" (this release).
- The README's sample table listed **two of the four** samples in `package.json`.

### Verified

- **39/39 tests pass outside Unity** — `Runtime/View`, `Runtime/World`, `Runtime/Snapshot`
  and `Runtime/Prediction` compiled with `dotnet` on .NET 10 against the real
  `Shared.GameLogic` at `sgl-v0.1.6`, the tag `package.json` pins.
- **Mutation-checked, not just green:** replacing `TryMove` with a hand-rolled integrator
  fails 3 tests; removing the relocalization backstop fails 1.
- **Not verified in the Unity Editor**, which was held by another task throughout. The
  DOTS sample's own compilation (Entities, Entities.Graphics, `Input.GetAxisRaw`) and the
  on-screen result are unexercised. 0.4.1's repaired CI gate — which now really does run
  the suite, 138 tests on `main` — is what will exercise them.
- **No keypress-to-visible measurement.** It could not be taken before this change because
  the sample had no keypress, and taking it now needs the Editor. The arithmetic case is
  that prediction removes RTT (measured 20–31 ms) and the server tick wait from the local
  avatar's response, leaving input-send quantisation (0–66 ms at 15 Hz). **That is a
  projection from measured components, not a measurement.**

## [0.4.1] - 2026-08-14

**Use this instead of `0.4.0`.** `0.4.0` is tagged and published to GitHub Packages, and it
does not work in a project that does not already supply
`System.Runtime.CompilerServices.Unsafe` from somewhere else: its runtime assembly fails to
load, silently. It also does not compile in a clean project, because `VContainer` was
referenced but never declared. `0.4.1` fixes both and supersedes it.

`0.4.0` is deliberately **not** retagged. A published version can be superseded, never
rewritten — moving the tag would leave the registry artifact and the tag pointing at
different code, which is worse than the state it would be fixing.

Both defects were invisible for the same reason, and it is the reason worth remembering:
**something other than the package supplied the dependency.** The only project anyone runs
supplies `Unsafe` three times over by accident and supplies VContainer itself, and this
repository's own CI bootstrap hardcodes VContainer into the manifest it writes. Every
install anyone had ever tested was propped up from outside. And the test job that existed
to catch it was reporting green while executing zero tests.

### Fixed

- **The package did not load at all in a project that does not already happen to supply
  `System.Runtime.CompilerServices.Unsafe`.** `Runtime/Plugins/Google.Protobuf.dll`
  references that assembly and shipped with a two-line stub `.meta`, so it imported with
  Unity's default `validateReferences: 1`. In a project without the assembly, validation
  refuses the plugin and the failure cascades:

  ```
  Assembly 'Packages/com.cuvara.netcode/Runtime/Plugins/Google.Protobuf.dll' will not be loaded due to errors:
  Unable to resolve reference 'System.Runtime.CompilerServices.Unsafe'.

  Assembly 'Library/ScriptAssemblies/Cuvara.Netcode.Tests.Editor.dll' will not be loaded due to errors:
  Reference has errors 'Cuvara.Netcode.Runtime'.
  ```

  `Cuvara.Netcode.Runtime` is poisoned, and so is everything referencing it. The plugin now
  ships a full `PluginImporter` meta with `validateReferences: 0`, which is what Unity's own
  message recommends.

  **Declaring the dependency was tried first and is not available to a package.**
  `org.nuget.system.runtime.compilerservices.unsafe` lives on OpenUPM, a *scoped registry* —
  and a UPM package cannot declare a scoped registry for its consumers, only a project can.
  Adding it resolved to `Package [org.nuget.system.runtime.compilerservices.unsafe@6.0.0]
  cannot be found` in a clean project. Vendoring a copy of the DLL was rejected too: the
  consuming project already carries the assembly from two other plugin folders, and a third
  would risk a duplicate-assembly conflict in the one project that currently works.

  It stayed invisible because the only project anyone runs supplies the assembly several
  times over by accident — `com.gdk.core/Plugins`, `Assets/Plugins/NuGet`, and Burst — none
  of it this package's doing.

- **`VContainer` was referenced by the runtime assembly and never declared, so a clean
  install did not compile.** Found by `com.cuvara.dots`' new gate, which installs this
  package into a minimal project:

  ```
  Runtime/Bootstrap/NetworkBootstrap.cs(13,7): error CS0246: The type or namespace name 'VContainer' could not be found
  Runtime/DI/NetworkingRegistration.cs(31,23): error CS0246: The type or namespace name 'IContainerBuilder' could not be found
  ```

  `jp.hadashikick.vcontainer@1.16.8` is now a declared dependency. The README had
  documented it as a manual step, so this was deliberate rather than forgotten — but a
  hard asmdef reference that the package does not declare fails as a compile error deep in
  someone else's build, where declaring it fails as a resolution error that names the
  package. The second is the better failure.

  This package's own CI could not have caught it either: the bootstrap manifest hardcodes
  `jp.hadashikick.vcontainer`, so CI was supplying by hand exactly what the package failed
  to declare. Same accident as the one above, a different actor.

- **`gitDependencies` renamed to `x-manualDependencies`.** `Shared.GameLogic` was recorded
  under a `dependencies`-shaped key that **stock Unity UPM does not read**, so it looked
  declared and was not. It genuinely cannot be declared — a package's `dependencies` takes
  registry version ranges only, a git URL is valid in a project manifest and nowhere else,
  and this is a git subpath rather than a published package. The `x-` prefix marks it as
  informational, and the README now states it as an install prerequisite rather than
  implying Unity will resolve it.

### Changed

- **The CI test job is a gate now, rather than a decoration.** It ran green while executing
  **zero tests** for its entire history, so every green on this repository up to and
  including `v0.4.0` asserted only that Unity started and exited 0. The runner is invoked
  with `USE_EXIT_CODE=false` and publishes a NEUTRAL check rather than a red one on an empty
  run, so neither Unity's exit code nor the check could catch it. A step now parses the NUnit
  XML the runner produces and fails on no XML, on zero tests, or on any failure or error, and
  prints the count.

## [0.4.0] - 2026-08-14

Minor rather than patch because `IEntityView.Spawn` gains a parameter. One line per
implementation to migrate, and the sample in this repo gets shorter as a result.

Also in this release: the local player is no longer rendered behind its own authoritative
position, and the DOTS sample stops labelling two entities `★ YOU` after a rejoin.

### Fixed

- **The local player was interpolated like everyone else, rendering it behind its own
  authoritative position.** `WorldViewBinder` used `localId` only to set the `isLocal`
  flag at spawn; the entity then went through the same lerp-between-the-last-two-snapshots
  path as every remote. That path renders up to one snapshot interval in the past by
  design — correct for remote entities, whose smoothness is the entire reason it exists,
  and wrong for the one entity whose response delay a player is holding a key to feel.

  Measured against a live backend at 15 Hz, comparing the rendered local position with the
  newest authoritative position: **mean 0.172 world units of lag, worst case 0.471**,
  against a per-tick step of 0.333 units over a measured 68.4 ms interval — about
  **35 ms of render delay on average and up to ~97 ms**. After the change the same
  measurement reads **0.000**, and remote entities still measure 0.07–0.17 units, so their
  interpolation is untouched.

  **This is not prediction and does not claim to be.** It removes the render buffer, not
  the round trip. What remains between a keypress and seeing yourself move is input-send
  quantisation (0–66 ms at 15 Hz), RTT (20–31 ms measured), and the server tick; closing
  that needs a prediction layer reconciling against `WorldState.AckTick`, which is
  surfaced for exactly that purpose and which nothing consumes yet.

  **The trade is real and worth stating**: the local avatar now advances in 15 Hz steps
  instead of gliding, because there is no longer anything between two snapshots to glide
  through. Latency is bought with smoothness on that one entity. Prediction is what buys
  both, and it is still unwritten.

  A late snapshot makes the local entity **hold at its last received position** rather than
  extrapolate. There is nothing honest to extrapolate from — the client does not simulate
  the local player, so a guess would be motion the server never confirmed, visibly undone
  when the real snapshot lands. Remote entities keep extrapolating to `t = 1.2`, where the
  alternative is a visible stall and the correction lands on somebody else's avatar.

- **A rejoin in the DOTS sample left two entities labelled `★ YOU`, one of them somebody
  else.** `LeaveRoom` cleared every cached HUD string and disposed the client, but never
  reset `WorldViewBinder` or the view — so the ending session's entities stayed presented,
  with the `IsLocal` flag they were given when they *were* the local player.

  That flag is decided in exactly one place, `Spawn`, and the binder only calls `Spawn`
  for ids it has not already seen. A carried-over entity is therefore never
  re-evaluated. Rejoining authenticates with a fresh device id and so a fresh Nakama user
  id, whose entity is spawned local as well — two locals, and the older one is a stranger.
  Measured directly after a `Leave Room`: the view still held the previous session's
  player at `IsLocal=True` with no client connected at all.

  It needs the old entity to still be listed when the new session's first snapshot
  arrives, which a rejoin inside the server's ~30 s entity hold satisfies.

  `StartConnection` and `LeaveRoom` now share a `ResetSessionView` that resets the binder,
  despawning everything it holds, and clears the label caches. `StartConnection` also
  refuses to start a second session while a client is live — two clients ticking one
  binder was the other way to reach the same state, and nothing in the sample wanted it.

- **The DOTS sample's floating labels cached `★ YOU` per id and never re-derived it.**
  A second, independent defect on the rendering side, and the same shape as the RTT
  freeze fixed below in this release: `_entityLabelTextCache` was keyed on the entity id alone, so once
  a label had been built the star could not come off. The neighbouring `style` lookup read
  the *live* `IsLocal` on every frame, which is why an entity could render a stale star in
  a colour that correctly said "remote". The cache now stores the locality its text was
  built from and rebuilds when the two disagree.


- **The DOTS sample's two RTT readouts disagreed in the same frame — the top-right one
  had been frozen since the first frame of the session.** Observed live at `996ms` in the
  HUD against `31ms` in the FPS panel, and the panel held `31ms` unchanged across two
  captures 45 s apart. Both labels read the same `_client.Session.RoundTripMs`, so there
  was never a second measurement to disagree with; the two caches shared one dirty-flag
  field. The HUD's own cache check advances `_prevRttMs` to the current sample, and the
  FPS panel — drawn later in the *same* `OnGUI` pass — then tested `_prevRttMs != rttMs`
  as its own invalidation condition. That comparison is always false by the time it runs,
  so `_cachedFpsRttText` was built once and never rebuilt. The HUD number was the honest
  one throughout. The FPS panel now caches against its own `_prevFpsRttMs`, and
  `LeaveRoom` resets both previous-value fields along with the strings it was already
  clearing — without that, the first RTT after a rejoin could match the stale flag and
  start the freeze over again.

- **Configuring the DOTS sample with a single map connected to whatever `mapId` held,
  not to the map that was configured.** `Start`'s `availableMaps.Length <= 1` branch
  auto-connected by calling `RunAsync` directly, which reads the separate serialized
  `mapId` field — so a one-entry list of `map_07` connected to `map_01`. The two
  single-map cases are now split: an empty or null list connects to `mapId` as before,
  and a one-entry list connects to *that entry*, through the same `StartConnection` path
  the selector uses, so the map indicator and status text are set the same way in both.

### Changed

- **BREAKING: `IEntityView.Spawn` takes the entity's kind —
  `void Spawn(string id, bool isLocal, string type)`.** The server types every entity,
  and that type crosses the wire on every snapshot the entity appears in, keyframe *and*
  delta (`SnapshotDeltaState` encodes it alongside `SnapshotEncoder`). It reached
  `WorldViewBinder` intact and died there: the binder read `X`, `Y`, `Hp` and `MaxHp`
  off the entity and dropped `Type` on the floor, so a view was told an id and a bool
  and had to work out for itself what it was drawing.

  What that cost is not hypothetical. **Two independent implementations invented the
  same workaround** — inferring kind from an `"enemy-"` prefix on the id — this repo's
  own `DOTSEntityView` and, downstream, `PrefixArchetypeResolver` in
  `com.cuvara.dots`. Neither author would have chosen it; both were re-deriving a fact
  the snapshot already carried, through a rule the presentation layer made up, coupled
  to how the server happens to *name* entities rather than how it *types* them.

  Migration is one signature and, usually, one deletion:
  ```diff
  - public void Spawn(string id, bool isLocal)
  - {
  -     bool isEnemy = id.StartsWith("enemy-");
  + public void Spawn(string id, bool isLocal, string type)
  + {
  +     bool isEnemy = type == "mob";
  ```
  `type` is never null — empty when the server sent none — so no null check is needed.
  Values are the wire's names: `player`, `mob`, `npc`, `item`, `projectile`, or an
  unrecognised name passed through verbatim when a simulation grows a kind ahead of the
  schema.

  **Consumers can now delete prefix-based resolvers.** Anything that guessed entity kind
  from an id has a first-class source for it. Be aware that this is a compile break for
  anything implementing `IEntityView` directly, including through a helper interface of
  its own: verified against `com.cuvara.dots` 0.8.0, where `DotsEntityView.Spawn` is a
  `CS0535`, twenty test call sites through an `IEntityView`-typed variable are `CS7036`,
  and `INetworkArchetypeResolver.TryResolve` needs the type as well before its prefix
  resolver can actually be retired. Update the consumer and the package together.

  A fourth method or a binder-preferred overload were both considered and rejected. The
  interface documents itself as "deliberately three methods" so a DOTS implementation can
  replace `GameObjectEntityView` cheaply; either non-breaking route would have bought
  source compatibility with the exact narrowness that design is protecting, and left the
  prefix inference alive as a supported path. Nobody deletes a workaround that still
  compiles.

- `GameObjectEntityView` puts the kind in the GameObject's name
  (`remote:mob:1a2b3c4d`). Deliberately nothing else — giving mobs their own mesh or
  colour would be presentation policy, and this view exists to be the dumbest thing that
  can be looked at. A name makes the value visible in the hierarchy, which is what makes
  it verifiable.

- **`package.json`'s sample description for *DOTS Sample* now describes the sample.** It
  read "Spawns 5 ECS entities with 3D meshes that move and spin" — written before the
  networking, combat and economy landed, and the first thing anyone reads in Package
  Manager before importing.

### Removed

- **`DOTSEntityView`'s `EnemyIdPrefix` constant and the `id.StartsWith` test it fed.**
  Replaced by the `type` parameter. The `_enemyIds` set stays — `SetState` and the label
  pass need the kind every frame and only `Spawn` is told it, so it is a cache now
  rather than a re-derivation.

### Added

- **`availableMaps` on `DOTSSceneSetup`, and `DOTSNetworkBridge.ConfigureMaps`.**
  `DOTSSceneSetup` adds the bridge from `Awake`, and a component added at runtime can
  only carry its field initializers — never a scene's inspector values. The bridge
  therefore always started with the two-map default, always drew the selector, and the
  sample could never auto-connect no matter what the scene said. The setup component now
  carries the map list itself and hands it to the bridge it creates, in the same frame,
  before the bridge's `Start` reads it. `ConfigureMaps` ignores a null or empty array,
  and the setup component only configures a bridge it created — a bridge placed on the
  GameObject by hand keeps its own inspector values.

  The shipped scene still lists `map_01` and `map_02`, so the selector remains the
  out-of-the-box behaviour; the point is that a consumer can now change it. The list is
  written into `Scenes/DOTSSample.unity` explicitly rather than left to the field
  initializer, so it is visible and editable in the Inspector on first open.

## [0.3.2] - 2026-08-14

### Fixed

- **`Samples~/DOTSSample` was a stale mirror in three files, and importing it would
  have regressed the sample rather than reproduced it.** The sync in
  `9bbe634 chore(netcode): sync DOTSSample to Samples~ upstream mirror` copied the
  file *set* but left three files at their pre-combat content, so the mirror carried
  `CombatBootstrap.cs` and `DOTSNetworkBridge.cs` while nothing referenced or compiled
  them:
  - `DOTSSceneSetup.cs` did not add `CombatBootstrap` or `DOTSNetworkBridge` to the
    scene, and built the ground material with `Shader.Find` instead of
    `Resources.Load<Material>("DOTSGroundMaterial")`.
  - `DOTSSample.asmdef` was missing the `Cuvara.Netcode.Runtime` and `UniTask`
    references — without them `DOTSNetworkBridge.cs` and `SampleNakamaAuth.cs` do not
    compile, so a fresh import of the sample was a broken import.
  - `DOTSSpawner.cs` was missing the null guards on `World.DefaultGameObjectInjectionWorld`
    and on the material.

  All three are now synced from the imported copy, which is the version two later
  commits (`df2f15a` combat, `ba2882d` economy/leaderboard) developed against.

### Changed

- **Imported samples now live under one root, in Package Manager's own layout.**
  The DOTS sample sat at `Assets/Samples/com.cuvara.netcode/0.3.1/DOTSSample` — keyed
  by package *name* with an undisplayed folder name — while the three Package
  Manager-imported samples sat at `Assets/Samples/Cuvara Netcode/0.3.1/`. Unity imports
  to `Assets/Samples/<displayName>/<version>/<sample displayName>`, so the first path
  could only have been hand-copied, and the two trees read as two packages.
  Moved to `Assets/Samples/Cuvara Netcode/0.3.1/DOTS Sample` with its `.meta` files, so
  every asset GUID is preserved and no scene or asmdef reference breaks; the
  `com.cuvara.netcode` root is gone. Re-importing the sample from Package Manager now
  overwrites in place instead of producing a second copy.

## [0.3.1] - 2026-08-12

Documents multi-instance support that **0.3.0 shipped without documenting**, and settles
the World View sample's run length. Anyone diffing 0.3.0's tarball against its changelog
would have found `-instance N` present and unexplained; this is that explanation, not a
new feature.

### Added

- **`-instance N` command-line argument** for the World View sample. Present in 0.3.0's
  tarball but undocumented there.
  It is required rather than cosmetic: every standalone build reports
  `Application.isEditor == false`, so without an explicit instance number several copies
  all choose the same role, write over each other's report file, and share one motion
  phase — producing windows that cannot be told apart. The argument is what makes running
  more than one player build at a time meaningful. Absent, it defaults to 1.
- **Evenly spaced motion phases** across instances, `(instance - 1) × 2π/3`, so three
  clients sit 120° apart instead of bunching together. Also present in 0.3.0 and
  undocumented.
  Still phase rather than heading, for the reason that matters: **phase cannot accumulate
  into distance**, while two different headings diverge linearly and will eventually cross
  the server's 50-unit area of interest, at which point the clients stop seeing each other
  and a correct system looks broken.

### Changed

- World View sample `runSeconds` 75 → **300**, in both the code default and the serialized
  scene. This sample exists to be watched by a person, and 75 seconds is short for that.
  Both had to change: a modified `[SerializeField]` initializer does **not** update a value
  already serialized into a scene, so changing only the default silently keeps the old
  behaviour. 0.3.0's published scene still read 75.

### Verified

- **Three clients, three Nakama users, one map — every client saw all three.** Three
  separate Standalone player processes on Protobuf, each holding all three entities for the
  full 110 s observed, `views` tracking `world` at every sample, and
  `despawn(removed)=0 despawn(absent)=0` throughout — no spurious despawns across three
  clients for nearly two minutes:
  `CLIENT 1/2/3  t=110s  world=3 views=3 live=3 despawn(removed)=0 despawn(absent)=0`
  This is the first run with more than two clients, so it is also the first time the view
  layer rendered multiple remotes and the first time entity-id interning resolved more than
  two entities. Each client renders itself green and larger with its peers red, so a
  screenshot from any one of them shows one green and two red — the picture that
  distinguishes genuine multiplayer from a working pair.

## [0.3.0] - 2026-08-12

Minor rather than patch: `Runtime/View/` is new public API, there is a new sample, and a
new package dependency. It also carries the fix that would otherwise have been 0.2.1 —
folded in rather than released separately, because shipping new public API inside a patch
tarball would have put `Runtime/View/` in consumers' hands undocumented.

### Fixed

- **0.2.0's samples could not compile outside this repository.** Every sample referenced
  `Scripts.Nakama` — `NakamaSessionService` / `NakamaSettings` — which lives in the host
  project's `Assets/`, not in the package. The package's `Runtime/` was and is clean, so
  installing 0.2.0 worked; it broke only on sample import, which is exactly when a new
  consumer first touches it. Affected `NetcodeE2EHarness`, `SoloVisibilityProbe`,
  `TwoClientVisibilityHarness` and `WorldViewDemo`.
  Each sample now carries a `SampleNakamaAuth` that talks to Nakama over plain HTTP with
  `UnityWebRequest` and the package's own `JsonParser` — no Nakama SDK, no new dependency,
  and nothing outside the package. It is duplicated per sample on purpose: Package Manager
  imports samples independently, so a single shared copy outside the sample folders would
  not be imported, and two copies in one namespace would collide for anyone importing
  both. Each copy sits in its own sample's namespace.
  A real application should still implement `Cuvara.Netcode.Auth.IAuthProvider` rather than
  copy this; it is a sample's shortcut, not a pattern.
- Declared `com.unity.modules.unitywebrequest` as a package dependency, which the new
  sample auth needs and which a consumer project may have stripped.

### Added

- **Entity view layer** (`Runtime/View/`) — the world is now renderable.
  - `IEntityView` — three methods: `Spawn(id, isLocal)`, `Despawn(id)`,
    `SetState(id, x, y, hp, maxHp)`. Deliberately narrow, because the point of the seam
    is that a DOTS implementation can replace the GameObject one later without the
    netcode noticing, and a wide interface makes that swap expensive.
  - `GameObjectEntityView` — primitive capsules, green and larger for the local player,
    red for remotes. **No interpolation and no prediction, on purpose**: positions are
    applied exactly as the server sent them, so remote entities visibly step at the tick
    rate. Smoothing here would hide the tick rate and hide dropped snapshots, which are
    the two things this exists to make observable. HP shows as a vertical squash — one
    line, no UI, readable in a screenshot.
  - `WorldViewBinder` — reconciles the view against `WorldState`: present-and-unknown →
    spawn, known-and-absent → despawn, otherwise update. **Polls rather than subscribing
    to snapshots**, because GameObject APIs are main-thread only and a poll driven from
    `Update` is main-thread by construction, `WorldState` is already the merged result so
    a poll loses nothing, and reconciling against the whole world makes despawn fall out
    of absence — which is correct, since the wire does not distinguish an AOI exit from a
    true despawn. `NoteRemovedIds` optionally records ids named in `removed` so the two
    causes can be told apart in diagnostics; it is not load-bearing for the reconcile.
  - The local player is identified by comparing the entity key with `NetworkClient.UserId`
    — the key IS the Nakama user id, so this needs no extra wire field.
  - Chose GameObjects over DOTS deliberately: the project ships the DOTS packages and has
    no ECS code, and the first ECS in the codebase should not be the thing being used to
    debug netcode. If it broke, you could not tell which half broke.

- **World View sample** (`Samples~/WorldView`, displayName "World View"). One client that
  renders what it sees, plus a top-down camera built in code so the scene needs nothing
  configured by hand. Run one in a player build and one in the Editor to watch two
  clients move around each other.
  It captures its own PNG by rendering the camera to a `RenderTexture` rather than calling
  `ScreenCapture.CaptureScreenshot` — that call depends on a presenting surface, so in the
  Editor it silently wrote nothing while the log line still claimed success, from code
  identical to the player build's. Rendering explicitly behaves the same in both processes
  and fails loudly, which is what test evidence has to do.

- **Two-client visibility harness** in the E2E Certification sample
  (`Samples~/E2ECertification/Scripts/TwoClientVisibilityHarness.cs` +
  `Scenes/TwoClientVisibility.unity`). Runs two independent `NetworkClient` instances
  with two distinct Nakama identities in one play session and asserts that each one's
  `WorldState` contains the other. Every prior certification here was single-client, so
  the client had never actually resolved a remote entity — a world of one proves nothing
  about the multiplayer claim.
  The harness documents and guards three false-negative traps, each of which makes a
  working server look broken: the 50-unit area of interest (two clients driven with
  merely *similar* headings separate linearly and fall out of range — they are driven
  with identical vectors and distance is logged as evidence rather than assumed);
  per-user persisted positions (device ids are tagged per run so both users spawn
  fresh); and `NakamaSessionService`'s single PlayerPrefs session key, which would make
  both clients restore the *same* session and silently test one user against itself —
  a false pass, which is worse than a false failure.
  It also reports peak world count alongside the final one, because a run that holds two
  entities for 20 s and then drifts apart ends at one and hides its own success.

- **Two-process visibility probe** (`Samples~/E2ECertification/Scripts/SoloVisibilityProbe.cs`
  + `Scenes/SoloVisibility.unity`). One client per process, so running it in a player
  build alongside the Editor tests remote visibility across two OS processes with two
  Unity runtimes and no shared memory — the authoritative shape, where the in-process
  harness is only a proxy. Writes its findings to a file in the temp directory as well
  as the log, because a player build's console cannot be read from outside.
  Two properties of it are load-bearing, and both exist because two processes cannot be
  started at the same instant — gaps of 12 s and 29 s were measured client-side (30 s
  server-side for the latter), and 78 s in an earlier attempt:
  - It **holds position until a peer is actually in view**, then starts moving. Any
    client that moves during the join gap is already displaced when the second arrives;
    at ~5 units/s a 29-second gap is ~145 units, well past the 50-unit AOI, and the pair
    never sees each other while both are behaving correctly.
  - It then drives a **bounded oscillation** rather than a constant heading, so each
    client stays within a few units of spawn indefinitely while its position still
    changes — keeping "the peer is being updated" observable without letting the two
    drift apart. With a constant heading a 78-second offset put one client at x=353
    while the other was still at x=0.
  Together these remove launch timing from the experiment entirely, which is a property
  the in-process harness gets for free (both clients start in the same frame) and so
  could never have surfaced.

### Verified

- **Mutual visibility across two OS processes, on Protobuf.** A StandaloneWindows64
  player build and the Editor, each its own Unity runtime, two distinct Nakama users in
  `map_01`: both reported `WorldCount = 2` for the entire overlap window, each world
  containing the other's user id, and each peer's position updating at every 5-second
  sample. This is the authoritative result; the in-process harness agrees with it.
  A confirmation that fell out of it: when the Editor client exited, the player kept
  reporting the departed entity at a frozen position for the remainder of its run — the
  30-second hold seen from the outside. A held entity is indistinguishable from a live
  one that has stopped moving, which matters to whoever renders remote players.
- No static mutable state exists on the client path (`Client/`, `Connection/`,
  `Snapshot/`, `World/`, `Codec/`, `Transport/`) — every `static` is a pure method, a
  `static readonly` immutable, or a stateless helper class. That is what rules out an
  in-process test passing through shared memory rather than over the wire.
- **Mutual visibility, in-process, on Protobuf.** Two distinct users in
  `map_01`: `WorldCount == 2` on both sides, each world containing the other's user id.
  The remote entity is genuinely tracked, not merely spawned once — A's view of B
  matched B's own reported position at all six samples across 24 s. Snapshots carried
  two entities, so entity-id interning was exercised with more than one binding for the
  first time.
- **Area of interest measured at 50 units.** An earlier run separated the two clients;
  the remote entity was last visible at 50.5 units apart and absent at 62.2, matching
  the documented radius to within half a unit.
- **Entity hold measured at 30 s.** After a deliberate disconnect, the removal reached
  the other client at **30.1 s** — the documented hold, to within 0.15 s. World count
  went 2 → 1 and the departed entity left by id.

## [0.2.0] - 2026-08-12

Minor rather than patch: this adds a wire encoding, a public option, a generated-code
surface and a binary dependency. Nothing breaks — `ConnectAsync(jwt, mapId, ct)` is
untouched and JSON stays the default — but none of that is a patch.

### Added

- **Protobuf wire codec** (`Runtime/Codec/ProtobufWireCodec.cs`), the backend's default
  encoding. Selected via `RegisterNetworking(settings, WireEncoding.Protobuf)`; JSON
  remains the default so an existing caller's behaviour does not change on upgrade.
  Both servers mirror the encoding of the first frame per connection, so this is a
  client-side choice needing no server change.
  - Rejects `MsgType` 0 at **both** ends. proto3 omits a zero field 1, so a type-0
    envelope would not begin with `0x08` — the byte the peer sniffs to tell Protobuf
    from JSON's `{` — and the frame would be parsed as the wrong encoding entirely.
    Decoding rejects it too: a body starting `0x12` is valid Protobuf carrying only
    field 2 with the type defaulted to 0, so arbitrary bytes can otherwise "decode"
    successfully.
  - Entity kind reads the `type` enum first and falls back to `type_name`;
    `ENTITY_TYPE_UNSPECIFIED` means "see `type_name`", not "unknown". Reading only one
    half silently loses either every known kind or every future one.
- **`Google.Protobuf` 3.29.3**, vendored at `Runtime/Plugins/Google.Protobuf.dll`.
  **The package's first third-party binary.** It is unavoidable rather than chosen:
  the code generated from `wire.proto` carries 322 references to the Protobuf runtime
  and does not stand alone, and hand-writing the types would create a second
  definition of a schema that already exists. Vendored inside the package rather than
  in `Assets/` so the package stays importable on its own. The version deliberately
  matches the backend's pin (`GameServer.csproj`) and the generator used.
- **Generated schema types** at `Runtime/Protocol/Generated/Wire.cs`, namespace
  `RpgMmo.Wire.V1`. Regenerate identically with:
  `protoc --proto_path=backend/shared/proto --csharp_out=Runtime/Protocol/Generated wire.proto`
  using **libprotoc 29.3**. Committed because Unity cannot run protoc at import;
  `wire.proto` remains the single source, so this is one definition, not two.
- **`Runtime/link.xml`** preserving `Google.Protobuf` and `RpgMmo.Wire.V1` from IL2CPP
  managed-code stripping. Protobuf registers message types through static parsers and
  reaches properties reflectively, which the stripper cannot see. The failure would be
  runtime-only in a player while the Editor — which does not strip — stayed green.
- 13 interning tests (`Tests/Editor/SnapshotResolverInterningTests.cs`) covering the
  branches only a Protobuf connection can reach: unknown handle on a delta, bare
  handle on a keyframe, an aborted snapshot leaving the table untouched, handle
  rebinding across a keyframe including a double rebind, removals not releasing a
  binding, and both zero sentinels.

### Fixed

- `WireConnection` threw `"received a Protobuf frame, which this client cannot decode
  yet"` on any inbound Protobuf frame. Inbound is sniffed per frame rather than assumed
  to mirror the outbound codec, so **both** codecs are now held ready: JSON because the
  gateway writes eviction frames as JSON whatever the connection latched, and Protobuf
  because it is now implemented.
- `SnapshotResolver` cleared the handle table *before* resolving on a keyframe. That
  mutated state before validating it, so a malformed keyframe wiped the table and then
  aborted, leaving the client with no bindings and an empty world until a resync
  completed. Resolution now runs first and the clear happens only once every entity has
  resolved, restoring the all-or-nothing guarantee. A keyframe carrying a bare handle is
  rejected **without consulting the table** — the previous interval's binding for that
  number belongs to a different entity, so a successful lookup is the dangerous
  outcome, not the safe one.

### Verified

- Certified against a live Nakama + gateway + game server stack on **both encodings**,
  70 s per run so the server's 10 s ping / 30 s pong-timeout heartbeat is actually
  exercised — every earlier run sat inside that window and proved nothing about it.
  Protobuf: 881 snapshots, 851 deltas. JSON: 1029 snapshots, 995 deltas. Both forced a
  keyframe on resync, reconnected inside the 30 s entity hold with position preserved,
  and finished with zero errors. The 851 Protobuf deltas are the interning coverage JSON
  structurally cannot provide.
- **IL2CPP with managed stripping at High**, Android, arm64: `Google.Protobuf.dll`
  survives the stripping pass and reaches IL2CPP conversion; 2137 objects compiled and
  the build succeeded. This is what `link.xml` exists to guarantee and the one thing an
  Editor (Mono) run can never show. Note the project's Android default is stripping
  *Minimal*, the least aggressive level, so the test was run at High deliberately —
  a green build at Minimal would be weaker evidence than it looks.

## [0.1.2] - 2026-08-12

### Added

- **E2E Certification sample** (`Samples~/E2ECertification`, displayName "E2E Certification").
  A client-driven certification rig that drives the whole flow from inside Unity with
  no pasted token and no signing secret: Nakama device auth, the `gateway_token` RPC,
  both handshake hops, the input/snapshot loop, `RequestResync`, and a reconnect inside
  the server's 30 s entity hold. Results are exposed as static fields so an automated
  harness can assert on them without scraping the console.
  Shipped as a second sample rather than folded into Demo Bootstrap: the two want
  different scene setups, and merging them would make the minimal "does it connect"
  demo ship with its `NetworkBootstrap` disabled.

- `NetworkClient.HasAuthProvider` — reports whether an `IAuthProvider` was supplied,
  so a caller can choose the real auth path when one is wired up and fall back to a
  development credential when it is not, without throwing to find out which it is.

### Fixed

- `NetworkBootstrap` leaked a project-wide setting. It set
  `Application.runInBackground = true` and never put it back; in the Editor that setter
  writes through to `PlayerSettings` and survives play mode, so merely running the
  sample permanently rewrote `ProjectSettings.asset` in whatever project imported it
  (it surfaced as an unexplained `runInBackground: 0 -> 1` diff). The previous value is
  now captured and restored in `OnDestroy`. The override itself is unchanged and still
  applies for the whole session — it is load-bearing, because an unfocused Editor stops
  ticking the player loop and would silently stop sending input and heartbeats while
  snapshots kept arriving. Restored in `OnDestroy` rather than `OnDisable` on purpose:
  disabling the component does not end the session, and restoring the flag mid-session
  would cause the exact stall the override prevents.

- `NetworkBootstrap` never used `IAuthProvider`. It minted a development JWT via
  `DevJwt.Sign` unconditionally, which meant `NetworkClient.ConnectAsync(mapId, ct)`
  — the DI overload — was dead code, and a host app that had correctly registered a
  provider was still silently authenticated by the sample's local minting. It now
  resolves the token through the registered provider when the container supplies one.
  `DevJwt` remains the fallback when no provider is present, so the sample still runs
  with zero DI setup, and the chosen path is logged so the live identity source is
  never ambiguous. The connect-failure hint is now specific to the path in use rather
  than always blaming `JWT_SECRET`.

- Demo Bootstrap sample: `NetworkBootstrapConfig.asset` shipped `gatewayPort: 8100`,
  overriding the `8000` default in `NetworkBootstrapConfig.cs` and contradicting the
  class documentation. Importing the sample and pressing Play failed with
  `dial 127.0.0.1:8100 failed: ... actively refused it` against a default backend.
  The serialized asset now matches the code default.

### Changed

- `NetworkEndpoint.Parse` now recognises every listen-style host a server may
  advertise but no client can dial — `""`, `"0.0.0.0"` and `"::"` (`"[::]"` reduces
  to `"::"` once brackets are stripped) — via the new public
  `NetworkEndpoint.IsListenStyleHost`. This matches `NormalizeDialAddr` in
  `backend/smoketest/smoke/helpers.go` so both ends agree on the set. Previously only
  a completely empty host was handled.
- The substituted host is now **the gateway host the client already reached** rather
  than a hardcoded loopback, via the new
  `NetworkEndpoint.Parse(address, fallbackHost, out bool normalised)` overload. A
  device talking to a LAN or remote gateway must not be redirected to its own
  loopback. The single-argument `Parse` overload is unchanged and still falls back to
  `DefaultHost`.
- `GatewayClient.EnterWorldAsync` logs a warning naming the misconfiguration whenever
  the address is rewritten, so this fallback cannot silently mask a server that
  advertises an undialable `GAMESERVER_PUBLIC_ADDR`.

  This normalisation is **hardening, not the contract**. The contract is the
  server's: `GameServer/Program.cs` requires the advertised address to be dialable by
  the client, and the wire protocol specifies no format for `server_addr`.

## [0.1.1] - 2026-08-11

### Changed

- Migrate `Shared.GameLogic` git dependency URL from `dyCuong03/rpg-mmo-server` to `Cuvara/rpg-mmo-server`
- Bump `Shared.GameLogic` to `sgl-v0.1.6`
- CI test project updated to match new dependency URL

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
