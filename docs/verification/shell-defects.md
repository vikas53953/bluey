# Boo native shell defect review

Review basis: canonical `HEAD 7ef0e07cd4222a2933acb2da1ec53e12acf67d45`.
This is a read-only source-path review; no native or live-provider behavior is
claimed as tested. Existing unit/provider suites do not exercise these WPF
window paths.

## P1

### P1-01 — Annotation marks are not invalidated when the target moves

- **Evidence:** `src/Boo.App/MainWindow.xaml.cs:542-562` validates a reply only
  against the original `CapturedFrame.Context`; it never asks
  `TargetObserver` whether the target/window geometry is still current before
  showing the mark. `src/Boo.App/AnnotationWindow.xaml.cs:72-135` renders fixed
  coordinates and has no foreground, resize, scroll, display, or target-change
  invalidation path. `ClearAnnotations` is only called by selected lifecycle
  actions (`MainWindow.xaml.cs:1047-1054,1412-1420`).
- **Scenario/consequence:** Show a mark, then scroll a child control, resize or
  move the target, switch foreground apps, or wait while the overlay remains
  open. The mark stays over old screen coordinates and can point at the wrong
  control; this violates NAT-006/KTD2 even though the observation itself was
  valid when captured.
- **Smallest repair:** Bind the overlay to the observation/target generation
  and clear it on target/foreground/geometry/display changes. At minimum,
  re-check `TargetObserver.IsCurrent(frame.Context.Target)` immediately before
  rendering and re-observe the target/element geometry (not only the top-level
  HWND) while visible; a fresh capture must be required after invalidation.

### P1-02 — Capture indicator is wrong on active capture and provider failure

- **Evidence:** `CaptureForRequestAsync` sets `Screen sharing on` only after
  `CaptureAsync` has completed (`src/Boo.App/MainWindow.xaml.cs:1081-1097`), so
  the indicator is off during the actual capture. It is cleared on capture
  exceptions (`1103-1107`) and successful `RenderReply` (`488-501`), but the
  outer provider error path (`421-424`) and realtime tool error path
  (`1305-1313`) do not clear it. Any late request can also call the same status
  callback after a newer request has started.
- **Scenario/consequence:** A frame is captured and then the provider fails (or
  a request is superseded). The notch can continue saying `Screen sharing on`
  indefinitely, or report off while capture is underway. Users cannot trust
  the required NAT-008/KTD3 privacy disclosure.
- **Smallest repair:** Make capture/send status a generation-owned state
  machine (`Starting`, `Sharing`, `Paused/Error`, `Stopped`) and finalize it in
  `finally` on every current request path. Ignore stale generations when
  updating the panel/notch; clear only after capture/send has actually stopped.

### P1-03 — Approval preview omits the exact arguments and resolved target

- **Evidence:** `RenderReply` builds the approval text as only
  `action.Name` and `action.Target` (`src/Boo.App/MainWindow.xaml.cs:503-525`).
  It does not render `action.Arguments`, the argument digest, or the resolved
  file-root/canonical path, although the digest is what
  `ActionPolicy.Approve` binds (`src/Boo.Core/ActionPolicy.cs:67-81`).
- **Scenario/consequence:** A model proposes `write_file`, `delete_file`,
  `type_text`, or `click` with hidden content, overwrite, path, coordinates, or
  automation ID. The user sees a generic target and can approve a materially
  different action than the one required by NAT-015/NAT-016/KTD6.
- **Smallest repair:** Render a stable, redacted-but-complete argument summary
  and digest before approval; for file actions show the selected root and
  canonical relative target plus overwrite/content details. Keep the existing
  generation/target/digest checks and invalidate the preview if any displayed
  value changes.

### P1-04 — Click effects are always reported unverified, so real edit chains stop

- **Evidence:** Both successful branches in `DesktopTools.Click` return
  `ToolOutcome(false, true, ...)` (`src/Boo.Windows/DesktopTools.cs:224-258`):
  UI Automation invocation has no readback, and SendInput reports no post-click
  state. `MainWindow.ApproveButton_OnClick` advances queued steps only when
  `outcome.Success && outcome.Committed` (`src/Boo.App/MainWindow.xaml.cs:623-637`).
