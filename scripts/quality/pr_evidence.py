"""BOO-EVID-001: a pull request body must carry an evidence table (docs/quality/RULES.md).

  python -I scripts/quality/pr_evidence.py --self-test
  python -I scripts/quality/pr_evidence.py --event "$GITHUB_EVENT_PATH"        (in the pr-evidence workflow)
  python -I scripts/quality/pr_evidence.py --body FILE [--changed FILE]       (local check of a draft body)

The rule:
  1. The body has at least one Markdown table with a column headed `Label`.
  2. In every such table, each row's label is one of: live, mock, manual, UNVERIFIED, BLOCKED, review
     (the first word of the cell; bold, backticks and words after it are allowed: `**live** (OpenAI)`).
  3. Every `live` row links a GitHub Actions run of this repository (https://github.com/OWNER/REPO/actions/runs/N).
  4. A PR that changes anything under src/ or tools/ has at least one `live` row, or a `BLOCKED` row that says why.

It reads the body only from the event file (never from a ${{ }} expression in a shell), and never runs it.
"""
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile

RULE = 'BOO-EVID-001'
LABELS = {'live', 'mock', 'manual', 'unverified', 'blocked', 'review'}
SEPARATOR_CELL = re.compile(r'^:?-{3,}:?$')
DECORATION = re.compile(r'[*_`~]')


def _cells(line):
    """Cells of one Markdown table row; `\\|` is a literal pipe, so is a pipe inside `code`."""
    text = line.strip()
    if text.startswith('|'):
        text = text[1:]
    if text.endswith('|') and not text.endswith('\\|'):
        text = text[:-1]
    cells, cur, in_code, i = [], '', False, 0
    while i < len(text):
        ch = text[i]
        if ch == '\\' and i + 1 < len(text) and text[i + 1] == '|':
            cur += '|'
            i += 2
            continue
        if ch == '`':
            in_code = not in_code
        if ch == '|' and not in_code:
            cells.append(cur.strip())
            cur = ''
        else:
            cur += ch
        i += 1
    cells.append(cur.strip())
    return cells


def tables(body):
    """Every Markdown table in the body as (header cells, [(line number, row cells)]). Fenced code is skipped."""
    lines = (body or '').replace('\r\n', '\n').replace('\r', '\n').split('\n')
    found, i, fence = [], 0, None
    while i < len(lines):
        stripped = lines[i].strip()
        marker = re.match(r'^(`{3,}|~{3,})', stripped)
        if marker:
            if fence is None:
                fence = marker.group(1)[0]
            elif stripped.startswith(fence * 3):
                fence = None
            i += 1
            continue
        if fence is None and '|' in stripped and i + 1 < len(lines):
            header, sep = _cells(lines[i]), _cells(lines[i + 1])
            if len(sep) == len(header) and all(SEPARATOR_CELL.match(c.replace(' ', '')) for c in sep):
                rows, j = [], i + 2
                while j < len(lines) and lines[j].strip() and '|' in lines[j]:
                    rows.append((j + 1, _cells(lines[j])))
                    j += 1
                found.append((header, rows))
                i = j
                continue
        i += 1
    return found


def label_of(cell):
    """The first word of a label cell, decoration removed, lower-cased ('**live** (CI)' -> 'live')."""
    words = DECORATION.sub('', cell).strip().split()
    return re.sub(r'[^A-Za-z]', '', words[0]).lower() if words else ''


def check(body, changed_files, repo):
    """Problems (plain-words strings); empty means the body passes."""
    problems = []
    run_link = re.compile(r'https://github\.com/' + re.escape(repo) + r'/actions/runs/\d+', re.IGNORECASE)
    evidence = []
    for header, rows in tables(body):
        names = [label_of(h) for h in header]
        if 'label' in names:
            evidence.append((names.index('label'), rows))
    if not evidence:
        return ['the PR body has no evidence table: add a Markdown table with a column headed "Label" '
                '(values: live, mock, manual, UNVERIFIED, BLOCKED, review), one row per claim']
    labels_seen = []
    for col, rows in evidence:
        if not rows:
            problems.append('the evidence table has a header but no rows')
        for line, cells in rows:
            cell = cells[col] if col < len(cells) else ''
            label = label_of(cell)
            if label not in LABELS:
                problems.append(f'body line {line}: label "{cell[:40]}" is not one of live, mock, manual, '
                                'UNVERIFIED, BLOCKED, review')
                continue
            labels_seen.append(label)
            other = ' '.join(c for k, c in enumerate(cells) if k != col)
            if label == 'live' and not run_link.search(' '.join(cells)):
                problems.append(f'body line {line}: a "live" row needs a link to a GitHub Actions run of {repo} '
                                f'(https://github.com/{repo}/actions/runs/<id>); no run link means UNVERIFIED')
            if label == 'blocked' and not re.search(r'[A-Za-z]{3,}', DECORATION.sub('', other)):
                problems.append(f'body line {line}: a "BLOCKED" row must say why (what blocks the live run)')
    touches_product = sorted(f for f in changed_files if f.startswith(('src/', 'tools/')))
    if touches_product and not ({'live', 'blocked'} & set(labels_seen)):
        problems.append(f'this PR changes {touches_product[0]}' + (f' and {len(touches_product) - 1} more file(s)'
                        if len(touches_product) > 1 else '') + ' under src/ or tools/, so the evidence table needs '
                        'at least one "live" row with a run link, or a "BLOCKED" row that says why')
    return problems


