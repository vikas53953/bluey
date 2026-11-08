# Boo project status

Updated 8 November 2026 (video-parity build). Phase: **built and Windows-CI-tested; native hands-on QA pending**. Delivery is **not accepted** until Vikas runs the three demos + a 25-minute focus session on his PC.

Source of truth is now GitHub `vikas53953/bluey`. Every push runs `windows-ci` on windows-latest (build, Core + Integration tests, publish win-x64).

## 8 Nov additions (plan: docs/plans/2026-11-08-feat-boo-video-parity-plan.md)

Each lane: independent test author froze failing tests first, a separate builder implemented, a third agent reviewed. Core tests: 555 pass, 1 pre-existing skip.

| Lane | What it adds | Evidence | Not yet proven by hand on Windows |
| --- | --- | --- | --- |
| Hold-to-talk (U4) | Hold Ctrl+Alt+Space or hold the eyes; release sends; tap <200 ms shows "Hold to talk"; push-to-talk Realtime turns; latency recorder (median / p95) | main, Windows CI green; review fixed 5 bugs (Alt menu-bar leak, AltGr, lock-screen stuck keys, re-hold race) | Real mic round trip, 20-turn latency target, elevated windows |
| Chirp, start-with-Windows, QA capture switch | 880->1320 Hz chirp on nudge and session end (toggle, on by default); HKCU Run entry (off by default); BOO_QA_CAPTURABLE=1 makes Boo screenshot-able | main, Windows CI green | Audible chirp, registry write |
| Pointer (U3, U6) | Blue pointer flies to the target, then ring/arrow draws; nudge pointer via Windows OCR | PR #1, Windows CI green; review fixed stale-generation bug | Landing accuracy at 100/150 % and second monitor |
| Eyes and notch (U1, U2) | Face B "Button eyes" in HeyClicky pocket, data-driven from docs/design/boo-notch-spec.json; gaze follows cursor; blinks; listening/thinking/responding/capture/nudge/paused/happy faces; hover 240 ms reveal, 900+160 ms retract; Nunito font | PR #3 (includes #1), Windows CI green; review + integration review fixed 9 bugs | Look and feel, hover timing, idle CPU, DPI |

| Installer and updates (8 Nov night, branch claude/project-thread-e9urgv) | Velopack per-user installer `Boo-win-Setup.exe` (Start menu, Settings > Apps uninstall, `%LocalAppData%\Boo`); Settings > Updates opt-in check of GitHub Releases on the private repo with a DPAPI-protected read token, 24 h interval, manual check, explicit **Install and restart**; `releases.win.json` hash verified in CI and by Velopack on the PC; decisions in `Boo.Core/Updates.cs` (105 test cases) | release workflow uploads `boo-installer-win-x64`; `vpk pack` verified end to end on the build container (91 MB Setup.exe); whole app compiles | Install, Start menu shortcut, uninstall, and a real update round trip on Windows; SmartScreen prompt (unsigned) |

### Accessibility pass (8 Nov night, branch claude/project-thread-syqxnu, off lane/face c2cdcc2)

Screen-reader structure (UIA groups + heading levels), transcript as a navigable list, assertive approval announcement, F6 region cycling, visible focus rings, Windows Text size scaling, and "Animation effects off" now also stopping the pointer flight. Visual design unchanged. Proven in CI by UI smoke steps 11-14 and 23 Core tests; the human checks (Narrator, High contrast, 150/225 % text, animations off) are listed in [the accessibility checklist](verification/2026-11-08-accessibility-native-checklist.md).

### Hardening pass (8 Nov evening, branch claude/project-thread-2kwt3c)

Edge cases found and fixed, each with new tests (Core 1028 pass / 1 skip; Integration 119+; UI smoke 9 on windows-latest):

