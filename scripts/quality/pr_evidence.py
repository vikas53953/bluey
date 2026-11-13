"""BOO-EVID-001: a pull request body must carry an evidence table (docs/quality/RULES.md).

  python -I scripts/quality/pr_evidence.py --self-test          rules, on HTML samples (offline)
  python -I scripts/quality/pr_evidence.py --render-test        known tricks, rendered by GitHub itself (network)
  python -I scripts/quality/pr_evidence.py --event "$GITHUB_EVENT_PATH"   (in the pr-evidence workflow)
  python -I scripts/quality/pr_evidence.py --body FILE [--changed FILE]  (local check of a draft body)

The body is not parsed here. GitHub renders it (POST /markdown, gfm mode, this repository as context), and the check
reads only the rendered HTML: the real tables and the real link targets. So whatever a reader of the PR page cannot see
(HTML comments, code blocks, text in attributes, a URL that is only a link's hover title) does not count, by
construction. If GitHub cannot render the body, the check fails closed.

The rule:
  1. The rendered body has at least one table with a column headed `Label`.
  2. In every such table, each row's label is one of: live, mock, manual, UNVERIFIED, BLOCKED, review
     (the first word of the cell; words after it are allowed: `live (OpenAI)`).
  3. Every `live` row has a link (an <a href>) to a GitHub Actions run of this repository.
  4. A PR that changes anything under src/ or tools/ has at least one `live` row, or a `BLOCKED` row that says why
     (after the label, in the same cell or a column to its right).
"""
import html.parser
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request

RULE = 'BOO-EVID-001'
LABELS = {'live', 'mock', 'manual', 'unverified', 'blocked', 'review'}
API = 'https://api.github.com/markdown'


class _Table:
    def __init__(self):
        self.rows, self.row, self.cell, self.plain = [], None, None, True


class _Tables(html.parser.HTMLParser):
    """Every table in rendered HTML, with open tables kept on a stack (a table inside a cell is its own table and
    makes the outer one not plain). A cell is (text, hrefs of links that show something); a merged cell
    (colspan/rowspan) also makes its table not plain, because its columns no longer line up with the header."""

    VOID = {'br', 'img', 'hr', 'input', 'meta', 'link', 'wbr', 'source', 'col', 'area', 'base', 'embed', 'track'}

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.tables, self._open, self._tags, self._links = [], [], [], []

    def _top(self):
        return self._open[-1] if self._open else None

    def handle_starttag(self, tag, attrs):
        names = dict(attrs)
        top = self._top()
        if tag in self.VOID:
            if top is not None and top.cell is not None:
                if tag == 'br':
                    top.cell[0].append(' ')
                if tag == 'img':                          # an image shows something: its link is visible
                    for link in self._links:
                        link[1] = True
            return
        self._tags.append(tag)
        if tag == 'table':
            if top is not None:
                top.plain = False                         # a table inside a table: the outer one is not a grid
            table = _Table()
            self.tables.append(table)
            self._open.append(table)
        elif top is None:
            return
        elif tag == 'tr':
            top.row = []
            top.rows.append(top.row)
        elif tag in ('td', 'th') and top.row is not None:
            if names.get('colspan', '1') != '1' or names.get('rowspan', '1') != '1':
                top.plain = False
            top.cell = ([], [])
            top.row.append(top.cell)
        elif tag == 'a' and top.cell is not None and names.get('href'):
            self._links.append([names['href'], False, top.cell])

    def handle_endtag(self, tag):
        if tag not in self._tags:
            return                                         # a stray closing tag closes nothing
        while self._tags:
            open_tag = self._tags.pop()
            top = self._top()
            if open_tag == 'a' and self._links:
                href, shown, cell = self._links.pop()
                if shown:
                    cell[1].append(href)
            elif open_tag in ('td', 'th') and top is not None:
                top.cell = None
            elif open_tag == 'tr' and top is not None:
                top.row = None
            elif open_tag == 'table' and self._open:
                self._open.pop()
            if open_tag == tag:
                break

    def handle_data(self, data):
        top = self._top()
        if top is not None and top.cell is not None:
            top.cell[0].append(data)
            if data.strip():
                for link in self._links:
                    link[1] = True


