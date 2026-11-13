# Trust metrics

How far can we trust the agents' work? These numbers come only from git history and GitHub's own PR and Actions
records, so anyone can recount them. They start the measuring that Phase 3 of the agent-trust plan
(`docs/plans/agent-trust-plan.md`, section 4.3) needs before auto-merge can even be discussed.

- **Made by:** `scripts/quality/trust_metrics.py` (read-only: `gh api` GET calls and `git log` / `git show` only).
- **Raw data:** `docs/quality/trust-metrics/snapshot-2026-11-08_2026-11-13T06.json`. Every number below is computed
  from this file alone. PR descriptions and comments are kept only as their verdict lines.
- **Weekly:** the `trust-metrics` workflow (manual or Mondays) makes the same table for the last full week and
  uploads it for 7 days. It is not a required check. Its token cannot read branch protection, so there the two
  rows that need the required-check list say UNVERIFIED; a run with the owner's `gh` login (below) fills them.

## First table: 8 Nov 2026 00:00 UTC to 13 Nov 2026 06:00 UTC

This is the whole history of the repo's Actions up to the window's end (the first run was on 8 Nov). The block
below is the script's output, unedited.

<!-- generated: start -->

Window: 2026-11-08T00:00:00Z to 2026-11-13T06:00:00Z (UTC; start included, end excluded). Repo `vikas53953/bluey`; base branches: lane/face, main. End-of-window `lane/face` commit: `0122ac745354d5ea62ae910cbff635e41335d716`. Snapshot sha256: `940ef180d24eac358e5e8c78e6ccdf1dfa960dc4027491243032f759150919c8`.

