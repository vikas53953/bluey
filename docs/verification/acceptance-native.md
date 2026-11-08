---
title: Boo native packaged-app acceptance matrix
type: verification
date: 2026-11-07
status: test-contract-draft
scope: native packaged Windows app and disposable fixture
---

# Boo native packaged-app acceptance matrix

This is the independent native acceptance oracle for the Boo Windows companion.
It is written before feature implementation and is intended to be frozen by the
root before builders receive a green baseline. Case IDs are stable handles for
evidence; the requirement column always states the user-visible requirement in
plain language so an ID alone is never the oracle.

The matrix covers the packaged Windows application, not the browser preview. A
preview screenshot, a provider mock, a prerecorded voice clip, or a unit test
can support a deterministic row but cannot pass a native/live row. Every row is
one of `PASS`, `FAIL`, `BLOCKED`, or `NOT TESTED`. `BLOCKED` and `NOT TESTED`
never count as acceptance.

## Evidence classes and pass rules

| Class | What it proves | What it cannot prove |
| --- | --- | --- |
| `DET` | Public-contract behavior with deterministic input, a clock, a transport fixture, or the disposable HTML workspace. | HWND ownership, real display capture, appearance, acoustic quality, live provider behavior, or user-perceived latency. |
| `NATIVE` | The exact packaged Windows build running on the test PC, with screenshots, UI Automation output, screen recording, or OS observations. | Live model correctness or speech intelligibility unless the row also says `LIVE`. |
| `LIVE` | A real provider session, real network, real selected microphone/speaker, and a human user speaking where stated. | Provider retention policy, hardware not present, or a simulated voice acting as a real-user pass. |
| `PERF` | A complete sample distribution with all required samples, model/build/network metadata, and raw timestamps. | A single fastest turn or a mock response. |
| `RELEASE` | The exact self-contained package identified by SHA-256 and its controlled local soak. | A different build, branch, source tree, or an unpackaged debug run. |
| `PROCESS` | Test-first ordering, frozen-oracle identity, independent QA, and honest evidence labels. | Product behavior by itself. |

The required environment must be recorded in the evidence bundle: Windows
edition/build, architecture, Boo package version and SHA-256, display topology
and per-monitor scale, foreground fixture process, audio device names, network
state, configured model IDs (never secrets), test-contract revision, and UTC
timestamps. Any missing environment item that could affect the result is a
blocker, not an assumption.

No paid API call, credential, or secret is used while authoring this matrix or
the fixture. Live rows may be run later only after the root/QA owner configures
the provider through protected local storage; evidence must redact keys and
tokens. No UI automation is run in this authoring turn while the root is using
the desktop. No descendant agents are created and no commit is made here.

## Disposable fixture contract

`tests/fixtures/native/Boo.QaWorkspace/` is the preferred deliberately small,
local-only WPF design workspace for native QA. Its standalone
`Boo.QaWorkspace.csproj` targets `net10.0-windows` with `UseWPF=true`, so it
provides a real deterministic target HWND, visible geometry, UI Automation
names, and a visible edit/Undo demonstration without browser navigation.
`tests/fixtures/native/design-workspace/index.html` is a companion local-file
fixture for responsive layout and file-readback checks. Neither fixture is
production code or a live web connector.

Initial state and invariants:

- The WPF target button has AutomationId `design-button`, accessible name
  `Create account`, text `Create account`, background `#2D6CDF`, and foreground
  `#FFFFFF`. The HTML target uses the matching `id="design-button"` contract.
- The edit panel exposes labelled text, background-colour, foreground-colour,
  `Apply edit`, and `Undo` controls. Apply changes all three target properties
  and records one undo snapshot; Undo restores the exact prior values and does
  not change the page layout.
- A successful target click writes `Action observed` to a live status region;
  the test must observe this change rather than infer it from a click call.
- The workspace contains enough vertical content to scroll the target out of
  place and has a responsive layout whose width can be changed by resizing the
  fixture window. The target and its `data-fixture-target` marker (HTML) or
  AutomationId (WPF) remain deterministic at every starting viewport.
- All controls have stable labels and keyboard order so target selection,
  editing, undo, and readback can be checked without private view-model state.

The HTML fixture may be opened from `file://` or a loopback-only server. Both
fixtures must remain offline, load no remote scripts, send no data to a network,
and grant the assistant no real account access. Test evidence names the fixture
revision and the temporary root used for any file-output action.

## Acceptance matrix

### Home, appearance, focus, and target ownership

#### NAT-001 — Boo is a single-PC packaged application

- **User requirement:** The product is named Boo and operates on the same
  Windows PC without a phone, pairing service, or second device.
- **Setup/actions:** Install or unpack the release candidate; launch it cold;
  inspect the visible title, process, tray entry, and package metadata. Do not
  pair a phone or use a browser preview as the application.