def tables(rendered_html):
    """[(header cell texts, [(row number, [(text, hrefs)])], plain)] for every table on the rendered page."""
    parser = _Tables()
    parser.feed(rendered_html or '')
    parser.close()
    out = []
    for table in parser.tables:
        rows = [[(' '.join(''.join(text).split()), hrefs) for text, hrefs in r] for r in table.rows if r]
        if not rows:
            continue
        header, body = rows[0], rows[1:]
        out.append(([text for text, _ in header], list(enumerate(body, 1)), table.plain))
    return out


def label_of(text):
    """The first word with a letter in it, lower-cased, letters only ('UNVERIFIED (no key)' -> 'unverified',
    a leading emoji is skipped: '✅ live' -> 'live')."""
    words = [w for w in (text or '').split() if re.search(r'[A-Za-z]', w)]
    return re.sub(r'[^A-Za-z]', '', words[0]).lower() if words else ''


def check(rendered_html, changed_files, repo):
    """Problems (plain-words strings); empty means the rendered body passes."""
    problems = []
    run_link = re.compile(r'^https://github\.com/' + re.escape(repo)
                          + r'/actions/runs/\d+(?:/job/\d+|/attempts/\d+)?/?(?:[?#][^/]*)?$', re.IGNORECASE)
    evidence = []
    for k, (header, rows, plain) in enumerate(tables(rendered_html), 1):
        cols = [c for c, h in enumerate(header) if label_of(h) == 'label'
                and len([w for w in h.split() if re.search(r'[A-Za-z]', w)]) == 1]
        if not cols:
            continue
        if not plain:
            problems.append(f'evidence table {k} is not a plain grid (merged cells or a table inside it): write it '
                            'as a Markdown pipe table')
            continue
        evidence.append((k, cols, rows))
    if not evidence and problems:
        return problems
    if not evidence:
        return ['the PR body has no evidence table a reader can see: add a Markdown table with a column headed '
                '"Label" (values: live, mock, manual, UNVERIFIED, BLOCKED, review), one row per claim, outside code '
                'blocks and HTML comments']
    labels_seen = []
    for k, cols, rows in evidence:
        if not rows:
            problems.append(f'evidence table {k} has a header but no rows')
        for n, cells in rows:
            where = f'evidence table {k}, row {n}'
            hrefs = [h for _, hs in cells for h in hs]
            for col in cols:
                text = cells[col][0] if col < len(cells) else ''
                label = label_of(text)
                if label not in LABELS:
                    problems.append(f'{where}: label "{text[:40]}" is not one of live, mock, manual, UNVERIFIED, '
                                    'BLOCKED, review')
                    continue
                labels_seen.append(label)
                if label == 'live' and not any(run_link.match(h) for h in hrefs):
                    problems.append(f'{where}: a "live" row needs a link to a GitHub Actions run of {repo} '
                                    f'(https://github.com/{repo}/actions/runs/<id>); no run link means UNVERIFIED')
                if label == 'blocked':
                    after = text.split(None, 1)[1:] + [t for t, _ in cells[col + 1:]]
                    if not re.search(r'[A-Za-z]{3,}', ' '.join(after)):
                        problems.append(f'{where}: a "BLOCKED" row must say why, after the label (in the same cell '
                                        'or the columns to its right)')
    touches_product = sorted(f for f in changed_files if f.startswith(('src/', 'tools/')))
    if touches_product and not ({'live', 'blocked'} & set(labels_seen)):
        problems.append(f'this PR changes {touches_product[0]}' + (f' and {len(touches_product) - 1} more file(s)'
                        if len(touches_product) > 1 else '') + ' under src/ or tools/, so the evidence table needs '
                        'at least one "live" row with a run link, or a "BLOCKED" row that says why')
    return problems


def render(body, repo, token=None):
    """The body as GitHub renders it on the PR page. Raises on any failure (the caller fails closed)."""
    data = json.dumps({'text': body or '', 'mode': 'gfm', 'context': repo}).encode('utf-8')
    request = urllib.request.Request(API, data=data, method='POST', headers={
        'Accept': 'application/vnd.github+json', 'Content-Type': 'application/json',
        'User-Agent': 'pr-evidence-check', 'X-GitHub-Api-Version': '2022-11-28'})
    if token:
        request.add_header('Authorization', f'Bearer {token}')
    with urllib.request.urlopen(request, timeout=30) as response:
        if response.status != 200:
            raise RuntimeError(f'GitHub markdown API answered {response.status}')
        return response.read().decode('utf-8')


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


