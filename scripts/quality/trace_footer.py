"""BOO-TRACE-001: an agent-written pull request body ends with a trace footer (docs/quality/RULES.md).

  python -I scripts/quality/trace_footer.py --self-test        rules, on HTML samples shaped like GitHub's (offline)
  python -I scripts/quality/trace_footer.py --render-test      known tricks, rendered by GitHub itself (network)
  python -I scripts/quality/trace_footer.py --event "$GITHUB_EVENT_PATH"   (in the trace-footer workflow)
  python -I scripts/quality/trace_footer.py --body FILE        local check of a draft PR body or PR comment (network)
  python -I scripts/quality/trace_footer.py --commit FILE      local check of a commit message (offline, plain text)

The footer is one line, the last line a reader sees:

  Agent: <lane name> | Thread: <thread link> | Model: <model>/<effort>

Grammar (after runs of spaces are folded to one, leading and trailing spaces dropped):
  footer  = "Agent: " lane " | Thread: " thread " | Model: " model
  lane    = 1 to 80 characters, no "|", no control characters, not only spaces   e.g. agent-trust lane (Claude)
  thread  = "t3-thread://" [A-Za-z0-9._~/-]+                                  e.g. t3-thread://v2/4112...
          | "https://" host-with-a-dot [ "/" non-space characters ]          e.g. https://example.com/t/1
  model   = name *( " " name ) "/" effort                                     e.g. claude-opus-5-5/medium
  name    = [A-Za-z0-9] [A-Za-z0-9._:-]*                                      e.g. codex gpt-6.1-sol/medium
  effort  = [a-z]+                                                            e.g. low, medium, high, xhigh, max
The words "Agent:", "Thread:" and "Model:" and the " | " separators are exact (ASCII, that case, that order).

PR bodies are not read as text. GitHub renders the body (POST /markdown, gfm mode, as pr_evidence.py does) and the check
reads the rendered HTML, so text a reader cannot see does not count: an unclosed code fence or HTML comment, an
indented code block, a <details> fold. A plain-text "last line" check would be fooled by each of those. If GitHub
cannot render the body, the check fails closed. The rule on the rendered page:
  1. Exactly one visible line starts with "Agent:" (code blocks and inline code are not counted).
  2. It is the last visible line of the page.
  3. It sits directly in a top-level paragraph (not a heading, list, quote, table or fold) and holds only text and,
     for an https thread, the link GitHub makes of it.
  4. Its text matches the grammar above.
A footer written straight under a table row (no blank line) becomes a table row on GitHub and fails rule 3: leave a
blank line above it. A footer straight under another line of text (no blank line) is still its own visible line and
passes.

Commit messages (--commit) are plain text (git shows them as written): the last non-empty line, ignoring lines that
start with "#", must match the grammar. Not run in CI (a convention; see docs/quality/RULES.md, BOO-TRACE-001).
"""
import html.parser
import json
import os
import pathlib
import re
import sys
import urllib.error
import urllib.request

RULE = 'BOO-TRACE-001'
API = 'https://api.github.com/markdown'
EXAMPLE = ('Agent: agent-trust lane (Claude) | Thread: t3-thread://v2/41127859-de66-4654-8378-fd8497b3a652 | '
           'Model: claude-opus-5-5/medium')
FOOTER = re.compile(r'^Agent: (?P<lane>.*?) \| Thread: (?P<thread>.*?) \| Model: (?P<model>.*)$')
LANE = re.compile(r'^[^|\x00-\x1f\x7f]{1,80}$')
THREAD = re.compile(r'^(?:t3-thread://[A-Za-z0-9._~/-]+|https://[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+(?:/\S*)?)$')
MODEL = re.compile(r'^[A-Za-z0-9][A-Za-z0-9._:-]*(?: [A-Za-z0-9][A-Za-z0-9._:-]*)*/[a-z]+$')
BLOCK = {'p', 'div', 'pre', 'table', 'thead', 'tbody', 'tfoot', 'tr', 'td', 'th', 'ul', 'ol', 'li', 'blockquote',
         'details', 'summary', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'hr', 'dl', 'dt', 'dd', 'section', 'article',
         'figure', 'figcaption', 'markdown-accessiblity-table', 'caption'}
VOID = {'br', 'img', 'hr', 'input', 'meta', 'link', 'wbr', 'source', 'col', 'area', 'base', 'embed', 'track'}
LINE_TAGS = {'a'}  # the only markup allowed on the footer line: the link GitHub makes of an https thread


