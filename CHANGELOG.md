# Changelog

All notable changes to the Cuvara Netcode package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

> **The defect: a constant that was correct, used for something it does not describe.**
>
> Every send loop in this package took its cadence from `GameConstants.DefaultTickRate`, under a
> tooltip reading *"matches the server's simulation rate"*. It does not. The server advertises
> `JoinTokenResponse.tick_rate = SimulationRates.MovementHz = CriticalHz`, which is **60**. The
> 15 in that constant is `WorldHz` — the World group's rate, which is also **the cadence the
> snapshot broadcast runs at**. So the client was not sending at the simulation rate. It was
> sending at exactly the *snapshot* rate, and that is the one cadence at which the pipeline
> constant cannot be measured at all.
>
> The constant was never wrong. Nothing about `DefaultTickRate = 15` needed changing, and it has
> not changed. What was wrong was the *use*, and a wrong use of a right constant does not fail a
> review that checks the constant. This is the same shape as 0.34.0's release theme — reasoning
> about one property and gating on another — arriving one layer down, in a value rather than in a
> guard, and found by reading rather than by a live run.
>
> **A fifth sibling for 0.34.0's list of failure modes, distinct from the four already there: a
> simulation whose idealisation removed the very quantisation the defect is made of.** The first
> model of this fix used an ideal timer. It produced 12 Hz reading `UnsweptSeconds` 16.67 ms and a
> lead of 0.48 against 13 Hz's 5.13 ms and 1.17 — a clean argument for coprimality, and a
> description of a client that does not exist. It had already reached this changelog before it was
> re-measured with acknowledgements read on render frames, where **11, 12, 13 and 14 Hz are
> indistinguishable at 60 fps** because the frame period is coarser than any of their phase
> spacings. The model was of the right system and the wrong machine.
>
> This is not the "fixture written from the code's model of the wire" failure — that one shares the
> code's assumption. Nor is it a reading believed for the wrong property. **An idealised model is
> wrong in the direction of the thing being idealised away**, and here the quantisation removed for
> tractability *was* the mechanism: the same frame grid that hides the coprime advantage is what
> made the cadence unreachable in the first place. Defence: when the defect is made of
> quantisation, a model without it cannot be evidence about the fix — and the conclusion has to be
> stated for the regime it was measured in. **Coprimality is chosen for the regime where it can
> matter and costs nothing where it cannot, not because it improves the reading on the machine this
> shipped from.**
>
> **A statistic computed from a fit that includes the suspect point cannot detect that point.**
> Three detectors for sparse left-tail contamination were built and all three failed, and it took
> the third to see that they had failed for one reason rather than three. The maximum residual
> understates a dropped `q00`; a leave-one-out variant *flips sign* as contamination grows; and
> `q00 − intercept` has no resolution at all — clean runs read +0.01…+0.05 and a run with one bad
> observation in 128 reads −0.11…+0.11. In every case **the contaminated point is inside the fit,
> so the line follows it down and the discrepancy the test looks for is absorbed by the thing it
> is measured against.** Detection has to come from data the fit excludes, or from outside the fit
> entirely — which is what `AckAheadOfSend` is, and why a counter that is *narrower than the
> question* is the only working left-tail instrument in the package. Generalised: **when a test and
> the thing it tests share an input, agreement is not evidence.**
>
> **A retracted instrument leaves the question it was built for standing, and the question keeps
> recruiting designs that assume an answer exists.** When the only detector for sparse left-tail
> contamination was withdrawn, the *question* it had been built for — "is the low tail clean?" —
> stayed on the table, and **two people independently specified experiments that separated an
> outcome nothing could separate any more**, one of them the person who had written the
> retraction an hour earlier. Noticing this takes more than care: the retraction was explicit,
> recent, and agreed, and it still did not propagate to the next design. The defence is not to
> remember harder. **Retract the question with the instrument, or write the gap into the output** —
> a single line in a report saying "this is undetectable by anything present" stops a reader who
> a remembered retraction does not, because it is where they are looking. It is the same move as
> the run precondition gate: put the check where the reader is, not where the knowledge is.
>
> **Prefer operations that cannot be partially wrong; where you cannot have that, build the check
> the measurement lacks.** Five times in one day an operation touched more than it was aimed at.
> Four were silent — a server unregistered in Redis so the gateway routed elsewhere, a stale test
> assembly, an environment that never crossed the WSL boundary, a stale results file — and each
> produced internally consistent numbers about the wrong object. The fifth, a version-pinning regex
> that matched every git dependency instead of one, **failed loudly and immediately**. The
> difference is not luck: package resolution carries a *total* correctness check, since every
> dependency must resolve, while a measurement carries none and will report faithfully on whatever
> it was pointed at. That asymmetry is what the run precondition gate exists to close, and it
> fired on its first deliberate test.
>
> **And one no-op in the opposite direction.** A cadence change alone greps clean, reads as a fix
> in the diff, and does nothing at all on the target machine, because the defect lives in the loop
> shape rather than in the constant. It was caught only because the recommendation was run against
> the real estimator before it was proposed. **Verify a recommendation the way a defect is
> verified.**

### Added

- **`AckAheadOfSendInduction`, a PlayMode test that induces the one left-tail cause this package
  can count, and reports which of four outcomes happened.** `AckLatencyEstimator.AckAheadOfSend`
  guards against an acknowledgement naming a tick the session never sent — a reconnect onto a
  server that has not reaped the previous session's `LastInputTick` — and it has read **0 on every
  run since it was added**. A guard nobody has watched act is indistinguishable from one that
  cannot act, which is the standard the run precondition gate was just held to by a deliberate
  failure test.

  It reports a **precondition block before any verdict**, in that gate's style: the last input
  tick before the disconnect, the first `ack_tick` after it, **the max sent tick at the instant
  that acknowledgement was observed**, and the elapsed time. The condition is induced iff the
  stale acknowledgement exceeds *that* — the quantity the guard actually tests — and **not** the
  first post-reconnect input tick, which is the obvious comparison and the wrong one: with a
  previous session ending at tick 5, a client that has sent 1–6 while awaiting its first snapshot
  makes `5 > 6` false and the guard correctly silent, while the obvious form reads `5 > 1` and
  would report a `Runtime/` defect. The test built to catch a precondition check that does not
  check the precondition must not contain one, so the walkthrough is in its remarks.

  Four outcomes, only one of which bears on whether the condition arises at all: **induced and the
  guard fired** (a result); **induced and it did not** (a `Runtime/` failure, not Inconclusive —
  and its message says so, including that the left-tail question then reopens from a different
  direction because this counter is the only working left-tail instrument in the package);
  **the server still held the old state but the induction was too slow** (null about the *method*);
  and **the server had already reaped the player** (the condition did not exist — the only reading
  that is evidence, and only when repeated). It also states in its own output that a left-tail
  contaminant from any other cause is undetectable by anything present, so that limitation travels
  with the instrument rather than in someone's memory.


- `Samples~/ClockSyncProbe` gains a send-cadence panel: a cadence slider, **a nominal-versus-achieved
  rate readout**, a live phase histogram over the same eight divisions
  `AckLatencyEstimator.SweepBuckets` counts, and the sweep verdict. The achieved rate is read from
  `LocalMovePredictor.ObservedInputInterval`, which has always measured it and which **nothing
  read** — a measurement nobody reads is the same defect as a counter that reads zero for two
  reasons, and this is the single line that would have caught the loops sending 12 Hz while their
  configuration said 15. The panel also names the frame-rate bound, and says so explicitly when
  the frame rate rather than the cadence is what is blocking a reading.
  Extended rather than given its own sample because it is the same story told to the same reader —
  a clock/fit panel already lives here. **Drag the cadence to 15 and the histogram collapses to one
  bar while the verdict flips to REFUSED.** A lock is not a subtle statistical condition on screen;
  it is one bar.

- `PredictionLatencyMeasurement` reports the **acknowledgement quantile ladder** — `q = 0, 0.10,
  0.25, 0.50, 0.75, 0.90` off one ring in one pass — with an ordinary least squares fitted through
  it, plus the client frame rate that bounds all of them. **The point is to stop choosing between
  statistics.** If one observation is `constant + wait` and the wait sweeps a snapshot interval,
  the quantiles are affine in `q`: the **slope** says whether the sweep covered the whole interval,
  and the **intercept** is the pipeline constant recovered independently of any single quantile —
  which is what the open question about `FloorPercentile` actually needs. A ladder that is not
  straight *falsifies* the model rather than returning a plausible number from it, which is a
  property two order statistics could never have; the reported residual is the test, and its
  tolerance is a fraction of the fitted slope rather than a constant somebody can tune. Both
  numbers were already computable and neither was shown.

- **`PredictionLatencyMeasurement` now refuses a run that measured a server other than the one it
  was configured for**, and reports which of its settings came from the environment rather than
  from a default. **Three times in one day an experiment ran to completion against the wrong
  object and produced internally consistent numbers**: a game server that never registered in
  Redis, so the gateway routed elsewhere; a `dotnet test` that silently re-ran a stale assembly;
  and a snapshot-rate experiment whose environment never crossed the WSL-to-Windows boundary
  (no `WSLENV`), so it measured the 15 Hz server while the 30 Hz one sat idle at
  `players_online 0`. In each case the output looked exactly as a *successful* run had been
  predicted to look, which is why reading the log is not a sufficient check.

  Two halves, because neither covers the other: the gate compares the measured snapshot gap
  against the gap the configured rate implies and returns Inconclusive on a mismatch — catching
  **misrouting**; the provenance line names which environment variables were actually present —
  catching **an override that never arrived**, where configuration and reality agree because both
  are the default. The comparison is in **base ticks**, deliberately, because a tick count is
  skew-invariant: comparing measured Hz against configured Hz would false-fail on a fast-clocked
  client, which is the very arm where the ladder is still readable. Its tolerance is a quarter of
  the expected gap — wide enough that a wobbling estimate does not make this the gate people
  disable, narrow enough that a doubled or halved interval cannot pass.


- **`LocalMovePredictor.Adoptions` — the third reconcile outcome now has a name and a
  counter.** A reconcile was documented and instrumented as having two outcomes: answered
  from the history (`HistoryHits`), or fallen back to replaying the ticks the server has not
  seen (`ReplayedSteps`). There is a third. When the history misses *and* the fallback finds
  nothing to rebuild — an empty pending buffer, and a snapshot tick that is not behind the
  client's clock, so the held-forward path of #53 does not fire either — the predictor
  replaces its prediction with the authoritative position outright and throws the whole
  prediction lead away.

  **That is the largest correction this class can make, and it moved no counter.**
  `ReplayedSteps` stood still, because nothing was replayed. `HistoryHits` stood still,
  because nothing was compared. The only reading that changed was `HistoryMisses` — whose
  own summary says the reconcile "fell back to replaying", which is exactly what did not
  happen. A live run reporting `reconciles from history 115 hit, 34 missed` reported
  `replayed steps 2`: **32 wholesale adoptions, invisible on every instrument the class
  had.**

  **Why it stayed invisible is the part worth keeping.** The measurement report printed
  `replayed steps 0   (zero is the HEALTHY reading …)` and `PREDICTION.md` said the same in
  prose. Both were written when replaying was the only fallback there was, and both were
  still *true of the case they were written about* — a client whose clock tracks the server
  hits the history every time and legitimately replays nothing. What changed underneath them
  was not the sentence but the set of things a miss could do, and neither text was gated on
  the miss count. So the reading that meant "everything is fine" and the reading that meant
  "the lead is being discarded fifteen times a second" printed as the same zero, under a
  note asserting the first.

  `Adoptions` is measured by comparing `ReplayedSteps` across the fallback rather than by
  testing the branch conditions, so a replay that runs its loop and produces no step — a
  lapsed hold, a movement model refusing every step — is counted as the adoption it is.
  What is counted is the outcome, not the route to it.