def _finish(results, name):
    failed = [n for n, ok in results if not ok]
    for n in failed:
        print(f'  FAILED: {n}')
    print(f'PR_EVIDENCE_{name}_{"FAILED" if failed else "PASSED"} {len(results) - len(failed)} of {len(results)}')
    return 1 if failed else 0


def self_test():
    """The rules on HTML shaped like GitHub's rendering (offline)."""
    results = []

    def expect(name, ok):
        results.append((name, bool(ok)))

    repo = 'owner/repo'
    run = f'https://github.com/{repo}/actions/runs/123'

    def table(rows, header=('Claim', 'Label', 'Proof')):
        head = ''.join(f'<th>{h}</th>' for h in header)
        body = ''.join('<tr>' + ''.join(f'<td>{c}</td>' for c in r) + '</tr>' for r in rows)
        return f'<table><thead><tr>{head}</tr></thead><tbody>{body}</tbody></table>'

    live = ['round trip', '<strong>live</strong> (OpenAI)', f'<a href="{run}">run</a>']
    rows = [live, ['demo', 'mock', f'<a href="{run}">r</a>'], ['Claude', 'UNVERIFIED', 'not tested'],
            ['review', 'review', 'REVIEW PASS']]
    good = '<p>Intro</p>' + table(rows)
    expect('a good table passes', check(good, ['src/a.cs'], repo) == [])
    expect('no table fails', check('<p>Just words.</p>', [], repo) != [])
    expect('an empty body fails', check('', [], repo) != [] and check(None, [], repo) != [])
    expect('a table without a Label column fails', check(table([['1', '2']], ('a', 'b')), [], repo) != [])
    expect('a header "Labels" or "Label notes" is not the Label column',
           check(table(rows, ('Claim', 'Labels', 'Proof')), [], repo) != []
           and check(table(rows, ('Claim', 'Label notes', 'Proof')), [], repo) != [])
    expect('an unknown label fails', check(table([live, ['demo', 'passed', 'x']]), [], repo) != [])
    expect('a live row with no link fails', check(table([['round trip', 'live', 'trust me']]), [], repo) != [])
    for name, href in (('another repo', 'https://github.com/x/y/actions/runs/9'),
                       ('a lookalike repo', f'https://github.com/{repo}-fork/actions/runs/9'),
                       ('a non-run page', f'https://github.com/{repo}/pull/9'),
                       ('a run number with letters after it', f'{run}abc'),
                       ('another host', f'https://github.com.evil.example/{repo}/actions/runs/9')):
        expect(f'a live row linking {name} fails',
               check(table([['round trip', 'live', f'<a href="{href}">run</a>']]), [], repo) != [])
    expect('a run URL only as text (not a link) fails', check(table([['round trip', 'live', run]]), [], repo) != [])
    expect('a run URL only in a title attribute fails',
           check(table([['round trip', 'live', f'<a href="https://example.com" title="{run}">logs</a>']]), [], repo)
           != [])
    expect('a run job link counts', check(table([['rt', 'live', f'<a href="{run}/job/5">job</a>']]), [], repo) == [])
    expect('a run link in another column of the row counts',
           check(table([[f'<a href="{run}">rt</a>', 'live', 'see claim']]), [], repo) == [])
    no_live = table(rows[1:])
    expect('src/ change without live or BLOCKED fails', check(no_live, ['src/Boo.App/A.cs'], repo) != [])
    expect('tools/ change without live or BLOCKED fails', check(no_live, ['tools/x/A.cs'], repo) != [])
    expect('docs-only change without live passes', check(no_live, ['docs/a.md'], repo) == [])
    expect('BLOCKED with a reason to the right passes',
           check(table(rows[1:] + [['live run', 'BLOCKED', 'no runner today']]), ['src/a.cs'], repo) == [])
    expect('BLOCKED with a reason in the label cell passes',
           check(table(rows[1:] + [['live run', 'BLOCKED: no runner today', '']]), ['src/a.cs'], repo) == [])
    expect('BLOCKED with only a claim and no reason fails',
           check(table(rows[1:] + [['Real AI round trip', 'BLOCKED', '']]), ['src/a.cs'], repo) != [])
    expect('every Label column is judged',
           check(table([['mock', 'round trip', 'live']], ('Label', 'Claim', 'Label')), [], repo) != [])
    expect('a header-only evidence table fails', check(table([]), [], repo) != [])
    expect('merged cells (colspan) make the evidence table fail',
           check('<table><tr><th colspan="2">Claim</th><th>Label</th></tr><tr><td>rt</td><td>mock</td>'
                 '<td>live</td></tr></table>', [], repo) != [])
    expect('merged cells (rowspan) make the evidence table fail',
           check(table([['<span>rt</span>', 'mock', 'x']]).replace('<td><span>', '<td rowspan="2"><span>'), [], repo)
           != [])
    expect('a table inside the evidence table makes it fail (rows after it cannot hide)',
           check(table([['Claude', 'BLOCKED', 'no key today<table><tr><td></td></tr></table>'],
                        ['real AI round trip', 'live', 'trust me']]), ['src/a.cs'], repo) != [])
    expect('a table inside a table marks the outer one as not a plain grid',
           [plain for _, _, plain in tables(table([['a', 'mock', '<table><tr><td>x</td></tr></table>']]))]
           == [False, True])
    expect('a link with no text does not count',
           check(table([['rt', 'live', f'trust me <a href="{run}"></a>']]), [], repo) != [])
    expect('a link with only whitespace does not count',
           check(table([['rt', 'live', f'<a href="{run}"> </a>']]), [], repo) != [])
    expect('an image link counts', check(table([['rt', 'live', f'<a href="{run}"><img src="x.png"></a>']]), [], repo)
           == [])
    expect('an emoji before the label is fine', check(table([['rt', '\u2705 live', f'<a href="{run}">r</a>']]),
                                                     [], repo) == [])
    expect('a run link that walks out of the run fails',
           check(table([['rt', 'live', f'<a href="{run}/../../../pull/1">r</a>']]), [], repo) != [])
    expect('a run attempt link counts', check(table([['rt', 'live', f'<a href="{run}/attempts/2">r</a>']]), [], repo)
           == [])
    expect('a second table without Label does not disturb the evidence table',
           check(table([['1', '2']], ('a', 'b')) + good, ['src/a.cs'], repo) == [])
    with tempfile.TemporaryDirectory() as tmp:
        event = pathlib.Path(tmp) / 'event.json'
        event.write_text(json.dumps({'pull_request': {'body': 'x', 'base': {'sha': 'x'}, 'head': {'sha': 'y'}}}),
                         encoding='utf-8')
        expect('a body GitHub cannot render fails closed',
               run_event(event, repo, out=lambda *_: None, renderer=_broken_renderer) == 1)
        expect('a PR whose changed files git cannot list fails closed (even with a good table)',
               run_event(event, repo, out=lambda *_: None, renderer=lambda b, r: good) == 1)
    return _finish(results, 'SELF_TEST')