def fold(text):
    return ' '.join((text or '').split())


def grammar(line):
    """Problems with one footer line's text (empty list: it matches the grammar)."""
    return []  # RED: rules not written yet; the self-test below must fail

    text = fold(line)
    match = FOOTER.match(text)
    if not match:
        return [f'the footer "{text[:120]}" does not have the shape "Agent: <lane name> | Thread: <thread link> | '
                'Model: <model>/<effort>" (exact words, " | " between the three parts)']
    problems = []
    if not fold(match['lane']) or not LANE.match(match['lane']):
        problems.append('the lane name after "Agent:" must be 1 to 80 characters with no "|" (it was '
                        f'"{match["lane"][:40]}")')
    if not THREAD.match(match['thread']):
        problems.append('the thread after "Thread:" must be a t3-thread:// link or an https:// URL with no spaces (it '
                        f'was "{match["thread"][:60]}")')
    if not MODEL.match(match['model']):
        problems.append('the model after "Model:" must be <model name>/<effort>, for example claude-opus-5-5/medium '
                        f'or codex gpt-6.1-sol/medium (it was "{match["model"][:40]}")')
    return problems


class _Lines(html.parser.HTMLParser):
    """The visual lines of rendered HTML: a new line at every block boundary and every <br>. Each line records its
    text, the block elements around it, the inline tags inside it, and whether any of it is code."""

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.lines, self._stack, self._line = [], [], None

    def _close_line(self):
        if self._line is not None and fold(''.join(self._line['text'])):
            self._line['text'] = fold(''.join(self._line['text']))
            self.lines.append(self._line)
        self._line = None

    def _open_line(self):
        if self._line is None:
            self._line = {'text': [], 'blocks': [t for t in self._stack if t in BLOCK], 'tags': set(),
                          'code': any(t in ('pre', 'code') for t in self._stack)}
        return self._line

    def handle_starttag(self, tag, attrs):
        if tag in BLOCK or tag == 'br':
            self._close_line()
        else:
            line = self._open_line()
            line['tags'].add(tag)
            if tag == 'code':
                line['code'] = True
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
        line = self._open_line()
        line['text'].append(data)
        if any(t in ('pre', 'code') for t in self._stack):
            line['code'] = True

    def close(self):
        super().close()
        self._close_line()


def lines(rendered_html):
    parser = _Lines()
    parser.feed(rendered_html or '')
    parser.close()
    return parser.lines


def check(rendered_html):
    """Problems (plain-words strings) with the rendered PR body; empty means it passes."""
    return []  # RED: rules not written yet; the self-test below must fail

    visible = [ln for ln in lines(rendered_html) if not (ln['code'] and 'pre' in ln['blocks'])]
    footers = [ln for ln in visible if ln['text'].startswith('Agent:') and not ln['code']]
    if not footers:
        return ['the PR body has no trace footer a reader can see: end it with the line "' + EXAMPLE + '" (your own '
                'lane, thread and model), after a blank line, outside code blocks, HTML comments and folds']
    if len(footers) > 1:
        return [f'the PR body has {len(footers)} visible lines starting with "Agent:"; keep exactly one footer, the '
                'last line (put examples in a code block)']
    footer = footers[0]
    if visible[-1] is not footer:
        return [f'the trace footer is not the last line a reader sees (after it: "{visible[-1]["text"][:60]}"); move '
                'it to the very end of the body']
    problems = []
    if footer['blocks'] != ['p'] or footer['tags'] - LINE_TAGS:
        problems.append('the trace footer must be a plain line of text in its own paragraph (not in a heading, list, '
                        'quote, table or fold, and no bold, code or images): leave a blank line above it')
    return problems + grammar(footer['text'])


def check_commit(message):
    """Problems with a commit message (plain text): its last non-empty, non-# line must be a footer."""
    return []  # RED: rules not written yet; the self-test below must fail

    body = [ln for ln in (message or '').splitlines() if ln.strip() and not ln.startswith('#')]
    if not body or not body[-1].startswith('Agent:'):
        return ['the commit message does not end with a trace footer line "' + EXAMPLE + '"']
    return grammar(body[-1])