- **Expected outcome:** The running product identifies itself as Boo, owns a
  native Windows process/window, and reaches its idle state on the same PC.
  There is no required second-device handshake. Quit terminates the owned
  process and leaves no Boo window or tray process behind.
- **Evidence:** Package manifest/version, SHA-256, process/window inventory
  before and after quit, screenshot of the title/tray identity.
- **Class/environment:** `NATIVE`; Windows 11 x64, packaged candidate, one PC.

#### NAT-002 — Pocket notch, eyes, rounded type, and reference treatment

- **User requirement:** Boo's primary home is a compact top-centre pocket notch
  that expands without displacing work; the selected companion is just eyes
  with warm rounded typography, with the remaining treatment following the
  HeyClicky reference rather than the rejected generic lavender treatment.
- **Setup/actions:** Run the packaged candidate on each available display at
  100%, 150%, and 200% scale where available. Capture compact and expanded
  states while an external fixture remains usable. Compare the actual native
  realization with the approved reference notes; do not use the browser preview
  as proof of native rendering.
- **Expected outcome:** The compact notch is top-centred within the selected
  display work area, stays compact while idle, and expansion does not move or
  cover the fixture's working content. The visible avatar is the selected
  minimal-eyes direction, text uses the chosen warm rounded treatment, and no
  rejected lavender-only visual is presented as the production direction.
  Differences from HeyClicky's exact dimensions/hover behavior are recorded as
  observations, not silently called a match.
- **Evidence:** Native screenshots at every tested scale, display geometry
  record, comparison notes naming the reference source and any unverified
  detail. A screenshot alone cannot prove focus or capture behavior.
- **Class/environment:** `NATIVE`; packaged app, real monitor(s), DPI settings,
  disposable fixture, reference notes.

#### NAT-003 — Tray recovery, stop, settings, and quit

- **User requirement:** A secondary tray menu can show Boo, stop it, open
  settings, and quit; repeated open/close must not orphan windows.
- **Setup/actions:** Collapse and reopen the notch repeatedly; invoke each tray
  command; start a disposable session, use tray Stop, then quit. Repeat after
  the display selection changes.
- **Expected outcome:** Show restores the visible notch, Stop cancels active
  work and leaves a readable stopped state, Settings opens the settings surface,
  and Quit closes all Boo-owned windows/processes. Repetition does not create
  duplicate notches, stale annotations, or surviving child processes.
- **Evidence:** Screen recording or timestamped screenshots, tray command log,
  process/window inventory after each quit.
- **Class/environment:** `NATIVE`; packaged app and Windows tray.

#### NAT-004 — Focus acquisition and return to the external target

- **User requirement:** The compact notch and drawings do not steal typing from
  the user's app; opening controls may take focus, and closing them returns
  focus to the same valid external target. Boo must never infer a target from
  its own window.
- **Setup/actions:** Focus the fixture's text input and type a sentinel string.
  Invoke Boo through the configured hotkey and through a mouse activation while
  the fixture is foreground. Record the fixture HWND/process/display before
  activation. Type in Boo's conversation field, close it, and type a second
  sentinel into the fixture. Repeat once after Boo was foreground, and once
  after closing/reusing the fixture window.
- **Expected outcome:** The first sentinel stays only in the fixture while the
  compact notch/annotations are visible. Opening the interactive panel focuses
  its own control; closing returns focus to the still-valid fixture HWND. A
  closed/reused HWND or a Boo-only foreground observation is rejected and
  results in an explicit target-selection state, not an action against Boo.
- **Evidence:** Before/after HWND and process identity, display ID and scale,
  typed sentinel screenshots, UI Automation focus events, and the readable
  invalid-target state where applicable.
- **Class/environment:** `NATIVE`; packaged app, fixture window, global hotkey,
  at least one real display.

#### NAT-005 — Click-through drawings and actual edit/undo demonstration

- **User requirement:** On an explicit request Boo draws a labelled ring/arrow
  or short mark on the selected control without consuming the control's input;
  the requested demo shows a real edit and a real Undo in the workspace.
- **Setup/actions:** Open the fixture from the disposable fixture contract.
  Request a highlight of `Create account`. Confirm the mark is anchored to the
  current target, then click the underlying button through the drawing. Use the
  fixture's edit controls to change button text and both colours; observe the
  edit; invoke Undo; click the restored button.
- **Expected outcome:** The mark is visible, labelled, and aligned to the
  target, while the underlying click changes the live status to `Action
  observed`. The edit changes all requested properties, and Undo restores the
  exact initial text/colours. Clearing the mark removes only Boo's drawing and
  does not alter the fixture. No click-through event reaches Boo's controls.
- **Evidence:** Before/after screenshots, screen recording of the underlying
  click and edit/Undo, fixture status-region readback, annotation observation ID
  and target bounds.
- **Class/environment:** `NATIVE` plus `DET` fixture oracle; packaged app,
  local fixture, no live provider required if the annotation proposal is
  deterministic. A fixture pass does not prove arbitrary-app targeting.

