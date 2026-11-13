---
name: verify-boo
description: Prove a Boo feature works by driving the REAL published Boo.exe the way a user does (FlaUI), with a real AI key where AI is involved, and keep screenshots, logs and run links as evidence. Use before calling any Boo feature or fix "done", before opening or approving a PR, when asked to verify / QA / smoke-test Boo, and whenever a feature is added or changed (then also update docs/FEATURE_MAP.md).
---

# verify-boo

Boo is a Windows app. A green unit test is not proof that a user can use a feature. This skill proves features on the
real app and keeps the evidence. It has two halves: **verify** (run the proofs) and **maintain** (keep the map of what
proves what honest).

## The prove-it rule (non-negotiable)

**No Boo feature is "done", "fixed" or "working" until a LIVE run proves it, with evidence attached.**

- **Live** means the published `Boo.exe`, driven like a user, nothing faked. AI features need a real provider and a
  real key. The QA mock (`tools/Boo.QaMockAi`, demo-ci) proves Boo's UI and logic, **not** the AI: call it "mock".
- **Evidence** means all three: screenshots (`*.png`), the observation/console logs, and a run link (GitHub Actions run
  URL) or, for a local run, the `summary.md` path plus the commit SHA it ran on.
- Label every claim **live**, **mock**, **manual** or **UNVERIFIED**. A claim with no run link is **UNVERIFIED**.
- A run that could not happen (no key, no runner, secret missing) is **BLOCKED**, never green. Say so in plain words.
- Never weaken, skip, or edit a frozen test (`tests/**`, `docs/verification/test-oracle.json`) to get green. New live
  checks go in `tools/Boo.VerifyLive`.
- Never print, log or screenshot a key. Keys come from repository secrets or a shell variable only; the evidence is
  scanned for them.

## What proves what

The full list is `docs/FEATURE_MAP.md` (every feature, its label, the exact test or checklist step). The headline
journeys, all on the real `Boo.exe`:

| Feature | Live proof (real key) | Also |
| --- | --- | --- |
| Notch wakes on **hotkey** (Ctrl+Alt+B) | `LiveJourneyTests.J0_NotchWakes_OnHotkey` | ui smoke step 1 (every push) |
| Notch wakes on **hover** | `LiveJourneyTests.J1_NotchWakes_OnHover` | ui smoke step 10 (every push) |
| **Typed chat** with a real AI reply | `LiveJourneyTests.J2_TypedChat_GetsARealAnswer`, `LiveRoundTripTests` | - |
| **Screen explain**: pointer + ring | `LiveJourneyTests.J3_ScreenExplain_PointerAndRingLandOnTheTarget` | demo-ci step 1 (mock) |
| **Research** with source links | `LiveJourneyTests.J4_Research_ShowsRealSourceLinks` | demo-ci step 3 (mock) |
| Permissioned **Apply / Undo** | `LiveJourneyTests.J5_ApplyThenUndo_OnlyAfterApproval` | demo-ci step 2 (mock) |
| **25-minute focus** session | `LiveJourneyTests.J6_FocusSession_25Minutes_OnTheDeveloperModeClock` | runbook 11 (real-time, manual) |

About the 25 minutes: Boo already has a test clock, **Developer Mode** (focus time runs 4x, a check every 10 s). The
live journey runs a real 25-minute session on that clock: about 375 real seconds, with real AI focus checks. No new
test hook was added. A 25-minute session on the **real-time** clock is still a manual check (runbook section 11);
never call that part live until a person has run it.

## How to verify (pick by what you have)

### 1. Live, on GitHub (the normal way; needs the `BOO_LIVE_OPENAI_KEY` repository secret)

```powershell
gh workflow run live-ai-smoke.yml --ref <your-branch> -f text_provider=openai -f journeys=true   # always GitHub's own Windows VM
gh run list --workflow live-ai-smoke.yml --branch <your-branch> -L 1     # get the run id and URL
gh run watch <run-id> --exit-status
gh run view <run-id> --log | Select-String 'j[0-6]-|live-01|test gate'   # observations + gates
gh run download <run-id> -n live-ai-evidence -D artifacts/live-evidence-<run-id>   # screenshots of Boo's windows only
```

Pass needs both gates green: "Test gate (the live test ran and passed)" and "Test gate (every live journey ran and
passed)", plus "Screenshots are Boo windows only". A red `preflight` job means the key secret is missing: that is
BLOCKED, not a product failure. Only ONE live run may wait in the queue (`concurrency: live-ai-smoke`): a new dispatch
cancels another branch's WAITING run, so dispatch only when none is waiting, and check the run's head SHA before you
trust its result (a failed dispatch can leave you looking at an older run).

### 2. By hand, only inside a throwaway Windows VM (never on a personal PC)

