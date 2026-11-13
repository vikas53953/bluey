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
        self.rows, self.row, self.cell, self.plain, self.shape = [], None, None, True, []


# The only table shape a Markdown pipe table renders to on GitHub: one header row of <th> in <thead>, then (optionally)
# body rows of <td> in one <tbody>; <table role="table">, cells may carry align. A table written in raw HTML that has
# any other shape (rows before the header, a footer, a caption, merged cells, a table inside it, extra attributes) can
# be drawn differently from the order it is written, so it is not accepted anywhere on the page.
PIPE_TABLE_SHAPE = re.compile(r'^<thead><tr>(?:<th></th>)+</tr></thead>(?:<tbody>(?:<tr>(?:<td></td>)+</tr>)+</tbody>)?$')
STRUCTURE = {'thead', 'tbody', 'tfoot', 'tr', 'th', 'td', 'caption', 'colgroup', 'col'}
ALLOWED_ATTRS = {'table': {'role'}, 'th': {'align'}, 'td': {'align'}}
# What Markdown itself puts inside a pipe-table cell on GitHub (links, autolinked PRs and commits, bold, italic,
# strikethrough, code, emoji, line breaks, images). Raw HTML that a browser does not draw as written (<details>, <rp>,
# <ruby>, <span>, ...) could make the check read different text or links than the reader sees, so it fails the table.
CELL_TAGS = {'a', 'strong', 'em', 'del', 'code', 'tt', 'br', 'g-emoji', 'img'}


class _Tables(html.parser.HTMLParser):
    """Every table in rendered HTML. Open tables are kept on a stack; each records its structure (checked against
    PIPE_TABLE_SHAPE) and its cells as (text, hrefs of links whose text has a letter or digit)."""

    VOID = {'br', 'img', 'hr', 'input', 'meta', 'link', 'wbr', 'source', 'col', 'area', 'base', 'embed', 'track'}

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.tables, self._open, self._tags, self._links = [], [], [], []

    def _top(self):
        return self._open[-1] if self._open else None

    def handle_starttag(self, tag, attrs):
        names = dict(attrs)
        top = self._top()
        if top is not None and top.cell is not None and tag not in CELL_TAGS and tag != 'table':
            top.plain = False                             # raw HTML inside a cell
        if top is not None and (tag in STRUCTURE or tag == 'table'):
            if set(names) - ALLOWED_ATTRS.get(tag, set()):
                top.plain = False
        if tag in self.VOID:
            if tag == 'col' and top is not None:
                top.plain = False
            if tag == 'br' and top is not None and top.cell is not None:
                top.cell[0].append(' ')
            return
        self._tags.append(tag)
        if tag == 'table':
            if top is not None:
                top.plain = False                         # a table inside a table
            table = _Table()
            if set(names) - ALLOWED_ATTRS['table']:
                table.plain = False
            self.tables.append(table)
            self._open.append(table)
            return
        if top is None:
            return
        if tag in STRUCTURE:
            top.shape.append(f'<{tag}>')
        if tag == 'tr':
            top.row = []
            top.rows.append(top.row)
        elif tag in ('td', 'th') and top.row is not None:
            top.cell = ([], [])
            top.row.append(top.cell)
        elif tag == 'a' and top.cell is not None and names.get('href'):
            self._links.append([names['href'], [], top.cell])

    def handle_endtag(self, tag):
        if tag not in self._tags:
            return                                         # a stray closing tag closes nothing
        while self._tags:
            open_tag = self._tags.pop()
            top = self._top()
            if open_tag == 'table':
                if self._open:
                    self._open.pop()
            elif top is not None:
                if open_tag in STRUCTURE:
                    top.shape.append(f'</{open_tag}>')
                if open_tag == 'a' and self._links:
                    href, text, cell = self._links.pop()
                    if re.search(r'[A-Za-z0-9]', ''.join(text)):   # a link a reader can see and read
                        cell[1].append(href)
                elif open_tag in ('td', 'th'):
                    top.cell = None
                elif open_tag == 'tr':
                    top.row = None
            if open_tag == tag:
                break

    def handle_data(self, data):
        top = self._top()
        if top is not None and top.cell is not None:
            if 'del' in self._tags:
                return                                     # struck-out text is withdrawn: a reader does not read it
            top.cell[0].append(data)
            for link in self._links:
                link[1].append(data)


