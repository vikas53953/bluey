# Capture activity indicator repair

This is a bounded repair under the accepted Windows companion plan and the
remaining shell-state finding in `docs/verification/review-residuals.md`.
Product choices and acceptance requirements are unchanged. Continue through
CE work under existing unattended authorization; no new approval gate.

## Evidence and outcome

MainWindow sets sharing on only after CaptureAsync returns. RenderReply,
ClearAnnotations, focus completion, and display selection independently set
it off; provider failures can leave it on. These writes cannot represent
overlapping focus checks and screen-help calls.

Show activity before entering native capture, retain it while that operation
may send/use the frame, and remove only that operation when its awaited work
and capture cleanup finish. Stop cancels work; it must not falsely claim
capture is idle while canceled work is still cleaning up. A later request or
focus check remains visible when an older operation ends. Clearing marks and
changing the selected display do not end someone else's activity.

## Public seam and implementation

Add `Boo.Core.CaptureActivityTracker` with `CreateOperation()` returning a
public disposable `CaptureActivityOperation`, and an `ActiveDisplays`
property returning an immutable snapshot of unique DisplayInfo values in
first-active order (ordinal display ID, ordered by each display's oldest
still-active operation, using that operation's display metadata). Each
operation is initially inactive; `Start(DisplayInfo)`
activates it once. Start after Dispose throws ObjectDisposedException; repeat
Start throws InvalidOperationException without replacing its display. Dispose
is idempotent and removes only its own activity. Null display is rejected.
Previously returned snapshots must remain stable. Protect tracker/operation
state consistently against concurrent Start/Dispose/read calls. No Reset or
clear-all API: cleanup ownership must not be bypassed by generation changes.

MainWindow creates one inactive operation per typed request, realtime
screen-help invocation, and focus check. Pass it into the corresponding
capture helper. After prerequisites and target selection succeed, Start it
and refresh the indicator immediately before awaiting CaptureAsync. Keep the
selected display in a local snapshot. Yield the dispatcher at Background
priority after the refresh so pending rendering can run, then recheck the
operation's cancellation/currentness and selected display before invoking
CaptureAsync. This allows the indicator to paint before native setup; exact
visible timing remains a native acceptance check. Keep the
operation in a using/finally spanning capture and provider work; if capture
returns no frame, dispose and refresh before a text-only provider request.
Finally always dispose and refresh, including every early return and failure.
All shell interaction is on its dispatcher. Refresh reads current tracker
state, so an old completion cannot overwrite a newer activity.

Use the displays recorded by active operations for the active indicator,
including multiple displays. Only idle state uses the current selected
display name. Replace boolean indicator writes with refresh-only calls;
remove indicator changes from RenderReply and ClearAnnotations. Do not add
tracker resets to BeginRequest, StopListening, StopAll, focus pause, or display
selection. Cancellation already prevents future work; outstanding activity
ends through its own cleanup. Adjust Stop's claim from capture idle to capture
stopping if activity remains, or use stable wording directing the user to the
capture indicator without claiming idle before cleanup. Keep the accessible text and notch
status callback. No new colors, geometry, provider calls, model, or settings.

The caller in App.xaml.cs must also preserve this ownership: its StopAll
currently overwrites the notch callback with `Stopped` immediately after
panel.StopAll. When the panel exists, let panel.StopAll refresh current
activity and keep that status; only use the standalone `Stopped` fallback
when no panel exists. Review the notch hotkey/tray callback chain as part of
integration. This is required for the collapsed indicator, not just the panel.

## Tests first and independent verification

Independent test author adds public-seam tests before the builder: inactive
creation; start/end; duplicate disposal; two operations on one display; two
displays; old completion after newer operation begins; immutable snapshot;
invalid/repeated/after-disposal Start; concurrent independent operations.
Use a compile-time stub only to run and retain behavioral RED evidence. Root
freezes the added test file before releasing the separate builder. Existing
frozen files remain unchanged. Builder owns Core tracker only in an isolated
worktree. Root owns shell integration; fresh reviewer checks both against
this contract, especially every early return, Stop/replacement and overlap.

Run focused tests, oracle verification, Release build and suite after
integration. Exact packaged QA checks sharing during capture/provider wait
and off after failure/completion or canceled cleanup. Independent model tests
do not prove WPF visibility, physical capture timing, native overlap, or the
teaching demo. No broad or repeated paid testing is implied by this unit.

## Review state

Root confidence: high on the ownership mechanism, native timing remains
unverified. Independent Advisor returned PLAN_APPROVED after source review.
Root clarified stable snapshot ordering and dispatcher rendering opportunity
before releasing the test author; neither changes product scope.
