# Comment lint (BOO-CMT-003)

PR 1.6 of the agent-trust plan (`docs/plans/agent-trust-plan.md`, section 2.5). Script:
`scripts/quality/comment_lint.py`. Workflow: `.github/workflows/comment-lint.yml` (job `comments`, every pull
request, Linux, about a minute, no uploads).

## The rule

A pull request may not **add** a comment line in `src/` that is not a `///` doc comment.

Still allowed on a new line (the same exemptions as the ratchet's BOO-CMT-001, see `docs/quality/RULES.md`):

| Allowed | Example |
| --- | --- |
| `///` XML doc directly above a declaration (type, member, delegate, enum member; attribute lines may sit between) | `/// <summary>Returns the saved colour.</summary>` |
| A whole-line rule tag that names a real test under `tests/` | `// INVARIANT: undo restores the bytes, see UndoJournalTests.Roundtrip` (also `// SECURITY: ...`) |
| A whole-line tracked to-do | `// TODO(#123): remove after the provider switch` |

Old comments are not judged here. The quality ratchet counts them (BOO-CMT-001, per project, in
`docs/quality/baseline.json`) and that count may only go down. Decision: **block new, ratchet old** (see
`docs/DECISIONS.md`, 13 Nov 2026).

## Why

Agents write comments to justify a workaround ("retry because flaky") instead of fixing it. And a rule written in a
comment ("the end event is raised only once") is never checked: it belongs in a test. Many of today's comments are
such rules, so they are not deleted in one go; they are turned into tests over time and the count goes down.

## How a line counts as new

- Every C# file under `src/` (`.cs` in any letter case) that the pull request adds, changes or renames.
- What is a comment comes from the ratchet's own C# lexer (`scripts/quality/ratchet.py`, imported, not copied):
  text inside strings, verbatim strings, raw strings and interpolation holes is not a comment; a `//` after a
  `#pragma` or `#endif` is; a `#region` name is not.
- A `///` line is a doc comment only inside a block of whole-line `///` lines that sits **directly above a declaration**
  (a type, member, delegate or enum member; attribute lines may sit between). A `///` in a method body or accessor,
  at the end of a code line, above a `}`, or followed by a blank line is judged like any other comment. (The ratchet
  counts every `///` as doc; this check is stricter for new lines.)
- A comment line on the head is **new** when the same file on the base has no equal line (spaces ignored). So moving
  a comment up or down, or re-indenting it, passes. Writing the same old comment a second time is new.
- A renamed file is compared with the file it was renamed from (git rename detection). A whole new file's comments
  are all new.
- The base is the merge base of the pull request's base and head.

## When it fails

```
COMMENT LINT FAILED: 1 new comment line(s) that are not /// doc comments.
  ...What / Why / Fix / Do NOT / Rule...
BOO-CMT-003  src/Boo.Core/ActionPolicy.cs:26  new comment
```

The comment's text is never printed (it could hold a key).

**Fix.** If it explains a workaround, fix the cause and delete the comment. If it states a rule, write a test and use
`// INVARIANT: ... see NameTests.Method`. If it documents an API, use `///`. If it is debt, open an issue and write
`// TODO(#123): ...` on its own line.

**Do NOT** reword, split or hide the comment (in a string, a `#region` name, an attribute) to dodge the check, and do
not edit the script or the workflow to make the PR pass: on every pull request the **base branch's own copy** of
`comment_lint.py` (with the base branch's `ratchet.py` next to it) judges the PR first, so changing the PR's copy
changes nothing. The PR that adds the script has no older copy to apply, and says so in the log.

## Where it is strict on purpose

- Changing the code on a line that already ends with a comment makes that line new, so its comment is judged too.
  Move the explanation into a better name, or delete it.
- Moving a comment into **another** file (not a rename) counts as new there.
- A file renamed and changed so much that git no longer sees a rename (under 50% alike) counts as a new file.

## What it does not catch

- Comments in `tests/` and `tools/` (tests are frozen and their comments often explain the scenario; the plan keeps
  them out of Phase 1).
- XAML `<!-- -->` and `.csproj` comments.
- Text with no `//` or `/* */`: prose inside `#if false ... #endif`, and `#warning` / `#error` messages.
- The INVARIANT (and SECURITY) exemption only checks that a `NameTests` class's text contains `Method(`. A PR can add
  such a test file; the barrier is review plus the frozen-oracle revision that any new test file needs.
- `.cs` files outside `src/` compiled in through `<Compile Include>` (PR #28's compile list will fail those once it
  merges).
- A comment whose text equals any line of the base file (for example a line inside a raw string) counts as old.
- Whether a `TODO(#123)` issue exists and is open (a later weekly job in the plan).
- Whether an INVARIANT tag's sentence matches what its named test checks: only that the test exists. A reviewer
  judges the sentence.
- A pull request that edits `comment-lint.yml` itself (the base copy cannot guard its own workflow); left to review,
  as for the other gates. To block merges it must be added to `lane/face` branch protection as a required check
  (`comments`), which is Vikas's call.

By design, not gaps: moving a comment within the same file (or re-indenting it) passes, and `#region` names are not
comments.

## Run it yourself

```
python -I scripts/quality/comment_lint.py --self-test
python -I scripts/quality/comment_lint.py --base origin/lane/face
```

The self-test runs 22 planted base/head pairs in throwaway git repositories, each with an exact expected result: an
added `// retry because flaky` fails; a moved comment, a `///`, an INVARIANT naming a real test and a `//` inside a
string pass; a whole new file's comments fail; a renamed file keeps its old comments; a `///` in a method body, at a
line end, above a `}` or before a blank line fails, while `///` above a class, method, property, field, delegate,
enum member or attribute lines passes.
