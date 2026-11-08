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
2. **Frame pacer.** `FramePacer` skips vsyncs that arrive sooner than ~1/72 s after the last processed frame, so a
   144 Hz or 240 Hz panel no longer doubles or quadruples the per-frame managed work for a face authored at 60 fps.
   A 60 Hz stream is never throttled.
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

| Metric | Before (instrumentation only) | After (this branch) | Budget |
| --- | --- | --- | --- |
| Startup to notch (ms) | _pending_ | _pending_ | see `budgets.json` |
| Idle CPU (% of one core) | _pending_ | _pending_ | |
| Idle working set (MB) | _pending_ | _pending_ | |
| Idle private bytes (MB) | _pending_ | _pending_ | |
| Idle allocation (KB/s) | _pending_ | _pending_ | |
| Gen0 collections per minute idle | _pending_ | _pending_ | |
| Notch frames/s while hidden | _pending_ | _pending_ | |
| Notch frames/s while revealed | _pending_ | _pending_ | |

Before = commit "perf: harness" (probe + tests, no optimisation). After = head of this branch.

## Not measured here

- Capture cost on a real GPU and real display (CI has WARP and a single virtual display); the device reuse and
  zero-copy change are reasoned, built and reviewed, not timed on hardware. Hands-on check: run a focus session
  with Developer Mode and watch Boo's private bytes stay flat across checks in Task Manager.
- Voice (Realtime) and the full panel are outside the idle budget: they only run when asked.
