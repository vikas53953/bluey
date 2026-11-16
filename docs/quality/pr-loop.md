# pr-loop: the deterministic PR loop (BOO-LOOP-001)

A plain script, no AI: `scripts/pr_loop.py` (Python stdlib, run with `python -I`), driven by the workflow
`.github/workflows/pr-loop.yml`. Each run reads every open PR of `vikas53953/bluey`, puts each in exactly one state,
and takes **at most one** allowed action per PR. It replaces "someone keeps looking at the PR list" with a fixed rule.

```
python -I scripts/pr_loop.py --self-test   every state and every refusal on planted fixtures (offline)
python -I scripts/pr_loop.py               read GitHub, print the plan, do nothing (dry run is the default)
python -I scripts/pr_loop.py --apply       read, print, then act
```

## The states (first unmet gate wins, so each PR gets exactly one)

Only "trusted" comments count: author association OWNER, MEMBER or COLLABORATOR. "Head" is the PR's full head SHA.

| State | When | Next (who the comment wakes) |
| --- | --- | --- |
| `blocked` | Draft, base not `lane/face`, fork PR, GitHub has not computed mergeability yet, or the verifier refused this head (the latest `codex-verified` status on it is `failure` or `error`). | owner / lane / loop (next run) |
| `needs-rebase` | Behind `lane/face` (compare API `behind_by` > 0) or conflicting. | lane (syncs the branch) |
| `ci-red` | A check run or a commit status on the head failed (`failure`, `cancelled`, `timed_out`, `action_required`, `startup_failure`, `stale`, `error`). When a check of the same name ran again (for example `evidence` after a body edit), the newest run counts, as in GitHub's own required-check rule. | lane |
| `waiting-QA` | A check is still running or a required check (`demos`, `build-test-publish`, `live-ai-smoke`, `evidence`, `package`, `preflight`, `quality`, `smoke`) has not reported yet; or all green but no comment starting `QA PASS at <head>`. | qa |
| `waiting-review` | No comment starting `CODEX REVIEW PASS — <head>`. | reviewer |
| `waiting-stamp` | No matched stamp (below). | verifier |
| `stamped-ready` | Everything above met **and** a matched stamp. | auto-merge |

**The matched stamp (the attribution rule, same as the merge verifier's):** the **latest** `codex-verified` commit
status on this exact head has state `success`, **and** a trusted comment whose first line is `CODEX VERIFIED <this
head>` contains `Returned status ID **<id>**, context `codex-verified`` with `<id>` equal to that status's ID. A
comment naming another SHA, a different status ID, a later failure status, or a comment from an untrusted account:
never a match.

## The allowed actions (at most one per PR per run)

1. **Arm auto-merge**: `gh pr merge <n> --auto --merge --match-head-commit <head>`. Only in `stamped-ready`, only
   when the repository allows auto-merge, only when auto-merge is not already armed. `--match-head-commit` means a new
   push between the read and the call makes GitHub refuse it. GitHub then merges only when every required check,
   including `codex-verified`, is green on the head.
2. **Branch update: not an action** (see the trap below). The script has no code path for it.
3. **One marker comment per PR**: first line `pr-loop: state=<state> head=<sha> next=<who>`, then the reason. Created
   once, then edited in place. Same state and head as the comment already says: nothing written. Drafts, other
   bases and PRs whose mergeability is not computed get no comment.

When a PR is `stamped-ready` and not armed, arming is that run's only action; the comment catches up next run.

## The ledger (idempotency, keyed by PR, head and action)

The simplest honest choice: **GitHub's own state is the ledger**, no file. `arm` is skipped when the PR's
`autoMergeRequest` is already set. `comment` is skipped when the bot's own marker comment (author
`github-actions[bot]`, marker `<!-- pr-loop:v1 -->`) already says this state and head; anybody else's comment with the
marker is ignored. Nothing to lose between runs, nothing to keep in a cache, nothing another account can forge.

## Never

- **PR #3** (`lane/face` → `main`): skipped right after the PR list is read (nothing about it is read further), and
  the executor refuses again if a plan ever names it.
- Tags, releases, installer, signing, repository settings, branch pushes: no code path.
- Merge without the matched stamp; merge directly (only arming GitHub's auto-merge, which still waits for every
  required check).
- Print a token: `gh` reads `GH_TOKEN`; the script never reads or prints it.
- **Fail closed:** any API error while reading (repository, base head, PR list, comments, checks, statuses, compare)
  stops the run before any action: exit 1, `::error::` with the reason. An error while acting stops the run there,
  exit 1. 200 or more open PRs: refused (the list could be cut short).