#### NAT-006 — Scroll, resize, display, and DPI invalidate stale targets

- **User requirement:** An old mark or action cannot survive target movement,
  scrolling, resizing, foreground changes, display changes, or DPI changes;
  Boo must obtain fresh evidence before acting.
- **Setup/actions:** Capture/identify the fixture target and start a delayed
  explanation/action. Before the delayed result arrives, scroll the target,
  resize the fixture, move it to another display, change that display's scale
  where possible, or switch the foreground app. Exercise each change at least
  once and then request a fresh target observation.
- **Expected outcome:** The old mark disappears or is visibly invalidated, and
  the delayed result cannot draw or act against the old bounds/display/scale.
  A fresh observation carries a new generation/target identity and can be used
  only after revalidation. Coordinate transforms remain within the selected
  display's physical bounds at 100%, 150%, and 200% cases.
- **Evidence:** Timestamped target/display/scale/observation records, before and
  after screenshots, rejected late-result log, and a successful fresh-target
  record.
- **Class/environment:** `NATIVE` with `DET` geometry oracle; two displays and
  adjustable DPI are required for full coverage. Missing display/DPI support is
  `BLOCKED`, never simulated as a pass.

#### NAT-007 — Uncertain or inaccessible targeting fails closed

- **User requirement:** If Boo cannot identify a stable, accessible target, it
  explains the limitation without guessing a mark or computer action.
- **Setup/actions:** Use an empty/unstable fixture region, a protected or
  inaccessible target, and a proposal whose observation or display does not
  match the current capture. Ask for an explanation and separately ask for a
  consequential action.
- **Expected outcome:** Boo gives a readable uncertainty/inaccessibility
  explanation, emits no guessed annotation, requests fresh selection where
  appropriate, and performs no action. The user can still type a follow-up.
- **Evidence:** Screen capture of the message and empty annotation layer,
  provider/tool request trace with target rejection reason, and no side-effect
  record.
- **Class/environment:** `DET` for proposal validation; `NATIVE` for the actual
  error surface. No mock success is accepted for a packaged-app claim.

### Screen capture, indicators, privacy, and lifecycle

#### NAT-008 — Capture indicator and Boo-window exclusion

- **User requirement:** A persistent readable indicator names whether screen
  capture is active and which display is selected; Boo's own UI is excluded from
  captured frames where supported, and exclusion failure pauses capture rather
  than leaking recursive overlays.
- **Setup/actions:** Start capture on a named display; collapse the panel while
  the indicator remains visible; inspect frames containing the fixture and Boo.
  Change the selected display. Exercise an injected/observed display-affinity
  failure, then stop capture.
- **Expected outcome:** The indicator remains visible while capture is active,
  including when the panel is collapsed, and names the actual selected display.
  Captured frames contain the intended external content but no Boo-owned notch,
  panel, indicator, or annotation window. If exclusion cannot be confirmed or
  the API fails, Boo pauses capture before sending another frame and explains
  the limitation; it does not claim privacy. The indicator clears only after
  capture has actually stopped.
- **Evidence:** Native screen recording, frame sample hashes/thumbnails with
  Boo regions inspected, display-affinity result, indicator screenshot and
  accessibility name, provider frame count before/after stop/failure.
- **Class/environment:** `NATIVE`/`DET` for state transitions; real Windows
  Graphics Capture and real display required for exclusion. A mock exclusion
  result is not a pass.

#### NAT-009 — Stop, disconnect, lock, sleep, and capture shutdown

- **User requirement:** Stop, provider disconnect, Windows lock, and sleep end
  microphone/capture activity as specified; stopping prevents new frames from
  being sent, and late results cannot draw or act.
- **Setup/actions:** Start a capture/voice turn, then exercise Stop, network
  disconnect, workstation lock, and sleep/resume independently. Inspect frame
  and audio counters after each transition and send a delayed provider result.
- **Expected outcome:** Input/capture ends at each required boundary; no frame
  or audio chunk created after the boundary is sent; late provider output is
  rejected by the new cancellation generation. Unlock/reconnect does not
  silently resume active capture or recording. The UI states exactly whether a
  partial effect occurred.
- **Evidence:** Ordered event trace with monotonic timestamps, frame/audio
  counters, lock/sleep OS event, stopped-state screenshot, and no-side-effect
  assertion.
- **Class/environment:** `DET` for event ordering; `NATIVE` for lock/sleep and
  actual capture. Do not replace Windows lock/sleep with an in-app toggle and
  call it native proof.

#### NAT-010 — Microphone permission, device loss, and network failure

- **User requirement:** Microphone capture starts only after explicit user
  activation and stops on stop/disconnect/lock/sleep; permission or device
  failures are readable, typing remains usable, and speech/chirp muting is
  independent.
- **Setup/actions:** With a real microphone, activate voice and verify the
  listening state. Deny microphone permission, remove/disable the selected
  device, and disconnect the network in separate runs. Toggle speech mute and
  optional chirp mute independently, then type a request.
