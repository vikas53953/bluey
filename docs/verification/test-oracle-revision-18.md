# Test-oracle revision 18: reviewed rebaseline (2026-11-10)

The oracle (`scripts/test_oracle.py`, manifest `docs/verification/test-oracle.json`) was last revised at revision 17 and was never run in CI. On `lane/face` it reported 96 differences. This note records the decision for **every** file, with evidence, before rebaselining. Nothing was accepted blindly.

## How the 96 differences broke down

- **3 frozen files changed** (of 44 frozen). The other 42 frozen files still match revision 17 exactly, including every frozen test file.
- **0 frozen files missing.**
- **95 files not frozen yet** (94 written after revision 17 by later lanes, which never ran `--extend`, plus 1 new in this PR).

Tamper scan over the whole git history of every test file (`git log` per file, counting `[Fact]`/`[Theory]` and lines per commit, plus every commit that removed an `Assert`, `Skip` or test attribute line): one shrink ever (`8185c1d`, since restored) and four assertion edits, each listed below. No `Skip =` anywhere except SF-006, which this PR removes.

Revision-history chain: all 16 links verify. They were hashed on Windows with CRLF line endings, so the script now accepts either newline form of the same text (newline style is not tampering).

## Changed frozen files

| File | What changed | Evidence | Decision |
|---|---|---|---|
| `src/Boo.Core/Contracts.cs` | `ProviderSettings` gains `TextProvider` and Anthropic/Gemini model fields (all optional, defaulted); `AppSettings` gains `StartWithWindows`, `Updates`; new `FocusNudge` record; `ChirpsEnabled` default `false` to `true` | Additive params from b18dae3 (ci lane freeze), ba822e4 (providers), 37c070c (installer), 70a3657 (OCR nudge). Chirp default flipped in 9a5f80d to satisfy `SettingsDefaultsTests.Chirps_are_on_by_default_per_design_decision`, which was itself written first in the freeze commit b18dae3 ("settings defaults (ChirpsEnabled on ...)") | ACCEPT: additive API plus one design decision pinned by a test frozen before it |
| `tests/Boo.Core.Tests/Boo.Core.Tests.csproj` | Copies `docs/design/boo-notch-spec.json` to the test output | b7c485b (face lane freeze) so `FaceSpecTests` can read the design spec. No test removed or filtered | ACCEPT |
| `tests/Boo.Core.Tests/ActionPolicyTests.cs` | SF-006 un-skipped and rewritten (this PR) | Old version: `[Fact(Skip=...)]`, and even unskipped it `return`ed (passed) when no link could be made. New: junction (no admin), directory symlink and file symlink cases; a positive control; asserts the reparse-point rejection specifically; fails loudly if a fixture cannot be created | ACCEPT: strictly stronger |

## Files added to the oracle

