"""BOO-BUG-001: every bug fix carries a regression test that was seen failing before the fix (docs/quality/bug-regression.md).

  python -I scripts/quality/bug_regression.py --self-test                 rules, on planted events (offline)
  python -I scripts/quality/bug_regression.py --event "$GITHUB_EVENT_PATH"   (in the bug-regression workflow)

A pull request is a bug fix when any of these is true:
  - its title starts with the word `fix` (`fix: ...`, `fix(notch): ...`, `Fix the hover race`);
  - it has the label `bug`;
  - its body has a heading `Bug` (for example `## Bug`);
  - it closes an issue labelled `bug` (a closing keyword such as `Fixes #12`, or an issue linked in the sidebar).
Any other pull request passes untouched.

A bug fix must have a `## Regression test` section in its body, with:
  - each test as a code span `path/to/File.cs::MethodName` (the file is new or changed in this PR and holds the method);
  - a list item `Red before:` with a link to a GitHub Actions JOB of this repository
    (https://github.com/<repo>/actions/runs/<id>/job/<id>) that ran on a commit BEFORE the fix: an ancestor of the PR
    head, not the head itself. The job failed, its log has a failure line for every named test, and the test file and
    method already existed at that commit;
  - a list item `Green on head:` with a link to a run (or job) of this repository on the PR head that succeeded.

Like BOO-EVID-001 the body is not parsed here: GitHub renders it (POST /markdown), so a section inside an HTML comment
or a code block does not count. Every GitHub read (rendering, runs, jobs, logs, issues) is read-only; if any of them
cannot be read, the check fails closed. The token is only ever sent in a request header, never printed.
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

RULE = 'BOO-BUG-001'
API = 'https://api.github.com'
TEST_REF = re.compile(r'^([A-Za-z0-9_][A-Za-z0-9_.\-/]*)::([A-Za-z_][A-Za-z0-9_]*)$')
FIX_TITLE = re.compile(r'^\s*fix(?![A-Za-z0-9_])', re.IGNORECASE)
CLOSING = re.compile(r'\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\b\s*:?\s+'
                     r'(?:https://github\.com/([\w.-]+/[\w.-]+)/issues/|([\w.-]+/[\w.-]+)?#)(\d+)', re.IGNORECASE)
ANSI = re.compile(r'\x1b\[[0-9;]*[A-Za-z]')
FAILED_LINE = (re.compile(r'^(?:\S+Z )?\s*(?:Failed|FAILED|\[FAIL\])\s+(\S+)'),   # xUnit/dotnet test, pytest
               re.compile(r'^(?:\S+Z )?\s*(\S+)\s+\[FAIL\]'))                    # xUnit console runner
LOG_LIMIT = 64 * 1024 * 1024


class ApiError(Exception):
    """A GitHub read that did not answer as expected (the check fails closed)."""


class GitError(Exception):
    """git could not answer (the check fails closed)."""


def run_link(repo):
    return re.compile(r'^https://github\.com/' + re.escape(repo)
                      + r'/actions/runs/(\d+)(?:/job/(\d+)|/attempts/\d+)?/?(?:[?#][^/]*)?$', re.IGNORECASE)


class _Sections(html.parser.HTMLParser):
    """Headings (level, text) and, per h1/h2 section, its items (li and p: text and visible link targets) and code
    spans. Struck-out text (<del>) is not read: a reader treats it as withdrawn."""

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.headings, self.sections = [], {}
        self._heading, self._current, self._items, self._links, self._code, self._tags = None, None, [], [], None, []

    def handle_starttag(self, tag, attrs):
        if tag in ('br', 'img', 'hr', 'input', 'wbr'):
            return
        self._tags.append(tag)
        if re.fullmatch(r'h[1-6]', tag):
            self._heading = [int(tag[1]), []]
        elif tag in ('li', 'p') and self._current is not None:
            item = {'text': [], 'links': []}
            self.sections[self._current]['items'].append(item)
            self._items.append(item)
        elif tag == 'a' and self._items and dict(attrs).get('href'):
            self._links.append([dict(attrs)['href'], []])
        elif tag == 'code' and self._current is not None:
            self._code = []

    def handle_endtag(self, tag):
        if tag not in self._tags:
            return
        while self._tags:
            open_tag = self._tags.pop()
            if re.fullmatch(r'h[1-6]', open_tag) and self._heading is not None:
                level, text = self._heading[0], ' '.join(''.join(self._heading[1]).split())
                self.headings.append((level, text))
                self._heading = None
                if level <= 2:
                    self._current = text.lower().rstrip(':').strip()
                    self.sections.setdefault(self._current, {'items': [], 'code': []})
            elif open_tag in ('li', 'p') and self._items:
                self._items.pop()
            elif open_tag == 'a' and self._links:
                href, text = self._links.pop()
                if re.search(r'[A-Za-z0-9]', ''.join(text)):         # a link a reader can see and read
                    for item in self._items:
                        item['links'].append(href)
            elif open_tag == 'code' and self._code is not None:
                self.sections[self._current]['code'].append(''.join(self._code).strip())
                self._code = None
            if open_tag == tag:
                break

    def handle_data(self, data):
        if 'del' in self._tags:
            return
        if self._heading is not None:
            self._heading[1].append(data)
            return
        for item in self._items:
            item['text'].append(data)
        for link in self._links:
            link[1].append(data)
        if self._code is not None:
            self._code.append(data)


def sections(rendered_html):
    parser = _Sections()
    parser.feed(rendered_html or '')
    parser.close()
    for section in parser.sections.values():
        for item in section['items']:
            item['text'] = ' '.join(''.join(item['text']).split())
    return parser.headings, parser.sections


def bug_reasons(pr, headings, raw_body, repo, gh):
    """Why this PR is a bug fix (empty: it is not). Raises ApiError when linked issues cannot be read."""
    reasons = []
    if FIX_TITLE.match(pr.get('title') or ''):
        reasons.append('its title starts with "fix"')
    if any((label.get('name') or '').lower() == 'bug' for label in pr.get('labels') or []):
        reasons.append('it has the label "bug"')
    if any(re.fullmatch(r'bugs?:?', text, re.IGNORECASE) for _, text in headings):
        reasons.append('its body has a "Bug" section')
    issues = set()
    for full, url_repo, short_repo, number in ((m.group(0), m.group(1), m.group(2), m.group(3))
                                               for m in CLOSING.finditer(raw_body or '')):
        if (url_repo or short_repo or repo).lower() == repo.lower():
            issues.add(int(number))
    linked = {}
    if pr.get('number'):
        owner, name = repo.split('/', 1)
        data = gh.graphql('query($o:String!,$n:String!,$p:Int!){repository(owner:$o,name:$n){pullRequest(number:$p)'
                          '{closingIssuesReferences(first:50){nodes{number labels(first:50){nodes{name}}}}}}}',
                          {'o': owner, 'n': name, 'p': pr['number']})
        try:
            nodes = data['data']['repository']['pullRequest']['closingIssuesReferences']['nodes']
            linked = {n['number']: [l['name'] for l in n['labels']['nodes']] for n in nodes}
        except (KeyError, TypeError) as error:
            raise ApiError('the issues this PR closes could not be read') from error
    for number in sorted(issues - set(linked)):
        issue = gh.get(f'/repos/{repo}/issues/{number}')
        linked[number] = [l.get('name', '') for l in issue.get('labels') or []]
    for number, labels in sorted(linked.items()):
        if any(l.lower() == 'bug' for l in labels):
            reasons.append(f'it closes issue #{number}, labelled "bug"')
    return reasons


def failed_in_log(log, method):
    for line in (log or '').splitlines():
        line = ANSI.sub('', line)
        for pattern in FAILED_LINE:
            m = pattern.match(line)
            if m:
                name = re.split(r'[(\[]', m.group(1))[0]
                if re.split(r'\.|::|/', name)[-1] == method:
                    return True
    return False


def has_method(text, method):
    return text is not None and re.search(r'(?<![A-Za-z0-9_])' + re.escape(method) + r'(?![A-Za-z0-9_])', text)


def check(pr, rendered_html, repo, gh, git):
    """(problems, reasons). Empty problems means the PR passes; empty reasons means it is not a bug fix."""
    headings, secs = sections(rendered_html)
    try:
        reasons = bug_reasons(pr, headings, pr.get('body') or '', repo, gh)
    except ApiError as error:
        return [f'could not tell whether this PR closes a bug issue ({error}); failing closed'], ['unknown']
    if not reasons:
        return [], []
    why = ' and '.join(reasons)
    section = secs.get('regression test') or secs.get('regression tests')
    if section is None:
        return [f'this PR is a bug fix ({why}) but its body has no "## Regression test" section: name the test that '
                'reproduces the bug (`path/File.cs::Method`), a "Red before:" link to the job where it failed before '
                'the fix, and a "Green on head:" link to the run where it passes'], reasons
    problems = []
    base, head = (pr.get('base') or {}).get('sha'), (pr.get('head') or {}).get('sha')
    tests = [TEST_REF.match(c).groups() for c in section['code'] if TEST_REF.match(c)]
    if not tests:
        problems.append('the "Regression test" section names no test: write each one as a code span '
                        '`path/to/File.cs::MethodName`')
    link = run_link(repo)

    def links(prefix):
        found = []
        for item in section['items']:
            if re.match(prefix, item['text'], re.IGNORECASE):
                found += item['links']
        return found

    red_links, green_links = links(r'^\W*red\s+before\b'), links(r'^\W*green\s+on\s+head\b')
    if not red_links:
        problems.append('the "Regression test" section has no "Red before:" item with a link to the job where the '
                        'test failed before the fix')
    if not green_links:
        problems.append('the "Regression test" section has no "Green on head:" item with a link to the run on the PR '
                        'head where the test passes')
    try:
        changed = set(git.changed(base, head))
    except GitError as error:
        return problems + [f'could not list the files this PR changes ({error}); failing closed'], reasons
    for path, method in tests:
        if path not in changed:
            problems.append(f'test {path}::{method}: the file is not new or changed in this PR (a regression test is '
                            'added with the fix)')
            continue
        try:
            if not has_method(git.show(head, path), method):
                problems.append(f'test {path}::{method}: no method {method} in {path} on the PR head')
        except GitError as error:
            problems.append(f'test {path}::{method}: could not read it on the PR head ({error}); failing closed')
    reds = []                                                      # (sha, log) of every red job that checks out
    for href in red_links:
        m = link.match(href)
        if not m:
            problems.append(f'red-before link {href[:100]} is not a full https link to a GitHub Actions job of {repo}')
            continue
        run_id, job_id = m.group(1), m.group(2)
        if not job_id:
            problems.append(f'red-before link {href[:100]} is a whole run: link the failed JOB '
                            '(.../actions/runs/<id>/job/<id>) so its log can be read')
            continue
        try:
            job = gh.get(f'/repos/{repo}/actions/jobs/{job_id}')
            if str(job.get('run_id')) != run_id:
                problems.append(f'red-before job {job_id} is not part of run {run_id}')
                continue
            sha = job.get('head_sha') or ''
            if job.get('conclusion') != 'failure':
                problems.append(f'red-before job {job_id} did not fail (conclusion: {job.get("conclusion")}); the '
                                'regression test must be seen failing before the fix')
                continue
            if not re.fullmatch(r'[0-9a-f]{40}', sha):
                problems.append(f'red-before job {job_id} has no readable commit; failing closed')
                continue
            if sha == head:
                problems.append(f'red-before job {job_id} ran on the PR head itself ({sha[:7]}): it must run on a commit '
                                'before the fix (push the test first, let it fail, then push the fix)')
                continue
            status = gh.get(f'/repos/{repo}/compare/{sha}...{head}').get('status')
            if status != 'ahead':
                problems.append(f'red-before job {job_id} ran on {sha[:7]}, which is not an earlier commit of this PR '
                                f'head (compare: {status})')
                continue
            reds.append((sha, gh.job_log(job_id), job_id))
        except ApiError as error:
            problems.append(f'red-before job {job_id} could not be read ({error}); failing closed')
    if reds:
        for path, method in tests:
            proven = False
            for sha, log, job_id in reds:
                try:
                    present = has_method(git.show(sha, path), method)
                except GitError:
                    present = False
                if present and failed_in_log(log, method):
                    proven = True
                    break
            if not proven:
                problems.append(f'test {path}::{method}: no red-before job shows it failing (a log line like '
                                f'"Failed ...{method}") on a commit where {path} already had it')
    for href in green_links:
        m = link.match(href)
        if not m:
            problems.append(f'green-on-head link {href[:100]} is not a full https link to a GitHub Actions run of '
                            f'{repo}')
            continue
        run_id, job_id = m.group(1), m.group(2)
        try:
            data = gh.get(f'/repos/{repo}/actions/jobs/{job_id}' if job_id else f'/repos/{repo}/actions/runs/{run_id}')
        except ApiError as error:
            problems.append(f'green-on-head run {run_id} could not be read ({error}); failing closed')
            continue
        if job_id and str(data.get('run_id')) != run_id:
            problems.append(f'green-on-head job {job_id} is not part of run {run_id}')
        elif data.get('head_sha') != head:
            problems.append(f'green-on-head run {run_id} ran on {str(data.get("head_sha"))[:7]}, not on the PR head '
                            f'{str(head)[:7]}: link the run on this head (edit the body after it finishes)')
        elif data.get('conclusion') != 'success':
            problems.append(f'green-on-head run {run_id} has not passed (status: {data.get("status")}, conclusion: '
                            f'{data.get("conclusion")}); edit the body once it is green to re-run this check')
    return problems, reasons


class GitHub:
    """Read-only GitHub REST/GraphQL reads. The token goes only into a request header."""

    def __init__(self, repo, token):
        self.repo, self.token = repo, token

    def _open(self, request, opener=None):
        request.add_header('Accept', 'application/vnd.github+json')
        request.add_header('User-Agent', 'bug-regression-check')
        request.add_header('X-GitHub-Api-Version', '2022-11-28')
        if self.token:
            request.add_header('Authorization', f'Bearer {self.token}')
        try:
            with (opener or urllib.request.build_opener()).open(request, timeout=30) as response:
                return response.status, response.headers, response.read(LOG_LIMIT)
        except urllib.error.HTTPError as error:
            if error.code in (301, 302, 303, 307, 308):
                return error.code, error.headers, b''
            raise ApiError(f'GitHub answered {error.code}') from None
        except (OSError, urllib.error.URLError) as error:
            raise ApiError(f'{type(error).__name__}') from None

    def get(self, path):
        status, _, body = self._open(urllib.request.Request(API + path))
        if status != 200:
            raise ApiError(f'GitHub answered {status}')
        return json.loads(body.decode('utf-8'))

    def graphql(self, query, variables):
        data = json.dumps({'query': query, 'variables': variables}).encode('utf-8')
        status, _, body = self._open(urllib.request.Request(API + '/graphql', data=data, method='POST'))
        answer = json.loads(body.decode('utf-8')) if status == 200 else None
        if not answer or answer.get('errors'):
            raise ApiError('GitHub GraphQL did not answer')
        return answer

    def job_log(self, job_id):
        class _Stay(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, *args, **kwargs):
                return None
        status, headers, body = self._open(urllib.request.Request(f'{API}/repos/{self.repo}/actions/jobs/{job_id}/logs'),
                                           urllib.request.build_opener(_Stay))
        if status in (301, 302, 303, 307, 308):
            location = headers.get('Location') or ''
            if not location.startswith('https://'):
                raise ApiError('the job log moved somewhere unreadable')
            try:                                             # the signed log link must NOT get the token
                with urllib.request.urlopen(urllib.request.Request(location, headers={
                        'User-Agent': 'bug-regression-check'}), timeout=60) as response:
                    body = response.read(LOG_LIMIT)
            except (OSError, urllib.error.URLError) as error:
                raise ApiError(f'job log: {type(error).__name__}') from None
        elif status != 200:
            raise ApiError(f'job log: GitHub answered {status}')
        return body.decode('utf-8', 'replace')

    def render(self, body):
        data = json.dumps({'text': body or '', 'mode': 'gfm', 'context': self.repo}).encode('utf-8')
        status, _, out = self._open(urllib.request.Request(API + '/markdown', data=data, method='POST',
                                                           headers={'Content-Type': 'application/json'}))
        if status != 200:
            raise ApiError(f'GitHub markdown answered {status}')
        return out.decode('utf-8')


class Git:
    def _run(self, args):
        try:
            return subprocess.run(['git', '-c', 'core.quotePath=false'] + args, capture_output=True)
        except OSError as error:
            raise GitError(type(error).__name__) from None

    def changed(self, base, head):
        if not base or not head:
            raise GitError('the event has no base or head commit')
        out = self._run(['diff', '--name-only', '--no-renames', '--diff-filter=AM', '-z', f'{base}...{head}'])
        if out.returncode != 0:
            raise GitError('git diff base...head failed')
        return [f for f in out.stdout.decode('utf-8', 'replace').split('\0') if f]

    def show(self, sha, path):
        if self._run(['cat-file', '-e', f'{sha}^{{commit}}']).returncode != 0:
            raise GitError(f'commit {str(sha)[:7]} is not in the checkout')
        out = self._run(['show', f'{sha}:{path}'])
        return out.stdout.decode('utf-8', 'replace') if out.returncode == 0 else None


def report(problems, reasons, out=print):
    for p in problems:
        out(f'::error::{RULE} {p}')
    if problems:
        out(f'BUG_REGRESSION_FAILED {len(problems)} problem(s). Fix: docs/quality/bug-regression.md, {RULE}')
        return 1
    out('BUG_REGRESSION_OK ' + ('(bug fix: ' + ' and '.join(reasons) + '; regression test proven red before and '
                                'green on head)' if reasons else '(not a bug fix: nothing to check)'))
    return 0


def run_event(event_path, repo, gh, git, out=print):
    try:
        event = json.loads(pathlib.Path(event_path).read_text(encoding='utf-8'))
        pr = event['pull_request']
        if not isinstance(pr, dict):
            raise TypeError
    except (OSError, ValueError, KeyError, TypeError):
        return report(['the event file has no pull request; failing closed'], ['unknown'], out)
    try:
        rendered = gh.render(pr.get('body') or '')
    except ApiError as error:
        return report([f'GitHub could not render the PR body ({error}); failing closed'], ['unknown'], out)
    problems, reasons = check(pr, rendered, repo, gh, git)
    return report(problems, reasons, out)


# ---------------------------------------------------------------- self-test (planted events, no network)

class _FakeGitHub:
    def __init__(self, data, logs=None, graphql=None, broken=False):
        self.data, self.logs, self.answer, self.broken = data, logs or {}, graphql, broken

    def get(self, path):
        if self.broken or path not in self.data:
            raise ApiError('GitHub answered 404')
        return self.data[path]

    def graphql(self, query, variables):
        if self.broken:
            raise ApiError('GitHub GraphQL did not answer')
        nodes = self.answer or []
        return {'data': {'repository': {'pullRequest': {'closingIssuesReferences': {'nodes': nodes}}}}}

    def job_log(self, job_id):
        if self.broken or job_id not in self.logs:
            raise ApiError('job log: GitHub answered 404')
        return self.logs[job_id]

    def render(self, body):
        if self.broken:
            raise ApiError('GitHub markdown answered 503')
        return body                                          # planted bodies are already HTML


class _FakeGit:
    def __init__(self, changed, files):
        self._changed, self.files = changed, files

    def changed(self, base, head):
        if self._changed is None:
            raise GitError('git diff base...head failed')
        return self._changed

    def show(self, sha, path):
        if sha not in self.files:
            raise GitError(f'commit {sha[:7]} is not in the checkout')
        return self.files[sha].get(path)


def self_test(out=print):
    results = []

    def expect(name, ok):
        results.append((name, bool(ok)))

    repo = 'owner/repo'
    base, red, head, other = 'b' * 40, 'a' * 40, 'c' * 40, 'd' * 40
    test_file, method = 'tests/Boo.Core.Tests/HoverRegressionTests.cs', 'Hover_after_quit_does_not_reopen'
    red_job = f'https://github.com/{repo}/actions/runs/10/job/11'
    green_run = f'https://github.com/{repo}/actions/runs/20'
    source = f'public class HoverRegressionTests {{ [Fact] public void {method}() {{ }} }}'
    api = {
        f'/repos/{repo}/actions/jobs/11': {'run_id': 10, 'head_sha': red, 'conclusion': 'failure'},
        f'/repos/{repo}/actions/jobs/12': {'run_id': 10, 'head_sha': head, 'conclusion': 'failure'},
        f'/repos/{repo}/actions/jobs/13': {'run_id': 10, 'head_sha': red, 'conclusion': 'success'},
        f'/repos/{repo}/actions/jobs/14': {'run_id': 10, 'head_sha': other, 'conclusion': 'failure'},
        f'/repos/{repo}/actions/runs/20': {'head_sha': head, 'conclusion': 'success', 'status': 'completed'},
        f'/repos/{repo}/actions/runs/21': {'head_sha': red, 'conclusion': 'success', 'status': 'completed'},
        f'/repos/{repo}/actions/runs/22': {'head_sha': head, 'conclusion': None, 'status': 'in_progress'},
        f'/repos/{repo}/compare/{red}...{head}': {'status': 'ahead'},
        f'/repos/{repo}/compare/{other}...{head}': {'status': 'diverged'},
        f'/repos/{repo}/issues/7': {'labels': [{'name': 'bug'}]},
        f'/repos/{repo}/issues/8': {'labels': [{'name': 'enhancement'}]},
    }
    log = ('2026-11-13T09:06:21.2603924Z   Failed Boo.Core.Tests.HoverRegressionTests.'
           f'{method} [377 ms]\n2026-11-13T09:06:22Z Total tests: 1, Failed: 1\n')
    logs = {'11': log, '12': log, '13': log, '14': log}
    gh = _FakeGitHub(api, logs)
    git = _FakeGit([test_file, 'src/Boo.App/Notch.cs'], {head: {test_file: source}, red: {test_file: source},
                                                          other: {test_file: source}})

    def body(red_href=red_job, green_href=green_run, tests=f'{test_file}::{method}', heading='Regression test',
             extra=''):
        items = f'<li>Test: <code>{tests}</code></li>' if tests else ''
        if red_href:
            items += f'<li>Red before: <a href="{red_href}">failed job</a></li>'
        if green_href:
            items += f'<li>Green on head: <a href="{green_href}">run</a></li>'
        return (f'<h2>Bug</h2><p>Boo reopened after quit.</p>{extra}<h2>{heading}</h2><ul>{items}</ul>'
                '<h2>Evidence</h2><p>table</p>')

    def judge(html, title='fix(notch): reopen after quit', labels=(), number=None, github=None, git_=None, raw=None):
        pr = {'title': title, 'labels': [{'name': l} for l in labels], 'body': raw if raw is not None else html,
              'base': {'sha': base}, 'head': {'sha': head}, 'number': number}
        return check(pr, html, repo, github or gh, git_ or git)

    def fails(result, words=''):
        problems, _ = result
        return problems != [] and (not words or any(words in p for p in problems))

    # The six required cases.
    expect('a bug PR without a "Regression test" section fails',
           fails(judge('<h2>Bug</h2><p>x</p><h2>Evidence</h2>'), 'no "## Regression test" section'))
    expect('a section without a red-before link fails', fails(judge(body(red_href=None)), 'no "Red before:"'))
    expect('a red-before job on the PR head itself fails',
           fails(judge(body(red_href=f'https://github.com/{repo}/actions/runs/10/job/12')), 'PR head itself'))
    expect('a red-before link to another repository fails',
           fails(judge(body(red_href='https://github.com/someone/else/actions/runs/10/job/11')), 'not a full https'))
    expect('a complete section passes', judge(body()) == ([], ['its title starts with "fix"', 'its body has a "Bug" section']))
    expect('a non-bug PR passes untouched',
           judge('<h2>Summary</h2><p>New feature.</p>', title='feat: focus timer') == ([], []))
    # What makes a PR a bug fix.
    plain = '<h2>Summary</h2><p>x</p>'
    expect('the label "bug" makes it a bug fix', fails(judge(plain, title='feat: x', labels=('bug',))))
    expect('a "## Bug" heading makes it a bug fix', fails(judge('<h2>Bug</h2><p>x</p>', title='chore: x')))
    expect('a title "Fix the hover race" makes it a bug fix', fails(judge(plain, title='Fix the hover race')))
    expect('a title "fixture update" is not a bug fix', judge(plain, title='fixture update') == ([], []))
    expect('"Fixes #7" (issue labelled bug) makes it a bug fix',
           fails(judge(plain, title='feat: x', raw='Fixes #7')))
    expect('"Fixes #8" (issue not labelled bug) does not',
           judge(plain, title='feat: x', raw='Fixes #8') == ([], []))
    expect('a sidebar-linked bug issue (GraphQL) makes it a bug fix',
           fails(judge(plain, title='feat: x', number=5,
                       github=_FakeGitHub(api, logs, graphql=[{'number': 9, 'labels': {'nodes': [{'name': 'Bug'}]}}]))))
    expect('an issue that cannot be read fails closed', fails(judge(plain, title='feat: x', raw='closes #99'),
                                                              'failing closed'))
    expect('GitHub unreachable fails closed even for a non-bug PR',
           fails(judge(plain, title='feat: x', number=5, github=_FakeGitHub(api, logs, broken=True)), 'failing closed'))
    # The section's contents.
    expect('a section that names no test fails', fails(judge(body(tests=None)), 'names no test'))
    expect('no green-on-head link fails', fails(judge(body(green_href=None)), 'no "Green on head:"'))
    expect('a red-before link to a whole run (no job) fails',
           fails(judge(body(red_href=f'https://github.com/{repo}/actions/runs/10')), 'whole run'))
    expect('a red-before link that is only text (http, not https) fails',
           fails(judge(body(red_href=f'http://github.com/{repo}/actions/runs/10/job/11')), 'not a full https'))
    expect('a red-before job that passed fails',
           fails(judge(body(red_href=f'https://github.com/{repo}/actions/runs/10/job/13')), 'did not fail'))
    expect('a red-before job on a commit that is not an ancestor fails',
           fails(judge(body(red_href=f'https://github.com/{repo}/actions/runs/10/job/14')), 'not an earlier commit'))
    expect('a red-before job whose run id does not match fails',
           fails(judge(body(red_href=f'https://github.com/{repo}/actions/runs/99/job/11')), 'not part of run'))
    expect('a red-before log without a failure line for the test fails',
           fails(judge(body(), github=_FakeGitHub(api, dict(logs, **{'11': 'Passed! 1 test'}))), 'no red-before job'))
    expect('a test file not changed in this PR fails',
           fails(judge(body(), git_=_FakeGit(['src/a.cs'], git.files)), 'not new or changed'))
    expect('a method missing from the test file fails',
           fails(judge(body(tests=f'{test_file}::Not_there')), 'no method Not_there'))
    expect('a test that did not exist at the red-before commit fails',
           fails(judge(body(), git_=_FakeGit(git._changed, {head: {test_file: source}, red: {}})), 'no red-before job'))
    expect('git unable to list changed files fails closed',
           fails(judge(body(), git_=_FakeGit(None, git.files)), 'failing closed'))
    expect('a green-on-head run on another commit fails',
           fails(judge(body(green_href=f'https://github.com/{repo}/actions/runs/21')), 'not on the PR head'))
    expect('a green-on-head run still in progress fails',
           fails(judge(body(green_href=f'https://github.com/{repo}/actions/runs/22')), 'has not passed'))
    expect('a struck-out "Regression test" heading does not count',
           fails(judge(body(heading='<del>Regression test</del>')), 'no "## Regression test" section'))
    expect('a struck-out red-before link does not count',
           fails(judge(body().replace(f'<a href="{red_job}">failed job</a>',
                                      f'<del><a href="{red_job}">failed job</a></del>')), 'no "Red before:"'))
    expect('a section written as "Regression tests" counts', judge(body(heading='Regression tests'))[0] == [])
    expect('xUnit console "[FAIL]" and pytest "FAILED" lines count',
           failed_in_log(f'  Boo.X.Y.{method} [FAIL]', method) and failed_in_log(f'FAILED t/x.py::{method} - E', method))
    expect('a "Passed" line or a longer method name does not count',
           not failed_in_log(f'  Passed Boo.X.Y.{method} [1 ms]', method)
           and not failed_in_log(f'  Failed Boo.X.Y.{method}_2 [1 ms]', method))
    with tempfile.TemporaryDirectory() as tmp:
        event = pathlib.Path(tmp) / 'event.json'
        event.write_text(json.dumps({'pull_request': {'title': 'fix: x', 'body': 'b', 'base': {'sha': base},
                                                      'head': {'sha': head}}}), encoding='utf-8')
        expect('a body GitHub cannot render fails closed',
               run_event(event, repo, _FakeGitHub(api, broken=True), git, out=lambda *_: None) == 1)
        event.write_text('{"push": {}}', encoding='utf-8')
        expect('an event without a pull request fails closed', run_event(event, repo, gh, git, out=lambda *_: None) == 1)
    failed = [n for n, ok in results if not ok]
    for n in failed:
        out(f'  FAILED: {n}')
    out(f'BUG_REGRESSION_SELF_TEST_{"FAILED" if failed else "PASSED"} {len(results) - len(failed)} of {len(results)}')
    return 1 if failed else 0


def main(argv):
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    repo = os.environ.get('GITHUB_REPOSITORY', 'vikas53953/bluey')
    if argv == ['--self-test']:
        return self_test()
    if len(argv) == 2 and argv[0] == '--event':
        return run_event(argv[1], repo, GitHub(repo, os.environ.get('GITHUB_TOKEN')), Git())
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