- **Expected outcome:** No microphone capture begins before activation. Each
  failure shows a readable cause and recovery path, preserves the typed input
  surface, stops active audio/capture on disconnect, and does not reconnect into
  recording automatically. Speech can be muted while text remains, and chirps
  can be muted without disabling speech unless the UI says so.
- **Evidence:** Windows privacy/device state, native error screenshots, audio
  session start/stop trace, typed response, and mute-state evidence.
- **Class/environment:** `NATIVE`; real permission boundary and real selectable
  audio device required. If no microphone/device is available, mark `BLOCKED`;
  a synthetic audio stream cannot pass the real-hardware row.

#### NAT-011 — Live spoken turn has matching readable text

- **User requirement:** A human can activate voice, see a listening state and
  transcript feedback, and receive a spoken explanation whose visible text
  matches it.
- **Setup/actions:** On the packaged candidate with the configured live model,
  a human speaker activates the microphone and says a fixed, ordinary question
  twice in separate turns. Observe listening, committed transcript, visible
  assistant text, and reply audio. Record the actual configured model and
  network conditions.
- **Expected outcome:** Listening is visibly entered only after activation; the
  spoken user turn is represented in the transcript; reply audio plays and the
  visible assistant message is the same response text. For comparison, apply
  Unicode NFC and whitespace collapse only; semantic paraphrase is not
  sufficient. If speech recognition, provider access, or audio output fails,
  show the failure and preserve typing instead of claiming a pass.
- **Evidence:** Screen recording including state transitions, redacted event
  trace, transcript and rendered text, audio-start timestamp, package hash,
  model ID, microphone/speaker identity, and human-speaker attestation.
- **Class/environment:** `LIVE`; packaged candidate, real microphone/speaker,
  real network, valid protected credentials, human speech. Prerecorded or
  synthesized input is not real-user proof.

#### NAT-012 — Typed turn and speech output use the same response text

- **User requirement:** Typing is a first-class input and can receive spoken
  output with matching readable text; voice failure does not remove typing.
- **Setup/actions:** Type a fixed request into the packaged conversation input;
  submit it; observe the rendered assistant text and audio. Repeat with speech
  output muted, then restore speech. If the microphone row is blocked, do not
  convert that block into a voice pass; test only the typed path.
- **Expected outcome:** The typed request remains visible and the assistant
  response text is rendered. With speech enabled, audio is generated from that
  same text under the documented normalization rule; with speech muted, text
  still arrives and no reply audio plays. The input remains usable after a
  provider/audio error.
- **Evidence:** Screen recording, input/output transcript, mute control state,
  audio event trace, and provider/model metadata with secrets redacted.
- **Class/environment:** `LIVE` for real provider/audio; `NATIVE`/`DET` only for
  local mute and state transitions. A fake audio callback cannot pass the live
  row.

#### NAT-013 — User interrupt stops reply audio and late output

- **User requirement:** A visible stop/interrupt path cancels an in-progress
  spoken reply, prevents later drawing/action effects, and reports the stopped
  state.
- **Setup/actions:** Start a real or transport-controlled reply long enough to
  interrupt. Press the visible interrupt/Stop control and separately use the
  global emergency Stop path. Deliver a delayed provider chunk after the stop.
- **Expected outcome:** Audio playback stops, the UI records an interrupted or
  stopped state, no later audio/text/drawing/action from the old generation is
  committed, and the next explicit request starts a new generation. Any effect
  committed before Stop is described as partial and is not falsely undone.
- **Evidence:** Ordered stop/provider/audio trace, screen recording, generation
  IDs, output/frame counts before and after stop, and partial-outcome message if
  applicable.
- **Class/environment:** `DET` for event ordering; `LIVE` for actual spoken
  playback. A simulated interruption does not establish acoustic stop behavior.

#### NAT-014 — Default privacy and protected credentials

- **User requirement:** Credentials stay in Windows-protected local storage;
  screenshots and microphone audio are not saved by default; provider
  processing is explained separately from local retention.
- **Setup/actions:** Configure a disposable credential through the product's
  protected setup, perform a capture/voice turn, end it, and inspect the app's
  designated data directory, logs, crash artifacts, and session records. Do not
  print or copy secret material.
- **Expected outcome:** The credential is stored/retrieved only through the
  protected Windows mechanism and is absent from logs, screenshots, fixture
  files, and plain settings. No raw frame or audio file exists by default.
  Retained session data and the provider-processing disclosure distinguish local
  retention from external provider handling. An invalid/missing credential
  gives setup guidance without deleting local history.
- **Evidence:** Redacted storage inventory, Windows protection metadata, log
  scan results, settings/disclosure screenshot, and retention-policy record.
- **Class/environment:** `NATIVE`/`DET`; disposable local profile, no secret in
  evidence. Provider retention itself is external and is not claimed by this
  test.

### Research, actions, approvals, and cancellation