- **Scenario/consequence:** A requested fixture edit that must click `Apply
  edit` (and then `Undo`) can issue the click but Boo reports `Not completed`
  and drops the remaining step. The requested real edit/Undo demonstration and
  NAT-005/NAT-017 verified-completion contract cannot pass; users receive an
  honest failure only after a possible partial effect.
- **Smallest repair:** Add a bounded post-click observation/readback (for the
  fixture, the status region or target property) and return `Success=true` only
  when it matches the approved expected outcome. Keep click effects marked
  partial/unverified and stop the chain when no such observation is available.

### P1-05 — External target priming is hard-coded to the primary display

- **Evidence:** `MainWindow.PrimeExternalTarget` always snapshots a synthetic
  `PrimaryScreen` display (`src/Boo.App/MainWindow.xaml.cs:113-126`) and is
  called before the panel is shown/loaded (`src/Boo.App/App.xaml.cs:48-62`).
  `TargetObserver.Snapshot` rejects a retained target when the requested
  display ID differs (`src/Boo.Windows/TargetObserver.cs:106-114`).
- **Scenario/consequence:** Open Boo while the external app is on a secondary
  display, then select that display. The retained target was primed under the
  primary ID; with Boo foreground, the later secondary snapshot has no valid
  retained target and capture/annotation fails closed. The selected-display
  native flow is therefore broken before any provider call.
- **Smallest repair:** Prime from the actual retained foreground HWND and its
  monitor (or defer priming until displays/settings are loaded and use the
  selected display), rather than manufacturing a primary-display snapshot.

## P2

### P2-01 — Realtime transcript duplicates committed input and can merge turns

- **Evidence:** Input transcription deltas append/extend a `You` line at
  `src/Boo.App/MainWindow.xaml.cs:1206-1208`; the corresponding completed
  transcript then unconditionally appends the full text as another line at
  `1209-1214`. Output audio-transcript and output-text deltas are both sent to
  the same speaker-only `AppendStreaming` path (`1229-1232,1395-1409`), with no
  item/turn ID.
- **Scenario/consequence:** A normal voice turn displays partial text followed
  by the same committed sentence, and provider output channels can be
  concatenated or duplicated. The visible transcript no longer matches the
  spoken turn required by NAT-011/NAT-012.
- **Smallest repair:** Track the realtime item/response ID and an in-progress
  line; replace the delta line on `completed/done` instead of appending, and
  deduplicate audio-transcript versus text output for the same response.

### P2-02 — Research links and status can remain from an older request

- **Evidence:** `BeginRequest` clears annotations and action state but not
  `SourceLinksList` (`src/Boo.App/MainWindow.xaml.cs:1031-1054`). Sources are
  cleared only inside successful `RenderReply` (`488-496`), while the provider
  error path (`421-424`) leaves the old links visible.
- **Scenario/consequence:** Complete a research request, then send another
  request that fails or is superseded. The prior URLs remain under the current
  conversation and can be mistaken for citations for the new request, contrary
  to NAT-015's source-preservation/readability boundary.
- **Smallest repair:** Clear or generation-tag the source collection at request
  start and on current-request failure; render links as part of the response
  record they belong to rather than as a single global stack.

### P2-03 — Selected display scale is recorded as 1.0 and positioning uses the wrong monitor DPI

- **Evidence:** `LoadDisplays` creates every `DisplayInfo` with `Scale: 1d`
  (`src/Boo.App/MainWindow.xaml.cs:273-286`), despite the app declaring
  PerMonitorV2. `PositionNearDisplay` converts physical screen coordinates
  using the panel window's current DPI (`312-332`), and `NotchWindow` does the
  same with its pre-move window DPI (`src/Boo.App/NotchWindow.xaml.cs:71-76`).
- **Scenario/consequence:** At 150%/200% or after moving between differently
  scaled displays, context metadata reports a false scale and the notch/panel
  can be offset or mis-sized. This undermines NAT-002/NAT-006's multi-DPI
  geometry evidence and makes overlay alignment untrustworthy.
- **Smallest repair:** Enumerate each monitor's effective DPI when constructing
  `DisplayInfo`; position after the window receives the destination monitor's
  DPI (or use the destination monitor's transform directly), then invalidate
  marks/context on a DPI change.

