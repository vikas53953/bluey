---
title: Boo ci lane native QA checklist (chirp, start-with-Windows, QA capture switch)
type: verification
date: 2026-11-08
status: test-contract-draft
scope: native Windows build; plan units U0 (QA capture switch), U6 (chirp), U7 (start-with-Windows)
---

# ci lane native QA checklist

Core behaviour is frozen in `tests/Boo.Core.Tests` (`ChirpSynthTests`, `ChirpPolicyTests`,
`StartupRegistrationTests`, `CaptureExclusionPolicyTests`, `SettingsDefaultsTests`). Those are
`DET` evidence only. Every row below needs `NATIVE` evidence on a real Windows PC running the
exact build under test. Mark each row `PASS`, `FAIL`, `BLOCKED` or `NOT TESTED`; only `PASS`
counts. Record Windows build, Boo build/commit, audio output device, and UTC time.

## Chirp (U6) — spec: 880 Hz then 1320 Hz sine, 90 ms each, 40 ms gap, gain 0.12, default ON

| ID | Steps | Expected | Evidence |
| --- | --- | --- | --- |
| CH-1 | Fresh settings (delete settings file). Open Settings > Sound. | Chirps toggle is ON by default. | Screenshot of settings. |
| CH-2 | Start a focus session, go off-task until a nudge fires. | One short rising two-note chirp plays together with the nudge bubble; no click/pop at start or end. | Screen recording with system audio. |
| CH-3 | Let a focus session end (happy face). | The same rising chirp plays once at session end. | Screen recording with system audio. |
| CH-4 | Turn chirps OFF, repeat CH-2 and CH-3. | Nudge and session end appear with no sound. | Screen recording with system audio. |
| CH-5 | Hold-to-talk (microphone listening) at the moment a nudge would fire. | No chirp plays while listening. | Recording. |
| CH-6 | Trigger two chirp events less than 2 s apart (e.g. nudge then end). | Only one chirp is heard. | Recording. |
| CH-7 | Restart Boo after CH-4. | Chirps setting is still OFF (persisted). | Screenshot. |

## Start with Windows (U7) — default OFF

| ID | Steps | Expected | Evidence |
| --- | --- | --- | --- |
| SW-1 | Fresh settings. Open settings. | "Start with Windows" is OFF; `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v Boo` reports value not found. | Screenshot + reg output. |
| SW-2 | Turn it ON. | Run value `Boo` = `"<full path to Boo.exe>" --background` (quoted even with spaces). | reg output. |
| SW-3 | Toggle ON again / restart Boo with it ON. | Value unchanged, no duplicate values. | reg output. |
| SW-4 | Sign out and back in (or reboot). | Boo starts in background (notch only, no main window stealing focus). | Screenshot after login. |
| SW-5 | Turn it OFF. | Run value `Boo` removed; other Run values untouched. | reg output before/after. |
| SW-6 | Install/run Boo from a path containing spaces, enable. | Value quoted; login start works. | reg output + screenshot. |

## QA capture switch (U0) — BOO_QA_CAPTURABLE

| ID | Steps | Expected | Evidence |
| --- | --- | --- | --- |
| QA-1 | Launch Boo normally (variable unset). Take a screenshot with Snipping Tool / PrintScreen. | Boo windows (notch, main, annotation) are absent/black in the capture (WDA_EXCLUDEFROMCAPTURE). No QA warning shown. | Screenshot. |
| QA-2 | `set BOO_QA_CAPTURABLE=1` then launch Boo from that console. Take a screenshot. | Boo windows are visible in the capture. | Screenshot. |
| QA-3 | Same session as QA-2. | A visible QA-mode warning is shown in Boo's UI saying it is capturable. | Screenshot showing the warning. |
| QA-4 | Launch with `BOO_QA_CAPTURABLE=0`, `true`, or empty. | Behaves like QA-1 (excluded, no warning). | Screenshot. |
| QA-5 | QA-2 session: run a focus session / Boo's own screen capture. | Document whether Boo's own captures now include Boo; this is expected in QA mode only. | Note + capture sample. |

Anything only compiled in the Linux container is "compiled, not verified on Windows".
