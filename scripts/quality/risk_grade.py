"""BOO-RISK-001: every pull request body declares a risk grade (docs/quality/RULES.md). INFORMATIONAL: for routing only.

  python -I scripts/quality/risk_grade.py --self-test        rules, on HTML samples shaped like GitHub's (offline)
  python -I scripts/quality/risk_grade.py --render-test      known tricks, rendered by GitHub itself (network)
  python -I scripts/quality/risk_grade.py --event "$GITHUB_EVENT_PATH"   (in the risk-grade workflow)
  python -I scripts/quality/risk_grade.py --body FILE [--changed FILE] [--base BRANCH]
                                                              local check of a draft PR body (network); --changed is a
                                                              file with one changed path per line (the floor check)

The line, set by the builder by blast radius (what could break if the PR is wrong):

  Risk: <low | medium | high> — <one-line reason>          e.g.  Risk: medium — new CI check and script

  low     docs, tests, features behind a flag that stays off
  medium  build scripts, CI, configuration
  high    data loss, secrets, sign-in/keys, screen capture or privacy, a flag turned on, anything on the release path
          (lane/face to main, release, updates)

Grammar (after runs of spaces are folded to one, leading and trailing spaces dropped):
  line    = "Risk: " grade " " dash " " reason
  grade   = "low" | "medium" | "high"                 (exactly these words, lower case)
  dash    = "—" | "–" | "-" | "--"                    (an em dash as written above; a typed hyphen is accepted too)
  reason  = 3 to 200 characters with at least one letter
"Risk:" is exact (that word, that case, then one space).

PR bodies are not read as text. GitHub renders the body (POST /markdown, gfm mode, as pr_evidence.py does) and the check
reads the rendered HTML, so a line a reader cannot see does not count (a code block, inline code, an HTML comment, a
<details> fold). If GitHub cannot render the body, the check fails closed. The rule on the rendered page:
  1. Exactly one visible line starts with "Risk:" (any case; a line in a code block or starting in inline code does not
     count). Anywhere on the page; its own line (a blank line or a line break before it).
  2. It sits directly in a top-level paragraph (not a heading, list, quote, table or fold).
  3. Its text matches the grammar above.
  4. Floor (when the changed paths are known: always in CI): the grade is not lower than the floor computed from the
     paths the PR changes and its base branch (FLOOR below, the one place the table is kept). A PR may declare higher
     than the floor, never lower. The floor cannot see whether a flag is on: that, and anything else the table misses,
     is the builder's call.

The grade NEVER relaxes any gate: every other check, review and live run applies the same to a low PR as to a high one.
It only routes: a high PR gets a second, independent Codex review before the codex-verified stamp (HANDOFF section 5).
Routers read the grade from this script's output line (RISK_GRADE_OK grade=...), never by searching the body text,
because a search could find a Risk line that a reader cannot see.
"""
import fnmatch
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

RULE = 'BOO-RISK-001'
API = 'https://api.github.com/markdown'
GRADES = ('low', 'medium', 'high')
EXAMPLE = 'Risk: medium — new CI check and script; informational only, relaxes no gate'
NOTICE = (f'{RULE} is informational and for routing only: it never relaxes any gate (every other check, review and '
          'live run still applies in full).')
ROUTE_HIGH = ('Routing: high risk, so this PR gets a second, independent Codex review before the codex-verified stamp '
              '(HANDOFF section 5, rule 4).')
LINE = re.compile(r'^Risk: (?P<grade>\S+) (?P<dash>—|–|-|--) (?P<reason>.+)$')
BLOCK = {'p', 'div', 'pre', 'table', 'thead', 'tbody', 'tfoot', 'tr', 'td', 'th', 'ul', 'ol', 'li', 'blockquote',
         'details', 'summary', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'hr', 'dl', 'dt', 'dd', 'section', 'article',
         'figure', 'figcaption', 'markdown-accessiblity-table', 'caption'}
VOID = {'br', 'img', 'hr', 'input', 'meta', 'link', 'wbr', 'source', 'col', 'area', 'base', 'embed', 'track'}

