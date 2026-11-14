# Answer the review before asking again (BOO-REVIEW-001)

**What.** When the newest Codex review comment on a pull request is a `CODEX REVIEW BLOCK — <sha>`, the PR
description must answer every finding of that BLOCK before anyone asks for a new review. The `review-answers`
check (`.github/workflows/review-answers.yml`, script `scripts/quality/review_answers.py`) turns red until it does.

**Why.** On 14 Nov 2026 about 15 Codex reviews were wasted: a builder pushed a new head and asked for a re-review while
the last BLOCK's findings were still open, and the reviewer had to post the same finding again. Examples:

- PR #61: [6862197381](https://github.com/vikas53953/bluey/pull/61#issuecomment-6862197381),
  [6862816057](https://github.com/vikas53953/bluey/pull/61#issuecomment-6862816057),
  [6863912081](https://github.com/vikas53953/bluey/pull/61#issuecomment-6863912081) (the same P1 three times)
- PR #58: [6862224283](https://github.com/vikas53953/bluey/pull/58#issuecomment-6862224283),
  [6862818292](https://github.com/vikas53953/bluey/pull/58#issuecomment-6862818292),
  [6863910216](https://github.com/vikas53953/bluey/pull/58#issuecomment-6863910216) (the same P1 three times)
- PRs #28, #32, #54, #91 and #3: the same finding three times each.

The same mistake happened more than twice, so it gets an automatic check (pipeline principle 6).

## How to answer

Fix the findings first. Then add this to the PR description, one row per finding, **in the same order as the BLOCK**:

```markdown
## Review answers

| Finding | Answer |
| --- | --- |
| P1 — short title of finding one | fixed in 1a2b3c4 |
| P2 — short title of finding two | disputed: the reviewer read the old copy of the file; line 40 already checks it |
```

- The first cell starts with the finding's level (`P0`..`P3`).
- The last cell is either `fixed in <sha>` (7 to 40 hex characters) or `disputed: <reason>` (at least 10 characters).
- A `fixed in` commit must be part of the PR head and made **after** the reviewed commit (not the reviewed commit
  itself, and not an older one). A fix that was already there when the reviewer looked cannot be the answer.
- A later `CODEX REVIEW PASS — <sha>` (or `PASS WITH ISSUES — <sha>`) comment closes the BLOCK only when `<sha>` is the PR's current head. A review of an older head never clears a BLOCK on a newer one.
- A BLOCK whose findings the check cannot read (no line starting with `P0`..`P3`) fails closed: the reviewer reposts
  it with one line per finding, like `P1 — <title>`.

## Limits (said plainly)

- **One GitHub account.** Every agent posts as the repository owner's account. Only CODEX REVIEW comments by that
  login count (the workflow passes it as `--trusted-authors`); anyone else's BLOCK or PASS is ignored. The check
  cannot tell which agent behind that account wrote a comment.
- **`fixed in` means "on top of the reviewed commit".** The fix commit must be a descendant of the reviewed commit
  and part of the head. The check does not prove when it was made: a commit pushed on top of the reviewed one
  before the review was posted also passes.
- **Timing.** The check runs on a push or an edit of the PR text. A BLOCK posted after the last push shows up only on
  the next push or the next PR-text edit; until then the old green result stays on the page.
- **It checks the answer, not the fix.** It proves each finding got a dated answer; whether the fix is right is still
  the reviewer's job.
- **Not required yet.** Making `review-answers` a required check on `lane/face` is Vikas's decision.
- A PR could loosen its own checker, so the base branch's copy of the script judges the PR first (same pattern as
  `pr-evidence`). A PR that edits the workflow file itself is a gap left to review.