| File | First commit | Tests now | Decision |
|---|---|---|---|
| `tests/Boo.Core.Tests/AccessibilityPreferencesTests.cs` | c3fff3e feat(a11y): UIA groups and headings, keyboard F6 regions, focus rings, t | 9 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/ActionReadbackTests.cs` | e89036a feat(u5): research sources and verified edit -> Apply -> Undo chain | 24 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/AiProviderTests.cs` | b1bae4d test(providers): contract tests for AnthropicClient, GeminiClient, facto | 20 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/BlinkSchedulerTests.cs` | b7c485b test: freeze face lane acceptance (U1/U2) | 9 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/CaptionRevealCompletionTests.cs` | 756de70 fix(notch): caption completion from the timeline, not the thresholded op | 3 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/CaptionStreamTests.cs` | 3e01982 fix(notch): keep typing a streamed caption past the 160-character window | 8 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/CaptureExclusionPolicyTests.cs` | b18dae3 test: freeze ci lane acceptance | 7 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/ChirpPolicyTests.cs` | b18dae3 test: freeze ci lane acceptance | 9 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/ChirpSynthTests.cs` | b18dae3 test: freeze ci lane acceptance | 17 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/CrashRedactionTests.cs` | b6d2efc fix(hardening,ci): redact Google/GitHub keys in crash logs; tighten rele | 3 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/CrashReportTests.cs` | ca84a79 feat(core): crash-report policy and shortcut status model | 21 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/CubicBezierEaseTests.cs` | 76a6ecb feat(core): notch easing, face part placement and press-hold tracker | 4 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/FaceMomentScheduleTests.cs` | b7e85ad fix(face): give Responding, Nudge and Happy their own hold expiries | 5 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/FaceMomentsTests.cs` | df40f7f fix(integration): never strand Thinking/Responding; hold Responding thro | 4 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/FaceMotionTests.cs` | 0ec27ae face polish: studio-accurate eye moods, wander, indicators, row layout a | 49 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/FaceSpecTests.cs` | b7c485b test: freeze face lane acceptance (U1/U2) | 19 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/FaceStateResolverTests.cs` | b7c485b test: freeze face lane acceptance (U1/U2) | 6 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/FileUndoJournalTests.cs` | 0eeb406 feat(undo): reverse Boo's last file edit through the approval card | 9 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/FocusClockTests.cs` | f20235f fix(focus): monotonic focus clock, sleep-gap pause, crash recovery | 12 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/FocusLatencyRegressionTests.cs` | fa3144e fix(review): dev-mode nudges with real latency; overlapping captures; si | 1 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/FocusRecoveryTests.cs` | f20235f fix(focus): monotonic focus clock, sleep-gap pause, crash recovery | 5 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/FocusSessionControllerTests.cs` | 37003f1 test(focus): frozen failing tests for focus controller and timeline | 24 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/FocusSupersedeTests.cs` | f97a671 fix(focus): settle a live session that Start replaces instead of leaving | 6 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/FocusTimelineTests.cs` | 37003f1 test(focus): frozen failing tests for focus controller and timeline | 38 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/FuzzKit.cs` | ee08e5b Fuzz Boo's untrusted-input parsers and fix what the fuzzers found | 0 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/GazeTests.cs` | b7c485b test: freeze face lane acceptance (U1/U2) | 15 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/GitHubAccessTokenTests.cs` | 37c070c feat(installer): Velopack per-user installer and opt-in GitHub Releases  | 13 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/HoldToTalkControllerTests.cs` | f9e3bf7 test: freeze talk lane acceptance (U4) | 46 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/LatencyRecorderTests.cs` | f9e3bf7 test: freeze talk lane acceptance (U4) | 23 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/MicrophoneFailureTests.cs` | 7a8d16d Classify OpenAI failures: bad key, quota vs rate limit, outage, offline | 11 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/MonitorLayoutTests.cs` | d6f401d feat(core): MonitorLayout for per-monitor DPI, display fallback and work | 24 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/NotchFaceGeometryTests.cs` | 76a6ecb feat(core): notch easing, face part placement and press-hold tracker | 7 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/NotchLayoutTests.cs` | b7c485b test: freeze face lane acceptance (U1/U2) | 16 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/OcrNudgeTargetMatcherTests.cs` | 967d108 test: freeze pointer lane acceptance (U3/U6) | 19 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/ParityMotionTests.cs` | adba011 test(parity): failing tests for spec motion timings, caption reveal, nud | 37 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/PauseAllShortcutTests.cs` | 8048820 test(U8): frozen failing tests for pause-all hotkey, voice focus command | 17 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/PerfBudgetTests.cs` | d161d29 perf: frame pacer, one Direct3D device, zero-copy capture readback, Read | 19 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/PointerAnnotationSequencerTests.cs` | 967d108 test: freeze pointer lane acceptance (U3/U6) | 13 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/PointerFlightPlannerTests.cs` | 967d108 test: freeze pointer lane acceptance (U3/U6) | 21 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/ProviderFailureClassifierTests.cs` | 7a8d16d Classify OpenAI failures: bad key, quota vs rate limit, outage, offline | 18 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/PushToTalkUplinkTests.cs` | 4c200b6 feat: low-level hook and push-to-talk wiring | 5 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/RequestIntentTests.cs` | e89036a feat(u5): research sources and verified edit -> Apply -> Undo chain | 15 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/ResearchSourcesTests.cs` | e89036a feat(u5): research sources and verified edit -> Apply -> Undo chain | 36 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/ScopedFilesNameFidelityTests.cs` | this PR (uncommitted) | 4 | NEW in this PR: red-first tests for the trailing-dot/device-name/wildcard class (STATUS residual "notes." lands on "notes"). |
| `tests/Boo.Core.Tests/ScopedFilesRootCollapseTests.cs` | ed7f13b fix(core): a scoped file path can no longer resolve to the root itself o | 2 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/SecurityHardening2Tests.cs` | 03ea5b8 security: hands-free voice gate, provider key shape check, GitHub token  | 12 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/SecurityHardeningTests.cs` | 7cab2ec security: show real target window on approvals, block shells/self/hidden | 12 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/SettingsDefaultsTests.cs` | b18dae3 test: freeze ci lane acceptance | 2 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/SettingsRoundTripTests.cs` | 0813385 fix(settings): do not drop a pasted GitHub token when a model id is reje | 3 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/ShortcutStatusTests.cs` | ca84a79 feat(core): crash-report policy and shortcut status model | 9 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/StartupRegistrationTests.cs` | b18dae3 test: freeze ci lane acceptance | 16 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/TestThreadPool.cs` | 06cb52b test(core): raise the thread-pool floor so gate-based tests cannot starv | 0 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/UndoJournalTests.cs` | e89036a feat(u5): research sources and verified edit -> Apply -> Undo chain | 43 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/UntrustedInputFuzzTests.cs` | ee08e5b Fuzz Boo's untrusted-input parsers and fix what the fuzzers found | 8 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/UpdatePolicyTests.cs` | 37c070c feat(installer): Velopack per-user installer and opt-in GitHub Releases  | 23 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/UpdateStatusTextTests.cs` | 37c070c feat(installer): Velopack per-user installer and opt-in GitHub Releases  | 17 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/UpdateVersionGateTests.cs` | 37c070c feat(installer): Velopack per-user installer and opt-in GitHub Releases  | 9 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Core.Tests/VoiceCommandTests.cs` | 8048820 test(U8): frozen failing tests for pause-all hotkey, voice focus command | 32 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Demo.Tests/Boo.Demo.Tests.csproj` | 08fd832 tests: Boo.Demo.Tests drives the four demos end-to-end with FlaUI and sa | - | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Demo.Tests/BooAppFixture.cs` | 08fd832 tests: Boo.Demo.Tests drives the four demos end-to-end with FlaUI and sa | 0 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Demo.Tests/DemoTests.cs` | 08fd832 tests: Boo.Demo.Tests drives the four demos end-to-end with FlaUI and sa | 5 | 3a6912f also accepts "QA source" link names (the mock AI's sources). ACCEPT (demo runs against the mock). |
| `tests/Boo.Demo.Tests/OrderedSteps.cs` | 08fd832 tests: Boo.Demo.Tests drives the four demos end-to-end with FlaUI and sa | 0 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Demo.Tests/xunit.runner.json` | 08fd832 tests: Boo.Demo.Tests drives the four demos end-to-end with FlaUI and sa | - | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Integration.Tests/CrashLogStoreTests.cs` | ca84a79 feat(core): crash-report policy and shortcut status model | 6 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Integration.Tests/ProviderFailureClientTests.cs` | 7a8d16d Classify OpenAI failures: bad key, quota vs rate limit, outage, offline | 7 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Integration.Tests/ResearchRequestShapeTests.cs` | e89036a feat(u5): research sources and verified edit -> Apply -> Undo chain | 7 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Integration.Tests/ReviewCaptureDisposeTests.cs` | 2b36131 fix(review): defer capture-device release past an in-flight readback; re | 2 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Integration.Tests/SessionRecoveryTests.cs` | f20235f fix(focus): monotonic focus clock, sleep-gap pause, crash recovery | 2 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Integration.Tests/VoiceToolProtocolTests.cs` | 8048820 test(U8): frozen failing tests for pause-all hotkey, voice focus command | 13 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Perf.Tests/Boo.Perf.Tests.csproj` | 0ba778f perf: render-loop policy, in-process perf probe and perf budget tests (b | - | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Perf.Tests/BooPerfBudgetTests.cs` | 0ba778f perf: render-loop policy, in-process perf probe and perf budget tests (b | 1 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Perf.Tests/DiagSnapshot.cs` | 0ba778f perf: render-loop policy, in-process perf probe and perf budget tests (b | 0 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Perf.Tests/PerfAppFixture.cs` | 0ba778f perf: render-loop policy, in-process perf probe and perf budget tests (b | 0 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Perf.Tests/PerfBudgets.cs` | 0ba778f perf: render-loop policy, in-process perf probe and perf budget tests (b | 0 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Perf.Tests/budgets.json` | 0ba778f perf: render-loop policy, in-process perf probe and perf budget tests (b | - | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Perf.Tests/xunit.runner.json` | 0ba778f perf: render-loop policy, in-process perf probe and perf budget tests (b | - | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Providers.Tests/AnthropicClientTests.cs` | b1bae4d test(providers): contract tests for AnthropicClient, GeminiClient, facto | 20 | 0662256 loosened "\"observedControls\"" to "observedControls" because the JSON is nested in a text part and quotes are escaped. ACCEPT, but WEAKER than before: worth a stronger assertion later. |
| `tests/Boo.Providers.Tests/AnthropicRequestShapeTests.cs` | b0c4388 feat(providers): Anthropic Claude client (Messages API) | 4 | 7ba89f1 changed max_tokens 4096 to 16384 together with the product change "larger structured budget". ACCEPT (assertion follows a deliberate product change, not weakened). |
| `tests/Boo.Providers.Tests/AssistantProvidersTests.cs` | b1bae4d test(providers): contract tests for AnthropicClient, GeminiClient, facto | 6 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Providers.Tests/Boo.Providers.Tests.csproj` | ba822e4 feat(providers): provider abstraction for text AI (OpenAI default, Claud | - | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Providers.Tests/GeminiClientTests.cs` | b1bae4d test(providers): contract tests for AnthropicClient, GeminiClient, facto | 20 | 0662256: same loosening as AnthropicClientTests. ACCEPT, weaker; follow-up. |
| `tests/Boo.Providers.Tests/GeminiRequestShapeTests.cs` | 5ae02b0 feat(providers): Google Gemini client | 6 | 7ba89f1: maxOutputTokens 4096 to 16384 and untitled-source title falls back to host, matching the OpenAI path. ACCEPT (deliberate behaviour change). |
| `tests/Boo.Providers.Tests/ProviderTestKit.cs` | b1bae4d test(providers): contract tests for AnthropicClient, GeminiClient, facto | 0 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Providers.Tests/ResponsesResultShapeTests.cs` | 90c50e0 fix(providers): vendor wording for malformed Responses results; Realtime | 3 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Providers.Tests/SmokeTests.cs` | ba822e4 feat(providers): provider abstraction for text AI (OpenAI default, Claud | 1 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Providers.Tests/StructuredReplyFuzzTests.cs` | ee08e5b Fuzz Boo's untrusted-input parsers and fix what the fuzzers found | 2 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Ui.Tests/Boo.Ui.Tests.csproj` | 384241a CI: native UI smoke tests for published Boo.exe (FlaUI) (#4) | - | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Ui.Tests/BooAppFixture.cs` | 384241a CI: native UI smoke tests for published Boo.exe (FlaUI) (#4) | 0 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Ui.Tests/BooSmokeTests.Accessibility.cs` | c3fff3e feat(a11y): UIA groups and headings, keyboard F6 regions, focus rings, t | 4 | Emptied by 8185c1d (474 lines to 0, CI stayed green); restored byte-for-byte by 2a9552b; c8ec2ca then added a hover-reveal before reading the notch help (+14/-2, no assertion removed). Current file has all 4 tests. ACCEPT, and the new test gate now blocks an emptied file. |
| `tests/Boo.Ui.Tests/BooSmokeTests.Hardening.cs` | 54f65ed test(ui): smoke steps for no-key Ask/Talk, focus session, emergency stop | 4 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Ui.Tests/BooSmokeTests.U8.cs` | 8048820 test(U8): frozen failing tests for pause-all hotkey, voice focus command | 2 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Ui.Tests/BooSmokeTests.cs` | 384241a CI: native UI smoke tests for published Boo.exe (FlaUI) (#4) | 4 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Ui.Tests/CorruptDataStartupTests.cs` | 54f65ed test(ui): smoke steps for no-key Ask/Talk, focus session, emergency stop | 1 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Ui.Tests/OrderedSteps.cs` | 384241a CI: native UI smoke tests for published Boo.exe (FlaUI) (#4) | 0 | ACCEPT: helper or test as first written, no test removed |
| `tests/Boo.Ui.Tests/xunit.runner.json` | 384241a CI: native UI smoke tests for published Boo.exe (FlaUI) (#4) | - | ACCEPT: helper or test as first written, no test removed |

## Gate now in CI

`windows-ci` runs `python -I scripts/test_oracle.py` and `python -I scripts/test_gate.py static` before restore, and `test_gate.py trx` after each test group. Any change to a file above now needs a reviewed revision (`--extend` for new files, `--rebaseline --reason ... --accept-changed <files>` naming every changed file).