## Where it runs and what it costs

- The repository is **public** (`gh api repos/vikas53953/bluey --jq .visibility` → `public`, 16 Nov 2026), and the
  jobs run on GitHub-hosted `ubuntu-latest`. GitHub documents standard hosted runners as free for public
  repositories. The billing endpoints for the account are not readable with the token here (they need the `user`
  scope: HTTP 404). The run timing endpoint is readable and shows **0 billable ms** for this workflow's runs (for
  example run 43802607289: `UBUNTU total_ms 0`, 2 jobs). Cost: **$0**.
- `allow_auto_merge` is **on** for the repository (`gh api repos/vikas53953/bluey --jq .allow_auto_merge` → `true`,
  16 Nov 2026). If it is ever turned off, the loop reports it (`::warning::`), never arms, and never changes the
  setting; the marker comment names the owner as next. The REST repository reply hides this field from a read-only
  token (the first hosted dry run failed closed on exactly that, run 43803095094), so the script then asks GraphQL
  (`autoMergeAllowed`); if the setting is still unreadable it is reported as "unreadable with this token" and the loop
  never arms.
- `gh pr merge --auto` arms GitHub's auto-merge while required checks are pending. If every requirement is already met
  when it runs, GitHub may merge at once; that is the same outcome auto-merge would reach, still bound to the head by
  `--match-head-commit`.
- Schedule: every 10 minutes, plus a manual run with a `dry_run` input (default `true`). Concurrency group `pr-loop-queue`
  so two runs never overlap.
- **Acting needs an explicit switch.** A manual run with `dry_run=false`, or the repository variable `PR_LOOP_APPLY`
  set to `on` (owner's choice) for the schedule. Anything else is a dry run.
- **GitHub starts schedules and manual runs only from the default branch, which is `main`.** Until this file reaches
  `main`, the loop does not run on its own. On pull requests only the `selftest` job runs: the self-test and a
  read-only dry run of the real queue (its token cannot write).
- Permissions: the `selftest` job reads only (`contents`, `pull-requests`, `statuses`, `checks`: read). The `loop`
  job has `pull-requests: write` (comments, auto-merge) and `contents: write`, because arming auto-merge needs merge
  rights (GitHub's guidance for enabling auto-merge from a workflow; **UNVERIFIED here until the first armed run on
  `main`**, and a deviation from the plan's `contents: read`, for the orchestrator to accept); `statuses` and `checks`
  read.
- On pull requests the check runs only when the loop's own files change (`paths:`), so other PRs never carry it. Checkout with `persist-credentials: false`, so the token is not left in
  git. The refuse step stops the job anywhere but a GitHub-hosted VM.

## The GITHUB_TOKEN trap, and the chosen option (i)

A push made with the workflow's own `GITHUB_TOKEN` (including GitHub's "update branch") **does not start other
workflows**. If the loop updated a behind branch, the new head would have no CI, so it could never be QA'd, reviewed
or stamped, and the PR would sit there looking busy. **Option (i), chosen:** the scheduled loop never updates
branches. It only arms auto-merge and writes the marker comment; branch syncs stay with the orchestrator / the lane's
task, whose pushes do start CI. `needs-rebase` therefore names `lane` as next.

Related, not solved here (open question): when auto-merge was armed by `GITHUB_TOKEN`, GitHub performs the merge as
`github-actions[bot]`, and that merge push to `lane/face` also starts **no** push-triggered workflows on `lane/face`
(for example `feature-map`, the push runs of `quality-gates` and `windows-ci`). The PR's own checks have all run
before the merge, so no gate is skipped for the PR, but the after-merge runs on `lane/face` would be missing. Fixes
for later, owner's call: a GitHub App token for the arm step, or letting the next PR's runs cover `lane/face`.

## Proof

- Self-test red first (logic stubbed), local and hosted: run 43802607289, `0/38 self-test cases passed`.
- Fail closed for real: hosted run 43803095094 could not read `allow_auto_merge` with the read-only token and stopped
  with `fail closed while reading GitHub, no action taken`, exit 1 (fixed by the GraphQL fallback above).
- Self-test green after the implementation, and a read-only dry run of the real queue on every PR run (the plan for an
  unstamped PR says `arm: no (no codex-verified stamp on this head)`).
- Nothing armed or commented on any real PR while this was built (dry run only).