- `adopted wholesale` line in `PredictionLatencyMeasurement`'s report, beside `reconciles
  from history`, printing the count against the miss count so the next live run reads the
  two together.

- `ReconcileAdoptionTests`, pinning all four reconcile states through the public surface: a
  hit never adopts; a miss with pending input replays; a miss whose snapshot is *behind* the
  clock rebuilds the held lead (#53) and does not adopt; and a miss with nothing to replay
  adopts, with every pre-existing counter asserted to stand still — which is the defect,
  stated as a test.

### Changed


- **Direction changes now reach the server up to 10 ms later: +5 ms mean, +10 ms worst case.** This
  is a real cost in feel and it is accepted deliberately, because the term it buys is currently
  worth multiple base ticks of standing reconcile error. Recorded here so it is a trade on the
  record rather than a silent regression. The uplink packet rate also falls ~13%, and because sends
  are now strictly slower than acknowledgements arrive, `AckLatencyEstimator.Superseded` goes to
  zero.
- The server is unaffected by the slower cadence, and this was checked rather than assumed. Its
  movement model integrates the newest held direction once per base tick whether or not a packet
  arrived (`ApplyHeldMovement`: *"never on how many input packets a client sends"*), and the hold
  expiry is a 250 ms **silence** timeout rather than a send-rate window. A stall still takes four
  consecutive lost packets at 13 Hz exactly as it did at 15; the tolerated silence is identical.
- The four fixed harnesses (`E2ECertification`'s three, `WorldView`) stay pinned at 15 Hz on
  purpose — changing what a certification harness measures as a side effect of a cadence fix is not
  something to do quietly — and each now carries a comment saying so and pointing at `InputCadence`,
  so the disagreement with the default does not read as an oversight to be tidied away.


- **The `replayed steps 0` remark is corrected rather than removed**, in both the report and
  `PREDICTION.md`. It claimed zero replayed steps was "the HEALTHY reading" outright; it is
  healthy **only when the miss count is zero**. `PREDICTION.md` now quotes the old claim,
  says why it was wrong, and gives the three outcomes in a table — a reader who remembers the
  old advice finds it addressed instead of finding silence.
- `NETCODE.md`'s measurement-guard table said the harness asserts `ReplayedSteps > 0`. It has
  asserted `HistoryHits + ReplayedSteps > 0` since the history path landed. Corrected, and
  `Adoptions` is documented as deliberately excluded from that sum: an adoption compares
  nothing, so a run made entirely of adoptions has run its reconcile loop and still never
  tested prediction against the server.

### Fixed


- **The client no longer sends input at the snapshot rate, so the `uplink + snapshot age` term is
  measurable at all.** `AckLatencyEstimator` recovers that constant by timing an input to the
  first snapshot whose `ack_tick` reaches it — `uplink + wait-for-the-next-snapshot + age`, where
  the wait is the only varying term, so the minimum converges on the constant. **That argument
  holds only while the wait sweeps.** Sending at the snapshot rate locks the two cadences in
  phase: every observation carries the same fixed wait and the minimum reads high by up to a whole
  snapshot interval.

  **The guard added in 0.34.0 detected this and refused, and refusing was right.** Measured live:
  `median 61.9 ms, p90 62.7, min 23.7` — a p10-to-median span of 0.28 base ticks, a textbook lock.
  A phase-locked client genuinely holds no evidence about its own pipeline constant, and a floor
  offered on that evidence would read high, which is an **over**-lead — the original defect
  arriving from the other side. But refusing correctly is not the same as being finished: the
  fallback is `RoundTripMs * 0.5`, and on a fast link `round(4 ms × 60 Hz / 1000)` is **0**. For a
  phase-locked client the term was therefore unobtainable *in principle*, not merely
  unimplemented. **No statistic, guard or fallback can close it** — they all describe a
  distribution that was never generated. Only changing the cadence generates it.

  New `InputCadence` picks the send rate from the snapshot rate: the fastest rate below it that is
  coprime with it (so the phase set is as fine as possible), visits at least
  `AckLatencyEstimator.MinimumOccupiedBuckets` phases, and completes a sweep inside
  `AckLatencyEstimator.MinimumSamples` observations. Against the default 15 Hz snapshot rate that
  is **13 Hz**: 13 distinct phases spaced 5.13 ms, a full sweep every 0.5 s against a 5 s epoch.

  **The rule is encoded rather than the number, because `WorldHz` is operator-configurable** and a
  hard-coded 13 would be right for one deployment and silently wrong for the next — which is the
  same defect as the anchor it replaces. **Note the coupling this creates:** the cadence now
  depends on `AckLatencyEstimator.MinimumSamples` and `MinimumOccupiedBuckets`. Changing either
  changes how often every client sends input. Both are referenced by name so the dependency is
  greppable, and `InputCadence` says so in its remarks.

  Why not the neighbouring rates, measured by driving the real estimator with an injected constant
  of 1.00 base ticks:

  | cadence | phases | sends/sweep | outcome |
  |---|---|---|---|
  | 15 Hz | 1 | ∞ | locked; no floor offered. The defect. |
  | 14 Hz | 14 | 14.0 | reads correctly but has 1 Hz of margin — it re-locks the moment it drifts to 15. |
  | **13 Hz** | **13** | **6.5** | **chosen.** Still yields an estimate when drifted to 13.25, 13.5, 13.75 and 14.0. |
  | 12 Hz | 4 | 4.0 | sweeps *faster* and is **closer** to 15, yet coarser — `gcd(12, 15) = 3`. |

  12 Hz is the case that decides the rule's shape: the condition is coprimality, not proximity.
  **Its penalty is invisible at 60 fps and real above it**, and saying so precisely matters —
  see the frame-rate limit below. At 60 fps the frame period (16.67 ms) is coarser than either
  cadence's phase spacing, so 12 and 13 Hz both read an unswept remainder of 16.67 ms and a lead
  of 1.00 against a true 1.00. At 144 fps, where the phases can be resolved, 13 Hz reaches 0.00 ms
  unswept and a lead of 1.67 while 12 Hz stalls at 13.89 ms and 0.42. Coprimality is chosen for
  the regime where it can matter and costs nothing where it cannot — **not** because it improves
  the reading on the machine this shipped from.

- **The configured cadence is now the cadence actually sent.** Both send loops were shaped
  `send(); await UniTask.Delay(period);`. `UniTask.Delay` starts its stopwatch when the delay is
  *constructed* — after the send — and resumes on the first Update frame at or past the period,
  **discarding the remainder every iteration**. Simulated at 60 fps, every nominal rate in
  `(12, 15]` collapsed onto 60/5 = **12 Hz**: 15 sent 12, 14 sent 12, 13 sent 12.

  **Changing the constant alone would have been a literal no-op** — same packets, same phase, same
  verdict — while reading as a fix in the diff and passing a test driven by an ideal timer.

  **Neither loop sent at the rate it named, and the two disagreed by 3 Hz.** The PlayMode harness
  paces with `PumpAsync`, whose absolute deadline happens to land exactly on four frames at 60 fps
  (`1/15 Hz = 66.67 ms`, `4 × 16.67 ms`) — 15 Hz is the one value in the range that survives the
  quantisation intact, which is why the live run shows a near-exact lock and is correctly refused.
  The bootstrap and DOTS loops meanwhile achieved ~12 Hz and swept **by accident**, at a cadence
  nobody chose, with four distinct phases instead of thirteen and `ConservativeFloorTicks` at 0.48
  against a true 1.00 — and would have stopped sweeping the moment the frame rate moved.
  **Production "working" here was not evidence of health; it was a second frame-quantisation
  accident that happened to fall the other way.**

  **And on a frame grid the cadence choice is not merely undone, it is unreachable.** A loop that
  re-derives its deadline from its wake-up can only send on a frame boundary, so its achievable
  rates are `fps / n`. Writing `k = fps / snapshotHz`, every achievable rate has a phase step of
  `frac(n / k)` — always a multiple of `1/k`, so it visits **at most k phases, whatever constant
  is written in the source**. At 60 fps against 15 Hz, `k = 4`: the achievable rates are 20, 15,
  12, 10, 8.57 and 7.5 Hz with phase steps of 0.75, 0, 0.25, 0.5, 0.75, 0 — never better than
  four phases, and 20, 15 and 7.5 Hz locked outright at one. **No value of the recommendation can
  produce a sweeping cadence on that grid.** The pinned schedule is therefore not an optimisation
  layered on the cadence choice; it is what makes any cadence choice reachable, and reverting to
  `await Delay(1f / 13f)` silently restores the defect in full.

  New `InputSendSchedule` advances by one period from the previous *scheduled* instant, so
  quantisation error cancels instead of accumulating. Catch-up after a stall is bounded
  (`ResyncAfterPeriods`) and counted: an unbounded backlog would arrive as a burst inside one
  snapshot interval, where every input is superseded and contributes no observation — destroying
  the phase relationship the schedule exists to preserve.

- `LiveBackendConfig.TickRate` was serving as both the prediction fallback rate **and** the
  harness's send cadence — the same conflation, in the instrument. Split into `FallbackTickRate`
  (still `CUVARA_TICK_RATE`, still 15, still only the fallback its documentation always described)
  and `InputSendHz` (new `CUVARA_INPUT_SEND_HZ`, defaulting to the recommendation). `SnapshotRateHz`
  is now named separately rather than implied.

- `DOTSNetworkBridge` passed `inputRateHz` as `fallbackTickRate` while its own remarks said that
  constant was "deliberately NOT reused for this". The two were numerically equal at 15 so the
  confusion cost nothing visible; offsetting the cadence would have quietly made the fallback wrong
  by a further 2 Hz. It now passes the constant directly — **the value is unchanged**, only the
  coupling is gone.


- **The five remaining script-bearing samples gain an `.asmdef`, closing the double-import
  compile error named as a known list in 0.34.0.** `ContentPipeline`, `E2ECertification`,
  `InterpolationProbe`, `KcpProbe` and `WorldView` each get one, modelled on `ClockSyncProbe`.
  Without an assembly definition a sample's scripts compile into the consuming project's
  **default assembly**, and Unity's sample importer writes every import to a version-named
  folder — `Assets/Samples/<package>/<version>/<sample>/` — so a project that imported an
  earlier version and committed it has two copies on disk after an update. Both copies compile
  together, every type is declared twice, and the default assembly fails with `CS0101` and
  `CS0229`. The Editor is dead until one copy is deleted by hand.

  **The property that makes this worth fixing pre-emptively rather than on report: it cannot be
  found before a release.** The second copy only comes into existence at the moment of a version
  bump, so the failure never lands on whoever imported the sample and tested it — it lands on the
  first person to update afterwards, in a project the sample's author never saw. No amount of
  care at import time surfaces it. 0.34.0 hit it live while importing that release's own headline
  sample against a committed 0.28.1 copy.

  0.34.0 fixed only `ClockSyncProbe` and deliberately named the other six, on the reasoning that
  writing six sets of assembly references blind on the eve of a release was the larger risk. That
  list is now closed, with one member removed from it rather than fixed: **`DemoBootstrap`
  contains no `.cs` files at all** — a scene and a `NetworkBootstrapConfig` asset, nothing that
  compiles — so it has no default-assembly footprint and cannot exhibit the defect. It is left
  without an asmdef on purpose, not overlooked.

  References were derived per sample from the types each source actually names, not copied
  between samples, and they differ: `InterpolationProbe` and `KcpProbe` need only
  `Cuvara.Netcode.Runtime`; `ContentPipeline`, `E2ECertification` and `WorldView` additionally
  need `UniTask` and `Shared.GameLogic`. `UnityEngine.UIElements` and `UnityEngine.Networking`
  are engine modules and are auto-referenced, which is why `ClockSyncProbe` lists neither despite
  building its whole panel in UIElements.

  **What this does not do.** Two imported copies now carry two asmdefs with the same assembly
  name, which Unity reports as a duplicate-assembly-name error rather than compiling. That is
  still an error, but it is scoped to the sample folders, names the offending assembly, and
  leaves the rest of the project compiling — where `CS0101` in the default assembly takes
  everything down at once and points at neither copy.

### Limitations


- **The acknowledgement floor requires at least three frames per snapshot, and below that no send
  cadence can supply it.** Acknowledgements are read on a render frame, so the wait term resolves
  only to a frame period: with `k = fps / snapshotHz`, at most `k` phases can be **told apart**,
  independently of how many the cadence **visits**. The occupancy test needs
  `MinimumOccupiedBuckets` = 3 of them, so the floor is unavailable below `3 × snapshotHz` — **45
  fps** against the default 15 Hz snapshot rate. Measured at 30 fps against the real estimator, 11,
  12, 13 and 14 Hz are **all** refused; at 45 fps and above, 13 Hz sweeps.

  **This package ships to Android with IL2CPP, where 30 fps is not a hypothetical — it is the
  target class.** So on a substantial share of real devices the pipeline constant is not
  measurable, the estimator correctly offers nothing, and the prediction lead falls back to the
  round trip. The refusal is right: the evidence genuinely is not there, and this is a limit of
  the *measurement*, not of the cadence. It is named here rather than left to be rediscovered on
  device, because the symptom — "13 Hz still offers no floor" — points at the cadence, and the
  cause is the frame rate. `BelowThreeFramesPerSnapshotNoCadenceCanSweep` pins it, and
  `ClockSyncProbe` reports which of the two constraints is binding rather than showing one
  undifferentiated REFUSED: **two refusals that look identical and mean different things is the
  defect this release is named after.**

- **At exactly 60 fps against a 60 Hz base tick the pipeline constant is not recoverable at any
  value, and this is a second face of the same law.** Acknowledgements are read on a render frame,
  so no observation can be finer than a frame period. At 60 fps that period is 16.67 ms — *exactly
  one base tick* — and since sends and acknowledgement reads both land on frames, **every
  observation is an integer number of base ticks**. Simulated across injected constants of 0.00,
  0.10, 0.50 and 1.00 base ticks, the estimator returns the same quantiles (2.00 / 2.00 / 4.00)
  in all four cases: the constant is not merely imprecise, it is absent from the output.

  A PlayMode run with no vsync usually sits far above 60 fps and therefore resolves fractional
  ticks — which is how the first live run to offer a floor produced a p10 of 0.47 base ticks
  (7.83 ms). That value is itself proof the client was above 128 fps, since no observation can be
  shorter than one frame. **The frame rate is not a nuisance parameter for this measurement; with
  the `k`-law above it is one of the two things that decide whether the quantity exists in the
  output at all.** `PredictionLatencyMeasurement` now prints the client frame rate next to every
  reading, and says so explicitly when one frame equals one base tick, because a floor quoted
  without the frame rate it was taken at is not a reading.

- **The quantile ladder survives a clock rate the environment gate refuses, and that is a property
  worth relying on rather than a coincidence.** Every acknowledgement observation is a difference
  of two readings of the *same* client clock, so a clock running fast by `(1+e)` scales every
  observation by `(1+e)` and nothing else. A uniform scaling maps a straight line to a straight
  line: `C + q·S` becomes `(1+e)C + q·(1+e)S`, so **slope and intercept inflate together and the
  shape is untouched**. The two things the ladder is read for — *did the wait sweep* (slope against
  the snapshot interval) and *what share of the floor is `FloorPercentile`* (`0.1·slope` over the
  total) — are both ratios, and both are therefore exactly skew-invariant. Only the *absolute*
  constant is inflated.

  Confirmed on the first ladder run, which had a 90 697 ppm arm the gate refuses. That skew alone
  predicts a slope of 4.363 base ticks against 4.31 measured (−1.2%), and it explains the arm's
  "55.0 Hz" tick rate as the *same* artifact rather than a second fault: `60 / 1.0907 = 55.01`. A
  fast client clock makes a healthy 60 Hz server look slow by exactly the factor it inflates
  intervals by. De-skewed, the refused arm and the clean arm agree on the pipeline constant to
  0.03 base ticks (0.5 ms).

  **Three limits, because "readable" is not "unconditional".** *(1)* The ladder is skew-invariant
  in shape but skew-**blind** in attribution: a client running 9% fast and a server running 9% slow
  predict identical ladders, and nothing in this instrument separates them. *(2)* Only a *uniform*
  scaling is harmless — a clock that changes rate within the observation window smears the line,
  and the reported residual is the detector for exactly that. *(3)* **The inflation reaches
  behaviour, not just the report.** `ConservativeFloorTicks` feeds the steering lead in these same
  units, so a client whose clock runs fast by `e` over-leads by `e` times the floor. At 9% and a
  0.77-tick floor that is 0.07 of a base tick — small, but it is a real over-lead in the direction
  this estimator exists to avoid, and it is not visible in any counter that reports the floor
  alone.

- **OPEN: `FloorPercentile` is now measured as the inflating term, and that does not by itself say
  what should replace it.** Across four arms, two snapshot rates and two containers, the floor
  tracks `intercept + 0.1 × slope` — at 30 Hz, `0.16 + 0.207 = 0.37` against 0.37 measured. The
  pipeline constant on loopback is **0.16–0.27 base ticks (2.7–4.5 ms)**; everything above that in
  the reported floor is the statistic. The pre-registered rule in `AckLatencyEstimator` says the
  minimum returns if the floor is inflated, and its condition is now met.

  **It should not be executed on this evidence, because every candidate is indistinguishable on
  it.** Simulated against the real estimator on a clean sweep, the minimum, the tenth percentile
  less `0.1·S`, the ladder intercept and the conservative floor all land within 0.05 of the true
  constant. The four arms measured were all clean sweeps. **They diverge only under contamination,
  and they diverge in opposite directions for the two kinds:**

  | | right tail (server stalls) | left tail (spurious short observations) |
  |---|---|---|
  | minimum | **best** — a right tail cannot move it (0.20/0.50/1.02 against C of 0.20/0.50/1.00) | **fatal** — collapses onto the contaminant (0.03/0.07/0.15) |
  | tenth percentile | inflated by the same `0.1·S` | survives while contamination stays under 10% |
  | ladder intercept | **worst** — the tail bends the line and least squares drags the intercept down, under-reading by up to 0.18 | also degraded |

  **Read that last row twice, because the intuition it violates is the natural one.** The ladder
  intercept is the most principled-looking candidate on clean data — it uses every observation,
  it is the model's own estimate of the constant, and it carries a built-in validity check. It is
  also the **least** trustworthy of the four exactly where robustness is needed, and for a reason
  that is easy to miss: least squares fits the whole line, so a right tail lifting the *upper*
  quantiles rotates the line and drags the *intercept* down at the other end. A statistic can be
  contaminated by data at the opposite end of the distribution from where it is read. Anyone
  arriving at this problem fresh — including the two of us, on this morning's numbers — will want
  to propose "just use the intercept". The simulation is what refutes it, not the reasoning.

  So the choice is not "which statistic is more robust" but **which contamination this system
  actually produces** — and the left-tail case is exactly the one whose known cause
  (`AckAheadOfSend`, a previous session's `LastInputTick`) has since been guarded, so its current
  rate is unmeasured rather than known to be zero.

  **The discriminating measurement is the ladder's own residual, under load.** Both contaminations
  raise it decisively — 0.12–0.37 right-tailed and 0.14–0.26 left-tailed, against 0.02–0.05 on a
  clean sweep — and the position of `q00` relative to the fitted line separates which. That points
  at a fifth option the ladder makes possible for the first time, and which is what this class
  already believes: **guard on straightness and keep a low statistic**, so that a contaminated
  distribution is refused rather than handed to a statistic chosen to survive it. In the class's
  own words, *a statistic cannot repair a guard*.

  **PROPOSAL, WITH ITS DECIDING MEASUREMENT STILL UNRUN — do not read this as settled.**
  `floor = quantile(0.10) − 0.10 × measured_slope`. The percentile stays; its now-measured bias is
  subtracted using the ladder's own slope per run, so the correction is self-correcting rather
  than assuming `S`. On clean data it lands within 0.05 of the constant.

  *Why the minimum is not proposed — and the case against it is weaker than it first looks.*
  Contamination makes the minimum **under**-read, and this package's stated asymmetry is that an
  under-lead "merely leaves residual in place" while an over-lead is the original defect arriving
  from the other side. So the minimum's failure mode is in the *tolerable* direction, and
  "disqualified" was too strong. What survives against it is narrower and still real: it is an
  extremum over a ring, driven by a single sample, so it carries run-to-run variance into the
  steering lead even on clean data. Meanwhile the **raw** tenth percentile is systematically
  biased by `0.1 · S` in the *dangerous* direction — which is the whole finding of this section,
  and the reason the proposal subtracts that bias rather than keeping or replacing the statistic.
  Corrected, it is unbiased on clean data and under-reads under contamination: safe in both.
  Uncorrected, it over-leads on every healthy run.

  *The porosity of a straightness guard, which is why the guard does not settle the choice.* A straightness
  guard set at 0.12 from the six clean arms (residuals 0.03–0.11) is **porous to sparse left-tail
  contamination**. Simulated over 200 seeds at a true constant of 0.25:

  | contamination | mean residual | passes the guard | minimum reads |
  |---|---|---|---|
  | none | 0.03 | 100% | 0.26 |
  | right tail 10% | 0.11 | 80% | 0.26 |
  | **left tail — one observation in 128** | **0.07** | **98%** | **0.13** |
  | left tail 2% | 0.09 | 90% | 0.05 |

  **One spurious short observation halves the minimum and the guard does not see it**, because
  `q00` moves to the contaminant while the fitted line follows it down, so the residual understates
  the displacement. The asymmetry is the whole point: a right tail is both *detected* and *harmless*
  to the minimum; a left tail is neither. A leave-one-out test on `q00` — the obvious fix — is not
  usable either: its sign **flips** at higher contamination, because the remaining points are
  themselves contaminated and the line follows them down.

  **AND THERE IS NO TEST HERE FOR SPARSE LEFT-TAIL CONTAMINATION — INCLUDING THE ONE THIS ENTRY
  ORIGINALLY NAMED.** `q00`'s distance from the fitted line was proposed as the sufficient shape
  test, on the reasoning that a spurious short observation drags `q00` below the line. Measured
  over 300 seeds, it does not: clean runs give +0.01..+0.05 and a run with **one** contaminant in
  128 gives −0.11..+0.11, ranges that overlap almost entirely. At a −0.10 threshold it catches 3%
  of contaminated runs; at −0.12 or beyond, none. The mechanism is the one that defeats the
  residual and the leave-one-out variant too: **`q00` is one of the six fitted points, so when it
  drops the line follows it down and the difference barely moves.** The number is still reported,
  labelled as a datum and not a test, so the idea is not re-derived and trusted.

  Two consequences, both narrowing what earlier entries here claimed. **The load run rules out
  gross contamination of either kind and cannot rule out the sparse left tail** — no instrument
  present would have seen it. And **the only left-tail cause with a detector is the one
  `AckAheadOfSend` counts**; an unknown cause is invisible to everything this harness prints.

  **THE ARGUMENT THAT CARRIES THIS DOES NOT DEPEND ON ANY CONTAMINATION RATE, AND THAT IS WHY THE
  PROPOSAL IS NOT HELD ON AN UNRUN EXPERIMENT.** It was first argued from guard porosity, which
  does depend on the rate; that argument is weaker than the one that replaced it. The one that
  carries it is the **direction of harm on a healthy run**: the raw tenth percentile is
  systematically biased by `0.1 · S` in the *over-lead* direction — the original defect arriving
  from the other side — on every clean run, whether or not any contamination exists. Subtracting
  the measured bias removes that, and the corrected statistic then under-reads under contamination,
  which is the direction this package calls tolerable. **Safe on clean data and safe when wrong**,
  with no rate in the argument.

  The left-tail rate remains unmeasured and is still worth measuring, for a narrower reason: it
  converts `AckAheadOfSend` from a guard nobody has watched act into a measurement. It has read 0
  on seven consecutive runs, which means the condition did not arise on those runs, not that it
  cannot. The documented cause is an acknowledgement naming a tick this session never sent — a
  reconnect onto a server still holding the previous session's `LastInputTick`. **That experiment
  can only measure the one cause the package counts; an unknown cause is invisible to every
  instrument here, so a null result narrows the question rather than closing it.**

  *A sequencing disclosure, because a reader should be able to judge this rather than trust it.*
  The porosity finding above was made **after** the load run had already retired the percentile's
  original justification, which is the shape of a post-hoc rescue. It is offered as falsifiable
  rather than as argued: the reconnect experiment settles it either way, and it was named as the
  decisive one before this proposal was written down.

  **The load run retired the percentile's original defence and did not touch this one.** Under an
  8-player load the ladder stayed straight — residuals 0.03 and 0.07, `q00` on the fitted line
  (0.00 and −0.03) — so neither contamination appeared. The percentile's justification of record
  was a *bimodal-under-load* regime, and that regime has not been observed on this system. A
  constant defended by a regime nobody can produce is not defended. That defence is gone; the
  guard-porosity argument above is a different one, and load could never have tested it, because
  the left-tail cause is a reconnect condition rather than a load phenomenon.

  **On not executing the pre-registered rule.** `AckLatencyEstimator` says the minimum returns if
  the floor is inflated. Its *condition* is met — confirmed on two independent axes. Its *premise*
  is false: it assumed a working sweep guard makes the minimum safe, and the guard cannot see the
  case that kills the minimum. **A pre-registered rule whose premise is falsified by later evidence
  must not be executed on the strength of its condition alone.** Pre-registration protects against
  reading numbers backwards; it does not protect against the reasoning that set the threshold being
  wrong, and the two failures look identical from inside the rule.

### Open terms


Both were found while instrumenting the adopt path, both are real, and neither is fixed
here — each needs its own change with its own test rather than a silent rider on this one.
Named rather than left to be rediscovered.

- **The two-argument `Reconcile(Vec2, long)` overload can adopt while incrementing nothing
  at all.** `HistoryMisses` is gated on `serverBaseTick != NoServerTick && serverBaseTick > 0`,
  so a two-arg caller whose pending buffer is empty takes the authoritative position
  wholesale and moves neither `HistoryHits`, nor `HistoryMisses`, nor `ReplayedSteps`.
  `Adoptions` is the first counter that sees it — it is measured off the fallback's outcome
  and has no such gate — but the hit/miss pair still reads as though no reconcile occurred.
  Not the live path: `com.cuvara.dots` drives the three-argument form. A consumer that
  cannot supply the snapshot tick is on it, which is exactly the caller least able to
  diagnose the result.

- **`Reset()` clears `Adoptions` but not `HistoryHits` / `HistoryMisses`.** It already
  cleared `ReplayedSteps`, `Snaps`, `Reconciles`, `DroppedInputs`, `RejectedInputs` and
  `CoalescedInputs` and left the history pair alone; `Adoptions` was added to the cleared
  set because it is the sibling of `ReplayedSteps`, which makes the asymmetry visible rather
  than creating it. The consequence is specific and worth stating: **after a reconnect, any
  ratio between `Adoptions` and `HistoryMisses` is meaningless**, because the numerator
  restarted at zero and the denominator did not. `adopted wholesale N of M misses` is
  therefore only readable within one session.

- **OPEN TERM — `ConservativeFloorTicks` is correct by coincidence, and its sign depends on a
  number chosen for an unrelated reason.** This is recorded as a defect rather than an
  observation, because a term that works only for its current inputs is not a working term.

  It subtracts `UnsweptSeconds` — on a swept link about `S / phases` — from a floor inflated by
  `0.1 · S`. **Those are unrelated quantities.** One is the part of the wait's range never
  sampled; the other is the offset of a chosen order statistic from the minimum. Nothing connects
  them, and they nearly cancel only because the two shipped cadences visit 13 phases (15 Hz) and
  23 phases (30 Hz) — both near the **10** at which `1/phases` happens to equal `FloorPercentile`
  and the cancellation would be exact.

  What survives is `S · (0.1 − 1/phases)`, and **its sign flips with the phase count**:

  | phases | remainder | direction |
  |---|---|---|
  | 13 (13 Hz vs 15 Hz) | `4 · (0.1 − 0.077)` = **+0.09** | over-lead |
  | 23 (23 Hz vs 30 Hz) | `2 · (0.1 − 0.043)` = **+0.11** | over-lead |
  | 10 | 0 | exact, by coincidence |
  | 8 | `4 · (0.1 − 0.125)` = **−0.10** | under-lead |

  So both shipping configurations sit on the over-lead side — the direction this estimator exists
  to avoid — by about a tenth of a base tick, and a cadence recommendation that happened to select
  8 phases would silently invert that with nothing in any counter changing. The phase count is
  chosen by `InputCadence` for reasons that have nothing to do with `FloorPercentile`; the two
  constants are coupled only by this accident.

  **It is not urgent and it is not small in the way that matters.** The magnitude is a tenth of a
  tick; the defect is that the term's correctness is not a property of the term. Whatever is
  decided about `FloorPercentile` — including leaving it alone — this subtraction should be
  restated as something that follows from what it is correcting for, or removed in favour of one
  that is.

  **Retired by consequence, not fixed, if the proposal above is adopted.** Subtracting a *measured*
  statistical bias (`0.10 × measured_slope`) removes the reason to subtract the unrelated
  `UnsweptSeconds`, and with it the coincidence. The arithmetic above stays recorded rather than
  deleted, because "this term was removed because something else replaced its job" and "this term
  was correct" are different histories, and only the first one warns the next person who reaches
  for `UnsweptSeconds` as a bias correction.

### Notes


- **The recommendation was simulated against the real estimator before it was proposed, and the
  simulation refuted two claims that would otherwise have shipped.** The first — that 14 Hz would
  be marginal at the minimum sample count — was wrong, and finding out why surfaced the quantile
  degeneracy logged below. The second was the one that mattered: **the cadence change alone does
  nothing on the target machine.** It greps clean, it reads as a fix in the diff, and it passes a
  test driven by an ideal timer, because the defect lives in the loop shape rather than in the
  constant. That is the failure mode of this codebase arriving through the *harness* instead of
  through an edit — a test that shares the code's model of the wire can only confirm it — and the
  only reason it was caught is that the proposal was run against the real class before it was
  believed. **Verify a recommendation the same way a defect is verified.**

- **Figures in this entry were re-taken on clean rebuilds, because one of them was stale.** A
  mutation check of the new tests initially reported a pass; `dotnet test` had silently re-run the
  previous assembly rather than rebuilding, because the sources live outside the throwaway project
  directory. A clean rebuild showed 6 of 15 failing, which is the real result. Every
  nominal-versus-achieved number quoted above was re-measured with `rm -rf bin obj` first, and the
  12 Hz comparison was corrected as a result: its penalty is invisible at 60 fps, and the earlier
  draft quoted an ideal-timer figure as though it described the shipping client.

- **The harness is also the client, so this change moves the instrument and the thing measured in
  the same commit.** There is no third client to hold fixed. The consequence is that a live run
  cannot, on its own, separate *"the fix worked"* from *"the harness now samples differently"*:
  both the cadence and the pinned schedule alter which phases get sampled. Stated here rather than
  discovered later. What the run *can* establish is the qualitative step — a floor offered at all
  where none was before — because the previous state was a refusal, not a different number.
- **`AckLatencyEstimator`'s sweep guard is weakest on its first verdict, and this is arithmetic
  rather than a suspicion.** `Quantile` computes `index = (int)(q * _obsCount)`. At
  `_obsCount == 8` — exactly `MinimumSamples` — the tenth percentile is index 0 and the ninetieth
  is index 7: **the minimum and the maximum**, which is precisely the `max - min` statistic the
  guard was rewritten to stop being, and which a single outlier satisfies. The same holds at
  n = 9. From **n ≥ 10** the low quantile moves off index 0 and the statistic becomes a real order
  statistic. Not fixed here — tuning a guard's constants while changing the cadence feeding it
  would make neither result attributable — and logged so it joins the known list rather than being
  rediscovered.

- **Known pre-existing discrepancy, unchanged by this work.** `GameConstants.MaxBankedMovementMs`
  reasons that `MaxBankedMovementTicks(15) = 4` ticks of 66.7 ms covers a bursting client's 264 ms
  idle "exactly". That arithmetic is for the *uniform* 15 Hz configuration. Under the live split
  60/15 rates the handler is built with `MovementHz = 60`, so the budget is `MaxBankedMovementTicks(60)
  = 15` base ticks = **250 ms**, and the 264 ms case it claims to cover is already 14 ms over. This
  is in the server repo, predates this change, and is logged here so it is on a known list rather
  than a future surprise.


- **A limitation with no consequence for the action is not a limitation — and the caveats carried
  through this work were audited against that rather than the principle merely being stated.**

  The case that produced it: the quantile ladder cannot tell a client running 9% fast from a
  server running 9% slow, and that was carried for most of a day as a standing limitation. It is
  not one. Both causes produce an error of `advertised/measured − 1`, and both take the same
  correction — convert with the measured rate. Nothing anybody would *do* differs between the two
  worlds, so the inability to distinguish them costs nothing.

  Applied to the rest of this work's caveats, and deliberately reported with the ones that
  **survive**, because a principle that dissolves everything it is pointed at is a licence rather
  than a test:

  | caveat | verdict |
  |---|---|
  | the ladder cannot attribute skew to client or server | **dissolved** — same correction either way |
  | two quantiles are silent about a distribution | **discharged** — it had a real consequence, which is why the ladder exists; it is now paid, not waived |
  | the harness is also the client, so the instrument moved with the measurement | **narrowed** — it bars a quantitative before/after comparison, which nothing here relied on; the qualitative step (a refusal becoming a floor) is unaffected |
  | below 3 frames per snapshot no cadence sweeps | **stands** — it decides whether the feature works on a 30 fps device |
  | at exactly 60 fps the constant is unrecoverable | **stands** — it decides whether a run can be read at all |
  | a non-uniform clock breaks the ladder | **stands** — it changes what must be checked (the residual) |
  | the clock error reaches the steering lead, not just the report | **stands, bounded** — real, and at most `0.02 × floor` on any run the validity gate admits |
  | `Quantile` degenerates to min/max at `n == MinimumSamples` | **stands** — the guard's first verdict is its weakest |

  **The guard on the principle, which matters more than the principle.** "No consequence for the
  action" has to mean *no consequence for any action anyone might take with this information* —
  not *no consequence for the action I already intend*. Read the second way it becomes a tool for
  discarding inconvenient caveats, which is a considerably worse failure than carrying a few
  harmless ones. The test is whether two people who disagree about what to do next would both be
  unaffected; if only one of them is, the limitation is real and it is theirs.
## [0.34.0] - 2026-09-08

> **The failure mode behind this release: reasoning about one property and gating on another.**
>
> Every defect in this release was found by noticing that a statement believed to be evidence was
> true of something *adjacent* to what it was being used to prove. None of them were wrong
> statements, which is why none of them were caught by review.
>
> - `SkewPpm` returns **0** when no line has been fitted, byte-identical to what two perfectly
>   matched clocks produce. A prediction-OFF arm reading "0 ppm" was taken for a control.
> - A **1.103** clock ratio was recorded as this machine's measured truth and used to widen
>   `MinimumSkew`/`MaximumSkew`. It was a delay-floor artefact; the same machine measures 1.0002
>   idle. The bounds are unchanged — the number that was wrong was the justification.
> - The live measurement was `[Ignore]`d on one named term, and an ignored test reads as *"not
>   applicable"* rather than *"unverified"*, so nothing downstream of it was checked for three
>   releases.
> - *"A physical constant cannot take two values in one run"* was sound about a crystal ratio and
>   was applied to a loaded server tick loop, which is not one.
> - The rate was gated on corroboration and the **age** left on `IsUsable`, justified by an
>   argument about the *slope*. The age is the height above a line the slope tilts, so the same
>   displacement moves both, and a refused slope went on steering the lead through the residual.
> - The server's own metrics were read across a six-sample window that happened to be quiet and
>   reported as "the server was fine". Sampled finer, it dips to 54.23 Hz and drops 38 ticks.
>   **A window with no drops is not a run with no drops.**
>
> **Three sibling failure modes, with different defences.** Not every mistake here was a reading
> believed for the wrong property, and the ones that were not need different answers.
>
> *An aggregate read once is a claim about the moment you read it, not about the run.* The
> server's drop counter was sampled at 20-second intervals against a transient lasting seconds,
> read as flat, and reported as "the server was fine". The counter was never at fault and reading
> it **more carefully** would not have helped — only reading it **more often**. Defence: match the
> sampling interval to the lifetime of the thing being excluded, and say what window a claim
> covers.
>
> *A fixture written from the code's model of the wire can only confirm it.* Twice in this work a
> test passed against a defect because it shared the defect's assumption: one stamped
> acknowledgements at `now + latency`, reproducing the very interval-shrinking flaw it was meant
> to catch, and two others warmed up long enough to *fit* but not to *corroborate*, so they pinned
> a branch that was no longer taken. Defence: **derive the fixture from the wire's behaviour, not
> from the code's model of it** — acknowledgements arrive on the snapshot cadence and the send
> time moves, because that is what actually happens.
>
> *When two mechanisms produce the same counter, testing either against the pooled data tests
> neither.* A signature for one cause of reconcile misses was refuted across seven runs — by a run
> whose misses came from the *other* cause, already identified and already fixed. Controlled to the
> subset where the first mechanism cannot operate, the signature holds 3 for 3, which is not a
> finding at n=3 but makes the verdict *untested*, not *refuted*. **Note the direction: this one
> discards something possibly true, where the other three accept things that are false.** A warning
> written only against false acceptance leaves it invisible. Defence: before testing a second
> cause, exclude the runs the first can explain — and say which subset the claim is about.
>
> *Some defects cannot be found before a release by construction.* Importing a sample twice is a
> hard compile error, and the second copy only exists after a **version bump** — so it lands on
> the first person to update and never on the person who imported. Pre-release testing imports
> into a tree with no older copy, so no amount of it finds this. Defence: for anything keyed by
> version, the acceptance test is "does it work **on top of the previous version**", not "does it
> work".
>
> **Every guard in this release asks "should I believe this measurement". Not one asked "and what
> happens when I don't".** That gap has three separate defects in it, and it was only visible from
> the third:
>
> - the acknowledgement floor **truncated to whole base ticks** contributed *zero* while still
>   taking its branch, so turning the estimator on deleted the round-trip term and put nothing
>   back;
> - the round-trip fallback itself is `round(RoundTripMs * Hz / 1000)`, which is **`round(0.24)` =
>   0** on loopback — it does not degrade gracefully, it vanishes;
> - and the provisional age, once it saturated `Math.Min(.., gap)`, delivered **`gap`** — the
>   warm-up fallback — on every call.
>
> Three different mechanisms, one hole: the fallback path was never measured, never guarded and
> never printed, because attention was on whether to trust the estimate. **A fallback is not
> automatically the safe option; it is another claim about the same quantity and needs the same
> scrutiny as the estimate it replaces.**
>
> **The sharpest instance, because it is a fallback rather than a reading.** The fitted rate is
> refused *because its slope is untrusted* — and the fallback asserts **slope = zero**, which is
> the same untrusted quantity set to a different value, and the one that grows without bound. A
> fallback is not automatically the safe option: it is another claim about the same thing, and it
> needs the same scrutiny as the estimate it replaces. Measured, that claim cost 45.56 base ticks
> of age against a true 0.09, which saturated its own clamp and delivered the warm-up fallback the
> release exists to remove.
>
> And a note on instruments, since three of the defects above were in them rather than in the
> mechanism: **instruments are cheaper to fix than mechanisms, and not cheaper to get wrong.** A
> wrong instrument costs a full measurement cycle and sends the reader to the wrong layer — one
> label here asserted the opposite of what the code did, on the exact line an investigation had
> come down to.
>
> Two rules come out of it, and they are worth more than any single fix here. **Never read a zero
> as evidence without the counter beside it that says a measurement happened** — hence
> `staleness fit` printing fits/refused/baseline, the wire-rate gap printing a percentage instead
> of "(agrees)", and the clock error printing a band rather than a latched sample. And **a gate
> with no test that reads it is not a gate**: removing one token from the binder's rate gate
> restored the defect in full while 126 tests stayed green, which is why
> `WorldViewBinderRateGateTests` now exists.
>
> The corresponding design move is to gate on **reproducibility rather than on a diagnosis**. A
> fitted rate is believed once it survives a doubled baseline — no threshold on magnitude, no
> claim about the cause. A transient server dip, a delay-floor step and a genuine clock
> difference are then sorted correctly without anyone having to be right about which is
> happening.
>
> **How each fix in this release was validated, because they are not equal — and because the
> strongest available evidence for most of them is not a live run.** Every fix here guards against
> an **untrustworthy timebase**. In a context where the timebase is trustworthy the fit
> corroborates, the rate gate passes through without refusing, the age stays fitted, and the
> saturation refusal cannot fire at all. **A guard against an abnormal condition cannot be
> validated in a context that does not produce the condition** — and the context in which this
> package reads clean is the same one in which v0.33.0 read clean while carrying the defect that
> started this work.
>
> That is a genuine bind: the guards are exercised only where the measurement is currently
> unmeasurable, and measurable only where they are not exercised. A third context resolves it. The
> EditMode suite **injects each abnormal condition synthetically** — a 300 ms delay-floor step, a
> phase-locked distribution with one outlier, an acknowledgement from a session that never existed,
> a provisional reading that saturates its clamp — and each one reproduces on demand and **fails
> without its fix**. So for a guard against a condition nobody can produce on demand in a live run,
> a discriminating synthetic test is not a weaker grade of the same evidence: **it is the only kind
> of evidence available.**
>
> The ledger is therefore **two fixes confirmed live — one by a before/after between two runs the
> validity gate admits, one by a behavioural falsifier across four environments — five confirmed
> against synthetic conditions that reproduce and discriminate, one structural-only with no test
> and zero live magnitude, one retained for attribution reasons alone, and a machine that cannot
> currently measure the thing this branch is about in any context tried.** Ordered weakest first, so a reader hunting a regression has a
> map of where to look rather than a reassurance.
>
> | Fix | Validation |
> |---|---|
> | Age gated on corroboration (`AgeIsFitted`) | **Confirmed on two measurable runs.** `reconciles from history` 39 hit/120 missed → **147/15**, age 45.56 → 0.09 — and the before/after runs read **59.5 Hz (−0.8%)** and **60.1 Hz (+0.2%)**, so *both pass the 2% validity gate*. The only before/after comparison in this work drawn between two runs the gate would admit. |
> | Saturated provisional age refused | **Confirmed behaviourally, four times, in four environments** (90 833 / 35 532 / 39 235 / 90 690 ppm, the last with a single test in the process). Falsifier stated in advance: large skew *and* `TARGET LEAD 4` would mean failure; measured lead **0** every time. Note all four runs **fail** the validity gate — which does not weaken it, because the check is whether a code path fired, not what a correction figure read. A behavioural falsifier survives an unmeasurable run; a numeric comparison does not. |
> | Rate gated on corroboration (`RateCorroborated`) | **Synthetic, discriminating.** Delay-floor step reproduces the artefact in a test; live readings of 220 ppm idle against 90 636 loaded on one machine. |
> | Sweep guard: span between quantiles + bucket occupancy | **Synthetic, discriminating.** Two tests fail against the extremes-based span. First *observed* discriminating in run 2 (p10–median spread 3.09 offered, 0.28 refused) — after the fix, not as validation of it. |
> | Acknowledgement floor carried as a fraction | **Synthetic, discriminating.** `Math.Floor` demonstrably returned 0 for both live readings (0.14, 0.68). |
> | Acknowledgements from a previous session discarded | **Synthetic, discriminating.** Reproduces in a test; **never observed live** — `ack floor ack-ahead` has read 0 on every run since it was added. |
> | Newest-retired input timed, superseded counted | **Synthetic, discriminating.** The min-over-a-group identity is arithmetic; the sweep-span corruption reproduces in a test. |
> | Round trip computed unconditionally | **Structural only** — no synthetic test. Structural; its live magnitude on loopback is **zero**, so it has never been exercised. |
> | Tenth-percentile floor | **Weakest.** Introduced on one loaded run, its justifying test now fails at its own precondition once the sweep guard works, and it is retained only because reverting it in the same commit would have made the sweep fix unattributable. |
>
> **And the set has never been measured as a set — which is the risk, not the count.** Nine fixes
> validated individually against a measurement with a 2.6× environmental spread is a weaker
> position than nine fixes validated together against a stable one, and **no amount of per-fix
> confidence adds up to the second**. Every fix is individually justified; the nine of them have never run together against a measurement capable of
> resolving them, and the one time two landed together the result could not be attributed. Two runs
> of one identical commit produced a **2.6× spread in apparent clock skew, a 4× spread in
> correction count, and opposite floor decisions** — so a single run of any commit is worth very
> little, and much of the attribution in this work rested on exactly that. That is a limitation of
> how this release was validated, not a footnote about process.
>
> **The conclusion, which is not about any of the individual defects.** Nine fixes came out of
> this work and not one of them was found by reasoning about the code. Every advance came from a
> measurement: a counter printed beside another counter, a band printed instead of a sample, a
> distribution printed instead of a single figure, a server metric read from outside the client, a
> test that failed at its own precondition. Both people working on it were confidently wrong
> repeatedly, in both directions, and each time the correction came from an instrument rather than
> from an argument. **The code was not fixed by understanding it better. It was instrumented until
> it could not hide.** Where this release's guards disagree with a future reader's intuition, the
> guards were measured and the intuition was not.
>
> **The commit sequence is left unsquashed on purpose.** A tenth-percentile floor was introduced,
> the sweep guard above it was then fixed, and the p10's own justifying test failed *at its
> precondition* — the distribution it was built around is refused outright once the guard works.
> That sequence is the direct evidence that the statistic was compensating for the guard, and
> squashing it would delete the only record of it, leaving a reverted constant with no visible
> reason.

### Fixed

- **The measurement prints the snapshot age as a band with a trend, and stops recommending the
  thing the rate gate exists to prevent.** The age is the height of the newest snapshot above a
  fitted envelope, and that envelope's intercept is anchored to a *single* sample
  (`_offset = _anchorY - _skew * _anchorX`) — so a delay displacement between the anchors shifts
  the height exactly as it tilts the slope. One printed number cannot tell a client genuinely
  acting on 87 ms-old data from a fit whose anchor was laid before the displacement arrived. The
  two differ in shape over a run — a growing backlog shows a rising **trend**, a contaminated fit
  shows a **step** — so `snapshot age band` now prints min..max with first-half and second-half
  means and names which shape it sees. Separately, `ClockErrorNote`'s droop branch used to end
  "Feed the fitted rate to the clock"; that advice predates the corroboration gate and now
  recommends exactly what the gate prevents, since the large ppm it fires on is usually an
  uncorroborated fit. It now says to read `rate corroborated` first, and that the droop
  arithmetic describes a rate that does not exist when it reads NO.

- **The binder's rate gate now has a test of its own** (`WorldViewBinderRateGateTests`). The
  estimator's tests pin `RateCorroborated`; they cannot pin that `WorldViewBinder` *reads* it,
  and nothing did — deleting the second half of `Staleness.IsUsable && Staleness.RateCorroborated`
  left all 126 other tests green while restoring the defect in full. Verified: with the gate
  removed, two clocks that genuinely agree plus a 300 ms delay-floor step drive the client to
  apply a **0.9923x** rate scale, and the test fails; with it, the scale stays at 1. It drives a
  real `LocalMovePredictor` and `WorldState` through `binder.Tick` on an injected `IViewClock`,
  so it needs no sleeping and reads `ClockRateScale` directly. The companion case pins that a
  corroborated half-percent rate *does* still reach the clock, so the gate cannot pass by
  refusing to correct at all.

- **The measurement prints the fit's baseline and fit counts beside the ppm, and stops calling
  an 8% wire-rate gap "(agrees)".** Two instruments that should have caught the rate artefact and
  did not. A delay-floor step of *d* seconds fakes a slope of `d / baseline`, so the same ppm
  means opposite things at different baselines — 90 000 ppm over 4 s is a 362 ms hitch, over 60 s
  it would need 5.4 s of floor movement — and the reading could not be judged without the
  baseline beside it. `staleness fit` now prints fitted/refused/baseline, and calls out
  `fits 0` explicitly: **`SkewPpm` returns 0 when there is no fit, which is identical to what two
  perfectly matched clocks produce.** That trap is documented in the estimator and caught us
  anyway — a prediction-OFF arm reading "0 ppm" was taken for a control proving the loaded arm's
  90 000 ppm artefactual, when it was an arm that never fitted a line at all (`Staleness.Sample`
  is only reached on the predictor's path). Separately, `tick rate measured` now always prints
  the percentage gap: `TickRateEstimator.DisagreementTolerance` is 15%, correctly sized to catch
  a *wrong rate* (the nearest realistic pair is 15 against 20 Hz), so a client measuring 55.0 Hz
  off a 60 Hz server "agreed" on every arm of a run whose clock was being steered 8% wrong. The
  tolerance is **not** changed — a wrong rate and a distorted observation of the right rate are
  different faults wanting different bands — but a gap of 3% or more is now called out as the
  starved-frame-loop signature it is.

- **`Clock Sync Probe` gains a "Raise delivery floor" button — the defect made pressable.** Every
  netcode feature ships a scene, and this one belongs in the existing clock scene rather than a
  new one: same subject, same two synthetic clocks. The button adds a *sustained* 300 ms to every
  delivery, distinct from the one-off "Stall a frame" — a lower envelope shrugs off a stall, which
  is what it is for, and is blind to a floor that rises and stays risen. The two clocks remain in
  perfect agreement and the fit reports tens of thousands of ppm anyway; the readout then shows
  `corroborated NO`, `age from the unit-rate floor`, and a steering lead that stays put instead of
  climbing with the baseline. The scene's `TargetLeadTicks` now mirrors the binder's gate on
  `AgeIsFitted` rather than `IsUsable`, so the sample cannot drift from the code it demonstrates.
  **The dial's +110,000 ppm default is relabelled**: it was recorded as this machine's measured
  ratio and used to justify widening the clamp, and it is falsified — 220 ppm idle on the same
  Windows-Editor/Linux-container pair. It stays as a synthetic stress case at the clamp boundary,
  in the scene, its README and the sample description, with the correction stated in all three.

- **A refused slope no longer reaches the lead through the snapshot age either.** Gating the
  clock was half a fix. `StalenessTicks` is `y - (offset + skew * x)`, and `skew` is the *same*
  slope `RateCorroborated` refuses — so an uncorroborated fit went on steering the lead through
  the residual, undiminished, and growing with the distance from the anchor. Measured live on a
  run where the rate was correctly refused and every rate counter read clean: a **51 225 ppm** fit
  over a **6.1 s** baseline displaces the line by 0.31 s — **18.7 base ticks** — the age read
  **5.24** against a true idle age of 0.06, the lead went to **6** where healthy runs sat at 1,
  and the correction stayed at three whole steps. An uncorroborated fit now falls back to the
  **provisional** reading, which is measured at unit rate against a running floor and therefore
  cannot accumulate with the baseline at all. That is not new code: it is the branch that already
  existed for the pre-fit window, safe for exactly the reason it was safe there — it carries no
  slope — and `TargetLeadTicks` already clamps it with `Math.Min(.., gap)`. The unit-rate floor is
  now kept live while a fit exists but is uncorroborated, so that reading does not go stale. New
  `AgeIsFitted` says which line the age is measured against; **`IsUsable` does not mean that and
  reading it as though it did was the defect.** The comment it replaced in `TargetLeadTicks` —
  *"A fitted line. Believe it; the ceiling below is the only guard it needs"* — was true while the
  only thing that could go wrong with a fit was noise, and is not true now that a fit can be a
  displacement divided by a baseline.

- **A fitted clock rate no longer reaches the clock unless it reproduces over a doubled
  baseline.** `SnapshotStalenessEstimator` fits a line through two best-case samples, and that
  line is a *rate* only if the **minimum achievable delay was the same at both anchors**. The
  class documented that assumption and never tested it, and `WorldViewBinder` handed the result
  straight to `LocalMovePredictor.SetClockRateScale`. When a starved frame loop raises the delay
  floor, the later anchor sits above the true line and the slope absorbs the displacement **as
  rate**. Measured on one machine minutes apart: idle, **220 ppm** (a ratio of 1.0002 — two
  ordinary crystals); inside a loaded PlayMode suite, **90 636 ppm**. A crystal ratio does not
  move 90 000 ppm in ten minutes. Over the 4 s minimum baseline that slope is a delay-floor step
  of 362 ms, which is an ordinary hitch — and the client obediently ran its base-tick clock
  **8.3% slow**, sat at a three-tick standing error, and corrected by three whole steps at every
  transition. A rate reads the same over any baseline; a floor step fakes `step / baseline` and
  halves when the baseline doubles, so the new `RateCorroborated` gate requires the reading to
  reproduce once the baseline has **doubled** — scale-invariant, and needing no threshold on
  magnitude. `IsUsable` still gates the *age*, which is the right evidence bar for a residual
  read once per snapshot and the wrong one for a rate applied every second forever.

- **Comparing consecutive fits was not enough, and the guard's own test caught it.** A decaying
  slope changes by `step * epoch / baseline²` between neighbours, which falls under any fixed
  tolerance once the baseline is long enough — a 300 ms floor step self-corroborates at about
  25 s on a reading still 12 000 ppm wrong. Requiring the baseline to double makes a pure decay
  disagree by half of itself at every scale.

- **A rate beyond one percent is counted rather than passing silently.** `SkewPpm` has always
  said a few hundred ppm is two crystals and that tens of thousands "is worth an error rather
  than a correction"; nothing enforced it. `FitsExtraordinary` now counts them and the
  measurement reports them. It is a counter and not an unconditional refusal on purpose: refusing
  outright would permanently disable rate correction on a machine whose ratio genuinely is
  extraordinary, which is the failure the original 0.90/1.10 bounds produced, arriving by another
  door. Such a reading still has to corroborate like every other.

### Changed

- **`MinimumSkew`/`MaximumSkew` are left at 0.75/1.33, and the reasoning recorded with them is
  corrected.** They were widened from 0.90/1.10 on the belief that the development machine's true
  clock ratio is **1.103**; that figure is now believed to have been this same delay-floor
  artefact, since the same Windows-Editor/Linux-container pair measures 1.0002 when idle. The
  mass refusals that justified widening were most likely the clamp working. The bounds are
  **not** narrowed back, because narrowing would not have caught the artefact that prompted this
  — the live skew was **0.9169**, comfortably inside the old bounds — and because an artefact and
  a rate are not distinguished by magnitude at all, but by whether the reading survives a change
  of baseline. `LocalMovePredictor`'s rate-scale clamps are the reciprocals of these two and are
  therefore also unchanged; no behaviour changes for a client on a genuinely odd clock beyond the
  corroboration requirement above. Every comment and test remark claiming 1.103 as a *measured*
  ratio has been rewritten to say where the figure came from and why it is not trusted — the
  `[TestCase(1.103)]` cases are kept and relabelled as synthetic, because the band still has to
  admit such a ratio.


### Added

- **`AckLatencyEstimator` — the pipeline constant the staleness envelope absorbs.** The client
  applies an input at its OWN base tick; the server applies it at the tick its packet is drained
  on, so the two label the same input with the same tick number only if the client leads by
  `uplink + snapshot age`. `SnapshotStalenessEstimator` fits a lower envelope and therefore
  absorbs any constant by construction, so neither its reading nor the clock error can ever show
  this term — which is why it survived two rounds of fixes with every counter reading clean. The
  estimator times each input from its send to the first snapshot whose `ack_tick` reaches it and
  takes the minimum, which converges on `uplink + age`. No new wire traffic and no server change:
  both ends of the interval were already at the client. Call
  `WorldViewBinder.NoteInputSent(tick)` beside `LocalMovePredictor.RecordInput`, or the estimator
  has one end of the interval and offers nothing.

### Fixed

- **The acknowledgement floor is carried into the lead as a fraction, not truncated to whole
  base ticks.** Truncation was the reason this term stayed open. Live the floor read 0.14 and
  0.68 base ticks and `Math.Floor` returned zero both times, so the estimator contributed
  *nothing* on exactly the links it exists for — every localhost run measured. And a sub-tick
  deficit is not a sub-tick problem: the tick label is an integer, so a client leading 0.68 ticks
  short carries the wrong tick number for most of every tick and the reconcile returns a whole
  step for it. That is the second step of the 2.00-step residual against a floor of 1.00. The
  one-sided bias that truncation was there to provide is kept and moved into the units that are
  actually uncertain — `AckLatencyEstimator.ConservativeFloorTicks` is the floor less
  `UnsweptSeconds`, the measured part of the wait's range never sampled — so it shrinks as the
  sweep completes instead of firing as a total loss whenever the link is fast.

- **An acknowledgement now times only the newest input it retires.** It timed every input it
  covered. That could never pull the floor *down* — an older input waited for an acknowledgement
  a later one had already earned, so its interval is larger, and a minimum is monotone — but it
  stretched the observed **span**, which is the whole of the evidence `SweptEnough` rests on. On
  a client sending four inputs per snapshot in phase, the superseded send times widened the span
  by three send periods and the guard read "swept" on a link whose wait never varied at all,
  offering a floor inflated by a fixed wait. That is an over-lead, the original defect arriving
  from the other side. Superseded observations are drained and counted (`Superseded`) rather than
  folded in.

- **A measured floor no longer silently lowers the runaway ceiling.** `rttTicks` was computed
  only on the branch a floor did not take, so the arrival of a floor dropped the round trip out
  of `ceiling = gap * 2 + rttTicks + floor` as well as out of the lead — a clamp tightening for a
  reason that has nothing to do with a runaway, on a change whose safety argument was that it
  could not touch the steer. It is now computed unconditionally. Together with the truncation
  above this is the coupling behind the clock error moving from −1 to −2/−4 when the estimator
  was first wired in: the floor was never steering anything, the round trip had stopped steering
  anything, and a truncated floor put nothing back.

- **`Clock Sync Probe` gains an `.asmdef`, because importing it twice was a hard compile error.**
  Unity's sample importer writes to `Assets/Samples/<package>/<version>/<sample>/`, so a project
  that imported an earlier version and committed it has two copies on disk after an update.
  Without an assembly definition both compile into the project's default assembly and collide
  with `CS0101` and `CS0229` — the whole assembly fails, the Editor is dead until one is deleted
  by hand, and because the copies sit in different version folders **it lands on the first person
  to update, never on the person who imported.** Found by hitting it while importing this
  release's own headline sample.

  **Six other samples have the same defect and are deliberately left alone in this release**:
  `ContentPipeline`, `DemoBootstrap`, `E2ECertification`, `InterpolationProbe`, `KcpProbe` and
  `WorldView` all ship without an assembly definition, and only `DOTSSample` and
  `ReconnectPolicyDemo` have one. Fixing all seven blind would mean writing six sets of assembly
  references that cannot be compiled from the package, on the eve of a release; the one that
  demonstrates this release's fix is fixed, and the rest are named so they are a known list
  rather than six future surprises.

- **The sweep guard measures its span between the tenth and ninetieth percentiles, not between
  the extremes — and the floor goes back to the minimum as a result.** `SweptEnough` is what
  decides whether a minimum is evidence about the pipeline constant, and its span was
  `max - min`, which **a single observation satisfies**. Measured live on a client sending at
  15 Hz into a 15 Hz snapshot stream — the phase-locked case this class's remarks warn about by
  name — the input-to-acknowledgement distribution was `min 5.5 ms, median 58.8, p90 62.2`: a
  lock with one outlier. `max - min` read 57 ms against a 33 ms requirement and passed. The floor
  then landed on the locked mode at **3.33 base ticks while the harness's own observed minimum
  was 0.33** — ten times high, in the opposite direction from every failure this estimator had
  produced before, straight into the steering lead, which reached 8 and pushed the reconcile's
  compare point past the retained history (**39 hits against 120 misses**, where a healthy run
  had 138 against 3). Between quantiles, one outlier moves nothing and the same data is refused.
  The span is paired with a **bucket-occupancy** test, because extent is not shape: a span
  between two order statistics still describes two points, while occupancy of at least three of
  eight divisions of the interval cannot be produced by any arrangement of two observations. Both
  are kept — the span bounds the extent, the occupancy bounds the shape, and neither implies the
  other. A **third** flaw in the same guard is recorded rather than fixed: the snapshot interval
  every requirement is scaled by is itself the smallest gap between acknowledgements, so it reads
  the interval *less the arrival jitter* and weakens the sweep requirement in proportion — about
  a quarter on a 66 ms cadence. Same shape again, and it deserves its own measurement rather than
  a fix folded in behind this one.

- **The tenth-percentile floor is deliberately NOT changed in the same commit.** The expectation
  is that the sweep fix makes the choice moot — a genuinely swept distribution has its tenth
  percentile a hair above its minimum, and the distributions where they diverge are now refused
  before any statistic is taken. But if both shipped together and the floor came back correct,
  nothing would distinguish which one did the work, and the answer would have to be reasoned
  rather than read. The revert is written and held. Either way the lesson does not depend on the
  outcome: **a statistic cannot repair a guard**, and reaching for a more robust one is a sign the
  guard above it is admitting data it should not.

- **An acknowledgement naming a tick the client never sent is discarded rather than timed.**
  `ack_tick` is defined as *this client's* newest accepted input tick, so one greater than the
  newest tick this client has stamped cannot be about this client's inputs — it is a server
  still holding the previous session's `LastInputTick` for the same user while a fresh
  connection restarts its numbering at 1. Left unguarded every early input satisfies
  `tick <= ackTick` the instant it is sent, is retired by the very next snapshot, and is timed
  at the wait for one client frame: single-digit milliseconds against a real pipeline of
  twenty-five, which a minimum filter then holds for its whole epoch memory. Measured live: a
  floor of **0.17 base ticks on a run whose input-to-acknowledgement minimum was 1.39 and p90
  1.95**, leaving the lead at 0 and the correction at 3.67 wire-sized steps. This is why
  `InputToVisibleMovement_WithAndWithoutPrediction` passed when filtered to itself and failed
  inside the full PlayMode suite — run alone, the previous session has been reaped; run after
  other tests, it has not. Counted as `AckAheadOfSend` and printed as `ack floor ack-ahead`,
  because a client seeing it past its first seconds is talking to a server that thinks it is
  someone else.

- **The measurement reports the clock error as a band, and stops asserting a cause it cannot
  support.** `clock error (last steer)` is one instantaneous integer sample of a quantity that
  quantises, so a clock sitting steadily between two ticks reports one value or the next
  depending on where the last snapshot fell — a band of width 1 straddling the target is a
  clock in step, a band of width 1 sitting off it is a standing offset, and a single number
  cannot tell those apart. A `clock error band` line now prints the range sampled every frame.
  Two related traps are closed with it: a prediction-OFF run's `0` is an **absence**, not a
  zero — `SteerToServerTick` returns immediately when the predictor is disabled, so `TickError`
  is never assigned — and is now printed as `NOT SAMPLED`; and the fall-through note read
  "the clock is not tracking the steering target" for any error of 2 or more, which since
  v0.33.0 fed the fitted rate to the clock is *every* such error, because the droop branch it
  falls through from is computed from a `SkewPpm` drift that is now ~0 by design. An alarming
  string reached by construction is not a finding. The note also records that this figure is
  measured against `serverTick + TargetLeadTicks`, so it is **not comparable across builds that
  changed the lead arithmetic** — the same clock reads one lower per tick the lead gained.

- **`InputToVisibleMovement_WithAndWithoutPrediction` is no longer `[Ignore]`d.** It was ignored
  on this one named open term. Every assertion stands where it was — the 1.5-step correction
  budget and the budget of 2 corrections above one step included; neither was widened.


- **The measurement refuses a run it cannot measure, instead of reporting its numbers.** A run whose
  client does not observe the snapshot stream at the rate the server sends it has not measured
  prediction; it has measured whatever made the stream look slow — and every lead term is derived
  from that stream. Six runs of near-identical code gave ON-arm wire rates of 57.1, 59.5, 60.1,
  55.6, 55.8 and 58.0 Hz against an advertised 60, with **two runs of a single commit differing by
  2.6× in apparent clock skew, 4× in correction count, and disagreeing on whether a floor could be
  offered at all.** The cause is not known after six runs of looking — and a validity gate needs a
  precondition, not a diagnosis. Past a 2% gap the run is `Inconclusive`: **refused, not clamped and
  not annotated**, because a discarded run costs seven minutes and a silently annotated one gets
  quoted six months later. The refusal carries the numbers it refused, printed where the verdict
  would have been, so a reader grepping for corrections finds the refusal rather than a figure. The
  threshold is set from those six runs and is **weak evidence** — a 2% gate discards four of them,
  the two it keeps are the two whose corrections were lowest, n=2, and the same runs produced the
  hypothesis. If a later run passes the gate and still reads badly, that is the gate being wrong
  rather than the fix. Precedent: the loadtest harness already refuses a run whose entity count does
  not match what was requested, for the same reason — **a run that failed its preconditions produces
  numbers that look like results.**

- **A saturated provisional age is refused instead of being delivered as the clamp — the clamp
  value *was* the defect.** `Math.Min(StalenessTicks, gap)` reads as a safety ceiling and behaves
  like one while the provisional figure is roughly right. It is not a ceiling once the figure runs
  away: the provisional reading carries **no slope term**, so an untrustworthy timebase makes it
  accumulate at the apparent skew, it exceeds `gap` on every call, and the clamp then returns a
  **constant** — which is `gap`, the warm-up fallback the whole of v0.33.0 and v0.34.0 exist to
  stop steering on. The report even labels it one line below: *"a lead equal to this is the warm-up
  fallback, not a measurement."* Measured: a provisional age of **45.56 base ticks** against a true
  age of 0.09 one commit earlier on the same box, all three arms steering on a lead of 4, the worst
  correction figures of the sequence (**37 of 39** above one step at **4.00**) with `reconciles
  from history 142 hit / 0 missed` — nothing missing from the history, so the corrections were pure
  over-lead. A provisional age above one snapshot interval is not a plausible age for a healthy
  route (a route genuinely that slow produces a fit whose slope *reproduces*, and takes the fitted
  branch), so it is now treated as **evidence of an unusable reading** and contributes zero. An
  untrustworthy timebase must produce an under-lead, not the largest lead available; the uplink is
  still covered by the acknowledgement floor, which is measured independently.

- **The provisional floor decays per epoch, like the fitted path's anchors.** It only ever moved
  downward, so it was a memory of the session's fastest moment and the height above it carried the
  whole of any rate difference accumulated since. One epoch of memory now, the same shape
  `AckLatencyEstimator` uses, so a single unlucky epoch cannot leave the client without a reading.
  This bounds the accumulation; it does not eliminate it at large apparent skew, which is why the
  saturation refusal above is the load-bearing half.

- **`SNAPSHOT AGE ... (fitted)` was labelled from the wrong flag, and the provisional age carries
  no rate term.** Two defects in the work this release added, found by a run that could not be
  read because of the first. The label was driven by `IsUsable` — "a line exists" — while the age
  had fallen to the provisional path because the slope was refused, so the report asserted the
  opposite of what the code did on the one line the investigation turned on. `AgeIsFitted` is now
  printed. And the provisional reading is `(y - x) - floor` at **unit rate**: it has no slope, so a
  client clock *n*% fast adds *n*% of elapsed time to it every second. Measured: **45.56 base
  ticks** (759 ms) on a run whose apparent skew was 81 351 ppm over ~10 s — 0.074 × 10 s = 740 ms,
  which is the reading to within 2%. The same build read **0.09** on a run whose skew was −55 ppm.
  It is bounded where it steers, by `Math.Min(.., gap)`, so no lead exceeded 4 in any arm; the
  reported number is not bounded and now says what it is measured against.

- **The age band's trend test was multiplicative and could not see a linear drift.** `second half >
  first half × 1.5` reported `43.31 -> 45.60` as *"flat: the age is a stable property of the
  route"*, because the ratio is 1.05 — on a quantity with a large offset, a proportional test is
  blind to exactly the additive growth it is there to catch. Now `second - first >= 1.0` base tick.

- **The estimator's own observation distribution is printed** — count, p10 and median in base
  ticks. The harness's floor times ~20 sample inputs while the estimator times every send at a
  different phase, so when they disagree there is no way to tell which distribution is unusual
  without seeing the estimator's. Both statistic choices made in this cycle were made by reasoning
  about the harness's twenty samples, and both were wrong.

### Known

- **The snapshot interval the sweep requirement scales by is itself a minimum, and reads about
  25% low.** `AckLatencyEstimator` measures the interval as the smallest gap between
  acknowledgements — and a gap *can* fall below the cadence, when one arrival is late and the next
  is on time, so it reads the interval **less the arrival jitter**. Every requirement scaled by it
  is weakened in proportion: on a 66 ms cadence with a frame of jitter, roughly a quarter. This is
  the third instance in one class of a minimum standing in for a quantity it is silent about, and
  it is recorded rather than fixed on purpose — folding it in behind the sweep fix would make its
  own effect unattributable, which is the mistake this release spent a run avoiding. The guard is
  *lenient* because of it, never strict, so it cannot cause an over-lead on its own.

- **A phase-locked client cannot measure its own pipeline constant, and the round-trip fallback is
  zero on a fast link.** A client whose send cadence equals the snapshot cadence never sees a small
  wait, so no floor can be offered — correctly, since inventing one is the over-lead defect. And
  `rttTicks = round(RoundTripMs * Hz / 1000)` is `round(4 × 60 / 1000)` = **0** on loopback, so the
  fallback does not degrade gracefully, it vanishes. The choice for such a client is therefore
  *measured floor or nothing*, and `uplink + snapshot age` stays uncovered for it. Closing that is a
  **send-cadence** decision — deliberately offsetting the client's send rate from the snapshot rate
  so the wait sweeps — which is a product change, not a netcode fix, and is not made here.

## [0.33.0] - 2026-09-08

### Fixed

- **The prediction clock no longer runs a whole snapshot interval past the server for the
  first eight seconds of every session.** `WorldViewBinder.TargetLeadTicks()` took the
  snapshot's age from `SnapshotStalenessEstimator` and, until that estimator had fitted a
  rate, fell back to a derived one snapshot interval. A rate is a slope and cannot honestly
  be fitted over a short baseline, so `IsUsable` cannot turn true early; measured against a
  15 Hz snapshot stream the first fit lands **8.2 s after join** — epoch one only sets an
  anchor, and two consecutive two-second epochs cannot span the four seconds a fit needs.

  On localhost the derived fallback is **4 base ticks against a real age of 0.06**. The lead
  steers a clock rather than reporting one, so that error makes a tick number stop naming the
  same moment on the two sides, and `LocalMovePredictor.Reconcile`'s history path — which
  indexes the client's own history by the *server's* tick number — returns the whole of it as
  a positional correction of **0.3333 world units, 4.00 steps** at every start and stop. A
  live run read 162 reconciles, 36 corrections and a max correction of 4.00 steps with both
  sides agreeing on 60 Hz, `TickRateDisagrees` false and every other counter clean — the
  signature of a 4x tick-rate mismatch, produced by no rate mismatch at all.

  `SnapshotStalenessEstimator` now offers the age *provisionally* from
  `MinimumProvisionalSamples` snapshots (~0.2 s) onward, as the height above a running
  unit-rate floor, flagged by the new `HasEstimate` alongside the unchanged `IsUsable`. The
  age does not need the rate: over a few seconds the envelope's slope is one to within a few
  hundred ppm, 0.02 base ticks over ten seconds against the four it replaces. The rate fit,
  its baseline requirement and the test that pins it are untouched.

  The binder takes `min(provisional, derived)` while the line is unfitted and believes a
  fitted reading outright. The asymmetry is not a heuristic: an unfitted rate can only drift
  the reading upward, so below the derived figure the reading is evidence and above it it is
  drift. Taking the smaller is never worse than the fallback it replaces.

  Measured live: **max correction 4.00 steps to 2.00**, and `TARGET LEAD` from 4 to 0 off a
  fitted line.

- **The base-tick clock now runs on the server's timebase, using the rate already fitted.**
  `SteerToServerTick` is a proportional controller with no integral term, so against a
  constant clock-rate difference it settles at a standing tick offset instead of removing it
  — ordinary droop, of exactly `drift / (gain * snapshotHz)`. At gain 0.1 and 15 snapshots a
  second that is `drift / 1.5`: a client clock 9% fast against a 60 Hz server gains 5.4 ticks
  a second and sits **3.6 base ticks** ahead, permanently. Reproduced across 1.00x–1.103x,
  matching the formula to two decimals, and pinned by `PredictionClockRateTests`.

  That offset is not a diagnostic. `Reconcile`'s history path compares at the snapshot's own
  tick NUMBER, so an offset of n ticks makes the two sides label different moments with the
  same number and the whole of it comes back as position. Live, a client measuring the wire
  at **55.0 Hz against an advertised 60** (ratio 1.091 — the Windows-performance-counter-
  against-Linux case `MinimumSkew`'s remarks document) sat at a clock error of **3**.

  `SnapshotStalenessEstimator` had already fitted that rate as `SkewPpm` and nothing in
  `Runtime/` read it. `LocalMovePredictor.SetClockRateScale(float)` now scales the base-tick
  accumulator onto the server's timebase and `WorldViewBinder` feeds it the fitted rate
  before each steer. Feed-forward rather than an integral term: the number is already
  measured, and an integrator would rediscover it slowly and with wind-up. Only the tick
  accumulator is scaled — `_sinceInput` and `_elapsed` pace rendering against the real frame
  clock and are correct in client seconds.

  Gated on `Staleness.IsUsable`, never the provisional reading, which carries no rate at all;
  scales outside the reciprocals of the estimator's own `MinimumSkew`/`MaximumSkew` are
  **refused rather than clamped** and counted in `RefusedClockRateScales`; non-finite and
  non-positive values leave the last good scale standing. The default is 1.0 — exactly the
  previous behaviour.

  Measured live: **clock error 3 to -1**, with `clock rate correction` reading 0.9170x
  against a fitted 90 558 ppm.

### Changed

- **`PredictionLatencyMeasurement` bounds the correction MAGNITUDE instead of counting
  corrections**, and reports the clock offset that causes one.
  `SmoothedCorrections <= Samples / 4` was unreachable by correct code — it compared a run
  total over ~162 reconciles against a budget worded per sample, printing "36 of 20 samples"
  — and it misdirected twice, both times reading a high count with clean rate counters as a
  "4x tick-rate mismatch". The stimulus is 20 isolated impulses, so 40 start/stop
  transitions, and two free-running clocks at the same rate disagree by ±1 base tick about
  which tick a transition lands on: one step of correction at each is the floor. It is
  replaced by `MaxCorrection / ExpectedStepFromWire <= 1.5` steps plus
  `CorrectionsAboveOneStep <= 2`, sized by the rate measured off the wire because a client on
  the wrong rate sizes its own yardstick by that rate.

- **The reconciliation guard no longer reads a healthy client as an open loop.**
  `ReplayedSteps > 0` was written when replaying was the only thing a reconcile could do; the
  history path compares at the snapshot's own tick and returns, leaving nothing to replay. A
  live run failed claiming prediction "ran open-loop" with 140 reconciles and 0 replayed
  steps. The guard is now `HistoryHits + ReplayedSteps > 0`.

- **Correction figures are sampled across the whole run**, not inside the sample windows.
  Once the acknowledgement loop closed faster than a snapshot interval, the
  forced-divergence configuration — whose entire job is to prove a correction CAN happen —
  reported `max correction 0.0000`, because no snapshot arrived inside any of its windows.

- **The report gained the numbers that identify a clock offset rather than its symptoms**:
  `reconciles from history`, `SNAPSHOT AGE measured`, `TARGET LEAD in use`, `snapshot gap
  measured`, `round trip reported`, `ACK FLOOR`, `clock rate difference` (ppm), `clock rate
  correction`, and a `clock error` note that prints the droop the measured rate difference
  predicts beside the observed error. The harness also sets `binder.RoundTripMs`, which every
  real consumer does and it never did.

### Known

- **`InputToVisibleMovement_WithAndWithoutPrediction` is `[Ignore]`d with every assertion
  standing: the steering target does not yet cover `uplink + snapshot age`.** The client
  applies an input at its own base tick and the server applies it at the tick its packet is
  drained on, so the lead must cover the uplink; that plus the snapshot's minimum age is
  ~1 base tick on localhost and comes back as position at every start and stop. The residual
  is **2.00 steps against a floor of 1.00**, so the 1.5-step budget is correct and must not be
  widened — a bound that accepted 2.00 would accept the defect it measures. Every other
  counter reads clean, because a lower-envelope fit absorbs the constant by construction,
  which is why this has twice been misdiagnosed as a tick-rate mismatch. Follow-up:
  `AckLatencyEstimator` on branch **`feat/ack-latency-estimator`** — taken off this branch
  because it did not converge inside the measurement's ~9 s window (0.14 base ticks against
  the harness's own observed minimum of 0.68) and moved the clock error from -1 to -2.

### Added

- `SnapshotStalenessEstimator.HasEstimate` and `MinimumProvisionalSamples`.
- `LocalMovePredictor.SetClockRateScale`, `ClockRateScale`, `RefusedClockRateScales`.
- `PredictionClockRateTests`, pinning both the droop and its removal — the droop case
  deliberately included so the fix reads as a removal rather than a widened tolerance.
- `WorldViewBinderLeadTests`, pinning the steering target directly rather than through a
  downstream symptom; the defect it catches was invisible on every other counter.
- `InternalsVisibleTo("Cuvara.Netcode.Tests.PlayMode")`, so the measurement can report
  `WorldViewBinder.TargetLeadTicks()`. Diagnostic only.

## [0.32.0] - 2026-09-07

### Fixed

- **Reconnect Policy Demo reported `Reconnected in 0.0 s` for a ~40 s, five-attempt
  reconnect**, and drove the budget bar from the same wrong origin. `StateChanged(InWorld)`
  fires before `Reconnected`, and the sample cleared its start timestamp there, so the
  elapsed was measured from the successful attempt rather than from the close the policy
  decided to reconnect on. Seen live against a game server frozen 45 s with `docker pause`.
  Now started at the close, stopped only by `Reconnected`/`ReconnectFailed`, and measured on
  `NetworkSettings.MonotonicClock` — the same monotonic source the client budgets with —
  instead of `DateTime.UtcNow`. The header also marks the heartbeat button's
  `PongTimeout`/`PingInterval` override as sticky, which it always was.

- **A consumer could not supply its own `ITransportFactory` at all (regression in 0.31.1).**
  0.31.1 moved the default transport factory to a factory lambda; a caller that registered
  `ITransportFactory` after `RegisterNetworking()` — the documented way to substitute one until
  now — no longer overrode it but made the *whole container fail to build*, because two lambda
  registrations of one interface share the implementation type
  `VContainer.Internal.FuncInstanceProvider` and VContainer rejects the duplicate:
  `VContainerException: Conflict implementation type : Registration ITransportFactory
  ContractTypes=[] Singleton VContainer.Internal.FuncInstanceProvider` at
  `LifetimeScope.Awake()`, followed by a `NullReferenceException` from the scene component whose
  client never resolved. Seen in a Reconnect Policy Demo player against the live backend,
  2026-09-07. Fixed by the `transports` parameter below; the Reconnect Policy Demo now uses it.

### Added

- **`RegisterNetworking()` takes the dependencies it registers**, so exactly one registration of
  each interface exists and substitution needs no second registration:
  `RegisterNetworking(this IContainerBuilder builder, NetworkSettings settings = null,
  WireEncoding encoding = WireEncoding.Json, ITransportFactory transports = null,
  IWireCodec codec = null, INetLog log = null)`. Null keeps the previous default for each
  (`DefaultTransportFactory`, the codec `encoding` names, `UnityNetLog`); a non-null value is
  registered as an instance, and `codec` wins over `encoding`. Source-compatible — existing
  call sites are unchanged. The XML docs state why registering these interfaces yourself
  afterwards cannot work.

- **Sample: Reconnect Policy Demo** (`Samples~/ReconnectPolicyDemo`). Builds `NetworkClient`
  through `RegisterNetworking()` in a VContainer `LifetimeScope` — the DI path, not a hand-built
  client — reads the same `-cuvara-*` / `CUVARA_*` backend flags as the DOTS sample,
  authenticates with Nakama and joins. UI Toolkit buttons: **Kill transport** (closes the live
  game-session transport → `PeerClosed`/`TransportError` → automatic reconnect), **Simulate
  heartbeat timeout** (drops `PongTimeout` to 3 s and blackholes the session transport's reads →
  `HeartbeatTimeout` → automatic reconnect), **User close** (`Disconnect()` — must not reconnect),
  **Connect again** (a fresh operation after a user close). A live panel shows state, attempt
  n/N, elapsed vs the 60 s budget, the operation generation, the last close cause and every
  `ReconnectProgress`/`Reconnected`/`ReconnectFailed` event; the log carries the `[DOTSNet]`
  markers the multi-client harness reads.
- `NetworkClient.Generation` — read-only operation generation for diagnostics overlays (the
  demo shows it). Pinned by `NetworkClientGenerationTests`.


## [0.31.1] - 2026-09-07

### Fixed
- **`RegisterNetworking()` could not resolve `NetworkClient` from a scope.** It registered
  `DefaultTransportFactory` by type, whose constructor takes `string transportKey = null`;
  VContainer does not honour default parameter values, so the first scene component that
  injected `NetworkClient` failed with `No such registration of type: System.String`
  (IndieRPGMMOAdventure MainScene, 2026-09-07). Every sample built the client by hand, so the
  registration had never been exercised. Now registered through a factory lambda, with a
  bare-container resolution test gated on VContainer being present.

### Added

- **Reconnect policy by disconnect cause** (`ReconnectPolicy`, audit F08). `PeerClosed`,
  `HeartbeatTimeout` and `TransportError` — NAT expiry, Wi-Fi hand-off, app suspend — now
  reconnect automatically, immediately and then with exponential backoff + jitter, inside a
  60 s total budget. Not 25 s "inside the 30 s hold": the hold starts when the *server*
  notices the drop, which on a client-side loss is up to 30 s later and on a server freeze
  is only after it comes back — measured live 2026-09-07 (45 s freeze, two clients): 25 s
  gave up four rounds in, seconds before the server re-registered. `server_shutdown` keeps its
  delay-first round (storm spreading). A user close, a `kick` (any reason), an unpaired
  `disconnect` with any reason but `server_shutdown`, and a protocol error never reconnect.
  A gateway `kick` marks the client evicted so the session drop that follows is terminal.
  Every round re-authenticates through `IAuthProvider` (the old join token was consumed).
  Rounds stop early on permanent server answers — `invalid token`, `invalid auth request`,
  `map is not available`, or a provider that cannot produce a credential — and surface the
  real error. Full cause → action → budget table in `Documentation~/NETCODE.md`
  ("Reconnect policy"); `ReconnectPolicyTests` pins it.
- `NetworkSettings.ReconnectOnConnectionLoss` (default on), `ReconnectMaxDelay` (8 s),
  `ReconnectBudget` (60 s), `HeartbeatScheduler`, `MonotonicClock`.
- `NetworkClient.ReconnectProgress` event carrying the existing `ReconnectionProgress`
  struct (attempt, cap, pause), `NetworkClient.IsReconnecting`,
  `NetworkClientState.Reconnecting`, `ReconnectExhaustedException` (`Attempts`, `Elapsed`,
  `Permanent`, last failure as inner) delivered through `ReconnectFailed` when the loop
  gives up. `GameSessionClient.CloseInfo`.
- **Operation generation** (audit F09, netcode half). `ConnectAsync`, `TransferToMapAsync`,
  `Disconnect()`, `Dispose()` and each reconnect round start a new generation; every async
  step re-checks its token and generation after each await, and a superseded flow completes
  with `OperationCanceledException` without touching state. The gateway and session are
  locals owned by the flow until the join lands; a `finally` disposes both on any failure,
  cancel or supersede, so `State` always matches what is connected. Auth (including the
  `IAuthProvider` call) moved inside that ownership — a cancel during auth used to leave the
  gateway socket open and `State == Authenticating`. `NetworkClientRecoveryTests` covers
  cancel-during-auth, stale completion after `Disconnect()`, a newer connect superseding an
  older one, and disconnect during a backoff pause.
- **Monotonic clock for elapsed time.** `WireConnection` measured heartbeat age and RTT with
  `DateTimeOffset.UtcNow`; an NTP step of +1 h between two pings read as 3600 s of silence
  and killed a healthy link. Heartbeat age, RTT and the reconnect budget now read
  `NetworkSettings.MonotonicClock` (a process `Stopwatch`). The `ping.timestamp` protocol
  field stays wall-clock and is used only as an echo match token; RTT is the monotonic
  delta to the matched ping. `WireConnectionClockTests` stages ±1 h steps.

### Changed

- `NetworkSettings.ReconnectDelay` default 2 s → **1 s** and the schedule is exponential
  (1, 2, 4, 8, 8 …, capped by `ReconnectMaxDelay`) instead of linear (2, 4, 6 …);
  `ReconnectAttempts` default 5 → 12 (the budget, not the count, normally ends the loop).
- `ConnectAsync(jwt, mapId, ct)` rejects an empty `jwt` with `ArgumentException` locally
  instead of sending it to the gateway.
- `TransferToMapAsync` closes the gateway politely as well as the session before redialing.
- The heartbeat loop logs (rather than silently dies on) an unexpected exception.
- `NetworkBootstrap` logs the new `Reconnecting` state.

### Documentation

- `Documentation~/NETCODE.md`: new "Reconnect policy" section (cause → action → budget
  table, permanent-error table, why the gateway link is not retried in place, "One
  operation at a time"); heartbeat section documents the monotonic clock; map-transfer
  flow updated. `README.md` feature bullets updated.

## [0.30.0] - 2026-09-06

### Added

- ConnectionStateChangedEvent with Previous, Current, Reason fields
- IConnectionStateObserver interface
- NetworkDiagnosticsViewModel for HUD binding
- ReconnectionProgress data struct
- ServerTimeInfo for debug HUD
- ContentReadyEvent for loading screens
- GracefulDisconnectOptions

## [0.29.0] - 2026-09-06

### Added

- **KCP transport — reliable UDP, wire-compatible with kcp-go v5.** `KcpTransport`
  implements `ITransport` over UDP using the same ARQ state machine the game server
  runs, ported from `GameServer.Net.Transport.Kcp`. Stream mode with the identical
  `[4-byte BE length][body]` framing as TCP, so nothing above `ITransport` knows which
  transport it is on. Tuning matches the server exactly: NoDelay=1, Interval=10ms,
  FastResend=2, NoCongestion=1, Wnd=128, MTU=1350. `DefaultTransportFactory` now
  returns a `KcpTransport` when the game server advertises `transport:"kcp"` instead
  of throwing. Optional AES-256-CFB encryption compatible with kcp-go's
  `NewAESBlockCrypt`, gated on a transport key passed to the factory.
  - `Kcp.cs` — pure ARQ protocol: no sockets, no threads, no Unity types.
  - `KcpTransport.cs` — `ITransport` over `UdpClient` with receive/update loops.
  - `KcpCrypto.cs` — kcp-go-compatible AES-256-CFB + CRC32 + HKDF key derivation
    (hand-rolled for .NET Standard 2.1 compatibility — Unity does not ship
    `System.Security.Cryptography.HKDF`).
  - `AssemblyInfo.cs` — `InternalsVisibleTo` for the test assembly, scoped to testing
    only; no runtime seam uses it.
  - 8 `KcpCoreTests` — delivery, bidirectional, stream ordering, fragmentation,
    framing, conv mismatch, empty send.
  - 8 `KcpCryptoTests` — roundtrip, wrong key, same key different instances, hex key
    decode, HKDF stretch, CRC32 known value, short packet.
  - **KCP Probe** sample scene — two KCP state machines back-to-back with configurable
    packet loss, no server needed.

- **Seamless map transfer via `NetworkClient.TransferToMapAsync(mapId, ct)`.** The
  gateway is already a redirector (ADR-3) and `enter_world` accepts any map id, so a
  map transfer is an orchestrated disconnect + re-join with no new wire message. Leaves
  the current game server cleanly, re-authenticates through a fresh gateway connection
  (the `IAuthProvider` returns a cached credential in the common case), and joins the
  new map's server. Cancels any in-progress automatic reconnect.
  - `NetworkClientState.Transferring` — emitted before the transfer begins.
  - `NetworkClient.CurrentMapId` — the map the client is on or was last on.

- **Structured network metrics via `INetworkMetrics` / `NetworkMetrics`.** Observable
  metrics with event-driven publishing: smoothed RTT + jitter, snapshot rate, bandwidth
  in/out, reconciliation count + mean correction, server/ack tick. The default
  implementation accumulates over a configurable window (5 s, matching the health line's
  existing cadence) and publishes a `NetworkMetricsSnapshot` struct. The health line in
  the DOTS sample can become a consumer rather than computing its own counters.
  - 8 `NetworkMetricsTests` — window publishing, RTT smoothing, bytes tracking,
    reconciliation tracking, reset, multi-window independence, zero-reconciliation edge.

## [0.28.2] - 2026-09-05

### Fixed

- **Snapshot gap tracking no longer blocked by alternating gaps.** The single-candidate
  design tracked one gap value at a time; alternating gaps (e.g. 3, 4, 3, 4 ticks) reset
  the counter on every switch, preventing either from reaching the 2-confirmation
  threshold. `SnapshotTickGap` stayed at 0, disabling the hold window. Replaced with
  per-gap-value counting (fixed 16-slot array, zero allocation): each gap value
  accumulates its own confirmation count independently. Test added:
  `AlternatingGapsAreEachConfirmedIndependently`.

## [0.28.1] - 2026-08-28

### Fixed

- **No health line while the session is down** (#59, follow-up). A health window
  landing mid-outage printed negative garbage: the predictor is reset during the
  outage (counters and base tick back to zero) while the baselines still held the
  old session's totals. The sample now re-baselines and stays silent unless the
  client is in world, so the first line after a reconnect measures from the
  reconnect.

## [0.28.0] - 2026-08-28

### Fixed

- **Ghost entities after an automatic reconnect** (#59). `StartPrediction`
  replaced the `WorldViewBinder` without despawning what the old binder held, so
  the new binder's despawn pass could never consider those entities and anything
  that left AOI or died during the outage stayed rendered forever, frozen.
  `StartPrediction` now calls `Reset()` on the outgoing binder first.
- **Negative health-line figures after a reconnect** (#59). The sample bridge's
  health baselines (`_lastFramesRx`, predictor counter marks) survived the
  session swap while the new session's counters restarted at zero — observed live
  as `reconciles=-179 framesRx=-36.6/s`. The `Reconnected` handler zeroes them.
- **`DOTSEntityView.SetState` erased entity rotation every frame** (#60). The
  unconditional `LocalTransform` write reset `Rotation` to identity; position
  writes now preserve the rest of the transform.

### Changed

- **The DOTS sample's per-frame view cost collapsed** (#60):
  - One shared `RenderMeshArray` (palette + enemy materials, both meshes) built at
    construction, indexed per entity via `MaterialMeshInfo` — materials were
    previously created per spawn, never destroyed (a steady leak under AOI churn),
    and put every entity in its own render batch.
  - `SetState` is change-gated on cached position/HP: static entities cost a
    dictionary lookup instead of 3–5 EntityManager accesses per entity per frame.
  - The entity-label sweep runs once per frame in `LateUpdate`; `OnGUI` reads the
    cache, draws only on `Repaint`, and reuses a cached `CalcSize` measurement.
  - Combat/attack polls guard with `IsEmptyIgnoreFilter` instead of
    `CalculateEntityCount()`, which completed the query's dependency chain — a
    per-frame sync against the simulation group to learn a number the guard never
    used.
  - `AutoAttackSystem` — the one O(players × enemies) system — is Burst-compiled
    like its siblings; nothing blocked the attribute.
  - `WorldViewBinder.Tick` skips its local-entity view write once `AdvanceFrame`
    owns the rendering (same condition under which it owns the clock); the write
    was superseded within the same frame, costing a wasted round trip.
  - `_binder.AdvanceFrame` runs on `Time.unscaledDeltaTime` — `deltaTime` is
    scaled by `timeScale` and clamped by `maximumDeltaTime`, while the binder's
    own clock and the camera follow are wall-time; the disagreement surfaced as a
    reconciliation snap after pauses and hitches.
  - `GameObjectEntityView` writes the health-squash `localScale` only when HP
    changed, instead of dirtying the transform hierarchy every frame.

## [0.27.0] - 2026-08-27

### Added

- **Automatic reconnect on `server_shutdown`** (#54). The server holds a
  disconnected player's entity for 30 s precisely so the client can come back
  into its own body, and nothing in the package consumed that window — a
  shutdown notice just ended the session. When the session closes with the
  server's `server_shutdown` reason, an `IAuthProvider` is registered, and
  `NetworkSettings.ReconnectOnServerShutdown` is on (the default),
  `NetworkClient` now re-runs the whole connect flow by itself: up to
  `ReconnectAttempts` (5) rounds, pausing `ReconnectDelay × round` plus jitter
  so the default schedule spans the hold window and a restart's reconnect wave
  arrives spread out instead of as one synchronized storm. New events —
  `ReconnectAttemptStarted`, `Reconnected`, `ReconnectFailed` — let a caller
  narrate it; a user-initiated `Disconnect()`/`Dispose()` never triggers it.
- **`DelegateAuthProvider`** — adapts a delegate to `IAuthProvider`, for token
  sources that are a method on an existing object (the DOTS sample's Nakama
  helper, a test fixture).
- **`NetworkSettings.EnterWorldTimeout`** (20 s) and **`RetryJitter`** (500 ms).

### Fixed

- The retry/reconnect EditMode tests wait on wall-clock deadlines instead of
  frame counts, and the pauses themselves go through a new
  `NetworkSettings.DelayScheduler` seam (default: the realtime player-loop
  delay used until now). The headless EditMode runner can spin a
  [UnityTest]'s `yield return null` steps without ever pumping the editor
  tick that completes a real `UniTask.Delay`, so a policy test crossing one
  stalled forever in CI while passing in an interactive Editor; the tests
  now schedule those pauses synchronously.
- **A retryable `enter_world` refusal now consumes a join attempt instead of
  aborting the connect** (#54). `EnterWorldAsync` sat inside the retry loop but
  outside its `try`: the gateway deliberately types "server is starting, retry
  shortly" as retryable and its single-flight allocation assumes the client
  retries — yet any assignment failure escaped with zero of the
  `JoinAttempts` burned. Refusals are now classified: the gateway's terminal
  precondition answers (`session expired`, `rate limited`) abort immediately
  with the real error instead of drowning it under "could not join"; everything
  else retries with a jittered pause.
- **`enter_world` runs under its own 20 s budget instead of `ConnectTimeout`'s
  10 s** (#54, server side rpg-mmo-server#235). Against a cold map the gateway
  may allocate a server and wait for it to register (its own handler budget is
  18 s); the old 10 s cancelled client-side a join the gateway was about to
  complete, and the cancellation aborted the whole connect.

### Changed (sample)

- **A reconnect is no longer a cold login.** `SampleNakamaAuth` caches the
  gateway JWT with its `exp` and answers from it while >30 s of validity
  remain, falling back to Nakama session refresh before device re-auth. Before
  this, every reconnect re-ran device auth (a Postgres round trip and the
  rate-limited `gateway_token` RPC, burst 5) for a credential the client
  already held — a 200-client server restart was ~400 avoidable Nakama calls,
  and more than five network flaps in an hour became a login failure. The
  bridge registers a `DelegateAuthProvider` over the cached path, which is
  what arms the package's automatic reconnect, and surfaces the new reconnect
  events in its status line.

## [0.26.0] - 2026-08-26

### Fixed (sample)

- **Only the local player auto-attacks, and only inside the server's range.** Two defects
  in `DOTSEntityView`, exposed together by the server's new `/status` attack counters
  during a zero-leaderboard-kills investigation (345 of 364 attacks rejected in 90 s,
  breadcrumb `target out of range: distance 18.42 exceeds 3.00`):
  - every player entity — remote ghosts included — received `AutoAttack`, so the bridge
    forwarded attacks that remote players' ghosts fired to the server **as the local
    player's input**, aimed from positions up to a map away (18.42 on a client whose own
    range check was 10 is only possible from another player's position);
  - the firing range was a hardcoded `10f` while the server's validator accepts 3.0
    (`GameConstants.AttackRange`), so the client rendered bullets and "attacked" targets
    the server silently refused — visually working, doing nothing.
  `AutoAttack` is now added only when `isLocal`, and its range is
  `Shared.GameLogic.Components.GameConstants.AttackRange` — the shared library exists
  exactly so client and server cannot disagree on a rule; the sample now uses it for
  this one too.

## [0.25.0] - 2026-08-26

### Changed (sample)

- **The combat path stops logging every shot.** `AutoAttackSystem` logged each fire and the
  bridge logged each attack send — together ~**1,200 log lines per minute** on a running
  client, the largest remaining source after the poll and counter spam was gated in 0.21.0.
  The bridge's send log moves behind `verboseLogging`; the per-fire log in the DOTS system is
  removed outright, because a `SystemBase` has no clean reach into that MonoBehaviour toggle
  and a log that cannot be turned off does not belong at several lines per second. The
  `verboseLogging` attack-counter diagnostics still cover the path when it is under
  investigation. Measured after: **~30 lines per minute**, all of them health lines.


## [0.24.0] - 2026-08-26

### Fixed

- **The render path allocates zero bytes per frame.** `WorldViewBinder.Tick` enumerated
  `WorldState.Entities` through its `IReadOnlyDictionary` interface, which boxes the
  dictionary's struct enumerator — **88 bytes per foreach, measured**, once per rendered
  frame, ~44 KB/s at 500 fps into Unity's stop-the-world GC. It was the only per-frame
  allocation left in the audited surface (`LocalMovePredictor`, the estimators and the
  interpolation path all measure 0 B on their per-frame paths). The binder now enumerates
  `WorldState.EntityMap` — the same map as its concrete type, added in `Shared.GameLogic`
  `sgl-v0.3.0` — which measures 0 B and is ~20 % faster at 100 entities (4,865 → 3,864 ns),
  the interface dispatch going with the box. `EntityMapIsTheSameMapAsEntities_NotACopy`
  pins the identity contract. Requires `com.rpgmmo.shared-gamelogic` at `sgl-v0.3.0`
  (manifest **and** lock).

### Added

- **`Clock Sync Probe` sample scene.** Two synthetic clocks, no server, no network: a dial
  sets how fast the client's clock runs against the server's, `SnapshotStalenessEstimator`
  fits the ratio from snapshot arrivals alone, and the prediction clock steers on it. The
  default dial position is **+110 ×10³ ppm — this package's own development machine**, the
  measured value that sat just past the original clamp and silently disabled the fit until
  0.23.0. Convergence, refusal past the clamp (now a colour, once a silence), a stalled
  frame eating into `ClampedFrames` instead of burst-advancing, and a stepped server clock
  moving `HardResyncs` instead of `Snaps` — each visible rather than read about. UI Toolkit
  (UXML/USS), per the sample-scene contract; readouts are the same counters
  `[DOTSNet/health]` prints, so numbers here compare directly with a live client.


### Added

- **`WireConnectionDispatchTests` — the transport layer's first tests (#50).** Five cases
  drive `WireConnection` through a scripted `ITransport`: every decoded frame is counted
  before dispatch decides its fate, a 1000-frame burst drains inside one `Start()` with
  exactly one read per frame, a ping is answered with a pong echoing its timestamp, the
  kick/disconnect pair closes once with the kick's cause, and a frame decoded after the
  eviction never reaches the consumer.

  What makes them runnable without pumping a player loop: a completed UniTask continues
  synchronously, so a transport serving pre-canned frames is drained entirely inside
  `Start()`, and when the script is exhausted the read loop parks on a task that never
  completes — exactly as it parks on a quiet socket. The one asynchronous case (the pong,
  whose write crosses a `Task.AsUniTask()` continuation posted to Unity's synchronization
  context even when the task completed synchronously) is a `[UnityTest]` coroutine for that
  reason, and the reason is written on it.

  The burst case is the regression fence for the half-the-player-loop-rate ceiling fixed in
  0.22.0: a read path that costs a scheduler hop per frame cannot drain a burst inside one
  call, and this asserts it in fifty lines instead of the investigation it actually took.
  `TcpTransport` itself — real-socket framing and scheduling cost — remains uncovered and
  #50 stays open for that half.

- The test assembly now references `UniTask`, which the fake transport needs.


## [0.23.0] - 2026-08-26

### Fixed

- **The skew clamp was set at the edge of reality, so the staleness fit was refused on every
  snapshot and nothing said so.** `MinimumSkew`/`MaximumSkew` were 0.90/1.10. The development
  machine's true ratio is about **1.1107** — the Windows performance counter runs fast against
  the Linux clock the server ticks on — so every pair was rejected, `IsUsable` stayed false for
  a whole session, and the steering silently fell back to the derived figure.

  It was invisible by construction: `SkewPpm` reads 0 without a fit, which is exactly what two
  clocks that agree look like. It surfaced only because a live client reported `fits=0` after
  215 s while `stSamples=540` proved the estimator was being fed.

  Bounds are now **0.75/1.33**. Between "two ordinary machines" and "a tick rate that is simply
  wrong" there is an order of magnitude — a client predicting at 60 against a 15 Hz server is
  off by 300 %, not by 10 — and the bound belongs in the middle of that gap rather than at the
  edge of the first. `FitsRefused` and `RefusedSkewPpm` now report a refusal and the value that
  was refused, so a bound that is wrong can be seen to be wrong instead of inferred from an
  absence.

  Measured after: `fits=16 refusedFits=0 skew=110,703 ppm staleness=0.21–0.28 ticks`, and the
  reconcile correction improved from 0.027 to **0.0134–0.0154 units** because the steering is
  using the measured staleness again rather than the derived one.

- **The DOTS sample renews its Nakama session instead of using one token until it stops
  working.** `SampleNakamaAuth` kept the session token and discarded everything else; on a
  default Nakama (`session.token_expiry_sec = 60`) every backend call was dead a minute into
  every session, silently, because the game connection uses a separate gateway token and keeps
  running (#48).

  It now keeps the `refresh_token` and the device id, reads the expiry from the token's own
  `exp` claim rather than assuming a server setting, and renews ten seconds early via
  `/v2/account/session/refresh` — falling back to a full device authentication when the refresh
  token has expired too (an hour by default). `Refreshes` and `Reauthentications` are counted
  separately, because "refreshing every minute" and "re-authenticating every minute" look the
  same from outside and mean different things.

  Renewal happens before each authenticated poll rather than on a 401, because a refresh on 401
  still costs one user-visible failure per expiry. Verified live: **185 s of running — three
  times the expiry — with zero `Auth token invalid`**, where the same setup previously failed
  and never recovered.

### Changed (diagnostics)

- The health line reports `stSamples`, `fits`, `refusedFits` and `refusedSkew`. The first two
  are what distinguished "the estimator is not being fed" from "the estimator is refusing what
  it is fed", which was the whole of the investigation above.


## [0.22.0] - 2026-08-26

### Fixed

- **The read path could not keep up with the snapshot stream below ~34 fps.** Every await in
  `TcpTransport` goes through `UniTask`, whose continuation is scheduled on Unity's
  `SynchronizationContext` and therefore costs a whole player-loop frame.
  `ReadFrameAsync` performed **two** awaits per frame — one for the 4-byte length header, one
  for the body — which caps the read loop at **half the player-loop rate**. Measured against a
  15/s server: 15.0/s at 60 fps, 10.00/s at 20 fps, **5.00/s at 10 fps**, with the socket
  backlog growing without bound below the knee.

  `ReadFrameAsync` now parses frames out of a 16 KiB receive buffer and touches the socket
  only when the buffer cannot satisfy the next frame; a `UniTask` that completes synchronously
  never hops the player loop, so a backlog drains within one frame instead of one frame per
  frame. The same sweep afterwards: **15.0/s held all the way down to 5 fps**.
  `ReadExactAsync` becomes `ReadSomeAsync` — one read, no read-exactly loop, because on a real
  network a split body made it three awaits per frame and dropped the ceiling to a third.

  Also removes `FlushAsync` from `WriteFrameAsync`: `NetworkStream.Flush` is a documented
  no-op, and on the player loop that await was not free. At 60 Hz input the write path had the
  same ceiling, so below ~120 fps a client could not send the input it was producing.

  **Irrelevant on a desktop at 480 fps, which is where this was investigated. Not irrelevant
  on Android**, where 30 fps is a normal target and 30 fps is exactly the knee.

### Not a defect, recorded because it cost an investigation

- **A client whose clock runs fast reports a low snapshot rate, and nothing is wrong.** On the
  development machine the Windows performance counter — which `Stopwatch` and
  `Time.realtimeSinceStartup` both read — runs measurably fast against the Linux clock the
  server ticks on (60.502 s against 58.290 s over one interval). A client counting frames in
  its own seconds therefore reads **13.7/s from a server sending 15.0/s**, its observed tick
  stream reads 54/s against 60, and `SkewPpm` reports ~81,400.

  All of that is the estimator working: it fits offset **and** rate, measured the real 8 %
  difference, and the steering absorbed it — which is why `Snaps` stays 0 and the reconcile
  correction stays near 0.02 units on a client whose clock disagrees with the server's by 8 %.
  The pre-0.20.0 design, a minimum-filtered offset with no rate term, would have read this as
  an offset growing forever.

  Two lessons worth keeping. **Agreement between a client's own clocks proves nothing** —
  `clockRatio = 1.0000` between `Time.realtimeSinceStartup` and `Stopwatch` was taken as
  evidence the seconds were real, and both read the same wrong counter. And **the 10 % clamp
  on the fit is closer to load-bearing than it looks**: one ordinary development machine eats
  8 % of it.


## [0.21.0] - 2026-08-25

### Changed (diagnostics)

- **The health line measures both clocks over the same window.** A snapshot rate that reads
  low is either frames that did not arrive or a second that is not a second, and only
  measuring both separates them. `unityWin` (`Time.realtimeSinceStartup`) against `swWin`
  (`System.Diagnostics.Stopwatch`, which is what the netcode itself reads) settled it in one
  run: `clockRatio=1.0000` over five seconds against `framesRx=13.8/s` from a server proven to
  send 15.000/s. The seconds are real and the frames are genuinely missing — see #49.


### Changed (sample)

- **The DOTS sample stops logging once a second for the lifetime of the client.** An
  `[Debug] AttackRequest count:` line fired every second unconditionally, and every consumed
  attack request logged as well. Both are kept — they were clearly useful once — behind a new
  `verboseLogging` inspector flag, off by default. The timer itself now ticks only when the
  flag is on.

- **The three Nakama pollers back off and stop repeating themselves.** `PollLeaderboardAsync`,
  `PollEconomyAsync` and `PollServerStatusAsync` each looped at a fixed cadence forever. Against
  a backend where the leaderboard has not been created that is a 404 and a warning every ten
  seconds for the whole session; against a backend that is simply down it is two HTTP requests
  every `statusPollInterval` with nothing to draw either way.

  One shared `PollBackoff` helper now doubles the interval per consecutive failure to a 60 s
  ceiling and resets on the first success. The log reports **state changes**: one line when a
  poll starts failing, one when it recovers carrying the failure count, and nothing in between.
  A poll that has been failing for ten minutes used to produce sixty identical warnings, which
  is the shape that hides a real change — see #48, where a token expiring at T+60 s was
  invisible inside exactly that noise. Per-poll response and body logs move behind
  `verboseLogging`. The on-screen error panels are untouched.


## [0.20.0] - 2026-08-25

### Added

- **A test scene for remote interpolation, which 0.19.0 owed and did not ship.**
  `Samples~/InterpolationProbe` drives a synthetic 15 Hz snapshot stream into the
  interpolation core — no server, no network, no backend to start — and renders it beside
  the reset-on-arrival algorithm the same release deleted, so the pop is visible as a
  difference between two dots instead of a paragraph.
  - **Why a shipped feature was still short something.** The free-running render clock
    landed with a changelog entry, a `Documentation~/NETCODE.md` section and four tests in
    `RemoteInterpolationContinuityTests`. Every one of those describes the fix in *numbers*.
    None of them carries what the change was actually about, which is how the motion
    **looks** — "stepped backwards 0.2000 units" is a sentence, whereas a dot snapping back
    is the defect itself. The standing rule here is that a feature ships a changelog entry,
    documentation **and** a scene; the scene is the deliverable that gets forgotten, and it
    was forgotten precisely because the other two were unusually thorough. Nobody could
    have looked at this fix and judged it by eye until now.
  - **What the scene shows.** One entity on a circle at a constant server speed, three
    dots: server truth now, the production core, and the pre-0.19 algorithm. Buttons inject
    a single early arrival, late arrival or skipped tick — the same three scenarios
    `RemoteInterpolationContinuityTests` constructs, reused rather than reinvented so the
    scene demonstrates what the suite actually defends. A repeat mode applies any of them to
    every *other* snapshot, and a jitter slider runs to ±150 ms.
  - **Measured by replaying the scene's own loop headlessly**, at 200 fps against 15 Hz
    snapshots, as a multiple of the median frame step: an injected early arrival renders at
    **1.13× on the production track against 4.36× on the old one** — reproducing the 4.3×
    the 0.19.0 entry reported, from an independent harness; a late arrival, **1.09× against
    7.86×** with two backward steps on the old track; a skipped tick, **1.05× against
    7.44×**. Continuous ±15 ms jitter costs the production track nothing and gives the old
    one **48 backward steps**. The production track's backward-step count is **zero in every
    scenario the scene can produce**, and the readout says in words that a non-zero one is
    a bug worth reporting.
  - **The jitter slider deliberately runs past the buffer.** `TargetDelay` is 100 ms, and
    at ±100 ms of arrival jitter the production track's largest step jumps from 1.25× to
    **4.52×** — the buffer emptying, exactly where its own depth says it should. Past that,
    at ±150 ms, motion is visibly uneven and **still has not stepped backwards once**,
    because the clock rate is floored above zero unconditionally whatever the config says.
    A slider that stopped below 100 ms could never have shown either half of that.
  - **The old algorithm is duplicated inside the sample, on purpose and under a banner.**
    `Scripts/ObsoleteResetOnArrivalInterpolator.cs` reimplements the deleted pre-0.19
    arithmetic — arrival-stamped phase, `t <= 1.2` clamp, EMA of arrival gaps — because a
    side-by-side is a far stronger demonstration than a description, and there is nothing
    left in `Runtime/` to compare against. It is referenced from nothing outside
    `Samples~/`, it is not tested, and the file opens with a block saying it must never be
    fixed, extended or reused: improving it would destroy the only thing it is for.
  - UI is UXML and USS, like every scene in this package — no IMGUI and no uGUI canvas —
    and every asset in the sample carries its committed `.meta`.


### Added — the snapshot's age is measured, and it steers the prediction clock

- **`SnapshotStalenessEstimator` fits the server's clock to the client's — offset *and* rate
  — and reports how old the newest snapshot is when it is acted on.** A snapshot stamped with
  base tick `T` was produced at server time `T / hz` and observed at local time `t`, so
  `t = offset + skew * (T / hz) + delay` with `delay >= 0`. Because the delay is never
  negative the samples lie **above** a line and touch it at their best moments; fitting that
  lower envelope gives the offset and the rate, and a sample's height above it is that
  snapshot's age. Least squares would be wrong — it fits the middle of a distribution whose
  upper side is unbounded delay, so every bad frame drags the answer.

  The envelope is fitted through two anchors, each the lowest sample of its own epoch, kept
  far apart so the delay is largely divided out: the long baseline is what makes a small rate
  difference measurable at all. It is the cheap form of the convex-hull method used for clock
  synchronisation over paths with unknown delay. Sampled at the moment of **use**, so the wait
  for a client frame — the term a fixed formula cannot express — is inside it. `SkewPpm`,
  `BaselineSeconds` and `Fits` report the fit. 17 tests, all driven by a synthetic clock so a
  reading is a property of the arithmetic rather than of the machine.

- **`WorldViewBinder` steers on it**, with the derived figure (one snapshot interval plus the
  rounded half round trip) as the fallback for the first seconds of a session, and a ceiling
  of two snapshot intervals plus a round trip. The ceiling is not decoration: this number
  steers a clock, and a measurement that can run away takes the simulation with it.

- **`LocalMovePredictor.TickRateHz`** — the rate the predictor was built with, so a tick → time
  conversion uses the rate the *server* stamped ticks at rather than one estimated off the
  wire.

**Why it is measured rather than derived.** The derived figure is whole ticks while the real
age is fractional and set by where a client's frame loop falls against the server's send
cadence — a phase fixed at join that then holds for the session. Two clients on one machine
against one server, started six seconds apart: an unlucky phase left one with a constant
**0.3333-unit, 4.00-step** correction on every snapshot while the other sat at 0.0033.

**Why the rate term is not optional, and what it cost to learn.** A first version fitted the
offset alone against a fixed rate. A minimum-filtered offset cannot see a rate difference, and
one it cannot see appears as an offset that grows without bound. Wired to the steering target
it made a live client categorically worse, twice: fed a rate measured off the wire (57.7 Hz
for a 60 Hz server) it drifted 4 %/s, the reading passed **613 ticks** with the target
following it and snaps reached **71 per five-second window**; fed the advertised rate it still
settled around **205 ticks** where two or three was right. Both are one defect — a term the
model did not have. `ARateDifferenceIsMeasuredRatherThanAccumulated` now covers 500, 5 000 and
40 000 ppm, the last being exactly the case that broke the old design.

**Measured live, two clients, the join stagger that used to be the bad case:**

| | before | after |
|---|---|---|
| staleness reading | 205–613 ticks, climbing | **0.00–3.44 ticks** |
| correction, client A | 0.0033 | **0.0029–0.0151** |
| correction, client B | **0.3333 constant** | **0.0000–0.0147** |
| snaps | 0 | 0 |

**A finding the fit hands over.** The rate it settles on is **~81 400 ppm — 8.1 %**: the
snapshot-tick stream a client observes advances that much slower than its own clock. That is
the "server writes 15 snapshots/s, client counts 14.2" gap, quantified rather than inferred,
and it is now absorbed instead of accumulating. It is **not explained** — the server's
`snapshots_frames_written_total` says 15/s per client reached a socket, so the loss is in the
client's read path. Worth its own investigation; the 10 % clamp is what stands between a worse
figure and a refused fit.

### Changed

- **Prediction no longer banks elapsed time: every step is exactly one tick.**
  `StepDeltaTime` returned `min(now - lastMoveTick, MaxBankedTicks) * dt`, mirroring what
  the server then did, so the inputs per-tick coalescing discards from a burst did not take
  their simulated time with them. **Both sides have dropped it in the same change**
  (`rpg-mmo-server` `gameserver-dotnet` CHANGELOG, same date) — a client that banks against
  a server that does not is the same defect pointing the other way.

  It restored the right distance and destroyed the frames it was supposed to save: measured
  against a live server, a 1.36-unit step where a normal one is 0.083, read by a player as
  the avatar jumping. Worse, agreement depended on the two ends' *independent* measurements
  of elapsed time matching, which across a network is exactly what cannot be relied on — so
  a correctly predicting client was snapped back on ordinary jitter. That is the reported
  symptom: move one step, jerk back, continue. One step per tick makes the two sides agree
  **by construction**: over any interval each takes one step per tick, so each travels
  `speed x ticks`. Jitter can shift *when* a step happens, never *how many*.

- **The hold window is the shared silence timeout, not the measured snapshot gap.**
  `ApplyHeld` expired a held direction at `baseTick - heldFrom >= HoldTicks`, where
  `HoldTicks` came off the wire through `SetHoldTicks`. It is now
  `> MaxBankedTicks` — `GameConstants.MaxBankedMovementTicks`, 250 ms, the same constant
  `InputHandler` compiles against. Two consequences: the client can no longer expire a
  direction on a different tick from the server, and the join-keyframe failure mode
  (`SnapshotTickGap` pinned at 1, hold off for the whole session) is gone at the source.
  `>` rather than `>=` is deliberate and matches the server: gaps `1..MaxBankedTicks` step
  inclusive.

  `HoldTicks` and `SetHoldTicks` remain as **diagnostics** — `WorldViewBinder` still feeds
  the cadence to the interpolation clock, where it still means what it says — and gate no
  movement. `SkipNoHoldWindow` is retired at 0; nothing can switch the hold off now.

### Fixed

- **An explicit stop landing on a tick the hold had already stepped was swallowed.**
  `RecordInput` forced the verdict to `Accepted` on a coalesced input, which *refreshed* the
  held direction instead of clearing it — so releasing the stick on such a tick coasted for
  the full 250 ms silence timeout, the one artefact a player attributes directly to their
  own input. The vector is now put to `MovementSystem` for its verdict and the position
  thrown away, which is the client's half of the guard `InputHandler.ProcessInput` already
  had. Unreachable before this release: an input never landed on a tick the hold had
  stepped, because the hold expired on exactly the tick the next input arrived on.

- **A coalesced input froze the rendered position for the rest of the tick.** It zeroed
  `_step` and restarted `_sinceInput` while the step the *hold* took on that tick was still
  mid-interpolation. Measured as a render lag rising from 1.00 to 2.04 steps on the tick
  after every send and decaying back over the interval — a hitch once per send, invisible to
  every correction counter because the simulated position was right throughout. Render state
  is now left alone by an input that produced no displacement; the lag is flat at 1.00 step.

### Fixed (continued)

- **A direction change landed one base tick late, and the live path disagreed with its own
  replay.** `Advance` steps the hold on *entry* to a base tick, so on a tick whose own input
  has not arrived yet the hold applied the OLD direction and rule 1 then coalesced the new
  one away. The server has no such ambiguity — `TickLoop` drains a tick's inputs and calls
  `ApplyHeldMovement` after — and `Reconcile`'s replay loop has always run in that order.

  Rule 1 is **"one step per tick", not "first step wins"**. An input arriving on a tick the
  hold already stepped now **re-takes** that tick's single step, from the position the tick
  began at, in the input's direction: same count, same origin, same arithmetic, newest
  direction. A stop or a vector the model refuses rolls the tick back instead, which is what
  the server does by never taking the step at all; the rollback is folded into the render
  offset and decays rather than jumping.

  Reordering `Advance` to close out the ending tick was the other candidate and was measured:
  it fixes the same eight tests and puts the hold's step a tick late for the renderer, taking
  frame-delta burstiness from 1.00 to 3.23. Replacement keeps both properties.

  `PendingInput.LastMoveTickBefore` carries the pre-hold value for a replaced tick, which is
  how `Reconcile` knows the hold got there first and reproduces the same ordering. Without
  that, replay coalesced the input away, applied the hold in the previous direction, and the
  correction *accumulated* instead of converging — measured at 120 steps over 30 snapshots.

- **A replacement reported a zero step to the renderer** — it measured the step against the
  position after the hold's step rather than against the position the tick began at, so an
  unchanged direction read as "this tick produced no motion". The rendered position stalled
  for the rest of every send interval: lag rising from 1.00 to 1.95 steps and decaying back,
  once per send. Measured from the tick's start it is flat at 1.00 step.

### Added

- **`LocalMovePredictor` clamps how many base ticks one `Advance` may consume**
  (`MaxCatchUpTicks`, the same 250 ms budget as the hold window), and reports
  `ClampedFrames` / `DiscardedCatchUpSeconds`. `deltaTime` is whatever the last frame took,
  and at startup or after a focus loss that is *seconds*: without the clamp one frame
  burst-advances hundreds of base ticks, each stepping the held direction, and the lead that
  creates is never given back — the reconcile's lead replay is bounded by the hold window, so
  anything past it lands as a correction. Time over the budget is discarded rather than
  carried, which the second test pins: carrying it is the same burst one frame later.

- **`WireConnection.FramesReceived` and `WorldViewBinder.LastServerTick`**, diagnostics only.
  The first separates "the server did not send it" from "the client did not consume it" —
  opposite fixes, and no counter in the package could tell them apart. The second, against
  `LocalMovePredictor.BaseTick`, is the prediction lead in ticks: the one number that says
  whether the two clocks are keeping step.

### Changed (sample)

- **The DOTS sample auto-joins the map named on the command line.** `-cuvara-map map_01`
  was already parsed into `MapExplicit` but the bridge still waited for the on-screen map
  picker whenever more than one map was offered, so a built player sat on the selector
  forever — unusable for an automated run against a live server, which is the case the
  sample exists to serve.

- **`[DOTSNet/health]` prints the counters every 5 s** — reconciles, smoothed corrections,
  snaps, smoothing span against observed step interval, held steps against base ticks, fps,
  snapshots applied per second, frames received per second, RTT, and the client-vs-server
  tick lead. "It still feels jerky" and "Snaps=0" cannot both be acted on without this line,
  and every smoothness defect this package has shipped left the reconcile counters clean
  because the simulated position was right and only the rendered one was wrong.

### Changed (project)

- **`ProjectSettings.runInBackground` is now 1.** A server-authoritative client that pauses
  when it loses focus keeps its connection open while its simulation stops, then resumes
  with a multi-second `deltaTime` — the exact burst the new clamp exists to bound, and a
  guaranteed desync on top. It also made a built player untestable from a script, since a
  player launched without focus never progressed past scene setup.

### Changed (tests)

- **`LocalMovePredictorTests.ServerWalk` still implemented the banked rule** and was the last
  copy of it in the client. Unbanked; the walks it backs now advance *between* inputs rather
  than after the last one, which was adding a held step the model had no input for.

- **`TheCorrectionMeasuresTheClockError` is now `AWrongClockStillShowsUpAsACorrection`, and
  the instrument it pinned is gone.** `correction_steps = (clockFactor - 1) * SendEvery` held
  exactly at every factor; 1.25x, 1.5x and 2.0x now all report **1.00**. Three-argument
  `Reconcile` replays the ticks between the snapshot's tick and the client's as prediction
  lead (#53), and the lead it accepts is bounded by the hold window — which went from 66 ms
  to 250 ms. A clock error smaller than that is now replayed rather than corrected. Read the
  clock off the harness's `tick rate measured` against `TICK RATE IN USE` instead;
  `LastCorrection` still answers "do the two sides agree" exactly, and
  `ACorrectClockProducesNoCorrectionAtAll` still reads 0.00.

- **`RenderSmoothingTests` assumed one input meant one step** — true only while the hold was
  off for want of a measured snapshot gap. Its three stationary-avatar cases now release the
  stick explicitly, and `TheStepIsFullyShownAfterOneInputInterval` compares against the step
  that was taken rather than against a simulated position the hold has since moved on from.

## [0.19.0] - 2026-08-22

## [0.18.0] - 2026-08-22

### Fixed

- **Remote entities no longer pop, stall or sprint when a snapshot is early, late or
  dropped.** `WorldViewBinder` now renders them through the shared interpolation core
  instead of its own arrival-phase arithmetic. Three separate visible defects, all of them
  the same root cause and all of them previously invisible to the test suite, which
  asserted only `Is.LessThan(1f)` at the instant a snapshot landed.
  - **What a player saw before.** *Early arrival:* the avatar **lurched forward** — the
    unrendered remainder of the current segment was discarded when the phase restarted at
    zero, measured at **4.3x a normal frame's travel in a single frame**, on ordinary
    jitter with **no packet loss required**. Note the direction: forward, not backward. A
    test asserting only "never moves backwards" passes on the old code in this case, which
    is how it stayed uncovered. *Late arrival:* the phase ran to the `t <= 1.2` clamp, the
    entity **froze for two frames and then stepped backwards 0.2 units** — the extrapolated
    fifth of a segment being undone rather than absorbed. That is what rubber-banding is.
    *One dropped snapshot:* the entity rendered at **1.54x speed (23.1 units/s against
    15.0) and then stalled**, though the server never changed its speed — it covered two
    units in two ticks as always.
  - **What a player sees now**, measured on the same three streams: a maximum single-frame
    step of **1.15x the median** on the early arrival (against an assertion bound of 2.5x),
    **1.06x** on the late one, **no backward step anywhere in any of the three**, and a
    skipped tick rendering at **1.008x** its ordinary speed. The perfectly periodic control
    stream is unchanged at 1.06x against its tighter 1.5x bound.
  - **The mechanism, not the symptom.** The interpolation factor used to be
    `(now - arrivalTime) / measuredArrivalInterval`, with the arrival stamped and the
    factor read in the same `Tick` call — so it was exactly zero on every arriving
    snapshot, whatever the previous frame had drawn. Where an entity was drawn was
    therefore a function of when a packet arrived. It now comes from a free-running clock
    that an arrival does not reset, samples are bracketed by their tick rather than by
    time since arrival, and the interval is estimated per tick rather than per snapshot.
    None of the three defects can recur by construction rather than by care.
  - **The two-sample pair became an 8-deep ring**, which is what lets an early snapshot
    *wait* instead of displacing the segment being rendered. Rings are pooled across
    despawns: area-of-interest churn makes spawn and despawn steady-state events here, and
    a fresh array per entry would hand that churn to the garbage collector. The per-frame
    render path allocates **zero bytes**, measured — byte-for-byte identical to before this
    change, including the one pre-existing 88 B/frame allocation that comes from
    `foreach`-ing `WorldState.Entities`, an `IReadOnlyDictionary` whose enumerator boxes.
    That one is untouched and is not new; fixing it means changing `WorldState`'s public
    surface, which is a separate change.
  - **Remote entities now render 100 ms behind the newest received tick** (about 1.5
    snapshot intervals) where they previously ran one interval behind with no margin at
    all — which is precisely why they extrapolated and popped. **The local player pays
    none of it**: it is excluded from interpolation entirely and still renders at the
    newest received position, or at the predicted one when a `LocalMovePredictor` is
    supplied. Nothing about prediction, reconciliation or the `AdvanceFrame` clock-
    ownership rule changed.
  - `WorldViewBinder(IEntityView, LocalMovePredictor, IViewClock, InterpolationConfig)` is
    a new overload for a deployment at a different world rate; every existing overload
    keeps the defaults and every existing call site compiles unchanged. `RenderTick` is
    exposed for diagnostics — it should sit a little under `TargetDelay` behind the newest
    received tick, and a value drifting away from that is the shape of a rate-estimate
    problem.

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

- **A shared snapshot-interpolation core in `Runtime/Interpolation/` — no Unity types, no
  ECS types, no allocation, no managed fields.** `InterpolationSample`,
  `InterpolationConfig`, `InterpolationClock`, `ISampleBuffer`, `InterpolationRing` and
  the static `SnapshotInterpolation`. Nothing consumes it in this entry; it is added
  first, tested first, and wired in separately, so the swap can be reviewed as a swap.
  - **Extracted rather than fixed in place, because a second copy is the real risk.** The
    math has lived exactly once, in `WorldViewBinder`, and the DOTS path consumes its
    output through `IEntityView.SetState` rather than reimplementing it. Fixing it inside
    the binder would have kept that true only until the ECS path needed to evaluate at
    frame rate in a job, at which point there would have been two implementations of a
    thing whose entire job is that the client and the renderer agree. `Evaluate` is
    generic over `where TBuffer : struct, ISampleBuffer` precisely so Burst can specialise
    it per concrete buffer: the GameObject path passes a struct over a pooled array, the
    ECS path will pass a struct over a `DynamicBuffer`, and neither boxes.
  - **The render moment is a fractional server tick, not a phase between two samples.**
    `InterpolationClock.RenderTick` advances with real frame time and is steered toward
    `NewestTick - TargetDelay / SecondsPerTick` by running slightly fast or slightly slow,
    capped at 10 %. It is never snapped. The rate is `1 + adjust` and is floored above
    zero whatever the config says, so `RenderTick` is **strictly increasing for any
    positive frame delta** — which is what turns "a remote entity never steps backwards
    between two frames" from a hope into a property of the design, because the rendered
    position is a monotonic function of a monotonic clock along a fixed path.
  - **The target is built from a tick number, so arrival jitter cannot move it.** A tick
    is an exact integer off the wire. The cost is that the effective delay settles about
    half a tick beyond `TargetDelay`, since the target steps on arrivals while the clock
    runs continuously; 33 ms at 15 Hz, and worth paying for a target nothing can jitter.
  - **Seconds-per-tick, not seconds-per-snapshot.** The estimate is an EMA of
    `arrivalGap / tickGap`, seeded from `TickRateEstimator.SnapshotTickGap` — which was
    already being computed and handed to the predictor two lines away, and which the
    interpolator ignored. Because a dropped snapshot carries a proportionally larger tick
    delta, the ratio does not move. The first real measurement replaces the seed outright
    rather than being smoothed into it, so a join at a rate the seed guessed wrong does
    not render at the wrong speed for several seconds.
  - **Every magic number is now a named field with a stated reason.** `TargetDelay`
    100 ms (~1.5 intervals at 15 Hz: one interval is the minimum that can work at all,
    the extra half is jitter margin sized against the measured 8-13 ms RTT spread plus
    ±16 ms of scheduling jitter on a 60 Hz client); `MaxExtrapolation` 50 ms replacing the
    bare `t <= 1.2`; `RingCapacity` 8, ~0.53 s of history and the size that keeps a DOTS
    `DynamicBuffer` in chunk memory. `default(InterpolationConfig)` is all zeroes, so
    `Normalized()` fills any non-positive field from the defaults — except
    `MaxExtrapolation`, where zero is a deliberate "never extrapolate" and defaulting it
    would silently overrule a deployment that asked for no guessing.
  - **24 tests in `Tests/Editor/InterpolationCoreTests.cs`** pin the edges the four
    continuity tests cannot reach, because those are integration-level and would cover a
    single-sample buffer or a ring wrap only by accident: clock seeding and catch-up
    bounds, strict monotonicity under sustained maximum error, out-of-order rejection,
    empty and single-sample buffers, bracketing inside a deep buffer, the two-tick segment,
    the extrapolation cap and its disable, ring wraparound and ordering across several
    wraps, duplicate-tick refusal, and reuse after `Clear`.

- **`IViewClock` — `WorldViewBinder`'s interpolation clock is injectable.** A third
  constructor overload, `WorldViewBinder(IEntityView, LocalMovePredictor, IViewClock)`,
  takes the time source remote interpolation is derived from; passing null (or using
  either existing overload) gets `StopwatchViewClock`, which is the same self-starting
  `Stopwatch` the binder has always held privately. **Nothing observable changes at
  runtime** — no production call site passes a clock, and the default reads the same
  `Stopwatch.Elapsed.TotalMilliseconds` from the same two places it always did.
  - **Why it had to exist before anything else could be asserted.** The interpolation
    factor is `(now − arrival) / measuredInterval`, so every property worth asserting
    about the rendered motion — that it never steps backwards between frames, that a
    skipped server tick does not double the rendered speed — is a property of the curve
    *between* arrivals. With the clock private and self-starting, a test can only sample
    the instant it happened to execute at, and immediately after handing the binder a
    snapshot that instant is `t ≈ 0`: the one point on the curve where continuity is
    trivially true no matter how discontinuous the curve is elsewhere. That is why the
    existing interpolation tests assert `Is.LessThan(1f)` and nothing sharper, and their
    own fixture remark says so.
  - **Arrival time and frame time move independently through it**, which is the property
    that matters, because the defect being characterised is precisely the relationship
    between the two. The binder stamps an arrival with whatever the clock reads on the
    pass carrying a new snapshot and computes the render phase with whatever it reads on
    every other pass, so setting the clock before each `Tick` places arrivals and frames
    at chosen, unrelated instants on one timeline. A single knob that advanced both
    together would not have been enough.
  - Shaped as a null-defaulting constructor overload rather than an `internal` field plus
    `InternalsVisibleTo`, matching how the same class already takes its optional
    `LocalMovePredictor`, and as an interface in its own file with its implementation
    beside it, matching `INetLog`/`UnityNetLog`. `InternalsVisibleTo` appears nowhere in
    this package and would have been a new convention imported for one field.

- **`RemoteInterpolationContinuityTests` — four tests that characterise remote
  interpolation as a curve rather than as a point.** One remote entity travelling in a
  straight line at a constant server speed, arrivals at the package's own 15 Hz and frames
  every 5 ms, driven through the new `IViewClock` so arrival time and frame time are placed
  independently. Each test asserts on the *frame-to-frame* displacement — its sign, its
  size relative to the median step, and the rendered speed across an interval — never on an
  absolute coordinate, because a coordinate cannot distinguish smooth motion from motion
  that arrived by lurching.
  - **Three of them fail on the current implementation, deliberately, and are committed
    failing.** A suite that went straight from "does not exist" to "green" would never have
    shown anyone the defect it was written for. They are not `[Ignore]`d: a test that proves
    a defect has to run and be seen failing, and each carries a comment naming the specific
    production line it fails at and the free-running render clock that will make it pass.
  - **`APerfectlyPeriodicStreamRendersSmoothly` is the control and passes today.** Without
    it a failing suite is indistinguishable from a broken harness — if the clock seam, the
    frame pump or the sampling were wrong, everything would fail and the three failures
    would prove nothing. It is held to a tighter bound (1.5x the median step, against 2.5x
    for the others) because on a stream with no jitter the only irregularity left is the
    frame that straddles an arrival, 5 ms not dividing 66.7 ms evenly.
  - **The early-arrival case lurches FORWARD, and the test had to be written for that
    rather than for the backward pop it was expected to show.** When a snapshot lands, the
    interpolator shifts `From = To` and restarts the phase at 0, so it renders the *older
    endpoint of the new pair* — which is ahead of wherever the previous frame had
    interpolated to. Measured: a single frame moving **4.3x the median step** when the
    snapshot arrives 16.7 ms early, with **no backward step anywhere in the run**. A test
    asserting only "never moves backwards" — the obvious shape, and the one the defect
    report called for — would therefore have **passed** here and left the real
    early-arrival discontinuity uncovered. Both bounds are asserted for that reason.
  - **The backward pop belongs to the late-arrival case**, where `t` runs to the `1.2`
    extrapolation cap, the entity freezes, and the arriving snapshot then undoes the
    extrapolated distance instead of absorbing it: measured at **0.2 units backwards after
    2 stalled frames**, ~2.7x the median forward step. Stall-then-jump is what a player
    reads as rubber-banding, and it is bounded by the cap rather than by how late the
    snapshot was.
  - **A skipped server tick renders at 1.54x speed**, measured. The entity never changed
    speed — two units in two ticks is the same one unit per tick as always — but the binder
    divides a doubled position delta by an interval its EMA has moved only 30 % of the way
    (66.7 ms to ~86.7 ms), so a dropped packet reads on screen as sprinting and then
    freezing. Asserted on the mean over the affected interval, which is the stable
    statistic; the instantaneous peak is worse and is what is actually visible.

- **`Reconcile(Vec2, long, long)` — the prediction lead survives an acknowledgement that
  empties the pending buffer** (#53). The third argument is the base tick the snapshot was
  produced on.
  - An **overload, never a change**: the two-argument form is a cross-package contract
    enforced by `PredictionSurfaceContractTests` and driven by `com.cuvara.dots`. Callers
    that cannot supply the tick keep today's behaviour exactly, including today's defect,
    and a test pins that.
  - `WorldViewBinder` passes `world.Tick`, which it already had — it was feeding the same
    value to `SeedBaseTick` one line above, so the two clocks were already in one space and
    the plumbing was a single argument.
  - **Why the tick is genuinely required.** The cheaper fix was implemented first and
    measured: anchor the replay to the acknowledged input's own base tick, which the
    predictor already holds, needing no new parameter. It rebuilds the lead correctly and
    then over-replays whenever the snapshot already covers the hold window — **3 steps of
    phantom correction on a case with no lead at all**. The anchor supplies a start; only
    the snapshot's tick supplies the end. That attempt is not in any branch; the test that
    rejected it is.
  - Two things are deliberately not replayed, each an earlier attempt's measured failure:
    the acknowledged input's own step (the server applied it, so it is in `authoritative`),
    and anything at or before the snapshot's tick (the snapshot already includes it).

- **`ReconcileLeadTests` — a harness that actually measures the prediction lead** (#53).
  The defect is unfixed; this is the instrument a fix can be judged with, which #53 says has
  to exist first.
  - An earlier fixture of this name was shipped `[Ignore]`d by its own author because every
    configuration returned a constant `1.000` step — zero latency and both code paths alike.
    It never reached `develop` and no longer exists. A fix evaluated against it would have
    been evaluated against nothing.
  - What makes this one trustworthy is two tests, not the numbers: `ZeroLead_CorrectsByNothing`
    reads **0** where there is no lead to lose, and `PendingInputSurvives_TheLeadIsRebuilt`
    reads **materially different values on the anchored and unanchored paths** — compared
    against each other rather than a threshold, since indistinguishable readings were the old
    fixture's whole failure.
  - Three drafts were needed and each was corrected by a measurement rather than by reasoning:
    passing base ticks where `RecordInput` expects input ticks read one step off at every lead
    including zero; advancing after a consumed hold window produced no lead at all and looked
    like the defect being absent; and an anchor recorded *after* the held ticks rebuilt nothing,
    reading identically to the unanchored path. That last one is a real property of the replay
    and is documented at the call site.
  - The defect is pinned as characterization: `EmptyBuffer_DiscardsTheLead...` asserts the
    correction tracks the acknowledgement interval — 1, 2 and 3 steps for 1, 2 and 3 held
    ticks — and says at the call site that the expected value becomes `0` once #53 is fixed.
    These pass today. A fixture that failed would be reverted or ignored, and an ignored
    fixture is how the last one died.

- **`Generated Wire.cs matches the backend` CI job** (#20).
  `Runtime/Protocol/Generated/Wire.cs` is the third generated copy of `wire.proto` and the
  only one nothing guarded; the backend's two are covered by its
  `proto-generated-up-to-date` job.
  - A stale copy is the expensive kind of wrong: it **decodes cleanly**, reading any field
    added since generation as that type's default, so the symptom is a feature that looks
    wired up and silently does nothing while every test on both sides passes.
  - It diffs against the backend's **committed generated file** rather than running
    `protoc`. Both sides generate from one schema with one generator, so the artefacts are
    byte-identical by construction — md5 `c77092611a6e7815` on both when this landed.
    Regenerating here would mean pinning `protoc` and the C# plugin to the backend's exact
    versions, since generated output moves between generator versions; that yields diffs
    which are not drift, and a gate that cries wolf gets switched off.
  - It compares against the backend's `develop`, unpinned on purpose. A schema change
    landing there **should** turn this red until this package regenerates. A pin would hide
    the drift until someone moved it — the failure mode of the stale copy itself. The cost
    is that a backend change can redden a netcode PR that did not cause it; that is the
    two-repo protocol contract being enforced, not a defect.
  - On failure it prints the diff and names the trap the issue documented: a bare
    `--csharp_out` nests output under the `csharp_namespace`, writing
    `Generated/RpgMmo/Wire/V1/Wire.cs` and leaving the committed `Generated/Wire.cs`
    untouched **while reporting success**.

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
