# Test guard (BOO-TEST-002): the builder may not loosen its own tests

**The rule in one line:** a pull request that changes product code may not, in the same PR, make existing tests weaker,
unless a reviewer has read the test change and signed it for the exact commit being merged.

Why: if the agent that writes the code can also quietly delete an assertion, skip a test or re-freeze the oracle, a
green run proves nothing. The inner loop must be trustworthy (owner decision, 13 Nov 2026).

Checker: `scripts/quality/test_guard.py`. Workflow: `.github/workflows/test-guard.yml` (job `test-guard`, every PR
open, edit, push and reopen). The base branch's own copy of the checker judges first, so a PR cannot loosen the checker
that judges it.

## When it fails

Both of these in one PR:

1. **Product code changed:** anything under `src/`; anything under `tools/` outside a test project (a folder whose
   `.csproj` uses xUnit or sets `IsTestProject`); any `Directory.Build.*` file.
2. **An existing test weakened**, any of:
   - an existing file under `tests/` or in a `tools/` test project is deleted, or has a line removed or changed;
   - fewer assertion-shaped calls in a changed file (`Assert.*`, `.Should*(`, `.Verify*(`, `Verify*(`, `Expect*(`,
     `Ensure*(`), or fewer test attributes (`[Fact]`, `[Theory]`, `[InlineData]`, `[MemberData]`, ...);
   - a skip added (`Skip =`, `[Trait("Category", "Skip")]`, `Assert.Skip*`, `Skip.If`, `SkippableFact`, `[Ignore]`,
     `Explicit = true`), a new `#if`, a test that now starts with `return`, or a test commented out;
   - the frozen oracle changed (`docs/verification/test-oracle.json`, `docs/verification/test-oracle-history/**`) in
     any way other than a pure `test_oracle.py --extend` (only new files frozen, every frozen hash kept, history only
     added to).

**Never fails:** adding new test files, adding new tests to an existing file (lines only added, no skip or `#if`), a pure
oracle `--extend`, and any PR that does not change product code (the frozen oracle already guards test-only PRs).

## How to pass when the test change is right

Add this section to the PR body (outside HTML comments and code blocks):

```markdown
## Test change justification

- `tests/Boo.Core.Tests/GreetingTests.cs`, `SaysHi`: the old assertion compared the wrong field; the new one checks the
  text the user sees.

Test change reviewed: 0123456789abcdef0123456789abcdef01234567 by Fable reviewer
```

- Name **every** file and test method the failure lists, each on a line that also says why in words.
- The sign-off line carries the **full 40-character head SHA**. Any new push changes the head, so the old sign-off
  stops counting and the reviewer must sign again. Editing the body re-runs the check.
- The reviewer is a separate person or agent that read the test diff, never the builder of the PR.

## Limits (be honest about them)

- The sign-off is text in the PR body. The check proves it exists for this head; it cannot prove who wrote it. A
  separate reviewer and Vikas's required-check settings are the real gate.
- Detection reads text, not compiled code: a weakening the patterns do not see (for example an assertion moved into a
  helper that no longer asserts, in a new file) can pass. Any edit of an existing test line is caught, so this mostly
  matters for brand-new files.
- A PR can edit `test-guard.yml` itself; like every gate here, that is caught by review and the required-check settings,
  not by the workflow.

## Run it locally

```
python -I scripts/quality/test_guard.py --self-test
python -I scripts/quality/test_guard.py --base origin/lane/face --head HEAD [--body pr-body.md]
```