# The floor table: the only place it is kept (docs/quality/RULES.md, BOO-RISK-001, describes it).
# Patterns are matched with fnmatch on the repository path (forward slashes, case-sensitive), where "*" also matches "/".
# The floor of a PR is the highest tier any changed path reaches. A path that matches no HIGH pattern and every LOW
# pattern check fails gets MEDIUM (an unknown path is never low). A PR into main is HIGH (the release path).
HIGH_BASES = ('main',)
HIGH = (
    ('.github/workflows/release.yml', 'the release workflow'),
    ('src/*Update*', 'the in-app updater (release path)'),
    ('src/Boo.Storage/*', "the user's saved data and the key store (data loss, secrets)"),
    ('src/Boo.Providers/*', 'the AI clients: they carry the key and the screen picture off the PC (secrets, privacy)'),
    ('src/Boo.Core/AiProvider.cs', 'provider keys (secrets)'),
    ('src/*Capture*', 'screen capture (privacy)'),
    ('src/Boo.Windows/LowLevelKeyboardHook.cs', 'the keyboard hook (privacy)'),
    ('src/*Undo*', 'undo of file actions (data loss)'),
    ('src/Boo.Core/ActionPolicy.cs', 'what Boo is permitted to do (permissions)'),
    ('tools/Boo.CaptureProof/*', 'the proof that capture never sees other windows (privacy)'),
    ('tools/Boo.CaptureAffinity/*', 'the capture-exclusion proof (privacy)'),
    ('scripts/Enter-BooApiKey.ps1', 'key entry (secrets)'),
    ('scripts/Import-LocalCredential.ps1', 'key import (secrets)'),
    ('scripts/quality/key_scan.py', 'the key scan before uploads (secrets)'),
    ('scripts/ci/check_runners.py', "the guard that keeps CI off the owner's PC (privacy)"),
    ('scripts/ci/Record-Desktop.ps1', 'desktop recording (privacy)'),
    ('*.pfx', 'a signing certificate (secrets)'),
    ('*.snk', 'a signing key (secrets)'),
)
LOW = ('docs/*', 'tests/*', 'specs/*', '*.md')
LOW_EXCEPT = (('docs/verification/test-oracle.json', 'the frozen test oracle hashes (a gate)'),)


def fold(text):
    return ' '.join((text or '').split())


def path_floor(path):
    """(grade, why) for one changed path."""
    for pattern, why in HIGH:
        if fnmatch.fnmatchcase(path, pattern):
            return 'high', why
    for pattern, why in LOW_EXCEPT:
        if fnmatch.fnmatchcase(path, pattern):
            return 'medium', why
    if any(fnmatch.fnmatchcase(path, pattern) for pattern in LOW):
        return 'low', 'docs, tests or Markdown'
    return 'medium', 'build scripts, CI, config, app code or another path that is not docs or tests'


def floor(paths, base_ref=None):
    """(grade, path, why): the highest tier any changed path or the base branch reaches."""
    return ('low', None, 'RED: floor not written yet')
    best = ('low', None, 'only docs, tests or Markdown changed')
    if base_ref in HIGH_BASES:
        best = ('high', f'(base branch {base_ref})', 'a PR into main is on the release path')
    for path in paths:
        grade, why = path_floor(path)
        if GRADES.index(grade) > GRADES.index(best[0]):
            best = (grade, path, why)
    return best


def grammar(line):
    """(problems, grade) for one Risk line's text."""
    text = fold(line)
    match = LINE.match(text)
    if not match:
        return [f'the risk line "{text[:120]}" does not have the shape "Risk: <low|medium|high> — <one-line reason>" '
                '(exact word "Risk:" then one space, one grade word, a dash with a space on each side, the reason)'], None
    grade = match['grade']
    problems = []
    if grade not in GRADES:
        problems.append(f'the grade "{grade[:20]}" must be exactly one of low, medium, high (lower case)')
    reason = fold(match['reason'])
    if len(reason) < 3 or len(reason) > 200 or not re.search(r'[^\W\d_]', reason):
        problems.append(f'the reason after the dash must be 3 to 200 characters with a letter (it was "{reason[:40]}")')
    return problems, (grade if not problems else None)


