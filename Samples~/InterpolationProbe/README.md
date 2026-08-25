# Interpolation Probe sample

Drives a synthetic snapshot stream into the remote-interpolation core and shows what the
motion looks like — beside the same stream drawn by the algorithm 0.19.0 replaced.

**No server and no network.** Open `Scenes/InterpolationProbe.unity` and press play.

## Why this scene exists for a feature that already shipped

The free-running render clock landed in netcode **0.19.0** with a changelog entry, a
`Documentation~/NETCODE.md` section and four tests in
`Tests/Editor/RemoteInterpolationContinuityTests.cs`. All three of those describe the fix
in numbers. None of them carries the thing the change was actually about: *how the motion
looks*. "Stepped backwards 0.2000 units" is a sentence; a dot visibly snapping back is the
defect. A scene is the only artefact that shows it, so the feature shipped one short.

## What you are looking at

One entity travels a circle at a constant server speed, one snapshot per 15 Hz tick. Three
dots:

| Dot | What it is |
|---|---|
| **grey** | server truth *right now* — where the entity actually is |
| **green** | the production core: `InterpolationClock` + `SnapshotInterpolation.Evaluate` |
| **orange** | the pre-0.19 reset-on-arrival algorithm — **sample-only, see below** |

The green dot sits behind the grey one by the latency plus the 100 ms `TargetDelay`. That
gap is not a defect: it is the jitter buffer, and it is what everything below is bought
with.

A circle rather than a straight line on purpose — straight-line motion has to wrap, and a
wrap is a genuine backwards jump indistinguishable on screen from the defect being shown.

## Do this, expect that

Every number below was measured by replaying this scene's own loop headlessly at 200 fps
with 15 Hz snapshots, statistics from t = 1 s, quoted as a multiple of the median frame
step over a clean warm-up. They are what the readout should show you.

| Do this | Green dot (production) | Orange dot (pre-0.19) | What it means |
|---|---|---|---|
| Press play, touch nothing | max step **1.05×**, **no** backward step | max step **1.00×**, no backward step | The control. A perfectly periodic stream is the one case the old algorithm also renders smoothly — if the two columns disagree here, the harness is wrong, not the interpolator. |
| **Early arrival** | max step **1.13×**, **no** backward step | max step **4.36×**, **2 backward steps** | The lurch. One snapshot arrives a quarter of an interval sooner; the old phase resets to zero and discards the unrendered remainder of the segment. Note the direction — *forward*. This is ordinary jitter with **no packet loss at all**. |
| **Late arrival** | max step **1.09×**, **no** backward step | max step **7.86×**, **2 backward steps**, worst **0.0506 u** | Rubber-banding. The old phase runs to its `t ≤ 1.2` clamp, the dot stands still, then the arriving snapshot resets the phase and *undoes* the extrapolated fifth of a segment. |
| **Skip a tick** | max step **1.05×**, **no** backward step | max step **7.44×**, **2 backward steps** | The sprint-then-freeze. One snapshot never arrives; the next carries a doubled position delta, which the old EMA divides by an interval it moved only 30 % of the way. The entity never changed speed on the server. |
| **Repeat → every other snapshot early / late / dropped** | still no backward step | backward steps accumulate continuously | The same three, over and over, so you can watch rather than catch. *Every other*, not every one: shifting **every** arrival by the same amount is a constant latency, not jitter, and would do nothing. |
| **Arrival jitter → ±15 ms** | max **1.10×**, no backward step | max **6.21×**, **48** backward steps | Ordinary jitter. The buffer absorbs it completely. |
| **Arrival jitter → ±60 ms** | max **1.25×**, no backward step | max **13.3×**, **47** backward steps, worst **0.40 u** | Still absorbed. The green dot's render delay wanders and the clock trims its rate; the motion does not. |
| **Arrival jitter → ±100 ms** | max **4.52×**, still **no** backward step | max **19.1×** | **This is the edge, and it is where it should be.** The buffer is 100 ms deep; jitter of ±100 ms empties it. Watch the *render delay* readout dip toward zero just before the green dot starts making large forward catch-up steps. |
| **Arrival jitter → ±150 ms** | max **30×**, still **no** backward step | max **30×** | Past the edge. Motion is visibly uneven — and it is *still* monotonic. Nothing the config does can make the render clock reverse; the rate is floored above zero unconditionally. That invariant is the design, not a tuning. |

**The one number that never moves** is the green column's backward-step count. It is zero
in every row of this table, including the two where the buffer has run out. A backward step
on the production track means a real bug — the readout says so in words.

## The readout

| Field | What it is |
|---|---|
| frame step | distance the dot moved between the last two frames, plus a bar whose full width is 4× the median step |
| largest step | worst single-frame step since the last reset, and its ratio to the median |
| backward steps | how many frames reversed the direction of travel, and the worst one |
| render delay | how far behind the newest received sample the dot is drawn. Green: from the clock, `(NewestTick − RenderTick) × SecondsPerTick`. Orange: `(1 − phase) × interval` — it goes **negative** when the old algorithm extrapolates past anything the server ever said |

The bottom two lines report the stream (produced / delivered / dropped) and the clock
(render tick, newest tick, measured ms per tick, target delay).

## The orange dot is not production code

`Scripts/ObsoleteResetOnArrivalInterpolator.cs` is a **deliberate, sample-only
re-implementation of the algorithm deleted from the runtime in 0.19.0**. It exists so the
pop can be seen as a difference between two dots rather than described in a paragraph. The
file carries a banner saying exactly this, it is referenced from nothing under `Runtime/`,
and it must never be fixed or reused — improving it would destroy the only thing it is for.

Turn it off with the **Show the old algorithm** toggle if you want to watch the production
dot alone.

## Controls

| Control | Effect |
|---|---|
| Early arrival / Late arrival / Skip a tick | perturbs the **next** snapshot only, once |
| Repeat | applies the chosen perturbation to every **other** snapshot, continuously |
| Arrival jitter | uniform ±N ms on every arrival, 0–150 ms. The buffer is 100 ms deep — the slider deliberately goes past it |
| Pause | freezes the probe clock; the dots and the readout hold |
| Reset | clears both interpolators, the stream and every statistic |

Arrivals are kept in order however large the jitter is, because the wire is TCP. Without
that clamp a big jitter setting would reorder packets, the ring would correctly drop the
older one, and the screen would show extra packet loss the viewer never asked for.

## If the UI renders unstyled after import

`UI/InterpolationProbePanel.asset` references a theme by GUID, and a theme's GUID differs
per project — Unity generates `Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss`
fresh in each one. After importing this sample, select the panel asset and assign your
project's theme if the reference came through empty.

This is a general limitation of shipping `PanelSettings` in a UPM sample, not specific to
this one.
