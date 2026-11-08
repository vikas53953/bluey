# Boo performance budgets (8 November 2026)

Boo sits at the top of the screen all day, so its idle cost is part of the product. This page records what is
measured, where the budgets live, what changed, and the numbers from `windows-latest` before and after.

## What is measured, and where it fails the build

| Metric | How | Gate |
| --- | --- | --- |
| Startup to notch | Process start -> notch window found through UI Automation | `tests/Boo.Perf.Tests` (`budgets.json`, `startupToNotchMs`) |
| Idle CPU | `Process.TotalProcessorTime` over a 15 s window with the pointer parked away from the notch | `idleCpuPercent` |
| Idle memory | Max working set / private bytes during the idle window | `idleWorkingSetMb`, `idlePrivateBytesMb` |
| Idle allocation and GC | In-process `GC.GetTotalAllocatedBytes` / `GC.CollectionCount(0)` read through the `BOO_PERF_DIAG` probe | `idleAllocKbPerSecond`, `idleGen0PerMinute` |
| Render loop off while hidden | Notch frame counter must not move while the face is `Hidden`, before and after a hover reveal | `hiddenFramesPerSecond` |
| Render loop on while revealed | Frame counter must move at >= 20 fps while hovered | `revealFramesPerSecondMin` |
| Per-frame allocation (Core) | `GC.GetAllocatedBytesForCurrentThread` around 3600 `FaceMotion.Tick` calls; `FaceStateResolver` + timeline zero-alloc | `tests/Boo.Core.Tests/PerfBudgetTests.cs` |
| Render policy and pacer | Exhaustive truth table for `NotchRenderPolicy`, 60/144 Hz streams through `FramePacer` | `PerfBudgetTests.cs` |

The probe: when `BOO_PERF_DIAG=<file>` is set, `Boo.App/PerfDiagnostics.cs` rewrites that file every 500 ms with
GC counters, heap and working-set sizes, notch frames rendered, whether the render loop is subscribed, the face
state and pacer skips. Unset (every normal launch) it creates nothing.

CI: `windows-ci.yml` builds and runs `tests/Boo.Perf.Tests` against the published `Boo.exe` after the UI smoke
step and uploads `perf-results` (`perf-results.json`, `perf-results.md`, `diag.json`, `reveal.png`). A budget
breach fails the job. Budgets are deliberately set from measured runner numbers with headroom, not from a wish.

## Changes

1. **Render loop policy made explicit and testable.** `NotchRenderPolicy` (Core) decides when the notch needs
   `CompositionTarget.Rendering`; the window routes its start/stop through it. The loop is off whenever the face
   is hidden and the reveal timeline is in its hidden phase, which the perf test now proves on the runner.
2. **Frame pacer.** `FramePacer` skips vsyncs that arrive sooner than 10 ms after the last processed frame, so a
   120/144/240 Hz panel runs the face at 60/72/80 fps instead of doubling or quadrupling the per-frame managed work.
   60, 75 and 90 Hz streams are never throttled.
3. **Screen capture: one Direct3D device, zero-copy readback.** `WindowsFrameCaptureBackend` keeps its
   `NativeCaptureDevice` for the life of the app (dropped and recreated after any capture error) instead of
   creating and destroying a D3D11 device, DXGI bridge and WinRT device on every focus check. `NativeCaptureDevice`
   now encodes the JPEG straight from the mapped staging texture (`BitmapSource.Create` with the row pitch as
   stride) instead of copying every frame into a `width*height*4` managed array first: on a 2560x1440 display that
   removes a 14.7 MB large-object-heap allocation and a 1440-iteration row copy from every capture. The JPEG stream
   is pre-sized from the previous frame. `ScreenCapture` is now `IDisposable` and the panel disposes it on exit.
4. **Publish settings.** `PublishReadyToRun` (no JIT wait for the first frames), `TieredPGO`,
   `ConcurrentGarbageCollection=false` (one fewer always-on thread; Boo's heap is small so a blocking gen2 is
   milliseconds) and `SatelliteResourceLanguages=en` (smaller publish folder).

## Numbers from windows-latest

Filled from the `perf-results` artifacts of the two CI runs below. Runner numbers are noisy (shared vCPU, no GPU,
WARP Direct3D); they are for direction and gating, not for a spec sheet.

| Metric | Before (instrumentation only, run 85) | After (this branch, run 89) | Budget (fails CI) |
| --- | --- | --- | --- |
| Startup to notch (ms) | 1027 | 964 | 4000 |
| Idle CPU (% of one core) | 1.25 | 1.24 | 2.5 |
| Idle working set (MB) | 104.0 | 99.6 | 160 |
| Idle private bytes (MB) | 27.2 | 32.1 | 80 |
| Idle allocation (KB/s) | 2.66 | 2.65 | 16 |
| Gen0 collections per minute idle | 0 | 0 | 3 |
| Notch frames/s while hidden (before and after a hover) | 0 / 0 | 0 / 0 | 0.5 |
| Notch frames/s while revealed | 61.0 | 61.9 | >= 20 |
| Working set after the hover round trip (MB) | 124.8 | 114.9 | (informational) |
| Threads / handles idle | 18 / 543 | 18 / 544 | (informational) |

Reading: the lane/face baseline was already lean while idle, and the render loop was already off while hidden;
this branch's value there is the proof and the gate, not a drop. Startup improved ~6 % and working set ~4 %
with ReadyToRun (private bytes rose 5 MB because precompiled images map more private pages; working set, which
is what Task Manager shows, went down). The frame pacer does nothing on the runner's 60 Hz display by design
(`idlePacerSkippedDelta = 0`); its effect is on 120/144/240 Hz panels. The capture changes cannot be timed on
the runner (see below).

Before = commit 0ba778f (probe + tests, no optimisation), CI run 85. After = commit d161d29, CI run 89. Both on
windows-latest, single 60 Hz virtual display, 1024x768, WARP Direct3D. Budgets were then set to roughly 1.5-3x the
measured values so runner noise does not fail the build while a real regression (a leaked timer, a render loop that
stays on, a doubled working set) does.

## Not measured here

- Capture cost on a real GPU and real display (CI has WARP and a single virtual display); the device reuse and
  zero-copy change are reasoned, built and reviewed, not timed on hardware. Hands-on check: run a focus session
  with Developer Mode and watch Boo's private bytes stay flat across checks in Task Manager.
- Voice (Realtime) and the full panel are outside the idle budget: they only run when asked.
