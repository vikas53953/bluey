#!/usr/bin/env python3
"""BOO-REVIEW-001: a pull request that got a `CODEX REVIEW BLOCK` answers every finding before it asks again.

Why: on 14 Nov 2026 about 15 Codex reviews were wasted. A builder pushed a new head and asked for a re-review while the
last BLOCK's findings were still open, and the reviewer posted the same finding again (docs/quality/review-answers.md).

Usage:
  python -I scripts/quality/review_answers.py --body BODY.md --comments COMMENTS.json --head SHA
      --trusted-authors LOGINS [--repo PATH]
      BODY.md        the PR description, as text.
      COMMENTS.json  the PR's issue comments, as `gh api repos/{repo}/issues/{n}/comments --paginate` returns them
                     (one JSON array, or several arrays one after another).
      SHA            the PR's head commit.
      LOGINS         comma-separated GitHub logins whose CODEX REVIEW comments count (the workflow passes the
                     repository owner: every agent posts as that account). Comments by anyone else are ignored.
      PATH           a git checkout holding the PR's history (default: the current folder).
  python -I scripts/quality/review_answers.py --self-test
      Prove every rule on built-in samples (a temporary git repo is made for the commit checks).

The rule:
  1. Only comments by a trusted author count. Take the newest one that starts with `CODEX REVIEW BLOCK — <40-hex sha>`.
     None: nothing to answer, pass. A later `CODEX REVIEW PASS — <sha>` (or "PASS WITH ISSUES — <sha>") clears it
     only when <sha> is the PR's CURRENT head: a review of an older head never clears a BLOCK on a newer one.
  2. Its findings are the lines that start with P0..P3 (`P1 — ...`, `- P2 — ...`, `- **P1** — ...`, `- P2, blocking`).
     A BLOCK with no finding this script can read fails closed: the reviewer's format must be fixed, not skipped.
  3. The PR body has a heading with "Review answers" and, under it, a table with exactly one row per finding, in order.
     Row i's first cell starts with finding i's level. Its last cell is `fixed in <7-40 hex sha>` or
     `disputed: <a reason of at least 10 characters>`.
  4. A `fixed in` commit must exist, be part of the head (or be the head), and be a later commit on top of the
     reviewed one (a descendant of the BLOCK's commit, not that commit itself). The check does not prove WHEN the
     commit was made: a commit already pushed on top of the reviewed one before the review was posted also passes.
Exit 0 when the rule holds, 1 when it does not (with what is missing and how to fix it), 2 on bad usage.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import tempfile

RULE = 'BOO-REVIEW-001'
DASHES = '—–-'  # em dash, en dash, hyphen
BLOCK_RE = re.compile(r'^CODEX REVIEW BLOCK\s*[' + DASHES + r']\s*([0-9a-fA-F]{40})\b')
PASS_RE = re.compile(r'^CODEX REVIEW PASS(?: WITH ISSUES)?\s*[' + DASHES + r']\s*([0-9a-fA-F]{40})\b')
FINDING_RE = re.compile(r'^\s*(?:[-*+]\s+)?(?:\*\*)?(P[0-3])(?:\*\*)?\s*(?:[:,' + DASHES + r']|\*\*)')
HEADING_RE = re.compile(r'^\s{0,3}#{1,6}\s+(.*)$')
FIXED_RE = re.compile(r'^fixed in\s+([0-9a-fA-F]{7,40})\b', re.IGNORECASE)
DISPUTED_RE = re.compile(r'^disputed:\s*(.*)$', re.IGNORECASE | re.DOTALL)
SEPARATOR_CELL = re.compile(r'^:?-{3,}:?$')
MIN_REASON = 10

HOW_TO_FIX = ('How to fix: under a heading "## Review answers" in the PR description, add a table with one row per '
              'finding of that BLOCK, in the same order, for example\n'
              '  | Finding | Answer |\n'
              '  | --- | --- |\n'
              '  | P1 — <short title> | fixed in <sha of a later commit on top of the reviewed one> |\n'
              '  | P2 — <short title> | disputed: <why this is not a real problem> |\n'
              'Fix the findings first; only then ask for a new review.')


def clean(text):
    return (text or '').lstrip('﻿').replace('\r\n', '\n').replace('\r', '\n')


def load_comments(text):
    """Accept one JSON array or several concatenated arrays (what `gh api --paginate` prints)."""
    decoder = json.JSONDecoder()
    items, pos, text = [], 0, clean(text)
    while True:
        while pos < len(text) and text[pos].isspace():
            pos += 1
        if pos >= len(text):
            return items
        value, pos = decoder.raw_decode(text, pos)
        if isinstance(value, list):
            items.extend(value)
        elif isinstance(value, dict):
            items.append(value)
        else:
            raise ValueError('comments JSON must be an array of comment objects')


def author(comment):
    user = comment.get('user') or {}
    return str(user.get('login') or '').lower()


def latest_block(comments, head, trusted):
    """Return (comment, sha) of the newest BLOCK still standing, or (None, None).

    Only comments by a trusted author count. A PASS clears the BLOCK before it only when it names the current head."""
    ordered = sorted(enumerate(comments), key=lambda p: (str(p[1].get('created_at') or ''), p[1].get('id') or 0, p[0]))
    block, sha = None, None
    for _, c in ordered:
        if author(c) not in trusted:
            continue
        body = clean(c.get('body'))
        m = BLOCK_RE.match(body)
        if m:
            block, sha = c, m.group(1).lower()
            continue
        passed = PASS_RE.match(body)
        if block is not None and passed and passed.group(1).lower() == head.lower():
            block, sha = None, None
    return block, sha


def findings_of(body):
    levels = []
    in_code = False
    for line in clean(body).split('\n'):
        if line.lstrip().startswith('```'):
            in_code = not in_code
            continue
        if in_code:
            continue
        m = FINDING_RE.match(line)
        if m:
            levels.append(m.group(1))
    return levels


def strip_hidden(text):
    """Drop HTML comments and fenced code: a table there is not visible as a table on the PR page."""
    text = re.sub(r'<!--.*?-->', '', clean(text), flags=re.DOTALL)
    out, in_code = [], False
    for line in text.split('\n'):
        if line.lstrip().startswith(('```', '~~~')):
            in_code = not in_code
            continue
        if not in_code:
            out.append(line)
    return out


def split_row(line):
    s = line.strip()
    if s.startswith('|'):
        s = s[1:]
    if s.endswith('|') and not s.endswith('\\|'):
        s = s[:-1]
    return [cell.strip() for cell in re.split(r'(?<!\\)\|', s)]


def answers_table(body):
    """Rows (list of cells) of the first table under a heading containing "Review answers"; None if no heading."""
    lines = strip_hidden(body)
    start = None
    for i, line in enumerate(lines):
        h = HEADING_RE.match(line)
        if h and 'review answers' in h.group(1).lower():
            start = i + 1
            break
    if start is None:
        return None
    rows, seen_table = [], False
    for line in lines[start:]:
        if HEADING_RE.match(line):
            break
        if line.strip().startswith('|'):
            seen_table = True
            rows.append(split_row(line))
        elif seen_table and line.strip():
            break
    if len(rows) >= 2 and all(SEPARATOR_CELL.match(c.replace(' ', '')) for c in rows[1] if c):
        return rows[2:]
    return []  # no header and separator line: GitHub does not show it as a table


def unmark(cell):
    return re.sub(r'[`*_]', '', cell).strip()


def git(repo, *args):
    return subprocess.run(['git', '-C', repo] + list(args), capture_output=True, text=True)


def resolve(repo, sha):
    r = git(repo, 'rev-parse', '--verify', '--quiet', sha + '^{commit}')
    return r.stdout.strip() if r.returncode == 0 else None


def is_ancestor(repo, a, b):
    r = git(repo, 'merge-base', '--is-ancestor', a, b)
    if r.returncode not in (0, 1):
        raise RuntimeError(f'git merge-base failed for {a[:12]} and {b[:12]}')
    return r.returncode == 0


def check(body, comments, head, repo, trusted):
    """Return (ok, messages)."""
    trusted = {t.strip().lower() for t in trusted if t and t.strip()}
    if not trusted:
        return False, [f'::error::{RULE} no trusted reviewer login was given (--trusted-authors); failing closed.']
    block, block_sha = latest_block(comments, head, trusted)
    if block is None:
        return True, ['REVIEW_ANSWERS_OK no open CODEX REVIEW BLOCK on this PR (none, or a later CODEX REVIEW PASS '
                      'for this exact head).']
    where = block.get('html_url') or f"comment {block.get('id')}"
    levels = findings_of(block.get('body'))
    if not levels:
        return False, [f'::error::{RULE} the newest CODEX REVIEW BLOCK ({block_sha[:12]}, {where}) has no finding this '
                       'check can read (a line starting with P0, P1, P2 or P3). Failing closed. How to fix: the reviewer '
                       'reposts the BLOCK with one line per finding, like "P1 — <title>".']
    want = ', '.join(levels)
    rows = answers_table(body)
    if rows is None or not rows:
        what = 'no "Review answers" heading' if rows is None else 'no table rows under the "Review answers" heading'
        return False, [f'::error::{RULE} the newest CODEX REVIEW BLOCK ({block_sha[:12]}, {where}) has {len(levels)} '
                       f'finding(s) ({want}) but the PR description has {what}.', HOW_TO_FIX]
    errors = []
    if len(rows) != len(levels):
        errors.append(f'the "Review answers" table has {len(rows)} row(s) but the BLOCK has {len(levels)} finding(s) '
                      f'({want}); one row per finding, in order')
    repo_ok = None
    head_full = None
    block_full = None
    for i, (level, row) in enumerate(zip(levels, rows), start=1):
        first = unmark(row[0]) if row else ''
        if not first.upper().startswith(level):
            errors.append(f'row {i} should answer finding {i} ({level}) but its first cell is "{first[:40]}"')
            continue
        answer = unmark(row[-1]) if len(row) >= 2 else ''
        fixed = FIXED_RE.match(answer)
        disputed = DISPUTED_RE.match(answer)
        if disputed:
            if len(disputed.group(1).strip()) < MIN_REASON:
                errors.append(f'row {i} ({level}) says "disputed:" with a reason shorter than {MIN_REASON} characters')
            continue
        if not fixed:
            errors.append(f'row {i} ({level}) answer must be "fixed in <sha>" or "disputed: <reason>", '
                          f'not "{answer[:40]}"')
            continue
        if repo_ok is None:
            head_full = resolve(repo, head)
            block_full = resolve(repo, block_sha)
            repo_ok = bool(head_full and block_full)
            if not head_full:
                errors.append(f'the head commit {head[:12]} is not in the checkout at {repo}')
            if not block_full:
                errors.append(f'the BLOCK commit {block_sha[:12]} is not in the history of this checkout')
        if not repo_ok:
            continue
        fix_sha = fixed.group(1)
        fix_full = resolve(repo, fix_sha)
        if not fix_full:
            errors.append(f'row {i} ({level}): commit {fix_sha} does not exist in this repository')
        elif not is_ancestor(repo, fix_full, head_full):
            errors.append(f'row {i} ({level}): commit {fix_sha} is not part of this PR head ({head[:12]})')
        elif is_ancestor(repo, fix_full, block_full):
            errors.append(f'row {i} ({level}): commit {fix_sha} is the reviewed commit or older than it '
                          f'({block_sha[:12]}); a fix must be a later commit on top of the reviewed one')
    if errors:
        return False, [f'::error::{RULE} the newest CODEX REVIEW BLOCK ({block_sha[:12]}, {where}) is not fully '
                       f'answered: ' + '; '.join(errors) + '.', HOW_TO_FIX]
    return True, [f'REVIEW_ANSWERS_OK all {len(levels)} finding(s) ({want}) of the CODEX REVIEW BLOCK on '
                  f'{block_sha[:12]} are answered in the PR description.']


# ---------------------------------------------------------------------------------------------------------- self-test

def _commit(repo, name):
    with open(os.path.join(repo, name), 'w', encoding='utf-8') as f:
        f.write(name + '\n')
    for args in (['add', name], ['-c', 'user.name=selftest', '-c', 'user.email=selftest@example.invalid',
                                 'commit', '-q', '--no-gpg-sign', '-m', name]):
        r = git(repo, *args)
        if r.returncode != 0:
            raise RuntimeError('self-test git failed: ' + r.stderr.strip())
    return git(repo, 'rev-parse', 'HEAD').stdout.strip()


def self_test():
    failures = 0
    with tempfile.TemporaryDirectory() as repo:
        if git(repo, 'init', '-q').returncode != 0:
            print('SELF-TEST FAILED: git init did not work')
            return 1
        old = _commit(repo, 'a.txt')
        reviewed = _commit(repo, 'b.txt')
        fix = _commit(repo, 'c.txt')
        head = _commit(repo, 'd.txt')
        git(repo, 'checkout', '-q', '-b', 'other', old)
        stranger = _commit(repo, 'e.txt')
        git(repo, 'checkout', '-q', '-')
        missing = 'f' * 40

        def c(i, body, login='owner'):
            return {'id': i, 'created_at': f'2026-11-14T10:{i:02d}:00Z', 'body': body, 'user': {'login': login}}

        block = c(2, '﻿CODEX REVIEW BLOCK — ' + reviewed + '\n\n- P1 — test finding one\n'
                     'more text\n- **P2** — test finding two\n\n| Label | x |\n|---|---|\n| UNVERIFIED | y |\n')

        def body(*rows):
            lines = ['## Summary', 'text', '', '## Review answers', '', '| Finding | Answer |', '| --- | --- |']
            lines += [f'| {a} | {b} |' for a, b in rows]
            return '\n'.join(lines + ['', '## Evidence', 'x'])

        good = body(('P1 — test finding one', f'fixed in `{fix[:7]}`'),
                    ('P2 — test finding two', f'fixed in {head}'))
        cases = [
            ('no BLOCK comment at all', '', [c(1, 'hello')], True),
            ('BLOCK then a later PASS', '', [block, c(3, 'CODEX REVIEW PASS WITH ISSUES — ' + head)], True),
            ('BLOCK with 2 findings and a correct table', good, [c(1, 'hi'), block], True),
            ('BLOCK, PASS, then a newer BLOCK with no answers', '',
             [c(1, 'CODEX REVIEW BLOCK — ' + old + '\nP1 — x'), c(3, 'CODEX REVIEW PASS — x'),
              c(4, block['body'])], 'no "Review answers" heading'),
            ('missing table', '## Summary\nnothing here', [block], 'no "Review answers" heading'),
            ('table hidden in an HTML comment', '<!--\n' + good + '\n-->', [block], 'no "Review answers" heading'),
            ('one row missing', body(('P1 — one', f'fixed in {fix}')), [block], 'has 1 row(s)'),
            ('wrong level order', body(('P2 — two', f'fixed in {fix}'), ('P1 — one', f'fixed in {fix}')),
             [block], 'row 1 should answer finding 1 (P1)'),
            ('fixed in the BLOCK commit itself', body(('P1', f'fixed in {reviewed}'), ('P2', f'fixed in {fix}')),
             [block], 'later commit on top of the reviewed one'),
            ('fixed in a commit older than the BLOCK', body(('P1', f'fixed in {old}'), ('P2', f'fixed in {fix}')),
             [block], 'later commit on top of the reviewed one'),
            ('fixed in an unknown sha', body(('P1', f'fixed in {missing}'), ('P2', f'fixed in {fix}')),
             [block], 'does not exist'),
            ('fixed in a commit not in the head', body(('P1', f'fixed in {stranger}'), ('P2', f'fixed in {fix}')),
             [block], 'not part of this PR head'),
            ('disputed with a reason', body(('P1', f'fixed in {fix}'),
                                            ('**P2**', 'disputed: the reviewer read an old copy of the file')),
             [block], True),
            ('disputed with too short a reason', body(('P1', f'fixed in {fix}'), ('P2', 'disputed: no')),
             [block], 'shorter than'),
            ('answer that is neither fixed nor disputed', body(('P1', 'will do'), ('P2', f'fixed in {fix}')),
             [block], 'must be "fixed in'),
            ('BLOCK with no parseable findings', good,
             [c(2, 'CODEX REVIEW BLOCK — ' + reviewed + '\n\nSomething is wrong somewhere.')], 'Failing closed'),
            ('a PASS for an OLDER head does not clear a newer BLOCK', '',
             [block, c(3, 'CODEX REVIEW PASS — ' + reviewed)], 'no "Review answers" heading'),
            ('a PASS by an unrelated author does not clear the BLOCK', '',
             [block, c(3, 'CODEX REVIEW PASS — ' + head, login='someone-else')], 'no "Review answers" heading'),
            ('a BLOCK by an unrelated author is ignored', '',
             [c(2, block['body'], login='someone-else')], True),
            ('a PASS that names no commit does not clear the BLOCK', '',
             [block, c(3, 'CODEX REVIEW PASS')], 'no "Review answers" heading'),
            ('finding styles: "P2, blocking" and plain "P1 —"', body(('P1', f'fixed in {fix}'),
                                                                     ('P2', f'fixed in {fix}')),
             [c(2, 'CODEX REVIEW BLOCK — ' + reviewed + '\nP1 — a\n* P2, blocking — b\n'
                   'A P3 in prose is not a finding.')], True),
        ]
        # expect: True = must pass; a string = must fail, and its message must say that (the right reason).
        for name, b, comments, expect in cases:
            ok, messages = check(b, comments, head, repo, ['Owner'])
            right = ok if expect is True else (not ok and expect in ' '.join(messages))
            failures += not right
            want = 'pass' if expect is True else f'fail with "{expect}"'
            print(f'  [{"ok" if right else "WRONG"}] {name}: expected {want}, got {"pass" if ok else "fail"}')
            if not right:
                for m in messages:
                    print('        ' + m.splitlines()[0])
        ok, messages = check('', [block], head, repo, [])
        right = (not ok) and 'failing closed' in ' '.join(messages)
        failures += not right
        print(f'  [{"ok" if right else "WRONG"}] no trusted login given: expected fail closed, '
              f'got {"pass" if ok else "fail"}')
        # the paginated JSON shape
        try:
            parsed = load_comments('[{"id":1,"body":"a"}]\n[{"id":2,"body":"b"}]')
            assert [x['id'] for x in parsed] == [1, 2]
            print('  [ok] paginated comment JSON (two arrays) is read as one list')
        except Exception as e:  # noqa: BLE001
            failures += 1
            print(f'  [WRONG] paginated comment JSON: {e}')
    if failures:
        print(f'SELF-TEST FAILED: {failures} case(s) gave the wrong answer ({RULE}).')
        return 1
    print(f'SELF-TEST OK: every {RULE} case gave the expected answer.')
    return 0


def main(argv=None):
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    p = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    p.add_argument('--self-test', action='store_true')
    p.add_argument('--body')
    p.add_argument('--comments')
    p.add_argument('--head')
    p.add_argument('--repo', default='.')
    p.add_argument('--trusted-authors', default='')
    a = p.parse_args(argv)
    if a.self_test:
        return self_test()
    if not (a.body and a.comments and a.head):
        p.error('--body, --comments and --head are required (or --self-test)')
    if not re.fullmatch(r'[0-9a-fA-F]{40}', a.head):
        p.error('--head must be a full 40-character commit sha')
    with open(a.body, encoding='utf-8-sig', errors='replace') as f:
        body = f.read()
    with open(a.comments, encoding='utf-8-sig') as f:
        comments = load_comments(f.read())
    ok, messages = check(body, comments, a.head.lower(), a.repo, a.trusted_authors.split(','))
    for m in messages:
        print(m)
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