| Metric | Value | Why it matters |
| --- | --- | --- |
| PRs merged per week (into lane/face or main) | 2026-W45: 4, 2026-W46: 17 (total 21) | How much change lands; every other rate below is per this. |
| Revert rate | 0/21 = 0.0% | A revert means a merged change was wrong and the gates let it through. |
| Merged PRs with a `QA PASS <commit>` line | 16/21 = 76.2% | The pipeline says no merge without a QA verdict; this shows how often that held. |
| ... naming the exact head that merged | 16/21 = 76.2% | A PASS on an older commit does not prove the code that merged. |
| Reviewer FAIL rounds (PRs opened in window) | 3 rounds, mean 0.10 per PR; PRs with any: #24: 2, #28: 1 (lower bound); plus 4 rounds blocked by storage or budget | A fresh reviewer catching problems is the safety net working; a falling count means authors got better. |
| PRs that went red, then green at the merged head | 11/21 = 52.4% (#4, #11, #12, #13, #14, #16, #17, #18, #19, #20, #25) | Shows the checks are actually stopping bad commits, not just passing. |
| Flaky rate (same workflow + same commit, both pass and fail) | 19/743 = 2.6%; by workflow: demo-ci: 4, live-ai-smoke: 2, release: 2, windows-ci: 11 | Target under 2%. A flaky check trains people to re-run instead of read; above 2% the checks are noise. |
| Flaky, strict (one run whose re-run attempt flipped the result) | 10/1125 = 0.9% | The broad rate above also counts a push run against a pull-request run of one commit, and a pull-request run tests the merge with the base; the strict rate is same code, same trigger. |
| Runs that were re-run | 43/1165 = 3.7% | Re-runs hide red; each one should have a reason. |
| Required-check failures by job (failed attempts) | build-test-publish: 90, demos: 54, live-ai-smoke: 1; required: lane/face: build-test-publish, demos, live-ai-smoke, main: build-test-publish, demos | Which gate stops the most work tells where the pain (or the flakiness) is. |
| All failed jobs (any workflow) | build-test-publish: 90, demos: 54, package: 26, smoke: 20, preflight: 7, tlc: 2, live-ai-smoke: 1 over 199 failed attempts | Context for the line above: failures outside required checks. |
| Live real-AI runs (`live-ai-smoke`) | 125 runs, share of all runs 125/1165 = 10.7%; results: cancelled: 11, failure: 27, success: 87 | Only a live run proves the AI features work; mock runs never do. |
| Time from PR open to merge | median 2.9 h, 90th percentile 10.8 h, longest 47.7 h (n=21) | Watch only: too fast can mean a skipped review. |
| Time to green (PR open to all required checks green on the merged head) | median 3.6 h, 90th percentile 10.1 h (n=8); not measurable for #4, #7, #8, #9, #10, #11, #12, #14, #15, #16, #18, #19, #22 (judged against the required checks as read at fetch time; a PR merged before a check became required, or under its old job name, cannot show it green) | Watch only: how long a correct change waits on the gates. |
| Baseline total (sum of `docs/quality/baseline.json`) | 554 | Known-bad code patterns; may only go down. |
| Proof coverage (`docs/FEATURE_MAP.md` rows) | live 24, mock 31, manual 12, UNVERIFIED 3 (of 70 rows, best label per row) | UNVERIFIED must reach 0 for shipped features. |
| Escaped bugs (issues labelled `escaped`) | UNVERIFIED (the label does not exist yet) | Bugs found after merge show which layer is missing a check. |
| Prose-to-check ratio | UNVERIFIED (no rule-to-check mapping exists yet; not in git or Actions data) | Rules only in prose are not enforced. |
| Fuzz yield | not started (Phase 3) | New failures per fuzz hour; measured once the fuzz exists. |

Workflow runs in window: 1165 (demo-ci: 347, formal: 4, live-ai-smoke: 125, quality-gates: 60, release: 173, windows-ci: 456).
QA verdict per merged PR: #4 none, #7 none, #8 none, #9 none, #10 none, #11 at merged head, #12 at merged head, #13 at merged head, #14 at merged head, #15 at merged head, #16 at merged head, #17 at merged head, #18 at merged head, #19 at merged head, #20 at merged head, #21 at merged head, #22 at merged head, #23 at merged head, #24 at merged head, #25 at merged head, #27 at merged head.

Queries run (ids replaced by placeholders; every exact command was printed to stderr):

| Times run | Command |
| --- | --- |
| 199 | `gh api --paginate repos/vikas53953/bluey/actions/runs/{id}/attempts/{n}/jobs?per_page=100 --jq .jobs[] \| {name, conclusion}` |
| 6 | `gh api --paginate repos/vikas53953/bluey/actions/runs?per_page=100&created={day} --jq .workflow_runs[] \| {id, name, event, head_sha, head_branch, status, conclusion, run_attempt, created_at, updated_at, prs: [.pull_requests[].number]}` |
| 21 | `gh api --paginate repos/vikas53953/bluey/commits/{sha}/check-runs?per_page=100 --jq .check_runs[] \| {name, conclusion, completed_at}` |
| 1 | `gh api --paginate repos/vikas53953/bluey/issues/comments?per_page=100 --jq .[] \| {id, issue: (.issue_url \| split("/") \| last \| tonumber), created_at, body: (.body // "")}` |
| 1 | `gh api --paginate repos/vikas53953/bluey/pulls?state=all&per_page=100 --jq .[] \| {number, base: .base.ref, head_ref: .head.ref, head_sha: .head.sha, created_at, merged_at, closed_at, body: (.body // "")}` |
| 46 | `gh api repos/vikas53953/bluey/actions/runs/{id}/attempts/{n} --jq {conclusion}` |
| 1 | `gh api repos/vikas53953/bluey/branches/lane%2Fface/protection/required_status_checks --jq {contexts}` |
| 1 | `gh api repos/vikas53953/bluey/branches/main/protection/required_status_checks --jq {contexts}` |
| 1 | `gh api repos/vikas53953/bluey/labels/escaped --jq {name}` |
| 1 | `git log origin/lane/face --since=2026-11-08T00:00:00Z --until=2026-11-13T06:00:00Z -i --grep=^Revert  --grep=This reverts commit --format=%H%x09%cI%x09%s` |
| 1 | `git log origin/main --since=2026-11-08T00:00:00Z --until=2026-11-13T06:00:00Z -i --grep=^Revert  --grep=This reverts commit --format=%H%x09%cI%x09%s` |
| 1 | `git rev-list -1 --first-parent --before=2026-11-13T06:00:00Z origin/lane/face` |
| 1 | `git show 0122ac745354d5ea62ae910cbff635e41335d716:docs/FEATURE_MAP.md` |
| 1 | `git show 0122ac745354d5ea62ae910cbff635e41335d716:docs/quality/baseline.json` |

<!-- generated: end -->

## What the first table says, in plain words

- **No reverts.** Nothing merged had to be undone. Too early to read much into it: 21 PRs in 5 days.
- **QA verdicts became the rule from PR #11 on.** Every merged PR from #11 to #27 names a QA PASS for the exact
  commit that merged. #4, #7, #8, #9 and #10 came before the pipeline (8 to 12 Nov) and have none.
- **Flaky rate is above the 2% target when counted the plan's way: 19 of 743 (2.6%).** Of those 19: 10 are one run
  whose re-run flipped from fail to pass, 7 are a push run against a pull-request run of the same commit (a
  pull-request run tests the merge with the base, so the code is not quite the same), and 2 are separate manual
  live runs of one commit. Counting only re-runs of the very same run, 10 of 1,125 runs flipped (0.9%). Both
  numbers are kept so the trend can be watched honestly. Several fall on 12 Nov, when the artifact storage quota
  turned runs red (PR #15's commit `8836c7e` is one); the table does not yet tell an infrastructure red from a code red.
- **About half the merged PRs went red first, then green.** The checks are stopping commits, not rubber-stamping.
- **Most failed jobs are `build-test-publish` (90) and `demos` (54),** the two required checks on both branches.
  Why each failed is not in this table.
- **Live real-AI runs are about 1 in 9 runs (125 of 1,165); 87 passed, 27 failed, 11 were cancelled.** Why the 27
  failed is not in this table.
- **Time to green is only measurable for 8 PRs,** because the required list (read today) includes `live-ai-smoke`,
  which only became a required job with PR #20 on 13 Nov. It gets better as new PRs arrive.

## Not measured (UNVERIFIED), and why

| Metric | Why not | What would fix it |
| --- | --- | --- |
| Escaped bugs | There is no `escaped` label on GitHub yet, so there is nothing to count. | Create the label; tag every bug found after merge, with the layer that should have caught it. |
| Prose-to-check ratio | No list links each written rule (AGENTS.md, prompts) to a `BOO-*` check. It is not in git or Actions data. | A rule-to-check map file (later Phase 1 work). |
| Fuzz yield | The fuzz does not exist yet. | Phase 3. |
| Reviewer FAIL rounds (exact) | PR descriptions are rewritten, so older FAIL notes can vanish. The count is a **lower bound**. | Reviewers post each verdict as a new comment, never only in the description. |
| Required checks in the weekly workflow | The workflow token may not read branch protection. | Run locally with the owner's `gh` login, as below. |

## How each number is counted

- **Window:** two fixed UTC times passed to the script; the start is included, the end is not. Nothing uses "now".
- **PRs:** only PRs whose base is `lane/face` or `main`. "Merged" means merged inside the window; FAIL rounds use PRs
  opened inside the window.
- **A run belongs to a PR** when GitHub lists the PR on the run, or the run is on the PR's branch and started while
  the PR was open.
- **Verdict lines** are read from the PR description and its comments: `QA PASS`, `QA: **PASS**`, `QA FAIL`,
  `REVIEW FAIL`, followed on the same line by a commit id (7 to 40 hex characters with at least one letter, so a run
  number never counts as a commit). A FAIL line that says "blocked" (storage, budget) is a blocked round, not a
  reviewer FAIL. Each (kind, commit) counts once.
- **Red then green:** a workflow failed on an earlier commit of the PR and passed on the commit that merged.
- **Flaky (plan's way):** the same workflow on the same commit gave both a pass and a fail, counting re-runs and push
  and pull-request runs of that commit. Cancelled and skipped results are ignored.
- **Flaky (strict):** one run whose attempts disagree (a re-run flipped it).
- **Required-check failures:** failed jobs, in failed run attempts, whose name is a required check today.
- **Time to green:** from PR open to the moment every required check had a passing result on the merged commit.
- **Baseline total and proof coverage:** read from the `lane/face` commit at the window's end.

## How to reproduce

Recompute from the saved data (gives this table byte for byte):

```
python -I scripts/quality/trust_metrics.py --from-snapshot docs/quality/trust-metrics/snapshot-2026-11-08_2026-11-13T06.json --since 2026-11-08T00:00:00Z --until 2026-11-13T06:00:00Z
```

Fetch fresh from GitHub (needs `gh` logged in; the owner's login also reads the required checks). Every command is
printed before it runs. A fresh fetch can differ only if GitHub's records changed after the window (a later
re-run, an edited PR description); the snapshot sha256 in the first line shows whether the data is the same.

```
git fetch origin lane/face main
python -I scripts/quality/trust_metrics.py --since 2026-11-08T00:00:00Z --until 2026-11-13T06:00:00Z --save-snapshot snap.json
```

Check the script itself (planted data with known answers):

```
python -I scripts/quality/trust_metrics.py --self-test
```