def render(body, repo, token=None):
    """The body as GitHub renders it on the PR page. Raises on any failure (the caller fails closed)."""
    data = json.dumps({'text': body or '', 'mode': 'gfm', 'context': repo}).encode('utf-8')
    request = urllib.request.Request(API, data=data, method='POST', headers={
        'Accept': 'application/vnd.github+json', 'Content-Type': 'application/json',
        'User-Agent': 'trace-footer-check', 'X-GitHub-Api-Version': '2022-11-28'})
    if token:
        request.add_header('Authorization', f'Bearer {token}')
    with urllib.request.urlopen(request, timeout=30) as response:
        if response.status != 200:
            raise RuntimeError(f'GitHub markdown API answered {response.status}')
        return response.read().decode('utf-8')


def report(problems, out=print):
    for p in problems:
        out(f'::error::{RULE} {p}')
    if problems:
        out(f'TRACE_FOOTER_FAILED {len(problems)} problem(s). Fix: docs/quality/RULES.md, {RULE}')
        return 1
    out('TRACE_FOOTER_OK')
    return 0


def _finish(results, name):
    failed = [n for n, ok in results if not ok]
    for n in failed:
        print(f'  FAILED: {n}')
    print(f'TRACE_FOOTER_{name}_{"FAILED" if failed else "PASSED"} {len(results) - len(failed)} of {len(results)}')
    return 1 if failed else 0


def _p(text):
    return f'<p dir="auto">{text}</p>\n'