class _Lines(html.parser.HTMLParser):
    """The visual lines of rendered HTML: a new line at every block boundary and every <br>. Each line records its
    text, the block elements around it, and whether its first text is code (inline code or a code block)."""

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.lines, self._stack, self._line = [], [], None

    def _close_line(self):
        if self._line is not None and fold(''.join(self._line['text'])):
            self._line['text'] = fold(''.join(self._line['text']))
            self.lines.append(self._line)
        self._line = None

    def handle_starttag(self, tag, attrs):
        if tag in BLOCK or tag == 'br':
            self._close_line()
        if tag not in VOID:
            self._stack.append(tag)

    def handle_endtag(self, tag):
        if tag not in self._stack:
            return
        while self._stack:
            open_tag = self._stack.pop()
            if open_tag in BLOCK:
                self._close_line()
            if open_tag == tag:
                break

    def handle_data(self, data):
        if not data.strip() and self._line is None:
            return
        if self._line is None:
            self._line = {'text': [], 'blocks': [t for t in self._stack if t in BLOCK], 'lead_code': None}
        if self._line['lead_code'] is None and data.strip():
            self._line['lead_code'] = any(t in ('pre', 'code') for t in self._stack)
        self._line['text'].append(data)

    def close(self):
        super().close()
        self._close_line()


def lines(rendered_html):
    parser = _Lines()
    parser.feed(rendered_html or '')
    parser.close()
    return parser.lines


def check(rendered_html, paths=None, base_ref=None):
    """(problems, grade, floor or None) for the rendered PR body; paths None means the floor is not checked."""
    return [], 'low', None  # RED: rules not written yet (the self-test must fail)
    visible = [ln for ln in lines(rendered_html) if not ln['lead_code'] and 'pre' not in ln['blocks']]
    found = [ln for ln in visible if ln['text'][:5].lower() == 'risk:']
    if not found:
        return [f'the PR body has no risk line a reader can see: add the line "{EXAMPLE}" (your own grade and reason), '
                'on a line of its own, outside code blocks, HTML comments and folds'], None, None
    if len(found) > 1:
        return [f'the PR body has {len(found)} visible lines starting with "Risk:"; keep exactly one (put examples in a '
                'code block)'], None, None
    line = found[0]
    problems = []
    if line['blocks'] != ['p']:
        problems.append('the risk line must be a plain line in a top-level paragraph (not in a heading, list, quote, '
                        'table or fold): put it on its own line with a blank line above it')
    grammar_problems, grade = grammar(line['text'])
    problems += grammar_problems
    if problems or paths is None:
        return problems, (None if problems else grade), None
    least = floor(paths, base_ref)
    if GRADES.index(grade) < GRADES.index(least[0]):
        return [f'the declared grade "{grade}" is lower than this PR\'s floor "{least[0]}": {least[1]} ({least[2]}). '
                'Declare at least the floor (a PR may declare higher, never lower)'], None, least
    return [], grade, least


def changed_from_git(base, head, cwd=None):
    """Files the PR changes (merge base of base and head, to head). Raises when git cannot tell (fail closed)."""
    out = subprocess.run(['git', '-c', 'core.quotePath=false', 'diff', '--name-only', '--no-renames', '-z',
                          f'{base}...{head}'], cwd=cwd, capture_output=True, check=True)
    return [f for f in out.stdout.decode('utf-8', 'replace').split('\0') if f]


def render(body, repo, token=None):
    """The body as GitHub renders it on the PR page. Raises on any failure (the caller fails closed)."""
    data = json.dumps({'text': body or '', 'mode': 'gfm', 'context': repo}).encode('utf-8')
    request = urllib.request.Request(API, data=data, method='POST', headers={
        'Accept': 'application/vnd.github+json', 'Content-Type': 'application/json',
        'User-Agent': 'risk-grade-check', 'X-GitHub-Api-Version': '2022-11-28'})
    if token:
        request.add_header('Authorization', f'Bearer {token}')
    with urllib.request.urlopen(request, timeout=30) as response:
        if response.status != 200:
            raise RuntimeError(f'GitHub markdown API answered {response.status}')
        return response.read().decode('utf-8')


