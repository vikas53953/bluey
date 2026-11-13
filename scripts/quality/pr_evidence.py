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

It judges only what a reader of the rendered PR sees, by GitHub's own Markdown rules: HTML comments, fenced and
indented code blocks are not evidence, and table cells split on every unescaped `|` (also inside `code`).
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
SEPARATOR_CELL = re.compile(r'^:?-+:?$')          # GitHub accepts one dash or more
DECORATION = re.compile(r'[*_`~]')


def _cells(line):
    """Cells of one table row, split as GitHub does: on every `|` not escaped as `\|` (inside `code` too)."""
    text = line.strip()
    if text.startswith('|'):
        text = text[1:]
    if text.endswith('|') and not text.endswith('\\|'):
        text = text[:-1]
    return [c.replace('\\|', '|').strip() for c in re.split(r'(?<!\\)\|', text)]


def visible(body):
    """The body as a reader sees it: HTML comments removed (an unclosed `<!--` hides everything after it)."""
    text = (body or '').replace('\r\n', '\n').replace('\r', '\n')
    text = re.sub(r'<!--.*?-->', lambda m: '\n' * m.group(0).count('\n'), text, flags=re.S)
    cut = text.find('<!--')
    return text if cut < 0 else text[:cut]


def _indent(line):
    return len(line) - len(line.lstrip(' '))


def tables(body):
    """Every Markdown table a reader sees, as (header cells, [(line number, row cells)]). Fenced code (closed only by
    the same character, at least as long, nothing after it) and indented code (4+ spaces) are not tables."""
    lines = visible(body).split('\n')
    found, i, fence = [], 0, None
    while i < len(lines):
        line = lines[i]
        stripped = line.strip()
        if fence is not None:
            close = re.match(r'^(`{3,}|~{3,})\s*$', stripped)
            if _indent(line) < 4 and close and close.group(1)[0] == fence[0] and len(close.group(1)) >= len(fence):
                fence = None
            i += 1
            continue
        opening = re.match(r'^(`{3,}|~{3,})', stripped)
        if _indent(line) < 4 and opening and not (opening.group(1)[0] == '`'
                                                   and '`' in stripped[len(opening.group(1)):]):
            fence = opening.group(1)
            i += 1
            continue
        if _indent(line) < 4 and '|' in stripped and i + 1 < len(lines) and _indent(lines[i + 1]) < 4:
            header, sep = _cells(line), _cells(lines[i + 1])
            if len(sep) == len(header) and all(SEPARATOR_CELL.match(c.replace(' ', '')) for c in sep):
                rows, j = [], i + 2
                while j < len(lines) and lines[j].strip() and '|' in lines[j] and _indent(lines[j]) < 4:
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
    run_link = re.compile(r'https://github\.com/' + re.escape(repo) + r'/actions/runs/\d+(?![\w-])', re.IGNORECASE)
    evidence = []
    for header, rows in tables(body):
        cols = [k for k, h in enumerate(header) if label_of(h) == 'label']
        if cols:
            evidence.append((cols, rows))
    if not evidence:
        return ['the PR body has no evidence table a reader can see: add a Markdown table with a column headed '
                '"Label" (values: live, mock, manual, UNVERIFIED, BLOCKED, review), one row per claim, outside code '
                'blocks and HTML comments']
    labels_seen = []
    for cols, rows in evidence:
        if not rows:
            problems.append('the evidence table has a header but no rows')
        for line, cells in rows:
            for col in cols:
                cell = cells[col] if col < len(cells) else ''
                label = label_of(cell)
                if label not in LABELS:
                    problems.append(f'body line {line}: label "{cell[:40]}" is not one of live, mock, manual, '
                                    'UNVERIFIED, BLOCKED, review')
                    continue
                labels_seen.append(label)
                if label == 'live' and not run_link.search(' '.join(cells)):
                    problems.append(f'body line {line}: a "live" row needs a link to a GitHub Actions run of {repo} '
                                    f'(https://github.com/{repo}/actions/runs/<id>); no run link means UNVERIFIED')
                if label == 'blocked':
                    after = DECORATION.sub('', cell).strip().split(None, 1)[1:] + cells[col + 1:]
                    if not re.search(r'[A-Za-z]{3,}', DECORATION.sub('', ' '.join(after))):
                        problems.append(f'body line {line}: a "BLOCKED" row must say why, after the label (in the '
                                        'same cell or the columns to its right)')
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
    expect('a BLOCKED row without a reason fails, even with a long claim',
           check(no_live + '| Real AI round trip | BLOCKED |  |\n', ['src/a.cs'], repo) != [])
    expect('a BLOCKED reason in the label cell counts',
           check(no_live + '| Real AI round trip | BLOCKED: no runner today |  |\n', ['src/a.cs'], repo) == [])
    expect('a table inside an HTML comment does not count',
           check('Nothing tested.\n<!--\n' + good + '-->\n', ['src/a.cs'], repo) != [])
    expect('an unclosed HTML comment hides the rest', check('x\n<!--\n' + good, [], repo) != [])
    expect('a live row whose only run link is inside a comment fails',
           check(good.replace(f'[run]({run})', f'<!-- {run} -->'), [], repo) != [])
    expect('a table inside a 4-backtick fence closed only by 3 backticks does not count',
           check('````\n```\n' + good + '````\n', [], repo) != [])
    expect('a fence line with words after it does not close the fence',
           check('```\n``` words\n' + good + '```\n', [], repo) != [])
    expect('a table in an indented code block does not count',
           check('\n'.join('    ' + x for x in good.split('\n')), [], repo) != [])
    expect('one-dash separators (valid on GitHub) make a table',
           check(good.replace('| --- | :---: | --- |', '|-|:-:|-|'), ['src/a.cs'], repo) == [])
    expect('every Label column is judged',
           check('| Label | Claim | Label |\n| - | - | - |\n| mock | round trip | live |\n', [], repo) != [])
    expect('a run link must end at the run number',
           check(good.replace(f'[run]({run})', f'[run]({run}abc)'), [], repo) != [])
    expect('a table only inside a code fence does not count', check('```\n' + good + '```\n', [], repo) != [])
    expect('an escaped pipe stays inside its cell',
           check(good.replace('| demo |', '| a \\| b |'), ['src/a.cs'], repo) == [])
    expect('a pipe inside `code` splits the cell, as on GitHub',
           _cells('| `a|b` | mock |') == ['`a', 'b`', 'mock'])
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
        changed = pathlib.Path(argv[3]).read_text(encoding='utf-8').splitlines() if len(argv) == 4 else []
        return report(check(body, changed, repo))
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