#### NAT-015 — Real web sources and local-file readback

- **User requirement:** For an explicit research request Boo returns real source
  links; a requested local action reports completion only after the file is
  actually created and read back.
- **Setup/actions:** With real network and protected provider access, ask for a
  bounded onboarding-reference shortlist. Inspect each returned source link.
  Approve a file creation under a fresh user-selected temporary root. Have Boo
  read the resulting file through its bounded file tool before reporting
  success; independently read the file bytes from the same root afterwards.
- **Expected outcome:** Sources are actual accessible web URLs, not invented
  citations or instructions from page content. The action preview names the
  resolved root and file. The file exists inside the selected root, contains
  the approved shortlist, and Boo reports completion only after its own readback
  matches the committed content. Traversal, junction/reparse-point escape, and
  arbitrary shell/URI launches are rejected.
- **Evidence:** Redacted request/response trace with source URLs, approval
  preview, canonical path, file hash/contents, Boo readback, and independent
  filesystem readback. Use a disposable temp root and delete it after evidence
  capture.
- **Class/environment:** `LIVE` web/provider plus `NATIVE` local file action;
  real network, packaged app, disposable directory. A stub source list or
  fixture-only file does not pass the live-source row.

#### NAT-016 — Consequential approval, cancellation, and changed target

- **User requirement:** Consequential actions require a concrete confirmation
  naming the action and target; cancelling does nothing; changing the target or
  arguments invalidates the prior approval.
- **Setup/actions:** Request a file write or bounded fixture action. Inspect the
  pending preview, cancel it, and verify no effect. Start another preview,
  approve the exact action, then change its target path, file contents, or
  foreground target before commit. Finally approve a fresh preview.
- **Expected outcome:** The preview identifies the exact action, target, and
  argument digest. Cancellation leaves no side effect. Any target/argument,
  observation, display, or request-generation change makes the old approval
  visibly invalid and requires a new confirmation. Only the freshly approved
  action can commit.
- **Evidence:** Preview screenshots, approval digest/generation trace, temp-root
  file inventory before/after each branch, and readable invalidation reason.
- **Class/environment:** `DET` policy plus `NATIVE` packaged controls; no real
  account or external recipient is ever used.

#### NAT-017 — Stop-versus-commit race has an honest partial outcome

- **User requirement:** Stopping a task prevents later actions and discards late
  results; a side effect already committed before Stop is reported accurately,
  not pretended to be undone.
- **Setup/actions:** Use a barrier-controlled disposable file action and run both
  orderings: Stop immediately before the commit boundary, and commit immediately
  before Stop. Deliver a late model result in each ordering.
- **Expected outcome:** In the first ordering no file replacement or later
  effect occurs. In the second ordering the committed file remains, the UI
  reports the partial completion, and all later effects are refused. Late
  results from the stopped generation cannot draw, type, click, or write.
- **Evidence:** Serialized event trace with barrier/order marker, generation and
  commit IDs, filesystem hashes, side-effect count, and user-visible outcome.
- **Class/environment:** `DET` core policy with `NATIVE` disposable file action;
  no arbitrary scripts or real accounts.

#### NAT-018 — Elevated/protected target and hostile web content fail closed

- **User requirement:** Boo stays within the bounded Windows action boundary;
  it does not bypass higher-integrity/protected windows or let web text create
  a new authorized task.
- **Setup/actions:** Attempt a disposable action against a higher-integrity or
  protected target where available. Include untrusted page text that asks Boo
  to ignore the user's request and run an unrelated action.
- **Expected outcome:** Boo reports the integrity/protection limitation and
  offers a handoff or cancellation without silent elevation. Web content is
  treated as evidence only; it cannot add an action or approval, and no
  unrelated side effect occurs.
- **Evidence:** Target integrity metadata, readable handoff/error state,
  request/tool trace, and unchanged disposable target/filesystem.
- **Class/environment:** `NATIVE`/`DET`; protected target availability is a
  required environment item for a full pass. If unavailable, mark that branch
  `BLOCKED` rather than using a normal window as a substitute.

### Focus sessions and history

#### NAT-019 — Focus start, timer, pause, resume, and end

- **User requirement:** The user can declare a task and duration, start/pause/
  resume/end a focus session, and see a readable timer without phone pairing.
- **Setup/actions:** Declare a fixture task and duration; start, pause, resume,
  and end the session through the packaged controls. Inspect the timer while
  idle, running, and paused.
- **Expected outcome:** The task and duration are visible, the timer advances
  only while running, pause freezes monitoring and timer state, resume requires
  explicit user action, and end closes the session. No phone or second device is
  requested. Reopening the app does not turn a paused/ended session into a
  running one.
- **Evidence:** Screen recording, timer timestamps, session state trace, and
  process/network inventory showing no pairing dependency.
- **Class/environment:** `NATIVE` with `DET` clock fixture for boundary tests;
  packaged app.

#### NAT-020 — Focus classification, sustained nudge, and no autonomous action

