# Changelog

All notable changes to the Cuvara Netcode package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **The samples job compiled one sample out of five** (#30). The gate added to close that
  issue named `DOTSSample` directly, so `DemoBootstrap`, `WorldView`, `E2ECertification` and
  `ContentPipeline` shipped with nothing compiling them while the job reported success over
  samples it had never seen. It now copies every sample `package.json` declares.
  A list maintained by hand goes stale silently, which is the exact failure #30 was opened
  about — naming one sample in the fix reproduced it one sample at a time.
  - Compiling all five immediately found a real break: **`WorldView` does not compile** in
    the CI project. Its screenshot helper calls `Texture2D.EncodeToPNG`, an extension method
    living in `com.unity.modules.imageconversion`, which a real Unity project has by default
    and this job's hand-written manifest did not. The sample compiled everywhere except the
    one place that was supposed to be checking it. Module added to the samples manifest.

### Added

- **`sync-main.yml` — `main` now follows the release tag by itself.** Runs on a `v*` push and
  opens an auto-merging pull request moving `main` to the tagged commit.
  - It opens a PR instead of pushing: `main` requires a pull request and four passing checks,
    and a workflow that bypassed that would be removing the gate from the branch other people
    read. No approval is required there, so a green PR lands on its own.
  - No-ops when `main` already contains the tag, and warns instead of forcing when the move
    would not be a fast-forward.
  - `workflow_dispatch` accepts a tag, for a tag pushed while this was broken or a sync PR
    that was closed.
  - Written because a `main` that drifts is worse than one that is obviously abandoned: it
    *looks* current while being stale, which is exactly how `develop` sat two releases behind
    with nothing noticing.

### Changed

- **`develop` is the integration branch, and release tags are cut on it.** PRs target
  `develop`; `main` is still built on push but nothing targets it by default.
  - `release-reminder.yml` now watches `develop` instead of `main`. Watching `main` meant the
    reminder fired on a branch nothing was merging into, so a version could sit untagged on
    the branch people actually work on — which is how `develop` fell **two releases behind**
    (`v0.16.3` and `v0.17.0` were both tagged on `main`) with nothing noticing. Anyone
    branching from `develop` started without them.
  - `ci.yml` accepts pull requests targeting either branch, so a PR aimed at `develop` is
    built. It previously only ran on PRs into `main`.
  - `release.yml` is unchanged and did not need changing: it triggers on the **tag**, not on
    a branch, so a tag cut on `develop` already ran it. The branch decides where work lives,
    not whether a release fires.
  - The policy is written into `README.md` under *Branching and releases*, with the failure
    that motivated it, so the next person does not have to reconstruct it.

## [0.17.0] - 2026-08-22

### Added

- **Content pipeline client** (`Runtime/Content/`, sample `Samples~/ContentPipeline`).
  Game content lives as JSON on the game server and is served over HTTP at `/content`, so a
  content change reaches players through a server restart rather than a client build, a
  `Shared.GameLogic` tag and a `packages-lock.json` bump (ADR-19). Until now the only channel
  between the repos was a package pinned by exact commit — right for simulation rules, which
  must change on both sides at once or prediction diverges, and fatal for content, whose
  whole value is iteration speed.
  - `ContentClient` caches **by hash, never by time**. Content does not expire; it changes
    when a server restarts with different files, and the hash is how that is detected. A TTL
    cache would either re-download unchanged content or serve content that had changed.
  - Prefers `X-Content-Hash` over `ETag`: `UnityWebRequest` and several proxies rewrite or
    strip `ETag`, and a client that cannot read back its own hash can never send `?hash=` —
    so every launch silently re-downloads the full set while appearing to work.
  - Treats a `304` arriving as a `UnityWebRequestException` as success. Unity raises any
    non-2xx as a protocol error, so the successful steady-state answer would otherwise report
    a content failure on every launch after the first.
  - A `304` against an empty cache clears the stored hash rather than looping on a response
    it cannot satisfy.
  - `ContentJsonReader` builds the **same `Shared.GameLogic.Content` types the server
    simulates against** and runs the **same validator**. The parser is per-side and the
    schema is not — forced rather than preferred: Unity compiles `Shared.GameLogic` as source
    and has no `System.Text.Json`, while the server is NativeAOT and cannot reflect.
  - Client-side validation grants the client nothing: it answers "is this content coherent",
    never "may this player have this item".
- **`Samples~/ContentPipeline`** — a UXML scene listing every fetched item with a chip
  reading NETWORK, CACHE or LOCAL. All three verified against a real server; the second run
  reads CACHE, which is the 304 path working.

### Changed

- **CI pins `Shared.GameLogic` at `sgl-v0.2.2`**, up from `sgl-v0.1.9`. `Runtime/Content/`
  compiles against `Shared.GameLogic.Content`, a namespace `0.1.9` does not have, so every
  Unity job in this repo failed to compile until the pin moved — the package's own
  `dependencies` do not name it, because it is supplied by the consuming project.

## [0.16.3] - 2026-08-20

Test-harness only. No runtime assembly changed.

### Added — `PredictionLatencyMeasurement` can measure the unseeded base tick

`SeedBaseTick` shipped in v0.16.0 with a described mechanism and **no number**. The harness
could not supply one: it drives the predictor through `WorldViewBinder`, and the binder seeds
on every snapshot, so every run it had ever produced was already the *after*.

`MeasureAsync` takes `seedBaseTick`, and the unseeded arm is interleaved with the other two.
Reproducing the pre-fix state needs no production change: `SeedBaseTick` takes effect once and
`_baseTick` starts at 1, so seeding it with **1** leaves the counter where it began and marks it
seeded, which makes the binder's real call a no-op for the rest of the run.

**Measured against a live backend, 2026-08-20** (medians of 3 interleaved runs, 20/20 usable
samples, server-advertised 60 Hz and 60.0 Hz measured off the wire):

| | unseeded | seeded |
|---|---|---|
| max correction | **0.0833** world units | **0.0000** |
| corrections smoothed / snapped | — | 1 / 0 |
| reconciles | 140 | 162 |

`0.0833` is not an arbitrary figure: player speed 5 ÷ 60 Hz = 0.08333, i.e. **exactly one base
tick of movement**. That is what a one-tick phase misalignment produces, so the number and the
documented mechanism corroborate each other rather than merely coexisting.

Reported, never asserted. It is a phase effect and the unseeded arm carries the widest spread of
the three configurations (28 % of mean, against 6 % for the seeded arm) — the correction going to
zero clears that comfortably, the reconcile count does not and should not be read as a result.

## [0.16.2] - 2026-08-20

Test-infrastructure only. No runtime assembly changed.

### Fixed — `PredictionSurfaceContractTests` could not survive an added overload

`Method(string name)` selected out of `GetMethods` with `SingleOrDefault(m => m.Name == name)`.
`SingleOrDefault` throws `InvalidOperationException` — "Sequence contains more than one matching
element" — as soon as two methods share a name, so the first overload added to
`LocalMovePredictor` made the fixture throw.

The fixture exists to guard the surface `com.cuvara.dots` drives, and its own remarks tell callers
to extend that surface by **adding rather than changing**. An overload is the sanctioned way to add,
and the guard reported the sanctioned move as a broken contract.

Two things made it worse than a plain false positive:

- it surfaced as an **exception**, not an assertion, so none of the explanatory messages this
  fixture is built around were printed; and
- it broke **every case sharing the fixture** — `AdvanceKeepsItsSignature` and the rest — none of
  which the author had touched. The failure pointed away from the change that caused it.

`Method` now matches on name **and** parameter types, so each case resolves the exact signature it
is asserting and overloads are invisible to the others. Observed while prototyping a three-argument
`Reconcile`; that prototype was rejected on its own merits and no overload is added here — this is
the guard, fixed so the next addition fails for a real reason or not at all.

## [0.16.1] - 2026-08-19

Sample-only release. No runtime assembly changed, so nothing about transport, codec,
handshake, snapshots or prediction moves — a consumer who does not import the DOTS
sample gets the same package as 0.16.0 under a new version number.

### Added — `Samples~/DOTSSample/BackendCommandLine.cs`

The DOTS sample can now be pointed at a backend from the command line (with
`CUVARA_*` environment variables as a fallback), and can be run as several
processes that authenticate as several distinct Nakama users.

Both of these were previously impossible in a build. The sample scene carries only
`DOTSSceneSetup`, which adds `DOTSNetworkBridge` at runtime — so the bridge can never
hold anything but its own field initializers, and every address it knew
(`127.0.0.1:8000` for the gateway, `127.0.0.1:7350` for Nakama) was baked into source.
Retargeting a player meant editing a `[SerializeField]` default and rebuilding. That is
wrong by construction for this backend: the game server runs as an Agones pod whose port
is assigned at scheduling time.

- **Flags, and their precedence.** Command line beats environment beats the value the
  caller already had; with no arguments at all every field is left exactly as it was, so
  the sample's out-of-the-box behaviour is unchanged.

  | Flag | Environment | Selects |
  |---|---|---|
  | `-cuvara-gateway-host` | `CUVARA_GATEWAY_HOST` | gateway host |
  | `-cuvara-gateway-port` | `CUVARA_GATEWAY_PORT` | gateway port |
  | `-cuvara-nakama-scheme` | `CUVARA_NAKAMA_SCHEME` | `http` / `https` |
  | `-cuvara-nakama-host` | `CUVARA_NAKAMA_HOST` | Nakama host |
  | `-cuvara-nakama-port` | `CUVARA_NAKAMA_PORT` | Nakama port |
  | `-cuvara-nakama-key` | `CUVARA_NAKAMA_SERVER_KEY` | Nakama server key |
  | `-cuvara-status-url` | `CUVARA_STATUS_URL` | game-server status URL for the HUD |
  | `-cuvara-map` | `CUVARA_MAP_ID` | map to join, skipping the selector |
  | `-cuvara-device` | `CUVARA_DEVICE_ID` | explicit device id |
  | `-cuvara-instance` | `CUVARA_INSTANCE` | label shown in the HUD and folded into the device id |

  The names match the ones `Tests/Runtime/LiveBackend.cs` already reads, so one exported
  environment drives both the Editor live-backend tests and a built player.

- **`ResolveDeviceId` is what makes N processes N players.** Nakama device auth keys the
  user by device id; two instances that compute the same one log in as the same user and
  the second eviscerates the first. What is left on screen is one client alone in an
  empty world — the same picture a broken area-of-interest draws, which is why this is
  worth a changelog paragraph rather than a footnote. `ResolveDeviceId` folds the process
  id, the instance label and the clock into the id so co-located processes cannot collide,
  and honours an explicit `-cuvara-device` so a launcher can give each window a name that
  greps out of the server logs.

- **No address is baked in, deliberately.** `Resolve` takes the caller's current values as
  its defaults and returns them untouched when nothing overrides them. The sample ships
  pointing at localhost because that is where a dev stack is, not because the package has
  an opinion about where your backend lives.

- **`-cuvara-map` collapses the offered map set to one entry.** With two or more maps
  available the bridge draws a selector and waits for a click, which an unattended launcher
  cannot supply; the window would sit at a menu and read as "connected to nothing".

- Reading the command line is wrapped in a `try` — WebGL denies
  `Environment.GetCommandLineArgs()` outright, and the environment fallback must still work
  there rather than throwing at startup.

### Changed — `Samples~/DOTSSample/DOTSNetworkBridge.cs` wires the above in

One `BackendCommandLine.Resolve` call at the top of `Start`, before anything connects, and
`ResolveDeviceId` at the authentication call site. Nothing runs per frame, and no netcode
behaviour changes.

### Why these two files moved upstream now

They already existed in the Cuvara client and were held back at 0.16.0 as "harness". They
are not: pointing a build at a chosen backend, and running several clients that are several
users, is what any consumer with more than one window needs. Keeping them out also left a
permanent false positive in the client's vendor-drift check, because a drift allowlist can
legitimately excuse a file that is *absent* upstream but must never excuse one that
*differs* — an exemption that covered modifications would hide real drift behind it.

### Documentation

`Documentation~/NETCODE.md` gains **Pointing a build at a backend**: the full flag and
environment table with its precedence rule, the identity-collision failure mode and why it
looks like a netcode bug, and why no address is baked into the sample.

## [0.16.0] - 2026-08-19

Three defects in the movement predictor, all of which left every existing counter clean
while the player's own avatar misbehaved. Read the first section even if you are not
touching prediction code: it changes what you are allowed to assume about the golden
vectors, and about the `sgl-*` pin.

### If you read nothing else

- **The golden vectors do not prove client/server agreement.** They cover
  `Shared.GameLogic`, which both sides genuinely share. All three defects below lived in
  `LocalMovePredictor`, the *client-side* mirror of `GameServer/Input/InputHandler.cs` —
  code the shared library does not contain and the vectors therefore never touch. Green
  vectors mean the movement *arithmetic* matches. They say nothing about *when* each side
  decides to run it, which is where prediction actually diverges.
- **The `sgl-*` pin is a label, not behaviour.** This release moves the manual
  `com.rpgmmo.shared-gamelogic` pin from `sgl-v0.1.8` to `sgl-v0.1.9`. That range changes
  exactly one line inside `Shared.GameLogic/` — the version string. The real change in it
  is in `GameServer/Input/InputHandler.cs`, which is *outside* the UPM package and does not
  ship to the client at all. So the pin bump moves no client code; it names the server
  build this predictor was transcribed against. Do not infer behaviour from a pin diff.
- **If you write your own view binding, you must call `SeedBaseTick`.** See below.

### Changed — `StepDeltaTime` now takes `heldFrom`, and a restart after idle steps **once**

This is the contract not to "fix" back.

`LocalMovePredictor.StepDeltaTime(baseTick, lastMoveTick, heldFrom)` returns one plain
timestep (`_dt`) when `heldFrom == 0`, before any banked-time arithmetic runs. It mirrors
`rpg-mmo-server` `GameServer/Input/InputHandler.cs:78-93` line for line, and the rule it
transcribes is stated in `gameserver-dotnet/docs/API.md`:

> **Only a moving entity accrues that time.** A deadzone input clears the hold, and an
> entity with no held direction is *stopped*, not stalled — so a player who releases the
> stick, waits, and presses again is owed nothing for the pause.

**What happens if someone removes that guard**, on the reasoning that rule 3 says a step
covers the time since the entity last moved: the client predicts up to `MaxBankedTicks` of
travel — 15 timesteps at 60 Hz, from the 250 ms `MaxBankedMovementMs` bound — that the
server never takes, on the first input after every pause. The player stops, starts, and
lurches a quarter-second of travel forward in one frame, then rubber-bands back when the
snapshot lands. It reproduces on no test that does not deliberately contain an idle, and it
is the most common thing a player does.

Paired with it: an input the movement model resolves to `MoveResult.None` (a deadzone
vector) now clears the hold **and** stamps `_lastMoveTick`, on the live path and the replay
path alike, exactly as `InputHandler.ProcessInput` does in its pre-check and again in its
`MoveResult.None` branch. Leaving `_lastMoveTick` stale is what let an idle bank time in the
first place. A `Rejected` vector still leaves both fields alone, matching the server, which
logs and drops it without disturbing state. `GameConstants.MaxBankedMovementMs` is untouched
— the cap was never the problem; what counted against it was.

Measured against a transcription of `InputHandler` (60 Hz base / 15 Hz world, zero
latency): **19 snaps and a 14.00-step worst correction before, 0 snaps and 0.0000 after.**
The `heldFrom` guard alone takes it to zero snaps; the `_lastMoveTick` stamp takes the
residual correction from 3.00 steps to nothing.

**Why no existing test caught it.** `LocalMovePredictorTests.ServerWalk` — the oracle the
bit-exactness assertions compare against, whose own docblock claimed it "is not a second
copy of the movement rule" — was a hand-rolled rule-3 loop missing both guards. It modelled
the client, not `InputHandler`, so `PredictingForwardMatchesTheServerExactly` passed on a
walk that deliberately contains a stop *because both sides were wrong the same way*. It is
now transcribed from `ProcessInput`, and two tests pin the restart-after-idle case directly.

### Added — `SeedBaseTick(long serverTick)`, and its one caller (#13)

**If you use `WorldViewBinder`, this is already wired and you need do nothing.** If you
bind views yourself — a DOTS system reading `LocalMovePredictor.Position` into
`LocalTransform`, or any custom binder — **you must call it**, or the feature is inert and
you keep the defect:

```csharp
predictor.SeedBaseTick(world.Tick);   // BEFORE Reconcile, every snapshot; one-shot inside
predictor.SetServerSpeed(e.Speed);
predictor.Reconcile(new Vec2(e.X, e.Y), world.AckTick);
```

`_baseTick` used to start at 1 and free-run on wall-clock accumulation while the server's
`current_tick` sat in the hundreds of thousands. The absolute values never mattered —
`StepDeltaTime` and `ApplyHeld` use differences — but the **phase** did: the hold window is
`HoldTicks` base ticks wide, and where each clock's boundary fell relative to an input
changed how many held steps got applied between inputs. On localhost, matched rates, no
loss: 17 of 20 samples needed a correction of exactly 2 steps.

It is a separate method rather than a `Reconcile` parameter for the same reason
`SetServerSpeed` is: `Reconcile`'s signature is a cross-package contract that
`com.cuvara.dots` drives and `PredictionSurfaceContractTests` pins. Seeding takes effect
once — the accumulator clock in `Advance` owns the counter afterwards, and re-seeding every
snapshot would fight it. `Reset()` clears the flag so a new session re-seeds.
`LocalMovePredictor.BaseTick` is public so you can confirm the seed took.

### Fixed — the local avatar froze for part of every base tick (#11)

`SmoothingSpan` returned the integration timestep `_dt` unconditionally whenever a hold
window was in use, on the premise that with a hold the server steps every base tick and so
the steps being spread arrive one `_dt` apart.

**That is the steady case, not the only one.** The base tick immediately after an input is
declined by rule 1 — `ApplyHeld`'s `heldFrom == baseTick` guard, because the input already
stepped that tick — so the next step lands a full timestep after the *following* boundary,
a gap of up to `2 * _dt`. Spreading it over `_dt` finishes the step part-way through the
gap; `StepProgress` pins at 1, `remaining` goes to zero, and `Position` is bit-identical for
the rest of the gap. At ~1000 fps against 60 Hz that is a run of still frames on the one
entity the player is controlling.

It is invisible to every correction counter because **the simulated position is correct
throughout** — only the rendered one stops. `Snaps` stays 0, the corrections budget passes,
the tick rate agrees, the hold window measures the correct 4.

The span now follows the interval steps are **actually** arriving at, smoothed with the same
α = 0.3 moving average `WorldViewBinder` uses on snapshot arrivals, floored at `_dt` as
before. In sustained movement that interval *is* `_dt`, so the steady case is unchanged; it
widens only across the boundary that was freezing. A gap longer than the whole hold window
means the hold lapsed and the step begins a new burst rather than continuing one — adopting
it would spread the next step across an idle period and leave the avatar crawling behind its
own simulation, which is the 0.12.3 defect in a new place — so such a gap restarts the
measurement and the span falls back to its floor. `StepProgress` still saturates at 1: a
wider span makes the avatar reach the step *later*, never *further* than the step the input
actually produced.

### Fixed — one narrow snapshot pair permanently shrank the hold window

`TickRateEstimator.SnapshotTickGap` is a running minimum of the base-tick gap between
consecutive snapshots that never recovers, and it feeds the predictor's hold window
directly. The premise behind the minimum — that only drops move a gap, and only widen it —
is false at one moment every session passes through: the first snapshot after joining is a
keyframe emitted when the join is handled rather than on a world-tick boundary, so the gap
to the next scheduled snapshot is whatever the phase happens to be, 1 to 3 base ticks
instead of 4. Two snapshots batched into one socket read do the same.

**A narrower gap must now be observed twice before it is adopted.** The true cadence repeats
on every snapshot and confirms immediately; a one-off join artefact never does. Drops still
only widen a gap and are still ignored, so "minimum, not mean" is kept.

**What you would observe without the two-observation rule:** the hold window is pinned for
the whole session at the width of one off-cadence join keyframe. At a real cadence of 4 with
a keyframe gap of 1, `HoldTicks` sits at 1, which switches the hold off entirely — the
predictor then steps only on inputs, reproduces a quarter of the server's motion at a 15 Hz
send rate against a 60 Hz base tick, and the difference arrives as a correction on every
snapshot. It is set once, at join, and never recovers; reconnecting is the only thing that
clears it, and whether it clears is a coin flip on phase.

`SnapshotTickGap` had no EditMode coverage at all despite being the sole source of the hold
window; four tests now pin the rule.

**Known limitation, deliberately recorded.** The rule tracks a single candidate, so gaps
alternating between two values would reset the candidate on every sighting and leave
`_minGap` at 0 — and `SetHoldTicks(0)` is ignored, so `HoldTicks` would sit at its fallback
of 1 with the hold switched off. A steady cadence cannot produce that, and a bisect confirmed
this rule is not behind the snap counts it was briefly suspected of, but counting per gap
value rather than tracking one candidate is the durable form, and is left as follow-up.

### Added — a diagnostic surface for render-side faults, pinned as contract

The three defects above share one property: **no correction counter could see any of them**,
because in all three the simulated position was right and only the rendered one was wrong.
`Snaps`, `Reconciles`, `ReplayedSteps` and `LastCorrection` cannot answer "is my avatar's
rendered position keeping up with its simulated one". These can, and they are part of the
package's public contract rather than debug leftovers — `PredictionSurfaceContractTests`
pins each one, so removing one is a deliberate act:

| Member | Answers |
|---|---|
| `IntegrationTimestep` | the base tick period the predictor is integrating on |
| `EffectiveSmoothingSpan` | the span one step is being spread across |
| `ObservedStepInterval` | the interval steps are actually arriving at |
| `RenderStepProgress` | how much of the current step has been rendered, 0..1 |
| `HoldIsActive` | whether a step is in flight *right now* |
| `BaseTick` | the predictor's tick, on the server's timeline once seeded |
| `BaseTicksAdvanced` / `HeldStepsApplied` | ticks advanced vs. ticks that moved |
| `SkipNoHoldWindow`, `SkipNothingHeld`, `SkipInputAlreadyStepped`, `SkipExpired`, `SkipRefusedByMovementModel`, `SkipNoDisplacement` | *why* the shortfall between those two |
| `StepIntervalSamples` / `StepIntervalResets` | whether the interval measurement is converging or being torn down |

**Read `EffectiveSmoothingSpan` against `ObservedStepInterval` first.** A span shorter than
the interval means the avatar finishes its step and then holds still —
`RenderStepProgress` pins at 1 and `Position` goes exactly constant — for the remainder.
Longer, and the avatar permanently lags its own simulation. Neither shows in any correction
counter. That comparison is the line that named #11.

The skip counters are recorded **only on the live path**. `Reconcile` replays the same
guards over the unacknowledged timeline, and folding those in would measure how much replay
ran rather than how the rendered position behaved.

### Removed — `HoldDeclines`, `DebugBaseTick`, `DebugLastMoveTick`, `DebugStepDt`

Never part of a released surface; they existed only during the investigation.
`HoldDeclines` was the exact sum of the six `Skip*` counters, which are reported
individually. `DebugBaseTick` survives, renamed to `BaseTick` and documented. The `HoldSkip`
reason code is now private: it is how the class talks to itself, and publishing it would
invite a consumer to switch on values this package expects to be free to extend.

### Changed — the measurement rig, `Tests/Runtime/PredictionLatencyMeasurement.cs`

A harness, not a runtime feature; nothing in `Runtime/` depends on it. Summarised because
its findings are the evidence for everything above.

- **Its own clock leaked.** `lastFrameAt` was only written inside the sample loop, so the
  first `AdvanceFrame` of every sample re-advanced the whole ~400 ms settle window on top of
  what `PumpAsync` had already advanced — ~24 base ticks in one call, which expired the
  four-tick hold window before a single frame rendered against it. `FramesHoldActive` came
  back as 20 across 20 samples where ~67 per sample was expected. This is the same
  double-advance `WorldViewBinder` guards with `_frameDriven`, reappearing in the harness.
  Guarded against recurrence: the report prints the largest `AdvanceFrame`, counts calls
  wider than one base tick, and self-flags an implausibly small hold denominator.
- **The first reading of each sample spanned no time.** It read the position as of
  `RecordInput` returning, against a baseline captured moments earlier — necessarily zero,
  and correctly so, since `RecordInput` deliberately preserves the rendered position across
  an input. It now re-baselines rather than being recorded as a fault; the count prints as
  `zero-duration reads` and must equal the sample count exactly.
- **The smoothness assertion is now `RenderingFaultPercent`** — still frames on which the
  hold window was still running, over the frames on which it was running. The old figure
  divided by every sampled frame, including the tail of each sample where the avatar is
  *correctly* at rest: a live run split 437 still frames into 417 legitimate and 20 genuine.
  Budget 0.5%; the correct value is exactly zero and the budget is margin against scheduling
  noise, not against a known source. Every still frame is classified at the moment it occurs
  by `HoldIsActive`, which is what makes the narrowing a counter rather than an argument.
- **Correction magnitude is now also sized by `MeasuredTickRate`.** Sizing it only by the
  client's own belief about the tick rate is self-referential: a client predicting at the
  wrong rate sizes its own yardstick by the same wrong rate, so four real base ticks of
  correction print as `1.00 steps`, which is what perfect health looks like. Measured at
  15 Hz against 60 Hz: 0.3334 world units reported as one step; matched rates: 0.0833, also
  one step. Identical readings, opposite verdicts.
- **The correction-shape note no longer asserts a clock error.** A whole-number-of-steps
  correction has two candidate causes the arithmetic cannot distinguish — a clock error
  (linear in `holdTicks`) and banked movement (capped at `MaxBankedMovementTicks`). The note
  now compares against the cap and names the reading that matches.
- **Added:** the still-run-length histogram (20 isolated frames and 3 freezes of 7 need
  opposite responses), the per-repeat `SNAPS PER RUN` distribution (`Representative()`
  selects by a *rendered* metric while `Snaps` is a simulation property, so any rendering
  change reselects the reported repeat), `HOLD WINDOW IN USE`, `sample window`,
  `ack timeouts`, `non-advancing frames`, and `harness clock resolution`.
- **Harness frame timing** now comes from `Time.realtimeSinceStartupAsDouble` rather than
  differencing `Time.realtimeSinceStartup`, a `float` whose spacing coarsens with process
  uptime (~1.95 ms past 4.5 hours) and near 1000 fps can return the same instant twice.

## [0.15.5] - 2026-08-15

### Fixed — the local avatar stuttered at every frame rate

`WorldViewBinder` advanced the predictor **twice per frame**, so its clock ran at about
**2x real time**.

0.15.0 added `AdvanceFrame(deltaTime)` as the per-frame driver but left the existing
advance inside the snapshot pass in place. That pass is not once per arriving snapshot:
a real client calls `Tick(world, localId)` **every rendered frame**, whether or not a
snapshot landed. Both drivers therefore ran on every frame, each covering the same span.

The doubling does not show up as the avatar running away — reconciliation pins the
position to the server's every snapshot — so it is spent on base ticks instead. The
server holds a direction for `WorldEvery` base ticks and stops the entity when that
window expires. At double rate the client's copy of the window expired in **half the
real time it should**, so the predicted avatar moved for the first part of each send
period and stood still for the rest.

Three things about the symptom follow from that and made it hard to place:

- **Frame rate is irrelevant.** Capping to 60 fps changed nothing, which wrongly ruled
  out the render path as the location.
- **Only the local player is affected.** Remote entities are driven by the
  interpolator's own clock and stayed smooth throughout — "everyone else moves fine,
  only the character I control stutters" is the exact signature.
- **Distance is correct.** Nothing about total travel or final position is wrong, so no
  positional assertion catches it.

Measured on a live client at 15 input sends per real second: the predictor read the
interval between them as **0.133 s** where 0.067 s was sent, and **85-100% of frames**
rendered no movement with worst-frame jumps of **1.1-1.25 units**. After the fix, the
same build reads **0.0669 s**, with **0-0.3% still frames** and a worst-frame jump of
**0.027 units** while moving.

The frame loop now owns the clock: `AdvanceFrame` claims each span it advances, and the
snapshot pass advances only when no frame loop is running at all — a headless harness
that pumps snapshots and renders nothing, which must still see prediction move. That
second case is covered by its own test, so the fix cannot decay into "delete the
snapshot advance".

### Added

- `PredictorClockAdvancesOnceTests` — asserts the predictor's clock matches real time
  with both drivers running, driving `Tick` every frame as a real client does. Verified
  against the defect: with the fix reverted the suite reports 1 failure, with it
  restored 372/372 pass. It asserts `ObservedInputInterval`, not travel, because travel
  is unchanged by the defect.

### A note on the version number

0.15.3 was reserved for this and is being skipped. 0.15.4 is already tagged, so
publishing a lower number afterwards would make the changelog read backwards and any
"latest" resolution ambiguous. A reserved number stops being free once something else
ships past it.

## [0.15.4] - 2026-08-15

Tests and measurement. No runtime change — and the point of it is that **no runtime change
was needed**, which is not what the previous release said was coming.

`0.15.3` is deliberately skipped: it is reserved for the frame-clock fix described below,
which is not in this repository.

### A planned change, withdrawn on evidence

0.15.2 announced a `Reconcile` contract change to fix a "phase error" — an input
acknowledged on receipt while its hold keeps stepping, so replay drops steps the server
has not yet taken. The reasoning was sound and the constant `2.00` fitted it exactly.

**It is wrong.** Running client and server against each other end to end, over thirty
snapshots, with the real server rules on the other side:

```
clock=1.00x   correction: 0.00 steps
clock=1.25x   correction: 1.00 steps
clock=1.50x   correction: 2.00 steps
clock=2.00x   correction: 4.00 steps
```

**A predictor whose clock is right disagrees with the server by nothing at all.** There is
no inherent hold-remainder defect, and the cross-package `Reconcile` change — which
`PredictionSurfaceContractTests` pins and which would have been expensive to reverse — is
not needed and is not being made.

The `2.00` was real, but it was a reading of something else.

### The correction is an instrument

`correction_steps = (clockFactor - 1) * holdTicks`, exactly, at every factor measured. So a
live run reporting `2.00` steps against a 4-tick hold is reporting **a predictor clock
running at 1.5x real time**, and one reporting `0.00` is reporting a clock that is right.

The measurement now prints that reading beside the correction, so the next person does not
have to derive it — this took several releases to arrive at.

### Added

- **`ACorrectClockProducesNoCorrectionAtAll`** — the property the whole prediction path
  exists to have, and nothing asserted it end to end. The pieces were pinned individually
  (the step, the hold, replay parity) but client and server were never run against each
  other over many snapshots with the answer required to be exactly zero. **This is the
  guard that catches a clock defect**, and it would have caught the one this release was
  chasing.
- **`TheCorrectionMeasuresTheClockError`** at 1.25x, 1.5x and 2.0x, so the reading above
  stays trustworthy rather than becoming folklore.

Both go into an existing fixture with an existing `.meta`, per 0.15.1.

### Correcting 0.15.2 again

That release said a sub-threshold correction is invisible and the user cannot feel it.
Wrong, and it cost several builds. Below the snap threshold a correction is *smoothed*,
which means a decaying offset injected on every snapshot — fifteen times a second — and
that reads as jerk at any frame rate. It is also local-only, because remote entities are
never reconciled. "Smoothed" is not "unseen".

## [0.15.2] - 2026-08-15

Assertions only. No runtime change — and the measurement below is the reason there is no
runtime change.

### Measured before changing anything

A player build reported **82.7% of frames with no movement**, at ~500 fps against a 60 Hz
tick. That is 8.3 frames per tick, so a rendered position advancing once per tick leaves
`(F-1)/F` = 87.5% still — the figure identifies its own cause.

The predictor was measured at those exact parameters before touching it, and it is **not**
the source: per-frame deltas came out `0.01012, 0.01011, 0.01011, 0.01010, ...` against a
step of `0.08333`, eight even frames per tick. It interpolates within the tick correctly.
A per-tick figure therefore comes from a consumer sampling `Position` once per tick, which
is exactly what `AdvanceFrame` was added in 0.15.0 to fix — and the build measured predates
it.

A candidate change was also tested and rejected on the same run: restarting the
interpolation only on an input that moved made still frames **worse**, 10.8% to 16.2%,
leaving burstiness untouched. Second time that change has been proposed and measured away.

### Added

- **The live measurement asserts still frames below 25%.** More direct than burstiness and
  it needs no noise floor: a still frame either is one or is not. The failure message
  computes frames-per-tick from the run's own observed fps and tick rate, so a reader sees
  immediately whether the figure matches a per-tick render.
- **`AlmostEveryFrameMovesAtAFrameRateThatDoesNotDivideTheTick`** — 500 fps against 60 Hz,
  8.33 frames per tick. Every existing evenness case used frame rates that divide the tick
  exactly, so the awkward case, which is the only one a real client ever runs, was not
  covered.

### On the assertions added here

Both live in files that already have their `.meta`, and both were added to existing
fixtures rather than new files, deliberately: 0.15.1 records that
`HeldMovementParityTests.cs` shipped in 0.14.0 without one and therefore never ran in
Unity — not here and not in any consumer — while passing out of Unity, where `.meta` files
are irrelevant. Every mutation result quoted for that fixture in 0.14.0 was true of the
out-of-Unity run and vacuous in Unity. A green out-of-Unity suite says nothing about
whether Unity can see the file.

## [0.15.1] - 2026-08-15

### Fixed

- **`HeldMovementParityTests.cs` shipped in 0.14.0 without its `.meta`, so Unity ignored
  the asset entirely — the parity test has never run, in this repository or in any
  consumer.** Worse for consumers than for us: Unity logs
  `has no meta file, but it's in an immutable folder`, and the test framework turns an
  unexpected log error into an exception, so **the error fails the whole test run of any
  project that installs the package**. Found by `com.cuvara.dots`' CI, whose job reported
  failure with 137/137 EditMode and 29/29 PlayMode passing and not one test failed.

  A `.meta` is load-bearing for a git-installed package: the package lands in the
  immutable `Library/PackageCache`, where Unity will not generate one. This is the third
  time a missing or stub `.meta` has silently disabled shipped content here.

## [0.15.0] - 2026-08-15

**Consumers must now call `WorldViewBinder.AdvanceFrame(deltaTime)` once per rendered
frame.** Without it the smoothing this package has spent five releases on is computed and
never sampled.

### The defect

Prediction was advanced, and the local entity re-rendered, **only inside snapshot
processing**. The rendered position therefore changed at the **world** rate — 15 Hz at
the default configuration — however fast the client was drawing. Every frame between
snapshots showed the avatar perfectly still; the frame a snapshot landed on showed the
whole interval's movement at once.

Spreading a step across an interval achieves nothing when nothing samples the position
during that interval. The interpolation was only ever read at its endpoints.

Nothing reported it. Positions were correct, corrections were zero, `input -> visible`
was unaffected at 0.1 ms. It surfaces only in frame-delta burstiness, as roughly *frames
per snapshot interval* — about 20 at 300 fps against 15 Hz.

**The clue was in the measurement all along and was read past three times:** the
predicting and non-predicting configurations reported burstiness in the same band. A
number that does not care whether prediction is switched on is not measuring prediction.

### Added

- **`WorldViewBinder.AdvanceFrame(float deltaTime)`** — advances prediction and re-renders
  the local entity. Call once per frame from `Update` or equivalent, separately from
  snapshot handling. No-ops safely before a local entity exists and when there is no
  predictor, so it can be called unconditionally.
- Called every frame by the DOTS sample and by both frame loops in the live measurement.
- `WorldViewBinderTests` covers it, including that it is safe with no predictor and no
  local entity. Mutating `AdvanceFrame` to a no-op fails the first of those.

### Why no test caught this

Every case in `WorldViewBinderTests` drives the binder by feeding it snapshots. **The
frame loop was not modelled at all**, so a position that only moved on snapshots was
indistinguishable from a correct one. The same shape as the smoothing fixture using one
constant for two different rates, and as the gate that could not go red: the fixture could
not express the failure.

### Not addressed

The `2.00`-step correction is unchanged and deliberately not touched, so this release's
effect on burstiness is attributable to one change. The phase fix — the snapshot's server
tick as an anchor for replay — is next.

## [0.14.1] - 2026-08-15

Measurement and tests. No runtime change.

### Added

- **Each configuration is measured three times, interleaved, and the spread is reported.**
  A single sample per configuration cannot distinguish a regression from a noisy metric,
  and this metric is noisy: between two runs where nothing in the non-predicting path had
  changed, its burstiness moved by a third. Interleaved rather than batched, because the
  machine and the backend drift over the length of a run and batching puts that drift
  entirely into whichever configuration went last. The median run is reported whole rather
  than figures averaged across runs, so the relationships between the numbers still belong
  to a run that actually happened.

- **Predicted motion must be more even than unpredicted motion** — asserted, because
  smoothing that makes motion less even than no smoothing is not earning its place, and
  evenness is the metric closest to what a player reports.

  **The assertion is gated on the spread.** It fires only when the gap between the two
  configurations exceeds the disagreement between runs of a single configuration;
  otherwise it warns and says the metric cannot resolve a difference that small.
  Asserting through a noisy metric manufactures regressions and fixes in equal measure.

- **Two evenness cases in `HeldMovementParityTests`**, measuring the same max/mean frame
  delta the live harness reports. Every existing case asserted *where* the avatar is; none
  asserted how evenly it gets there, which is the thing actually being complained about.

### A hypothesis that did not survive

A candidate mechanism for the reported burstiness increase — that an input moving nothing
(coalesced under rule 1, or a deadzone) restarts the interpolation with a zero step and
snaps the rendered position onto the simulated one — **was tested and is wrong.** The
compensation added in 0.10.x already folds that discontinuity into the render offset, so
the rendered position stays continuous. The code change was reverted; the tests written to
catch it are kept, and they are what disproved it.

Recorded rather than dropped because the reasoning was sound and someone will have it
again.

## [0.14.0] - 2026-08-15

Prediction now implements **all three** rules of the server's movement model. 0.13.0 added
the second; this adds the first and third, which `gameserver-dotnet/docs/API.md` now
specifies normatively and states plainly: *a client that implements the first two but not
the third will diverge from the server exactly when the network is worst.*

Requires **`sgl-v0.1.8`** or newer — the tag that adds
`GameConstants.MaxBankedMovementTicks`.

### Added

- **Rule 1, coalescing: at most one step per player per base tick.** The predictor could
  step twice in one tick — once from the hold, once from an input arriving later in the
  same tick. The server cannot: it drains inputs and then applies holds inside one tick,
  and guards the hold on `HeldFromTick == baseTick`. A client has no ordering guarantee
  between its frame loop and its send loop, so it guards both sides. Counted as
  `CoalescedInputs`, which is not a fault at or above the base tick rate and is worth
  looking at below it.

- **Rule 3, the elapsed-time step.** `dt` covers the time since the entity last *actually
  moved*, `min(now - lastMoveTick, cap) / tickRate`, with the cap read from
  `GameConstants.MaxBankedMovementTicks` rather than copied. Invisible to a client sending
  every tick, because `lastMoveTick == now - 1` always — which is why the server could add
  it without regenerating the golden vectors. Visible to a client sending at 15 Hz into a
  60 Hz base tick whenever jitter opens a gap past the hold window.

  The cap is part of the movement model, not a server-side valve: a client banking
  unbounded time reconciles against a server that does not, on exactly the frames where
  the network was worst.

### Fixed

- **Replay reproduced a different position from forward prediction**, which would inject a
  correction on every reconcile that the network never caused. One misplaced line cleared
  `_lastMoveTick` on the deadzone branch of `RecordInput` instead of in `Reset`.

  Worth recording how it hid: clearing that field sends the elapsed-time rule down its
  "never moved" early return, which yields one plain timestep and so **reproduces the
  pre-rule-3 behaviour exactly**. The forward-parity test stayed green while rule 3 was
  not in effect. The failing replay test is what exposed the passing one as a lie, and it
  was found by tracing the values rather than by reasoning about them — three separate
  arguments about that timeline were all wrong.

### Changed — an invariant was retired on purpose

- **`SmoothingDoesNotTouchTheSimulatedPosition` is now
  `SubTickFramesDoNotTouchTheSimulatedPosition`.** The old name asserted that `Advance`
  affects only the rendered position. Rule 2 already falsified that in 0.13.0 — `Advance`
  runs the base-tick timeline — and rules 1 and 3 make it more so. The test was rewritten
  to assert what is now true and still worth protecting: a **partial** tick must not step
  the simulation. Both predictors receive the same whole ticks; one receives them in
  slices. If those slices move `SimulatedPosition`, the frame rate has entered the
  simulation and bit-exactness with the server is gone.

  Stated here rather than edited quietly, because a test adjusted until it passes is how
  an invariant dies without anyone deciding to kill it.

- **The tests' restatement of the server banks time, and there is now one copy of it.**
  `ServerWalk` modelled a pre-rule-3 server with a fixed `dt`, and a second copy of the
  same loop was inlined in another case. A second copy of the server model in the tests is
  the same defect as a second copy of a server constant in the code.

### Verification

96/96 out of Unity. Mutation-checked: disabling rule 3 fails 4 tests, disabling rule 1
fails 1, removing the cap fails 1.

### Known and not addressed here

The live measurement reports `max correction in steps = 2.00` — exactly two whole steps,
with send-gap burstiness `1.01`, so coalescing on the wire is not contributing. A whole
number is a phase error rather than a rate error: an input is acknowledged when the server
has *received* it, not when it has finished integrating it, and `DropAcknowledged` retires
it at acknowledgement along with the hold steps its window has not yet taken. Fixing it
needs the snapshot's server tick as an anchor so replay can reconstruct which of those
steps the authoritative position already contains — a change to what `Reconcile` is told,
not an adjustment to the arithmetic. Whether rules 1 and 3 move that figure is now
measurable.

## [0.13.2] - 2026-08-15

Measurement only. No runtime change.

### Fixed

- **The measurement printed a configured constant where a measured value belongs.** With
  prediction off there is no predictor to ask for a speed, and that branch assigned
  `LiveBackendConfig.PlayerSpeed` so the expected-step row could still be printed. A run
  in which the avatar never moved therefore reported `effective speed 5`, which reads as
  evidence that snapshots were arriving and carrying speed. It reports `not measured` now.
  Added in 0.12.2, by the same hand that removed the last constant of this kind.

### Added

- **Distinct positions and `SetState` call counts per run.** "The server never moved the
  entity", "the harness never noticed it moving" and "no snapshot ever reached the binder"
  produce an identical report — no usable samples, 100% still frames — and nothing already
  present told them apart.
- **Send-gap burstiness.** `rpg-mmo-server#100` discards inputs that clump into one tick
  along with the simulated time they carried, up to 46% of movement at 60/15/5, so an
  unevenly sending client is legitimately outrun by its own prediction. Measured rather
  than assumed even.
- **The correction reported as a count of steps, not only a distance.** A whole number of
  steps is a phase error: an input is acknowledged when the server has *received* it, not
  when it has finished integrating it, and replay drops the input at acknowledgement along
  with the hold steps its window has not taken yet. A fraction would point at the rate or
  the arithmetic instead. The 0.1667 measured at 60/15/5 is 2.00 steps exactly, which is
  why 0.13.0 did not move it: that release corrected how many steps the client takes, not
  when they stop being replayable.

## [0.13.1] - 2026-08-15

Documentation only. No behaviour change.

### Changed

- **`TickRateEstimator.SnapshotTickGap` now states the coupling it rests on** instead of
  presenting the derivation as safe. It measures the snapshot cadence and is *used* as
  the hold window; those are two separate facts about the server, equal only because
  `ApplyHeldMovement` is passed `_rates.WorldEvery` and snapshots also go once per world
  tick. Nothing on the wire couples them. If the server ever holds for a window it does
  not send on, the derived value is wrong by a fixed ratio — which smooths rather than
  snaps and no counter can see, the signature of all four defects this package has hit.

  The note also says where it would surface: the live measurement's near-zero-corrections
  assertion, which is what found the missing hold. If that fires while the tick rate
  agrees, this coupling is the thing to suspect.

## [0.13.0] - 2026-08-15

Prediction reproduces the server's **held movement**. Until now the client took one step
per input while the server takes one per base tick for a whole world interval, so the
client predicted a quarter of the server's motion at a 15 Hz send rate against a 60 Hz
base tick — a corrections-on-every-input defect that never once snapped.

Found by the assertion added in 0.12.2. That assertion was written to catch the tick-rate
mismatch and caught a second, unrelated defect with the same signature on its first live
run, which is the argument for asserting the healthy case rather than only the failing
one.

### The rule, from the server source

`InputHandler.ProcessInput` steps once on the input's own base tick and records the
direction as held. `InputHandler.ApplyHeldMovement` — called from `TickLoop` on **every**
base tick, including ticks where no packet arrived at all — steps again while
`baseTick - heldFrom < holdTicks`, where `holdTicks` is `_rates.WorldEvery`, four at
60/15. An explicit stop (`MoveResult.None`) clears the hold immediately; a rejected
vector leaves it alone.

One input at 15 Hz therefore produces four steps, `4 x 5/60 = 0.3333`, and the server
moves at the configured 5 u/s. The client produced `0.0833` and moved at 1.25.

### Fixed

- **The predictor is now tick-driven, like the server.** `Advance` runs whole base ticks
  and integrates the held direction on each, instead of moving only when an input is
  recorded. Replay reproduces the same timeline rather than one step per pending input,
  so the live path and replay agree and a reconcile no longer injects a correction the
  network did not cause.
- **The hold window is measured, not configured.** Snapshots are emitted once per world
  tick, so the gap between the base ticks two consecutive snapshots carry *is*
  `WorldEvery`. `TickRateEstimator.SnapshotTickGap` derives it and `WorldViewBinder` hands
  it to the predictor, so no consumer has to know the number and none can set it wrongly.
  The minimum gap is used rather than the mean: a dropped snapshot only widens a gap, so
  an average is biased upward by exactly the losses.
- **Until a hold has been observed, behaviour is unchanged** — `HoldTicks` is 1 and the
  predictor takes one step per input, as before. An unmeasured window is not guessed at.
- **The smoothing span from 0.12.3 is now conditional.** With a hold, steps arrive one
  timestep apart however slowly the client sends, so spreading them over the input
  interval would leave the rendered position permanently four timesteps behind its own
  simulation. That fix addressed a symptom whose cause was the missing hold; it is
  retained only for the no-hold case.

### Corrected

**The "Not fixed here" note in 0.12.3 was wrong and is withdrawn.** It claimed the server
also moves at 1.25 u/s and that the fix was raising the client's send rate to 60 Hz. The
server moves at the configured 5; only the client was slow. Raising the send rate would
have masked the shortfall at four times the input traffic per client and fixed nothing.
The error came from reading `applyMovement` in the drain loop, concluding one step per
input, and not noticing that the hold path runs outside the drain entirely.

This also settles the `0.2500` figure the measurement reported with prediction off: three
steps, which is what a snapshot sampling mid-hold shows. The original 0.3333 expectation
was right and the correction offered against it was wrong.

### Added

- `LocalMovePredictor.HoldTicks` / `SetHoldTicks(int)` — the observed window, on the same
  "values below 1 are not sent" rule the speed and tick-rate fields use.
- `TickRateEstimator.SnapshotTickGap` — base ticks between consecutive snapshots.
- `HeldMovementParityTests`, seven cases asserting the predictor against a restatement of
  the server's scheduling that drives the same `MovementSystem.TryMove`, so only the
  scheduling is compared and the arithmetic stays shared. Includes a direct assertion
  that one second of held input travels the configured speed: client and server can agree
  perfectly on a wrong number, and the absence of corrections cannot detect that.

### Known gap, upstream

`gameserver-dotnet/docs/API.md` states `dt = 1 / tick_rate` and "movement integrates once
per simulation tick". Both are true of the server and both read, on the client, as "once
per input". Nothing in the normative section says the newest input is held and
re-integrated until a world interval expires, or that a deadzone clears it. A client
implemented exactly to the document builds the predictor this release replaces. This is
the third server behaviour a client has had to infer from source; it belongs beside the
`tick_rate` contract and is being raised against the backend.

## [0.12.3] - 2026-08-15

Fixing the tick rate in 0.12.0 made the visible stutter worse, and this is the repair.

Render smoothing spread each step over the integration timestep. Until 0.12.0 that was
also the interval between inputs, because the predictor was built from the client's own
rate, so the two were the same number and nothing distinguished them. Once the timestep
came from the server's 60 Hz base tick while the client kept sending at 15, the whole
step was shown in the first 16.7 ms and the avatar then sat still for the remaining 50.
Measured at 300 fps: **150 of 200 render frames frozen**.

No counter could have shown this. The simulation was exactly right — the predicted
positions matched the server bit for bit and no correction was ever raised. Only the
rendering was wrong, and the only symptom was a user saying it did not feel smooth.

### Fixed

- **The smoothing span is now the observed interval between inputs**, not the integration
  timestep. It is measured rather than declared: the alternative is for the client to
  announce its send rate, which is one more constant free to drift from the truth, and
  drifting constants are the failure this area has now produced three times. Clamped
  below at the timestep so a burst cannot drive the span to zero; a gap longer than four
  intervals is treated as a pause and restarts the measurement rather than smearing the
  next step across the length of the idle.

  This remains interpolation. The span changes, the bound does not — progress still
  saturates at 1, so the rendered position never passes the step an input actually
  produced, however late the next one is. A longer span makes the avatar arrive later,
  never further.

  The first input after a connect or a pause is still shown over the timestep, because no
  interval has been observed yet and nothing can be measured from one sample.

### Added

- `LocalMovePredictor.ObservedInputInterval` — the measured cadence, for diagnostics. A
  value far from the client's intended send period means inputs are not being submitted
  at the rate the client believes.
- `RenderSmoothingTests.EveryFrameMovesWhenInputsAreSlowerThanTheIntegrationStep`, which
  is the test that was missing. Every other test in that fixture used one constant for
  both the integration timestep and the input interval, so the two were equal by
  construction and the entire class of defect was invisible to it.

### Not fixed here

One accepted input displaces `speed / tickRate`, and the server applies one step per
input received. A client sending at 15 Hz against a 60 Hz base tick therefore moves at
**1.25 u/s against a configured 5** — and client and server agree perfectly while doing
it, so no correction is raised and nothing in the package can detect it. The fix is for
the client to send at the server's base tick rate, which is four times the input traffic
per client and lands on the bandwidth budget in ADR-7. That is a project decision, not a
package one, and it is open.

## [0.12.2] - 2026-08-15

The instrument that exists to catch a tick-rate mismatch was itself running at the wrong
tick rate. `PredictionLatencyMeasurement` built its `PredictionSettings` from
`LiveBackendConfig.TickRate` — a constant defaulting to 15 — before it had connected, so
against the now-60 Hz server it predicted a step four times too long on every input. The
run it produced reported `corrections smoothed = 20` out of 20 samples and every other
number it printed was measured through that error.

This is the same defect 0.12.0 shipped a fix for in the consumer path, and the harness
kept its own copy of the constant. A measurement that does not obtain its parameters the
way the thing it measures obtains them is measuring a different system.

Found and diagnosed independently by @dyCuong03 in #36, which reached the same
ordering fix first. This lands over it because #36 did not compile — an escaping
artifact in its report string — and because the cross-check against the measured
rate was still missing. The framing of the defect below is theirs.

### Fixed

- **The measurement now connects before it builds the predictor.** The timestep comes
  from the join response, so there is no longer an order in which a predictor can exist
  without the server's rate. `PredictionSettings.FromServer` is used exactly as the
  sample uses it, with `LiveBackendConfig.TickRate` demoted to the fallback it always
  should have been.

### Added

- **The rate in use is printed, and the measured rate beside it.** Each configuration
  reports `TICK RATE IN USE ... (advertised by the server)` or `<- FALLBACK, server
  advertised none`, then the rate `TickRateEstimator` recovered from snapshot arrivals
  and whether the two agree. The previous header printed the configured constant as
  though it were operative, which is precisely how this went unnoticed.
- **Three assertions on the healthy configuration**, so a repeat fails rather than
  reports: the measured rate must not disagree with the rate in use, `Snaps` must be
  zero, and `SmoothedCorrections` must not exceed a quarter of the samples. The last
  carries the note that when it last fired it was a 4x tick-rate mismatch that no other
  counter showed — every individual correction was 0.25 units, under the 0.5 snap
  threshold, so the failure smoothed silently instead of snapping.
- **The expected displacement per input is printed next to the largest frame jump**, as
  `speed / tickRateInUse`, with the observed/expected ratio and a marker when it exceeds
  1.5. This is instrumentation for an open question rather than an answer to it: a
  prediction-off run reported a largest frame jump of `0.2500` where one accepted input
  at speed 5 on a 60 Hz integration step should displace `0.0833`. The server applies one
  movement step per received input — `TickLoop` sets `applyMovement` only for the newest
  input per handle per tick — so three steps' worth of displacement between consecutive
  snapshots is not accounted for by the send rate alone. The ratio is now in the output
  instead of being reconstructed afterwards from numbers that had to be assumed.

## [0.12.1] - 2026-08-15

The smoothness figure was not comparable between runs, and the way that surfaced is worth
recording: two runs of the same build reported a largest single-frame jump of **0.0149**
and **0.0244** world units — a 60% spread that looks like measurement noise.

**It is not noise. It is frame rate.** Those values imply **336 fps and 205 fps**, and a
smoothed step necessarily divides into larger pieces when there are fewer frames to divide
it across. The metric was frame-rate dependent by construction, so it could not be compared
between runs, between machines, or between a developer's Editor and a player's build.

### Added

- **`FrameDeltaBurstiness` — worst frame ÷ average frame.** **1.0 is perfect**: every frame
  moved the same distance. Unsmoothed motion puts a whole step on one frame and nothing on
  the rest, so the ratio becomes the number of frames per input interval. This is the
  number to quote; the raw distances are kept for context and now print the frame rate
  beside them so nobody compares two of them without noticing.

- **`ObservedFps`**, measured over the sampled frames.

### Note

The harness has now produced two figures that needed explaining rather than reporting —
this one, and a "forced divergence" that forced none. Both were caught by someone asking
why a number looked odd rather than by anything automatic. **A measurement tool needs its
own scepticism applied to it**, and the useful habit is checking whether a suspicious value
has a mechanical explanation before treating it as data: 0.0149 versus 0.0244 was one
division away from being obvious.

## [0.12.0] - 2026-08-15

0.11.0 read the advertised tick rate. This makes the client **verify** it, and makes the
fallback **observable** — both now required by the normative contract in
`gameserver-dotnet/docs/API.md`, which landed after 0.11.0 shipped.

### The contract, and where 0.11.0 fell short of it

> *"`tick_rate` is the rate at which the authoritative simulation tick advances — which is
> also the rate at which player movement is integrated. A client MUST use
> `dt = 1 / tick_rate`."*

Read from the doc rather than relayed. Three clauses bear on the client, and 0.11.0
satisfied one and a half:

| Clause | 0.11.0 | now |
|---|---|---|
| MUST NOT assume a constant | ✅ | ✅ |
| **SHOULD measure the rate**, and cross-check it even when advertised | ✗ | ✅ |
| MAY fall back **only if observable** | sample logged it; package could not express it | ✅ |

The fallback rule is the one I had wrong. I had been told to mirror the `speed` rule
exactly — silent fallback to a configured value — and the doc is stricter for a stated
reason worth keeping: **`speed` is per-entity and a wrong value is bounded by that
entity's real speed; `tick_rate` scales every predicted displacement by a whole ratio.**
15 against 60 is 4× per input, which lands under the correction threshold and smooths
rather than snaps. Same "zero means not sent" encoding, deliberately not the same silent
fallback.

### Added

- **`TickRateEstimator`** — recovers the base tick rate from snapshot arrivals:
  `(tick₂ − tick₁)` over the wall-clock interval. **This works even though snapshots
  arrive at the slower world rate**, because the `tick` they carry is a *base* tick: at 60
  simulated and 15 sent, successive snapshots are four ticks apart and the arithmetic still
  yields 60. A client that measured 15 here would predict at a quarter rate, so that case
  is the one the tests lead with.

  Requires a 1-second window and 5 samples before offering an estimate — a shorter window
  divides a small tick delta by a small jittery interval and produces a confident-looking
  number that is mostly scheduling noise.

- **`WorldViewBinder.TickRate`** — the estimator, fed from the one place that already sees
  every snapshot and owns a clock. Every consumer gets the cross-check for free.

- **`PredictionSettings.FromServer(advertised, fallback, speed, bounds)` and
  `TickRateIsFallback`**, mirrored on `LocalMovePredictor`. The flag *is* the observability
  the protocol requires — a counter a caller can surface — so the package can now express
  the rule rather than leaving each consumer to remember it.

- **The DOTS sample warns on fallback and verifies the advertised rate against the
  measured one**, once per session, logging either a confirmation or an error naming both
  numbers.

- **`TickRateEstimatorTests`** — 8 cases, leading with 60-simulated/15-sent.

### Note on trust

Verifying an advertised value against an independent measurement is not defensiveness for
its own sake. A wrong tick rate produces **no symptom anyone can name**: it is wrong by a
fixed ratio on every input, under the smoothing threshold, forever, with every counter
reading healthy. It is the third defect of that shape in two days. A second, independent
observation is the only thing that catches it, and the wire was already carrying enough to
make one.

## [0.11.0] - 2026-08-15

**The client took its prediction timestep from a local constant. The server moved movement
integration to 60 Hz. Nothing on either side noticed.** Minor rather than patch because
`JoinTokenResponse`, `GameSessionClient` and `NetworkClient` all gain a `TickRate`.

### The defect this closes

Backend `develop` now runs multi-rate — critical 60 Hz, world 15 Hz — and
`InputHandler` is constructed with `rates.CriticalHz`, so **movement integrates at
`dt = 1/60`**. The client predicted at `1/15`, four times the distance per input.

**The magnitude is what made it dangerous rather than obvious.** Measured:

| inputs in flight | correction | vs the 0.5 u smoothing threshold |
|---|---|---|
| 1 | **0.2500 u** | **under — smoothed, no visible snap** |
| 5 | 1.2500 u | over — snaps |
| 15 | 3.7500 u | large snap |
| *(rates matched)* | **0.0000 u** | — |

So it would not look broken. It would feel **soft and slightly wrong continuously**, with
an occasional jump once several inputs were in flight — *"still not smooth, and it jerks
occasionally"*. Every counter would have read healthy, and the user would have blamed the
prediction work. This is the third "the client assumed a server constant" defect in two
days and the second to hide beneath the smoothing threshold.

`staging` is unaffected: it was cut before the multi-rate change, so it is a consistent
15 Hz on both sides. The break exists only on `develop`.

### Added

- **`JoinTokenResponse.TickRate`**, decoded from `wire.proto` field 4 in both codecs, and
  surfaced as `GameSessionClient.TickRate` and `NetworkClient.TickRate`.

  **Zero means "not sent", not "no ticks"** — the same rule as `EntitySnapshot.Speed`,
  deliberately identical. It is the same situation, and a second convention for it would be
  a trap of its own.

- **The DOTS sample builds its predictor after the join, from the advertised rate**, with
  the configured value as fallback. `inputRateHz` is explicitly *not* reused for this: how
  often this client sends is a client choice, the integration rate is the server's, and
  conflating them is what made the constant look shareable.

- **`AMismatchedTickRateProducesACorrection`** pins the magnitude at **0.25 u** and asserts
  it is *below* the smoothing threshold — documenting the trap in the test rather than
  only in prose, so the reason this is hard to see is visible where someone will read it.
  Plus `AMatchedTickRateProducesNoCorrection` for the other side.

### Changed

- `Runtime/Protocol/Generated/Wire.cs` regenerated with libprotoc 29.3. Diff is field 4 and
  the descriptor blob, nothing else. Per [#20](https://github.com/Cuvara/Netcode/issues/20)
  I checked the committed file afterwards rather than trusting protoc's exit code — it
  wrote flat this time, which means the `mv` step documented in 0.7.0 describes only one of
  protoc's two behaviours. **The reliable step is checking the file, not the recipe.**

### Not changed, deliberately

**The sample still sends input at 15 Hz into a 60 Hz drain.** That is legitimate — three of
four base ticks simply carry no input — but it changes the superseded-input behaviour
documented in 0.5.0, so it is a second variable. One thing moves at a time: land the rate
decode, measure, then decide the input rate separately.

## [0.10.4] - 2026-08-15

**The measurement harness's "forced divergence" configuration forced no divergence.** It
reported a correction of exactly zero on a live run, which read as reconciliation being
broken. It is not broken; the configuration was measuring a case that legitimately yields
zero.

### The diagnosis

The clue was in the run's own output: `input -> authoritative: no usable samples`. **An
input that is never sent is never acknowledged, so it is never removed from the pending
buffer** — every reconcile replays it on top of the authoritative position and reproduces
the prediction exactly. Correction is zero because nothing has diverged. From the client's
side a dropped input is indistinguishable from one still in flight, which is what it is.

Reproduced in isolation:

| Configuration | Correction | Pending | Replayed |
|---|---|---|---|
| dropped, still pending *(what the harness did)* | **0.000000** | 1 | 1 |
| acknowledged but not applied | 0.333333 | 0 | 0 |
| predicted vector ≠ sent vector | 0.333333 | 0 | 0 |

The first row is the live case, and `replayed=1` shows `Reconcile` ran with the server's
position and correctly found nothing to correct. The earlier out-of-Unity experiment
measured the *second* row and called it "input superseded"; the harness was then built
around *dropping* the input instead of *acknowledging it unapplied*, which is a different
thing.

**So the reading that a zero correction on the healthy run is bit-exactness stands.** This
configuration never contradicted it.

### Fixed

- **The divergence run now sends a zero vector while predicting a non-zero one.** The
  server acknowledges the tick — so the input leaves the buffer — having moved nowhere,
  and the disagreement is real and permanent. It also fixes
  `input -> authoritative: no usable samples`, because the tick is actually sent.

  **The guard was not relaxed.** It is still `MaxCorrection > 0`; what changed is that the
  configuration behind it now produces a divergence to detect.

- **An orphaned `<param>` tag**, stranded onto `FirstUnreachableAsync` when 0.10.3 inserted
  the reachability probe between a docstring and its method. Warning-level, so CI compiled
  around it.

### Added

- **`LocalMovePredictor.Reconciles`** — times `Reconcile` folded in an authoritative
  position. `ReplayedSteps` alone cannot answer "is reconciliation running", because a
  reconcile with nothing pending replays nothing; conflating the two is what made the live
  result look like a broken loop. The seeding call is deliberately not counted, so a
  nonzero value means a real reconcile rather than initialisation. The harness reports it
  and asserts on it in the divergence run.

- **Three EditMode tests pinning the distinction**, so it does not have to be re-learned
  live: `AnUnacknowledgedInputIsNotADivergence`,
  `PredictingADifferentVectorThanWasSentDiverges`,
  `ReconcilingWithNothingPendingStillCounts`.

### The live numbers this does not affect

From the same run, and they stand: **input → visible 56.0 ms → 0.1 ms**, and **largest
single-frame jump 0.3333 → 0.0149 world units, a 22× reduction** — the 15 Hz stutter
measured in-engine for the first time, against 0.0143 predicted out of Unity.

## [0.10.3] - 2026-08-15

**The live-backend measurement failed a consumer's CI.** It is a test, not runtime code —
but it shipped in the package, so it is the package's problem.

### Fixed

- **`PredictionLatencyMeasurement` now skips, with a reason, when no backend is
  reachable.** It was gated only by `[Category("LiveBackend")]`, and **a consuming project
  runs the whole PlayMode suite without filtering by category**, so the gate did nothing
  there and the test failed with `Cannot connect to destination host`. **A package cannot
  rely on a consumer's runner passing the right filter** — correctness has to live in the
  test.

  A cheap bounded TCP probe (1.5 s, not the auth flow) checks the gateway and Nakama; if
  either is unreachable the test calls `Assert.Ignore` naming which one and where it
  looked. **Any exception from the probe is treated as unreachable, never as a failure** —
  a throw from the probe is the same situation as a refused connection, and surfacing it
  would recreate the bug being fixed.

  **Ignore, not silent-pass.** An ignored test with a reason appears in the report and
  names what is missing; a test that quietly goes green by doing nothing is the failure
  this repository has spent two days eliminating, and it is not being reintroduced in the
  one place whose job is producing honest numbers.

  **The category is kept.** It lets someone deliberately select or exclude the test; the
  Ignore makes it safe when nobody does either.

  **Nothing else was weakened.** With a backend present it still asserts everything it
  asserted before — all seven guards, including `ReplayedSteps > 0` and the forced
  divergence. The change is "skip when there is nothing to measure", not "assert less".

### Note

I predicted this failure mode when the harness landed — *"it cannot run in CI, so gate it
behind a category or a define"* — and then implemented the half of the gate that depends on
the consumer cooperating. Foreseeing a problem and shipping a mitigation that only works
under your own configuration is not much better than not foreseeing it.

## [0.10.2] - 2026-08-15

**The DOTS sample threw on its first frame in any project using the Input System package,
which is most Unity 6 projects.** It reached a user as *"I don't see any player or enemy
spawned"* — not degraded input, a dead sample.

### Fixed

- **`SampleMovementInput` read the legacy `UnityEngine.Input` API unconditionally.** Under
  `activeInputHandler: 1` (Input System package only) that class **throws** rather than
  returning zero, and the read is the first statement of `Update()` — so the exception
  took the connection, the spawn and the render down with it. The sample did not degrade,
  it died, and it failed as "nothing works" rather than "input does nothing", which cost
  two builds to diagnose.

  Now reads through whichever backend the project actually has, using the
  `ENABLE_INPUT_SYSTEM` / `ENABLE_LEGACY_INPUT_MANAGER` defines Unity provides for exactly
  this. The new backend is preferred when both are present. **A sample shipped in a package
  cannot dictate a consumer's Player Settings**, and requiring `activeInputHandler: 2` was
  doing precisely that — a project-wide setting with consequences well beyond this sample.

- **The input read can no longer take the bridge down.** It is wrapped, and a failure logs
  once and continues with zero movement: the client still connects, spawns and renders,
  and the local player simply does not move. Correct API selection should make this
  unreachable; it exists because the observed failure mode was *total*, and a sample whose
  input fails should still be a working sample.

- **`DOTSSample.asmdef` references `Unity.InputSystem`.** The sample now needs that package
  when the project uses the new handler.

### Added

- **A CI job that compiles the samples**, with the project set to `activeInputHandler: 1` —
  deliberately the strictest setting, because that is where the legacy API throws.

  **`Samples~/` is excluded from Unity's import, so nothing else in the workflow compiles a
  line of it.** 206 tests passed around a file that was read and never built. That gap is
  what let this ship. The job does not *run* the sample, so it would not have caught this
  particular runtime throw — but it closes the structural hole, and it catches every
  compile-time break from here on. Stated plainly rather than oversold.

### Honest note on what this means for earlier feedback

**No build anyone has tested has ever had working keyboard input.** The legacy call arrived
with the WASD wiring in 0.5.0 and has thrown in this project ever since; earlier builds were
driven by the scripted walk, which needs no input at all, so nothing surfaced. The user's
"less stuttering when moving" was therefore about autopilot motion, not about their own
input — worth knowing before reading that feedback as a verdict on responsiveness. The
~72 ms measurement is unaffected: it came from the PlayMode harness, which drives the
predictor directly and never touches the sample.

## [0.10.1] - 2026-08-15

Two things that should have been in 0.10.0 and were lost when it merged mid-edit.

### Fixed

- **The DOTS sample's `inputRateHz` defaulted to a literal `15` instead of
  `GameConstants.DefaultTickRate`.** It has to equal the server's simulation tick rate —
  the server integrates one step per accepted input at `1/tickRate` and applies only the
  newest when several land in one tick — so a drift between the two is not a preference,
  it is a desync. `NetworkBootstrapConfig` already defaulted from the constant; the
  sample did not, which made it the copy most likely to be wrong and the one a client
  team actually builds from.

  The mismatch does not fail loudly. The client is wrong by a little on every tick, is
  corrected by every snapshot, and the player sees rubber-banding rather than a
  misconfiguration — the failure `PredictionSettings` documents and refuses to guess its
  way into.

  **The field initializer is load-bearing here precisely because nothing serializes it.**
  `DOTSSceneSetup` adds `DOTSNetworkBridge` at runtime, so the scene carries no component
  and no stored value to override the default. Author the component into a scene instead
  and the serialized number wins, at which point this default stops applying and the
  scene has to be updated too — noted in the code so the next reader does not trust the
  initializer in a situation where it does not apply.

  The matching server-side half is `rpg-mmo-server#94`: `Program.cs` fell back to a
  literal `15` while its neighbours used `GameConstants`, so bumping the shared constant
  moved the client and left the server behind. Neither fix makes the rate observable —
  that is `rpg-mmo-server#93`, which proposes `tick_rate` on `JoinTokenResponse`.

  *(Moved here from `[Unreleased]` — this release tags it, so filing it as unreleased
  would be a heading that disagrees with what shipped. Entry unchanged; authored with
  `#27`.)*

- **Restored `#25`'s `SmoothingOffset` assertion in `SmoothedOffsetDecaysToExactlyZero`.**
  I had reverted it to the older `Position == SimulatedPosition` form on the grounds that
  interpolation makes that equality true again. That was half right: the equality does hold
  now, but `#25` replaced it for a better reason than the one I was answering — the intent
  under test is *"the correction settles at exactly zero"*, and the equality was a proxy
  that happened to coincide with it. **Both assertions now stand together**: the offset one
  states the intent, the equality additionally proves the step is fully shown. Losing a
  test improvement while replacing the implementation it came with is not a trade anyone
  chose; it was an accident of the swap.

### Documentation

- **The interpolation-vs-extrapolation trade is now on the record, with credit.** 0.10.0
  merged before the fuller version of that section landed, so the CHANGELOG explained the
  outcome without explaining the choice. It now states what each approach costs, why the
  user picked this one, and that `sample-runner`'s implementation did not lose on quality —
  it was measured, honest about its trade, and its author independently nominated its own
  overshoot as the likely cause of the user's symptom while investigating something
  unrelated.

## [0.10.0] - 2026-08-15

The other half of the user's complaint. `v0.9.x` made the avatar respond ~72 ms sooner;
this makes it move *continuously* while it does. Their words after the last build: *"I
feel less stuttering when moving now, but it is still not smooth — it still jerks
occasionally."*

### Fixed

- **The predicted position only advanced at the input rate, so the avatar stepped 15 times
  a second.** `_predicted` moves only inside `RecordInput`. At 15 Hz input and 350 fps that
  is ~23 identical frames followed by a jump of a whole step (0.333 world units at the
  default speed). Prediction fixed *where* the avatar is; nothing had addressed *how often*
  that is updated. It is the residue of removing local interpolation in 0.4.0 — that
  removal was right for latency and took the frame-rate smoothing with it.

  `Position` now walks back the unshown fraction of the latest step, spreading it across
  the frames of the interval.

### Interpolation within the step, never past it

The rendered position is bounded by **a step that was actually taken from an input that
was actually submitted**. It is interpolation, and that is the entire safety argument.

**When input stops, the avatar arrives at the predicted position and stops.** Carrying
motion forward on the last known direction would move it somewhere the player never asked
for, and the correction would land exactly when they released the key and were watching —
a worse artefact than the one being removed, at a more noticeable moment. Pinned by
`WhenInputStopsThePositionConvergesAndDoesNotOvershoot`, which asserts the bound on every
one of 100 frames across ten intervals of silence, and by
`StoppingInputLeavesThePositionCompletelyStill`.

### This does not give back the latency 0.4.0 removed

Motion now **begins** on the frame after the input rather than teleporting on it — a frame,
not an interval, and not a round trip. **Expect `input -> visible` to move from ~0.1 ms to
roughly one frame (~3 ms at 350 fps)** when the harness is next run. That is a real if
tiny regression in that metric and it is stated here rather than discovered: at 350 fps
the first frame already shows ~0.014 units of movement, well past the harness's detection
threshold. Against 72 ms it is not a trade anyone would decline, but it is a trade.

`SimulatedPosition` is untouched, so replay determinism and the bit-exact agreement with
the server are unaffected — pinned by `SmoothingDoesNotTouchTheSimulatedPosition`.

### Continuity, in the two places it can break

- **An input boundary that does not land exactly on time.** The unshown remainder of the
  previous step is carried into the render offset instead of being discarded, so a late or
  early input does not jump the avatar.
- **A reconcile mid-step.** The correction is now *added* to the outstanding offset rather
  than replacing it; overwriting would discard part of a step in flight — a small jump at
  snapshot rate, which is the exact artefact this release removes. A **snap** clears the
  remainder deliberately: it belongs to a step taken from a position the server has just
  ruled out, and replaying it would add a second, smaller wrong movement after the snap.

### Why interpolation rather than extrapolation — the trade, on the record

**Two reasonable implementations existed and one was chosen. This is the reasoning, so
nobody has to re-derive it.**

The step exists in full the moment `RecordInput` runs, so you cannot have all three of
*motion starts on the input frame*, *motion is continuous*, and *the rendered position
never leads the truth*:

| Approach | Onset | Continuous | Leads truth |
|---|---|---|---|
| show the step at once (pre-0.10.0) | immediate | **no** — 15 Hz stepping | no |
| spread it from where you **were** (this release) | one frame | yes | **no** |
| spread it from where you **are** (#25) | immediate | yes | **yes**, up to one step |

**`#25`, by `sample-runner`, took the third** and was honest about it: its own comment
records that the rendered position "started leading the simulated one". It renders ahead
of the last submitted input, so on **key release or direction change** it over-travels up
to a full step — 0.333 world units at the default speed — and eases back over ~250 ms
through the decay channel.

**This release takes the second.** The rendered position is bounded by a step actually
taken from an input actually submitted, so it never passes one and there is nothing to
come back from.

**Measured afterwards, the onset cost I claimed for this does not exist.** Both
implementations take exactly one frame (2.86 ms at 350 fps) to first visible movement,
because #25 also restarts its render step from zero at each input. I had asserted
interpolation cost a frame that extrapolation did not; it does not, and the "median must
not move" requirement is satisfied by both. Correcting it here rather than leaving a
favourable-sounding trade on the record that measurement does not support.

**A rejection worth correcting for the record**: extrapolation was justified partly on the
grounds that the alternative "would hand back ~66 ms of the latency prediction had just
bought". That is true of a *different* alternative — interpolating between the last two
predicted positions, which renders a whole interval in the past. It is not true of this
one, which spreads a step already taken across the frames that consume it. The cost is a
frame, not an interval.

**The user chose this one**, on the grounds that their complaint is jerkiness rather than
lag, and a systematic artefact at every key release and direction change is jerkiness. With
WASD, direction changes are constant.

**`sample-runner`'s version did not lose on quality.** It was measured, honest about its
trade, and — while investigating tick cadence for an unrelated reason — its author
independently nominated its own overshoot as the most likely cause of the user's
"occasionally jerks", which is the same suspect these tests were built to catch, reached
from the opposite direction. Its `SmoothingOffset` accessor and its correction to
`SmoothedOffsetDecaysToExactlyZero` are both kept here: the latter replaced a proxy
assertion with the intent it stood for, which is the better test whichever smoothing wins.

**Measured, both implementations driven through identical input at 350 fps** (client-side
only — no server, so no reconcile; see the caveat below):

| | interpolation (this) | extrapolation (#25) |
|---|---|---|
| onset to first visible movement | 1 frame, 2.86 ms | 1 frame, 2.86 ms |
| still frames during steady movement | 0.0% | 0.0% |
| **largest single-frame jump** | **0.0143 u** | 0.0305 u |
| **frame-delta std dev** | **0.00191** | 0.00367 |
| **overshoot past the last input on release** | **0.0000 u** | **0.3333 u — one whole step** |
| **wrong-direction excursion on reversal** | **none** | **0.2849 u** |

Both remove the 15 Hz stutter. **Interpolation is additionally about twice as smooth in
ordinary movement** — half the largest frame jump and half the jitter — which was not the
expected result and is the opposite of the concern that extrapolation might be smoother in
the common case.

The reversal number is the one that matters for the reported symptom: with WASD, direction
changes are constant, and extrapolation carries **0.285 world units in the direction the
player has already stopped asking for**.

*Caveat: these are client-side measurements with no server attached, so the release
overshoot has nothing to correct it and never settles here. In the live system a snapshot
reconciles it — `sample-runner` measured that recovery at ~250 ms. The overshoot magnitude
and the reversal excursion are pure client-side arithmetic and hold regardless.*

Run against this release's tests, the extrapolating implementation fails four:
`WhenInputStopsThePositionConvergesAndDoesNotOvershoot`,
`StoppingInputLeavesThePositionCompletelyStill`,
`TheStepIsFullyShownAfterOneInputInterval`, `ASnapClearsTheUnshownRemainder`. Those encode
the defended property and are the reason this replaced that.

### Added

- **`RenderSmoothingTests`** — 9 cases. Both properties are mutation-checked: removing the
  smoothing fails 2, allowing extrapolation past the step fails 3.
- **Smoothness measurement in the PlayMode harness.** Per-render-frame movement of the
  rendered position, reported as **percentage of frames with no movement at all**, largest
  single-frame jump, mean, and standard deviation. That is the stutter quantified — before,
  a long run of exact zeros punctuated by one whole step; after, a small near-constant
  delta every frame. **"Looks smooth" is not measurable and is not claimed.**

### Still open

The user said it jerks **occasionally**, which is a different signature from a steady 15 Hz
step and is probably a second cause. The harness already reports `corrections snapped`
versus `smoothed`; non-zero snaps during ordinary play would name it. Candidates, in the
order worth checking: a dropped or superseded input causing a snap, the `t = 1.2`
extrapolation cap on *remote* entities when a snapshot is late, client-side GC or
frame-time spikes, and the correction smoothing threshold being too coarse. **Measure
before fixing** — this release addresses the steady stepping only.

## [0.9.1] - 2026-08-15

**A guard added in 0.9.0 was wrong, failed on the first live run, and is replaced here
with one that can actually distinguish the two cases it was conflating.** The measurement
it was gating stands: prediction removes **~72 ms** from input-to-visible on localhost.

### The determination

0.9.0 asserted `MaxCorrection > 0`, calling an exact `0.000` *"the signature of the
predictor reconciling against its own output"*. The first live run failed it — and the
same run disproved the diagnosis: `replayed steps 3` means `Reconcile` fired and replay
ran, which is exactly what open-loop cannot do.

Settled by experiment rather than argument, against the real `Shared.GameLogic`:

| Condition | Correction | Replay |
|---|---|---|
| matched speed, all acked | **0.000000** | — |
| matched speed, **replay ran** | **0.000000** | 2 steps |
| client speed 4 vs server 5 | **0.235702** | — |
| input superseded by the server | **0.235702** | — |

Row 2 is the one that decides it: it reproduces the live condition — replay ran *and* the
correction was zero — in isolation, and shows that combination is healthy. On localhost,
with no loss and the shared library bit-exact on both sides, **zero divergence is the
designed outcome**; it is what ADR-10, the FMA-denying split in `Integrate` and the golden
vectors exist to produce. Rows 3 and 4 show the mechanism produces a correction the moment
the two sides genuinely disagree.

So `LastCorrection` never answered "is reconciliation alive?" — it answers "do the two
sides disagree?", whose healthy answer on a lossless link is *no*.
`ReplayedSteps` answers the first question, and already did.

### Changed

- **The `MaxCorrection > 0` assertion is removed from the healthy run** and replaced by a
  **third measurement configuration that deliberately diverges**: it predicts a sample
  input locally and never sends it, so the server cannot have applied it, and asserts a
  correction appears. That keeps the property the old guard was reaching for —
  corrections are provably not stuck at zero — without misreading agreement as failure.

  **A deliberately wrong *speed* would not have worked**, and that is worth recording: the
  wire carries per-entity speed since 0.8.0 and the binder feeds it to `SetServerSpeed`
  every snapshot, so a wrong configured speed is corrected back within one snapshot and no
  divergence survives. Dropping an input cannot be undone that way.

  The divergence run's timings are **not** comparable with the other two and are excluded
  from the comparison — dropping inputs delays acknowledgement by design. Only its
  correction is read.

### Added

- **`ReconciliationDivergenceTests`** — the four rows above, as EditMode tests that need no
  backend and run in CI. They pin both readings so the distinction cannot be lost again,
  including the case the old assertion misread.

### Note

The guard did its job by refusing to pass quietly, and then had to be shown wrong on
evidence rather than relaxed because it was inconvenient. Weakening it without the
experiment would have been indistinguishable, from the outside, from weakening it because
it failed.

## [0.9.0] - 2026-08-15

A PlayMode harness that measures what prediction actually removes, against a live
backend. Minor rather than patch because it adds a test assembly
(`Cuvara.Netcode.Tests.PlayMode`); no runtime code changed.

### Added

- **`PredictionLatencyMeasurement` — the measurement everything since 0.4.0 has been
  waiting on.** Connects to a live gateway + game server, runs the same scenario with the
  predictor enabled and disabled, and reports both with median, min, max, p90 and mean
  over 20 samples per configuration.

  Per sample: settle on zero input so movement is attributable to one input, submit a
  single input at tick `T` and stamp the clock, then record when the **view** is told a
  changed local position and when the first snapshot with `AckTick >= T` arrives.

  **Written here, run elsewhere.** The obvious route — build a player, press WASD, watch —
  is unavailable: driving it needs someone to click a map button and type, and the machine
  that can run the backend cannot do either. An in-engine harness is the only honest path
  left, and it has the side benefit of one driver on the Editor.

- **Guards that make the numbers mean something**, and the test fails without them:
  `PendingCount > 0`, `ReplayedSteps > 0`, `MaxCorrection > 0`, and `EffectiveSpeed`
  matching the server. **A predictor that never reconciles is indistinguishable by
  position alone from one that is perfectly accurate** — both look right — so timings
  alone would prove only that numbers were collected. An exact `0.000` correction across
  a whole run is the signature of reconciling against its own output rather than the
  server's.

- **`LiveBackendConfig`** — every endpoint overridable by environment variable, so a run
  can be pointed elsewhere without editing and recompiling.

### Naming, deliberately

**This does not measure keypress-to-visible and does not claim to.** That figure includes
the keyboard, the OS input stack and the display pipeline; it needs external capture and
nothing in-engine can see those legs. What is measured is **input-submitted → local avatar
moves on screen**, which is the whole of the interval prediction can affect. Those excluded
legs are constant between the two configurations, so the **difference** is unaffected by
their absence — the absolute figures are not a player-felt latency and must not be quoted
as one.

### Not covered by CI, stated rather than hidden

The CI job runs `testMode: EditMode` and never executes PlayMode tests. This assembly is
**compiled** there — which catches breakage and is worth having — but nothing in it runs.

**With no backend the test fails; it does not skip.** A test that turns green when its
dependency is missing is this repository's signature failure, paid for four times in two
days, and it is not being reintroduced in the one place whose entire job is producing an
honest number.

### Note

`NakamaDeviceAuth` duplicates `Samples~/DOTSSample/SampleNakamaAuth`. A `Samples~` folder
is excluded from Unity's import so its code cannot be referenced, and promoting it into
`Runtime/` would put a test convenience into the shipped package. The RPC body shape
(`"{}"`, not an empty string) was copied from the working sample rather than
reconstructed — it is the one part of the flow no compiler can check.

## [0.8.1] - 2026-08-14

Documentation only. Three sentences that **0.8.0 itself made false**, in the places
someone goes for authority on exactly this parameter.

### Documentation

- **`PredictionSettings` still said speed is "a per-entity server stat that no message on
  the wire carries today".** 0.8.0 put it on the wire and made
  `LocalMovePredictor.SetServerSpeed` consume it. Left alone, the next reader concludes
  they must maintain the value by hand and never looks for `SetServerSpeed` — in the
  class whose entire job is to warn about this parameter.

  Rewritten rather than deleted, because the paragraph is load-bearing: "speed is the
  fragile one, and a wrong value does not fail loudly — it rubber-bands" is still true and
  still why the type refuses to default anything. What changed is the remedy. `Speed` is
  now documented as the **fallback**, governing in exactly two situations it must still be
  right in: before the first snapshot, and against a server predating field 9.

- **`NETCODE.md` still carried 0.7.0's "Not yet consumed by `PredictionSettings`" note.**
  True when written, falsified by 0.8.0 one release later. Now states what actually
  happens and that `EffectiveSpeed` reports which value is live.

- **The DOTS sample's `playerSpeed` tooltip** repeated the same stale claim, in the
  Inspector — the one place a reader is holding the field while they read it.

### Note

All three were mine, written in 0.5.0 and 0.7.0 and falsified by my own 0.8.0. That is the
failure mode this package keeps finding in other people's code — a stale sentence in an
authoritative place is worse than no sentence — and shipping the fix that invalidates your
own documentation without re-reading it is how it happens. Found by `dots-builder`
checking a sample against the release, not by anything in CI, and nothing in CI could have
found it.

## [0.8.0] - 2026-08-14

Requires `com.rpgmmo.shared-gamelogic` **`sgl-v0.1.7`** or newer — that tag is what adds
`EntitySnapshotData.Speed`. Against `sgl-v0.1.6` this does not compile (`CS1729`,
`CS1061`), which is deliberate: a version of the client that silently dropped speed again
would be indistinguishable from one that never had it.

### Added

- **Prediction now uses the speed the server sends, closing
  [rpg-mmo-server#91](https://github.com/Cuvara/rpg-mmo-server/issues/91) end to end.**
  0.7.0 decoded `speed` off the wire into `ResolvedEntity`; this carries it the last hop
  through `WorldState` into the merger and into replay, so a buff, mount or slow no longer
  desyncs client and server silently.

- **`LocalMovePredictor.SetServerSpeed(float)` and `EffectiveSpeed`.** Additive on
  purpose. `Reconcile`'s signature is a cross-package contract enforced by
  `PredictionSurfaceContractTests` and driven from `com.cuvara.dots`, whose compiler
  errors cannot appear in this repository — adding a method breaks nobody, widening an
  existing one breaks a consumer with no signal here.

  **Non-positive is ignored**, because on the wire that means "not sent": proto3 elides a
  zero float, so a server predating field 9 is indistinguishable from a stationary
  entity. Accepting the zero would pin the predicted speed to zero against an older
  server and stop the local player moving — strictly worse than the drift being fixed.
  `PredictionSettings.Speed` remains the fallback, and `Reset` returns to it because the
  previous session's speed belonged to a different entity.

- **Five tests**, including `ServerSpeedStillMatchesTheServerExactly`, which asserts
  **bit-exact** agreement against a reference walk integrated at the server's speed —
  the same standard the rest of the replay tests hold, not merely "close".

### Changed

- **Minimum `com.rpgmmo.shared-gamelogic` raised to `sgl-v0.1.7`** — the tag that adds
  `EntitySnapshotData.Speed`. Bumped in all four live pins: `package.json`'s
  `x-manualDependencies`, the README install snippet, `NETCODE.md`, and **both** places
  the CI workflow writes it (the test project's manifest and the install probes).

  The CI pin is the one that matters and the one that caught this: the first run of this
  change went red because the workflow still bootstrapped `sgl-v0.1.6`, so the package
  it was testing could not compile. That is the gate doing exactly its job — a repo can
  pin its own dependency in five places, and a stale one in CI means the suite validates
  a configuration nobody ships. `NETCODE.md`'s other `sgl-v0.1.x` references are a
  history of past releases and are deliberately unchanged.

### Verified

- **54/54 out of Unity against the tagged `sgl-v0.1.7` source itself**, checked out at
  `d88213f` rather than against a branch that merely contains the change.
- **The dependency is demonstrated, not assumed**: the same tree against `sgl-v0.1.6`
  fails with exactly `CS1729` (no 7-argument `EntitySnapshotData` constructor) and
  `CS1061` (no `Speed` member).

## [0.7.0] - 2026-08-14

Decodes the per-entity `speed` the server now sends
([rpg-mmo-server#91](https://github.com/Cuvara/rpg-mmo-server/issues/91),
`wire.proto` field 9). Minor rather than patch because `ResolvedEntity` gains a field
and a constructor overload, and `Runtime/Protocol/Generated/Wire.cs` is regenerated.

### Added

- **`speed` decoded from both encodings into `EntitySnapshot` and `ResolvedEntity`.**
  It survives handle-only mentions, which is the case that matters: the server writes
  speed on every mention precisely so a delta is complete, and dropping it at handle
  resolution would leave speed correct once per keyframe interval and stale in between —
  the entity would still render, it would just predict at the wrong speed.

  Closes the last silent failure mode in prediction. Replay runs the same
  `MovementSystem.TryMove` the server runs, that needs a speed, and until now the client
  could only assume the spawn default. Any buff, mount or slow desynced the two with no
  error on either side; it presents as rubber-banding, which reads as a network problem.

- **`speed <= 0` means "not sent", not "immobile".** proto3 elides a zero float, so a
  server predating field 9 is indistinguishable from a stationary entity. The decode path
  deliberately **does not** substitute a default — it passes the zero through, and the
  fallback belongs to the prediction layer where the configured default lives. Trusting
  the wire value outright would let an old server pin the predicted speed to zero and
  stop the local player moving. `AbsentSpeedResolvesToZeroRatherThanAGuess` pins that the
  resolver does not invent a value.

- **`SnapshotSpeedTests`** — three cases: speed carried through resolution, speed
  surviving a handle-only mention, and an absent speed staying zero.

### Changed

- **`Runtime/Protocol/Generated/Wire.cs` regenerated** with libprotoc 29.3, the pinned
  version. The diff is field 9 and nothing else.

- **`ResolvedEntity` gains a 7-argument constructor**; the 6-argument form is kept and
  forwards with `speed: 0`. Additive, so no consumer has to change.

### Documentation

- **The documented `protoc` command was wrong and is fixed.** It omitted that protoc
  nests output under the `csharp_namespace`, so it lands at
  `Generated/RpgMmo/Wire/V1/Wire.cs` and must be moved to the flat committed path.
  `--csharp_opt=base_namespace=` does **not** flatten it — the backend's `generate.sh`
  passes that flag and its output is nested too. Following the command as written left
  the committed file untouched while appearing to succeed, which is how a stale copy gets
  shipped.
- **Stated that nothing in CI diffs this generated file.** The backend has a
  `proto-generated-up-to-date` job over its two copies; this third one is regenerated by
  hand, and a stale one decodes cleanly while silently ignoring any field added since.
- The prediction section's "speed is the weak joint" note is updated: the wire carries it
  now, and what remains is named precisely — see below.

### Known gap

**`PredictionSettings` does not consume the wire speed yet.** The remaining hop is
`WorldState` → `Shared.GameLogic.EntitySnapshotData`, which needs a `Speed` field on that
type. It is implemented on the backend but reaches this package only through the pinned
`com.rpgmmo.shared-gamelogic` UPM tag, and tagging that library is a release action owned
by the lead (backend `TEAM.md`). Until the tag moves, everything above is plumbing
waiting for its last connection and `PredictionSettings.Speed` remains the caller-stated
value. Recorded here rather than left implicit, because a half-connected feature that
looks complete is the failure mode this changelog keeps documenting.

## [0.6.2] - 2026-08-14

Makes `LocalMovePredictor`'s cross-package surface enforceable instead of merely
documented, and writes down the two ownership rules that the DOTS integration depends on.
No behaviour change.

### Added

- **`PredictionSurfaceContractTests` — a gate on the six members `com.cuvara.dots`
  drives.** `RecordInput`, `Reconcile`, `Advance`, `Position`, `IsEnabled`, `Reset`.

  **This exists because the break is otherwise invisible here.** The DOTS adapter
  references `Cuvara.Netcode.Runtime`; netcode must never reference it back, so the
  adapter is not built in this repository and **its compiler errors cannot appear in this
  repository's CI**. Rename `Reconcile` and everything stays green; the failure surfaces
  in another repo, whenever someone next compiles it.

  Two halves, both checked by mutation:

  | Change | Caught by |
  |---|---|
  | `Reconcile(Vec2, long)` → `(Vec2, int)` | compile error at the call sites, immediately |
  | `Advance(float)` → `Advance(double)` | **only** the reflection assert — every existing call still compiles via implicit widening |

  The second is the one worth having. A widening that compiles everywhere on this side is
  exactly the "harmless tidy-up" that reaches a consumer as a hard break.

- **A test that the predictor's surface names no Unity or DOTS type**, and that
  `Cuvara.Netcode.Runtime` does not reference `Unity.Entities`. That is what keeps the
  dependency one-directional and the algorithm testable in EditMode without a World.

### Documentation

- **One predictor instance, constructed at the composition root and injected.**
  `RecordInput` is called by whatever sends input, `Reconcile` by whatever consumes
  snapshots — a binder here, or a system in the DOTS package. Two instances is silent:
  the recording one is never reconciled and drifts, the reconciling one has an empty
  buffer, replays nothing, and returns the authoritative position every time. Nothing
  throws, `PendingCount` is legitimately zero, and the symptom is that prediction appears
  to do nothing — so the search starts in the replay arithmetic, which is correct.

- **The DOTS driving example now uses the real spelling**, verified against
  `com.cuvara.dots` rather than sketched: `ReconciliationAnchor.ServerPosition` (the raw
  `(x, y)` stored verbatim before any arithmetic) converted with `SimConversions.ToVec2`,
  paired with `WorldState.AckTick`. The world-space `Position` field on the same component
  is what `LocalTransform` wants and is **not** what the predictor wants.

- **`PredictedTransform` must be released when prediction stops** — spectate, death, or
  `IsEnabled == false` — or `LocalTransform` has no writer at all and the entity freezes.
  That is the marker's own failure mode reached from the opposite direction, and it shows
  up in a build rather than in CI.

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
