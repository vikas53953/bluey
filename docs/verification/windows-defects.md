# Windows capture/action defect review

Review basis: canonical source `7ef0e07cd4222a2933acb2da1ec53e12acf67d45`
(the worktree later advanced to a follow-up commit, but the reviewed files are
unchanged). This is a read-only source review against
`docs/verification/acceptance-native.md`. The 84 passing tests do not exercise
the WGC, Win32 foreground, UI Automation, or packaged file-action paths. No
native runtime or live-provider claim is made here.

## P1

### P1-01 — A frame-arrival failure hangs capture and retains native resources

- **Source proof:** `src/Boo.Windows/ScreenCapture.cs:78-110` waits on
  `frameSource.Task`. The only completion path is `FrameArrived` or caller
  cancellation; there is no bounded wait or timeout around `StartCapture()`.
  Until that task completes, the `GraphicsCaptureSession`, frame pool, and
  `NativeCaptureDevice` `using` scopes at `50-55` remain undisposed.
- **Scenario/consequence:** A selected monitor disappears, the graphics-capture
  session starts but never raises a frame, or the native driver stops delivering
  frames. Without Stop/cancellation, the request never returns and the capture
  resources remain live; the app has no recovery state or bounded failure. This
  blocks the required capture/lifecycle behavior even though the happy-path
  tests are green.
- **Smallest repair:** Await the first frame with a short linked cancellation
  timeout, convert timeout/native completion failure to a readable limitation,
  and ensure the handler drains/disposes any late frame after the session is
  torn down.

### P1-02 — Concurrent captures restore exclusion while another capture is active

- **Source proof:** `ScreenCapture.CaptureAsync` applies each window's display
  affinity at `32` and restores the saved values in `72-75`. There is no
  instance gate or reference count around the whole capture lifetime.
- **Scenario/consequence:** The focus timer can overlap a user request. Capture
  A and B both save the previous affinity and set `WDA_EXCLUDEFROMCAPTURE`; A
  finishes first and restores the prior value while B is still capturing. B's
  next frame can therefore contain Boo's notch/panel/indicator, violating the
  privacy boundary while capture is reported active.
- **Smallest repair:** Serialize capture sessions per `ScreenCapture` instance,
  or maintain reference-counted per-HWND exclusion leases and restore only when
  the last session ends. Re-check/close the lease on cancellation and timeout.

### P1-03 — An all-zero owned-window list bypasses capture exclusion

- **Source proof:** `ValidateCaptureArguments` only rejects
  `ownedWindows.Count == 0` (`ScreenCapture.cs:113-118`). A list such as
  `[0]` passes, while `ApplyCaptureExclusion` silently skips zero handles at
  `139-148` and returns an empty prior-affinity list. Capture then proceeds at
  `42-69` without excluding any Boo HWND.
- **Scenario/consequence:** A transient/uninitialized HWND list can produce a
  real frame containing Boo's own UI and send it to the provider, despite the
  fail-closed exclusion requirement.
- **Smallest repair:** Require at least one nonzero, live owned HWND and fail
  closed if any required owned window cannot be inspected/excluded. Do not let a
  skipped zero handle count as successful privacy setup.

### P1-04 — Invalid/protected foreground events leave an old target retained

- **Source proof:** `TargetObserver.OnForegroundChanged` ignores a non-Boo
  foreground event when `ReadWindow` returns null or `IsProtectedWindow` is true
  (`TargetObserver.cs:171-180`), without clearing `_retainedTarget`. Later,
  `Snapshot` deliberately reuses that retained target while Boo is foreground
  (`106-110`). Only the display-read failure branch (`190-201`) clears it.
- **Scenario/consequence:** Observe external window A, switch to an inaccessible
  or protected window (or an unusable desktop HWND), then reopen/focus Boo. The
  old A identity can still be returned and used for capture or action even
  though the most recent external target was not inspectable. This violates the
  fail-closed and changed-target cases in NAT-004/NAT-007.
- **Smallest repair:** On every non-Boo foreground transition, clear the retained
  target before attempting inspection; retain only after a complete successful
  identity/display read. Treat a null HWND and protected/inaccessible target as
  an explicit no-target state. Preserve the old target only for Boo-owned
  foreground transitions.

### P1-05 — UI Automation can invoke an off-screen or wrong element without native target checks

- **Source proof:** `DesktopTools.Click` tries `TryInvokeAccessibleTarget` before
  `TryPrepareSendInputTarget` (`DesktopTools.cs:217-242`). The UIA helper finds
  only by AutomationId and immediately calls `Invoke()` (`721-735`); it never
  reads `BoundingRectangle`, `IsOffscreen`, or checks that the element is inside
  the currently observed target. `TrySetAccessibleValue` has the same omission
  (`754-774`). With no AutomationId, `FindElement` even returns the root
  element (`790-798`).
- **Scenario/consequence:** After scrolling a control out of view, or with an
  AutomationId that resolves to a hidden descendant, a click/type proposal can
  mutate that element while Boo is foreground. The UIA path bypasses the
  foreground/focus/hit-window checks that the SendInput fallback performs at
  `826-870`, so a stale or non-visible control can be acted on without a fresh
  visual/UIA target proof.
- **Smallest repair:** Resolve the element, require a finite non-empty visible
  bounding rectangle within the current target bounds (and matching any
  approved point/element identity), then revalidate HWND/process/bounds and
  target observation immediately before Invoke/SetValue. Use the same bounded
  timeout and fail-closed result as the SendInput path.

