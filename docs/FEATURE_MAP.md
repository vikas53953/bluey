# Boo feature map

Every Boo feature, how it is proven, and the exact command, workflow or test that proves it. Owned by the
`verify-boo` skill (`.claude/skills/verify-boo/SKILL.md`). Keep it true: when a feature is added or changed, its row
changes in the same pull request, and `pwsh .claude/skills/verify-boo/scripts/Test-FeatureMap.ps1` must pass.

## What the labels mean

| Label | Meaning |
| --- | --- |
| **automated live** | The real published `Boo.exe`, driven by FlaUI like a user, with nothing faked. Where the feature uses AI, a real provider answers with a real key. One test switch is always on: `BOO_QA_CAPTURABLE=1`, so Boo's own windows can be photographed. It also means Boo's windows can appear in what the AI sees, which a normal user's Boo hides. The live journeys hide Boo's panel before the test checks any pixels, but treat "live" as "live in QA mode"; the normal-mode difference is a manual check (runbook 4.1). |
| **automated mock** | Automated, but something is stood in for: the AI answers come from `tools/Boo.QaMockAi` (demo-ci), or the code is tested without the app (Core / Integration / Providers tests with fake HTTP). Proves Boo's logic and UI, not the real AI. |
| **manual** | Only a person at a real PC can prove it today. The checklist step is named. A manual row is proven only by a dated, filled-in checklist result. |
| **UNVERIFIED** | Nothing proves it yet. Do not call it done. |

A feature can carry two labels (for example live for the main path and mock for the edge cases). The **best** label is
listed first. "Proven" always needs a run link or a filled-in checklist; a label on this page is a method, not a pass.

## Where the proofs run

| Short name | What runs | How to start it |
| --- | --- | --- |
| **live journeys** | `tools/Boo.VerifyLive` (7 ordered steps, real key) | `gh workflow run live-ai-smoke.yml --ref <branch> -f text_provider=openai -f runner=windows-latest -f journeys=true`, or locally `pwsh .claude/skills/verify-boo/scripts/Invoke-VerifyBoo.ps1 -Suite live` with `BOO_LIVE_OPENAI_KEY` set |
| **live smoke** | `tools/Boo.LiveSmoke` (one real round trip) | the same `live-ai-smoke` workflow (always runs), or `Invoke-VerifyBoo.ps1 -Suite live` |
| **ui smoke** | `tests/Boo.Ui.Tests` (no AI, no key) | `windows-ci` on every push, or `Invoke-VerifyBoo.ps1 -Suite ui` |
| **perf** | `tests/Boo.Perf.Tests` | `windows-ci` on every push |
| **demo-ci** | `tests/Boo.Demo.Tests` against the mock AI | `demo-ci` on every push (blocking) |
| **unit** | `tests/Boo.Core.Tests`, `tests/Boo.Integration.Tests`, `tests/Boo.Providers.Tests` | `windows-ci` on every push |
| **runbook** | `docs/verification/2026-11-08-acceptance-runbook.md` | a person, on a real PC |

## The map