def changed_from_git(base, head):
    """Files the PR changes (base...head). Raises when git cannot tell, so the check fails closed."""
    out = subprocess.run(['git', '-c', 'core.quotePath=false', 'diff', '--name-only', '--no-renames', '-z',
                          f'{base}...{head}'], capture_output=True, check=True)
    return [f for f in out.stdout.decode('utf-8', 'replace').split('\0') if f]


def report(problems, out=print):
    for p in problems:
        out(f'::error::{RULE} {p}')
    if problems:
        out(f'PR_EVIDENCE_FAILED {len(problems)} problem(s). Fix: docs/quality/RULES.md, {RULE}')
        return 1
    out('PR_EVIDENCE_OK')
    return 0


def self_test():
    results = []

    def expect(name, ok):
        results.append((name, bool(ok)))

    repo = 'owner/repo'
    run = f'https://github.com/{repo}/actions/runs/123'
    good = ('Intro\n\n| Claim | Label | Proof |\n| --- | :---: | --- |\n'
            f'| round trip | **live** (OpenAI) | [run]({run}) |\n| demo | mock | [r]({run}) |\n'
            '| Claude | UNVERIFIED | not tested |\n| review | review | REVIEW PASS |\n')
    expect('a good table passes', check(good, ['src/a.cs'], repo) == [])
    expect('no table fails', check('Just words, no table.', [], repo) != [])
    expect('a table without a Label column fails', check('| a | b |\n| - | - |\n| 1 | 2 |\n', [], repo) != [])
    expect('an unknown label fails', check(good.replace('| mock |', '| passed |'), [], repo) != [])
    expect('a live row without a run link fails', check(good.replace(f'[run]({run})', 'see logs'), [], repo) != [])
    expect('a live row linking another repo fails',
           check(good.replace(f'[run]({run})', '[r](https://github.com/x/y/actions/runs/9)'), [], repo) != [])
    expect('a live row linking a non-run page fails',
           check(good.replace(f'[run]({run})', f'[r](https://github.com/{repo}/pull/9)'), [], repo) != [])
    no_live = good.replace('| **live** (OpenAI) |', '| mock |')
    expect('src/ change without live or BLOCKED fails', check(no_live, ['src/Boo.App/A.cs'], repo) != [])
    expect('tools/ change without live or BLOCKED fails', check(no_live, ['tools/x/A.cs'], repo) != [])
    expect('docs-only change without live passes', check(no_live, ['docs/a.md'], repo) == [])
    blocked = no_live + '| live run | BLOCKED | no runner today, key missing |\n'
    expect('src/ change with a BLOCKED row that says why passes', check(blocked, ['src/a.cs'], repo) == [])
    expect('a BLOCKED row without a reason fails',
           check(no_live + '| x | BLOCKED |  |\n', ['src/a.cs'], repo) != [])
    expect('a table only inside a code fence does not count', check('```\n' + good + '```\n', [], repo) != [])
    expect('an escaped pipe stays inside its cell',
           check(good.replace('| demo |', '| a \\| b |'), ['src/a.cs'], repo) == [])
    expect('a pipe inside `code` stays inside its cell',
           check(good.replace('| demo |', '| `a|b` |'), ['src/a.cs'], repo) == [])
    expect('a header-only evidence table fails', check('| Claim | Label |\n| --- | --- |\n', [], repo) != [])
    expect('CRLF bodies parse', check(good.replace('\n', '\r\n'), ['src/a.cs'], repo) == [])
    expect('Label header in bold is found', check(good.replace('| Label |', '| **Label** |'), ['src/a.cs'], repo) == [])
    expect('an empty body fails', check('', [], repo) != [] and check(None, [], repo) != [])
    with tempfile.TemporaryDirectory() as tmp:
        event = pathlib.Path(tmp) / 'event.json'
        event.write_text(json.dumps({'pull_request': {'body': None, 'base': {'sha': 'x'}, 'head': {'sha': 'y'}}}),
                         encoding='utf-8')
        expect('an event with a null body fails closed', run_event(event, repo, out=lambda *_: None) == 1)
    failed = [n for n, ok in results if not ok]
    for n in failed:
        print(f'  FAILED: {n}')
    print(f'PR_EVIDENCE_SELF_TEST_{"FAILED" if failed else "PASSED"} {len(results) - len(failed)} of {len(results)}')
    return 1 if failed else 0


def run_event(event_path, repo, out=print):
    event = json.loads(pathlib.Path(event_path).read_text(encoding='utf-8'))
    pr = event.get('pull_request') or {}
    body = pr.get('body') or ''
    try:
        changed = changed_from_git(pr['base']['sha'], pr['head']['sha'])
    except (KeyError, TypeError, OSError, subprocess.CalledProcessError):
        changed = None
    problems = check(body, changed or [], repo)
    if changed is None:
        problems.append('could not list the files this PR changes (git diff base...head failed); failing closed')
    return report(problems, out)


def main(argv):
    if argv == ['--self-test']:
        return self_test()
    repo = os.environ.get('GITHUB_REPOSITORY', 'vikas53953/bluey')
    if len(argv) == 2 and argv[0] == '--event':
        return run_event(argv[1], repo)
    if len(argv) in (2, 4) and argv[0] == '--body' and (len(argv) == 2 or argv[2] == '--changed'):
        body = pathlib.Path(argv[1]).read_text(encoding='utf-8')
        changed = pathlib.Path(argv[3]).read_text(encoding='utf-8').split() if len(argv) == 4 else []
        return report(check(body, changed, repo))
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
