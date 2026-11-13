# Boo — project handoff

**Read this first.** It is written so that any agent or person can pick up the project cold. Every fact here comes
from the repository, its pull requests or its Actions runs, with a link. Anything not proven is marked **UNVERIFIED**.

**Keep it true.** Every PR must update the [Current status](#7-current-status) section of this file and add any new
decision to [docs/DECISIONS.md](docs/DECISIONS.md). See [CONTRIBUTING.md](CONTRIBUTING.md).

Status last updated: **13 Nov 2026, after PR #37 merged**, with `lane/face` at `34473d6` (PR #37; PR #31 at `a3ba89e` and PR #33 at `2433397` before it); PR #34 (agent-trust Phase 1 PR 1.10, evidence-table check) synced on top.

---

## 1. What Boo is

Boo is a local Windows desktop assistant. A small "notch" with two eyes sits hidden at the top centre of the screen;
hovering the top edge or pressing **Ctrl+Alt+B** wakes it. It can:

- explain what is on screen (screenshot → AI → spoken/written answer, with a pointer and ring on the element);
- hold a voice conversation (hold **Ctrl+Alt+Space** or hold the eyes; OpenAI Realtime);
- do small requested tasks on the PC (edit a field, write a file) **only after the user presses Approve**, then read the
  result back, with a local **Undo**;
- research with source links;
- run focus sessions (default 30 min in the app; the acceptance test uses 25 min) that screenshot about every 30 s and
  nudge when you drift.

User-facing docs: [README](README.md) (how to try), [docs/USER_GUIDE.md](docs/USER_GUIDE.md),
[docs/DEMO_SCRIPT.md](docs/DEMO_SCRIPT.md). Older detailed status: [docs/STATUS.md](docs/STATUS.md) (last full update
8–10 Nov; this file supersedes it for current status).

Owner: **Vikas** (repo `vikas53953/bluey`, private). Agents build; Vikas decides what ships.

## 2. Architecture and project map

C# / WPF on **.NET 10** (SDK pinned in [global.json](global.json): `10.0.403`, `rollForward: disable`). Windows only.
Why not Electron: see [docs/DECISIONS.md](docs/DECISIONS.md).

### In `Boo.sln` (built by `dotnet build Boo.sln`)

"net10.0-windows" below is short for `net10.0-windows10.0.19041.0`.

| Project | Target | Plain-words role |
| --- | --- | --- |
| `src/Boo.App` | net10.0-windows | The app: WPF windows, notch and eyes, main panel, tray, hotkeys. References all others. Publishes as `Boo.exe`. |
| `src/Boo.Core` | net10.0 | Rules with no Windows dependency: requests, focus sessions, approvals, face/notch spec, provider choice, update decisions, file scoping. Most unit tests target this. |
| `src/Boo.Windows` | net10.0-windows | Windows plumbing: screen capture, audio, display geometry/DPI, UI Automation, input. |
| `src/Boo.Providers` | net10.0 | AI clients: OpenAI (Responses, Realtime voice), Anthropic Claude, Google Gemini. |
| `src/Boo.Storage` | net10.0-windows | Session history (`sessions.db`), settings, DPAPI-protected key files. |
| `tests/Boo.Core.Tests` | net10.0 | Unit tests for Boo.Core (1719 on 12 Nov). |
| `tests/Boo.Integration.Tests` | net10.0-windows | Cross-project tests (148). |
| `tests/Boo.Providers.Tests` | net10.0 | Provider clients against in-memory HTTP, no key (115). |

### Not in `Boo.sln` (built in their own CI step)

| Project | Plain-words role |
| --- | --- |
| `tests/Boo.Ui.Tests` | FlaUI smoke tests that drive the **published** `Boo.exe` on a real desktop (15 tests). Needs `BOO_UI_EXE`. |
| `tests/Boo.Perf.Tests` | Idle CPU / memory / frame-rate budgets on the published `Boo.exe` (`budgets.json`). |
| `tests/Boo.Demo.Tests` | 5 tests: Step 0 (settings and workspace setup) plus the four scripted demos (explain + ring, edit + undo, research, focus nudge) against the **mock** AI. Holds `BooAppFixture.cs`, the shared launch harness. |
| `tests/fixtures/native/Boo.QaWorkspace` | Small fixture app the edit/undo demo types into. |
| `tools/Boo.QaMockAi` | CI-only fake `api.openai.com` with scripted answers (every answer starts with `QA-`). |
| `tools/Boo.LiveSmoke` | One **real** AI round trip through the published `Boo.exe` (used only by `live-ai-smoke`). |
| `tools/Boo.LiveProbe` | Manual one-shot paid probe of the OpenAI `responses`, `decisions` or `realtime` path. Not used by CI. |

Other folders: `docs/plans` (implementation plans), `docs/verification` (evidence, native checklists, frozen-test
oracle), `docs/design` (`boo-notch-spec.json` drives the notch face), `docs/security`, `scripts/` (CI gates and
local helpers), `.github/workflows/` (CI).

Key environment variables: `BOO_DATA_DIR` (use a temp data folder instead of `%LocalAppData%\Boo`),
`BOO_QA_CAPTURABLE=1` (lets tests screenshot Boo's windows), `BOO_UI_EXE`, `BOO_UI_ARTIFACTS`.

## 3. Build, run and test

### Locally (Windows)

Prerequisite: .NET SDK `10.0.403` exactly. `scripts/build.ps1` expects it in `<canonical checkout>/.dotnet`; otherwise
use any install of that version and plain `dotnet`.

```powershell
dotnet restore Boo.sln
dotnet build Boo.sln -c Release --no-restore
dotnet test tests/Boo.Core.Tests -c Release --no-build
dotnet test tests/Boo.Integration.Tests -c Release --no-build
dotnet test tests/Boo.Providers.Tests -c Release --no-build
python -I scripts/test_oracle.py          # frozen-test oracle must say ORACLE_VERIFIED
python -I scripts/test_gate.py static     # no empty test files, no Skip =

# Published app, then run it
dotnet publish src/Boo.App/Boo.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/ci
artifacts\ci\Boo.exe                      # then Ctrl+Alt+B
artifacts\ci\Boo.exe --notch-gallery <dir> # renders every face state to PNG, offscreen, then exits
```

**Warning: run the UI, perf and demo suites only in CI or a VM.** Since PR #18 their fixtures copy the whole desktop
only on a GitHub-hosted runner; anywhere else they draw Boo's own windows only and refuse rectangle screenshots, so
those tests fail by design on a personal PC. Before PR #18 they took FULL-SCREEN screenshots (`Capture.Screen()` in
`tests/Boo.Ui.Tests/BooAppFixture.cs`). Never run them on the owner's real desktop. Run them in CI, a VM or Windows
Sandbox. To see Boo on the owner's PC, use `--notch-gallery` or capture only Boo's own window.

Known local snag: the SF-006 symlink tests in Boo.Core.Tests fail on purpose (they never skip) when the PC cannot
create symbolic links. Turn on Windows Developer Mode or run elevated.

### In CI (`.github/workflows/`)

| Workflow | Runs on | Job / check name | What it proves | Uses a real AI? |
| --- | --- | --- | --- | --- |
| `windows-ci.yml` | every push and PR (and manual) | `build-test-publish` | oracle + static gate → build → Core / Integration / Providers tests (+ trx gate) → publish → notch gallery → UI smoke (FlaUI) → perf budgets | no |
| `demo-ci.yml` | every push and PR (and manual) | `demos` | setup + the 4 demos (5 tests), end to end, against `Boo.QaMockAi` (hosts-file entry + a self-signed root certificate on the runner) | **no: mock** |
| `release.yml` | push to `main`, `v*` tags, PRs (and manual) | `package`, `github-release` | Core / Integration / Providers tests, then Velopack installer + portable ZIP; a `v*` tag publishes a GitHub Release | no |
| `live-ai-smoke.yml` | **every pull request** + manual (`workflow_dispatch`), **GitHub's `windows-latest` VM only** | `preflight`, `smoke`, **`live-ai-smoke` (required, PR #20)** | one real round trip: typed question → screenshot → real provider → answer in Boo. Fails closed without a key. Refuses any non-GitHub runner; evidence screenshots are Boo's windows only (PR #18). | **yes** |

Run the live smoke: GitHub → Actions → live-ai-smoke → Run workflow, `text_provider=openai`, and an optional `model`
override (blank = Boo's default). There is no runner choice: it always runs on GitHub's `windows-latest` VM, never on
a personal PC (`scripts/ci/check_runners.py` in windows-ci fails any workflow that could run elsewhere; the repo keeps
0 registered self-hosted runners). Or: `gh workflow run live-ai-smoke.yml --ref <branch> -f text_provider=openai`.
Full guide: [docs/LIVE_AI_SMOKE.md](docs/LIVE_AI_SMOKE.md).

### Required secrets (names only — never put values anywhere)

| Secret | Used by | State on 12 Nov |
| --- | --- | --- |
| `BOO_LIVE_OPENAI_KEY` | `live-ai-smoke` | set (created 12 Nov) |
| `BOO_LIVE_ANTHROPIC_KEY` | `live-ai-smoke` with `text_provider=anthropic` | not set |
| `BOO_LIVE_GEMINI_KEY` | `live-ai-smoke` with `text_provider=gemini` | not set |

No other secret is used; `release` publishes with the built-in `GITHUB_TOKEN`. On a PC, Boo keeps keys DPAPI-protected
in its data folder (`credential.bin`, `credential.anthropic.bin`, `credential.gemini.bin`). Never print, log or
screenshot a key; logs record lengths only.

## 4. Branch model and protection

- **`lane/face`** is the integration branch. Feature and fix branches open PRs **into `lane/face`**.
- **`main`** receives `lane/face` through **PR #3** only. This is a convention: protection does not require a PR.
- `claude/project-thread-*` and `lane/pointer` are old lane branches; their content is already in `lane/face`.
  (`claude/project-thread-549bgh` was squash-merged as PR #4 `384241a`, so its own commit SHAs are not in lane/face.)
- A PR must be **up to date** with its base to merge. After every merge into `lane/face`, the other open PRs show
  BEHIND and need a sync (see section 7).
- Branch protection, set 12 Nov on **both `lane/face` and `main`** (read back from the API):
  - required checks: **`demos`** (demo-ci) and **`build-test-publish`** (windows-ci), both from GitHub Actions
    (app id 15368); on **`lane/face` only**, since 13 Nov ~06:15 IST, also **`live-ai-smoke`** (app id 15368, read
    back after the change: `demos`, `build-test-publish`, `live-ai-smoke`, strict);
  - branch must be up to date before merging;
  - force pushes and branch deletion blocked;
  - no required approvals (Vikas is the only reviewer); admins not enforced (Vikas can override).
  - `live-ai-smoke` is a **required check on `lane/face`** (since PR #20 merged, `e1d4b5c`): it runs on every pull
    request, and its final always-run job is green only if the key check and the real round trip both succeeded
    (GitHub counts a skipped required job as passing). It is pinned to app id 15368, so no other app can post a
    passing status with that name. **`main` step still to do:** add the same pinned context to `main` protection only
    once `lane/face` (with the `pull_request` trigger) has reached `main` through PR #3; before that, `main` PRs would
    wait forever for a check their workflow cannot produce. Open PRs into `lane/face` whose branch predates PR #20 must
    sync first, or the check stays "Expected".
- **Merging, tagging and releasing stay with Vikas**, and agents never merge, tag, release, or push to `lane/face` or
  `main`. Exception, recorded by PR #18: since 13 Nov 12:15 AM IST the verifying bot (Grok Bot) merges PR #17, PR #18 and the
  agent-trust Phase 1–3 PRs once every gate passes; tags and releases stay with Vikas either way.

## 5. Vikas's pipeline (rules every agent follows)

1. **Branch + PR only.** Work on a new branch off `origin/lane/face`; open one PR into `lane/face`.
2. **Tests first, frozen.** Everything under `tests/` (plus `docs/verification/acceptance-*.md`,
   `docs/verification/public-contracts.md`, `src/Boo.Core/Contracts.cs`) is a frozen oracle, hashed in
   `docs/verification/test-oracle.json` and checked by CI (`python -I scripts/test_oracle.py`).
   - New test files: `python -I scripts/test_oracle.py --extend "<reason>"` (adds only; refuses if a frozen file changed).
   - Changing a frozen file: `--rebaseline --reason "<reason>" --accept-changed a,b` — only with a written reason, and
     the change must be reviewed. Never weaken a frozen test.
3. **No skipped tests.** `scripts/test_gate.py` fails on `Skip =`, empty test files, projects with no tests, and any
   run with zero or skipped tests. A missing-key test fails; it never skips to green.
4. **No self-grading.** After building, a **separate, fresh reviewer agent** (no shared context) checks the work and
   every claim against raw logs. Fix what it finds, then get a fresh review again.
5. **Live real-key round trip.** A mock-only green is a false green. Run `live-ai-smoke` with the real
   `BOO_LIVE_OPENAI_KEY` on the PR branch. If it cannot run, report **BLOCKED**, never green.
6. **Evidence table in the PR.** Every check or feature is marked **mock** / **live** / **manual** / **UNVERIFIED**,
   with a run link. No run link = UNVERIFIED. End with one line: `QA PASS — <full head SHA>` or `QA FAIL — <SHA>`.
7. **Never capture the owner's real screen.** Screenshots only of a test window, a VM or CI runner, or Boo's own
   offscreen renders. Enforced in code since PR #18: the FlaUI fixtures copy the whole desktop only on a GitHub-hosted
   runner and never in the live smoke (`BOO_CAPTURE_SCOPE=boo-windows`); anywhere else they draw Boo's own windows
   from their own content (`PrintWindow`, files `*.boo-<n>.png`) and refuse rectangle screenshots. demo-ci proves it
   with the explanation overlay open over a magenta decoy (`tools/Boo.CaptureProof`, zero decoy pixels). No workflow
   may use a self-hosted runner (`scripts/ci/check_runners.py`).
8. **Honest red.** If evidence cannot be uploaded (for example the artifact quota), the job stays red. Never add
   `continue-on-error` to uploads or tests to get a green.
9. **Secrets:** names only. Never print a key, write one to disk logs, or put one in a screenshot.
10. **Reports for Vikas** are short, plain words (no jargon), and anything he must review is a visual page.

## 6. Live QA scope (as of 12 Nov)

Only the **OpenAI** key is in use for live QA. **Claude (Anthropic), Gemini and voice are UNVERIFIED: not tested, by
choice.** Mark them that way, not as BLOCKED and not as green.

## 7. Current status

### Pull requests

PRs into `lane/face` must be **up to date** with it to merge, so every merge makes the others BEHIND. Sync by merging
`origin/lane/face` into the branch (no force push), then re-run CI and one live smoke on the new head.

| PR | Branch → base | State (13 Nov, after PR #33) | Notes |
| --- | --- | --- | --- |
| [#3](https://github.com/vikas53953/bluey/pull/3) | `lane/face` → `main` | **open**, goes last | The integration PR. Needs green checks plus a fresh live smoke on the latest `lane/face` tip, and the wfx-loot audit still CLEAN. `main` also holds its own LICENSE commit (`ecea48b`), so `lane/face` is 1 commit behind `main` (GitHub still shows PR #3 as mergeable). |
| [#45](https://github.com/vikas53953/bluey/pull/45) | `docs/phase2-notch-coverage-plan` → `lane/face` | **open** | Records Vikas's 13 Nov 2:59 PM decisions in the agent-trust plan: new PR **2.11 "every notch state tested"** (reducer behaviour tests + app-level checks on `windows-latest`, real clock where timing matters; no notch state left UNVERIFIED) and **2.12 "Phase 2 gate"** (full-app QA run before any Phase 3 PR merges); Boo is **OpenAI-only**, so Claude and Gemini are **out of scope** (new feature-map label `out of scope`, accepted by `Test-FeatureMap.ps1`); tests use the cheapest OpenAI models, configurable. Docs and the map checker only. |
| [#37](https://github.com/vikas53953/bluey/pull/37) | `quality/build-facts` → `lane/face` | **merged 13 Nov** (`34473d6`) | Quality gates judge the **shipped** build: windows-ci's Release build and publish now write binary logs (`-bl`, nothing else changes); `scripts/quality/buildfacts` (a pinned, lock-filed .NET reader) turns them into `build-facts.json` (every Csc call with response files expanded and analyzer configs' text, references and where each came from, imports, packages, outputs with SHA-256); `scripts/quality/build_facts.py` fails closed on incomplete facts, unknown tasks or compiler switches, response files, and published assemblies that do not trace to a judged compile or a restored package. Only `build-facts.json` is uploaded (through its key scan), never the binary logs. Nine fixtures: four show each known bypass of PRs #28/#32 in the facts, five (response file, two tampers, a same-name compile outside the solution, a fake `*_wpftmp.csproj`) must also fail the check; the judges come in #28 and #32. Rules: `docs/quality/RULES.md` "Build facts (the shipped build)". |
| [#34](https://github.com/vikas53953/bluey/pull/34) | `quality/pr-evidence` → `lane/face` | **open** (verdict in its PR body) | Agent-trust PR 1.10: new workflow `pr-evidence` runs `scripts/quality/pr_evidence.py` on every PR open, edit and push. The body must have an evidence table with a `Label` column (live / mock / manual / UNVERIFIED / BLOCKED / review); every live row links an Actions run; a PR changing `src/` or `tools/` needs a live row or a BLOCKED row with a reason (BOO-EVID-001). The base branch's copy of the checker judges too. `scripts/test_gate.py static` now checks every xUnit project, including the 3 under `tools/` (BOO-TEST-001). Rules in `docs/quality/RULES.md`. |
| [#33](https://github.com/vikas53953/bluey/pull/33) | `quality/time-provider` → `lane/face` | merged 13 Nov (1:39 PM IST), `2433397` | Agent-trust **Phase 2, PR 2.3** (cleanup C3): every hidden clock read (`DateTime(Offset).Now/UtcNow`, rule BOO-TIME-001) in `src/` goes through one injected `TimeProvider`; 14 → 0 (App 11, Windows 2, Core 1). `App` owns `TimeProvider.System` and passes it down; Core/Windows types take an optional `TimeProvider`. Behaviour unchanged; no tests edited (new fake-clock tests come with the Phase 2 property tests). Hold-to-talk and the focus tick clock take the same clock. Ratchet baseline lowered. **Merge order with #28:** #28's `src/*/AnalyzerBaseline.cs` lists these 14 BOO-TIME-001 hits and its checker wants exact counts, so whichever of #28 and #33 merges second removes those entries. Follow-ups: `Stopwatch`/`Environment.TickCount64` clocks, `Task.Delay` (BOO-TIME-002), `DispatcherTimer` (BOO-TIME-003). QA verdict and run links: in the PR body. |
| [#26](https://github.com/vikas53953/bluey/pull/26) | `quality/key-scan` → `lane/face` | merged 13 Nov (12:48 IST), `342b93d` | Agent-trust PR 1.7: `scripts/quality/key_scan.py` scans exactly what each upload step in `windows-ci`, `demo-ci` and `live-ai-smoke` sends, right before it (rule BOO-SAFE-002: key-shaped text and real secret values). Each upload runs only when its scan step reports `safe=true`; any other outcome skips it. `--diff` mode is ready for the `quality` workflow. |
| [#31](https://github.com/vikas53953/bluey/pull/31) | `quality/netarchtest` → `lane/face` | merged 13 Nov, `a3ba89e` | Agent-trust **Phase 1, PR 1.11**: new frozen test project `tests/Boo.Architecture.Tests` (oracle **revision 25**, `--extend`, 3 new files) with the 6 architecture rules BOO-ARCH-001..006 of plan section 2.4 B, each an exact allowlist of today's offenders matching plan section 1.5 (002 finds a third, `StartupRegistration`, which the plan's text count could not see); rule texts in `docs/quality/RULES.md`. Two lines in `windows-ci.yml` run it and add it to the trx test gate (agreed with the agent-trust thread). |
| [#27](https://github.com/vikas53953/bluey/pull/27) | `docs/notch-statechart` → `lane/face` | **merged 13 Nov** 10:45 IST (`0122ac7`) | Agent-trust **Phase 2, PR 2.1**, docs only: `docs/architecture/notch-lifecycle.md` maps today's notch lifecycle (reveal timeline, face states, hold-to-talk, focus, request generations, approval card, undo, pointer) with a `file:line` cite per transition, each plan invariant marked enforced / partly / not enforced, and 9 findings to settle before the model and reducer PRs. Records Vikas's go for Phases 2 and 3 and the Phase 2 limits ($0 GitHub, $10/month OpenAI). QA verdict and run links: in the PR body. |
| [#22](https://github.com/vikas53953/bluey/pull/22) | `fix/demo-step4-state-race` → `lane/face` | **merged 13 Nov** (`af241fa`) | Demo Step 4 test race fix: re-read the notch state after the button search; oracle revisions 22–23 (`docs/verification/test-oracle-revision-22.md`). |
| [#24](https://github.com/vikas53953/bluey/pull/24) | `docs/verify-boo-followups` → `lane/face` | **merged 13 Nov** (`673b38e`) | Docs-only follow-ups from PR #13's reviews: `SKILL.md` screenshot naming (the `.capture.txt` names each `boo-<n>` window; the panel is the title that starts with `Boo companion`), HANDOFF known issues 11–12 wording. No code change; the 45 s OpenAI timeout is untouched. |
| [#13](https://github.com/vikas53953/bluey/pull/13) | `test/verify-boo-skill` → `lane/face` | **merged 13 Nov** (`13e5484`) | `verify-boo` skill (`.claude/skills/verify-boo`), live journeys j0–j6 (`tools/Boo.VerifyLive`), `docs/FEATURE_MAP.md`, prove-it rule (AGENTS.md, CLAUDE.md), `journeys` input on `live-ai-smoke` (journeys run only on a manual dispatch with `journeys=true`, never on the per-PR run). Also changes `src/Boo.Providers/StructuredReplies.cs` (AI instructions for drawable ring marks; inside a string, so the quality ratchet stays at 554). Since PR #18 the journeys capture Boo's windows only (j3 reads the ring and pointer from Boo's overlay rendered from its own content; "target in front" is asked of Windows), and the skill's local runner refuses to run on a personal PC. Merged at its QA PASS head `3168e40` (live [43339557570](https://github.com/vikas53953/bluey/actions/runs/43339557570): round trip + 7/7 journeys; fresh reviewer REVIEW PASS). Follow-ups: PR #24. |
| [#17](https://github.com/vikas53953/bluey/pull/17) | `docs/agent-trust-plan` → `lane/face` | **merged 13 Nov** 07:50 IST (`879ad4d`) | 3-phase agent trust plan (`docs/plans/agent-trust-plan.md`), with a dated progress note (steps 1, 2 and 4 and Phase 1 PRs #20, #21 done). Latest completed QA: **QA PASS at `77bfb91`** (live [43340191296](https://github.com/vikas53953/bluey/actions/runs/43340191296)), before the PR #13 sync; this head is re-QA'd in the PR body. |
| [#18](https://github.com/vikas53953/bluey/pull/18) | `fix/live-smoke-off-desktop` → `lane/face` | **merged 13 Nov** (`00a1fc2`) | Live smoke on GitHub's VM only, with screenshots of Boo's windows only. Removes the self-hosted runner option; runner check in windows-ci; overlay capture proof in demo-ci; oracle revision 24 (3 capture fixtures). Merged at head `cadc05e`; its final QA evidence is in PR #18's description (an earlier QA PASS at `b590f92`: live [43313942831](https://github.com/vikas53953/bluey/actions/runs/43313942831)). |
| [#20](https://github.com/vikas53953/bluey/pull/20) | `ci/live-smoke-required` → `lane/face` | **merged 13 Nov** (`e1d4b5c`) | Live smoke on every pull request with a final required `live-ai-smoke` job; per-PR concurrency; docs on cost, fork/Dependabot PRs and the gate-edit hole. Latest completed QA: none yet on lane/face base (stacked QA PASS at `ed7ed61`). |
| [#21](https://github.com/vikas53953/bluey/pull/21) | `quality/ratchet` → `lane/face` | **merged 13 Nov** (`1c82af8`) | A ratchet of 16 known-bad code patterns in `src/` (`quality-gates.yml`, job `quality`); 554 hits today, may only go down; the base branch's own ratchet also judges every PR. Final QA: **QA PASS at `297d3dd`** (live [43335633395](https://github.com/vikas53953/bluey/actions/runs/43335633395), quality-gates [43335633345](https://github.com/vikas53953/bluey/actions/runs/43335633345)). |
| [#23](https://github.com/vikas53953/bluey/pull/23) | `quality/ratchet-hardening` → `lane/face` | **merged 13 Nov** (`af089dd`) | Hardens the ratchet so it catches ordinary spellings and **never counts less than before** (except deliberate corrections where the old lexer counted text that is not code: format clauses, `#` directive text, long raw-string openings): the text as written is always counted, extra views (`\u`/`@`/invisible characters, `using` aliases and `using static`, `global using`) can only add; calls split over lines count; it judges exactly the files git would commit (`.cs` in any letter case) and fails on C# files or projects outside `src/<Name>/` and on symlinks. Build-file tricks, subclass calls and the other gaps in `docs/quality/RULES.md` go to the compiler-exact PRs 1.3/1.4. Self-test 133 snippets; today's 554 hits unchanged. |
| [#25](https://github.com/vikas53953/bluey/pull/25) | `fix/perf-diag-priority` → `lane/face` | **merged 13 Nov** 10:10 IST (`40e7828`) | The perf probe (`PerfDiagnostics`, only when `BOO_PERF_DIAG` is set) writes `diag.json` from a timer at `Background` priority instead of `ApplicationIdle`, which starved while the notch rendered and left the file stale during the 2 s reveal sample (push runs 43343217812: 0 fps, 43357430089: 10.99 fps, while 222 and 216 frames rendered). One file in `src/`; no test changed. QA verdict in the PR body. |
| [#19](https://github.com/vikas53953/bluey/pull/19) | `docs/scrub-local-paths` → `lane/face` | merged 13 Nov 03:17 IST (`b765f0f`) | Replaces the owner's Windows username and home paths in old docs (and in files that arrived later, e.g. the Living ink evidence notes) with `%USERPROFILE%` / `<user>_<machine>`. Old commits keep the old text (no history rewrite). |
| [#16](https://github.com/vikas53953/bluey/pull/16) | `docs/project-handoff` → `lane/face` | merged 13 Nov 02:46 IST (`e0fb7b3`) | This handoff (HANDOFF.md, docs/DECISIONS.md, CONTRIBUTING.md). Merged with the PR #12 session notes and decision records. |
| [#15](https://github.com/vikas53953/bluey/pull/15) | `ci/artifact-retention-7d` → `lane/face` | merged 13 Nov 01:40 IST (`95e9718`) | `retention-days: 7` on every upload. |
| [#14](https://github.com/vikas53953/bluey/pull/14) | `fix/anti-mock-lan-ips` → `lane/face` | merged 13 Nov 01:16 IST (`d0dfd0e`) | The anti-mock guard rejects every local, private and LAN address, plus proxies (99 tests). Its tests are frozen as oracle **revision 21** (on top of Living ink's revision 20). |
| [#12](https://github.com/vikas53953/bluey/pull/12) | `feat/living-ink` → `lane/face` | merged 13 Nov 00:40 IST (`8474556`) | Living ink notch. Oracle revision 20 (face tests load face B by name; `docs/verification/test-oracle-revision-20.md`). The ~17 MB memory finding was fixed by drawing the notch in software (decision `D-2026-11-12-notch-software-drawing`). |
| — | `lane/face`, `main` | direct commits 13 Nov 00:11 IST | MIT LICENSE (`9729a14` on lane/face, `ecea48b` on main), with Vikas's approval while Actions was blocked. |
| [#11](https://github.com/vikas53953/bluey/pull/11) | `ci/real-gates` → `lane/face` | merged 12 Nov (`db0a339`) | demo-ci made a blocking gate; manual `live-ai-smoke` that fails closed. |
| [#10](https://github.com/vikas53953/bluey/pull/10) | `fix/ci-frozen-tests-and-sf006` → `lane/face` | merged 12 Nov (`f244e47`) | Oracle and no-vacuous-pass gate in CI; SF-006 runs; `ScopedFiles` refuses names Windows would rewrite. |
| [#9](https://github.com/vikas53953/bluey/pull/9), [#8](https://github.com/vikas53953/bluey/pull/8), [#7](https://github.com/vikas53953/bluey/pull/7) | lanes → `lane/face` | merged 8 Nov | Perf budgets; Velopack installer + opt-in updates; security hardening. |
| [#4](https://github.com/vikas53953/bluey/pull/4) | → `main` | merged 8 Nov (`384241a`) | FlaUI UI smoke in windows-ci. |
| [#1](https://github.com/vikas53953/bluey/pull/1), [#2](https://github.com/vikas53953/bluey/pull/2), [#5](https://github.com/vikas53953/bluey/pull/5), [#6](https://github.com/vikas53953/bluey/pull/6) | lanes → `main` | closed 12 Nov | **Superseded**: all their commits are already in `lane/face`. |

Each PR's latest evidence table and verdict is in its own latest QA comment. This table only tracks state.

### Known issues

0. **A PR can change the gates that judge it** (independent reviews of PRs #20 and #21, 13 Nov 2026). A
   `pull_request` run uses the PR's own workflow files, so a PR could make `live-ai-smoke` (or any check) pass without
   doing its job. Only a GitHub setting closes it: a `CODEOWNERS` file covering `.github/workflows/**`, `scripts/ci/**`,
   `scripts/quality/**`, `scripts/test_*.py`, `docs/quality/**` and `docs/verification/test-oracle*` (with
   `test-oracle-history/**`), plus "Require review from Code Owners" on `lane/face` and `main`. **Decision pending with
   Vikas.** Until then the verifying bot treats any PR touching those paths as needing Vikas's review.

1. **Live-smoke queue.** Before PR #20: one global queue (`concurrency: group: live-ai-smoke`, no cancel-in-progress),
   where a new dispatch cancels an older *waiting* run. With PR #20: one queue per PR (or ref), and a newer push to the
   same PR cancels the older run, which may already have spent its AI request. Dispatch only when no live run is waiting. Live runs cost real money; keep to one per PR head (the Grok Bot pipeline note of 13 Nov, 12:45 AM IST, gives the OpenAI cap as $10/month; the cap itself is UNVERIFIED in the repo).
2. **Anti-mock guard** (PR #14, merged): rejects loopback, private/LAN, link-local, unique-local, CGNAT and fake-IP
   addresses and proxies. Still not detected: a planted trusted root certificate, or a mock on the PC's own
   public-looking address.
3. **Demo Step 4 race: fixed by PR #22 (merged 13 Nov, `af241fa`).**
   `Step4_Focus_NudgeThenBackToIt_EndsHappy` failed twice on docs-only commits
   ([43240958545](https://github.com/vikas53953/bluey/actions/runs/43240958545),
   [43296734862](https://github.com/vikas53953/bluey/actions/runs/43296734862)). Judged a test race, not an app bug:
   the test sampled `Capture`, then found "Back to it" after the flip to `Nudge`, and never re-read the state. The fix
   re-reads the state after the button search (oracle revisions 22–23; record in
   `docs/verification/test-oracle-revision-22.md`).
4. **Flaky perf budget: `revealFramesPerSecond >= 20`.** On GitHub's shared Windows VMs the notch's reveal frame rate
   swings widely on the same code: 44 windows-ci runs between 19:00 and 20:10 UTC on 12 Nov measured 8 to ~74 fps (28 of
   them 40–55). One docs-only run failed at 8 fps ([43296723546](https://github.com/vikas53953/bluey/actions/runs/43296723546))
   while its twin passed at 41 fps; another got 20.99. Since PR #12 the notch draws in software, so it may be more
   sensitive to VM load. Not fixed; a budget or measurement change needs its own reviewed PR. **PR #25** addresses one
   cause found on 13 Nov: the probe's `ApplicationIdle` timer starved during the reveal, so the reported fps was
   too low although the notch rendered (0 and 10.99 fps reported while 222 and 216 frames rendered). Whether that ends the
   flake is UNVERIFIED until a run of push and pull_request builds after it merges stays above the budget; on its own
   head one run still read ~1 s stale (31.49 fps, run 43365292078). The exact fix is to measure fps on the probe's
   own clock (`uptimeMs`) in `BooPerfBudgetTests`, which needs a reviewed oracle revision.
5. **Living ink notes** (from PR #12's reviews, low): built-in fallback face is still B (`NotchSpec.Default`); the
   dark-desktop rim follows the Windows app mode, not the wallpaper; pure black/white custom colours draw as ink/cream;
   the wake-up's first beats are faint on dark desktops and the eyes are not drawn in its "eyes shut" beat; window-level wake-up/celebrate rules are covered by the gallery
   and live runs only. Not measured: software-drawing CPU on 4K / 200 % or high-refresh screens, memory after the
   panel has been opened, and repeat hovers (~0.7 MB each, same on the old build).
6. **SF-006 symlink cases** fail on a dev PC without Windows Developer Mode, same on unchanged `lane/face` (never skipped); they pass in CI.
7. **Merge order / oracle revisions.** `lane/face` is at oracle revision **24** (PR #31 adds **25**, additive): PR #22 (`af241fa`) took 22–23 on top of PR #14's 21, and
   PR #18 (`00a1fc2`) re-froze its 3 capture fixtures on top as revision 24. PR #13 dropped every `-f runner=` use when it synced with PR #18. PRs #13, #18, #20 and #21, which edited the same
   workflows, have all merged (by `13e5484`); a new PR that edits `live-ai-smoke.yml`, `demo-ci.yml`, `windows-ci.yml` or
   `quality-gates.yml` syncs with `lane/face` first.
8. The notch memory guard (`NotchDrawingMemoryTests`) has now run in CI: windows-ci on `8474556`
   ([43287434389](https://github.com/vikas53953/bluey/actions/runs/43287434389)) passed UI smoke, perf budgets and the gate.
9. Residuals listed in [docs/STATUS.md](docs/STATUS.md) ("Known residuals") still apply unless a later PR says
   otherwise.
10. Resolved 13 Nov: the artifact storage quota and the Actions budget block. The repo went public, so standard runners
   are free. On 12 Nov the 8 Nov app copies (168 artifacts, 15.93 GB) were deleted, and uploads work again.
11. **Open follow-ups from PR #13's reviews (low; PR #13 merged).** `Test-FeatureMap.ps1` does not catch a map row typed without its
   leading `|` or a lowercase ID; a failed restore of Windows animation effects after a live run is only written
   to stderr; Boo's `AnnotationValidator` still drops a whole explanation when one mark is invalid (run 43196068202:
   "The annotation mark bounds are invalid"; PR #13 only tells the AI the mark rules); j3 does not prove its ring is
   new (safe while j3 is the first explain request: Boo clears old overlays on every reply); the j3/j6 "target in
   front" check looks at one point.
12. **Explain requests can hit Boo's 45 s OpenAI limit** (the panel's `HttpClient { Timeout = 45 s }`,
   `MainWindow.xaml.cs`). PR #13 live run 43333049693 ended j3 with Boo's status "OpenAI response timed out. Try again.";
   run 43202679653 failed j3 the same way from the outside, but its cause is UNPROVEN (the test did not yet record Boo's
   status then, and that run's evidence is no longer available). Chat, research and Apply/Undo never timed out. The request is Boo's own (its
   capture of the display plus the question), so the test's screenshot method cannot cause it; in passing runs the
   marks arrive in about 4 to 6 s (`marksAfterSeconds`: 6.2 s in 43335756258, 4.2 s in 43339557570). Raising the limit
   (longer waits vs fewer timeouts) is a product decision for Vikas; nothing in the code was changed.

### UNVERIFIED (not proven on `lane/face`)

- ~~Claude (Anthropic) and Gemini~~: **out of scope** since 13 Nov 2026 (Boo is OpenAI-only, chat and voice; Vikas's decision). Their mock tests remain; they are not counted as UNVERIFIED.
- **Voice** (OpenAI Realtime, hold-to-talk) as a live round trip on `lane/face`. PR #12's builder reports one live
  voice turn on `feat/living-ink`; not re-verified independently.
- **Pointer + ring with a real model, beyond PR #13's one target.** Since PR #13 merged (`13e5484`), its live journey j3 proves the
  ring runs through a known button's edges and the pointer lands on it (passed on `de9492f`, `7c5af8d`, `60930b9`,
  `e86a107`, `87a1442` and, reading Boo's own overlay render, `d2f9827` and `3168e40`; later runs in each PR's evidence
  table), on one 1024x768 VM screen. Still
  UNVERIFIED: other targets, other DPI or multiple monitors, and **Windows animation effects off**: Boo then hides the
  pointer by design, and the journeys run with animations switched on.
- **A manual run by Vikas on his PC**: the three demos and a real 25-minute focus session
  ([docs/MORNING_CHECKLIST.md](docs/MORNING_CHECKLIST.md)). Delivery is not accepted until this is done.
- Installer install / Start-menu / uninstall / real update round trip on a PC; the SmartScreen prompt.
- The `github-release` job (needs a `v*` tag; the portable ZIP and installer upload again since 13 Nov).
- Hover timing, DPI and multi-monitor behaviour by hand (native checklists in `docs/verification/2026-11-08-*`).

### Quality ratchet (Phase 1, PR #21)

`.github/workflows/quality-gates.yml` (job `quality`, Linux, about a minute, no uploads) runs
`scripts/quality/ratchet.py`: today's count of 16 known-bad patterns in `src/` (554 hits on `lane/face`, 13 Nov 2026, per
project, in `docs/quality/baseline.json`) may only go **down**. A rise fails with what / why / fix / do-not / rule; a fall
fails until `--lower` writes the new baseline. On a pull request the **base branch's own** ratchet and baseline also judge
the code, so a PR cannot raise its own numbers. `--self-test` checks 133 snippets with exact counts and the pass/fail
logic. Rules, fixes and known gaps: `docs/quality/RULES.md`. To be made a required check (`quality`) after it merges.

### Next phase

Packaging for real use. What exists and what is missing:

| Item | Exists today | Missing / UNVERIFIED |
| --- | --- | --- |
| Installer | Velopack per-user `Boo-win-Setup.exe` built by `release` (PR #8) | Install/uninstall on a real PC; release workflow red while storage is full |
| Code signing | none: the build is **unsigned** (README: SmartScreen "More info > Run anyway") | Certificate choice and signing step: not started |
| Start with Windows | HKCU Run entry, off by default; UI smoke `StartWithWindows_Checkbox_WritesAndRemovesRunValue` passes on the VM | Boo actually starting after a real sign-in |
| Auto-update | Opt-in GitHub Releases check (24 h, DPAPI-protected read token, explicit Install and restart) | A real update round trip from one tagged release to the next |

## 8. Where to look next

- Decisions and why: [docs/DECISIONS.md](docs/DECISIONS.md)
- Plans: `docs/plans/` (start with `2026-11-06-2242-feat-boo-windows-companion-plan.md` and
  `2026-11-08-feat-boo-video-parity-plan.md`)
- Evidence and native checklists: `docs/verification/`
- Security review: `docs/security/2026-11-08-security-review.md`