### Notch and presence

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| N1 | Notch wakes on **hover** at the top-centre strip, retracts after the mouse leaves | automated live | `tools/Boo.VerifyLive/LiveJourneyTests.cs::J1_NotchWakes_OnHover` (live journeys); `tests/Boo.Ui.Tests/BooSmokeTests.U8.cs::Notch_HoverStrip_RevealsAndRetracts` (ui smoke) |
| N2 | **Ctrl+Alt+B** opens (and re-opens) the full window | automated live | `tools/Boo.VerifyLive/LiveJourneyTests.cs::J0_NotchWakes_OnHotkey`; `tests/Boo.Ui.Tests/BooSmokeTests.cs::OpenShortcut_CtrlAltB_OpensCompanionWindow` |
| N3 | Notch is screenshot-able only in QA mode (`BOO_QA_CAPTURABLE=1`); otherwise Boo excludes itself from capture | automated live (QA mode) + manual (normal mode) | `tests/Boo.Ui.Tests/BooSmokeTests.cs::Notch_Screenshot_IsCapturedForEvidence`; `tests/Boo.Core.Tests/CaptureExclusionPolicyTests.cs`; normal mode: `docs/USER_GUIDE.md` section 12 ("Screenshots of Boo are black"), checked by a person |
| N4 | Face states (idle, listening, thinking, responding, capture, nudge, paused, happy) render | automated mock | `windows-ci` step "Notch face gallery" (`Boo.exe --notch-gallery`, 10 PNGs); `tests/Boo.Core.Tests/FaceStateResolverTests.cs`; `tests/Boo.Core.Tests/FaceSpecTests.cs` |
| N5 | Eyes follow the cursor and blink every 3 to 6 s | automated mock + manual | `tests/Boo.Core.Tests/GazeTests.cs`; `tests/Boo.Core.Tests/BlinkSchedulerTests.cs`; runbook 2.2, 2.3 |
| N6 | Click the eyes opens the full window | manual | runbook 2.5 |
| N7 | Right-click the notch acts as Stop | manual | runbook 2.6 |
| N8 | Tray icon menu: Show Boo, Stop, Settings, Quit | manual | runbook 2.6 |
| N9 | Notch placement across monitors, DPI, top taskbar, unplugged monitor | automated mock + manual | `tests/Boo.Core.Tests/MonitorLayoutTests.cs`; `tests/Boo.Core.Tests/NotchLayoutTests.cs`; `docs/verification/2026-11-08-face-native-checklist.md` |
| N10 | Appearance: body and eye colours, offset; 3:1 contrast rule; reset | automated live | `tests/Boo.Ui.Tests/BooSmokeTests.cs::Appearance_SaveRejectReset_PersistsTruthfully` |
| N11 | One copy at a time (a second Boo.exe exits) | manual | runbook 0.1 |
| N12 | Living ink notch (PR #12): ink-blot pocket, cream eyes, moods (listen / think / speak / sleep / alert / happy) that morph shape, hover timings unchanged, reduced motion jumps to the end pose, a quiet rim in Windows dark mode | automated mock + manual | `tests/Boo.Core.Tests/LivingInkTests.cs`; `windows-ci` step "Notch face gallery"; look and feel on a real PC is manual (`docs/verification/2026-11-08-face-native-checklist.md`) |

### Talking and typing

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| T1 | **Typed chat** gets a real AI answer in Conversation | automated live | `tools/Boo.VerifyLive/LiveJourneyTests.cs::J2_TypedChat_GetsARealAnswer` (asks the capital of France, expects "Paris"); `tools/Boo.LiveSmoke/LiveRoundTripTests.cs::TypedQuestion_GetsAnAnswerFromTheRealProvider` (live smoke) |
| T2 | Without a key, Ask and Talk fail honestly and the mic stays off | automated live | `tests/Boo.Ui.Tests/BooSmokeTests.Hardening.cs::AskAndTalk_WithoutApiKey_FailHonestlyAndMicrophoneStaysOff` |
| T3 | Hold **Ctrl+Alt+Space** to talk; release sends; tap under 0.2 s shows "Hold to talk" | automated mock + manual | `tests/Boo.Core.Tests/HoldToTalkControllerTests.cs`; `tests/Boo.Core.Tests/PushToTalkUplinkTests.cs`; runbook 3.1, 3.2 |
| T4 | Hold the eyes to talk | manual | runbook 3.3 |
| T5 | Talk button (continuous voice), interruption, 3-turn memory | manual | runbook 3.4, 5.1; `docs/verification/2026-11-08-talk-native-checklist.md` |
| T6 | Spoken replies (OpenAI Realtime voice) | UNVERIFIED | No automated live voice test exists (FlaUI cannot speak into a mic). Runbook 3.1 is the manual path once run. |
| T7 | Voice latency target (median <= 1.5 s, p95 <= 3 s over 20 warm turns) | automated mock + manual | `tests/Boo.Core.Tests/LatencyRecorderTests.cs`; runbook 3.5 |
| T8 | Voice commands (start / pause / resume / end focus, "how's my focus", "bye") | automated mock | `tests/Boo.Core.Tests/VoiceCommandTests.cs`; `tests/Boo.Integration.Tests/VoiceToolProtocolTests.cs`. Live voice: UNVERIFIED |
| T9 | Typed requests are classified (Undo / Action / Research / Explain) from their first words | automated mock | `tests/Boo.Core.Tests/RequestIntentTests.cs` |

### Screen explain and pointer

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| E1 | **Screen explain**: one capture of the chosen display, a real answer, the pointer flies to the item and a **ring** is drawn around it | automated live + automated mock | `tools/Boo.VerifyLive/LiveJourneyTests.cs::J3_ScreenExplain_PointerAndRingLandOnTheTarget` (real model; Boo's panel is hidden and a known target window brought forward; NEW ring-coloured pixels must run around the "Send report" button and NEW pointer-coloured pixels must sit on it); `tests/Boo.Demo.Tests/DemoTests.cs::Step1_Explain_DrawsPointerRingAtExpectedCoordinate` (mock; ring at a scripted coordinate, 40 px tolerance; the pointer is not checked there). The flight itself (animation) is not checked, only where the pointer ends up. Boo shows no pointer at all when Windows animation effects are off (reduced motion, by design), so a live journeys run turns them on before Boo starts and keeps them on for all seven steps (session only, restored when the run ends). The reduced-motion paths (marks without a pointer, and hover and focus with animations off) are therefore UNVERIFIED live. The ring must run through the button's left, right and bottom edges and the pointer must land within 20 px of the button. |
| E2 | Marks are only drawn when they validate against the frame | automated mock | `tests/Boo.Core.Tests/AnnotationTests.cs`; `tests/Boo.Core.Tests/PointerAnnotationSequencerTests.cs`; `tests/Boo.Core.Tests/PointerFlightPlannerTests.cs` |
| E3 | Pointer lands exactly on the item at 125 % / 150 % scaling and on a second monitor | manual | runbook 4.2; `docs/verification/2026-11-08-pointer-native-checklist.md` |
| E4 | Marks disappear on scroll, resize or window switch | manual | runbook 4.3 |
| E5 | No display chosen: "Choose a display in Settings before asking about the screen." | UNVERIFIED | No test asserts this message; the live journeys always pick a display first. |

### Research

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| R1 | **Research with source links** (clickable "Open source ..." links under Sources) | automated live + automated mock | `tools/Boo.VerifyLive/LiveJourneyTests.cs::J4_Research_ShowsRealSourceLinks` (real web search; at least one real link, not the mock's); `tests/Boo.Demo.Tests/DemoTests.cs::Step3_Research_ShowsThreeExampleSourceLinks` (mock, three links) |
| R2 | Only absolute http/https links shown; no sources = "Treat this answer as unverified" | automated mock | `tests/Boo.Core.Tests/ResearchSourcesTests.cs`; `tests/Boo.Integration.Tests/ResearchRequestShapeTests.cs` |
| R3 | Clicking a source opens the browser | manual | runbook 6.1 |

### Actions, approval and Undo

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| A1 | **Permissioned Apply then Undo** of a file in the Task folder: nothing changes before Approve; Undo is its own approval card | automated live + automated mock | `tools/Boo.VerifyLive/LiveJourneyTests.cs::J5_ApplyThenUndo_OnlyAfterApproval` (real model creates a file only after Approve; approved Undo deletes it); `tests/Boo.Demo.Tests/DemoTests.cs::Step2_Edit_ApplyThenUndo_RoundTripsFixtureFile` (mock; edit then Undo restores the exact bytes) |
| A2 | Apply then Undo of a **field in another app** (Notepad) with read-back | manual | runbook 6.2 to 6.7 |
| A3 | Undo refusals (nothing recorded, empty before, changed since) and the file undo journal | automated mock | `tests/Boo.Core.Tests/UndoJournalTests.cs`; `tests/Boo.Core.Tests/FileUndoJournalTests.cs` |
| A4 | Guards: no typing into shells or admin tools, not into Boo itself, no hidden characters, no URL user-info, no overwrite without approval, files stay inside the Task folder | automated mock + manual | `tests/Boo.Core.Tests/ActionPolicyTests.cs`; `tests/Boo.Core.Tests/FileWriteBoundaryTests.cs`; `tests/Boo.Core.Tests/SecurityHardeningTests.cs`; `tests/Boo.Core.Tests/SecurityHardening2Tests.cs`; `tests/Boo.Core.Tests/ScopedFilesRootCollapseTests.cs`; runbook 7.1 |
| A5 | Approval expires when the target changes | automated mock | `tests/Boo.Core.Tests/ActionPreviewValidationTests.cs`; `tests/Boo.Core.Tests/ControlReadCoordinatorTests.cs` |
| A6 | Untrusted screen text and model output cannot trigger actions on their own | automated mock | `tests/Boo.Core.Tests/UntrustedInputFuzzTests.cs`; `tests/Boo.Providers.Tests/StructuredReplyFuzzTests.cs` |

### Focus

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| F1 | **25-minute focus session** runs to "Session complete", happy face, history row "25 min ... Completed" | automated live (Developer Mode clock) + manual (real-time clock) | `tools/Boo.VerifyLive/LiveJourneyTests.cs::J6_FocusSession_25Minutes_OnTheDeveloperModeClock` (25 focus-minutes on Boo's existing 4x Developer Mode clock = about 375 real seconds, real AI focus checks every 10 s, the on-task window kept in front; the history must show a focus estimate above 0%); the plain 25 real minutes is runbook 11.1 to 11.4 |
| F2 | Start, Pause, Resume, End update status and history | automated live | `tests/Boo.Ui.Tests/BooSmokeTests.Hardening.cs::FocusSession_StartPauseResumeEnd_UpdatesStatusAndHistory` |
| F3 | Off-task nudge on the notch, "Back to it", happy face at the end | automated mock + manual | `tests/Boo.Demo.Tests/DemoTests.cs::Step4_Focus_NudgeThenBackToIt_EndsHappy` (mock answers off-task on purpose); runbook 10.2 to 10.4. The live journey records nudges but does not force one. |
| F4 | Nudge policy (two off-task checks 2 to 4 min apart, at most one nudge per 2 min) | automated mock | `tests/Boo.Core.Tests/FocusPolicyTests.cs`; `tests/Boo.Core.Tests/FocusSessionControllerTests.cs` |
| F5 | "5 min break" pill pauses the session | manual | runbook 10.3 (pill shown with the nudge) |
| F6 | **Ctrl+Alt+F12** and **Ctrl+Alt+S** pause a running session | automated live | `tests/Boo.Ui.Tests/BooSmokeTests.Hardening.cs::EmergencyStopHotkey_PausesRunningFocusSession`; `tests/Boo.Ui.Tests/BooSmokeTests.U8.cs::PauseAllHotkey_CtrlAltS_PausesRunningFocusSession` |
| F7 | Lock / sleep / a 30 s tick gap pause the session | automated mock + manual | `tests/Boo.Core.Tests/FocusClockTests.cs`; by hand: `docs/USER_GUIDE.md` section 10 ("Paused after sleep" behaviour) |
| F8 | Crash recovery: a running session comes back as Paused | automated mock | `tests/Boo.Core.Tests/FocusRecoveryTests.cs`; `tests/Boo.Integration.Tests/SessionRecoveryTests.cs` |
| F9 | Session history timeline (on / off task, pauses) | automated mock | `tests/Boo.Core.Tests/FocusTimelineTests.cs` |
| F10 | Developer Mode (4x clock, checks every 10 s) | automated live | `tools/Boo.VerifyLive/LiveJourneyTests.cs::J6_FocusSession_25Minutes_OnTheDeveloperModeClock` (the 25-minute session completes in about 6.5 real minutes) |
| F11 | Chirp on nudge and session end (880 to 1320 Hz), Mute chirps | automated mock + manual | `tests/Boo.Core.Tests/ChirpPolicyTests.cs`; `tests/Boo.Core.Tests/ChirpSynthTests.cs`; runbook 10.3 (audible) |

### Settings, providers and data

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| S1 | OpenAI key saved with DPAPI, cleared from the box, never shown | automated mock | `tests/Boo.Demo.Tests/DemoTests.cs::Step0_Settings_FakeKeyAndWorkspaceCopy` (a fake key typed into the masked box and saved through the UI); `tests/Boo.Integration.Tests/StoragePrivacyTests.cs`. The live runs seed the real key straight into the key file (`src/Boo.Storage/CredentialStore.cs`), so they do not exercise the box; their key-leak scan only proves no evidence file contains it. |
| S2 | Task folder setting (the only folder file actions touch) | automated live | `tools/Boo.VerifyLive/LiveJourneyTests.cs::J5_ApplyThenUndo_OnlyAfterApproval` (set through Settings > Task folder > Save settings) |
| S3 | Text AI provider: **OpenAI** | automated live | `tools/Boo.LiveSmoke/LiveRoundTripTests.cs::TypedQuestion_GetsAnAnswerFromTheRealProvider` and `tools/Boo.VerifyLive/LiveJourneyTests.cs::J2_TypedChat_GetsARealAnswer`, run with `text_provider=openai` |
| S4 | Text AI provider: **Anthropic Claude** | automated mock (live: UNVERIFIED) | `tests/Boo.Providers.Tests/AnthropicClientTests.cs`; live needs the `BOO_LIVE_ANTHROPIC_KEY` secret, which does not exist yet |
| S5 | Text AI provider: **Google Gemini** | automated mock (live: UNVERIFIED) | `tests/Boo.Providers.Tests/GeminiClientTests.cs`; live needs the `BOO_LIVE_GEMINI_KEY` secret, which does not exist yet |
| S6 | Model id override | automated mock | `tests/Boo.Core.Tests/AiProviderTests.cs`; live: the workflow's `model` input |
| S7 | Plain messages for 401 / 403 / quota / 429 / 5xx / offline / TLS / timeout | automated mock | `tests/Boo.Core.Tests/ProviderFailureClassifierTests.cs`; `tests/Boo.Integration.Tests/ProviderFailureClientTests.cs` |
| S8 | Start Boo with Windows (HKCU Run value) | automated live | `tests/Boo.Ui.Tests/BooSmokeTests.cs::StartWithWindows_Checkbox_WritesAndRemovesRunValue` |
| S9 | Corrupt settings / appearance: Boo starts with defaults and Save repairs | automated live | `tests/Boo.Ui.Tests/CorruptDataStartupTests.cs::CorruptSettingsAndAppearance_BooStartsWithDefaults_AndSaveRepairsFiles` |
| S10 | Microphone choice and microphone failure messages | automated mock + manual | `tests/Boo.Core.Tests/MicrophoneFailureTests.cs`; `docs/verification/2026-11-08-talk-native-checklist.md` |
| S11 | Reduced motion (app setting or Windows "Animation effects off") | automated mock | `tests/Boo.Core.Tests/AccessibilityPreferencesTests.cs` |
| S16 | Mute speech (replies stay text only) | UNVERIFIED | No test clicks the Mute speech box. The live runs start Boo with speech already off (`SpeechEnabled`) and get text answers, which shows the speech-off path works, not the setting. |
| S12 | Hotkey conflicts are reported truthfully | automated mock | `tests/Boo.Core.Tests/ShortcutStatusTests.cs`; `tests/Boo.Core.Tests/PauseAllShortcutTests.cs` |
| S13 | Opt-in updates from GitHub Releases | automated mock (live: UNVERIFIED) | `tests/Boo.Core.Tests/UpdatePolicyTests.cs`; `tests/Boo.Core.Tests/UpdateVersionGateTests.cs`; a real update round trip has never run |
| S14 | Installer (Velopack) and portable ZIP | automated mock (build only) + UNVERIFIED (install on a PC) | `.github/workflows/release.yml` builds and hashes them; install, Start menu and uninstall are runbook 0.2 |
| S15 | Crash logs are redacted (keys, tokens, profile path) | automated mock | `tests/Boo.Core.Tests/CrashRedactionTests.cs`; `tests/Boo.Integration.Tests/CrashLogStoreTests.cs` |

### Accessibility and performance

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| X1 | Groups and headings for screen readers | automated live | `tests/Boo.Ui.Tests/BooSmokeTests.Accessibility.cs::Panel_ExposesGroupsAndHeadings` |
| X2 | Keyboard: Tab, F6 / Shift+F6, Esc | automated live | `tests/Boo.Ui.Tests/BooSmokeTests.Accessibility.cs::Panel_KeyboardNavigation_TabF6Escape` |
| X3 | Conversation is a list with named items | automated live | `tests/Boo.Ui.Tests/BooSmokeTests.Accessibility.cs::Transcript_IsAListWithNamedItems` |
| X4 | Every control has a name; the notch has keyboard help | automated live | `tests/Boo.Ui.Tests/BooSmokeTests.Accessibility.cs::InteractiveControls_HaveNames_AndNotchHasKeyboardHelp` |
| X5 | Narrator, High contrast, text size, animations off | manual | `docs/verification/2026-11-08-accessibility-native-checklist.md` |
| X6 | Window survives small resize and maximize / restore | automated live | `tests/Boo.Ui.Tests/BooSmokeTests.Hardening.cs::Panel_SurvivesSmallResizeAndMaximizeRestore` |
| X7 | Idle memory, CPU and render-loop budgets | automated live | `tests/Boo.Perf.Tests/BooPerfBudgetTests.cs::Idle_startup_memory_cpu_and_render_loop_stay_within_budget` |
| X8 | The always-on notch draws in software, and one hover-and-retract stays within its memory budget | automated live | `tests/Boo.Perf.Tests/NotchDrawingMemoryTests.cs::Notch_draws_in_software_and_one_hover_stays_within_memory_budget` (windows-ci perf step) |
