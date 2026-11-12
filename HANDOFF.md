# Boo — project handoff

**Read this first.** It is written so that any agent or person can pick up the project cold. Every fact here comes
from the repository, its pull requests or its Actions runs, with a link. Anything not proven is marked **UNVERIFIED**.

**Keep it true.** Every PR must update the [Current status](#7-current-status) section of this file and add any new
decision to [docs/DECISIONS.md](docs/DECISIONS.md). See [CONTRIBUTING.md](CONTRIBUTING.md).

Status last updated: **13 Nov 2026, ~02:00 IST**, with `lane/face` at `95e97189c8a44d15b1ff6efeea89c4eb8b212770`.

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

**Warning: the UI, perf and demo suites take FULL-SCREEN screenshots** (`Capture.Screen()` in
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
| `live-ai-smoke.yml` | **manual only** (`workflow_dispatch`) | `preflight`, `smoke` | one real round trip: typed question → screenshot → real provider → answer in Boo. Fails closed without a key. | **yes** |

Run the live smoke: GitHub → Actions → live-ai-smoke → Run workflow, `text_provider=openai`,
`runner=windows-latest` (the default `self-hosted` needs a registered runner; none is registered as of 12 Nov), and an
optional `model` override (blank = Boo's default). Or:
`gh workflow run live-ai-smoke.yml --ref <branch> -f text_provider=openai -f runner=windows-latest`.
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
  - required checks: **`demos`** (demo-ci) and **`build-test-publish`** (windows-ci), both from GitHub Actions;
  - branch must be up to date before merging;
  - force pushes and branch deletion blocked;
  - no required approvals (Vikas is the only reviewer); admins not enforced (Vikas can override).
  - `live-ai-smoke` is **not** a required check: it only runs by hand, so it would block every merge.
- **Merging, tagging and releasing stay with Vikas**, and agents never merge, tag, release, or push to `lane/face` or
  `main`. Proposed in PR #18 (not merged yet): since 13 Nov 12:15 AM IST the verifying bot (Grok Bot) merges PR #17, PR #18 and the
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
   offscreen renders.
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

| PR | Branch → base | State (13 Nov, ~02:00 IST) | Notes |
| --- | --- | --- | --- |
| [#3](https://github.com/vikas53953/bluey/pull/3) | `lane/face` → `main` | **open**, goes last | The integration PR. Needs green checks plus a fresh live smoke on the latest `lane/face` tip, and the wfx-loot audit still CLEAN. `main` also holds its own LICENSE commit (`ecea48b`), so `lane/face` is 1 commit behind `main` (GitHub still shows PR #3 as mergeable). |
| [#16](https://github.com/vikas53953/bluey/pull/16) | `docs/project-handoff` → `lane/face` | **open** | This handoff (HANDOFF.md, docs/DECISIONS.md, CONTRIBUTING.md). Merged with the PR #12 session notes and decision records. |
| [#19](https://github.com/vikas53953/bluey/pull/19) | `docs/scrub-local-paths` → `lane/face` | **open**, QA PASS at `44be70b` (needs sync) | Replaces the owner's Windows username and home paths in old docs. |
| [#13](https://github.com/vikas53953/bluey/pull/13) | `test/verify-boo-skill` → `lane/face` | **open** | `verify-boo` skill: live journeys j0–j6, `docs/FEATURE_MAP.md`, prove-it rule. Since `1c4b2d1` it also changes `src/Boo.Providers/StructuredReplies.cs` (AI instructions for drawable ring marks). |
| [#17](https://github.com/vikas53953/bluey/pull/17) | `docs/agent-trust-plan` → `lane/face` | **open** | 3-phase agent trust plan (`docs/plans/agent-trust-plan.md`). |
| [#18](https://github.com/vikas53953/bluey/pull/18) | `fix/live-smoke-off-desktop` → `lane/face` | **open** | Live smoke on GitHub's VM only, with screenshots of Boo's windows only. Removes the self-hosted runner option. |
| [#20](https://github.com/vikas53953/bluey/pull/20) → #18's branch, [#21](https://github.com/vikas53953/bluey/pull/21) → #20's branch | stacked | **open** | Live smoke as a required check on every PR; a ratchet of known-bad code patterns. |
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

1. **Live-smoke queue.** `live-ai-smoke` has `concurrency: group: live-ai-smoke` (no cancel-in-progress). GitHub keeps
   only ONE waiting run per group, so a new dispatch cancels an older *waiting* run before it starts (no AI request is
   made). Dispatch only when no live run is waiting. Live runs cost real money; keep to one per PR head (the Grok Bot pipeline note of 13 Nov, 12:45 AM IST, gives the OpenAI cap as $10/month; the cap itself is UNVERIFIED in the repo).
2. **Anti-mock guard** (PR #14, merged): rejects loopback, private/LAN, link-local, unique-local, CGNAT and fake-IP
   addresses and proxies. Still not detected: a planted trusted root certificate, or a mock on the PC's own
   public-looking address.
3. **Flaky demo: `Step4_Focus_NudgeThenBackToIt_EndsHappy`.** Seen again on docs-only `3593ba3`
   ([43296734862](https://github.com/vikas53953/bluey/actions/runs/43296734862)). On docs-only commit `1e5c754`, all 5 demo tests passed in
   [43240969060](https://github.com/vikas53953/bluey/actions/runs/43240969060) but Step 4 failed in
   [43240958545](https://github.com/vikas53953/bluey/actions/runs/43240958545): the "Back to it" button appeared while
   the notch's reported state still read `Capture` (`DemoTests.cs:228`). The cause isn't proven. The test is frozen, so
   a fix needs its own reviewed PR.
4. **Flaky perf budget: `revealFramesPerSecond >= 20`.** On GitHub's shared Windows VMs the notch's reveal frame rate
   swings widely on the same code: 44 windows-ci runs between 19:00 and 20:10 UTC on 12 Nov measured 8 to ~74 fps (28 of
   them 40–55). One docs-only run failed at 8 fps ([43296723546](https://github.com/vikas53953/bluey/actions/runs/43296723546))
   while its twin passed at 41 fps; another got 20.99. Since PR #12 the notch draws in software, so it may be more
   sensitive to VM load. Not fixed; a budget or measurement change needs its own reviewed PR.
5. **Living ink notes** (from PR #12's reviews, low): built-in fallback face is still B (`NotchSpec.Default`); the
   dark-desktop rim follows the Windows app mode, not the wallpaper; pure black/white custom colours draw as ink/cream;
   the wake-up's first beats are faint on dark desktops and the eyes are not drawn in its "eyes shut" beat; window-level wake-up/celebrate rules are covered by the gallery
   and live runs only. Not measured: software-drawing CPU on 4K / 200 % or high-refresh screens, memory after the
   panel has been opened, and repeat hovers (~0.7 MB each, same on the old build).
6. **SF-006 symlink cases** fail on a dev PC without Windows Developer Mode, same on unchanged `lane/face` (never skipped); they pass in CI.
7. **Merge order / oracle revisions.** `lane/face` is at oracle revision 21 (PR #14). PR #18 has already re-synced and
   sits at revision 22 on top of it. Still-open PRs that edit the same workflows: #13 and #18 (plus #20 and #21, stacked
   on #18) edit `live-ai-smoke.yml`; #13 and #18 edit `demo-ci.yml`; #18 edits `windows-ci.yml`.
8. The notch memory guard (`NotchDrawingMemoryTests`) has now run in CI: windows-ci on `8474556`
   ([43287434389](https://github.com/vikas53953/bluey/actions/runs/43287434389)) passed UI smoke, perf budgets and the gate.
9. Residuals listed in [docs/STATUS.md](docs/STATUS.md) ("Known residuals") still apply unless a later PR says
   otherwise.
10. Resolved 13 Nov: the artifact storage quota and the Actions budget block. The repo went public, so standard runners
   are free. On 12 Nov the 8 Nov app copies (168 artifacts, 15.93 GB) were deleted, and uploads work again.

### UNVERIFIED (not proven on `lane/face`)

- **Claude (Anthropic) and Gemini** text providers against the live APIs (no keys set; by choice).
- **Voice** (OpenAI Realtime, hold-to-talk) as a live round trip on `lane/face`. PR #12's builder reports one live
  voice turn on `feat/living-ink`; not re-verified independently.
- **Pointer + ring landing on the right element with a real model.** In PR #13's live journey j3 the ring landed on
  the button in 1 of 3 runs, and the pointer assertion never ran to completion.
- **A manual run by Vikas on his PC**: the three demos and a real 25-minute focus session
  ([docs/MORNING_CHECKLIST.md](docs/MORNING_CHECKLIST.md)). Delivery is not accepted until this is done.
- Installer install / Start-menu / uninstall / real update round trip on a PC; the SmartScreen prompt.
- The `github-release` job (needs a `v*` tag; the portable ZIP and installer upload again since 13 Nov).
- Hover timing, DPI and multi-monitor behaviour by hand (native checklists in `docs/verification/2026-11-08-*`).

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