def self_test():
    """The rules on HTML shaped like GitHub's rendering (taken from real renders), plus the grammar and commit mode."""
    results = []

    def expect(name, ok):
        results.append((name, bool(ok)))

    good = _p('Intro text.') + _p(EXAMPLE)
    https = 'Agent: x | Thread: <a href="https://example.com/t/1" rel="nofollow">https://example.com/t/1</a> | ' \
            'Model: codex gpt-6.1-sol/medium'
    expect('a valid footer passes', check(good) == [])
    expect('a valid footer with an https thread (autolinked) passes', check(_p('x') + _p(https)) == [])
    expect('a footer on the line after the Generated-with line (soft break) passes',
           check(_p('x') + _p('\U0001f916 Generated with <a href="https://claude.com/claude-code" rel="nofollow">'
                              'Claude Code</a><br>\n' + EXAMPLE)) == [])
    expect('a footer after a table and a blank line passes',
           check('<markdown-accessiblity-table><table role="table"><thead><tr><th>a</th></tr></thead><tbody><tr>'
                 '<td>b</td></tr></tbody></table></markdown-accessiblity-table>\n' + _p(EXAMPLE)) == [])
    expect('a footer example in a code block before the real footer passes',
           check(_p('x') + '<pre class="notranslate"><code class="notranslate">Agent: &lt;lane name&gt; | Thread: x'
                 '</code></pre>\n' + _p(EXAMPLE)) == [])
    expect('an inline-code footer example in a list before the real footer passes',
           check('<ul dir="auto">\n<li><code class="notranslate">Agent: a | Thread: b | Model: c/d</code></li>\n</ul>\n'
                 + _p(EXAMPLE)) == [])
    expect('an HTML comment after the footer (invisible) passes', check(good + '<!-- note -->\n') == [])
    expect('a missing footer fails', check(_p('Intro text.')) != [])
    expect('an empty body fails', check('') != [] and check(None) != [])
    expect('an unclosed code fence hiding the footer fails',
           check(_p('x') + '<pre class="notranslate"><code class="notranslate">' + EXAMPLE + '\n</code></pre>\n') != [])
    expect('an unclosed HTML comment hiding the footer fails (GitHub drops it)', check(_p('x') + '\n') != [])
    expect('a footer inside a <details> fold fails',
           check(_p('x') + '<details><summary>s</summary>\n' + _p(EXAMPLE) + '</details>\n') != [])
    expect('a footer in a blockquote fails', check(_p('x') + '<blockquote>\n' + _p(EXAMPLE) + '</blockquote>\n') != [])
    expect('a footer as a list item fails', check(_p('x') + f'<ul dir="auto">\n<li>{EXAMPLE}</li>\n</ul>\n') != [])
    expect('a footer as a heading fails', check(_p('x') + f'<h2 dir="auto">{EXAMPLE}</h2>\n') != [])
    expect('a footer swallowed into a table row fails',
           check('<markdown-accessiblity-table><table role="table"><thead><tr><th>a</th><th>Label</th></tr></thead>'
                 '<tbody><tr><td>Agent: agent-trust lane (Claude)</td><td>Thread: t3-thread://v2/x</td></tr></tbody>'
                 '</table></markdown-accessiblity-table>\n') != [])
    expect('a footer in bold fails', check(_p('x') + _p(f'<strong>{EXAMPLE}</strong>')) != [])
    expect('a footer in inline code fails', check(_p('x') + _p(f'<code class="notranslate">{EXAMPLE}</code>')) != [])
    expect('a footer not on the last line fails', check(_p(EXAMPLE) + _p('More text after it.')) != [])
    expect('a footer followed by a soft-break line fails', check(_p(EXAMPLE + '<br>\nmore')) != [])
    expect('two footers fail', check(_p(EXAMPLE) + _p(EXAMPLE)) != [])
    expect('two footers on one paragraph fail', check(_p(EXAMPLE + '<br>\n' + EXAMPLE)) != [])
    expect('an unfilled template placeholder fails (GitHub strips the <...> as tags)',
           check(_p('x') + _p('Agent:  | Thread:  | Model: &lt;model/effort&gt;')) != [])
    for name, line in (
            ('an empty lane', 'Agent:  | Thread: t3-thread://v2/a | Model: m/medium'),
            ('a lane of 81 characters', 'Agent: ' + 'x' * 81 + ' | Thread: t3-thread://v2/a | Model: m/medium'),
            ('a missing Thread part', 'Agent: lane | Model: m/medium'),
            ('a thread that is not a link', 'Agent: lane | Thread: my thread | Model: m/medium'),
            ('an http (not https) thread', 'Agent: lane | Thread: http://example.com/t | Model: m/medium'),
            ('an https thread without a host', 'Agent: lane | Thread: https:// | Model: m/medium'),
            ('a bare t3-thread:// scheme', 'Agent: lane | Thread: t3-thread:// | Model: m/medium'),
            ('a model without an effort', 'Agent: lane | Thread: t3-thread://v2/a | Model: claude-opus-5-5'),
            ('a model with an empty effort', 'Agent: lane | Thread: t3-thread://v2/a | Model: claude-opus-5-5/'),
            ('an upper-case effort', 'Agent: lane | Thread: t3-thread://v2/a | Model: claude-opus-5-5/Medium'),
            ('the parts in the wrong order', 'Agent: lane | Model: m/medium | Thread: t3-thread://v2/a'),
            ('lower-case keywords', 'agent: lane | thread: t3-thread://v2/a | model: m/medium'),
            ('a missing space after a separator', 'Agent: lane |Thread: t3-thread://v2/a | Model: m/medium'),
            ('a look-alike A in Agent', 'Аgent: lane | Thread: t3-thread://v2/a | Model: m/medium'),
            ('an extra fourth part', 'Agent: lane | Thread: t3-thread://v2/a | Model: m/medium | Extra: x')):
        expect(f'{name} fails', grammar(line) != [] and check(_p('x') + _p(line.replace('<', '&lt;'))) != [])
    for name, line in (('the mandated example', EXAMPLE),
                       ('a codex model with a space', 'Agent: review lane (Codex) | Thread: t3-thread://v2/a-b | '
                                                      'Model: codex gpt-6.1-sol/medium'),
                       ('extra spaces folded', 'Agent:  lane   x |  Thread: https://example.com | Model: m/high'),
                       ('an https thread with a path and query', 'Agent: l | Thread: https://claude.ai/code/x?y=1 | '
                                                                 'Model: m.1:2/max')):
        expect(f'grammar: {name} passes', grammar(line) == [])
    commit = 'subject\n\nbody\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>\n' + EXAMPLE + '\n'
    expect('commit: footer as the last line passes', check_commit(commit) == [])
    expect('commit: git comment lines after the footer are ignored', check_commit(commit + '# Please enter...\n') == [])
    expect('commit: footer before the trailer fails',
           check_commit('subject\n\n' + EXAMPLE + '\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>\n') != [])
    expect('commit: no footer fails', check_commit('subject\n\nbody\n') != [] and check_commit('') != [])
    expect('commit: a malformed footer fails',
           check_commit('subject\n\nAgent: lane | Thread: nope | Model: m/medium\n') != [])
    expect('report prints TRACE_FOOTER_OK only with no problems',
           report([], out=lambda *_: None) == 0 and report(['x'], out=lambda *_: None) == 1)
    with_event = _event_case(lambda b, r: good)
    expect('event mode passes a good rendered body', with_event == 0)
    expect('event mode fails closed when GitHub cannot render', _event_case(_broken_renderer) == 1)
    expect('event mode fails a PR with no body', _event_case(lambda b, r: good, body=None) == 1)
    return _finish(results, 'SELF_TEST')


