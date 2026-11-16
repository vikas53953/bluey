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
| `blocked` | Draft, base not `lane/face` (every PR on `main`, not only #3), fork PR, GitHub has not computed mergeability yet, the verifier refused this head (the latest `codex-verified` status on it is `failure` or `error`), or the stamp is vetoed (below). | owner / lane / loop (next run) |
| `needs-rebase` | Behind `lane/face` (compare API `behind_by` > 0) or conflicting. | lane (syncs the branch) |
| `ci-red` | A check run or a commit status on the head failed (`failure`, `cancelled`, `timed_out`, `action_required`, `startup_failure`, `stale`, `error`), or a **required** check concluded anything but `success` (a required check that is `skipped` or `neutral` is not green; only other checks, such as `github-release`, may skip). When a check of the same name ran again (for example `evidence` after a body edit), the newest run counts, as in GitHub's own required-check rule. | lane |
| `waiting-QA` | A check is still running or a required check (`demos`, `build-test-publish`, `live-ai-smoke`, `evidence`, `package`, `preflight`, `quality`, `smoke`) has not reported yet; or all green but no comment starting `QA PASS at <head>`. | qa |
| `waiting-review` | No comment starting `CODEX REVIEW PASS — <head>`. | reviewer |
| `waiting-stamp` | No matched stamp (below). | verifier |
| `stamped-ready` | Everything above met **and** a matched, unvetoed stamp. | auto-merge |

**The matched stamp (the attribution rule, same as the merge verifier's):** the **latest** `codex-verified` commit
status on this exact head has state `success`, **and** a trusted comment whose first line is `CODEX VERIFIED <this
head>` contains `Returned status ID **<id>**, context `codex-verified`` with `<id>` equal to that status's ID. A
comment naming another SHA, a different status ID, a later failure status, or a comment from an untrusted account:
never a match.

**The veto:** if **any** other comment on the PR (issue comments, PR review bodies, read through GraphQL because only GraphQL gives a review's edit time (`lastEditedAt`), and inline review comments from `/pulls/{n}/comments`, all read every run and again in the preflight; a failed read, an error, a missing field or more than 100 reviews stops the run), created, submitted or edited after that stamp comment, contains `BLOCK`,
`NOT VERIFIED` or `SURVIVED`, the PR is `blocked` and never armed. Matching is **case-sensitive and whole-word**: the
phrase must not touch a letter, digit or underscore on either side, so `BLOCK` and `CODEX REVIEW BLOCK —` veto while
`blocked`, `BLOCKED` and `UNBLOCK` do not. Any author counts (failing safe: a stray word can only stop an arm, never
cause one). Only the loop's **own** ledger comment is exempt: written by the loop's login (`github-actions[bot]`),
starting with the marker and in ledger shape; a copied marker in anybody else's comment does not switch a veto off.
Every item is normalized to one shape before judging: kind (issue comment, review, inline comment; each kind numbers its ids separately), id, created/submitted time, last edit time (none when the API says never edited, unknown when the field is missing), body, author. Only the stamp comment itself is exempt, matched by kind **and** id, so a review or inline comment that happens to share its number is still judged. An item counts as after the stamp when it was created or submitted later, or edited later (a review submitted earlier but edited after the stamp counts), or when its creation or edit time is unknown. Order: timestamps have one-second resolution, so an issue comment created in the same second as the stamp comment
is ordered by comment id (ids only grow); a review or inline comment in that second cannot be ordered (its ids are a
different sequence) and counts as after; a review with no submission time counts as after; an edit made in the same second as the stamp cannot be ordered and counts as
after it (fail closed). Comments older than the stamp comment (and not edited since) do not veto: they are what the
verifier already saw.

## The API boundary (strict validation)

Every field the loop relies on is read in one place, through one accessor, from every source: PR metadata, issue
comments, PR reviews, inline review comments, check runs, commit statuses, merged PRs, and the small replies
(repository settings, base head, compare, workflow runs). Rules:
- a required key must be **present**: absent is not the same as null;
- its type must match (a number is never a yes/no, text is never a number);
- every time goes through **one strict parser**: ISO-8601 UTC only (`2026-11-16T03:00:00Z`); no other zone, no
  missing zone, no impossible date;
- null is accepted only where the API gives it a meaning: a review's `lastEditedAt` null = never edited; a review's
  `submittedAt` null = not submitted (counts as after the stamp); a check run's `conclusion` / `completed_at` null =
  not finished (a completed run without them is malformed); a deleted account (user or author null).

Anything else is **malformed**: the whole run takes no action and exits 1 (`fail closed (malformed GitHub data)`),
naming the field but never its value. So a review whose `lastEditedAt` is missing or not a time can never be read
as "never edited". Downstream code reads only the adapters' records, with plain indexing; a self-test scans the
script's syntax tree and fails on any `.get(` outside `gh_env`, `main` and the test code. The tests run through the
real `GhClient`: a raw-JSON stand-in for `gh` serves the planted data as JSON text, exactly as `gh` would.

## The allowed actions (at most one per PR per run)

1. **Arm auto-merge**: `gh pr merge <n> --auto --merge --match-head-commit <head>`. Only in `stamped-ready`, only
   when the repository allows auto-merge, only when auto-merge is not already armed, only after the fresh preflight,
   and at most one arm per run. `--match-head-commit` means a new push between the read and the call makes GitHub
   refuse it. GitHub then merges only when every required check, including `codex-verified`, is green on the head.
2. **Branch update: not an action** (see the trap below). The script has no code path for it.
3. **One marker comment per PR**: first line `pr-loop: state=<state> head=<sha> next=<who>`, then the reason. Created
   once, then edited in place. Same state and head as the comment already says: nothing written. Drafts, other
   bases and PRs whose mergeability is not computed get no comment.

When a PR is `stamped-ready` and not armed, arming is that run's only action; the comment catches up next run.

Plus one repository-level action, **tip CI after a bot merge** (below).

## The preflight before every arm, and one arm per run

The plan is read at the start of the run; GitHub can change before the arm (a newer `codex-verified` failure, a
withdrawn or edited `CODEX VERIFIED` comment, a new BLOCK comment, the PR retargeted to `main`, a red check, the base
moving on). `--match-head-commit` binds only the head. So immediately before **each** arm the script re-reads, fresh:
the repository's `allow_auto_merge`; the PR itself (same number, same head, open, not a draft, not a fork, base exactly
`lane/face`, mergeable, auto-merge not already armed); the base head and the PR's `behind_by`; the check runs and
statuses on the head; the comments (so the veto is checked again). The PR must still classify as `stamped-ready`, and
the latest `codex-verified` status must still be the **same status ID** the plan saw (a newer SUCCESS stamp with its
own comment also means no arm this run: the next run plans from it). Any change: no arm (`SKIP ... the fresh preflight
no longer qualifies it`). Any read error: no arm, the run stops, exit 1.

**The allowed base is an allowlist of one: `lane/face`.** A PR on any other base (`main`, or anything else, whatever
its number) is never acted on, and `--base` refuses any other value before anything is read (tested through the command line). This closes the whole "PR #3-like" class, not
just PR #3.

**At most one arm per run.** An arm can let GitHub merge and move `lane/face` on, and every later plan in the run was
read against the old base. Rather than re-derive mid-run, the loop arms at most one PR per run; the next run re-reads
everything.

**Honest limit:** the preflight **narrows** the race window to the few seconds between its reads and the arm call; it
is **not** an atomic compare-and-swap of base, status and comment. The backstop stays server-side: branch protection
requires `codex-verified` (and every other required check) on the head at merge time, and `--match-head-commit` binds
the head.

## Tip CI after a bot merge

A merge made by auto-merge that the workflow token armed is recorded as made by a bot, and GitHub starts **no** push
workflows for it, so `lane/face` would get no `windows-ci` or perf run on its new tip. Each run the loop reads the
last 20 PRs merged into `lane/face`. When the **current tip of `lane/face` is the merge commit of a PR merged by a
bot** (`mergedBy.is_bot`), it dispatches `windows-ci.yml` and `perf-guards.yml` on `lane/face` (both already have a
`workflow_dispatch` trigger, so no workflow changed; GitHub allows `workflow_dispatch` from the job token). The
ledger is GitHub's own runs: a workflow that already has any run on that tip SHA is not dispatched. Right before each
dispatch it re-reads the tip (moved: skip, the new tip's own pushes cover it) and the run count (a run appeared:
skip). A merge by a person starts push CI by itself: no dispatch. Dry run: printed, not done. A read error: no action,
exit 1.

**UNVERIFIED until the first real bot merge** (no real dispatch was made while building this). What to check then:
the merged PR shows `mergedBy` as a bot; the next loop run prints `tip of lane/face <sha>: bot merge of PR #N;
dispatch: windows-ci.yml, perf-guards.yml`; both runs appear on that SHA with event `workflow_dispatch`, triggered by
`github-actions[bot]`; a further loop run prints `none (runs already exist)`. Not covered by this dispatch: the other
push-only runs on `lane/face` (`demo-ci`, `quality-gates`, `feature-map`); the PR's own runs covered them before the
merge.

## The ledger (idempotency, keyed by PR, head and action)

The simplest honest choice: **GitHub's own state is the ledger**, no file. `arm` is skipped when the PR's
`autoMergeRequest` is already set. `comment` is skipped when the bot's own marker comment (author
`github-actions[bot]`, marker `<!-- pr-loop:v1 -->`) already says this state and head; anybody else's comment with the
marker is ignored. `dispatch` is skipped when the tip SHA already has a run of that workflow. Nothing to lose between
runs, nothing to keep in a cache, nothing another account can forge.

## Never

- **PR #3** and **every PR whose base is `main`** (or anything but `lane/face`): never acted on. PR #3 is also skipped
  right after the PR list is read, and the executor refuses again if a plan ever names it.
- Tags, releases, installer, signing, repository settings, branch pushes: no code path.
- Merge without the matched, unvetoed stamp; merge directly (only arming GitHub's auto-merge, which still waits for
  every required check).
- Print a token: `gh` reads `GH_TOKEN`; the script never reads or prints it.
- **Fail closed:** any API error while reading (repository, base head, PR list, comments, checks, statuses, compare,
  merged PRs, workflow runs) stops the run before any action: exit 1, `::error::` with the reason. An error while
  acting stops the run there, exit 1. 200 or more open PRs: refused (the list could be cut short).

## Log safety (no header, token or key in any output)

**One redaction, one choke point.** Everything the script writes goes through `safe()`: stdout and stderr (only via
`emit()`), every line `run()` hands to any output function, the marker comment payloads it sends to GitHub, exception
text, and the dry-run plan. There is no queue file. `safe()` is driven by:
1. **the same key shapes as the repository key scan**: the `SHAPES` list is loaded from
   `scripts/quality/key_scan.py` itself (no second list to drift); each match becomes `[redacted]`;
2. **sensitive labels**, any case, with spaces, hyphens or underscores (`API key`, `api-key`, `API_KEY`,
   `Authorization`, `Bearer`, `token`, `secret`, `password`, `cookie`, `Set-Cookie`, `private key`, `access key`)
   followed by `:`, `=` or a space: the label stays, the rest of that line is replaced by `[redacted]`.

Before both, a **whole private-key block** is removed: from its `-----BEGIN ... PRIVATE KEY-----` line across lines to
its END line, or, when no END line follows, to the end of the text (fail closed). key_scan's own private-key shape
matches only the BEGIN delimiter, which is right for a scanner that fails a file but not for redaction; widening
key_scan's shape is a separate key_scan change, not made in this PR. `gh` error text goes through `safe()` before its
line filter, so a block split over lines is caught whole. If the patterns cannot be loaded, `safe()` withholds the
text entirely (fail closed). Because a label removes the
rest of its line, the script writes its own words first and GitHub-reported text (check names, status contexts,
comment authors) last, for example `failed on the head (failure): build API key: [redacted]`: the context survives.
At startup `sys.stdout` and `sys.stderr` are wrapped so even a stray write is redacted (defence in depth). A self-test
scans the script's own syntax tree: no `print`, `write` or `sys.stdout`/`sys.stderr` outside `emit()` and the wrapper.

Kept from before: the environment handed to `gh` drops `GH_DEBUG`, every `GIT_TRACE*`, `GIT_CURL_VERBOSE`,
`GH_VERBOSE` and `DEBUG`; a `--verbose` / `--debug` flag on a `gh` call is refused before `gh` runs; `gh`'s error
text is first filtered line by line; the environment is never printed. Each job still tees the loop's output into
`pr-loop-log/` and, as its last step, runs `scripts/quality/key_scan.py --secret-env GH_TOKEN pr-loop-log`. That scan
is a **detection backstop only**: by then the text has already been streamed to the job log, so the redaction above is
what keeps it out.

Tested end to end with the fake client: every shape key_scan knows (built at run time, never stored key-shaped) plus
labelled values, through every output path (gh error text, check name, status context/description, a comment read
back, the marker comment payload, an exception message, the dry-run plan), asserting none reaches captured
stdout/stderr or comment payloads and that the non-sensitive context still appears. Each layer is also tested on its
own, so none can be dropped because another still covers it.

## How it is started

- **No `schedule:` trigger.** GitHub fires schedules only from the default branch (`main`). A small scheduler that
  lives on `main` (built by the Phase 2 lane, not here) runs `gh workflow run pr-loop.yml --ref lane/face` every 5 to
  10 minutes, which runs this file as it is on `lane/face`.
- `workflow_dispatch` with a `dry_run` input, **default `true`**. **Acting needs `dry_run=false`**, which is the
  owner's policy decision (the scheduler passes `-f dry_run=false` only once he says so). Concurrency group
  `pr-loop-queue`, so two runs never overlap.
- On pull requests that change the loop's own files (`paths:`) only the `selftest` job runs: the self-test and a
  read-only dry run of the real queue (its token cannot write). Other PRs never carry this check.
- Permissions: the `selftest` job reads only (`contents`, `pull-requests`, `statuses`, `checks`: read). The `loop`
  job (never on pull requests) has `pull-requests: write` (comments, auto-merge), `contents: write` (arming auto-merge
  needs merge rights; a deviation from the plan's `contents: read`), `actions: write` (the tip dispatch), and
  `statuses`/`checks` read. Checkout with `persist-credentials: false`. The refuse step stops the job anywhere but a
  GitHub-hosted VM.

## What it costs: $0 (proof)

- The repository is **public**: `gh api repos/vikas53953/bluey --jq .visibility` → `public` (16 Nov 2026).
- Only standard GitHub-hosted labels: `pr-loop` runs on `ubuntu-latest`; the dispatched `windows-ci` and
  `perf-guards` use their existing `windows-latest` labels. `scripts/ci/check_runners.py` allows only standard
  hosted labels and passes on every workflow.
- GitHub documents standard GitHub-hosted runners as free for public repositories.
- The account billing endpoints are not readable with the token here (they need the `user` scope: HTTP 404). The run
  timing endpoint is readable and shows **0 billable ms**: `pr-loop` run 43802607289 `UBUNTU total_ms 0` (2 jobs),
  and a ten-minute `windows-ci` run 43813170620 `WINDOWS total_ms 0`.

## Settings

- `allow_auto_merge` is **on** (`gh api repos/vikas53953/bluey --jq .allow_auto_merge` → `true`, 16 Nov 2026). If it
  is ever turned off, the loop reports it (`::warning::`), never arms, and never changes the setting. The REST reply
  hides this field from a read-only token (the first hosted dry run failed closed on exactly that, run 43803095094),
  so the script then asks GraphQL (`autoMergeAllowed`); still unreadable means "unreadable with this token", never arm.
- `gh pr merge --auto` arms GitHub's auto-merge while required checks are pending. If every requirement is already met
  when it runs, GitHub may merge at once; that is the same outcome auto-merge would reach, still bound to the head.

## The GITHUB_TOKEN trap, and the chosen option (i)

A push made with the workflow's own `GITHUB_TOKEN` (including GitHub's "update branch") **does not start other
workflows**. If the loop updated a behind branch, the new head would have no CI, so it could never be QA'd, reviewed
or stamped. **Option (i), chosen:** the loop never updates branches. It only arms auto-merge, writes the marker
comment and dispatches tip CI after a bot merge; branch syncs stay with the orchestrator / the lane's task, whose
pushes do start CI. `needs-rebase` therefore names `lane` as next.

## Unproven, and the owner's call

- `contents: write` and `actions: write` are confined to the `loop` job, which never runs on pull requests; the PR
  job's token is read-only, checkout does not persist credentials, and acting needs `dry_run=false`.
- **UNVERIFIED:** that the acting-mode permissions are enough to arm and to dispatch; that a `--ref lane/face` dispatch
  from the scheduler on `main` runs this file; the tip-CI dispatch on the first real bot merge (what to check is
  above). No dry run can prove these.
- **Activation is an owner policy decision** (the scheduler on `main`, `dry_run=false`), not something this PR turns
  on. The tip dispatch covers `windows-ci` and `perf-guards`; other push-only runs after a bot merge stay a policy
  question, not an accepted replacement for them.

## Proof

- Self-test red first (logic stubbed): run 43802607289, `0/38`. Fail closed for real: run 43803095094.
- Review P1 (stale snapshot) red first: run 43812847889, `41/56`.
- Review P2 and the 16 Nov requirements (a)-(d) red first: run 43828969596, `67/76` (the 9 new behaviour cases fail;
  the P2 and (b) cases pass on the old code and are proven by mutation kills instead).
- Mutation kills (local): removing the one-arm limit, the stamp-ID continuity check, the base allowlist, the veto,
  either run-ledger check, or the bot-merger condition each fails at least one self-test case.
- Nothing armed, commented or dispatched on any real PR or branch while this was built (dry run only).
