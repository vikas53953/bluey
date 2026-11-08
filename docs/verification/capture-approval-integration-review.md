# Capture and approval integration review

Date: 2026-11-07

Scope: read-only review of the canonical `cf191e7` tree plus integrated
working changes against `docs/plans/2026-11-07-capture-approval-repairs.md`.
Reviewed `ScreenCapture`, `WindowsFrameCaptureBackend`, capture status/error
display, `ActionPreviewBuilder`, `FileActionResolver`, `DesktopTools` file
resolution, and the MainWindow approval card/handlers.

## Result

No actionable defect was found within the requested capture-lifetime,
exclusion/cancellation, or exact-approval-preview scope.

The capture service copies and validates owned handles before waiting, keeps
one complete backend lifetime behind `_lifetimeGate`, starts the deadline only
after acquiring that gate, and awaits backend cleanup before releasing it
(`src/Boo.Windows/ScreenCapture.cs:33-93`). The production backend registers
frame cancellation before `StartCapture`, disposes late frames, checks
cancellation after JPEG readback, and restores every recorded affinity after
WGC cleanup (`src/Boo.Windows/WindowsFrameCaptureBackend.cs:38-121,
124-163, 165-209`). Caller cancellation remains distinct from deadline
limitation in the service.

The approval path builds a copied `FrozenDictionary` request and immutable,
ordinal-sorted fields, resolves file destinations through the same resolver
used by execution, and blocks denied or invalid file actions
(`src/Boo.Core/ActionPreviewBuilder.cs:16-63`). `ShowActionPreview` stores the
copied request/model, renders all fields in read-only controls, exposes the
blocked reason, and disables Approve when blocked. `ApproveButton_OnClick`
checks the stored model's `CanApprove` and reentrancy state, and rechecks
window identity only for window-bound actions
(`src/Boo.App/MainWindow.xaml.cs:612-728`). `ClearActionPreview` clears the
visual model, copied request, authority, target, and chain queue across stop,
voice invalidation, request replacement, and cancellation
(`src/Boo.App/MainWindow.xaml.cs:139-185, 496-530, 1093-1130,
732-754`).

Capture failures now remain visible in both the persistent detail text and
live status text, with stale-generation guards
(`src/Boo.App/MainWindow.xaml.cs:1142-1204`; `src/Boo.App/MainWindow.xaml`:52-63,
251-254). The approval card's bounded ScrollViewer and read-only selectable
values are present in `src/Boo.App/MainWindow.xaml:117-159`.

## Separate residuals and evidence boundary

This source review does not relabel the separately tracked native COM/WinRT
ownership gaps in `CaptureInterop`/`NativeCaptureDevice`, native file-identity
TOCTOU, or target-epoch invalidation as solved. The inspected receipts report
deterministic capture/action tests and Release/oracle checks only; no live WGC,
protected-window exclusion, native GPU cleanup, actual approval UX, or physical
latency acceptance was established here.
