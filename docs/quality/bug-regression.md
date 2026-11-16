# BOO-BUG-001: every bug becomes a permanent regression test before its fix merges

**The rule.** Every bug found (by the explorer, a review, CI or Vikas using Boo) gets a test that reproduces it. The
test is seen **failing** before the fix and **passing** after it, and it stays in the suite for good. Decided by Vikas,
13 Nov 2026 (see `docs/DECISIONS.md`).

**Who checks it.** The `bug-regression` workflow (`.github/workflows/bug-regression.yml`) runs
`scripts/quality/bug_regression.py` on every pull request when it is opened, edited, pushed, reopened or labelled.

## Which pull requests count as a bug fix

Any one of these makes a PR a bug fix:

- its title starts with the word `fix` (`fix: ...`, `fix(notch): ...`, `Fix the hover race`; not `fixture ...`);
- it has the label `bug`;
- its body has a heading `Bug` (for example `## Bug`);
- it closes an issue labelled `bug` (a closing word such as `Fixes #12` in the body, or an issue linked in the sidebar).

Every other PR passes this check untouched.

## What a bug fix must carry

A `## Regression test` section in the PR body, written like this:

```markdown
## Regression test

- Test: `tests/Boo.Core.Tests/HoverRegressionTests.cs::Hover_after_quit_does_not_reopen`
- Red before: https://github.com/vikas53953/bluey/actions/runs/123/job/456
- Green on head: https://github.com/vikas53953/bluey/actions/runs/789
```

The check reads it as GitHub shows it (a section inside an HTML comment or a code block, like the example above, does
not count) and verifies:

| What | The check |
| --- | --- |
| Test | Each test is a code span `path/File.cs::MethodName`. The file is new or changed in this PR and holds that method on the PR head. |
| Red before | A full `https://github.com/<this repo>/actions/runs/<id>/job/<id>` link to a **job** (not a whole run, so its log can be read). The job **failed**; it ran on a commit that is an **ancestor** of the PR head and **not the head itself**; the test file and method already existed at that commit; its log has a failure line for every named test (`Failed ...MethodName` or `... [FAIL]`); **and** the test results (`.trx` files) in that run's artifacts have a `Failed` result for **that exact test**: the same fully-qualified class (`Namespace.Class`, read from the test file) and method. Another test failing, or the same method name in another class, does not count. |
| Green on head | A run (or job) link of this repository whose commit **is** the PR head and which **succeeded**, **and** whose test results (`.trx` in the run's artifacts) hold **that exact test** (class and method) at least once, with every result `Passed`. Not found, zero results, skipped (`NotExecuted`), a failed theory row, or only other tests passing all fail. |

Every GitHub read is read-only (the workflow has `contents`, `actions`, `issues` and `pull-requests` read access, no
write). If any read fails (rendering the body, a job, its log, a run's test results, a linked issue), the check fails closed;
re-run it. The test results come from the run's artifacts: `windows-ci` uploads every `.trx` as `test-results`
(key-scanned before upload). Artifacts larger than 32 MB (the published app) are skipped.
Every `.trx` file read must be **one complete, clean run** whose summary agrees with its rows, or the run's
results count as unreadable (fail closed): every result row is `Passed`, `Failed` or `NotExecuted`; exactly one run
summary, whose outcome is `Failed` when any row failed and `Completed` otherwise; exactly one counter block, with
`total`, `executed`, `passed`, `failed` and `notExecuted` equal to the rows and every other counter (`aborted`, `error`,
`timeout`, `inconclusive`, `passedButRunAborted`, `warning`, ...) equal to 0, since no row has that outcome. A file
with a DOCTYPE or an entity declaration is refused wherever it sits and in any encoding the XML parser reads (UTF-8,
UTF-16, ...).

## How to produce the proof

1. Write the test that reproduces the bug. Push it to the PR branch **alone, without the fix**.
2. Let `windows-ci` (or another workflow that runs that test with `--logger trx` and uploads the `.trx` files) fail on
   that commit. Open the failed job and copy its
   address (`.../actions/runs/<id>/job/<id>`) into `Red before:`.
3. Push the fix. When the run on the new head is green, copy its address into `Green on head:`. Editing the body
   re-runs the check.
4. Every later push changes the head, so update `Green on head:` to the new run once it is green.

## Where the test goes

- Tests under `tests/` are the **frozen test oracle** (`docs/verification/test-oracle.json`). A new test file there is
  frozen in the same PR with `python scripts/test_oracle.py --extend "<reason>"`; prefer a new file, because adding a
  method to an existing frozen file is a change that needs a reviewed `--rebaseline`. Never weaken or delete a frozen
  test to get green.
- A live journey (the real app driven like a user) goes in `tools/Boo.VerifyLive`.

## Do not

- Do not link the run on the fixed head as "Red before" (the check refuses the head itself).
- Do not link a job of another repository, a fork's copy, or a job that failed for another reason (its log must show
  the named test failing).
- Do not put the section inside an HTML comment or a code block (it does not count), or strike it out.

## Known gaps (left to review)

- Only .NET tests whose run uploads `.trx` files can be proven (today: the projects `windows-ci` tests). A pytest test,
  or a test run by a workflow that uploads no `.trx`, cannot be proven and the check fails; add the upload (with the
  key scan before it) to that workflow first.
- The class is read from the test file by text (namespace + the last `class`/`record` before the method). A test in a
  nested class (TRX writes `Outer+Inner`) is not matched and fails closed; keep regression tests in a top-level class.
- It proves the test failed before and the run passed on the head, not that the test is a faithful reproduction of
  the bug. The reviewer judges that.
- Job logs expire after the repository's log retention (90 days by default), and `test-results` artifacts after 7 days
  (`retention-days` in `windows-ci.yml`). After that the proof cannot be read and the check fails: re-run the red
  and green runs, or re-run the check before then.
- A PR can edit `bug-regression.yml` itself; as with every gate here, that is caught by review and the required-check
  settings (Vikas's decision), not by the workflow. The base branch's copy of the script judges first, so a PR cannot
  loosen the script.