def tables(rendered_html):
    """[(header cell texts, [(row number, [(text, hrefs)])], pipe_shaped)] for every table on the rendered page."""
    parser = _Tables()
    parser.feed(rendered_html or '')
    parser.close()
    out = []
    for table in parser.tables:
        rows = [[(' '.join(''.join(text).split()), hrefs) for text, hrefs in r] for r in table.rows]
        shaped = table.plain and bool(PIPE_TABLE_SHAPE.match(''.join(table.shape)))
        if not rows:
            out.append(([], [], False))
            continue
        out.append(([text for text, _ in rows[0]], list(enumerate(rows[1:], 1)), bool(shaped)))
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
    for k, (header, rows, shaped) in enumerate(tables(rendered_html), 1):
        if not shaped:
            problems.append(f'table {k} is not a plain Markdown pipe table (raw HTML table or HTML inside a cell, '
                            'footnotes or math in a cell, merged cells, a table inside it, rows before the header or a '
                            'footer): write every table as a Markdown pipe table with plain text, links, bold, italic, '
                            'strikethrough and code in its cells')
            continue
        cols = [c for c, h in enumerate(header) if label_of(h) == 'label'
                and len([w for w in h.split() if re.search(r'[A-Za-z]', w)]) == 1]
        if cols:
            evidence.append((k, cols, rows))
            continue
        for n, cells in rows:
            if any(label_of(text) == 'live' for text, _ in cells):
                problems.append(f'table {k}, row {n}: a "live" cell in a table without a Label column (is the header '
                                'misspelled?); put claims in the evidence table')
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
        return f'<table role="table"><thead><tr>{head}</tr></thead>' + (f'<tbody>{body}</tbody>' if rows else '') \
            + '</table>'

    live = ['round trip', '<strong>live</strong> (OpenAI)', f'<a href="{run}">run</a>']
    rows = [live, ['demo', 'mock', f'<a href="{run}">r</a>'], ['Claude', 'UNVERIFIED', 'not tested'],
            ['review', 'review', 'REVIEW PASS']]
    good = '<p>Intro</p>' + table(rows)
    expect('a good table passes', check(good, ['src/a.cs'], repo) == [])
    expect('no table fails', check('<p>Just words.</p>', [], repo) != [])
    expect('an empty body fails', check('', [], repo) != [] and check(None, [], repo) != [])
    expect('a table without a Label column fails', check(table([['1', '2']], ('a', 'b')), [], repo) != [])
    expect('a header-only table (no body rows) is a pipe table', tables(table([]))[0][2] is True)
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
    expect('a table inside a table marks the outer one as not a pipe table',
           [shaped for _, _, shaped in tables(table([['a', 'mock', '<table><tr><td>x</td></tr></table>']]))][0]
           is False)
    expect('a link with no text does not count',
           check(table([['rt', 'live', f'trust me <a href="{run}"></a>']]), [], repo) != [])
    expect('a link with only whitespace does not count',
           check(table([['rt', 'live', f'<a href="{run}"> </a>']]), [], repo) != [])
    expect('a table inside an open <details> is judged normally',
           check(f'<details open><summary>Evidence</summary>{good}</details>', ['src/a.cs'], repo) == [])
    expect('a link folded inside <details> in the cell does not count',
           check(table([['rt', 'live', f'trust me <details><summary></summary><a href="{run}">r</a></details>']]),
                 [], repo) != [])
    expect('an image-only link does not count (it may show nothing)',
           check(table([['rt', 'live', f'<a href="{run}"><img src="x.png"></a>']]), [], repo) != [])
    for name, invisible in (('a zero-width space', '\u200b'), ('a soft hyphen', '\u00ad'), ('a Braille blank', '\u2800')):
        expect(f'a link whose only text is {name} does not count',
               check(table([['rt', 'live', f'trust me <a href="{run}">{invisible}</a>']]), [], repo) != [])
    expect('a header row after the body rows (drawn first by a browser) fails',
           check('<table role="table"><tbody><tr><td>a</td><td>b</td><td>Label</td></tr></tbody>'
                 '<thead><tr><th>Claim</th><th>Label</th><th>mock</th></tr></thead></table>', [], repo) != [])
    expect('a table footer fails',
           check(good.replace('</table>', '<tfoot><tr><td>x</td><td>mock</td><td>y</td></tr></tfoot></table>'),
                 ['src/a.cs'], repo) != [])
    expect('a blank first row before the header fails',
           check(good + '<table><tr><td></td><td></td><td></td></tr><tr><th>Claim</th><th>Label</th><th>Proof</th>'
                 '</tr><tr><td>rt</td><td>live</td><td>trust me</td></tr></table>', ['src/a.cs'], repo) != [])
    expect('a look-alike Label header (Cyrillic a) with a live row fails',
           check(good + table([['real AI round trip', 'live', 'trust me']], ('Claim', 'L\u0430bel', 'Proof')),
                 ['src/a.cs'], repo) != [])
    expect('an extra attribute on a cell fails', check(good.replace('<td>demo</td>', '<td title="x">demo</td>'),
                                                       ['src/a.cs'], repo) != [])
    expect('aligned columns (align attribute) pass',
           check(good.replace('<th>', '<th align="center">').replace('<td>', '<td align="left">'), ['src/a.cs'], repo)
           == [])
    expect('a cell starting with "live" in a table without a Label column fails',
           check(good + table([['real AI round trip', 'live (OpenAI)']], ('Claim', 'L\u0430bel')), ['src/a.cs'], repo)
           != [])
    expect('struck-out text is not read: "~~mock~~ live" is a live row and needs a run link',
           check(table([['rt', '<del>mock</del> live', 'trust me']]), [], repo) != [])
    expect('struck-out text is not read in a table without a Label column either',
           check(good + table([['rt', '<del>x</del> live', 'trust me']], ('Claim', 'Lable')), ['src/a.cs'], repo) != [])
    expect('a struck-out run link does not count',
           check(table([['rt', 'live', f'<del><a href="{run}">run</a></del>']]), [], repo) != [])
    expect('"~~live~~ UNVERIFIED" reads as UNVERIFIED',
           check(table([['rt', '<del>live</del> UNVERIFIED', 'not tested']]), [], repo) == [])
    expect('a table without a Label column that mentions "the live run" passes',
           check(good + table([['the live run', 'x']], ('a', 'b')), ['src/a.cs'], repo) == [])
    for name, cell in (('a folded <details> before the label', '<details><summary></summary>mock</details> live'),
                       ('ruby <rp> text before the label', '<ruby><rp>mock</rp></ruby> live'),
                       ('a <span> in a cell', '<span>live</span>')):
        expect(f'raw HTML in a cell fails the table: {name}',
               check(table([['rt', cell, f'<a href="{run}">r</a>']]), [], repo) != [])
    expect('Markdown cell content passes (code, emoji, bold, italic, strikethrough, br, commit link)',
           check(table([['<a href="https://github.com/x/y/commit/1"><tt>1</tt></a> <code>c</code>',
                         '<g-emoji>✅</g-emoji> <strong>live</strong> <em>a</em> <del>b</del>',
                         f'<br><a href="{run}">r</a>']]), ['src/a.cs'], repo) == [])
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
    ('a run link whose text is a zero-width space', '| Claim | Label | Proof |\n|---|---|---|\n'
     '| rt | live | trust me [\u200b](https://github.com/{repo}/actions/runs/123) |\n', False),
    ('an image-only run link', '| Claim | Label | Proof |\n|---|---|---|\n'
     '| rt | live | trust me <a href="https://github.com/{repo}/actions/runs/123"><img></a> |\n', False),
    ('a header written after the body rows', '<table><tbody><tr><td>a</td><td>b</td><td>Label</td></tr>'
     '<tr><td>real AI round trip</td><td>live</td><td>BLOCKED nothing</td></tr></tbody><thead><tr><th>Claim</th>'
     '<th>Label</th><th>mock</th></tr></thead></table>\n', False),
    ('a look-alike Label header', '| Claim | Label | Proof |\n|---|---|---|\n| live run | BLOCKED | no runner |\n\n'
     '| Claim | L\u0430bel | Proof |\n|---|---|---|\n| real AI round trip | live | trust me |\n', False),
    ('a folded details before the label', '| Claim | Label | Proof |\n|---|---|---|\n'
     '| real AI round trip | <details><summary></summary>mock</details> live | trust me |\n'
     '| live run | BLOCKED | no runner today |\n', False),
    ('a run link hidden in <rp>', '| Claim | Label | Proof |\n|---|---|---|\n'
     '| real AI round trip | live | trust me <ruby>x<rp><a href="https://github.com/{repo}/actions/runs/123">run</a>'
     '</rp><rt>y</rt></ruby> |\n', False),
    ('a look-alike Label header with "live (OpenAI)"', '| Claim | Label | Proof |\n|---|---|---|\n'
     '| live run | BLOCKED | no runner today |\n\n| Claim | L\u0430bel | Proof |\n|---|---|---|\n'
     '| real AI round trip | live (OpenAI) | trust me |\n', False),
    ('struck-out mock before live', '| Claim | Label | Proof |\n|---|---|---|\n| live run | BLOCKED | no runner today |\n'
     '| real AI round trip | ~~mock~~ live | trust me |\n', False),
    ('struck-out word before live under a misspelled Label', '| Claim | Label | Proof |\n|---|---|---|\n'
     '| live run | BLOCKED | no runner today |\n\n| Claim | Lable | Proof |\n|---|---|---|\n'
     '| real AI round trip | ~~x~~ live | trust me |\n', False),
    ('a struck-out run link', '| Claim | Label | Proof |\n|---|---|---|\n'
     '| rt | live | ~~[run](https://github.com/{repo}/actions/runs/123)~~ |\n', False),
    ('commit and PR autolinks, emoji, code in cells pass', '| Claim | Label | Proof |\n|---|---|---|\n'
     '| #1 and 342b93d72418efd7520219fb99ca27fa9b2f5904 `x` | :white_check_mark: **live** | '
     '[run](https://github.com/{repo}/actions/runs/1) <br> ~~old~~ |\n', True),
    ('aligned columns and a job link pass', '| Claim | Label | Proof |\n|:--|:-:|--:|\n'
     '| rt | \u2705 **live** (OpenAI) | [job](https://github.com/{repo}/actions/runs/1/job/2) |\n', True),
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