| Area | Fix | Still needs a hands-on check |
| --- | --- | --- |
| Focus sleep/resume, drift | FocusClock measures real time; a >30 s gap (sleep without a Suspend event) pauses with "Paused after sleep"; Running sessions saved every 30 s and recovered as Paused after a crash | Real sleep / modern standby |
| Crash handling | Redacted crash logs in <data>/logs (keys, Bearer tokens, profile path removed), StopAll on crash, recoverable UI errors keep Boo alive (max 5/min), startup errors stay fatal, one-time "Boo recovered" notice | A real crash |
| Hotkey conflicts | One truthful message when Ctrl+Alt+B, Ctrl+Alt+F12 or the Ctrl+Alt+Space hook fail; shown in Settings, status and tray; tray labels drop dead shortcuts; retried on unlock/display change | Another app owning the combo |
| OpenAI / network | 401, quota, rate limit, 5xx, offline and timeout get distinct plain messages; missing key says so; Realtime connect has a 15 s timeout and keep-alive detects dead sockets so voice never sticks on Listening/Thinking | Live OpenAI errors |
| Microphone | No mic, privacy switch off, in use, unplugged each get their own message; Listening never shows when the mic did not start | Real privacy switch, unplug |
| Multi-monitor / DPI | Each monitor's own DPI used for pointer, ring, overlay and panel; overlay placed in physical pixels; notch re-centres when its monitor is unplugged and sits below a top taskbar | 100% + 150% two-monitor setup |
| Corrupt settings | Corrupt settings.json no longer crashes startup (defaults + message) | - |

Native checklists for each lane: docs/verification/2026-11-08-*-native-checklist.md.

### Provider abstraction (8 Nov, plan: docs/plans/2026-11-08-multi-provider.md)

| Area | What it adds | Evidence | Not yet proven by hand on Windows |
| --- | --- | --- | --- |
| Text AI provider choice | `IScreenAssistant` / `IFocusClassifier` behind `AssistantProviders`; OpenAI stays the default; Anthropic Claude and Google Gemini selectable in Settings > Text AI for screen explain, research, nudges and focus classification, with editable model ids; one DPAPI key file per provider; voice stays on OpenAI Realtime; "No {vendor} key yet" status per provider | Core + Providers build on Linux; Claude and Gemini clients are source-tested with mocked HTTP | Neither Claude nor Gemini has been exercised natively against the live APIs; the Settings UI (provider combo, model boxes, two new key boxes) has not been opened on Windows |

---

## Earlier status (7 Nov, Codex build 15)

Updated 7 November 2026. Phase: **integration and native Windows QA**. Delivery is **not accepted**.

The app exists and has been packaged locally. It is not yet a reliably demonstrated finished assistant. No routine owner approval is pending; autonomous implementation remains authorized.

Candidate15 is open in the normal Windows profile after the final presentation check, with status Ready, screen sharing off and microphone idle. Open it later from `artifacts/candidate15/Boo.exe`, then use Ctrl+Alt+B.

| Capability | Implemented | Proven on the running Windows app | Remaining |
| --- | --- | --- | --- |
| Top-center launcher and app shell | Eyes-only hover-hidden notch, saved colors, full resizable window | Candidate15 hotkey opening and truthful color save/reject/reset; candidate14 restart persistence | Hover, visual colors, resizing, tray, display and accessibility acceptance |
| Screen explanation | Capture, cloud request, response pipeline | Real AI screen explanations observed | Drawing correctness, DPI and multi-monitor acceptance |
| Requested desktop actions | Target observation, immutable preview and execution | Candidate13 changed a synthetic app field after approval; independent readback matched | Complete Apply/Undo demo and stale-target end-to-end proof; no universal Undo feature is claimed |
| Voice conversation | Microphone, realtime provider, speech/text, interruption | Complete native flow not accepted | Voice round trip, interruption, accessibility and latency |
| Research and file tasks | Provider research and bounded tools | Complete demo not accepted | Source-backed research and verified task results |
| Focus companion | Sessions, classification, nudges, pauses, history | Deterministic tests for the complete flow | Native session and longer-running checks |
| Packaging | Self-contained Windows build | Candidate14 opened the full app even with corrupt optional history | Complete packaged acceptance remains pending |

