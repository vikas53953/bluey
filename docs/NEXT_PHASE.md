# Next phase: Phase 3, the outer loop (release-time fuzz)

Status: **DRAFT PLAN, docs only.** Written 14 Nov 2026 on `lane/face` at `75ef95d`. Nothing here is built.
Source of truth: `docs/plans/agent-trust-plan.md` (cited below as "plan, section N"). Where this page and the plan
disagree, the plan wins until Vikas decides otherwise.

**Hard rule from Vikas (13 Nov, plan "Vikas's decisions" note and section 7):** no Phase 3 PR merges until the
Phase 1 gate **and** the Phase 2 gate have passed. Plan section 5, step 6 adds: Phase 2's `live-journeys` check must
first be a required check and stable for **2 weeks**. So this page is a plan to have ready, not a start signal.

Words used here, once:

- **Fuzz** = a robot that uses Boo like a careless user, with random but repeatable clicks and keys, for a long time.
- **Oracle** = the rule that says "this is a bug" (for example: Boo crashed, or Stop did not go quiet in 2 s).
- **Minimiser** = after a failure, replay with fewer and fewer steps until the shortest list that still fails.
- **Red proof** = we plant a bug on purpose and show the new check turns red. A check that never went red proves nothing.
- **Gate** = the "Boo – Phase N gate" run of plan section 7: the whole app, live, on GitHub's VM.

---

## 1. Where we are (checked 14 Nov 2026)

How checked: `git log origin/lane/face`, `gh pr list --state merged --base lane/face`, `gh pr list --state open`, and
branch protection read from the GitHub API. "Not started" means no merged or open PR was found for it.

### 1.1 Phase 1: the deterministic layer (plan section 2.9)

| # | Item | Status |
| --- | --- | --- |
| 1.1 | Ratchet of known-bad patterns | **merged** #21 (+ #23); `quality` is now a required check on `lane/face` |
| 1.2 | Architecture references | **open** #32 |
| 1.3 | Banned APIs + canary | **open** #28 (build facts it needs: **merged** #37) |
| 1.4 | Threading + catch analyzers | **not started** |
| 1.5 | No-suppress + gate manifest | **open, draft**: #85 (gate manifest), #106 (warning-disable gate) |
| 1.6 | Comment lint | **open** #51 |
| 1.7 | Key scan everywhere | **merged** #26 (+ #46) |
| 1.8 / 1.9 | GitHub VM only, Boo-window screenshots only | **merged** #18 |
| 1.10 | Evidence table in every PR | **merged** #34 |
| 1.11 | NetArchTest architecture rules | **merged** #31 |
| 1.12 | Trust-metrics baseline | **merged** #36 |
| C1 | No `async void` | **open** #60 |
| C3 | One injected clock (`TimeProvider`) | **merged** #33 (also Phase 2 item 2.3) |
| C2, C4, C5 | Dispatcher, delay, static state cleanups | **not started** (UNVERIFIED: no PR found by title) |