def _broken_renderer(body, repo):
    raise urllib.error.URLError('offline (self-test)')


RENDER_CASES = (
    # (name, Markdown body, should pass) - known tricks; GitHub renders each, the check judges what it shows
    ('a normal table with a live run link', '| Claim | Label | Proof |\n|---|---|---|\n'
     '| rt | live | [run](https://github.com/{repo}/actions/runs/123) |\n', True),
    ('a table inside an HTML comment', 'Nothing tested.\n<!--\n| Claim | Label | Proof |\n|---|---|---|\n'
     '| rt | live | https://github.com/{repo}/actions/runs/123 |\n-->\n', False),
    ('a run link only inside an HTML comment', '| Claim | Label | Proof |\n|---|---|---|\n'
     '| rt | live | <!-- https://github.com/{repo}/actions/runs/123 --> |\n', False),
    ('a line without a pipe does not end the table', '| Claim | Label | Proof |\n|---|---|---|\n'
     '| docs | live | [r](https://github.com/{repo}/actions/runs/123) |\nnote\n| real AI round trip | live | trust me |\n',
     False),
    ('a table inside an attribute', '<div title="\n| Claim | Label | Proof |\n|---|---|---|\n'
     '| rt | live | [r](https://github.com/{repo}/actions/runs/123) |\n"></div>\n', False),
    ('a table inside <pre>', '<pre>\n| Claim | Label | Proof |\n|---|---|---|\n'
     '| rt | live | [r](https://github.com/{repo}/actions/runs/123) |\n</pre>\n', False),
    ('a run URL only as a span title', '| Claim | Label | Proof |\n|---|---|---|\n'
     '| rt | live | <span title="https://github.com/{repo}/actions/runs/123">see logs</span> |\n', False),
    ('a run URL only as a link hover title', '| Claim | Label | Proof |\n|---|---|---|\n'
     '| rt | live | [logs](https://example.com "https://github.com/{repo}/actions/runs/123") |\n', False),
    ('a fence hidden behind a comment marker', '`<!--`\n```\n`-->`\n| Claim | Label | Proof |\n|---|---|---|\n'
     '| rt | live | [r](https://github.com/{repo}/actions/runs/123) |\n```\n', False),
    ('a table indented with a tab (code)', '\t| Claim | Label | Proof |\n\t|---|---|---|\n'
     '\t| rt | live | [r](https://github.com/{repo}/actions/runs/123) |\n', False),
    ('a table inside a 4-backtick fence', '````\n```\n| Claim | Label | Proof |\n|---|---|---|\n'
     '| rt | live | [r](https://github.com/{repo}/actions/runs/123) |\n````\n', False),
    ('a table in a blockquote is a real table', '> | Claim | Label | Proof |\n> |---|---|---|\n'
     '> | rt | live | [r](https://github.com/{repo}/actions/runs/123) |\n', True),
    ('a bare run URL is autolinked', '| Claim | Label | Proof |\n|-|-|-|\n'
     '| rt | live | https://github.com/{repo}/actions/runs/123 |\n', True),
    ('merged cells shift the Label column', '<table><tr><th colspan="2">Claim</th><th>Label</th></tr>\n'
     '<tr><td>real AI round trip</td><td>mock</td><td>live</td></tr></table>\n', False),
    ('a table inside the evidence table', '<table><tr><th>Claim</th><th>Label</th><th>Proof</th></tr>\n'
     '<tr><td>Claude</td><td>BLOCKED</td><td>no key today<table><tr><td></td></tr></table></td></tr>\n'
     '<tr><td>real AI round trip</td><td>live</td><td>trust me</td></tr></table>\n', False),
    ('a run link with no text', '| Claim | Label | Proof |\n|---|---|---|\n'
     '| rt | live | trust me <a href="https://github.com/{repo}/actions/runs/123"></a> |\n', False),
)