## Verified baseline

- Latest checked source: `114e4999fb077312a4bd83acfa71b3ef74b3eb8f`; eyes-only notch, saved colors, full desktop window, bounded failure recovery and truthful appearance feedback are integrated.
- Latest package: `artifacts/Boo-0.1.0-candidate15-win-x64.zip`. Exact identity: [candidate15 package](verification/candidate15-package.md). It is a development candidate, not an accepted release.
- Latest Release build passed. Automated tests: **242 passed, 1 existing host-dependent skip**. Frozen test oracle: revision 17, 44 files. This is source-level evidence, not new native UI acceptance.
- Formatting passes for the three repaired source files. The repository-wide check still fails on existing whitespace in three other files; the global gate is not passed.
- Recent fixes bound concurrent native control reads and defer action-preview keyboard focus until layout. **The intended focus handoff was not observed**. A control experiment also failed to move focus with Tab in the simple fixture app; this is a broader driver/observation limitation, not a confirmed Boo-specific cause. The focus repair remains unaccepted.
- Candidate13 opened the full-window accessibility surface and performed an approved synthetic field edit with independent readback. It did not apply the edited value to the sample button or demonstrate Undo. Those observations belong to candidate13, not automatically to candidate14.
- Candidate14 opened through Ctrl+Alt+B with a corrupt history file, showed a bounded history error, and left the original corrupt file unchanged. Color save, invalid-contrast rejection, reset and restart persistence were observed through accessibility and noncredential file state. This does not certify rendered colors or hover.
- Candidate15 passed all three prewritten appearance-feedback cases through fresh native accessibility state and exact saved-file hashes: reject after success, reset after rejection, and successful save after rejection. Root inspected the retained state evidence. It opened in the normal profile and was left idle for the user.

## Current work

1. Independent review found history-startup, disconnected-monitor, appearance-read and status-disclosure defects. Separate builders repaired them after the new RED regression and native checklist were frozen. Root integration checks pass; a fresh reviewer confirms the source repairs. Existing frozen tests remain unchanged.
2. The contradictory appearance-feedback defect is repaired. A separate author froze the regression, a new builder implemented the fix, a fresh reviewer checked it, and all three exact-candidate15 native retests passed. This closes that bounded repair, not whole-product acceptance.
3. Candidate13 proved the approved field mutation and independent readback. The full Apply/Undo follow-up stopped before a new request because the supported driver encountered a user-input/minimized-window guard. No Boo process was left running. Hover, chrome interaction, high contrast and mixed DPI remain unverified.
4. Finish edit/Undo, voice, drawing, research, focus, accessibility, display and performance acceptance.

## Latest product decisions

- The idle mini contains only eyes: no Boo wordmark and no profile name.
- Default body is black and eyes are white; adjustable colors persist locally.
- Idle mini retracts; hovering its top-center edge reveals it. Clicking opens a full resizable desktop app.
- No profile name is invented or read from the Windows account. Any future profile label belongs only in the opened experience.
- Capture and microphone activity remain visibly disclosed while active.

Separate test authors, builders and reviewers are used. The root integrates and reruns checks. Native QA is serialized because several agents driving the same desktop would invalidate observations.

Evidence: [candidate15 native retest](verification/native-appearance-candidate15.md), [candidate14 history and persistence](verification/native-shell-candidate14.md), [candidate13 action demonstration](verification/native-presence-candidate13.md), [current integration](verification/appearance-feedback-integration.md), [review residuals](verification/review-residuals.md), [plan](plans/2026-11-06-2242-feat-boo-windows-companion-plan.md). Opening instructions are in [the README](../README.md).