- **User requirement:** Running focus observations classify activity as on task,
  off task, or uncertain; only sustained off-task evidence nudges the user;
  monitoring never clicks, closes apps, or blocks sites.
- **Setup/actions:** Run a labelled disposable focus session. Feed related
  fixture activity, a short unrelated interval, sustained unrelated activity,
  uncertain/malformed observations, and late/out-of-order observations. Keep an
  external app available to confirm no intervention.
- **Expected outcome:** Related activity stays on task; a brief detour does not
  nudge; only the defined sustained evidence span triggers a readable nudge.
  Unknown, failed, stale, or out-of-order evidence resets the off-task run.
  Focus code performs no click, close, keystroke, site block, or action-tool
  invocation, including after a nudge.
- **Evidence:** Observation fixtures and timestamps, classification history,
  nudge screenshot/log, action-tool invocation count (zero), and external-app
  state before/after.
- **Class/environment:** `DET` policy plus `NATIVE` nudge UI; model estimates
  are labelled as estimates, not an accuracy benchmark.

#### NAT-021 — Lock, sleep, pause, end, and reconnect do not auto-resume focus

- **User requirement:** Lock, sleep, pause, and ending stop monitoring; unlock
  or provider reconnection alone never resumes it automatically.
- **Setup/actions:** Start a running fixture session and independently apply
  pause, end, Windows lock/unlock, sleep/resume, provider disconnect/reconnect.
  After each return, observe capture, classification, timer, and session state.
- **Expected outcome:** Monitoring and capture stop at every required boundary.
  Unlock, wake, or network reconnect leaves the session stopped/paused until an
  explicit Resume/Start action. The history marks gaps as pause/unknown rather
  than fabricating observations.
- **Evidence:** OS power/lock events, capture/classification counters, state
  screenshots, and history rows with gap categories.
- **Class/environment:** `NATIVE` for actual lock/sleep; `DET` for state
  transitions. Missing permission to lock/sleep makes the corresponding row
  `BLOCKED`.

#### NAT-022 — History distinguishes evidence, unknown gaps, and pauses

- **User requirement:** Session history records observed on/off-task intervals,
  unknown intervals, and pauses honestly; it does not present periodic checks as
  an exact time audit.
- **Setup/actions:** Complete a disposable session containing on-task samples,
  sustained off-task samples, an unknown/provider gap, a pause, and an explicit
  end. Restart Boo and open history.
- **Expected outcome:** The timeline preserves the categories and boundaries,
  survives restart, and labels estimates/periodic observations as such. Unknown
  and paused intervals are not silently counted as on-task or exact active
  time. Deleting one disposable history entry does not delete another.
- **Evidence:** Redacted database/export inspection through the public history
  surface, before/after screenshots, and restart trace.
- **Class/environment:** `DET` storage fixture plus `NATIVE` history view;
  disposable local profile.

### Accessibility and disclosure

#### NAT-023 — Names, keyboard access, visible focus, and reduced motion

- **User requirement:** Primary controls and status indicators have readable
  accessibility names; keyboard users can perform the core flows; focus is
  visible; reduced-motion preference is respected.
- **Setup/actions:** With Windows accessibility inspection and keyboard only,
  traverse notch expansion, conversation, listening/stop, capture indicator,
  display selector, approve/cancel, focus controls, settings, and tray recovery.
  Enable Windows reduced-motion preference and repeat the transitions.
- **Expected outcome:** Every actionable control has a meaningful name and
  keyboard path, focus is visible and ordered, Escape/Stop has the documented
  result, and indicators expose state text including the selected display.
  Reduced motion removes nonessential animation without hiding state or
  controls. No core action requires a mouse or speech.
- **Evidence:** UI Automation name/control-type/value dump, keyboard-only
  screen recording, focus screenshots, and reduced-motion before/after captures.
- **Class/environment:** `NATIVE`; packaged app, Windows accessibility tools,
  keyboard.

#### NAT-024 — Capability disclosure is honest

- **User requirement:** The retained visual MVP/reference alternatives are
  clearly labelled as simulated or sample capabilities; native acceptance does
  not claim that a browser preview proves live AI, microphone, capture, or file
  control.
- **Setup/actions:** Inspect the preview/reference entry points and the native
  package's help/about/evidence labels where present. Compare the acceptance
  record's live and simulated evidence labels.
- **Expected outcome:** Sample conversation, browser speech, annotations, and
  focus-preview states are visibly disclosed as simulated where they are not
  native/live. Native evidence cites the exact package, provider, hardware, and
  environment instead of presenting a preview screenshot as live proof.
- **Evidence:** Screenshot or text inspection with file paths, package help/about
  capture, and the final evidence index showing the class of every result.
- **Class/environment:** `PROCESS`/`NATIVE`; no provider call is needed.

### Performance and exact release observation

#### PERF-001 — Activation and teaching latency distributions