def report(problems, grade=None, least=None, out=print):
    for p in problems:
        out(f'::error::{RULE} {p}')
    if problems:
        out(f'RISK_GRADE_FAILED {len(problems)} problem(s). Fix: docs/quality/RULES.md, {RULE}')
        out(NOTICE)
        return 1
    if least is None:
        out(f'RISK_GRADE_OK grade={grade} floor=NOT-CHECKED (no changed paths given; CI checks the floor)')
    else:
        out(f'RISK_GRADE_OK grade={grade} floor={least[0]} (set by {least[1]}: {least[2]})')
    out(NOTICE)
    if grade == 'high':
        out(ROUTE_HIGH)
    return 0


def _finish(results, name):
    failed = [n for n, ok in results if not ok]
    for n in failed:
        print(f'  FAILED: {n}')
    print(f'RISK_GRADE_{name}_{"FAILED" if failed else "PASSED"} {len(results) - len(failed)} of {len(results)}')
    return 1 if failed else 0


def _p(text):
    return f'<p dir="auto">{text}</p>\n'


def self_test():
    """The rules on HTML shaped like GitHub's rendering (taken from real renders), the floor table, and event mode."""
    results = []

    def expect(name, ok):
        results.append((name, bool(ok)))

    def ok(rendered, paths=None, base=None):
        return check(rendered, paths, base)[0] == []

    intro = _p('Intro.')
    for grade in GRADES:
        line = f'Risk: {grade} — a reason here'
        expect(f'a valid {grade} line passes with its grade', check(intro + _p(line)) == ([], grade, None))
    expect('the mandated example passes', ok(intro + _p(EXAMPLE)))
    expect('a typed hyphen as the dash passes', ok(intro + _p('Risk: low - docs only')))
    expect('a double hyphen or en dash passes', ok(_p('Risk: low -- docs only')) and ok(_p('Risk: low – docs')))
    expect('a bold "Risk:" label passes', ok(intro + _p('<strong>Risk:</strong> low — x docs')))
    expect('inline code inside the reason passes',
           ok(intro + _p('Risk: medium — edits <code class="notranslate">scripts/x.py</code> only')))
    expect('a risk line on a soft-break line inside a paragraph passes',
           ok(_p('Intro line<br>\nRisk: high — touches capture<br>\nNext line')))
    expect('a risk line anywhere on the page (not last) passes', ok(_p('Risk: low — docs') + _p('More after.')))
    expect('extra spaces are folded', ok(_p('Risk:  low   —   docs  only')))
    expect('a missing risk line fails', not ok(intro))
    expect('an empty body fails', not ok('') and not ok(None))
    expect('a risk line in a code block fails',
           not ok(intro + '<pre class="notranslate"><code class="notranslate">Risk: low — x\n</code></pre>'))
    expect('a risk line in inline code fails',
           not ok(intro + _p('<code class="notranslate">Risk: low — x</code>')))
    expect('a risk line in an HTML comment fails (GitHub drops it)', not ok(intro + '\n'))
    expect('a risk line in a <details> fold fails',
           not ok(intro + '<details><summary>s</summary>\n' + _p('Risk: low — x docs') + '</details>'))
    expect('a risk line as a list item fails', not ok(intro + '<ul dir="auto">\n<li>Risk: low — x docs</li>\n</ul>'))
    expect('a risk line as a heading fails', not ok('<h2 dir="auto">Risk: low — x docs</h2>'))
    expect('a risk line in a blockquote fails', not ok('<blockquote>\n' + _p('Risk: low — x docs') + '</blockquote>'))
    expect('a risk line in a table cell fails',
           not ok('<markdown-accessiblity-table><table role="table"><thead><tr><th>Risk: low — docs</th></tr>'
                  '</thead></table></markdown-accessiblity-table>'))
    expect('two risk lines fail', not ok(_p('Risk: low — docs') + _p('Risk: high — capture')))
    expect('two risk lines in one paragraph fail', not ok(_p('Risk: low — docs<br>\nRisk: low — docs')))
    expect('an example in a code block next to the real line passes',
           ok('<pre class="notranslate"><code class="notranslate">Risk: low — x\n</code></pre>'
              + _p('Risk: medium — CI only')))
    for name, line in (
            ('a bad grade word', 'Risk: moderate — CI only'),
            ('an upper-case grade', 'Risk: Low — docs'),
            ('a lower-case keyword', 'risk: low — docs'),
            ('an upper-case keyword', 'RISK: low — docs'),
            ('the unfilled template (GitHub strips the <...> as a tag)', 'Risk: low | medium | high — '),
            ('an empty reason', 'Risk: low —'),
            ('a reason of only spaces', 'Risk: low —    '),
            ('a reason with no letter', 'Risk: low — 123'),
            ('a reason of 201 characters', 'Risk: low — ' + 'x' * 201),
            ('no dash', 'Risk: low docs only'),
            ('no space before the dash', 'Risk: low— docs only'),
            ('no grade', 'Risk: — docs only'),
            ('a colon instead of a dash', 'Risk: low: docs only'),
            ('two grades', 'Risk: low medium — docs')):
        expect(f'{name} fails', not ok(_p(line)))
    # The floor table.
    expect('floor: only docs/tests/Markdown is low',
           floor(['docs/a.md', 'tests/Boo.Core.Tests/CaptureXTests.cs', 'AGENTS.md', 'specs/x.txt'])[0] == 'low')
    expect('floor: no changed paths is low', floor([])[0] == 'low')
    expect('floor: a workflow is medium', floor(['docs/a.md', '.github/workflows/ci.yml'])[:2] ==
           ('medium', '.github/workflows/ci.yml'))
    for path in ('scripts/quality/x.py', 'src/Boo.App/Boo.App.csproj', 'Directory.Build.props', 'global.json',
                 'src/Boo.App/MainWindow.Talk.cs', 'tools/Boo.QaMockAi/Program.cs', 'docs/verification/test-oracle.json',
                 '.claude/skills/verify-boo/scripts/Test-FeatureMap.ps1', 'some-new-file.txt'):
        expect(f'floor: {path} is medium', path_floor(path)[0] == 'medium')
    for path in ('.github/workflows/release.yml', 'src/Boo.App/UpdateService.cs', 'src/Boo.Core/Updates.cs',
                 'src/Boo.Storage/CredentialStore.cs', 'src/Boo.Providers/ResponsesClient.cs',
                 'src/Boo.Windows/ScreenCapture.cs', 'src/Boo.Core/CaptureExclusionPolicy.cs',
                 'src/Boo.Windows/LowLevelKeyboardHook.cs', 'src/Boo.Core/FileUndoJournal.cs',
                 'src/Boo.Core/ActionPolicy.cs', 'tools/Boo.CaptureProof/OverlayCaptureProofTests.cs',
                 'scripts/Enter-BooApiKey.ps1', 'scripts/quality/key_scan.py', 'scripts/ci/check_runners.py',
                 'certs/boo.pfx'):
        expect(f'floor: {path} is high', path_floor(path)[0] == 'high')
    expect('floor: a PR into main is high', floor(['docs/a.md'], 'main')[0] == 'high')
    expect('floor: a PR into lane/face with docs only is low', floor(['docs/a.md'], 'lane/face')[0] == 'low')
    workflow = ['.github/workflows/risk-grade.yml']
    expect('floor: low on a workflow change fails', not ok(_p('Risk: low — just CI'), workflow))
    expect('floor: medium on a workflow change passes', ok(_p('Risk: medium — CI only'), workflow))
    expect('floor: high on a docs-only change passes (higher than the floor is allowed)',
           ok(_p('Risk: high — cautious'), ['docs/a.md']))
    expect('floor: medium on a capture change fails', not ok(_p('Risk: medium — small'), ['src/X/ScreenCapture.cs']))
    expect('floor: medium on a PR into main fails', not ok(_p('Risk: medium — sync'), ['docs/a.md'], 'main'))
    problems = check(_p('Risk: low — just CI'), workflow)[0]
    expect('floor: the failure names the path that set the floor',
           problems and '.github/workflows/risk-grade.yml' in problems[0])
    # Output: the machine line, the notice, the routing line.
    said = []
    expect('report: OK names grade and floor, says it relaxes no gate, routes high',
           report([], 'high', ('medium', 'x.yml', 'CI'), out=said.append) == 0
           and said[0] == 'RISK_GRADE_OK grade=high floor=medium (set by x.yml: CI)'
           and any('never relaxes any gate' in s for s in said) and any('second, independent Codex' in s for s in said))
    said = []
    expect('report: without changed paths it says the floor was NOT checked',
           report([], 'low', None, out=said.append) == 0 and 'floor=NOT-CHECKED' in said[0]
           and not any('Codex' in s for s in said))
    said = []
    expect('report: a failure says it relaxes no gate and exits 1',
           report(['x'], out=said.append) == 1 and any('never relaxes any gate' in s for s in said))
    # Event mode (a real throwaway git repository for the changed paths).
    good = _p('Intro.') + _p('Risk: medium — CI only')
    expect('event: a good body over a workflow change passes', _event_case(lambda b, r: good) == 0)
    expect('event: low over a workflow change fails',
           _event_case(lambda b, r: _p('Risk: low — CI only')) == 1)
    expect('event: a PR into main with medium fails', _event_case(lambda b, r: good, base_ref='main') == 1)
    expect('event: fails closed when GitHub cannot render', _event_case(_broken_renderer) == 1)
    expect('event: fails closed when git cannot list the changed paths',
           _event_case(lambda b, r: good, break_git=True) == 1)
    expect('event: a PR with no body fails', _event_case(lambda b, r: good, body=None) == 1)
    return _finish(results, 'SELF_TEST')