Also merged since the plan, outside its numbering: live smoke on every PR (#20, #47), pass^3 for real-AI paths (#86),
daily live sentinel (#91), silent-voice evidence (#118), native voice on the hosted VM (#68).

### 1.2 Phase 2: verification (plan section 3.3)

| # | Item | Status |
| --- | --- | --- |
| 2.1 | Notch lifecycle statechart (doc) | **merged** #27 |
| 2.2 | TLA+ model + TLC check | **merged** #29 (+ #57); `formal` is not a required check (HANDOFF known issue 14) |
| 2.3 | One injected clock | **merged** #33 |
| 2.4 | `NotchLifecycle` reducer in Core | **merged** #43 (no app wiring; only `src/Boo.Core/NotchLifecycle.cs` names it) |
| 2.5 | FsCheck properties | **open** #101 |
| 2.6 | Wire the app to the reducer (4 PRs) | **not started** |
| 2.7 | Conformance bridge (model → reducer) | **not started** |
| 2.8 | Video in demo-ci and live smoke | **demo part merged** #39; live part not done (waits for Vikas, HANDOFF #39 row); demo visual gate **open** #95 |
| 2.9 | `tools/Boo.Drive` command-line driver | **not started** (no `tools/Boo.Drive`); the app explorer #69 / #96 is the closest thing, **open** |
| 2.10 | `live-journeys` as a required check | **not started** (not in `lane/face` protection) |
| 2.11 | Every notch state tested live | **part 1 merged** #48; journey list j0–j18 **open** #50 |

Related open quality work: full-app QA suite for the gates #54, crash-and-reopen #113, cassettes #63, test guard #61,
bug-regression rule #58, review answers #120, perf flake #65, feature-map check in CI #104.

### 1.3 Gate conditions still unmet

| Condition (source) | State today |
| --- | --- |
| Phase 1 PRs 1.2–1.6 landed (plan 2.9) | 1.2, 1.3, 1.5, 1.6 open; 1.4 not started |
| Cleanups C1–C5 landed (plan 2.9 "Done when") | only C3 merged |
| `quality` required on `lane/face` **and** `main` (plan 2.9) | `lane/face` yes; `main` requires only `demos`, `build-test-publish`, `live-ai-smoke` |
| Every rule has a red-proof link in `docs/quality/RULES.md` (plan 2.9) | UNVERIFIED (not audited for this page) |
| **Phase 1 gate run** (plan 7) | **not run**; its tool, the full-app QA suite #54, is open |
| App's talk, focus, apply/undo, presence go through the reducer (plan 3.3) | not started (2.6) |
| 5 FsCheck properties on every PR and nightly (plan 3.3) | #101 open |
| Demo and live runs attach a video (plan 3.3) | demo yes; live no |
| `live-journeys` required, then **stable for 2 weeks** (plan 5, step 6) | not required yet, so the 2-week clock has not started |
| Feature map: 0 `UNVERIFIED` rows for shipped features (plan 3.3, 2.11) | not met: e.g. E5 (no display chosen), S16 (Mute speech), S13 live update, S14 install on a PC |
| **Phase 2 gate run** (plan 7) | **not run** |
| Scheduled runs (nightly TLC, daily sentinel) | do not fire yet: GitHub runs schedules only from `main` (HANDOFF known issue 14; `docs/quality/live-sentinel.md` section 1) |

**Earliest Phase 3 merge** = Phase 2 gate passed **and** 14 days after `live-journeys` became required. Today that
date cannot be set.

---

## 2. Phase 3 in one page

**What it is (plan 4, "Goal"):** before a release goes out, a robot uses the real installed Boo like a careless user
for a long time. Every problem it finds comes back as a short, repeatable bug report with a fix PR waiting for review.

**How it works, as a picture:**

```
 published Boo.exe ──install (Velopack)──► GitHub windows-latest VM
        │
   fuzz walker ── random, seeded steps weighted by the feature map ──► Boo
        │            (hover, hotkey, send, talk, explain, research, approve/reject, undo, focus, Stop, settings)
        │
   oracles watch ── crash log │ process exit │ notch rule broken (INV-*) │ Stop not quiet in 2 s │
        │           reply stuck past deadline │ perf budget │ key-shaped text in any log
        ▼
   failure? ── re-run once ── still fails? ── minimise ── repro.json + video + screenshots + logs
        ▼
   one GitHub issue (label fuzz) ──► orchestrator: draft PR (commit 1 = failing test, commit 2 = fix)
        ▼
   separate reviewer ──► Vikas approves in GitHub ──► merge by hand. Nothing auto-merges.
```

AI is the **mock** (`tools/Boo.QaMockAi`, with scripted slow, failed and malformed replies) for the long walk, then a
**small live tail** of real OpenAI turns at the end (plan 4.1).

### 2.1 What it must never do (plan 4.2 guardrails, all enforced by `autofix_guard.py`)

1. No bot PR without **red then green proven by CI**: the new test fails on the base commit and passes on the head (two linked runs).
2. Bot PRs touch only `src/**` and new test files. **Never** `.github/**`, `scripts/quality/**`, `BannedSymbols.txt`,
   baselines, the test oracle, `AnalyzerBaseline.cs`, or security code (`ActionPolicy.cs`,
   `CaptureExclusionPolicy.cs`, `CredentialStore.cs`).
3. At most **150 changed lines and 5 files**.
4. No baseline goes up, no new suppression, no new comment outside the allowed forms.
5. All required checks green, including `live-journeys` with video.
6. A separate reviewer approves, with the evidence table.
7. **Vikas approves in GitHub.**

And from the pipeline rules: **nothing auto-merges in Phase 3** (plan 4.2); the fuzz never runs on Vikas's PC or
captures his screen (BOO-SAFE-001, PR #18); it never types, prints or screenshots a key; it never retries a failure
into green (one re-run only to confirm a failure is real, both runs logged).

**Conflict to settle before any bot PR exists:** `lane/face` merges by GitHub auto-merge once CI and `codex-verified`
are green (`docs/PIPELINE_PRINCIPLES.md`, open PR #108). Guardrail 7 needs a human approval on fuzz PRs. See decision D2.

### 2.2 Costs, inside the caps

| Item | Cap (Vikas) | Phase 3 plan |
| --- | --- | --- |
| GitHub | **$0** (public repo, hosted runners only) | Hosted `windows-latest` only. Public repo: standard runners free. Storage: failing runs only (repro + video, about 10–30 MB, plan 4.4), 7-day retention. |
| OpenAI | **$10 a month**, cheapest models | Live tail at most **10 turns per run** (decision D5). Estimate: weekly + releases ≈ 5–6 runs ≈ 60 turns a month. The sentinel's 2 calls a day cost about US$0.12 a month (`docs/quality/live-sentinel.md`), so this is **well under $1** (estimate, UNVERIFIED until measured in task 3.6). Voice turns cost more than typed; counted separately. |
| Agent time | not a cash cap | Fix PRs are written by the existing orchestrator (Claude builds, Codex reviews), not by a new bot in Actions, so no new key and no new bill (decision D4). |

Near the OpenAI cap: the live tail pauses first, then everything live, and the agents say so (plan, 13 Nov note).

### 2.3 What Vikas still decides himself

Release, tags, installer, signing, auto-update and PR #3 (`lane/face` → `main`) (plan 7; `docs/PIPELINE_PRINCIPLES.md` 8);
approving every bot PR (guardrail 7); any spend over a cap; loosening any gate; whether auto-merge is ever discussed
(plan 4.2: only after 20 hand-merged bot PRs in a row with 0 reverts and a flaky rate under 2% over 30 days); and the
10-minute hands-on use before a release (plan, 13 Nov decision 5).

---

## 3. Task breakdown

Each task is one small PR into `lane/face`, one concern, with a red proof and an evidence table (BOO-EVID-001).
"Live" in acceptance = the **published** `Boo.exe` on GitHub `windows-latest`, driven by FlaUI, real cheapest OpenAI
model where AI is involved, evidence = screenshots, video, logs and a run link. Mock-AI runs are labelled **mock**.

Planted bugs live only in a throwaway **draft "RED PROOF ONLY (do not merge)"** PR, as #114 and #72 do today.

Dependency picture:

```
2.9 Boo.Drive (or #69 explorer kit) ─► 3.1 walker ─► 3.2 state oracles ─► 3.4 minimiser ─► 3.8 issue opener
                                         │                │                                      │
                                         ├► 3.3 bad-day AI │                                      ▼
                                         └► 3.5 installed app ─► 3.6 live tail ─► 3.7 weekly    3.9 guard ─► 3.10 fix-PR flow
                                                                                                         │
                                                                     3.11 release gate ◄─────────────────┘
                                                                     3.12 metrics page (any time after 3.7)
```

### 3.1 Fuzz walker with crash oracles (mock AI, manual run)

- **Goal:** a seeded random walk drives the published `Boo.exe` for a set time and flags crash, unhandled exception or process exit.
- **Depends on:** a driver: plan item 2.9 (`tools/Boo.Drive`) or the app explorer kit of #69 once merged. Do not copy either.
- **Files (new):** `tools/Boo.Fuzz/Boo.Fuzz.csproj`, `tools/Boo.Fuzz/Walker.cs`, `tools/Boo.Fuzz/Actions.cs`,
  `tools/Boo.Fuzz/CrashOracles.cs`, `.github/workflows/release-fuzz.yml` (manual `workflow_dispatch` only),
  `docs/quality/fuzz.md`. **Size M.**
- **Acceptance (mock):** a 30-minute run on `windows-latest` (plan 4.4, row 3.1) against the published `Boo.exe`; a
  step log with the seed; screenshots every N steps; the crash-log folder uploaded; run link. Same seed = same steps (two runs compared).
- **Red proof:** a draft PR plants a crash on the 3rd Stop; the walk finds it and names the step. Run linked.
- **UNVERIFIED after:** real-AI behaviour; installed (not portable) app; anything the walk's action list does not reach.

### 3.2 State oracles (notch rules, Stop, stuck reply, keys in logs)

- **Goal:** add the rest of plan 4.1's oracles: notch rules `INV-*` read through UI Automation, Stop quiet within 2 s,
  a reply stuck past the response deadline, perf counters over budget, key-shaped text in any log.
- **Files (new):** `tools/Boo.Fuzz/StateOracles.cs`, `tools/Boo.Fuzz/LogKeyOracle.cs` (reuses `scripts/quality/key_scan.py`
  patterns, never a real key). **Size M.**
- **Rules it checks:** only the ones today's code **keeps** (`docs/architecture/notch-lifecycle.md` section 4 and
  `specs/NotchLifecycle.cfg`). Rules today's code breaks (`NotchLifecycle_plan_gaps.cfg`, e.g. INV-PRIV-2, known issue
  13 / F10) start as **known findings**, listed, not red, so the fuzz is not red from day one.
- **Acceptance (mock):** 30-minute run, all oracles report "checked N times, 0 broken", with counts in the log.
- **Red proof:** one planted bug per oracle (e.g. Stop leaves the mic on; a fake key-shaped string written to Boo's
  log). Each turns the run red with the oracle's name.
- **UNVERIFIED after:** rules not readable through UI Automation (internal state only).

### 3.3 Bad-day AI replies (slow, failed, malformed)

- **Goal:** the mock answers slowly, with errors (429, 500), or with broken JSON on a seeded schedule.
- **Files:** prefer new `tools/Boo.Fuzz/FaultScripts/*.json` read by the mock. **Conflict risk:** `tools/Boo.QaMockAi/Program.cs`
  is changed by open #93; the bad-day set of open #63 (cassettes) should be reused, not copied. Wait for both. **Size S.**
- **Acceptance (mock):** run shows each fault class reached at least once, and Boo's visible error text for each (screenshots).
- **Red proof:** planted bug that hangs on a 500; the "stuck reply" oracle catches it.
- **UNVERIFIED after:** real provider faults (only the live tail and the sentinel see those).

### 3.4 Minimiser and repro bundle

- **Goal:** on a failure, re-run once from the seed; if it fails again, shrink the steps to the shortest list that still fails.
- **Files (new):** `tools/Boo.Fuzz/Minimiser.cs`, `tools/Boo.Fuzz/Repro.cs`; bundle = `repro.json` + video + screenshots + Boo's logs, key-scanned before upload. **Size M.**
- **Acceptance (mock):** planted bug's trace shrinks to **under 10 steps** (plan 4.4, row 3.2); the bundle replays red on its own in a second run.
- **Red proof:** same planted bug; plus a "flaky" planted bug that fails 1 time in 3 must **not** produce a repro (confirms the re-run rule).
- **UNVERIFIED after:** bugs that need wall-clock timing the replay cannot match.

### 3.5 Fuzz the installed app

- **Goal:** install Boo with the Velopack installer on the VM and fuzz the installed copy, as plan 4.1 says.
- **Files:** `.github/workflows/release-fuzz.yml` (install step). **Size S.**
- **Acceptance (live install, mock AI):** screenshots of the install, Boo started from its installed path, then the walk.
  This is also the first proof of feature-map row S14 "install on a PC" (on a VM, so labelled as such).
- **Red proof:** point the step at a broken installer file; the job fails before the walk starts.
- **UNVERIFIED after:** install on a real PC, uninstall, auto-update (S13).

### 3.6 Live tail: real OpenAI turns at the end

- **Goal:** after the mock walk, at most 10 real turns (typed and voice) on the cheapest models, through the same oracles.
- **Files:** `tools/Boo.Fuzz/LiveTail.cs`; workflow step reusing the live smoke's anti-mock check and key handling. **Size S.**
- **Acceptance (live):** real typed and voice answers visible in screenshots; voice passes the real-speech rule of #48 /
  #118 (raw audio kept); a cost line in the log (turns used, model names, never the key).
- **Red proof:** a run with the mock still answering in the live tail must fail the anti-mock check.
- **UNVERIFIED after:** behaviour of models other than the cheapest; quota-exhaustion handling.

### 3.7 Weekly run and flaky rate

- **Goal:** run the fuzz weekly (decision D1) and record flaky rate and fuzz yield in trust metrics.
- **Files:** `release-fuzz.yml` (schedule), `scripts/quality/trust_metrics.py` (one new metric). **Size S.**
- **Blocker:** schedules fire only from `main` (known issue 14). Until #3, a run is started by hand (decision D3).
- **Acceptance:** **7 logged runs** (plan 4.4, row 3.3) with links; flaky rate printed.
- **Red proof:** a planted always-failing oracle shows in the metric as a failure, not as "flaky".

### 3.8 Fuzz → one GitHub issue per unique failure

- **Goal:** each minimised failure opens or updates **one** issue (label `fuzz`), deduplicated by a failure signature.
- **Files:** `scripts/ci/fuzz_issue.py` (with `--self-test`), workflow step with `issues: write` only. **Size S.**
- **Acceptance:** two runs hitting the same planted bug make **one** issue with two run links; the issue carries the repro bundle link.
- **Red proof:** self-test with two different signatures must make two issues; same signature must not.

### 3.9 `autofix_guard.py` (the 7 guardrails as a check)

- **Goal:** a check that fails any PR labelled `fuzz-bot` that breaks guardrails 1–4, and reports 5–7 as pending.
- **Files (new):** `scripts/quality/autofix_guard.py`, `.github/workflows/autofix-guard.yml`, `docs/quality/autofix-guard.md`. **Size S.**
- **Acceptance:** green on a clean sample bot PR; red with a named reason on each planted bad one.
- **Red proof:** four throwaway PRs: touches `.github/**`; 200 lines; raises `baseline.json`; test that was never red on base.
- **Note:** a new workflow is a gate file (HANDOFF known issue 0): Vikas reviews it.

### 3.10 Fix-PR flow, end to end once

- **Goal:** one **real** fuzz finding goes issue → draft PR (commit 1 failing test, commit 2 fix) → separate reviewer → Vikas approves → merged by hand (plan 4.4, row 3.4).
- **Who writes it:** the existing orchestrator picks up `fuzz` issues; no bot with write access in Actions (decision D4).
- **Files:** none new beyond the fix itself and its test. **Size M** (the fix size is unknown until the finding exists).
- **Acceptance (live):** the fix PR's own `live-journeys` run with video, red run on base, green run on head, guard green.
- **UNVERIFIED until it happens:** the whole flow. If the fuzz finds nothing real, this task stays open; we do not invent a finding.

### 3.11 Gate releases on the fuzz

- **Goal:** on a `v*` tag, `release.yml`'s publish step waits for a green 60-minute fuzz run on the same commit (plan 4.1, 4.4 row 3.5).
- **Files:** `.github/workflows/release.yml`, `release-fuzz.yml`. No open PR touches them today. **Size S.**
- **Acceptance:** a dry run (manual dispatch with a "pretend tag" input that never publishes) shows publish waiting, then running after green.
- **Red proof:** the same dry run with a planted red fuzz stops before publish.
- **UNVERIFIED:** a real tagged release (tags are Vikas's alone).

### 3.12 Trust-metrics page for Phase 3

- **Goal:** add fuzz yield and bot-PR results (merged by hand, reverted, reviewer verdict) to the weekly metrics and Vikas's visual page.
- **Files:** `scripts/quality/trust_metrics.py`, `docs/quality/trust-metrics.md`. **Size S.**
- **Acceptance:** numbers match a hand count from the Actions and PR lists for one week.
- **Done when (plan 4.4):** 8 weeks of data, so the auto-merge question can be answered with data.

---

## 4. Risks and how we handle them

| Risk | Mitigation | Owner / decision |
| --- | --- | --- |
| **OpenAI cost overrun** | Hard cap of 10 live turns a run; mock for the walk; cost line per run; near $10 the live tail pauses first | Orchestrator acts; crossing the cap is Vikas's |
| **Flaky provider** (14 Nov: the sentinel's voice answer was silent while its text said "5", runs 43575595540 / 43576162645, cause UNVERIFIED) | Live-tail failures are labelled "provider" vs "Boo" using #118's raw-audio record; provider-only failures go to the sentinel issue, not a fix PR | Orchestrator |
| **False alarms from the VM** (focus stealing, slow VM, the reveal fps swings of known issue 4) | Re-run once from the repro; only failures that repeat open issues; perf oracle uses the existing budgets, not new ones | Orchestrator; any budget change is a reviewed PR |
| **Fake greens** (oracle never fires, steps silently skipped) | Every oracle prints "checked N times"; a run with 0 checks fails; red proof per oracle; `assert-tests-ran.ps1` rule applies | Codex verifier |
| **Bot "fixes" the test, not the bug** | Guardrails 1, 2, 4; frozen oracle; separate reviewer | `autofix_guard.py` + reviewer |
| **Bot PR spam** | One issue per failure signature; at most 2 open fuzz fix PRs at once; fix PRs only for findings that replay | Orchestrator |
| **Auto-merge slips a bot PR in** | Bot PRs stay draft and labelled `fuzz-bot`; verifier never sets `codex-verified` without Vikas's approval | **Vikas (D2)** |
| **Secrets** | Fuzz never types a key; key scan on every upload; live key only from `BOO_LIVE_*_KEY`; logs record lengths only | Orchestrator |
| **Runner minutes / storage** | Public repo, hosted runners free; weekly not nightly; only failing runs upload, 7 days | Vikas (D1) |
| **Merge-queue contention** (many open PRs; `HANDOFF.md`, `docs/DECISIONS.md`, `docs/FEATURE_MAP.md` in most of them) | Phase 3 work goes in new files under `tools/Boo.Fuzz/` and new workflows; HANDOFF/DECISIONS rows added last, after a sync | Orchestrator |
| **Schedules do not run before #3** | Manual runs until then; do not move work to `main` to get a schedule | **Vikas (D3)** |
| **No driver yet** (2.9 not started) | Build 2.9 or merge the explorer kit (#69) first; never a second driver | Orchestrator |

---

## 5. Open decisions for Vikas (one line each, with a recommendation)

| # | Decision | Recommendation |
| --- | --- | --- |
| D1 | Fuzz cadence | **Weekly + every release** (plan 6, decision 8); nightly only if the weekly yield stays high. |
| D2 | How bot PRs stay out of auto-merge | **Draft + `fuzz-bot` label; the verifier never sets `codex-verified` on them until you approve in GitHub.** |
| D3 | Runs before #3 reaches `main` | **Start by hand from `lane/face`** until #3; no workflow change on `main` just for a schedule. |
| D4 | Who writes fix PRs | **The existing orchestrator (Claude builds, Codex reviews)**; no bot with write access, no new AI key. |
| D5 | Live tail size | **10 turns a run (8 typed, 2 voice), cheapest models**, inside the $10 cap. |
| D6 | Red fuzz blocks a tagged release | **Yes**; only you can override, in writing. |
| D7 | Branch protection before bot PRs (plan 6, decision 4; known issue 0) | **Turn on `enforce_admins` and CODEOWNERS for gate files before task 3.9.** |

---

## 6. First three PRs once the gates pass

1. **3.1 Fuzz walker with crash oracles** (M), on top of the driver from 2.9 or #69.
2. **3.2 State oracles** (M): the notch rules, Stop-quiet and key-in-log checks, each with its red proof.
3. **3.4 Minimiser and repro bundle** (M): so the first real finding arrives as a short, replayable bug.

Before those three, the work that unlocks them is Phase 1 and 2's open items in section 1.3, in the order of
`docs/PIPELINE_PRINCIPLES.md` "Limits" (full-app E2E #54 first).