- **User requirement:** Activation feels responsive and valid first annotations
  arrive within the planned target; correctness takes priority over an
  unverified drawing.
- **Setup/actions:** On one fixed packaged candidate, collect at least 20
  activation samples from the user trigger to visible feedback and at least 20
  teaching requests from completed request to first valid annotation. Use a
  deterministic fixture target for geometry, but use the actual configured
  provider for the live teaching distribution. Record cold/warm status and
  discard no slow sample except with a written reason.
- **Expected outcome:** Report count, min, median, p95, max, every timeout/error,
  model, network, display/DPI, and package hash. Activation p95 is at most 150
  ms. Valid first-annotation p95 is at most 2.5 s, with zero stale/guessed
  annotations counted as success. If fewer than 20 valid samples are available,
  the row is `BLOCKED`/`NOT TESTED`, not a pass.
- **Evidence:** Raw timestamp CSV/JSON, calculation script or reproducible
  command, screenshots of representative states, and environment manifest.
- **Class/environment:** `PERF`/`LIVE`; packaged app, real display, live
  provider for teaching, no simulated voice substitution.

#### PERF-002 — Twenty warm spoken turns plus cold baseline

- **User requirement:** Warm voice turns meet the planned responsiveness target,
  with real spoken input and a spoken reply that remains text-matched.
- **Setup/actions:** Record one separately labelled cold connection. Then collect
  exactly 20 warm turns on the same packaged candidate, with a human speaking a
  fixed set of ordinary requests into the real selected microphone. Measure from
  end-of-speech to first reply audio; record transcript/text match and failures.
- **Expected outcome:** Report all 20 raw samples, median, p95, max, cold
  connection separately, actual model, and network. Warm median is at most 1.5
  s and p95 at most 3 s. Any missing sample, simulated speech, or unmatched
  text prevents a pass even if the summary statistic looks good.
- **Evidence:** Raw turn event log, timing distribution, audio/text-match
  evidence, model/package/network manifest, and human-speaker confirmation.
- **Class/environment:** `PERF`/`LIVE`; real microphone, speaker, network,
  protected credential, human speech. No prerecorded audio or mock provider.

#### RELEASE-001 — Exact packaged candidate and 15-minute local smoke/soak

- **User requirement:** Final native QA runs the three demos and failure paths on
  the exact packaged candidate, not an untracked source/debug build.
- **Setup/actions:** Produce a self-contained release package, compute and
  record its SHA-256 before launch, and run that same artifact for 15 minutes.
  Repeat open/close, start/pause/stop focus, target invalidation, provider
  disconnect, capture stop, and reopen using disposable fixture content. Sample
  crashes/errors, process count, handles, memory, and relevant latency. Hash the
  package again after the soak.
- **Expected outcome:** The before/after hashes are identical; no crash,
  unhandled error, surviving Boo process, orphan window, stale capture, or
  post-stop action remains. The evidence includes distributions and samples,
  not only a final screenshot. Any failure routes back to a named case and
  requires rebuild plus rerun of affected rows and the soak.
- **Evidence:** SHA-256 manifest, package path/version, 15-minute timestamped
  log, process/window inventory, memory/handle samples, error log, and fixture
  cleanup receipt.
- **Class/environment:** `RELEASE`; self-contained packaged Windows app,
  disposable fixture, fixed display/DPI, network state recorded.

#### PROCESS-001 — Acceptance was frozen before feature behavior

- **User requirement:** Independent test authors define public behavior and
  executable acceptance tests before feature implementation; builders cannot
  weaken or skip failures to make a build green.
- **Setup/actions:** Root records the commit and SHA-256 manifest for this
  acceptance document and the accompanying oracle/test files before merging
  feature behavior. Run the baseline against minimal public contracts/stubs.
- **Expected outcome:** The baseline has genuine behavior failures for missing
  behavior, while tool/discovery failures are reported separately. Any proposed
  oracle correction names its reason, increments a revision, and preserves the
  previous result. No production implementation is accepted as a prerequisite
  for the test contract.
- **Evidence:** Frozen commit/hash manifest, baseline command/output, revision
  history, and ownership record showing independent test author versus builder.
- **Class/environment:** `PROCESS`; repository history and test harness.

#### PROCESS-002 — Independent QA reran the exact candidate

- **User requirement:** Final QA is separate from builders and checks functional,
  native Windows, usability, accessibility, privacy, failure, and performance
  behavior on the exact packaged build; failed or untested required cases block
  acceptance.
- **Setup/actions:** A fresh QA owner reads this frozen matrix without builder
  self-certification, verifies the package hash and test-contract hash, and
  executes every applicable case. Record `BLOCKED` for missing hardware,
  credentials, displays, or model access rather than substituting a mock.
- **Expected outcome:** Every required row is `PASS` with evidence, or the
  release is explicitly not accepted. The report names unaddressed coverage,
  external provider limitations, and the exact package/environment.
- **Evidence:** Independent QA report, case-by-case result table, hash
  verification, screenshots/logs, and blocker list.