def _broken_renderer(body, repo):
    raise urllib.error.URLError('offline')


def _git(repo, *args):
    env = dict(os.environ, GIT_AUTHOR_NAME='t', GIT_AUTHOR_EMAIL='t@example.com', GIT_COMMITTER_NAME='t',
               GIT_COMMITTER_EMAIL='t@example.com', GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull)
    out = subprocess.run(['git', '-c', 'commit.gpgsign=false', *args], cwd=repo, env=env, capture_output=True,
                         check=True)
    return out.stdout.decode().strip()


def _event_case(renderer, body='Intro.\n\nRisk: medium — CI only\n', base_ref='lane/face', break_git=False):
    with tempfile.TemporaryDirectory() as tmp:
        repo = pathlib.Path(tmp) / 'repo'
        repo.mkdir()
        _git(repo, 'init', '-q')
        (repo / 'README.md').write_text('x\n', encoding='utf-8')
        _git(repo, 'add', '-A')
        _git(repo, 'commit', '-q', '-m', 'base')
        base = _git(repo, 'rev-parse', 'HEAD')
        (repo / '.github' / 'workflows').mkdir(parents=True)
        (repo / '.github' / 'workflows' / 'x.yml').write_text('x\n', encoding='utf-8')
        _git(repo, 'add', '-A')
        _git(repo, 'commit', '-q', '-m', 'head')
        head = 'f' * 40 if break_git else _git(repo, 'rev-parse', 'HEAD')
        event = pathlib.Path(tmp) / 'event.json'
        event.write_text(json.dumps({'pull_request': {'body': body, 'base': {'sha': base, 'ref': base_ref},
                                                      'head': {'sha': head}}}), encoding='utf-8')
        return run_event(event, 'owner/repo', out=lambda *_: None, renderer=renderer, cwd=repo)