def render_test(repo, token):
    """Every known trick, rendered by GitHub itself, must get the expected answer (needs network)."""
    results = []
    for name, body, should_pass in RENDER_CASES:
        try:
            problems = check(render(body.replace('{repo}', repo), repo, token), [], repo)
            results.append((f'{name}: {"passes" if should_pass else "fails"}', (problems == []) == should_pass))
        except (OSError, RuntimeError, urllib.error.URLError) as error:
            results.append((f'{name}: GitHub could not render it ({type(error).__name__})', False))
    return _finish(results, 'RENDER_TEST')


def run_event(event_path, repo, out=print, renderer=None):
    event = json.loads(pathlib.Path(event_path).read_text(encoding='utf-8'))
    pr = event.get('pull_request') or {}
    token = os.environ.get('GITHUB_TOKEN')
    try:
        rendered = (renderer or (lambda b, r: render(b, r, token)))(pr.get('body') or '', repo)
    except (OSError, RuntimeError, urllib.error.URLError) as error:
        return report([f'GitHub could not render the PR body ({type(error).__name__}); failing closed'], out)
    try:
        changed = changed_from_git(pr['base']['sha'], pr['head']['sha'])
    except (KeyError, TypeError, OSError, subprocess.CalledProcessError):
        changed = None
    problems = check(rendered, changed or [], repo)
    if changed is None:
        problems.append('could not list the files this PR changes (git diff base...head failed); failing closed')
    return report(problems, out)


def main(argv):
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    repo = os.environ.get('GITHUB_REPOSITORY', 'vikas53953/bluey')
    token = os.environ.get('GITHUB_TOKEN')
    if argv == ['--self-test']:
        return self_test()
    if argv == ['--render-test']:
        return render_test(repo, token)
    if len(argv) == 2 and argv[0] == '--event':
        return run_event(argv[1], repo)
    if len(argv) in (2, 4) and argv[0] == '--body' and (len(argv) == 2 or argv[2] == '--changed'):
        body = pathlib.Path(argv[1]).read_text(encoding='utf-8')
        changed = pathlib.Path(argv[3]).read_text(encoding='utf-8').splitlines() if len(argv) == 4 else []
        try:
            rendered = render(body, repo, token)
        except (OSError, RuntimeError, urllib.error.URLError) as error:
            return report([f'GitHub could not render the body ({type(error).__name__}); failing closed'])
        return report(check(rendered, changed, repo))
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
