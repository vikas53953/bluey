# Agent trust plan for Boo (3 phases)

Status: **PLAN ONLY, for Vikas to decide on.** No product code changes in this PR.
Written 12 Nov 2026 on `lane/face` at `f244e47`. Source idea: poteto's AIE talk (12 Nov 2026) on trusting coding agents.

> **Update 13 Nov 2026 (fact-check against `lane/face` at `8474556`).** The counts in sections 1 and 2 and the appendix
> are a baseline measured at `f244e47`. PR #12 (Living ink notch) merged since then and moved some of them:
> `src/` 98 files / 23,286 lines (Core 49 files / 8,209 lines, App 7,586 lines); `NotchWindow.xaml.cs` 1,716 lines
> (code-behind total 4,390); test attributes Core 1,001 and Perf 2; test oracle revision 20, 141 files, 19 history
> links; `Dispatcher.Invoke` in NotchWindow now at line 472; `Dispatcher.BeginInvoke` 14 (NotchWindow 2);
> `catch (Exception…)` 119 (135 broad catches, 70 filtered, about 74 handle-and-continue); tests use
> `Thread.Sleep` 27 times and `Environment.GetEnvironmentVariable` 11 times; non-doc comment lines about 395–400,
> `///` 1,399, XAML/csproj comments 18; the `TODO(talk lane)` is now at `NotchWindow.xaml.cs:126`. PR 1.1 measures
> the baseline again on its own head, so these numbers only change targets, not the plan. Also changed since
> `f244e47`: the repo is **public** (by 13 Nov) and CI uploads work again (section 1.6).

> **Progress, 13 Nov 2026 (`lane/face` at `1c82af8`).** Vikas approved the order in section 5 on 12 Nov, 10:02 PM IST
> (`docs/DECISIONS.md`). Done so far, each with full QA (separate reviewer, a real-key live run, evidence table):
> - **Step 1, storage:** PR #15 (7-day artifact retention) merged; the repo went public, so CI runs and uploads again.
> - **Step 2, screen-capture risk:** PR #18 merged (`00a1fc2`). The live smoke runs only on GitHub's VM; test screenshots
>   show Boo's windows only (proved with the explanation overlay open over a decoy, in demo-ci); a runner check fails any
>   workflow that could run elsewhere.
> - **Phase 1, added by Vikas (12 Nov, 11:20 PM IST):** PR #20 merged (`e1d4b5c`). `live-ai-smoke` runs on every PR and
>   is a **required check** on `lane/face` (pinned to GitHub Actions). The `main` step waits until `lane/face` reaches `main`.
> - **Phase 1, PR 1.1 (ratchet):** PR #21 merged (`1c82af8`). 16 patterns, 554 hits on `lane/face`, may only go down;
>   the base branch's own ratchet judges every PR.
> - **Still open in Phase 1:** PRs 1.2–1.12 in section 2.9, plus the queued follow-ups from the reviews. Owner review of
>   workflow and gate files (CODEOWNERS) is a pending decision for Vikas (`HANDOFF.md`, known issue 0).
> - Phases 2 and 3 have **not started**; they wait for Vikas's explicit go after Phase 1.

## The idea in one picture

poteto's "inverse pyramid": the widest, most trusted layer is the one a machine checks the same way every time.
Prose rules (instructions in prompts) are the thinnest and least trusted layer, so they come last.

```
 ███████████████████████████████████████████  1. Code and architecture    what is even possible   (most trusted)
   ███████████████████████████████████████    2. Lint, types, tests       what the build refuses
     ███████████████████████████████████      3. Runtime + formal checks  what the running app proves
          ███████████████████████             4. Prose rules in prompts   what we hope agents read (least trusted)
```

Rules that follow from it, and what they mean for Boo:

| Talk point | Boo version |
| --- | --- |
| Every correction becomes a deterministic check | Each time a reviewer or Vikas catches a mistake, the fix PR also adds a check that would have caught it (rule id + message), not a new sentence in a prompt. |
| Ban nuance-heavy APIs | `BannedApiAnalyzers` with a `BannedSymbols.txt` (Phase 1). |
| Context through directory structure + dependency rules | Project-reference matrix and namespace rules checked in CI (Phase 1). |
| Ban ~99% of comments | New non-doc comments fail CI unless they carry an allowed tag (Phase 1); existing ones ratchet down. |
| Review via artifacts (video, screenshots) | Screenshots already exist; add video of demo/live runs (Phase 2). |
| Feature map + custom CLI driver | PR #13's `docs/FEATURE_MAP.md` + `tools/Boo.VerifyLive`; add a small agent-facing driver (Phase 2). |
| Statecharts / TLA+ for user flows | TLA+ model of the notch lifecycle (Phase 2). |
| Property-based UI tests | FsCheck over UI event sequences and Apply/Undo (Phase 2). |
| Clean up old anti-patterns | Baseline + ratchet so counts only go down (Phase 1, then ongoing). |
| Release-time fuzz loop that opens fix PRs | Phase 3. |
| Electron-specific items | Do not apply: Boo is .NET 10 / WPF. The equivalents are FlaUI (UI Automation) instead of a DevTools driver, and Roslyn analyzers instead of ESLint. |

---

## 1. Current state (measured on `f244e47`)

All numbers come from this checkout. The exact commands are in the appendix so anyone can re-run them.

### 1.1 Projects and how they reference each other

| Project | Target | References | `.cs` files | Lines |
| --- | --- | --- | --- | --- |
| `src/Boo.Core` | `net10.0` (no Windows, no WPF possible) | none | 48 | 7,601 |
| `src/Boo.Providers` | `net10.0` | Core | 12 | 3,084 |
| `src/Boo.Storage` | `net10.0-windows10.0.19041.0` | Core (+ `Microsoft.Data.Sqlite`, `ProtectedData`) | 5 | 530 |
| `src/Boo.Windows` | `net10.0-windows10.0.19041.0`, WPF + WinForms, unsafe | Core (+ `NAudio`) | 14 | 3,877 |
| `src/Boo.App` | `net10.0-windows10.0.19041.0`, WPF + WinForms, `WinExe` | Core, Windows, Providers, Storage (+ `Velopack`) | 18 | 7,339 |
| **all of `src/`** | | | **97** | **22,431** |

- `Boo.sln` holds the 5 `src` projects and 3 test projects (`Boo.Core.Tests`, `Boo.Integration.Tests`,
  `Boo.Providers.Tests`). `tests/Boo.Demo.Tests`, `tests/Boo.Ui.Tests`, `tests/Boo.Perf.Tests`, the fixture project
  `tests/fixtures/native/Boo.QaWorkspace` and the three `tools/` projects (`Boo.LiveProbe`, `Boo.LiveSmoke`,
  `Boo.QaMockAi`) are outside the solution. The workflows build the three test projects, `Boo.LiveSmoke` and
  `Boo.QaMockAi` one by one; **no workflow builds `Boo.LiveProbe`**, so it can rot unnoticed.
- Test attributes (`[Fact]`/`[Theory]` lines): Core 969, Integration 123, Providers 62, Ui 15, Demo 5, Perf 1.
- The biggest files are WPF code-behind: `MainWindow.xaml.cs` 2,674 lines, `NotchWindow.xaml.cs` 1,489,
  `App.xaml.cs` 469. Most of the notch's behaviour lives there, not in `Boo.Core`. This matters for Phase 2.

### 1.2 Compiler and analyzer settings today

`Directory.Build.props` (applies to every project in the repo, tests and tools included):
`Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest`, `Deterministic=true`.
**One exception:** `tools/Boo.QaMockAi/Boo.QaMockAi.csproj:10` sets `TreatWarningsAsErrors=false`, so warnings
in the mock AI server do not fail the build.

| Setting | Today |
| --- | --- |
| `.editorconfig` / `.globalconfig` | none |
| `BannedSymbols.txt` / BannedApiAnalyzers | none |
| `EnforceCodeStyleInBuild` | not set (style rules `IDE*` are not checked in the build) |
| `AnalysisMode` | not set (only the default set of `CA` rules is on) |
| `GenerateDocumentationFile` | not set in any project |
| `NoWarn` / `WarningsNotAsErrors` in any project | 0 |
| `#pragma warning disable` in `src/`, `tools/`, `tests/` | 0 |
| `[SuppressMessage]` in `src/`, `tools/`, `tests/` | 0 |
| NetArchTest, FsCheck, TLA+, statechart library | none in the repo |
| Hand-rolled seeded fuzz helper | `tests/Boo.Core.Tests/FuzzKit.cs` (used by `UntrustedInputFuzzTests.cs`; also `tests/Boo.Providers.Tests/StructuredReplyFuzzTests.cs`) |

Good news: warnings already fail the build everywhere except `tools/Boo.QaMockAi`, and no single warning has been
suppressed in code. That makes a ratchet cheap. The `QaMockAi` opt-out goes into the baseline on day one and is
removed in cleanup C10 (2.8).

### 1.3 What we already have, mapped to the pyramid