# Markdown bodies rendered by GitHub itself (--render-test): the tricks a plain-text check would miss.
D = '—'
RENDER_CASES = (
    ('a plain risk line', f'Intro.\n\nRisk: medium {D} new CI check\n', True),
    ('a bold label', f'Intro.\n\n**Risk:** low {D} docs only\n', True),
    ('a risk line straight under another line (soft break)', f'Intro line\nRisk: high {D} touches capture\n', True),
    ('inline code in the reason', f'Intro.\n\nRisk: medium {D} edits `scripts/x.py` only\n', True),
    ('a code-block example before the real line',
     f'Intro.\n\n```\nRisk: <low | medium | high> {D} <reason>\n```\n\nRisk: low {D} docs\n', True),
    ('the PR template left unfilled', f'Intro.\n\nRisk: low | medium | high {D} <one-line reason>\n', False),
    ('no risk line', 'Intro.\n', False),
    ('a risk line in a code fence', f'Intro.\n\n```\nRisk: low {D} docs\n```\n', False),
    ('a risk line in an unclosed code fence', f'Intro.\n\n```\nRisk: low {D} docs\n', False),
    ('a risk line in an HTML comment', f'Intro.\n\n<!-- Risk: low {D} docs -->\n', False),
    ('a risk line in an unclosed HTML comment', f'Intro.\n\n<!--\nRisk: low {D} docs\n', False),
    ('a risk line in an indented code block', f'Intro.\n\n    Risk: low {D} docs\n', False),
    ('a risk line in inline code', f'Intro.\n\n`Risk: low {D} docs`\n', False),
    ('a risk line in a <details> fold', f'Intro.\n\n<details><summary>s</summary>\n\nRisk: low {D} docs\n\n</details>\n',
     False),
    ('a risk line as a list item', f'Intro.\n\n- Risk: low {D} docs\n', False),
    ('a risk line as a heading', f'## Risk: low {D} docs\n', False),
    ('two risk lines', f'Risk: low {D} docs\n\nRisk: high {D} capture\n', False),
    ('a bad grade word', f'Risk: moderate {D} CI\n', False),
    ('an empty reason', f'Risk: low {D}\n', False),
)