### P1-06 — Reparse-point checks are still vulnerable to path replacement races

- **Source proof:** `ScopedFiles.Resolve` walks existing components and rejects
  reparse points (`src/Boo.Core/ActionPolicy.cs:156-197`). `WriteFile` resolves
  again at `DesktopTools.cs:417-438`, but then uses path-based
  `FileStream`/`File.Move` at `440-457`; `DeleteFile` similarly resolves and
  checks at `515-528` before path-based `File.Delete` at `530`. No directory/file
  handle or final file identity is held across the check and mutation.
- **Scenario/consequence:** A local process can replace a checked parent with a
  junction or the checked target with a reparse point between the final walk and
  the path operation. The approved write/delete can then escape the selected
  root or affect a different file, defeating the NAT-015/NAT-018 containment
  contract. The deterministic reparse tests do not cover this check/use window.
- **Smallest repair:** Use handle-relative Windows file operations with
  reparse-point protection and verify the final file/directory identity before
  replacement/deletion; otherwise make the operation fail closed when the
  directory topology changes. Keep the readback tied to that same committed
  handle/path identity.

## P2

### P2-01 — Cancellation can still return a frame after the boundary

- **Source proof:** `ScreenCapture.CaptureAsync` checks cancellation at `59`,
  then performs synchronous GPU readback/JPEG encoding at `60` and returns a
  `CapturedFrame` at `69` without a second cancellation/generation check.
- **Scenario/consequence:** Stop during readback/encoding still allocates and
  returns a frame from the stopped generation. Current application callers add
  a later check, but the capture seam itself does not enforce NAT-009's “no
  frame after the boundary” rule and any future caller can forward the late
  frame.
- **Smallest repair:** Check the token/generation after readback and before
  constructing/returning `CapturedFrame`; make callers revalidate immediately
  before provider submission. Bound or isolate the synchronous readback so Stop
  cannot appear complete while native work continues indefinitely.

### P2-02 — Native COM/WinRT wrappers are released only by GC on repeated captures

- **Source proof:** `CaptureInterop.CreateItemForMonitor` obtains an RCW with
  `Marshal.GetObjectForIUnknown` at `34` and never releases the `interop` RCW.
  `NativeCaptureDevice.ReadJpeg` does the same for
  `IDirect3DDxgiInterfaceAccess` at `138`; `NativeCaptureDevice.Create` stores
  the projected `IDirect3DDevice` at `103`, but `Dispose` only releases the raw
  device/context pointers (`178-188`).
- **Scenario/consequence:** Every capture can leave COM/WinRT references alive
  until nondeterministic RCW/finalizer collection. Under periodic focus capture
  this can grow native handles/device memory and make the exact 15-minute soak
  fail or delay device teardown even though managed `using` scopes complete.
- **Smallest repair:** Give every RCW/projected device an explicit ownership
  release/dispose path in the corresponding `finally`/`Dispose`, and verify no
  COM reference remains after each capture. The magnitude of native growth needs
  a soak measurement; the missing deterministic release is visible in source.

### P2-03 — UIA and file effects run synchronously under the coordinator lock with no timeout

- **Source proof:** `DesktopTools.ExecuteAsync` performs the complete switch
  synchronously before returning its `Task` (`DesktopTools.cs:48-94`).
  `RequestCoordinator.TryCommit` holds `_gate` while running the supplied effect
  (`RequestCoordinator.cs:45-60`), including UIA `Invoke`/`SetValue` and file
  staging/readback. There is no action timeout or cancellation path while those
  native calls are blocked.
- **Scenario/consequence:** A hung UIA provider or slow/unavailable selected
  file root blocks the caller and prevents `Stop` from acquiring the coordinator
  lock. The emergency stop cannot interrupt the operation, and the UI cannot
  produce the required bounded partial/stopped outcome.
- **Smallest repair:** Reserve/validate the commit under the coordinator, run
  potentially blocking native/file work on a bounded worker with an explicit
  timeout, and let Stop invalidate the reservation without holding `_gate`
  across the external call. Report any effect that crossed the commit boundary
  as partial.

### P2-04 — Approval is not bound to target/display observation identity

- **Source proof:** `ActionPolicy.ComputeDigest` includes only tool name, request
  target, generation, and sorted arguments (`ActionPolicy.cs:97-113`).
  `ApprovalTicket` has only generation/digest (`src/Boo.Core/Contracts.cs:20`),
  and `TargetObserver` has no monotonic observation token. `DesktopTools` can
  therefore evaluate the same ticket after a target/display observation has
  changed, provided the HWND/process/bounds happen to match again.
- **Scenario/consequence:** Approve an action for target A, switch to another
  foreground/display, then return to A with the same HWND/bounds. The old ticket
  still evaluates as allowed, contrary to NAT-016's requirement that any target,
  observation, or display change requires fresh confirmation.
- **Smallest repair:** Include an immutable target identity, display ID, and
  observer-generation token in the approval material and advance that token on
  every external foreground/geometry/display transition. Reject the ticket if
  any component differs, even if the HWND later returns to the same bounds.

## Scope boundary

These are source defects and actionable hypotheses derived from the reviewed
paths. No claim is made here about actual WGC frame content, WinRT ABI success,
display-affinity effectiveness, or native leak magnitude; those require the
exact packaged build and native evidence. Repeated D3D-device creation,
annotation exclusion failure, and the microphone upload backlog were explicitly
left to the separate repair/measurement work.
