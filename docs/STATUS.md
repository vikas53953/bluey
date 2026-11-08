# Boo project status

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