These suites drive the real mouse and keyboard, and in the live suite Boo sends a picture of the screen to the AI. So
they never run on Vikas's or a developer's own PC (decision log: "never capture the owner's real screen"; the live
smoke runs only on GitHub's VM). The script refuses unless it runs on a GitHub-hosted runner or you pass
`-ThrowawayVm` inside a disposable VM. Even there, evidence screenshots are of Boo's own windows only.

```powershell
# Inside a throwaway VM only. No key: notch hover + hotkey, focus controls, pause hotkeys, appearance, accessibility
powershell -ExecutionPolicy Bypass -File .claude/skills/verify-boo/scripts/Invoke-VerifyBoo.ps1 -Suite ui -ThrowawayVm

# Inside a throwaway VM only. Real key, this shell only (never in a file): the live smoke + all seven live journeys
$env:BOO_LIVE_OPENAI_KEY = '<paste from your password manager>'
powershell -ExecutionPolicy Bypass -File .claude/skills/verify-boo/scripts/Invoke-VerifyBoo.ps1 -Suite live -ThrowawayVm
Remove-Item Env:BOO_LIVE_OPENAI_KEY
```

The script publishes `Boo.exe` from your checkout (or takes `-Exe <path>`), runs each suite through the existing
harness, gates it with `scripts/ci/assert-tests-ran.ps1`, scans the evidence for keys, and writes
`artifacts/verify/<UTC time>/summary.md` with PASS / FAIL / BLOCKED per suite and every screenshot path. It needs the
.NET SDK from `global.json` and no running Boo (Boo is single-instance). Exit code 0 only when every suite passed.

### 3. Mock and unit gates (every push, automatic)

`windows-ci` (unit tests, ui smoke, perf, notch gallery) and `demo-ci` (the four demos against the mock AI) run on every
push and pull request. They must be green on the PR head, but they are **mock** or **no-AI** evidence: they never
replace the live run for an AI feature.

## Reporting (what a "done" message or PR body must contain)

An evidence table, one row per feature or check you touched, every row with a label and a run link:

| Feature / check | Label | Evidence |
| --- | --- | --- |
| Research shows real source links | live | https://github.com/vikas53953/bluey/actions/runs/NNN (`j4-research.txt` lists the "Open source ..." links read through UI Automation; the panel image `j4-research-sources.boo-<n>.png` shows the answer, but its Sources list can be cut off at the bottom, so it is supporting evidence only) |
| Research edge cases | mock | windows-ci run link (ResearchSourcesTests) |
| Pointer at 150 % scaling | manual | runbook 4.2, not run yet = UNVERIFIED |

Then one line: `QA PASS — <full head SHA>` or `QA FAIL — <full head SHA>: <reason>`. Look at the screenshots yourself
before writing PASS: a test that passed with a wrong-looking screen is a finding, not a pass.

Screenshots are of Boo's own windows only (never the desktop): each capture moment saves one image per visible Boo
window, `<moment>.boo-0.png`, `<moment>.boo-1.png`, ..., and `<moment>.capture.txt` says which number is which window
(a line whose title STARTS with `Boo companion`, in live runs `'Boo companion [QA: capturable]'`, is the panel;
`'Open Boo companion'` is the notch; `'Boo screen explanation'` is the pointer/ring overlay). Match the start of the title,
never a substring: "Boo companion" also appears inside the notch's name, and the notch is often `boo-0`. The numbers
follow window order, so read the `.capture.txt` before citing an image; never assume `boo-0` is the panel. Say in the
evidence when a claim rests on a UI Automation read (for example `j4-research.txt`) rather than on what the image shows.

## How to maintain (keep the skill and the map true)

Run this whenever a feature, a test or the map changes, and before every PR:

```powershell
powershell -ExecutionPolicy Bypass -File .claude/skills/verify-boo/scripts/Test-FeatureMap.ps1
```

It fails when a map row has no valid label, cites a file or `file.cs::Method` that does not exist, claims "automated"
without citing a proof, or when any live/native test (`tools/Boo.VerifyLive`, `tools/Boo.LiveSmoke`,
`tests/Boo.Ui.Tests`, `tests/Boo.Demo.Tests`, `tests/Boo.Perf.Tests`) is not on the map.

When you add or change a feature:

1. Add or update its row in `docs/FEATURE_MAP.md`. New features start as **UNVERIFIED**.
2. If a user can reach it in the app, add a live step to `tools/Boo.VerifyLive/LiveJourneyTests.cs` (next `[Fact, Step(n)]`;
   reuse the fixture helpers; assert on UI Automation state, pixels or files, never on Boo's own text alone; capture
   `jN-*.png` and write `jN-*.txt`). If FlaUI cannot reach it (voice, audio, multi-monitor), label it **manual** and
   name the runbook step.
3. Run `Test-FeatureMap.ps1`, then the live workflow with `journeys=true`, and put the run link in the PR.
4. Only then move the row to **automated live**.

When a live step fails: read its `jN-*-FAILED.png` and `jN-*-FAILED.txt` and Boo's status line in the observation
first (logs before guesses). A provider error (401, quota, model unavailable) is BLOCKED by the account, not a Boo bug;
say which.

## Files

| File | Role |
| --- | --- |
| `.claude/skills/verify-boo/SKILL.md` | This guide: the rule, the commands, how to report and maintain |
| `.claude/skills/verify-boo/scripts/Invoke-VerifyBoo.ps1` | Local runner: publish, run suites, gate, leak-scan, summary.md |
| `.claude/skills/verify-boo/scripts/Test-FeatureMap.ps1` | Drift check for `docs/FEATURE_MAP.md` |
| `docs/FEATURE_MAP.md` | Every feature, its label, and what proves it |
| `tools/Boo.VerifyLive/` | The seven live journeys (real key; reuses `BooAppFixture` + `LiveAppFixture`) |
| `tools/Boo.LiveSmoke/` | The single live round trip and the shared `LiveAppFixture` |
| `.github/workflows/live-ai-smoke.yml` | Manual live workflow; `journeys=true` adds the live journeys |
| `scripts/ci/assert-tests-ran.ps1` | The no-empty-green gate every suite goes through |
