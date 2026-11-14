# Boo — project handoff

**Read this first.** It is written so that any agent or person can pick up the project cold. Every fact here comes
from the repository, its pull requests or its Actions runs, with a link. Anything not proven is marked **UNVERIFIED**.


Status last updated: **13 Nov 2026**; PR #46 synced after PR #47 merged, onto lane/face `2fa2064f7f5302a44581da276295349644ab3fec`. Exact-head QA pending in PR #46.

**Keep it true.** Every PR must update the [Current status](#7-current-status) section of this file and add any new
decision to [docs/DECISIONS.md](docs/DECISIONS.md). See [CONTRIBUTING.md](CONTRIBUTING.md).


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
| `full-app-qa.yml` (PR #54) | **manual only** (`workflow_dispatch`), GitHub's `windows-latest` VM only; never on push or PR, not a required check | `preflight`, `suite`, `red-probe`, **`full-app-qa`** (verdict) | the **phase-gate suite**: builds the whole app from `app_ref` (default `lane/face`) and runs anti-mock checks, launch / hotkey / hover, a real OpenAI chat that must echo a run-time code, Stop, focus pause, settings save + reload, the live round trip (voice once PR #47 lands, BLOCKED until then), j0-j6, and error paths (no key, bad key, network down, timeout); a separate job plants a mock and must see the suite go red. Green only if both. See `docs/verification/full-app-qa.md`. | **yes** (~$0.03-0.05 a run, estimate) |
| `trust-metrics.yml` (PR 1.12) | manual + Mondays 06:17 UTC, `ubuntu-latest`; never on push or PR, **not a required check** | `table` | the trust-metrics table for the last full week (or a given window) from git + GitHub PR/Actions history, read-only; table + snapshot uploaded for 7 days. See `docs/quality/trust-metrics.md`. | no |

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

## 6. Live QA scope (as of 13 Nov)

Only the **OpenAI** key is in use, for chat **and voice** (Vikas, 13 Nov 2026: one provider). **Claude (Anthropic) and Gemini are out of scope** (their code paths remain, untested); **voice is in scope and must be live-tested**. (Until 12 Nov the wording was "Claude (Anthropic), Gemini and voice
are UNVERIFIED: not tested, by choice"; that is superseded.)

## 7. Current status

### Pull requests

PRs into `lane/face` must be **up to date** with it to merge, so every merge makes the others BEHIND. Sync by merging
`origin/lane/face` into the branch (no force push), then re-run CI and one live smoke on the new head.

| PR | Branch → base | State (13 Nov, after PR #33) | Notes |
| --- | --- | --- | --- |
| [#43](https://github.com/vikas53953/bluey/pull/43) | `core/notch-lifecycle-reducer` → `lane/face` | **open** (branch off `34473d6`) | Agent-trust **Phase 2, PR 2.4**: `src/Boo.Core/NotchLifecycle.cs`, a pure reducer (state + event → new state + effects; no clock, no threads, no Windows types) that copies today's notch lifecycle from `docs/architecture/notch-lifecycle.md` and the TLA+ model on `spec/notch-tla` (PR 2.2), with the same event names. Covers the reveal timeline, face flags, both talk paths and the microphone owner, captures, Stop and quit, focus sessions, request generations, the approval card and both undo journals; the face comes from `FaceStateResolver.Resolve`. Gaps F4, F5, F6, F7, F9, F10 are kept on purpose (decision row in `docs/DECISIONS.md`). **Not wired into the app** (PR 2.6). New frozen test file `tests/Boo.Core.Tests/NotchLifecycleTests.cs`, **oracle revision 27** (`--extend`): 103 tests (mock: unit tests, no app run): one or more per doc row, the invariants that hold today, a search of every state reachable in 5 steps plus 300 seeded long walks, and `Gap_F*` tests that a fix PR must flip. Left out: pointer and ring (doc 3.8), the model's planted-bug and fix switches. QA verdict and run links: in the PR body. **Merge after #29** (its `Gap_F10_*` tests and comments point at finding F10 and `specs/` from #29). |
| [#42](https://github.com/vikas53953/bluey/pull/42) | `phase2/notch-reducer` → `lane/face` | **closed 13 Nov** (duplicate of #43) | A second implementation of PR 2.4 opened in parallel. Closed in favour of #43, which has more tests (103 vs 48), models the Talk-button path (finding F10) and matches the TLA+ model action by action. Both would have added oracle revision 26. |
| [#3](https://github.com/vikas53953/bluey/pull/3) | `lane/face` → `main` | **open**, goes last | The integration PR. Needs green checks plus a fresh live smoke on the latest `lane/face` tip, and the wfx-loot audit still CLEAN. `main` also holds its own LICENSE commit (`ecea48b`), so `lane/face` is 1 commit behind `main` (GitHub still shows PR #3 as mergeable). |
| [#48](https://github.com/vikas53953/bluey/pull/48) | `test/live-models-face-timeline` → `lane/face` | **open** | Agent-trust **PR 2.11, part 1**: a notch **face-timeline recorder** for the live journeys (`tools/Boo.VerifyLive/FaceTimeline.cs`, about every 25 ms, race-rule-safe waits; j1, j2, j6 record only). Its own model settings were dropped on the sync because #47 merged the same (cheapest-model live tests, `gpt-6-luna` chat); the live-test fixture still honours `BOO_LIVE_FOCUS_MODEL` / `BOO_LIVE_REALTIME_MODEL`. Journeys run [43417463825](https://github.com/vikas53953/bluey/actions/runs/43417463825) (pre-sync head `9f1b066`): 7/7 passed on `gpt-6-luna`, first timelines recorded. Journeys run [43499455364](https://github.com/vikas53953/bluey/actions/runs/43499455364) (head `4d164a0`, Boo's shipped models per the #47 rule, chat `gpt-6.1-sol`): 6/7 passed; **J3 screen explain failed** because the real OpenAI reply took longer than Boo's 45 s limit, and Boo showed "OpenAI response timed out. Try again." Earlier shipped-model journeys passed J3 in 6 to 8 s, and #68 did not touch the explain path, so this is slow-provider and UNEXPLAINED; a real user would see the same message. A retry was cancelled when #43 merged and the head moved. On the next head ([43556992022](https://github.com/vikas53953/bluey/actions/runs/43556992022)) all 7 journeys passed (J3 in 7 s), but the voice round trip failed its "at least 0.5 s of audio" rule with a correct answer ("5", 0.45 s). Root cause: the question asks for just the number, so the answer is one word with about 0.25 s of speech; the length rule measured trailing silence (a 0.70 s passing answer held 0.28 s of speech, the failing one 0.26 s) and would have passed half a second of silence. **Fix in this PR:** `tools/Boo.LiveSmoke/SpokenAudio.cs` measures real speech (20 ms windows louder than RMS 500) and the voice test requires at least 0.15 s of it, after a built-in self-check that silence counts as none. Codex mutation at `147d051` was 2/6 (survivors: a window exactly at the floor counted, the quiet floor removed, the minimum removed, the last window skipped). Fixed with offline tests (`tools/Boo.LiveSmoke/SpokenAudioTests.cs`, 5 checks: floor 499/500/501, quiet noise, 0.14 s rejected / 0.16 s accepted through the live test's own rule `SpokenAudio.IsSpokenAnswer`, 479/480/481/960-sample windows); they run with no key in the same live-smoke job. The self-check now runs before any paid call. |
| [#57](https://github.com/vikas53953/bluey/pull/57) | `specs/model-followups` → `lane/face` | **open** | Comment and doc fixes from PR #29's last review (no model logic change): exact cites (`MainWindow.Edits.cs:235-256`, `MainWindow.xaml.cs:851`), "Edits.cs" spelled `MainWindow.Edits.cs`, LIVE_2 and INV_FOCUS_AT_MOST_ONE comments made precise, `tlc-wide` no longer called nightly-only, README state counts from TLC runs instead of the Python copy; HANDOFF #29 row marked merged. After #68 moved code, two spec cites were corrected (`MainWindow.xaml.cs:2346`, `RealtimeClient.cs:489-490`). **Follow-up (not in this PR):** line-number cites drift whenever code moves; `docs/architecture/notch-lifecycle.md` still has about ten cites past `MainWindow.xaml.cs:2149` and into `RealtimeClient.cs` that #68 shifted. The class fix is cites anchored by method name plus a CI check that each cited line still holds the named symbol. |
| [#39](https://github.com/vikas53953/bluey/pull/39) | `ci/demo-video` → `lane/face` | **open** | Agent-trust **Phase 2, PR 2.8** (demo-ci only): `scripts/ci/Record-Desktop.ps1` records the GitHub VM desktop during the FlaUI demo step as one MP4 (10 fps, 1280 px wide, H.264 CRF 35), uploaded as the `demo-video` artifact (7 days) after its own key scan. It refuses to start anywhere but a GitHub-hosted runner (BOO-SAFE-001), uses a pinned ffmpeg build checked by SHA-256, and fails above 25 MB (the file is deleted). The stop step runs on failed demos too. `live-ai-smoke` is unchanged: a live video waits for Vikas's decision (the live smoke never captures the whole desktop). Size so far: 6 runs, 0.65–0.69 MB each (plan cap 25 MB). **Codex review BLOCK fix:** the stop step now checks the video with ffprobe and ffmpeg from the same pinned build (not empty, decodes cleanly, at least one video stream, positive duration and frame count); a bad video fails the job but is kept as `demo.rejected.mp4` with a note saying why. A self-test step proves that check on every run (empty, random-bytes and cut-in-half files rejected, a good test-pattern MP4 accepted). Open windows are minimised before recording and the mock AI server's console is hidden, so terminals stop covering Boo; six still frames from the video are uploaded with it. The video is the whole GitHub VM desktop; whether each demo is clearly visible stays UNVERIFIED until a run's frames show it. QA verdict and run links: in the PR body. |
| [#45](https://github.com/vikas53953/bluey/pull/45) | `docs/phase2-notch-coverage-plan` → `lane/face` | **closed 13 Nov** (superseded by #44) | Recorded Vikas's 13 Nov decisions (2.11, Phase 2 gate, OpenAI-only, cheapest models); #44 merged the same decisions first. Its extra `out of scope` feature-map label + checker rule was dropped as unneeded (rows use notes under the existing labels). |
| [#29](https://github.com/vikas53953/bluey/pull/29) | `spec/notch-tla` → `lane/face` | **merged 13 Nov** (`4f40852`; QA PASS at `56562ea`) | Agent-trust **Phase 2, PR 2.2**: TLA+ model of today's notch lifecycle (`specs/`), checked by TLC in a **new, non-required** workflow `formal.yml` (path-filtered to specs, the lifecycle doc and the future reducer, plus nightly; kept out of `quality-gates.yml` while PR #28 owns it). Main checks pass, each with a planted-bug red proof that it catches and a witness that its trigger is reached; the plan's invariants that today's code breaks each fail with a counterexample and pass with their modelled fix; a second job (`tlc-wide`, on PRs that touch the model and nightly) re-runs the main checks with more features overlapping (`MaxActive = 3`); it ran green on `56562ea` (about 2 million states) and a red proof (#52) showed it catches a planted bug. Also folds in #27's 9 review follow-ups and adds finding F10 (known issue 13). QA verdict and run links: in the PR body. |
| [#37](https://github.com/vikas53953/bluey/pull/37) | `quality/build-facts` → `lane/face` | **merged 13 Nov** (`34473d6`) | Quality gates judge the **shipped** build: windows-ci's Release build and publish now write binary logs (`-bl`, nothing else changes); `scripts/quality/buildfacts` (a pinned, lock-filed .NET reader) turns them into `build-facts.json` (every Csc call with response files expanded and analyzer configs' text, references and where each came from, imports, packages, outputs with SHA-256); `scripts/quality/build_facts.py` fails closed on incomplete facts, unknown tasks or compiler switches, response files, and published assemblies that do not trace to a judged compile or a restored package. Only `build-facts.json` is uploaded (through its key scan), never the binary logs. Nine fixtures: four show each known bypass of PRs #28/#32 in the facts, five (response file, two tampers, a same-name compile outside the solution, a fake `*_wpftmp.csproj`) must also fail the check; the judges come in #28 and #32. Rules: `docs/quality/RULES.md` "Build facts (the shipped build)". |
| [#46](https://github.com/vikas53953/bluey/pull/46) | `quality/key-scan-followups` → `lane/face` | **open** (verdict in its PR body) | PR #26 follow-ups: `key_scan.py` scans each folder/file name on its own (a `/` before `sk-`/`AIza` hid key-named files on Linux), rejects unknown command-line options (fail closed), reports malformed YAML instead of a traceback; its wrapper self-test runs on Linux too. |
| [#47](https://github.com/vikas53953/bluey/pull/47) | `feat/voice-live-test` → `lane/face` | **merged 13 Nov**, merge `2fa2064` | Live OpenAI voice round trip on every PR: a synthetic spoken question (checked-in WAV, never a mic) through Boo's own `RealtimeClient` on GitHub's VM; OpenAI must hear "two plus three" and answer "five" aloud. Models set per run (`model`, `realtime_model`, `transcribe_model`), cheap by default (`gpt-6-luna`, `gpt-realtime-2.1-mini`, `gpt-4o-mini-transcribe`; journeys keep Boo's shipped models); free model-exists check first; about US$0.004 a run. Provider-client real-AI evidence only; feature map T6 remains UNVERIFIED/manual for the native app voice path. Mutation follow-up adds five pure protocol test cases in RealtimeVoiceConfigurationTests (failure events, explicit transcription-model overrides, omitted/null defaults); original-head mutation gaps and final receipts are in the PR comments. Additive oracle extension for mutation-driven tests, per Vikas's 13 Nov order: only the new test file is registered; existing hashes unchanged, changedFiles/removedFiles empty, history intact, no rebaseline. PR #43 also proposes revision 26; whichever merges second must re-extend on the merged tip. |
| [#34](https://github.com/vikas53953/bluey/pull/34) | `quality/pr-evidence` → `lane/face` | **merged 13 Nov** (`f3b2c32`) | Agent-trust PR 1.10: new workflow `pr-evidence` runs `scripts/quality/pr_evidence.py` on every PR open, edit and push. The body must have an evidence table with a `Label` column (live / mock / manual / UNVERIFIED / BLOCKED / review); every live row links an Actions run; a PR changing `src/` or `tools/` needs a live row or a BLOCKED row with a reason (BOO-EVID-001). The base branch's copy of the checker judges too. `scripts/test_gate.py static` now checks every xUnit project, including the 3 under `tools/` (BOO-TEST-001). Rules in `docs/quality/RULES.md`. |
| [#36](https://github.com/vikas53953/bluey/pull/36) | `quality/trust-metrics` → `lane/face` | **merged 13 Nov** (`f232474`) | Agent-trust **Phase 1, PR 1.12**: `scripts/quality/trust_metrics.py` (read-only `gh`/`git`, fixed UTC window, `--self-test` with planted data, recompute from a snapshot) and the first table in `docs/quality/trust-metrics.md` (8 Nov 00:00 to 13 Nov 06:00 UTC: 21 PRs merged, 0 reverts, QA PASS at the merged head 16/21, flaky 2.6% the plan's way / 0.9% strict, 125 live runs). Escaped bugs and prose-to-check ratio are UNVERIFIED (no data source yet). Optional weekly `trust-metrics` workflow, not a required check. No app change. QA verdict and run links: in the PR body. |
| [#33](https://github.com/vikas53953/bluey/pull/33) | `quality/time-provider` → `lane/face` | merged 13 Nov (1:39 PM IST), `2433397` | Agent-trust **Phase 2, PR 2.3** (cleanup C3): every hidden clock read (`DateTime(Offset).Now/UtcNow`, rule BOO-TIME-001) in `src/` goes through one injected `TimeProvider`; 14 → 0 (App 11, Windows 2, Core 1). `App` owns `TimeProvider.System` and passes it down; Core/Windows types take an optional `TimeProvider`. Behaviour unchanged; no tests edited (new fake-clock tests come with the Phase 2 property tests). Hold-to-talk and the focus tick clock take the same clock. Ratchet baseline lowered. **Merge order with #28:** #28's `src/*/AnalyzerBaseline.cs` lists these 14 BOO-TIME-001 hits and its checker wants exact counts, so whichever of #28 and #33 merges second removes those entries. Follow-ups: `Stopwatch`/`Environment.TickCount64` clocks, `Task.Delay` (BOO-TIME-002), `DispatcherTimer` (BOO-TIME-003). QA verdict and run links: in the PR body. |
| [#26](https://github.com/vikas53953/bluey/pull/26) | `quality/key-scan` → `lane/face` | merged 13 Nov (12:48 IST), `342b93d` | Agent-trust PR 1.7: `scripts/quality/key_scan.py` scans exactly what each upload step in `windows-ci`, `demo-ci` and `live-ai-smoke` sends, right before it (rule BOO-SAFE-002: key-shaped text and real secret values). Each upload runs only when its scan step reports `safe=true`; any other outcome skips it. `--diff` mode is ready for the `quality` workflow. |
| [#31](https://github.com/vikas53953/bluey/pull/31) | `quality/netarchtest` → `lane/face` | merged 13 Nov, `a3ba89e` | Agent-trust **Phase 1, PR 1.11**: new frozen test project `tests/Boo.Architecture.Tests` (oracle **revision 25**, `--extend`, 3 new files) with the 6 architecture rules BOO-ARCH-001..006 of plan section 2.4 B, each an exact allowlist of today's offenders matching plan section 1.5 (002 finds a third, `StartupRegistration`, which the plan's text count could not see); rule texts in `docs/quality/RULES.md`. Two lines in `windows-ci.yml` run it and add it to the trx test gate (agreed with the agent-trust thread). |
| [#27](https://github.com/vikas53953/bluey/pull/27) | `docs/notch-statechart` → `lane/face` | **merged 13 Nov** 10:45 IST (`0122ac7`) | Agent-trust **Phase 2, PR 2.1**, docs only: `docs/architecture/notch-lifecycle.md` maps today's notch lifecycle (reveal timeline, face states, hold-to-talk, focus, request generations, approval card, undo, pointer) with a `file:line` cite per transition, each plan invariant marked enforced / partly / not enforced, and 9 findings to settle before the model and reducer PRs. Records Vikas's go for Phases 2 and 3 and the Phase 2 limits ($0 GitHub, $10/month OpenAI). QA verdict and run links: in the PR body. Merged at its QA PASS head `95b00ca` (live [43367289882](https://github.com/vikas53953/bluey/actions/runs/43367289882); 5 fresh reviews, the last PASS). Its 9 minor review follow-ups are folded into PR #29. |
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
| [#44](https://github.com/vikas53953/bluey/pull/44) | `docs/vikas-decisions-13nov` → `lane/face` | **open** | Docs only: records Vikas's 13 Nov decisions (OpenAI only for chat and voice, Claude/Gemini out of scope; cheapest OpenAI models in tests; every notch state tested, plan row 2.11; phase gates, plan section 7; Vikas's 10-minute pre-release check). QA verdict in the PR body. |
| [#54](https://github.com/vikas53953/bluey/pull/54) | `qa/full-app-suite` → `lane/face` | **open** | The **full-app QA suite** for the phase gates: new manual workflow `full-app-qa.yml` and test project `tools/Boo.QaSuite` (anti-mock, suite journeys, error paths) plus `qa_report.py` (labelled evidence table, notch states seen). Reuses `BooAppFixture`, `LiveAppFixture`, `RealAiHostGuard`, `Boo.LiveSmoke` and `Boo.VerifyLive` unchanged. Deliberate-failure probe in its own job. No `src/`, `tests/`, oracle or existing workflow change. Phase gate results will be recorded here by small follow-up PRs. QA verdict and run links: in the PR body. |
| [#86](https://github.com/vikas53953/bluey/pull/86) | `quality/live-pass3` → `lane/face` | **open** | pass^3 (rule BOO-LIVE-003): new non-required workflow `live-pass3.yml` runs the typed live round trip k times (3 to 5, default 3) as independent attempts on one build; `scripts/ci/pass_k.py` prints `PASS^k: P/k`, writes a JSON pass-rate summary, fails unless all k passed. Triggers: PRs touching the AI paths, and by hand. About US$0.012 per run. Doc: `docs/quality/live-pass3.md`. Evidence in the PR body. |
| [#86](https://github.com/vikas53953/bluey/pull/86) | `quality/live-pass3` → `lane/face` | **merged 14 Nov** (`9760387`) | pass^3 (rule BOO-LIVE-003): new non-required workflow `live-pass3.yml` runs the typed live round trip k times (3 to 5, default 3) as independent attempts on one build; `scripts/ci/pass_k.py` prints `PASS^k: P/k`, writes a JSON pass-rate summary, fails unless all k passed. Triggers: PRs touching the AI paths, and by hand. About US$0.012 per run. Doc: `docs/quality/live-pass3.md`. Evidence in the PR body. |
| [#91](https://github.com/vikas53953/bluey/pull/91) | `quality/live-sentinel` → `lane/face` | **open** | **BOO-LIVE-004 daily live sentinel**: `.github/workflows/live-sentinel.yml` + `scripts/ci/live_sentinel.py` (`--self-test`) + `docs/quality/live-sentinel.md`. Once a day the `lane/face` tip gets one real typed and one real voice OpenAI round trip on the cheapest models (2 paid calls, about US$0.12 a month); failures open or update one issue, a pass closes it. The schedule runs from `main` (the default branch), so it starts when `lane/face` merges into `main`. Proof runs (red and green) are in its PR body. #85 owns the gate manifest; whichever merges second adds live-sentinel to it. |

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

13. **The microphone can keep recording while the mic indicator is off** (finding F10 in `docs/architecture/notch-lifecycle.md`; found by the PR #29 model; code reading, UNVERIFIED by any run). Hold Ctrl+Alt+Space, click Talk twice (or Send, then Talk), release the chord: the Talk-button microphone keeps streaming while the mic indicator is off (the notch hides, or later shows only Thinking/Responding). A privacy bug; fix planned as its own small PR.
14. **`formal` (TLC) is not a required check.** It is path-filtered, so as a required check it would stay pending on PRs that do not touch its paths. To make it required later, switch to an always-running job that exits early when nothing relevant changed. Like every workflow, a PR can edit `formal.yml` itself (known issue 0). Its nightly `schedule` only fires from
   the default branch (`main`), so it does not run nightly until `lane/face` reaches `main`.
15. **Open item: pass^3 per-attempt screenshot check (Codex P2 on #86): deferred until #48 merges** — then one PR moves the inline screenshot check from `live-ai-smoke.yml` (+ the copy in `native-voice-e2e.yml`) into one shared script called by both and by each `live-pass3` attempt.

### UNVERIFIED (not proven on `lane/face`)

- **Claude (Anthropic) and Gemini** text providers: **out of scope** since 13 Nov 2026 (OpenAI only); not tested.
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

### 13 Nov: full-app acceptance correction (PR #54)
Run https://github.com/vikas53953/bluey/actions/runs/43427902088 reports 22/22 implemented tests passed on app/harness 56c7e01be0fa165e696309e84b79f0babd1427d5. UNVERIFIED as full-app acceptance: voice and j7-j18 were not proven. Raw timeout evidence reports 0.1 seconds, so it does not prove the intended 45-second timeout. The newer minimum-duration assertion must run live. The report now fails closed on any required BLOCKED/UNVERIFIED journey or face. No QA PASS. Final independent review, draft-PR red proof, synthetic voice and expanded journeys remain pending.

### 13 Nov, 7:45 PM follow-up: S2 failure triage (PR #54)
LIVE evidence inspected: https://github.com/vikas53953/bluey/actions/runs/43432343003, app screenshot qa-s2-chat-answer.boo-1.png shows TQDJ PHTG for tqdjphtg. Real provider answered, but failed the requested contiguous token. No application defect established. S2 now generates uppercase itself and asks for verbatim copying; its exact-token assertion is unchanged (no normalizing, retry or skip). UNVERIFIED until fresh live run. Corrected timeout evidence in this run reports 45.6 seconds after send. Anti-mock and red-probe succeeded. Required missing journeys and states still prevent full-app PASS; #47/#48/#50 remain open as checked this turn.

### Reviewer corrections and partial j13 coverage (PR #54)
Face evidence now uses an action-local recorder boundary; previous tests cannot satisfy S2. Report parses only timestamped qa-notch-states observations; expected faces in prose cannot become live evidence (behavioral self-test passes). New no-key FlaUI FocusEdgeTests exercises End from Paused, Happy, replacement without Happy, persisted Ended/Running sessions and UI history. This is partial j13; the full j13 mapping stays UNVERIFIED. Local .NET 10 SDK absent: compilation and native behavior pending GitHub. No full-suite acceptance. Remaining j7-j18 branches, voice, budget enforcement and draft visible-failure proof remain required.

### j7 hover edges implemented, native UNVERIFIED (PR #54)
Q7 drives all four j7 branches without an AI key on GitHub-hosted Windows only. Setup checks Windows animation effects on and Boo reduced motion off; session animation changes are restored. b/c reveal settles 400 ms before leave; return deadlines 400/950 ms use -20/+35 ms tolerance; a leaves at 100 ms with 70-150 ms tolerance. Timestamped UIA reads must have no gap above 100 ms; setup/timing misses fail, with no retries. Paused is observed throughout hover plus 1700 ms after leave. Phase timing is inferred, stated in observations. Report maps j7 only if Q7 passes. No new native pass claimed; all other missing journey branches stay required/red.

### j7 uncertainty bounds and recorder health (PR #54)
j7 now records UIA read start/end plus pointer call intervals. Each observed edge must be bounded to 35 ms. b/c reveal must finish before leave; the entire re-entry interval must fit in delay or 900-1060 ms retract, otherwise setup fails. a verifies Hidden against entry + 240+900+160 ms (20 ms frame tolerance), rejecting a countdown starting prematurely at leave. Q13 cannot accept no-Happy if reads fail/stall or have gaps above 100 ms; raw window observations are retained. Native changes UNVERIFIED pending GitHub. LIVE older run https://github.com/vikas53953/bluey/actions/runs/43441732363 tested merge fcb1e35ae9c82b0421c7ef73127982de538aa802: 22/22 implemented checks passed, nonce QTNDQQDH preserved exactly in inspected screenshot; anti-mock/probe succeeded. Overall remains RED for missing coverage, never QA PASS.

Build correction: run https://github.com/vikas53953/bluey/actions/runs/43443061340 failed compiling FocusEdgeTests with three xUnit2031 errors. Replaced Where+Assert.Single with Assert.Single's predicate overload; same cardinality and state assertions. No native evidence for Q7/Q13 from this run. CI must compile and run corrected head.

### 2fb382a live failure diagnosis and driver correction (PR #54)
Run https://github.com/vikas53953/bluey/actions/runs/43444088592 compiled the harness and drove Boo. Q7 failed setup: enter/leave Mouse.MoveTo calls each took about 700 ms, so measured leave was 844 ms instead of required 70-150 ms. Q13 failed recorder health (reads of 108 and 174 ms) while finding the same notch on each sample. Failure screenshots inspected; neither proves a Boo defect. FlaUI 4 source confirms MoveTo interpolates at 0.5 pixels/ms; switched to its immediate real-pointer Position API. Both samplers retain the same-process notch UIA reference; recorder invalidates stale references and retains failed-read evidence. No thresholds, assertions, retries or skips changed. Correction UNVERIFIED until the next GitHub native run; overall missing-coverage failures remain required.

### f498092 inspected live evidence and next coverage (PR #54)
Run https://github.com/vikas53953/bluey/actions/runs/43446672462 tested integrated app/harness b0e9c6a6c5cd1f83cfd721847744c4e9d7c362de: 25/25 implemented tests passed. Q7 timing traces and 950ms/Paused screenshots inspected; Q13 replacement history screenshot and healthy observation log inspected. Still SUITE RED for required missing journeys/states. #47 is merged and the integrated PR build includes its synthetic-audio provider roundtrip; that test instantiates RealtimeClient rather than driving Boo.exe voice UI, so report now says component and cannot satisfy any voice UI journey. Added Q15 a-c only: fresh unique write proposals, Cancel/Escape/replacement, old card/approval removal, 3-second file-absence observations, replacement nonce, screenshots/logs. Four bounded chat requests total, no retries; d/e remain required UNVERIFIED. New Q15 native behavior pending GitHub; no QA PASS.

### Review corrections and j16 file branch (PR #54)
Voice component is described as passed only if every corresponding result Passed; failed/skipped/mixed-result regressions pass. Q15 replacement now requires exact trimmed nonce equality. Source inspection found file identity is in the card's Resolved destination textbox, not its generic Write file/Delete file title; Q15 and Q16 bind the canonical destination to the unique expected file. Added Q16: create and approve, first undo and approve verifies removal, second undo and approve verifies exact text restoration, each with preapproval checks/readbacks/screenshots. One additional provider creation request, no retries; field branch and complete j16 remain required UNVERIFIED. Native behavior/compilation pending current-head GitHub evidence; no QA PASS.

### Issue #66 and selector correction (PR #54)
Run https://github.com/vikas53953/bluey/actions/runs/43450014918 tested integrated 0bc7dc292c483fb91356243eecd787a1dc0154b7: 25/29 passed. Q15 Cancel/Escape hit the label named Resolved destination, which has no Value/Text pattern; selector now also requires ControlType.Edit. Failure screenshot inspected. Q15 replacement and Q16 had no proposal: actual OpenAI replies could not identify the task folder from the captured screen. Product gap recorded at https://github.com/vikas53953/bluey/issues/66; original task requests and missing-card assertions retained as failing live regressions. Proposed source fix supplies selected-folder state through the OpenAI provider factory callback only for explicit tasks (frozen Contracts.cs unchanged); provider context explains local relative paths and approval, without sending the absolute folder path. ActionPolicy/FileActionResolver unchanged. Added mock HTTP emitted-request contract with true/false authorization cases, separately labelled mock in report. All source/native changes UNVERIFIED pending compiled GitHub run; no QA PASS.

Issue #66 review correction: selected-folder tasks no longer receive contradictory blanket no-actions/screen-target restrictions. Those restrictions apply to screen/UI actions; authorized relative file proposals are explicitly scoped to the local selected folder and still require approval. Emitted-request MOCK cases cover both frame and no-frame across all three authorization combinations (6 cases). Default non-file context unchanged. Reviewer findings addressed locally; compilation, mock execution and published-app regression remain UNVERIFIED until GitHub.

Build correction on 792c7cb: https://github.com/vikas53953/bluey/actions/runs/43454616565/job/131985405886 failed before the red probe because SelectedFolderContractTests lacked System.Net.Http. Added that import; no probe logic, assertion or threshold changed. This run provides no native/probe proof for the folder fix. Corrected-head compilation and real file regressions remain UNVERIFIED until GitHub.

Architecture correction on 9674952: windows-ci run https://github.com/vikas53953/bluey/actions/runs/43455095344 passed build/Core/Integration/Providers/LiveGuard, then rejected integrated MainWindow.xaml.cs at 2680 lines against frozen 2679 budget (BOO-ARCH-006). Extracted unchanged EnsureResponses setup into focused MainWindow.TextProviders.cs as required by the rule. No budget, frozen test or oracle changes. Current integrated-head architecture/native proof remains pending CI.

### Follow-up after required checks passed on bdcdd8a
windows-ci https://github.com/vikas53953/bluey/actions/runs/43455822924 succeeded, including the unchanged architecture budget. Predecessor full-app run https://github.com/vikas53953/bluey/actions/runs/43455095208 tested integrated 9f5bb23a3d925222d68d7b0fa61fba38d58edac1: 34/35 tests passed (6 mock contracts). Q15 a-c and Q16 file readbacks and final screenshots inspected and passed live. Full suite remained red for missing coverage plus Q13 observer-health failure (117 ms read). The old trace did not log its exact action boundary. Focus observer now starts immediately before resolved Start-button invocation, after setup edits; only reads starting after that boundary count, exact window bounds are retained, and health is evaluated before history I/O. Same 100 ms read/gap limit, no retries or threshold changes. New observer behavior UNVERIFIED pending current-head GitHub evidence.

### j18 typed branch added (PR #54)
Q18 requires a first request observed pending with no answer, rechecks immediately before second Send, then requires exactly the second nonce answer and watches 15 seconds for obsolete transcript rows. Two typed sends, no retries. The first can be cancelled during capture before provider dispatch; first-provider overlap and stale-provider-result rejection remain UNVERIFIED. Face timeline retained; ring transitions and Thinking ownership are not proven and full j18 remains required UNVERIFIED. New native behavior pending GitHub. c4d116b required checks passed; its full-app run was still in progress at this change, so no acceptance claim.

### c4d116b full-app result and j18 review corrections
https://github.com/vikas53953/bluey/actions/runs/43458394853 tested app/harness cb7cb2c8cf00adb2330cbc758f8ae3bd044fff19: 35/35 implemented tests passed, including 6 mock request-contract cases. Healthy Q13 window [5429,8660] retained; overall RED only for missing required journey/state coverage. Current Q15/Q16 file logs inspected. Local Q18 review corrected its claim to two typed sends (provider-start overlap not proven) and protects final diagnostic reads/screenshots from masking an original assertion failure. Strict assertion-time transcript reads, nonce equality and one-answer watch unchanged. Full j18 remains UNVERIFIED.

### eb8f232 native failures: preserved, not retried away
windows-ci https://github.com/vikas53953/bluey/actions/runs/43461565845 failed frozen U8 at FocusStatus FindFirst (COM 0x80131505) before Ctrl+Alt+S. Inspected screenshot shows Running/04:58. No src/tests/build-input changes c4d116b..eb8f232; cause UNVERIFIED, investigation https://github.com/vikas53953/bluey/issues/67. Added separate no-key ten-cycle compact-window UIA diagnostic with strict direct reads, per-phase logs and screenshots, no retries; frozen test remains untouched. Full QA https://github.com/vikas53953/bluey/actions/runs/43461576787 also failed Q7: Hidden interval [2641,2653] overlaps its allowed upper bound 2651, so these observations cannot establish a product timing violation. Reduced polling sleep 10 to 1 ms to improve observation resolution; all existing deadlines, 20 ms frame tolerance, 35 ms transition bounds and 100 ms gap limits unchanged. New native diagnostics UNVERIFIED; neither earlier failure is relabelled PASS.

Q67 review corrections: initial opening is inside evidence handling. Setup now uses strict local window discovery and one Ctrl+Alt+B press per intentional opening, with no retry helper or swallowed discovery error. Failure while writing secondary evidence cannot replace the primary assertion/UIA exception; a write failure on an otherwise passing case still fails the diagnostic. Ten-cycle reads/assertions and all timing budgets unchanged. Native behavior UNVERIFIED pending GitHub.

### ffc2a02 result and next bounded coverage
https://github.com/vikas53953/bluey/actions/runs/43465131653 tested app/harness 63a3cea85ab45e51be84ecfbf374ed99bc473361: 37/37 implemented tests passed, including six mock contracts. Q67 log contains all ten consecutive cycles without a UIA exception; original issue #67 cause remains unresolved. Q7 finer sampling places first Hidden in [2315,2317] with original limits; overall still RED for missing coverage. Added j15d (QD15): one model proposal for two files, first approval/readback, Next step card bound to second path, second approval/readback and final card removal. j15e remains required UNVERIFIED. Voice research found no fully documented ready injection route, but current VM endpoint presence was not tested; added a GitHub-only environment probe to start audio services and query WinMM devices/24k-mono16 support without recording or provider calls. Report blocks app-microphone journeys on measured absence; positive query never counts as app voice proof. New source/native behavior pending GitHub.

**QA review correction (13 Nov, PR #54; manual):** j15d compares raw file contents with both exact markers, without trimming. Panel opening and directory creation now sit inside failure-evidence handling. Report self-tests, feature map, oracle revision 25 and quality ratchet pass; .NET compilation, native sequential approvals and hosted audio capability remain UNVERIFIED until GitHub runs this head.

**Next bounded QA slice (manual; native UNVERIFIED):** j10a physically presses/releases the eyes once within a measured conservative 199 ms upper bound, requires the panel to open and healthy action-local observations without Listening. Full j10 stays incomplete; f0750 owns synthetic voice and b/c. PR #54 body lists every remaining j8-j18 branch and the three missing faces from the last completed receipt. Current run 43469807372 has a failed journey step; do not treat it as coverage-only red until logs are inspected.

**j10a review correction (manual; native UNVERIFIED):** retain polling health and tap bounds, add concurrent ItemStatus property-change event receipt, require known Hover and post-tap Idle events, and reject every action-local Listening event. No test retries or threshold changes. Hold this unpublished slice until run 43469807372 failure artifacts are available, then combine any root-cause correction before the next paid run.

**Run 43469807372 (live/manual evidence inspected):** integrated app/harness f79dfe24ee756ff21a95ec3d5e57ca84f993b1c3; 37/38 implemented checks passed (six mock cases). QD15 failed because the suite expected Write file as a title prefix, while the inspected panel screenshot shows the correct Step 1 of 2: Write file card. MainWindow.xaml.cs emits that prefix for queued actions. Correct the test to exact Step 1 of 2: Write file and exact Next step: Write file; retain raw contents, destination and separate-approval assertions. Not a product bug; no limits or retries changed. New head remains UNVERIFIED pending native run. Hosted audio receipt shows zero WinMM inputs/outputs with AudioEndpointBuilder and Audiosrv Running; share in PR54 for f0750. Voice microphone paths BLOCKED, never passed. Missing faces remain Listening, Thinking, Nudge; j8-j18 gaps remain in PR body.

**9d98843 CI failure (manual):** full-app run 43472631147 suite and red-probe never reached tests: CS0122 on protected AutomationProperty.PropertyId in j10a event subscription. Use the public notch.Automation.PropertyLibrary.Element.ItemStatus identifier (confirmed by installed FlaUI 4 metadata). Evidence run 43472732416 also rejected the QD15 live table row lacking a same-row run link; add its exact 43469807372 link. Rendered PR body now passes PR_EVIDENCE_OK. Neither failed job proves negative-control or native success at 9d98843. New-head compilation/native execution remain UNVERIFIED.

**j10a diagnostic plan (manual, native UNVERIFIED):** Independent Codex review recommends instrumentation before any product fix. QPC timestamps correlate physical tap, app mouse-up, target/placement/Show/refresh phases, dispatcher posted/started/completed operations and recorder discovery/property/lock insertion stages. An isolated QA data-folder marker enables app tracing for EyeTapFixture only. Background trace writes may perturb scheduling; compare phase overlap, never infer cause from a new green. No stack/CPU samples are yet collected; add them only if these stages cannot discriminate. Existing 100 ms health and 199 ms physical tap bounds, event assertions and zero retries unchanged. One fresh hosted run next; full suite still requires missing journeys.

**Issue78 profiling attempt (manual; execution UNVERIFIED):** Separate eye-tap-profile workflow pins app and harness to reviewed 7f8960aa83d2fbbac1f17be6f37c1cef85ad65af. No key or provider tests. One exact Q10, unchanged instrumentation/limits, no retries. Records WPR tool/profile details; one GeneralProfile+DotNET ETW capture if available, collector status/dropped-event receipt before stop, original test exit/TRX/window screenshots/timing and Boo symbols. Collector failure still runs Q10 once and keeps its verdict. CPU/native-thread attribution and usable stacks remain UNVERIFIED until ETL inspection. Diagnostic timing is not acceptance timing. Full paid workflow is not dispatched by these files.

**Run-trigger correction (manual, 14 Nov):** PR path filters use the cumulative PR diff, not just the latest commit. Push49c0236 therefore also auto-started paid full-app run43480914872:38/39, Q10 read174ms>100ms. Earlier no-paid-run claim was false. Make full-app-qa and eye-tap-profile workflow_dispatch-only before further pushes, so approved one-profile attempt43480914897 is not duplicated and live suite cost is deliberate. This changes scheduling only, not tests, assertions, skips or acceptance. Profile still running; ETW unavailable until cleanup.

### Blocker sprint: native voice E2E (13 Nov, proposed)

- `qa/native-voice-e2e`, based on lane/face b7e3e80: separate `tools/Boo.NativeVoice` + `native-voice-e2e.yml`; no #54 suite/workflow edits. #54 coordination: https://github.com/vikas53953/bluey/pull/54#issuecomment-6858672215.
- CI-gated synthetic PTT uses the normal published app controls, upload queue, Realtime client, transcript UI and AudioSession.Play buffer. HostedVoiceAudio substitutes only audio devices: validated <=8s PCM WAV in, consuming virtual output device out. Environment checks are policy signals, not host attestation. Never invoke on a personal PC.
- One response per bounded session, 128 output-token ceiling, tools disabled; configurable cheap realtime/transcription defaults. No retries. Estimate <$0.01 per dispatch, not an enforced monthly dollar cap; actual billing UNVERIFIED. Positive and deliberate wrong-answer red runs pending.
- Physical microphone/speakers, shipped default model and other voice journeys stay UNVERIFIED/manual. Existing provider-only #47 smoke remains unchanged and separate. No native voice PASS claimed before artifacts/screenshots and independent final-head review.

- First native runs on de77dc7 failed honestly before input: injected keyboard chord is intentionally ignored by the production hook (no PCM receipt, idle Boo screenshot). Journey now uses the existing notch-eyes mouse hold, leaving that safety guard intact. Mutation pass killed 5/6; a self-hosted mutant survived because the negative test also lacked an evidence path. The tools-only test now supplies valid remaining options and asserts the precise host-gate failure. New-head evidence pending.

- Hosted synthetic native voice proof on `37f2a847742678a3a10b2218da496feb4ed4e1b8`: [positive 43466425398](https://github.com/vikas53953/bluey/actions/runs/43466425398) native 1/1 and boundary 7/7, zero skipped. FlaUI held notch eyes, all 226,056 PCM bytes entered normal PTT; OpenAI mini + mini-transcribe returned 5; UI/notch screenshots inspected, virtual output consumed 24,960 bytes (22,275 nonzero). [Red 43466455113](https://github.com/vikas53953/bluey/actions/runs/43466455113) first proved the same normal journey, then correctly failed the planted seven expectation. Physical audio stays UNVERIFIED/manual.
- Mutation improvement: final six selected compiling mutants all killed (one assertion failure each), restored 7/7; [Codex report](https://github.com/vikas53953/bluey/pull/68#issuecomment-6859316959). No paid mutation calls. Windows CI rejected the two-line MainWindow growth (2681 > 2679); moved EnsureRealtime unchanged into MainWindow.NativeVoice.cs, no budget or frozen test changes. New final-head native evidence and separate review pending; no whole-PR QA PASS yet.

- Final replay 43468001613 on396a29f failed honestly: real five and consumed audio arrived before input transcription; the test asserted the user row too early. Independent Codex review identified this observer race. The same 45s bounded wait now requires the question transcript as well as completed reply and consumed audio; the final question assertion is retained. No timeout increase, request retry, app behavior change, frozen edit or relabelled pass. Review/app source remains396a29f; new tools-only observer head needs hosted proof.

**14 Nov PR54 conflict resolution (manual):** Merged lane/face f3039ff6849c1bb17d94b1472fd433fb37fcb4dd (#68 native voice). Retained both documentation histories. EnsureRealtime remains exactly in upstream MainWindow.NativeVoice.cs; EnsureResponses remains in QA MainWindow.TextProviders.cs with folder callback. Removed only their duplicate conflicted declarations from MainWindow.xaml.cs. Upstream frozen tests/oracle imported unchanged; oracle26/147 and25historylinks verify. Featuremap82/41methods, ratchet, report selftests, workflow scans pass. No paid/profile run dispatched; new integrated compile/native behavior UNVERIFIED. #68 evidence is upstream scoped voice proof, not full j8-j18 QA acceptance. Issue78 still unresolved.

**14 Nov issue78 offline result (manual):** docs/verification/issue78-offline-analysis.md records existing-ETL metadata/DIA analysis. Four native PDB GUID/age pairs validated;4560additional frame occurrences named,31043/36843still unnamed. Managed/native UI-thread mapping, event-loss localization and exclusive repair hotspot remain UNVERIFIED. No product fix/capture/AI run. Full-app NOT PASSED.

### PR #54 base sync after #39 (14 Nov 2026)

**manual:** merged lane/face `169e23c` and retained its demo-video workflow/recorder unchanged. The only conflict was adjacent decision-log records; both histories remain. QA assertions, limits, frozen tests and manual-only full-app dispatch are unchanged. Local gates recorded in the PR; exact-head CI and integrated native behavior remain **UNVERIFIED**. Full-app remains **NOT PASSED**.

### PR #54 sync to 754e7e8 (14 Nov 2026)

**manual:** retained both documentation histories while syncing the feature branch to lane/face `754e7e8d89fff58abb551283eb4f758f741da8bc`. Normal contract gate on d727001 passed ([43556936940](https://github.com/vikas53953/bluey/actions/runs/43556936940)); failed gate [43556953548](https://github.com/vikas53953/bluey/actions/runs/43556953548) is the intended unretired AM0 red proof, confirmed in logs. No gate weakening or new product repair. Exact-head CI and full-app acceptance remain **UNVERIFIED / NOT PASSED**.

### PR #54 sync after #91 (14 Nov 2026)

**manual:** synced lane/face `c74d0fcd58aa69c5c1cfbd66555879f6971ba30d`, retaining both handoff histories. Restored this file after a failed encoding write in the preceding commit. Full-app dispatch requested; app and harness pinned to the new head. Coverage remains **UNVERIFIED**; no QA PASS. Documented run estimate $0.03-0.05; cumulative provider billing remains UNVERIFIED.

### PR #54 bounded j12g addition (14 Nov 2026)

**manual:** Q12G checks one Ctrl+Alt+F12 while already Paused: identical session ID/status and panel status, Stopped acknowledgment, three-second pinned retention, action-local healthy observations and known pre/post UIA event delivery. Corrected independent-review findings (missing extension namespace; reject transient Happy before explicit End). Contract version2 maps the new method; feature row F15 is **UNVERIFIED**. Full j12 stays required and incomplete. Existing pinned full-app run43565296906 predates this addition; it cannot prove Q12G. Compilation/native behavior pending GitHub CI.

### PR #54 j12e regression before repair (14 Nov 2026)

**manual:** Q12E requires a real actionable proposal and verifies the physical right-click hit on the visible pocket button (Boo process/name/type), never the oversized window centre. Strict UIA reads, exact saved task folder/destination, enabled Approve and no file effects; one cheap model request, one click, no retries. Contract3 adds Q12E; F16 remains **UNVERIFIED**. Existing focused workflow takes an explicit manual approval_stop input; default PR run stays no-key Q12G. Product handler is unchanged to obtain a native regression before the suspected missing acknowledgment repair. No bug or PASS claimed before live receipt inspection.

### PR #54 harness corrections after #48 (14 Nov 2026)

**manual:** synced lane/face `bfc7288` (#48). Failed contract gate43569050438 correctly found five newly integrated offline SpokenAudio methods absent from the contract; version4 now requires ST0-ST4, labelled mock in reports. Q12E build failed xUnit2010 (string comparison assertion); use the equivalent case-insensitive Assert.Equal. Both focused runs stopped before native Q12E; neither proves the suspected right-click bug. No product repair, skip, retry or threshold change. Exact-head compilation/native behavior remain **UNVERIFIED**.