- **Class/environment:** `PROCESS`/`RELEASE`; fresh QA agent/team, exact package.

## Plain-language requirement coverage

The following map is intentionally prose-first. The parenthetical R-number is
only a traceability aid; the sentence is the acceptance meaning.

| User requirement in plain language | Native acceptance coverage |
| --- | --- |
| Boo is a named Windows application on the same PC and does not require another device. | NAT-001, RELEASE-001 |
| Boo's home is a compact top-centre notch that expands without displacing work, with tray recovery, stop, settings, and quit. | NAT-002, NAT-003 |
| An explicit screen-help request can draw labelled marks anchored to the correct control, while uncertainty produces an explanation without a guessed mark. | NAT-005, NAT-006, NAT-007 |
| Typing or voice can receive spoken explanations with matching visible text, with listening feedback and interruption. | NAT-010 through NAT-013, PERF-002 |
| Microphone capture requires user activation and ends on stop, disconnect, lock, or sleep; speech and chirps can be muted independently. | NAT-009, NAT-010, NAT-013 |
| Research returns source links and requested local actions are reported complete only after the observed result is read back. | NAT-015, NAT-018 |
| Consequential actions name the concrete target for approval; cancellation, changed targets, and Stop prevent later effects. | NAT-016, NAT-017, NAT-018 |
| A persistent indicator states capture status and selected display, and Stop prevents new frames. | NAT-008, NAT-009, NAT-023 |
| Credentials are protected locally, raw screenshots/audio are not saved by default, and provider processing is distinguished from local retention. | NAT-014, NAT-024 |
| A user can declare a focus task and duration, start/pause/resume/end it, and read the timer without phone pairing. | NAT-001, NAT-019 |
| Focus observations are on task/off task/uncertain; nudges require sustained evidence and never click, close, or block the user's apps. | NAT-020 |
| History distinguishes observed intervals, unknown gaps, and pauses and does not claim an exact time audit. | NAT-022 |
| Lock, sleep, ending, or pausing stops monitoring, and unlock/reconnect does not auto-resume it. | NAT-009, NAT-021 |
| The visual direction and preview alternatives are reference-grounded and simulated capabilities are clearly labelled. | NAT-002, NAT-024 |
| The build proceeds under the user's autonomous authorization while application-level consequential-action confirmation remains mandatory. | PROCESS-001, NAT-016 |
| Independent tests are recorded before feature implementation and are not weakened to make a build green. | PROCESS-001 |
| Separate final QA reruns the frozen cases on the exact packaged candidate, and any failed or untested required row prevents acceptance. | PROCESS-002, RELEASE-001 |

## Coverage that is deliberately not claimed by this matrix

- No package has been accepted by this authoring turn. Package version, exact
  SHA-256, native screenshots, live model access, display topology, DPI matrix,
  microphone, speaker, network, and 15-minute soak evidence remain pending.
- A fixture, transport stub, synthetic audio stream, browser speech API,
  prerecorded clip, or scripted provider reply cannot establish real-user
  speech, intelligibility, acoustic echo behavior, native capture exclusion,
  provider correctness, source freshness, or latency.
- The exact HeyClicky window could not be treated as captured reference proof;
  native coexistence/occlusion must be observed on the QA machine and reported
  as unverified if unavailable. The selected pocket notch, eyes, and rounded
  type are settled design direction, not evidence of implementation.
- Secure desktop, elevated-window, protected-content, multi-monitor, and
  100/150/200% DPI branches require the stated hardware/OS conditions. Missing
  conditions are `BLOCKED`; they are never replaced by a normal-window or
  single-scale simulation.
- Provider-side retention, billing/entitlement, live web-source correctness,
  and external account effects remain outside local fixture proof. No test sends
  a message to a real person, changes a real account, or uses arbitrary shell
  execution.
- Public distribution, auto-update, wake-word/continuous recording, phone
  pairing, antivirus bypass, silent elevation, and unrestricted “any task”
  automation are not acceptance claims for this delivery.

## Authoring result

Changed files are limited to:

- `docs/verification/acceptance-native.md` — this frozen native acceptance
  matrix, evidence rules, requirement map, and unaddressed-coverage record.
- `tests/fixtures/native/Boo.QaWorkspace/Boo.QaWorkspace.csproj`, `App.xaml`,
  `App.xaml.cs`, `MainWindow.xaml`, and `MainWindow.xaml.cs` — disposable WPF
  fixture with a deterministic target HWND, accessible names, real edit/Undo
  callbacks, and click/readback status. It contains no production behavior,
  network dependency, credentials, or external scripts.
- `tests/fixtures/native/design-workspace/index.html` — disposable local HTML
  fixture for responsive geometry and local-file readback. It contains no
  production behavior, network dependency, credentials, or external scripts.

No production files, project manifests, credentials, other test paths, UI
automation runs, commits, or paid API calls were made by this author.