| Layer | What exists | Where | Gap |
| --- | --- | --- | --- |
| 1 Code / architecture | Layered projects; Core and Providers target plain `net10.0`, so they **cannot** reference WPF | `src/*/*.csproj` | Nothing checks the reference matrix or namespace leaks (see 1.5). |
| 2 Lint / types | Nullable on, warnings are errors, `AnalysisLevel=latest` | `Directory.Build.props` | No banned APIs, no threading analyzers, no comment policy, no style enforcement. |
| 2 Tests | Frozen-test oracle: every file under `tests/`, the acceptance notes and `Contracts.cs` hash to `docs/verification/test-oracle.json` (revision 19, 139 files, 18 history links intact) | `scripts/test_oracle.py`, `windows-ci` step "Frozen-test oracle" | Only covers `tests/`; `tools/` is not frozen. |
| 2 Tests | No-vacuous-pass gate: no empty test file, no test project without tests, no `Skip =`; every `.trx` must show tests ran and none skipped | `scripts/test_gate.py` (static + trx), `scripts/ci/assert-tests-ran.ps1` | `test_gate.py static` only scans `tests/`, not `tools/Boo.LiveSmoke` / `tools/Boo.VerifyLive` (the trx check does cover them in `live-ai-smoke`). |
| 3 Runtime | UI smoke + perf budgets on the published `Boo.exe` (FlaUI) | `windows-ci` | Skipped at `f244e47` (storage quota); running again since 13 Nov (see 1.6). |
| 3 Runtime | 4 end-to-end demos on the published `Boo.exe` against a mock OpenAI (blocking) | `demo-ci`, `tools/Boo.QaMockAi`, `tests/Boo.Demo.Tests` | No video. |
| 3 Runtime | Live real-key round trip, fails closed when the key is missing; keys masked twice; evidence scanned for keys before upload | `live-ai-smoke.yml` (manual `workflow_dispatch`) | Manual only; not a required check. |
| 3 Runtime | Anti-mock guard: refuses to run if any AI host is pinned in `hosts` or resolves to loopback | `live-ai-smoke.yml` step "Refuse if any AI host is redirected"; PR #14 (open) widens it to every private/LAN address and moves it into `tools/Boo.LiveSmoke/RealAiHostGuard.cs` | PR #14 not merged. |
| 3 Runtime | Notch face gallery: every face state rendered offscreen to PNG | `Boo.exe --notch-gallery`, `windows-ci` | Skipped at `f244e47`; running again since 13 Nov (1.6). |
| Process | Branch protection on `lane/face` and `main`: required checks `demos` and `build-test-publish`, strict (branch must be up to date) | GitHub settings | No required reviews, admins not enforced, no rulesets. |
| Process | Separate reviewer, evidence table with live / mock / manual / UNVERIFIED labels | Vikas's pipeline; PR bodies | Lives only in prompts and PR habits. Nothing fails if the table is missing. |
| Process | verify-boo skill: 7 live journeys (j0–j6) on the real `Boo.exe`, a feature map of 68 features, a prove-it rule, a map drift checker | PR #13 (open, `test/verify-boo-skill`): `.claude/skills/verify-boo/`, `tools/Boo.VerifyLive/`, `docs/FEATURE_MAP.md`, `AGENTS.md` | Not merged; j3 (screen explain) failed 2 of 3 live runs; not a required check. |
| 4 Prose | `AGENTS.md` / `CLAUDE.md` rules (PR #13), the standing orders in Vikas's own Claude settings | prompts | See 1.4: most of these can become code. |

### 1.4 Prose rules that should become code checks

Each row is a rule that today is only written in a prompt or a PR habit. Phase 1 turns each into a CI check.

| Prose rule today | Why prose is not enough | Becomes (Phase 1) |
| --- | --- | --- |
| "Never capture Vikas's real screen" | `Capture.Screen()` (whole desktop) is called in 3 fixtures: `tests/Boo.Demo.Tests/BooAppFixture.cs:167`, `tests/Boo.Ui.Tests/BooAppFixture.cs:166`, `tests/Boo.Perf.Tests/PerfAppFixture.cs:178`. `tools/Boo.LiveSmoke` compiles the Demo fixture in and calls `CaptureScreen` 3 times. `live-ai-smoke.yml` defaults to `runner: self-hosted`, which `docs/LIVE_AI_SMOKE.md` (line 50) describes as "your own PC". So the default live run **would** photograph Vikas's desktop. | `BOO-SAFE-001`: a capture guard that refuses whole-desktop capture unless `RUNNER_ENVIRONMENT=github-hosted` or a dedicated sandbox label is present, plus a workflow lint that no FlaUI job runs on a bare `self-hosted` label. |
| "Never print or commit a key" | The key scan runs only inside `live-ai-smoke` on its own evidence folder. | `BOO-SAFE-002`: a key-pattern scan on every PR diff and every uploaded evidence folder, in all workflows. |
| "No skipped tests" | Already code for `tests/` (good). `test_gate.py static` does not look at `tools/`. | `BOO-TEST-001`: extend the static gate to `tools/*` test projects. |
| "Every claim has an evidence table with live/mock/manual/UNVERIFIED labels and a run link" | Only habit. A PR without a table merges fine. | `BOO-EVID-001`: a PR-body check (runs on `pull_request` opened/edited/synchronize). |
| "Don't touch `tests/` to get green" | Already code (oracle). | Keep. Add the new check files (`scripts/quality/*`, `BannedSymbols.txt`, baselines) to a second frozen list so an agent cannot quietly loosen a gate. |
| "Don't weaken warnings" | Nothing stops a PR adding `<NoWarn>` or `#pragma warning disable`. | `BOO-LINT-002`: fail on any new `NoWarn`, `WarningsNotAsErrors`, `TreatWarningsAsErrors>false`, `#pragma warning disable`, or `[SuppressMessage]` outside the baseline files. |
| "Never push to main / lane/face, never merge" | Partly code (branch protection), but admins are not enforced. | Decision for Vikas (see end): turn on `enforce_admins` and require 1 approving review. |
| "Plain words for Vikas" | Taste, not checkable. | Stays prose (the thin top of the pyramid). |

### 1.5 Anti-patterns counted in `src/` (product code)

`tools/` and `tests/` counts are shown where they matter for later phases.

| Pattern | `src/` | Where in `src/` | `tools/` | `tests/` | Notes |
| --- | --- | --- | --- | --- | --- |
| `async void` | **7** | `MainWindow.xaml.cs` 4, `MainWindow.Updates.cs` 2, `MainWindow.Talk.cs` 1 | 0 | 0 | 6 are event handlers (`*_OnClick`, `Realtime_OnReceived`); 1 is a plain method: `StartPushToTalk` (`MainWindow.Talk.cs:225`). |
| `Thread.Sleep` | **0** | | 1 | 23 | Tests: `BooPerfBudgetTests.cs` 7, `BooSmokeTests.Hardening.cs` 4, `DemoTests.cs` 3, `BooSmokeTests.cs` 3, ... |
| `Task.Delay` | **1** | `MainWindow.Edits.cs:382`: a 120 ms retry loop while re-reading a control (a "wait and hope" fix) | 0 | 20 | Tests: `ResponseDeadlineTests.cs` 8, ... |
| `new DispatcherTimer` | **8** | `MainWindow.xaml.cs` 3, `NotchWindow.xaml.cs` 2, `MainWindow.Talk.cs` 1, `MainWindow.Voice.cs` 1, `PerfDiagnostics.cs` 1 | 0 | – | Mostly real timers (focus clock, perf sampling). Not banned; counted so new ones are visible. |
| `Dispatcher.Invoke(` (blocking) | **2** | `App.xaml.cs:353`, `NotchWindow.xaml.cs:438` | 0 | 0 | |
| `Dispatcher.BeginInvoke(` | **13** | `App.xaml.cs` 9 (5 tray-menu items, 4 system events: display, session switch ×2, power), `MainWindow.xaml.cs` 2, `MainWindow.Talk.cs` 1, `NotchWindow.xaml.cs` 1 | 0 | 0 | |
| `Dispatcher.InvokeAsync(` | 2 | `MainWindow.Voice.cs:27`, `MainWindow.xaml.cs:2240` | 0 | 0 | The pattern to standardise on. |
| `Task<T>.Result` | **1** | `PushToTalkUplink.cs:97` (read only after `IsCompleted`, so it does not block) | 0 | 0 | `AudioSession.cs:111` `mm.Result` is an `MmException` property, not a task. |
| `Task.Wait(` / `.GetAwaiter().GetResult()` | **0** | | 1 | 1 | The 3 `.Wait(` calls in `src/` are on `ManualResetEventSlim` (`LowLevelKeyboardHook.cs:57`) and `SemaphoreSlim` with a timeout (`ScreenCapture.cs:109,117`), not on tasks. |
| Static mutable state | **2** | `AccessibilitySettings.cs:17` (`static UISettings? _uiSettings`), `App.xaml.cs:19` (`static bool StartedInBackground { get; private set; }`) | 4 | 2 | All other statics in `src/` are `readonly`, `const` or get-only. |
| Broad `catch` (bare `catch` or `catch (Exception …)`) | **134** | 118 `catch (Exception…)` + 16 bare `catch` | 3 | 62 | Of the 134: **24 have an empty body** (swallow), **73 handle and carry on** without rethrowing, **37 rethrow or wrap**. Top files: `MainWindow.xaml.cs` 32, `DesktopTools.cs` 9, `ControlObservationReader.cs` 7, `ResponsesClient.cs` 7. **69 of the 118** `catch (Exception…)` carry a `when (…)` filter, so they are already narrower than they look. |
| `DateTime.Now/UtcNow`, `DateTimeOffset.Now/UtcNow` | **14** | App 11, Windows 2, Core 1 | 6 | 15 | Hidden clock: makes time-based behaviour hard to test. |
| `Environment.GetEnvironmentVariable` | **3** | App 2, Core 1 (`CaptureExclusionPolicy`) | 4 | 9 | Hidden switches (`BOO_DATA_DIR`, `BOO_PERF_DIAG`, `BOO_QA_CAPTURABLE`). |
| `Process.Start(` | 3 | `DesktopTools.cs` 2, `MainWindow.xaml.cs` 1 | 0 | 1 | Launching programs is security-sensitive. |
| `GC.Collect(` | **0** | | 0 | 5 | Perf tests only. |
| Non-doc comments (`//`, `/* */`) | **374 lines** | 333 full-line `//` + 35 trailing `//` + 6 inline `/* */` | 20 | 575 | `///` XML doc lines: 1,291 in `src/`. XAML/csproj `<!-- -->`: 17. |
| …of which contain a "reason" word | about 62–65 (depends on how strings are stripped) | e.g. "otherwise", "never", "must not", "race", "instead", "because" (heuristic, list in appendix) | 5 | 64 | **Heuristic.** A sample shows most are invariants ("user-info URLs are never offered"), not workaround excuses. Needs a human pass, not a mass delete. |
| `TODO` / `FIXME` / `HACK` / `XXX` in comments | **1** | `NotchWindow.xaml.cs:111` `/// TODO(talk lane): …` (no issue link) | 0 | 0 | |

**Cross-layer references**

| Check | Result |
| --- | --- |
| Core → WPF / WinForms | **0, and impossible**: Core targets `net10.0`. |
| Providers → UI (App, WPF) | **0, and impossible**: Providers targets `net10.0` and references only Core. |
| Storage → Providers / Windows / App | 0 (references Core only). |
| Windows → Providers / Storage / App | 0 (references Core only). |
| Core → network types | **1**: `ProviderFailure.cs:1` `using System.Net.Http;` |
| Core → file system | **2**: `Directory.Exists` in `ActionPolicy.cs:209` and `FileActionResolver.cs:38`. |
| Core → registry | 0 real uses (`StartupRegistration.cs` defines `IStartupRegistry`; the real one lives outside Core, as intended). |
| App (UI) → network directly | **3**: `MainWindow.xaml.cs:65` field `HttpClient? _http`, created at `:1624` and `:2123`. Network calls should go through Providers. |
| Windows → WPF UI | 0 uses of `System.Windows.Controls`/`Shapes`; `NativeCaptureDevice.cs` uses `System.Windows.Media(.Imaging)` (bitmaps) and 2 files use `System.Windows.Automation` (UI Automation). Both fit a "Windows integration" layer. |

### 1.6 CI health (red at `f244e47`, green again since 13 Nov)

- **Update 13 Nov:** the repo is now public and uploads work again. `windows-ci` run 43287422701 and `demo-ci` run
  43287422696 on `lane/face` `8474556` passed every step, including the upload, notch gallery, UI smoke and perf
  budgets. The rest of this section describes 12 Nov, when it was written.
- On 12 Nov GitHub Actions **artifact storage quota was full** (the repo was private then). The 10 most recent completed runs of
  `windows-ci` and of `demo-ci` all failed. Example `windows-ci` run 43245893674: build and all three unit
  test steps **passed**; "Upload published app" failed with "Artifact storage quota has been hit"; as a result
  the notch gallery, **UI smoke tests and perf budgets were skipped**. So on 12 Nov the required check
  `build-test-publish` was red for storage reasons, and the UI/perf layer was not running at all.
- Artifact list (API, 12 Nov): `boo-win-x64` 34 uploads × ~84 MB, `boo-installer-win-x64` 10 × ~182 MB,
  `boo-portable-win-x64` 11 × ~85 MB, `ui-smoke-screenshots` 135 × ~3.4 MB, `demo-evidence` 70 × ~1.0 MB,
  `test-results` 176 × ~0.3 MB, `perf-results` 65 × ~0.2 MB, `live-ai-evidence` 8 × ~1.1 MB.
  Unexpired total listed: ~6,214 MB. PR #15's commit message reports 22.4 GB used in 4 days (90-day default retention).
- Every push to a PR branch runs `windows-ci` and `demo-ci` **twice** (once for `push`, once for `pull_request`;
  the run list shows them in pairs), so the 84 MB app upload happens twice per push.
- `release.yml` also runs on **every** `pull_request` (plus pushes to `main`, tags `v*` and manual runs) and uploads
  the installer (~182 MB) and the portable ZIP (~85 MB), about 3× the app upload. This is the largest storage cost
  per PR push.
- Recent green run times: `windows-ci` 5–6 min, `demo-ci` 2–4 min. `live-ai-smoke` with journeys: 3 runs on PR #13
  took about 10, 10 and 14 min (only one fully green), so plan on 10–14 min.
- PR #15 (open) sets `retention-days: 7` on every upload. Uploads already work again (repo public), so it is no
  longer a blocker, but it still keeps storage from filling up again.

---

## 2. Phase 1: the deterministic layer

Goal: a mistake an agent made once can never merge again, because the build or a CI script refuses it with
a message that says how to fix the cause.

### 2.1 Ratchet engine (baseline that can only go down)

- `docs/quality/baseline.json`: one number per rule per project (today's counts from section 1.5).
- `scripts/quality/ratchet.py`: re-counts with the same rules as the appendix; **fails** if any number went up;
  if a number went down it prints "baseline can drop from N to M, update baseline.json in this PR" and fails
  until the file is lowered, so the gain is locked in.
- For analyzer rules (2.2, 2.3) the baseline is the C# standard: one `AnalyzerBaseline.cs` per project with
  assembly-level `[SuppressMessage("…", "RS0030", Scope = "member", Target = "~M:…", Justification = "BASELINE 2026-11-12")]`
  entries. Any `[SuppressMessage]` or `#pragma warning disable` **outside** those files fails `BOO-LINT-002`.
  Today there are 0 of either, so this starts clean.
- New code is held to zero immediately; old code is fixed class by class (2.7).

### 2.2 Banned APIs (`Microsoft.CodeAnalysis.BannedApiAnalyzers`)

Wiring: one `PackageReference` and one `AdditionalFiles` item added in `Directory.Build.props`, conditioned on the
project living under `src/` (tests and tools get their own, smaller list later, because `tests/` is frozen and needs
a reviewed oracle revision). Diagnostic `RS0030` is already an error because `TreatWarningsAsErrors=true`.

`src/BannedSymbols.txt` (exact lines; the text after `;` is the message the agent sees):

```text
# BOO-ASYNC-001 blocking sleeps hide races
M:System.Threading.Thread.Sleep(System.Int32);BOO-ASYNC-001 A sleep hides a race instead of fixing it. Await the thing you are waiting for (Task, TaskCompletionSource, SemaphoreSlim.WaitAsync) or inject TimeProvider. Do not make the sleep longer.
M:System.Threading.Thread.Sleep(System.TimeSpan);BOO-ASYNC-001 A sleep hides a race instead of fixing it. Await the thing you are waiting for or inject TimeProvider.

# BOO-ASYNC-002 sync-over-async deadlocks the WPF UI thread
M:System.Threading.Tasks.Task.Wait;BOO-ASYNC-002 Blocking on a Task can deadlock the UI thread. Make the caller async and await it.
M:System.Threading.Tasks.Task.Wait(System.Int32);BOO-ASYNC-002 Make the caller async and await it.
M:System.Threading.Tasks.Task.Wait(System.Int32,System.Threading.CancellationToken);BOO-ASYNC-002 Make the caller async and await it.
M:System.Threading.Tasks.Task.Wait(System.Threading.CancellationToken);BOO-ASYNC-002 Make the caller async and await it.
M:System.Threading.Tasks.Task.Wait(System.TimeSpan);BOO-ASYNC-002 Make the caller async and await it.
M:System.Threading.Tasks.Task.Wait(System.TimeSpan,System.Threading.CancellationToken);BOO-ASYNC-002 Make the caller async and await it.
M:System.Threading.Tasks.Task.WaitAll(System.Threading.Tasks.Task[]);BOO-ASYNC-002 Use await Task.WhenAll.
M:System.Threading.Tasks.Task.WaitAll(System.Threading.Tasks.Task[],System.Int32);BOO-ASYNC-002 Use await Task.WhenAll.
M:System.Threading.Tasks.Task.WaitAll(System.Threading.Tasks.Task[],System.Int32,System.Threading.CancellationToken);BOO-ASYNC-002 Use await Task.WhenAll.
M:System.Threading.Tasks.Task.WaitAll(System.Threading.Tasks.Task[],System.Threading.CancellationToken);BOO-ASYNC-002 Use await Task.WhenAll.
M:System.Threading.Tasks.Task.WaitAll(System.Threading.Tasks.Task[],System.TimeSpan);BOO-ASYNC-002 Use await Task.WhenAll.
M:System.Threading.Tasks.Task.WaitAny(System.Threading.Tasks.Task[]);BOO-ASYNC-002 Use await Task.WhenAny.
M:System.Threading.Tasks.Task.WaitAny(System.Threading.Tasks.Task[],System.Int32);BOO-ASYNC-002 Use await Task.WhenAny.
M:System.Threading.Tasks.Task.WaitAny(System.Threading.Tasks.Task[],System.Int32,System.Threading.CancellationToken);BOO-ASYNC-002 Use await Task.WhenAny.
M:System.Threading.Tasks.Task.WaitAny(System.Threading.Tasks.Task[],System.Threading.CancellationToken);BOO-ASYNC-002 Use await Task.WhenAny.
M:System.Threading.Tasks.Task.WaitAny(System.Threading.Tasks.Task[],System.TimeSpan);BOO-ASYNC-002 Use await Task.WhenAny.
P:System.Threading.Tasks.Task`1.Result;BOO-ASYNC-002 .Result blocks if the task is not finished. Await it. If you already checked IsCompleted, await it anyway: the cost is nil and the rule stays simple.
M:System.Runtime.CompilerServices.TaskAwaiter.GetResult;BOO-ASYNC-002 GetAwaiter().GetResult() is a blocking wait. Make the caller async.
M:System.Runtime.CompilerServices.TaskAwaiter`1.GetResult;BOO-ASYNC-002 GetAwaiter().GetResult() is a blocking wait. Make the caller async.

# BOO-UI-001 one way onto the UI thread: Dispatcher.InvokeAsync
M:System.Windows.Threading.Dispatcher.Invoke(System.Action);BOO-UI-001 Synchronous Invoke can deadlock against a thread the UI waits on. Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.Invoke(System.Action,System.Windows.Threading.DispatcherPriority);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.Invoke(System.Action,System.Windows.Threading.DispatcherPriority,System.Threading.CancellationToken);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.Invoke(System.Action,System.Windows.Threading.DispatcherPriority,System.Threading.CancellationToken,System.TimeSpan);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.Invoke``1(System.Func{``0});BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.Invoke``1(System.Func{``0},System.Windows.Threading.DispatcherPriority);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.Invoke``1(System.Func{``0},System.Windows.Threading.DispatcherPriority,System.Threading.CancellationToken);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.Invoke``1(System.Func{``0},System.Windows.Threading.DispatcherPriority,System.Threading.CancellationToken,System.TimeSpan);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.Invoke(System.Delegate,System.Object[]);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.Invoke(System.Delegate,System.TimeSpan,System.Object[]);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.Invoke(System.Delegate,System.TimeSpan,System.Windows.Threading.DispatcherPriority,System.Object[]);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.Invoke(System.Delegate,System.Windows.Threading.DispatcherPriority,System.Object[]);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority,System.Delegate);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority,System.Delegate,System.Object);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority,System.Delegate,System.Object,System.Object[]);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority,System.TimeSpan,System.Delegate);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority,System.TimeSpan,System.Delegate,System.Object);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority,System.TimeSpan,System.Delegate,System.Object,System.Object[]);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.DispatcherExtensions.Invoke(System.Windows.Threading.Dispatcher,System.Action);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.DispatcherExtensions.Invoke(System.Windows.Threading.Dispatcher,System.Action,System.TimeSpan);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.DispatcherExtensions.Invoke(System.Windows.Threading.Dispatcher,System.Action,System.TimeSpan,System.Windows.Threading.DispatcherPriority);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.DispatcherExtensions.Invoke(System.Windows.Threading.Dispatcher,System.Action,System.Windows.Threading.DispatcherPriority);BOO-UI-001 Use await Dispatcher.InvokeAsync(...).
M:System.Windows.Threading.Dispatcher.BeginInvoke(System.Delegate,System.Object[]);BOO-UI-002 BeginInvoke is fire-and-forget: exceptions vanish and ordering is a guess. Use Dispatcher.InvokeAsync and observe the result (await it, or pass it to the UI error sink).
M:System.Windows.Threading.Dispatcher.BeginInvoke(System.Delegate,System.Windows.Threading.DispatcherPriority,System.Object[]);BOO-UI-002 Use Dispatcher.InvokeAsync and observe the result.
M:System.Windows.Threading.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority,System.Delegate);BOO-UI-002 Use Dispatcher.InvokeAsync and observe the result.
M:System.Windows.Threading.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority,System.Delegate,System.Object);BOO-UI-002 Use Dispatcher.InvokeAsync and observe the result.
M:System.Windows.Threading.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority,System.Delegate,System.Object,System.Object[]);BOO-UI-002 Use Dispatcher.InvokeAsync and observe the result.
M:System.Windows.Threading.DispatcherExtensions.BeginInvoke(System.Windows.Threading.Dispatcher,System.Action);BOO-UI-002 Use Dispatcher.InvokeAsync and observe the result.
M:System.Windows.Threading.DispatcherExtensions.BeginInvoke(System.Windows.Threading.Dispatcher,System.Action,System.Windows.Threading.DispatcherPriority);BOO-UI-002 Use Dispatcher.InvokeAsync and observe the result.
M:System.Windows.Forms.Application.DoEvents;BOO-UI-003 DoEvents re-enters the message loop and causes heisenbugs. Await instead.

# BOO-TIME-001 hidden clocks and timer-as-fix
P:System.DateTime.Now;BOO-TIME-001 Hidden clock. Take a TimeProvider (inject it; tests use a fake) and call GetLocalNow().
P:System.DateTime.UtcNow;BOO-TIME-001 Hidden clock. Take a TimeProvider and call GetUtcNow().
P:System.DateTimeOffset.Now;BOO-TIME-001 Hidden clock. Take a TimeProvider and call GetLocalNow().
P:System.DateTimeOffset.UtcNow;BOO-TIME-001 Hidden clock. Take a TimeProvider and call GetUtcNow().
M:System.Threading.Tasks.Task.Delay(System.Int32);BOO-TIME-002 A delay used to "let things settle" hides a race. Wait for the real signal. For a real timeout, use Task.Delay(TimeSpan, TimeProvider, CancellationToken) so tests can control time.
M:System.Threading.Tasks.Task.Delay(System.Int32,System.Threading.CancellationToken);BOO-TIME-002 Wait for the real signal, or use the TimeProvider overload.
M:System.Threading.Tasks.Task.Delay(System.TimeSpan);BOO-TIME-002 Wait for the real signal, or use the TimeProvider overload.
M:System.Threading.Tasks.Task.Delay(System.TimeSpan,System.Threading.CancellationToken);BOO-TIME-002 Wait for the real signal, or use the TimeProvider overload.

# BOO-RT-001 runtime knobs that agents reach for
M:System.GC.Collect;BOO-RT-001 Forcing a GC hides a leak. Find what holds the memory (dispose it). Perf tests have their own list.
M:System.GC.Collect(System.Int32);BOO-RT-001 Forcing a GC hides a leak.
M:System.GC.Collect(System.Int32,System.GCCollectionMode);BOO-RT-001 Forcing a GC hides a leak.
M:System.GC.Collect(System.Int32,System.GCCollectionMode,System.Boolean);BOO-RT-001 Forcing a GC hides a leak.
M:System.GC.Collect(System.Int32,System.GCCollectionMode,System.Boolean,System.Boolean);BOO-RT-001 Forcing a GC hides a leak.
M:System.Environment.GetEnvironmentVariable(System.String);BOO-RT-002 Hidden switch. Read environment variables only in BooEnvironment (one audited place) and pass the value in.
M:System.Environment.GetEnvironmentVariable(System.String,System.EnvironmentVariableTarget);BOO-RT-002 Read environment variables only in BooEnvironment.

# BOO-SAFE-003 launching programs is a permissioned action
M:System.Diagnostics.Process.Start(System.String);BOO-SAFE-003 Launching a program must go through the ActionPolicy gate (DesktopTools). Do not call Process.Start directly.
M:System.Diagnostics.Process.Start(System.String,System.String);BOO-SAFE-003 Go through the ActionPolicy gate.
M:System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo);BOO-SAFE-003 Go through the ActionPolicy gate.
M:System.Diagnostics.Process.Start(System.String,System.Collections.Generic.IEnumerable{System.String});BOO-SAFE-003 Go through the ActionPolicy gate.
```

Why each one, in plain words:

| Rule | Why it is banned | Hits in `src/` today (baselined) |
| --- | --- | --- |
| `BOO-ASYNC-001` `Thread.Sleep` | The classic agent "fix" for a timing bug: it makes the test pass on a fast machine and hides the race. | 0 |
| `BOO-ASYNC-002` `Task.Wait/WaitAll/WaitAny`, `.Result`, `GetAwaiter().GetResult()` | Blocking on async work from the WPF UI thread deadlocks. | 1 (`PushToTalkUplink.cs:97`, safe but simple to rewrite) |
| `BOO-UI-001` `Dispatcher.Invoke` | Synchronous cross-thread call; deadlocks when the UI thread is itself waiting. | 2 |
| `BOO-UI-002` `Dispatcher.BeginInvoke` | Fire-and-forget; exceptions are lost and ordering is "probably after". `InvokeAsync` returns an operation you can await. | 13 |
| `BOO-UI-003` `Application.DoEvents` | Re-entrancy bugs. | 0 (preventive) |
| `BOO-TIME-001` `DateTime(Offset).Now/UtcNow` | Hidden clock: time-based behaviour (focus sessions, nudges, update checks) cannot be property-tested in Phase 2 without a fake clock. .NET 10 ships `TimeProvider`. | 14 |
| `BOO-TIME-002` `Task.Delay` without `TimeProvider` | Same, plus "delay as a fix". The `TimeProvider` overloads stay allowed. | 1 |
| `BOO-RT-001` `GC.Collect` | Hides leaks. | 0 (preventive; perf tests keep it) |
| `BOO-RT-002` `Environment.GetEnvironmentVariable` | Test switches like `BOO_QA_CAPTURABLE` change privacy behaviour. One reader makes them auditable. | 3 |
| `BOO-SAFE-003` `Process.Start` | Running programs must go through the permission gate. | 3 |

Not banned, on purpose: `DispatcherTimer` (8 real uses; counted by the ratchet instead), `lock` (42 uses; correct
tool), `Task.Run` (0 in `src/`).

Honest limit: the `Dispatcher`, `DispatcherExtensions` and most `Task` overload lists were generated by reflecting
the .NET Framework assemblies on this PC; two newer overloads (`Task.Wait(TimeSpan, CancellationToken)`,
`Process.Start(String, IEnumerable<String>)`) were added by hand. .NET 10 may have more (for example
`Task.WaitAll(ReadOnlySpan<Task>)`, `Task.WaitAll(IEnumerable<Task>)`). The list above also still misses escape
routes the reviewer found, which PR 1.3 must add: instance `Process.Start()` (`new Process { … }.Start()`), the
`SecureString` `Process.Start` overloads, `ConfiguredTaskAwaitable…ConfiguredTaskAwaiter.GetResult`
(`.ConfigureAwait(false).GetAwaiter().GetResult()`), and `DateTime.Today`. Since C# 10,
`Dispatcher.BeginInvoke(() => …)` binds to the instance `BeginInvoke(Delegate, Object[])`, not the extension;
both are banned, so it fires either way.
PR 1.3 regenerates the list from the `net10.0` reference pack and adds a **canary**: a tiny project
`scripts/quality/canary/` with one deliberate call per line. CI builds it and **expects** one `RS0030` per
banned line. If a line stops firing, the canary fails. That proves every ban works.

### 2.3 Other analyzer rules (as errors, with baseline)

Added via a root `.editorconfig` (applies to `src/` first):

| Rule | What it catches | Expected hits today |
| --- | --- | --- |
| `Microsoft.VisualStudio.Threading.Analyzers` `VSTHRD100` | `async void` methods | 7 (allow-listed pattern: event handlers that call one `RunUiAction(async () => …)` helper which logs to `CrashLogStore`; then the count drops to 0) |
| `VSTHRD101` | async lambda passed as `void` delegate | measured in PR 1.4 |
| `VSTHRD110` | a returned `Task` that nobody observes | measured in PR 1.4 |
| `VSTHRD002` | sync wait on async work | overlaps 2.2; kept as a second net |
| `VSTHRD001` | legacy thread-switching APIs (`Dispatcher.Invoke`/`BeginInvoke`) | **off**: its suggested fix is JoinableTaskFactory, which Boo does not use; the same calls are already banned by `BOO-UI-001/002` |
| `VSTHRD004`, `VSTHRD010`, `VSTHRD011`, `VSTHRD200` | JoinableTaskFactory and `Async`-suffix naming rules | **off**: Boo does not use JoinableTaskFactory; the naming rule is noise |
| `CA1031` | catching `Exception` (or bare `catch`) without rethrow | at most 97 (24 empty + 73 handle-and-continue from 1.5); likely fewer, because filtered `catch … when (…)` may not be flagged; exact number measured in PR 1.4 |
| `CA2211` | non-constant public static fields | 0 public today; `BOO-ARCH-005` (2.4) covers the 2 private ones |
| `CA2007` | missing `ConfigureAwait` | **off**: 70 `ConfigureAwait(false)` already used where it matters; in WPF UI code it would be wrong |

**TreatWarningsAsErrors scope.** Keep it as it is: on for every project (true today except `tools/Boo.QaMockAi`, fixed in C10). Add only:
`EnforceCodeStyleInBuild=true` for `src/` with a short `.editorconfig` that makes one style rule an error
(`IDE0005` unused usings, which needs `GenerateDocumentationFile=true` with `CS1591` off; `IDE0055` formatting is
**not** turned on, too noisy). Do **not** switch to `AnalysisMode=All`: it would light up hundreds of rules at once.
`BOO-LINT-002` makes it impossible to turn warnings off again (2.5).

### 2.4 Architecture rules (directory structure + dependency rules)

Two layers, cheapest first:

**A. Project-reference matrix (script, runs on Linux in seconds).**
`docs/architecture/layers.json` + `scripts/quality/arch_refs.py` reads every `*.csproj` and fails on any
`ProjectReference` or `PackageReference` that is not allowed:

| Project | May reference projects | May reference packages | Must stay on |
| --- | --- | --- | --- |
| `Boo.Core` | none | none | `net10.0` (no `-windows`) |
| `Boo.Providers` | Core | none | `net10.0` |
| `Boo.Storage` | Core | `Microsoft.Data.Sqlite`, `System.Security.Cryptography.ProtectedData` | |
| `Boo.Windows` | Core | `NAudio` | |
| `Boo.App` | Core, Windows, Providers, Storage | `Velopack` | |
| any `tests/*` | only what it tests (today's refs are the allowlist) | test packages | |

Today: **0 violations.** The rule's value is that an agent cannot add `Boo.Windows → Boo.Providers` to "just
reuse a helper".

**B. Type-level rules (NetArchTest in a new test project).**
`tests/Boo.Architecture.Tests/` (added with `test_oracle.py --extend`, which is additive and reviewed):

| Rule id | Rule | Today |
| --- | --- | --- |
| `BOO-ARCH-001` | Types in `Boo.Core` do not depend on `System.Net.Http`, `System.Net.WebSockets`, `Microsoft.Win32`, `Microsoft.Data.Sqlite`, `NAudio`, `Velopack` | 1 (`ProviderFailure.cs` uses `System.Net.Http`) |
| `BOO-ARCH-002` | `Boo.Core` does not touch the file system (`System.IO.File`, `System.IO.Directory`); go through an interface like `IStartupRegistry` | 2 (`ActionPolicy.cs:209`, `FileActionResolver.cs:38`) |
| `BOO-ARCH-003` | `Boo.App` does not create `HttpClient` / `ClientWebSocket`; network goes through `Boo.Providers` | 3 (`MainWindow.xaml.cs:65,1624,2123`) |
| `BOO-ARCH-004` | `Boo.Windows` does not use `System.Windows.Controls`, `System.Windows.Shapes`, or derive from `Window` | 0 |
| `BOO-ARCH-005` | No mutable static fields or static properties with a setter in `src/` | 2 (`AccessibilitySettings._uiSettings`, `App.StartedInBackground`) |
| `BOO-ARCH-006` | Every `*Window` code-behind file stays under a size budget (start at today's max, 2,674 lines, ratchet down) | 0 over budget |

Directory structure: `Boo.App` is already sliced by feature with partial classes (`MainWindow.Talk.cs`,
`.Voice.cs`, `.Edits.cs`, `.Focus.cs`, `.Updates.cs`, `.Accessibility.cs`). Phase 1 only writes this down as a rule
(`BOO-ARCH-007`: a new feature in `MainWindow` goes in its own `MainWindow.<Feature>.cs`); moving files into
folders is not worth the churn now.

### 2.5 Comment policy (ban ~99% of comments, for new code)

poteto's point: agents use comments to justify a workaround ("retry because X is flaky") instead of fixing it.
Boo's sample shows something different too: many of the 374 non-doc comment lines in `src/` state **invariants**
("user-info URLs are never offered", "the end event is raised only once"). Those are rules that belong in a test
or a type, not in a comment. So: forbid new ones, and convert old ones into checks over time.

**Allowed (everything else fails `BOO-CMT-001`):**

| Allowed form | Example | Why |
| --- | --- | --- |
| `///` XML doc on a type or member | `/// <summary>Returns …</summary>` | Public API docs; shows up in tooltips. |
| `// TODO(#123): …` | must link a real GitHub issue number | Debt is tracked, not hidden. A check verifies the issue exists and is open (weekly job). |
| `// SECURITY: … see <TestClass>.<TestName>` | `// SECURITY: user-info URLs never offered, see SecurityHardeningTests.UserInfoUrls` | Invariant with its proof. A check verifies the named test exists. |
| `// INVARIANT: … see <TestClass>.<TestName>` | same idea, for non-security rules | Same. |
| License headers | (none today) | Legal. |

The one existing `/// TODO(talk lane)` has no issue link; it is baselined and fixed in C11.
| XAML `<!-- -->` and `.csproj` comments | 17 today | Out of scope for now; counted by the ratchet. |

**Enforcement:**
- `scripts/quality/comment_lint.py --diff origin/<base>...HEAD`: looks only at **added** lines in `src/**/*.cs`
  and `tools/**/*.cs`. Any added `//` or `/* */` that is not in the allowed list fails with:
  `BOO-CMT-001 src/X.cs:42 new comment. If it explains a workaround, fix the cause instead. If it states a rule,
  write a test and use "// INVARIANT: … see Test.Name". If it documents an API, use ///.`
- `ratchet.py` also counts total non-doc comment lines (374 today in `src/`); the number may only go down.
- `tests/` is not linted for comments in Phase 1 (frozen, and test comments often explain the scenario).

### 2.6 Prose rules turned into CI checks

| Id | Check | File | Runs where |
| --- | --- | --- | --- |
| `BOO-SAFE-001` | **No real-screen capture.** (a) Workflow lint: any job that runs FlaUI tests must run on `windows-latest` or a runner carrying a `boo-sandbox` label (a dedicated VM or a separate Windows user, never Vikas's own session). A bare `self-hosted` fails. (b) Runtime guard in `tools/Boo.LiveSmoke` and `tools/Boo.VerifyLive`: whole-desktop capture throws unless `RUNNER_ENVIRONMENT=github-hosted` or `BOO_SANDBOX=1`. (c) The same guard in the 3 frozen fixtures under `tests/` needs a reviewed oracle revision (`--rebaseline`), done as its own PR. (d) Change `live-ai-smoke.yml` default `runner` from `self-hosted` to `windows-latest`. | `scripts/quality/workflow_lint.py`, `tools/Boo.LiveSmoke/CaptureGuard.cs` | `quality-gates` (Linux) + every FlaUI run |
| `BOO-SAFE-002` | **Key scan everywhere.** Pattern scan (`sk-`, `sk-proj-`, `sk-ant-`, `AIza` prefixes plus high-entropy strings) on the PR diff, and on every evidence folder right before upload in `windows-ci`, `demo-ci` and `live-ai-smoke` (today only `live-ai-smoke`). Prints file and line, **never the match**. | `scripts/quality/key_scan.py` | `quality-gates` + before each upload |
| `BOO-TEST-001` | **No skips in tools.** `test_gate.py static` also scans `tools/*` test projects. | `scripts/test_gate.py` (a tooling file, not frozen) | `windows-ci` |
| `BOO-EVID-001` | **Evidence table present.** PR body must contain a table with a label column whose values are only `live`, `mock`, `manual`, `UNVERIFIED`, `BLOCKED`, `review`; every `live` row must have a GitHub Actions run link; a PR touching `src/` or `tools/` needs at least one `live` row or an explicit `BLOCKED` row with a reason. | `scripts/quality/pr_evidence.py` | `quality-gates` on `pull_request: [opened, edited, synchronize]` |
| `BOO-LINT-002` | **Warnings cannot be switched off.** Fails on new `NoWarn`, `WarningsNotAsErrors`, `TreatWarningsAsErrors>false`, `#pragma warning disable`, `[SuppressMessage]` outside `AnalyzerBaseline.cs`. | `scripts/quality/no_suppress.py` | `quality-gates` |
| `BOO-GATE-001` | **Gates cannot be loosened quietly.** `scripts/quality/**`, `src/BannedSymbols.txt`, `docs/quality/baseline.json`, `docs/architecture/layers.json`, `.editorconfig` hash to `docs/quality/gate-manifest.json`; changing them needs a `--rebaseline` with a reason (same idea as the test oracle). A baseline number **going down** is always allowed. | `scripts/quality/gate_manifest.py` | `quality-gates` |

All of these run in one new workflow, `.github/workflows/quality-gates.yml`, on `ubuntu-latest`
(1× minute cost, about 1 minute per run, no artifacts). Its job name `quality` becomes a required check.

### 2.7 Error messages that point at the root cause

Every check above prints the same 4-part shape, and the full text lives in `docs/quality/RULES.md` under an
anchor per rule id:

```
BOO-UI-002  src/Boo.App/App.xaml.cs:210
  What:   Dispatcher.BeginInvoke (fire-and-forget onto the UI thread).
  Why:    Exceptions thrown inside are lost and the order is a guess.
  Fix:    await Dispatcher.InvokeAsync(...) or hand the operation to RunUiAction(...).
          Do NOT suppress this rule or add a delay; that hides the bug.
  Rule:   docs/quality/RULES.md#boo-ui-002
```

The "Do NOT" line names the tempting wrong fix, because that is what an agent reaches for next.

### 2.8 Cleaning up what is already there

New violations fail from the day each PR lands. Old ones are fixed one class per PR, and each PR lowers
`baseline.json`:

| Cleanup PR | From → to | How |
| --- | --- | --- |
| C1 async void | 7 → 0 | `RunUiAction(Func<Task>)` helper in `Boo.App` that awaits and reports to `CrashLogStore`; handlers become one-liners. `StartPushToTalk` becomes `Task`-returning. |
| C2 Dispatcher | 2 `Invoke` + 13 `BeginInvoke` → 0 | Switch to `InvokeAsync`; the 9 calls in `App.xaml.cs` (5 tray-menu, 4 system events) share one helper. |
| C3 Hidden clock | 14 → 0 | Inject `TimeProvider` (App 11, Windows 2, Core 1). Prerequisite for Phase 2 property tests. |
| C4 Delay-as-fix | 1 → 0 | `MainWindow.Edits.cs:382` retry loop waits for the UI Automation property-changed event (or a `TimeProvider` delay if no event exists, with a test). |
| C5 Static state | 2 → 0 | Instance fields owned by `App`. |
| C6 Empty catches | 24 → 0 | Each becomes either a narrow `catch (SpecificException)` or a call to a named `IgnoreBecause.…` method whose name states the reason, with a test. |
| C7 Handle-and-continue catches | 73 → ratchet | Narrow the type where the thrown exceptions are known; the rest stay baselined. |
| C8 Env vars, Process.Start, Core leaks | 3, 3, 1+2 | `BooEnvironment`; route through `DesktopTools`; move `HttpRequestException` mapping into Providers; `IFileSystem` for Core. |
| C10 Mock server warnings | `QaMockAi` `TreatWarningsAsErrors=false` → removed | Fix its warnings, delete the opt-out. |
| C11 Untracked TODO | 1 → 0 | Turn `NotchWindow.xaml.cs:126` into a GitHub issue and a `// TODO(#N)` line. |
| C9 Comments | 374 → ratchet | Convert invariant comments into tests + `// INVARIANT:` tags; delete the rest when the code nearby is touched. |

Each cleanup PR is **behaviour-preserving** and must keep `demo-ci` and the live journeys green.

### 2.9 Phase 1 PR breakdown (in order)

| # | PR | Files | Size | Done when |
| --- | --- | --- | --- | --- |
| 0 | (prerequisite) Merge PR #15, artifact retention 7 days, and wait for storage to recalculate | workflows | exists | `windows-ci` reaches UI smoke and perf again on one run. **Already met** (run 43287422701, 13 Nov); PR #15 is still worth merging for retention |
| 1.1 | Quality workflow + ratchet + rule catalog | `.github/workflows/quality-gates.yml`, `scripts/quality/ratchet.py`, `docs/quality/baseline.json`, `docs/quality/RULES.md` | S | Red proof: a throwaway commit adding `Thread.Sleep` makes `quality` fail with `BOO-ASYNC-001`; green on the real head |
| 1.2 | Architecture refs | `docs/architecture/layers.json`, `scripts/quality/arch_refs.py` | S | Red proof: adding `Boo.Windows → Boo.Providers` fails |
| 1.3 | Banned APIs + canary + baselines | `Directory.Build.props`, `src/BannedSymbols.txt`, `src/*/AnalyzerBaseline.cs`, `scripts/quality/canary/` | M | Canary shows one `RS0030` per banned line; real build green with only baselined hits |
| 1.4 | Threading + catch analyzers | `.editorconfig`, `Directory.Build.props`, baselines | M | Exact counts written into `baseline.json`; build green |
| 1.5 | No-suppress + gate manifest | `scripts/quality/no_suppress.py`, `scripts/quality/gate_manifest.py`, `docs/quality/gate-manifest.json` | S | Red proof for each |
| 1.6 | Comment lint | `scripts/quality/comment_lint.py` | S | Red proof: an added `// retry because flaky` fails |
| 1.7 | Key scan everywhere | `scripts/quality/key_scan.py`, 3 workflows | S | Red proof with a fake key-shaped string in a temp file; no real key involved |
| 1.8 | No-real-screen guard (tools + workflow lint + live-smoke default runner) | `tools/Boo.LiveSmoke/CaptureGuard.cs`, `scripts/quality/workflow_lint.py`, `live-ai-smoke.yml` | S | Red proof: guard throws when `RUNNER_ENVIRONMENT` is not `github-hosted`; live smoke still green on `windows-latest` |
| 1.9 | Same guard in the 3 frozen fixtures | `tests/*/BooAppFixture.cs`, `PerfAppFixture.cs`, oracle `--rebaseline` | S | Next oracle revision (21 or later; PR #12 used 20) with reason; reviewer approves the exact 3 files |
| 1.10 | Evidence-table check + tools in static test gate | `scripts/quality/pr_evidence.py`, `scripts/test_gate.py` | S | A PR body without a table fails `quality` |
| 1.11 | NetArchTest project | `tests/Boo.Architecture.Tests/`, oracle `--extend` | M | 6 rules, baselined counts match section 1.5 |
| 1.12 | Trust-metrics baseline (start measuring now, see Phase 3) | `scripts/quality/trust_metrics.py`, `docs/quality/trust-metrics.md` | S | First table published with numbers from git + Actions history |
| C1–C9 | Cleanups (2.8) | product code | S each | Each lowers `baseline.json`; demo-ci + live journeys green |

**Effort:** 1.1–1.12 is about 12 small PRs, roughly 5–7 working days of agent time including separate review.
Cleanups C1–C9: about 2 weeks at low priority, one PR at a time, interleaved with feature work.

**Risks:**
- Quota red hides real red. On 12 Nov `windows-ci` stopped at the upload step and UI smoke / perf never ran; it runs
  again since the repo went public. If storage fills again, every Phase 1 PR must say so in its evidence table, not
  call it green.
- Analyzer noise. `CA1031` and `VSTHRD*` may report more than section 1.5 counted. Mitigation: baseline file, measure
  first (PR 1.4), never `AnalysisMode=All`.
- Banned-API overloads drift with .NET versions. Mitigation: the canary.
- Frozen tests. Anything under `tests/` needs an oracle revision; plan those PRs separately (1.9, 1.11).
- Comment lint false positives (URLs in strings, `//` inside verbatim strings). Mitigation: Roslyn-based trivia
  scan if the regex version misfires more than twice.

**How it is verified (Vikas's pipeline):** every PR has (1) a **red proof** run showing the new gate fails on a
bad sample, plus a green run on the real head; (2) a **separate reviewer** agent that never saw the author's
reasoning; (3) the **evidence table** with live/mock/manual/UNVERIFIED labels and run links; (4) **no skips**
(`test_gate.py`, `assert-tests-ran.ps1`). Gate-only PRs (1.1, 1.2, 1.5–1.7, 1.10, 1.12) do not touch the app, so
their live row is `BLOCKED: not applicable, no app change` — the reviewer confirms that. PRs that touch `src/` or
`tools/` (1.3, 1.4, 1.8, C1–C9) need a **live real-key OpenAI smoke** run on `windows-latest` with the journeys.

**CI cost:** `quality-gates` on `ubuntu-latest`, about 1 minute per run, 1× billing, no artifacts. Analyzers add
an estimated 20–60 s to the existing Windows build (estimate; measured in PR 1.3). Canary build: about 30 s on
Linux (it can target `net10.0` only for non-WPF lines; WPF lines are built in the Windows job). Storage: 0 new.
Optional saving worth more than all of this: run `windows-ci` / `demo-ci` on `push` only for `lane/face` and `main`,
and on `pull_request` for PRs, which halves today's paired runs (decision for Vikas).

**Done when:**
- `quality` is a required check on `lane/face` and `main`;
- every rule in 2.2–2.6 has a red-proof run linked in `docs/quality/RULES.md`;
- `baseline.json` exists, and at least C1–C5 have landed (async void, Dispatcher, clock, delay, static state at 0);
- `live-ai-smoke` defaults to `windows-latest`, and no FlaUI job can run on a bare `self-hosted` runner.

---

## 3. Phase 2: verification (runtime + formal)

Goal: prove behaviour, not just shape. The notch lifecycle gets a model that a checker explores exhaustively,
the code gets random-sequence tests, and every demo/live run produces a video a reviewer can watch.

### 3.1 The notch lifecycle today (why a model helps)

The lifecycle is spread across code-behind and several small Core pieces:
- `NotchFaceState` (10 values: `Hidden, Hover, Idle, Listening, Thinking, Responding, Capture, Nudge, Paused, Happy`) in `src/Boo.Core/NotchSpec.cs:15`;
- `RevealPhase` (5 values: `Hidden, Revealing, Revealed, RetractPending, Retracting`) in `src/Boo.Core/NotchFace.cs:285`;
- `PresenceController` (3 flags: pointer inside, capture active, microphone active → `IsRevealed`) in `src/Boo.Core/PresenceController.cs`;
- `FocusSessionController`, `HoldToTalkController`, `UndoJournal` / `FileUndoJournal`, `PointerPhase` (6 values), `RequestCoordinator` (request generations);
- and the glue in `MainWindow.xaml.cs` (2,674 lines) and `NotchWindow.xaml.cs` (1,489 lines).

Several comments in the glue state exactly the invariants a model would check, for example
`MainWindow.Focus.cs:42` "The end event is raised only once" and `MainWindow.xaml.cs:2280` "cannot leave the notch
stuck on Responding". Today those are promises in prose.

### 3.2 Deliverables

**A. Statechart (picture first).** `docs/architecture/notch-lifecycle.md`: a diagram of the states and events as the
code behaves **today**, written by reading the code, checked by a reviewer against the app.

States: `Hidden`, `Hover` (peek), `Awake` (idle, panel ready), `Chat` (thinking → responding), `Listening`
(push-to-talk held), `Capture` (screen explain running), `Interrupt` (Stop pressed; transient), `Focus`
(running / paused / nudge / ended), `ApplyPending` (proposal shown, waiting for Approve), `Applied`, `Undone`.
Events: `PointerEnter`, `PointerLeave`, `Hotkey`, `Send`, `Reply`, `ReplyFailed`, `TalkDown`, `TalkUp`,
`CaptureStart`, `CaptureEnd`, `Stop`, `FocusStart/Pause/Resume/End`, `Tick`, `Propose`, `Approve`, `Reject`,
`Undo`, `ApproveUndo`.

**B. TLA+ spec.** `specs/NotchLifecycle.tla` + `specs/NotchLifecycle.cfg`, checked by TLC
(`tla2tools.jar`, pinned version and SHA-256, run with Java on `ubuntu-latest`). Invariants to check:

| Id | Invariant (plain words) | Kind |
| --- | --- | --- |
| `INV-PRIV-1` | If the microphone or screen capture is active, the notch is visible (the user can always see Boo is listening or looking). Mirrors `PresenceController.IsRevealed`. | safety |
| `INV-PRIV-2` | No capture starts while the notch is `Hidden` and no capture is approved. | safety |
| `INV-APPLY-1` | A file change is applied only after an explicit `Approve` of **that** proposal (same id). | safety |
| `INV-APPLY-2` | `Undo` only targets something that was applied, and only once; after `Apply(p)` then approved `Undo(p)`, the workspace equals the state before `Apply(p)`. | safety |
| `INV-APPLY-3` | A new request never applies a proposal from an older request generation. | safety |
| `INV-FOCUS-1` | Each focus session emits exactly one end event. | safety |
| `INV-TALK-1` | After `TalkUp` (or `Stop`), the microphone is off. | safety |
| `INV-STOP-1` | `Stop` from any state reaches a quiet state (`Awake` or `Hidden`) with mic and capture off. | safety |
| `LIVE-1` | `Chat` always ends: `Responding ~> (Awake ∨ Hidden)` under weak fairness of `Reply`/`ReplyFailed`/timeout. "Never stuck on Responding." | liveness |
| `LIVE-2` | `Hover` without further input returns to `Hidden`. | liveness |

**C. Pure reducer in Core.** `src/Boo.Core/NotchLifecycle.cs`: `(State, Event) → (State, Effects)`, no WPF, no
clock (takes a `TimeProvider`), plus `NotchLifecycleTests` (new file, oracle `--extend`). Then the code-behind
calls the reducer instead of flipping flags itself (done in steps, each behaviour-preserving).

**D. Conformance bridge.** TLC dumps the reachable state graph (`-dump dot`). A test walks every edge up to a
bounded depth through the C# reducer and checks the C# state maps to the TLA+ state. If the code and model
drift, this test goes red.

**E. Property-based tests (FsCheck).** New project `tests/Boo.Property.Tests/` (`FsCheck.Xunit`, oracle `--extend`):

| Property | Generator | Checks |
| --- | --- | --- |
| Lifecycle invariants | random event sequences (length 1–200) from the event list above, with a fake `TimeProvider` | every `INV-*` above after every step |
| Apply/Undo round trip | random sequences of propose / approve / reject / apply / undo on a temp copy of `tests/fixtures/native/Boo.QaWorkspace` | undoing everything gives byte-identical files; undo never touches a path outside the workspace; double undo is a no-op; `UndoJournal` and `FileUndoJournal` agree |
| Focus session | random start / pause / resume / end / tick sequences | exactly one end event; elapsed time never negative; paused time not counted |
| Hold-to-talk | random key down/up interleavings, including the superseded-hold case at `MainWindow.Talk.cs:278` | mic never left open after the last release |
| Pointer | any `PointerClearReason` from any `PointerPhase` | ends in `Clear`/`Idle` |

Rules: fixed seed per CI run printed in the log; a failure prints the **shrunk** sequence and the seed; the fix PR
must add that sequence as a plain regression `[Fact]` ("every correction becomes a deterministic check").
`FuzzKit.cs` stays for the existing seeded fuzz tests; FsCheck adds shrinking, which `FuzzKit` lacks.
Case counts: 200 per property on PRs, 20,000 nightly.

**F. Video of demo and live runs.** `scripts/ci/Record-Desktop.ps1` records the runner desktop with `ffmpeg`
(`gdigrab`, 10 fps, 1280 px wide, H.264 CRF 35) for the whole FlaUI step; one MP4 per run uploaded as
`demo-video` / `live-video` with `retention-days: 7` and a hard cap (refuse to upload above 25 MB, log the size).
It **refuses to start** unless `RUNNER_ENVIRONMENT=github-hosted` or `BOO_SANDBOX=1` (`BOO-SAFE-001`), so it can
never record Vikas's own screen. The PR template links the video next to the screenshots.

**G. Agent-facing driver (the "custom CLI driver").** `tools/Boo.Drive/`: a small command-line tool that drives the
published `Boo.exe` one step at a time and prints JSON: `boo-drive start`, `hover`, `hotkey`, `say "<text>"`,
`approve`, `undo`, `stop`, `state` (reads the notch face state through UI Automation), `shot <name>`.
It reuses the existing FlaUI fixture and the same capture guard. Agents use it to reproduce a bug before
fixing it; Phase 3's fuzz uses it as its action vocabulary. Boo already has `--notch-gallery`,
`BOO_PERF_DIAG` and `BOO_QA_CAPTURABLE` as hooks.

**H. verify-boo required on every PR.** After PR #13 is fixed (j3) and merged:
- **mock journeys** (no key) run on every PR as part of `demo-ci` (already required as `demos`);
- **live journeys** run automatically on `pull_request` for same-repo branches that touch `src/`, `tools/` or
  `.github/` (not forks; secrets never reach forks), once per head SHA, and report a status `live-journeys`;
- `live-journeys` becomes a **required** check on `lane/face` and `main`;
- `Test-FeatureMap.ps1` becomes part of `quality-gates` (the map cannot drift).

### 3.3 Phase 2 PR breakdown (in order)

| # | PR | Size | Done when |
| --- | --- | --- | --- |
| 2.1 | Statechart doc of today's behaviour | S | Reviewer confirms each transition against code and one live run video/screens |
| 2.2 | TLA+ spec + TLC job (`specs/`, `quality-gates` step, path-filtered + nightly) | M | TLC passes all invariants; red proof: a deliberately broken spec (Apply without Approve) is caught with a counterexample trace |
| 2.3 | `TimeProvider` everywhere (= cleanup C3, if not done in Phase 1) | M | `BOO-TIME-001` baseline 0 |
| 2.4 | `NotchLifecycle` reducer in Core + unit tests | M | Tests green; no app wiring yet |
| 2.5 | FsCheck project: lifecycle + Apply/Undo + focus + talk + pointer properties | M | 5 properties green at 200 cases; nightly 20,000 green; red proof: a planted bug in a scratch branch is found and shrunk |
| 2.6 | Wire the app to the reducer, one feature per PR (talk, focus, apply/undo, presence) | 4 × M | Each: demo-ci, UI smoke, live journeys green; video attached |
| 2.7 | Conformance bridge (TLC graph → reducer) | M | Green; red proof: renaming a transition in the reducer fails it |
| 2.8 | Video recording in `demo-ci` and `live-ai-smoke` | S | Video artifact under 25 MB on 3 runs; guard refuses on non-hosted runners |
| 2.9 | `tools/Boo.Drive` CLI | M | An agent reproduces j5 (Apply/Undo) step by step from the CLI with screenshots |
| 2.10 | `live-journeys` required check | S | Branch protection shows it; a PR without a live run cannot merge |

**Effort:** about 13 PRs, 3–4 weeks. The big one is 2.6 (moving behaviour out of 4,163 lines of code-behind);
everything else is additive.

**Risks:**
- Extracting the lifecycle can change behaviour users feel (timing of reveal/retract, focus nudges). Mitigation:
  one feature per PR, demo-ci + live journeys + video on each, and the statechart doc agreed **before** any move.
- The model can drift from the code. Mitigation: the conformance bridge (2.7).
- Live journeys as a required check can be flaky (network, provider latency, j3 already failed 2 of 3).
  Mitigation: one automatic retry, both runs logged, and a flaky counter in trust metrics; j3 must be fixed first.
- Cost of live runs on every PR (see below). Mitigation: path filter, once per head SHA, not per push.
- `ffmpeg` on the runner: verify availability in PR 2.8; otherwise download a pinned build with a SHA-256 check.
- Storage again: video adds up. Mitigation: 7-day retention, 25 MB cap, upload video only for failed runs if the
  quota gets tight.

**How it is verified:** same pipeline as Phase 1 (separate reviewer, evidence table, no skips), plus for every PR
touching `src/`: live real-key OpenAI journeys on `windows-latest` with the **video** linked in the evidence
table. Formal and property PRs each include a **red proof** (the checker catches a planted bug).

**CI cost:**
- TLC: about 2–5 min on `ubuntu-latest` (1×), only when `specs/` or `NotchLifecycle*` change, plus nightly.
- FsCheck at 200 cases: estimated +30–90 s on the existing Windows test step (2× billing on private repos); nightly 20,000 cases
  on Linux (the property project targets `net10.0`): about 5–10 min.
- Video: estimated 3–10 MB per demo run, 5–15 MB per live run (estimate; measured in PR 2.8). With 7-day
  retention, roughly 10 PR pushes a day and today's paired push/PR runs, that is about 0.4–1.4 GB held at any time
  for demo videos alone (half that if decision 6 drops the paired runs) — **this only fits once the
  `boo-win-x64` uploads are fixed** (they are ~84 MB each today).
- Live journeys: 10–14 min on `windows-latest` per run (from PR #13's runs), which bills as 20–28 min on a
  private repo (the repo is public by 13 Nov, where GitHub's standard hosted runners are free), plus OpenAI usage per run. At one live run per PR head, 20 PRs a month ≈ 400–560 billed minutes.

**Done when:**
- TLC proves all `INV-*` and `LIVE-*` on every change to the model or reducer;
- the app's talk, focus, apply/undo and presence paths go through `NotchLifecycle`;
- 5 FsCheck properties run on every PR and nightly, and every past failure has a regression `[Fact]`;
- demo and live runs attach a video;
- `live-journeys` is a required check and the feature map has 0 `UNVERIFIED` rows for features marked shipped.

---

## 4. Phase 3: the outer loop (release-time fuzz)

Goal: before a release goes out, a robot uses the real installed Boo like a careless user for a long time, and
every problem it finds comes back as a reproducible bug with a fix PR waiting for review.

### 4.1 Deliverables

- `tools/Boo.Fuzz/`: drives the **published** `Boo.exe` (from the release build, installed with the Velopack
  installer) through `tools/Boo.Drive` actions. Random walk weighted by the feature map (hover, hotkey, send,
  talk, explain, research, propose/approve/reject, undo, focus start/pause/end, Stop, settings changes, monitor
  change where possible). Mock AI by default (`tools/Boo.QaMockAi`, with scripted slow, failed and malformed
  replies); a small live budget (for example 10 real turns) at the end.
- **Oracles** (what counts as a bug): a new file in Boo's crash log (`CrashLogStore`); an unhandled exception; a
  notch state that breaks an `INV-*` (read through UI Automation); `Stop` not reaching quiet within 2 s; a reply
  stuck longer than the response deadline; a perf budget breach (`BOO_PERF_DIAG` counters); a key-shaped string in
  any log; Boo's process exiting.
- **Trace minimiser:** on a failure, replay with fewer steps (delta debugging) until the shortest sequence that
  still fails; save it as `repro.json` + video + screenshots + Boo's logs.
- `.github/workflows/release-fuzz.yml`: runs on tags `v*` (one of `release.yml`'s triggers; that workflow also runs
  on every PR, which the fuzz must not) before the release job publishes, plus nightly on `lane/face`. Budget: 30 min on 1 runner for nightly, 60 min for a release.
  The release job waits for it.
- **Fix-PR bot:** for each minimised failure it opens a GitHub issue (label `fuzz`), then a **draft** PR whose
  first commit is the repro as a failing test (FsCheck regression `[Fact]` if it maps to the reducer, otherwise a
  `tools/Boo.VerifyLive` journey) and whose second commit is the fix. Runs as a delegated agent; never merges.
- `scripts/quality/autofix_guard.py`: rules any bot PR must pass (below).

### 4.2 Guardrails before auto-merge is even discussed

In Phase 3, **nothing auto-merges.** These must all hold, enforced by `autofix_guard.py`, for a bot PR to be
reviewable at all:

1. **Red then green, proven by CI:** the new regression test fails on the base commit and passes on the PR head
   (two linked runs).
2. **Touches only allowed paths:** `src/**` and new test files; never `.github/**`, `scripts/quality/**`,
   `BannedSymbols.txt`, baselines, the test oracle, `AnalyzerBaseline.cs`, security code
   (`ActionPolicy.cs`, `CaptureExclusionPolicy.cs`, `CredentialStore.cs`) — those need a human-started PR.
3. **Small:** at most 150 changed lines and 5 files.
4. **No baseline goes up**, no new suppression, no new comment outside the allowed forms.
5. **All required checks green**, including `live-journeys` with video.
6. **Separate reviewer agent** approves, with the evidence table.
7. **Vikas approves** in GitHub.

Auto-merge would only be a question after: 20 bot PRs merged by hand in a row with 0 reverts, a flaky rate under
2% over 30 days, and only for one narrow class (for example "narrow a broad catch that the fuzz proved can throw").
That is a separate decision, not part of this plan.

### 4.3 Trust metrics to track (start in Phase 1, PR 1.12)

| Metric | How it is measured | Target to watch |
| --- | --- | --- |
| Revert rate | merged PRs into `lane/face`/`main` whose change was later reverted (`git log` "Revert" + PR links), per 30 days | trend down; above 5% means gates are missing something |
| Flaky rate | same commit SHA, same workflow, different result (re-runs and push/PR pairs in the Actions API) | under 2% |
| Escaped bugs | bugs found after merge by Vikas, a live run, or the fuzz; GitHub label `escaped`; each one records which layer should have caught it | trend down; every escape adds a check |
| Prose-to-check ratio | rules in `AGENTS.md`/prompts that have a matching `BOO-*` check | trend up |
| Baseline totals | sum of `baseline.json` | strictly down |
| Proof coverage | feature-map rows by label (live / mock / manual / UNVERIFIED) | `UNVERIFIED` → 0 for shipped features |
| Time to green | PR open → all required checks green | watch, no target |
| Fuzz yield | new minimised failures per release / per nightly hour | expect high at first, then falling |

Published as a weekly table in `docs/quality/trust-metrics.md` and as a visual page for Vikas.

### 4.4 Phase 3 PR breakdown (in order)

| # | PR | Size | Done when |
| --- | --- | --- | --- |
| 3.1 | `tools/Boo.Fuzz` random walk + oracles, mock AI only, local + manual workflow | M | Runs 30 min on `windows-latest`; red proof: a planted crash is found |
| 3.2 | Trace minimiser + repro bundle | M | Planted bug's trace shrinks to under 10 steps |
| 3.3 | `release-fuzz.yml` nightly on `lane/face` | S | 7 nightly runs logged; flaky rate measured |
| 3.4 | Issue + draft fix-PR bot, `autofix_guard.py` | M | One real fuzz finding goes issue → draft PR → human review → merge |
| 3.5 | Gate releases on fuzz | S | `release.yml` waits for `release-fuzz`; a red fuzz blocks a tag release |

**Effort:** about 5 PRs, 2–3 weeks, then at least 4–6 weeks of watching the metrics before any talk of auto-merge.

**Risks:**
- False alarms from the runner (focus stealing, slow VMs). Mitigation: every oracle failure is re-run once from
  the repro; only reproducible failures open issues.
- Bot PRs that "fix" the test instead of the bug. Mitigation: guardrails 1, 2 and 4; frozen oracle; separate reviewer.
- A bot with write access. Mitigation: a fine-grained token limited to issues + pull requests on this repo; no
  `contents: write` on protected branches; branch protection with `enforce_admins`.
- Cost creep. Mitigation: fixed time budget per run; nightly can be turned down to weekly.
- Real AI spend in the live tail. Mitigation: hard cap on turns; key scan; the fuzz never types a key.

**How it is verified:** the fuzz itself is verified with planted bugs (red proofs) before it is trusted; each bot
PR goes through the same pipeline (separate reviewer, evidence table, live real-key journeys with video, no skips)
plus guardrails 1–7.

**CI cost:** (billed minutes apply only if the repo is private; it is public by 13 Nov) nightly 30 min on `windows-latest` = 60 billed min/day ≈ 1,800 billed min/month (90% of GitHub
Free's 2,000 included minutes on its own, if the account is on Free; the plan was not checked), so nightly may need
to be weekly, about 240 billed min/month; a release
run 60 min = 120 billed min. Storage: only failing runs upload (repro + video, about 10–30 MB each), 7-day
retention. OpenAI: capped live turns per run.

**Done when:**
- every tagged release has a linked fuzz run, and a red fuzz blocks the release;
- at least one real finding has gone fuzz → issue → draft PR → human-approved merge;
- the trust-metrics page has 8 weeks of data;
- the auto-merge question is answered with data, not opinion.

---

## 5. Recommended order

1. **Keep storage healthy** (merge PR #15). By 13 Nov the repo is public and the UI/perf layer runs again; on
   12 Nov every CI result was red for the wrong reason.
2. **Stop the real-screen risk** (PR 1.8: live smoke default to `windows-latest`, capture guard in tools).
   Small, and it closes a privacy gap today.
3. **Phase 1 gates 1.1 → 1.7, 1.10–1.12**, then cleanups C1–C5 (they unblock Phase 2).
4. **Fix j3 and merge PR #13** (verify-boo), so the feature map and live journeys exist on `lane/face`.
5. **Phase 2** in the order of 3.3; video (2.8) can go early because it helps every review.
6. **Phase 3** only after Phase 2's `live-journeys` check has been required and stable for 2 weeks.

## 6. Decisions Vikas must make

Each has a recommendation. Asked one at a time.

| # | Decision | Recommendation |
| --- | --- | --- |
| 1 | Live smoke default runner: keep "your own PC" (`self-hosted`) or switch to GitHub's VM (`windows-latest`)? | **Switch.** The own-PC runner takes full-desktop screenshots of your real screen. Use a separate VM or Windows user later if you want a local runner. |
| 2 | Start Phase 1 now (after storage is fixed)? | **Yes.** It is cheap (about 1 Linux minute per run) and stops repeat mistakes immediately. |
| 3 | Comment policy: block **new** non-doc comments and ratchet old ones, or delete old ones too? | **Block new, ratchet old.** Many old comments are real rules that should become tests first. |
| 4 | Turn on `enforce_admins` and require 1 approving review on `lane/face` and `main`? | **Yes.** Today an admin token can push past the checks. |
| 5 | Live journeys required on every PR, or only PRs touching `src/`, `tools/`, `.github/`? | **Only those paths, once per head SHA.** Docs-only PRs do not need a paid live run. |
| 6 | Run `windows-ci`/`demo-ci` on `push` only for `lane/face`/`main` (PRs use `pull_request`), and stop `release.yml` running on every PR? | **Yes.** Halves Windows minutes, and drops the ~267 MB of installer + ZIP uploaded per PR push. |
| 7 | Formal model: TLA+ for the notch lifecycle only, or skip formal and rely on FsCheck? | **TLA+ for the lifecycle only.** It is the one place with real concurrency (talk, capture, replies, Stop). |
| 8 | Fuzz cadence: nightly or weekly? | **Weekly + every release** until the minutes budget is known (nightly alone was about 1,800 billed min/month while the repo was private). |
| 9 | Auto-merge of bot PRs | **Not in this plan.** Revisit only with the data in 4.2. |
| 10 | Monthly budgets: GitHub plan minutes/storage and an OpenAI spend cap for live runs | Needed before Phase 2's required live check. |

---

## Appendix: how every number was counted

Run from the repo root on `f244e47`. `git grep -P` needs a PCRE-enabled git (Git for Windows has it).

```bash
# Projects, files, lines
git ls-files 'src/Boo.Core/*.cs' | wc -l                                   # 48
git ls-files 'src/*.cs' | xargs cat | wc -l                                # 22431
wc -l src/Boo.App/MainWindow.xaml.cs src/Boo.App/NotchWindow.xaml.cs       # 2674, 1489

# Test attributes per project
git grep -cE '^\s*\[(Fact|Theory)' -- 'tests/*.cs'

# Anti-patterns in src/ (swap src for tools or tests)
git grep -nP '\basync\s+void\b'                      -- 'src/*.cs'   # 7
git grep -nP '\bThread\.Sleep\s*\('                  -- 'src/*.cs'   # 0
git grep -nP '\bTask\.Delay\s*\('                    -- 'src/*.cs'   # 1
git grep -nP 'new\s+DispatcherTimer\b'               -- 'src/*.cs'   # 8
git grep -nP 'Dispatcher\.Invoke\s*\('               -- 'src/*.cs'   # 2
git grep -nP 'Dispatcher\.BeginInvoke\s*\('          -- 'src/*.cs'   # 13
git grep -nP 'Dispatcher\.InvokeAsync\s*\('          -- 'src/*.cs'   # 2
git grep -nP '\.Wait\s*\(|\.Result\b'                -- 'src/*.cs'   # inspect each: 0 blocking task waits
git grep -nP 'GetAwaiter\(\)\.GetResult\(\)'         -- 'src/*.cs'   # 0
git grep -nP 'catch\s*\(\s*(System\.)?Exception\b'   -- 'src/*.cs'   # 118
git grep -nP '^\s*catch\s*$|catch\s*\{'              -- 'src/*.cs'   # 16
git grep -nP '\bDateTime(Offset)?\.(Now|UtcNow)\b'   -- 'src/*.cs'   # 14
git grep -n  'Environment.GetEnvironmentVariable'    -- 'src/*.cs'   # 3
git grep -nP 'Process\.Start\s*\('                   -- 'src/*.cs'   # 3
git grep -nP '\bGC\.Collect\s*\('                    -- 'src/*.cs'   # 0
git grep -nP 'Capture\.Screen\(\)'                   -- tests tools  # 3 (tests only)
git grep -nE '#pragma warning disable|SuppressMessage' -- src tools tests   # 0

# Static mutable state: static fields/properties that are not readonly/const/get-only
git grep -n -w static -- 'src/*.cs' | grep -vE 'static (readonly|class|partial|extern|async)|\bconst\b|using static|operator|=>' \
  | awk -F: '{l=$0; sub(/^[^:]*:[^:]*:/,"",l); split(l,a,"="); if (a[1] !~ /\(/ && (l ~ /;[ \t]*$/ || l ~ /=/ || l ~ /set;/)) print $0}'
# -> 12 lines; 10 are get-only static properties initialised once; 2 are mutable (AccessibilitySettings.cs:17, App.xaml.cs:19)

# Cross-layer
git grep -nE 'System\.Net\.Http|Directory\.|File\.(Read|Write|Open|Exists|Delete|Move|Copy)' -- 'src/Boo.Core/*.cs'
git grep -nE 'HttpClient|ClientWebSocket' -- 'src/Boo.App/*.cs'
git grep -nE 'System\.Windows\.(Controls|Shapes)' -- 'src/Boo.Windows/*.cs'   # 0
```

Broad-catch split (24 empty / 73 handle-and-continue / 37 rethrow) and the comment counts (1,291 `///`, 333
full-line `//`, 35 trailing `//`; the 6 inline `/* */` come from `git grep -nP '/\*' -- 'src/*.cs'`; 62–65 with a
"reason" word) came from a small script that matches each
`catch` to its block and classifies the body, and walks every line of `src/**/*.cs` outside string literals. The
"reason" words: workaround, work around, hack, kludge, for now, temporar*, until, otherwise, so that, so the,
must not, never, avoid, race, flak*, retry, because, guard, in case, instead, without this, do not, don't, can't,
cannot. It is a heuristic for sizing, not a verdict on any single comment.

CI numbers: `gh run list --workflow <file> --limit 12`, `gh run view 43245893674 --json jobs`,
`gh api repos/vikas53953/bluey/actions/artifacts --paginate`, `gh api repos/vikas53953/bluey/branches/lane%2Fface/protection`.
