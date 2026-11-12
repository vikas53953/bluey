# Boo — project handoff

**Read this first.** It is written so that any agent or person can pick up the project cold. Every fact here comes
from the repository, its pull requests or its Actions runs, with a link. Anything not proven is marked **UNVERIFIED**.

**Keep it true.** Every PR must update the [Current status](#7-current-status) section of this file and add any new
decision to [docs/DECISIONS.md](docs/DECISIONS.md). See [CONTRIBUTING.md](CONTRIBUTING.md).

Status last updated: **12 Nov 2026**, with `lane/face` at `f244e47928ca95de7d903e4d8f188930a33665da`.

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

**Warning: run the UI, perf and demo suites only in CI or a VM**, never on the owner's real desktop. Since PR #18
their fixtures copy the whole desktop (`Capture.Screen()`) only on a GitHub-hosted runner; anywhere else they draw
Boo's own windows only, and their rectangle screenshots refuse to run, so those tests fail by design on a personal PC.
To see Boo on the owner's PC, use `--notch-gallery`.

Known local snag: the SF-006 symlink tests in Boo.Core.Tests fail on purpose (they never skip) when the PC cannot
create symbolic links. Turn on Windows Developer Mode or run elevated.

### In CI (`.github/workflows/`)

| Workflow | Runs on | Job / check name | What it proves | Uses a real AI? |
| --- | --- | --- | --- | --- |
| `windows-ci.yml` | every push and PR (and manual) | `build-test-publish` | oracle + static gate → build → Core / Integration / Providers tests (+ trx gate) → publish → notch gallery → UI smoke (FlaUI) → perf budgets | no |
| `demo-ci.yml` | every push and PR (and manual) | `demos` | setup + the 4 demos (5 tests), end to end, against `Boo.QaMockAi` (hosts-file entry + a self-signed root certificate on the runner) | **no: mock** |
| `release.yml` | push to `main`, `v*` tags, PRs (and manual) | `package`, `github-release` | Core / Integration / Providers tests, then Velopack installer + portable ZIP; a `v*` tag publishes a GitHub Release | no |
| `live-ai-smoke.yml` | **every pull request** + manual, **GitHub's `windows-latest` VM only** | `preflight`, `smoke`, **`live-ai-smoke` (required)** | one real round trip: typed question → screenshot → real provider → answer in Boo. Fails closed without a key. Refuses any non-GitHub runner; evidence screenshots are Boo's windows only. | **yes** |

Run the live smoke: GitHub → Actions → live-ai-smoke → Run workflow, `text_provider=openai`, and an optional `model`
override (blank = Boo's default). There is no runner choice: it always runs on GitHub's `windows-latest` VM, never on
a personal PC (`scripts/ci/check_runners.py` in windows-ci fails any workflow that names a self-hosted runner). Or:
`gh workflow run live-ai-smoke.yml --ref <branch> -f text_provider=openai`.
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
- A PR must be **up to date** with its base to merge. PRs #12 and #13 are based on `db0a339` and are 4 commits
  behind `lane/face` (12 Nov).
- Branch protection, set 12 Nov on **both `lane/face` and `main`** (read back from the API):
  - required checks: **`demos`** (demo-ci) and **`build-test-publish`** (windows-ci), both from GitHub Actions;
  - branch must be up to date before merging;
  - force pushes and branch deletion blocked;
  - no required approvals (Vikas is the only reviewer); admins not enforced (Vikas can override).
  - `live-ai-smoke` becomes a **required** check once the PR adding the `pull_request` trigger merges (Vikas,
    12 Nov 2026, 11:20 PM IST). It is the workflow's final job, which is green only if the key check and the real
    round trip both succeeded (a skipped job would otherwise count as passing).
- **Merging:** since 12 Nov 2026, 11:20 PM IST, PR #18, PR #17 and every agent-trust Phase 1/2/3 PR may be merged
  without asking Vikas **once every gate passes** (all required checks green on the head, a real-key
  `live-ai-smoke` pass on the head, a fresh reviewer's PASS, the evidence table, no skips). Since 13 Nov 2026,
  12:15 AM IST the verifying bot (Grok Bot) does the merge, never the agent that built the PR. Any other PR still needs
  him. **Tagging and releasing stay with Vikas.** Agents never push directly to `lane/face` or `main`.

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
   offscreen renders. Enforced in code (PR #18): the FlaUI fixtures copy the whole desktop only on a GitHub-hosted
   runner and never in the live smoke (`BOO_CAPTURE_SCOPE=boo-windows`); anywhere else they draw Boo's own windows
   from their own content (`PrintWindow`, files `*.boo-<n>.png`) and refuse rectangle screenshots. No workflow may use
   a self-hosted runner (`scripts/ci/check_runners.py`), and the repo must keep 0 registered runners. Side effect,
   intended: the UI, demo and perf suites now refuse to run on a personal PC (rectangle screenshots throw), so run
   them only through CI. A capture with the explanation overlay open is tested by `tools/Boo.CaptureProof` in demo-ci (overlay over a magenta
   decoy; zero decoy pixels allowed).
8. **Honest red.** If evidence cannot be uploaded (for example the artifact quota), the job stays red. Never add
   `continue-on-error` to uploads or tests to get a green.
9. **Secrets:** names only. Never print a key, write one to disk logs, or put one in a screenshot.
10. **Reports for Vikas** are short, plain words (no jargon), and anything he must review is a visual page.

## 6. Live QA scope (as of 12 Nov)

Only the **OpenAI** key is in use for live QA. **Claude (Anthropic), Gemini and voice are UNVERIFIED: not tested, by
choice.** Mark them that way, not as BLOCKED and not as green.

## 7. Current status

### Pull requests

| PR | Branch → base | State | Notes |
| --- | --- | --- | --- |
| [#3](https://github.com/vikas53953/bluey/pull/3) | `lane/face` → `main` | **open** | The integration PR. Latest QA: **QA FAIL (blocked: artifact storage)** at `f244e47` ([comment](https://github.com/vikas53953/bluey/pull/3#issuecomment-6826687928)). On the PR's own runs, Core 1719, Integration 148, Providers 115 and demos 5/5 passed. A manual real-key live run on `lane/face` ([43228924705](https://github.com/vikas53953/bluey/actions/runs/43228924705)): the test passed 1/1, and the run is red only at the evidence upload. UI smoke (15/15) and perf passed only on the same code via a since-deleted temp branch ([43230781809](https://github.com/vikas53953/bluey/actions/runs/43230781809)), because the PR's own runs skipped them. The required checks are red because uploads fail. |
| [#12](https://github.com/vikas53953/bluey/pull/12) | `feat/living-ink` → `lane/face` | **open** | Living ink notch (ink-blot pocket, cream eyes, wake-up, celebrate, light/dark). Independent review: **PASS WITH ISSUES** at the older head `88214af` ([verdict](https://github.com/vikas53953/bluey/pull/12#issuecomment-6820951829)). The head is now `736f4af` (rebased; not re-reviewed). Open: about 17 MB more memory after the face is first shown. Branch is 4 commits behind `lane/face`. **It edits 4 test files that are now frozen** (`FaceSpecTests`, `FaceMotionTests`, `NotchLayoutTests`, `NotchFaceGeometryTests`, frozen by PR #10), so it needs a reviewed `--rebaseline` (plus `--extend` for `LivingInkTests.cs`) before CI can pass. |
| [#13](https://github.com/vikas53953/bluey/pull/13) | `test/verify-boo-skill` → `lane/face` | **open** | `verify-boo` skill (live user journeys j0–j6 on the real `Boo.exe`), `docs/FEATURE_MAP.md` (68 features), `AGENTS.md` / `CLAUDE.md` prove-it rule. Own verdict: **QA FAIL** at `381055b`: CI red (storage), and j3 screen-explain failed 2 of 3 live runs. Branch is 4 commits behind `lane/face`. |
| [#14](https://github.com/vikas53953/bluey/pull/14) | `fix/anti-mock-lan-ips` → `lane/face` | **open** | The anti-mock guard rejects every local, private and LAN address, plus proxies (99 tests). Two fresh reviews: the code meets the requirement. **QA FAIL (blocked: artifact storage)** at `1e70b8a` ([evidence](https://github.com/vikas53953/bluey/pull/14#issuecomment-6826668854)). Real-key live run ([43238744459](https://github.com/vikas53953/bluey/actions/runs/43238744459)): the test passed 1/1, and the run is red only at the evidence upload. Moves the oracle to revision 20 and edits `Boo.sln`. |
| [#15](https://github.com/vikas53953/bluey/pull/15) | `ci/artifact-retention-7d` → `lane/face` | **open** | `retention-days: 7` on every upload. **QA FAIL (blocked: artifact storage)** at `8836c7e`. |
| [#16](https://github.com/vikas53953/bluey/pull/16) | `docs/project-handoff` → `lane/face` | **open** | This handoff (HANDOFF.md, docs/DECISIONS.md, CONTRIBUTING.md). Docs only. |
| [#17](https://github.com/vikas53953/bluey/pull/17) | `docs/agent-trust-plan` → `lane/face` | **open** | `docs/plans/agent-trust-plan.md`: the 3-phase agent-trust plan. Vikas approved its order on 12 Nov, 10:02 PM IST. Docs only. |
| [#18](https://github.com/vikas53953/bluey/pull/18) | `fix/live-smoke-off-desktop` → `lane/face` | **open** | Step 2 of that plan: live smoke on GitHub's VM only, Boo-window screenshots only, runner check in windows-ci. Oracle revision 20 (3 harness files). |
| [#11](https://github.com/vikas53953/bluey/pull/11) | `ci/real-gates` → `lane/face` | merged 12 Nov (`db0a339`) | demo-ci made a blocking gate; manual `live-ai-smoke` that fails closed. |
| [#10](https://github.com/vikas53953/bluey/pull/10) | `fix/ci-frozen-tests-and-sf006` → `lane/face` | merged 12 Nov (`f244e47`) | Oracle and no-vacuous-pass gate in CI; SF-006 runs; `ScopedFiles` refuses names Windows would rewrite. |
| [#9](https://github.com/vikas53953/bluey/pull/9), [#8](https://github.com/vikas53953/bluey/pull/8), [#7](https://github.com/vikas53953/bluey/pull/7) | lanes → `lane/face` | merged 8 Nov | Perf budgets; Velopack installer + opt-in updates; security hardening. |
| [#4](https://github.com/vikas53953/bluey/pull/4) | → `main` | merged 8 Nov (`384241a`) | FlaUI UI smoke in windows-ci. This is the current head of `main`. |
| [#1](https://github.com/vikas53953/bluey/pull/1), [#2](https://github.com/vikas53953/bluey/pull/2), [#5](https://github.com/vikas53953/bluey/pull/5), [#6](https://github.com/vikas53953/bluey/pull/6) | lanes → `main` | closed 12 Nov | **Superseded**: all their commits are already in `lane/face`. |

`main` has nothing that `lane/face` lacks (checked 12 Nov: `git log origin/lane/face..origin/main` is empty).

### Known issues

0. **A PR can change the gates that judge it** (found by the independent reviews of PRs #20 and #21, 13 Nov 2026).
   A `pull_request` run uses the PR's own workflow files, so a PR could make `live-ai-smoke` (or any check) pass
   without doing its job. The ratchet (PR #21) also compares against the base branch's own copy, which closes it
   there; for workflow files only a GitHub setting can: a `CODEOWNERS` file covering `.github/workflows/**`,
   `scripts/ci/**`, `scripts/quality/**` and `docs/quality/**`, plus "Require review from Code Owners" on
   `lane/face` and `main`. **Decision pending with Vikas.** Until then the verifying bot treats any PR touching
   those paths as needing Vikas's review.

1. **GitHub Actions artifact storage quota.** Earlier on 12 Nov there were 677 artifacts (22.45 GB). Every upload
   failed with `Failed to CreateArtifact: Artifact storage quota has been hit`, so every run went red. In windows-ci the
   failed app upload also **skips** the gallery, UI smoke and perf steps.
   - About 14:21–14:27 UTC on 12 Nov, with Vikas's approval, the 168 app/installer copies from 8 Nov (15.93 GB) were
     deleted. All evidence was kept. The time comes from the verify-boo agent thread's log; the API can't show deleted
     artifacts. The PR #3 comment posted at 14:41 UTC (6826687928) quoted 677 / 22.45 GB from a measurement taken
     before the deletion, which makes those figures stale. A later comment on PR #3 corrects them.
   - At 15:08 UTC: 509 artifacts, 6.52 GB. A run at 14:51 UTC still hit the quota, because GitHub recalculates usage
     every 6–12 h.
   - **Next step:** after the recount, re-run the checks on PRs #3, #13, #14, #15 and #16. PR #15 stops this recurring.
2. **Living ink memory** (PR #12): after the face is first shown, about 17 MB more memory than `lane/face`.
   - The review's A/B perf runs measured working set 222/220 MB vs 205/203 MB, and private bytes 142/140 MB vs
     122/124 MB.
   - Cause not proven: suspected per-frame geometry rebuilds in `NotchWindow.xaml.cs`. No perf budget covers it.
3. **Anti-mock guard** in `live-ai-smoke`: on `lane/face` it only rejects loopback (`127.x`, `0.x`, `::1`, `::`), so a
   mock on a LAN address would pass. **Fixed in PR #14 (not merged).** Still not covered even with #14: a planted
   trusted root certificate, or a mock on the PC's own public-looking address.
4. **Flaky demo: `Step4_Focus_NudgeThenBackToIt_EndsHappy`.** On the same commit (`1e5c754`, docs only), all 5 demo
   tests passed in [43240969060](https://github.com/vikas53953/bluey/actions/runs/43240969060) (run red only at the
   upload), but Step 4 failed in
   [43240958545](https://github.com/vikas53953/bluey/actions/runs/43240958545).
   - In the failing run the mock's nudge arrived and the "Back to it" button appeared, but the notch's reported state
     still read `Capture`: `states=…>Hidden>Capture`. `Assert.Contains("Nudge", seen)` then failed
     (`tests/Boo.Demo.Tests/DemoTests.cs:228`).
   - Whether this is a timing race in the test or the app setting the state after showing the button is not proven.
   - The test is frozen, so a fix needs its own reviewed PR.
5. **Merge order.** PR #14 and PR #18 **both** take oracle revision 20 (both add
   `test-oracle-history/revision-19.json`), and PR #12 will need its own oracle revision. Whichever merges second must
   re-run `--extend` / `--rebaseline` on top. PRs #13, #14, #15 and PR #18 all edit
   `live-ai-smoke.yml`, and #13 and #15 both edit `demo-ci.yml`. PR #13 still passes `-f runner=...` in its docs and
   journeys; it must drop that after PR #18 merges. HANDOFF.md and docs/DECISIONS.md are
   added by #16 and also by later PRs that copy #16's text and append to it; whichever merges second resolves the
   overlap by keeping both additions.
6. Residuals listed in [docs/STATUS.md](docs/STATUS.md) ("Known residuals") still apply unless a later PR says
   otherwise.

### UNVERIFIED (not proven on `lane/face`)

- **Claude (Anthropic) and Gemini** text providers against the live APIs (no keys set; by choice).
- **Voice** (OpenAI Realtime, hold-to-talk) as a live round trip on `lane/face`. PR #12's builder reports one live
  voice turn on `feat/living-ink`; not re-verified independently.
- **Pointer + ring landing on the right element with a real model.** In PR #13's live journey j3 the ring landed on
  the button in 1 of 3 runs, and the pointer assertion never ran to completion.
- **A manual run by Vikas on his PC**: the three demos and a real 25-minute focus session
  ([docs/MORNING_CHECKLIST.md](docs/MORNING_CHECKLIST.md)). Delivery is not accepted until this is done.
- Installer install / Start-menu / uninstall / real update round trip on a PC; the SmartScreen prompt.
- The release path: portable ZIP, checksum and the `github-release` job. They have been skipped on every run since
  storage filled.
- Hover timing, DPI and multi-monitor behaviour by hand (native checklists in `docs/verification/2026-11-08-*`).

### Agent-trust plan (approved order, 12 Nov 2026, 10:02 PM IST)

From `docs/plans/agent-trust-plan.md` (PR #17). Steps run strictly in this order:

1. Storage fix: PR #15 merges first.
2. Close the screen-capture risk: PR #18.
3. Phase 1 (deterministic checks), as the small PRs listed in the plan, one at a time. Since 13 Nov 2026, 12:15 AM IST
   (Grok Bot, for Vikas) they may be opened **stacked on PR #18** before it merges; the first one makes `live-ai-smoke`
   a required check on every PR, the next is the quality ratchet (PR 1.1). PR #15 is still open but no longer blocks:
   the repo is public, so standard runners are free and uploads work again.
4. Merge PR #13 (verify-boo).
5. Phase 2 and 6. Phase 3: **do not start** without Vikas's explicit go after Phase 1. Phase 2 also needs a monthly
   OpenAI spend cap set by Vikas (GitHub minutes are free since the repo went public).

### Quality ratchet (Phase 1, PR 1.1)

`.github/workflows/quality-gates.yml` (job `quality`, Linux, about a minute, no uploads) runs
`scripts/quality/ratchet.py`: today's count of 16 known-bad patterns in `src/` (527 hits, 13 Nov 2026, per project, in
`docs/quality/baseline.json`) may only go **down**. A rise fails with what / why / fix / do-not / rule; a fall fails
until `--lower` writes the new baseline. On a pull request the **base branch's own** ratchet and baseline also judge
the code, so a PR cannot raise its own numbers. `--self-test` checks 48 snippets with exact counts and the pass/fail
logic. Rules, fixes and known gaps: `docs/quality/RULES.md`. To be made a required check after it merges.

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