def render_test(repo, token):
    """Every known trick, rendered by GitHub itself, must get the expected answer (needs network)."""
    results = []
    for name, body, should_pass in RENDER_CASES:
        try:
            problems = check(render(body, repo, token))[0]
            results.append((f'{name}: {"passes" if should_pass else "fails"}', (problems == []) == should_pass))
        except (OSError, RuntimeError, urllib.error.URLError) as error:
            results.append((f'{name}: GitHub could not render it ({type(error).__name__})', False))
    return _finish(results, 'RENDER_TEST')


def run_event(event_path, repo, out=print, renderer=None, cwd=None):
    event = json.loads(pathlib.Path(event_path).read_text(encoding='utf-8'))
    pr = event.get('pull_request') or {}
    body = pr.get('body')
    if not body:
        return report([f'the PR body is empty; add the line "{EXAMPLE}" (your own grade and reason)'], out=out)
    try:
        base, head = pr['base']['sha'], pr['head']['sha']
        paths = changed_from_git(base, head, cwd)
    except (KeyError, TypeError, OSError, subprocess.CalledProcessError) as error:
        return report([f'git could not list the files this PR changes ({type(error).__name__}), so the floor cannot be '
                       'checked; failing closed'], out=out)
    token = os.environ.get('GITHUB_TOKEN')
    try:
        rendered = (renderer or (lambda b, r: render(b, r, token)))(body, repo)
    except (OSError, RuntimeError, urllib.error.URLError) as error:
        return report([f'GitHub could not render the PR body ({type(error).__name__}); failing closed'], out=out)
    problems, grade, least = check(rendered, paths, (pr.get('base') or {}).get('ref'))
    return report(problems, grade, least, out=out)


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
    if len(argv) >= 2 and argv[0] == '--body' and len(argv) % 2 == 0:
        options = dict(zip(argv[2::2], argv[3::2]))
        if set(options) - {'--changed', '--base'}:
            print(__doc__)
            return 2
        body = pathlib.Path(argv[1]).read_text(encoding='utf-8')
        paths = None
        if '--changed' in options:
            paths = [p.strip() for p in pathlib.Path(options['--changed']).read_text(encoding='utf-8').splitlines()
                     if p.strip()]
        try:
            rendered = render(body, repo, token)
        except (OSError, RuntimeError, urllib.error.URLError) as error:
            return report([f'GitHub could not render the body ({type(error).__name__}); failing closed'])
        problems, grade, least = check(rendered, paths, options.get('--base'))
        return report(problems, grade, least)
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