def _broken_renderer(body, repo):
    raise urllib.error.URLError('offline')


def _event_case(renderer, body='x'):
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        event = pathlib.Path(tmp) / 'event.json'
        event.write_text(json.dumps({'pull_request': {'body': body}}), encoding='utf-8')
        return run_event(event, 'owner/repo', out=lambda *_: None, renderer=renderer)


# Markdown bodies rendered by GitHub itself (--render-test): the tricks a plain-text check would miss.
F = EXAMPLE
RENDER_CASES = (
    ('a footer after a blank line', 'Intro.\n\n' + F + '\n', True),
    ('a footer under the Generated-with line (no blank line)',
     'Intro.\n\n\U0001f916 Generated with [Claude Code](https://claude.com/claude-code)\n' + F + '\n', True),
    ('an https thread', 'Intro.\n\nAgent: x | Thread: https://example.com/t/1 | Model: codex gpt-6.1-sol/medium\n',
     True),
    ('a code-block example before the real footer',
     'Intro.\n\n```\nAgent: <lane name> | Thread: <T3 thread link> | Model: <model/effort>\n```\n\n' + F + '\n', True),
    ('a trailing HTML comment after the footer', 'Intro.\n\n' + F + '\n\n<!-- note -->\n', True),
    ('no footer', 'Intro.\n', False),
    ('an unclosed code fence', 'Intro.\n\n```\n' + F + '\n', False),
    ('an unclosed HTML comment', 'Intro.\n\n<!--\n' + F + '\n', False),
    ('an indented code block', 'Intro.\n\n    ' + F + '\n', False),
    ('a <details> fold', 'Intro.\n\n<details><summary>s</summary>\n\n' + F + '\n\n</details>\n', False),
    ('a blockquote', 'Intro.\n\n> ' + F + '\n', False),
    ('a heading', 'Intro.\n\n## ' + F + '\n', False),
    ('straight under a table (becomes a table row)', '| a | Label |\n| - | - |\n| b | mock |\n' + F + '\n', False),
    ('an unfilled template placeholder',
     'Intro.\n\nAgent: <lane name> | Thread: <T3 thread link> | Model: <model/effort>\n', False),
    ('a link whose text is not the thread', 'Intro.\n\nAgent: x | Thread: [thread](https://example.com/t) | '
     'Model: m/medium\n', False),
    ('two footers', F + '\n\n' + F + '\n', False),
    ('text after the footer', F + '\n\nMore.\n', False),
)


def render_test(repo, token):
    """Every known trick, rendered by GitHub itself, must get the expected answer (needs network)."""
    results = []
    for name, body, should_pass in RENDER_CASES:
        try:
            problems = check(render(body, repo, token))
            results.append((f'{name}: {"passes" if should_pass else "fails"}', (problems == []) == should_pass))
        except (OSError, RuntimeError, urllib.error.URLError) as error:
            results.append((f'{name}: GitHub could not render it ({type(error).__name__})', False))
    return _finish(results, 'RENDER_TEST')


def run_event(event_path, repo, out=print, renderer=None):
    event = json.loads(pathlib.Path(event_path).read_text(encoding='utf-8'))
    body = (event.get('pull_request') or {}).get('body')
    if not body:
        return report(['the PR body is empty; end it with the trace footer line "' + EXAMPLE + '"'], out)
    token = os.environ.get('GITHUB_TOKEN')
    try:
        rendered = (renderer or (lambda b, r: render(b, r, token)))(body, repo)
    except (OSError, RuntimeError, urllib.error.URLError) as error:
        return report([f'GitHub could not render the PR body ({type(error).__name__}); failing closed'], out)
    return report(check(rendered), out)


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
    if len(argv) == 2 and argv[0] == '--body':
        body = pathlib.Path(argv[1]).read_text(encoding='utf-8')
        try:
            rendered = render(body, repo, token)
        except (OSError, RuntimeError, urllib.error.URLError) as error:
            return report([f'GitHub could not render the body ({type(error).__name__}); failing closed'])
        return report(check(rendered))
    if len(argv) == 2 and argv[0] == '--commit':
        return report(check_commit(pathlib.Path(argv[1]).read_text(encoding='utf-8')))
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
