"""BOO-LIVE-004 daily live sentinel helper (docs/quality/live-sentinel.md). Never reads or prints a key.

  python -I scripts/ci/live_sentinel.py --self-test
  python -I scripts/ci/live_sentinel.py trx DIR
      exactly one test ran in DIR's .trx files and it passed (the run is bounded to one real call per step)
  python -I scripts/ci/live_sentinel.py classify --job-status S [--outcome ID=OUTCOME]... [--models-http CODE]
                                         [--log ID=FILE]...
      prints `failures=<id>:<class>;...` (empty when nothing failed); appends it to $GITHUB_OUTPUT when set
  python -I scripts/ci/live_sentinel.py report --result R --failures F --run-url U --sha SHA --repo OWNER/NAME [--dry-run]
      on failure: open or update ONE issue titled TITLE; on success after a failure: comment and close it

What reaches the issue is built only from fixed words in this file (step names, error classes), a run link, a commit SHA
and a date, each checked against a strict pattern. Log files are read only to pick an error class from a fixed list;
no log text, provider reply or key is ever copied into the output.
"""
import datetime
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

RULE = 'BOO-LIVE-004'
TITLE = 'Live sentinel: real OpenAI round trip failing'
DOC = 'docs/quality/live-sentinel.md'

# Step ids, in the order the workflow runs them, with the plain name the issue shows.
STEPS = {
    'runner': 'Runner check (GitHub-hosted only)',
    'key': 'OpenAI key is present',
    'build': 'Build and publish Boo.exe from the lane/face tip',
    'models': 'OpenAI models exist (free check, before any paid call)',
    'hosts': 'AI hosts are real (no redirect to a mock)',
    'typed': 'Typed question round trip (real OpenAI chat)',
    'typed_gate': 'Typed round trip ran exactly once and passed',
    'voice': 'Spoken question round trip (real OpenAI voice)',
    'voice_gate': 'Voice round trip ran exactly once and passed',
    'keyscan': 'Key scan before upload',
    'upload': 'Upload evidence',
    'setup': 'Job setup (checkout, tools) or a step without an id',
}

# Error classes and what each means in plain words.
CLASSES = {
    'runner-not-github-hosted': 'the job was not on a GitHub-hosted runner, so it refused to run',
    'key-missing-or-malformed': 'the BOO_LIVE_OPENAI_KEY secret is missing, empty or not key-shaped',
    'auth-rejected': 'OpenAI rejected the key (HTTP 401/403): revoked, rotated or wrong project',
    'model-unavailable': 'a model is retired, renamed or not enabled for this key (HTTP 404 or model_not_found)',
    'quota-or-billing': 'OpenAI refused for quota or billing (insufficient_quota); check the spend cap and billing',
    'rate-limited': 'OpenAI rate-limited the call (HTTP 429)',
    'provider-server-error': 'OpenAI returned a server error (HTTP 5xx)',
    'network-error': 'the call never got an HTTP answer (DNS, network or TLS)',
    'provider-http-error': 'OpenAI returned another HTTP error',
    'ai-host-redirected': 'an AI host was redirected or did not resolve, so no mock could answer',
    'build-failed': 'Boo.exe or the live test did not build on the lane/face tip',
    'guard-refused': 'a live-test guard failed closed (runner, key, proxy or host check)',
    'timeout-no-answer': 'no answer arrived in time',
    'answer-check-failed': 'an answer arrived but did not pass the check, or the failure did not match a known class',
    'test-did-not-run': 'the round trip did not run exactly one test that passed',
    'key-found-in-evidence': 'the key scan found a key-shaped string in the evidence, so nothing was uploaded',
    'upload-failed': 'the evidence upload failed',
    'timeout-or-cancelled': 'the step timed out or the run was cancelled',
    'unknown': 'the job failed before any checked step, or in a step without an id',
}

FIXED_CLASS = {'runner': 'runner-not-github-hosted', 'key': 'key-missing-or-malformed', 'build': 'build-failed',
               'hosts': 'ai-host-redirected', 'typed_gate': 'test-did-not-run', 'voice_gate': 'test-did-not-run',
               'keyscan': 'key-found-in-evidence', 'upload': 'upload-failed', 'setup': 'unknown'}

# Log patterns, checked in this order (specific provider errors before the generic timeout).
LOG_PATTERNS = [
    ('quota-or-billing', r'insufficient_quota|exceeded your current quota|billing_hard_limit|billing'),
    ('auth-rejected', r'invalid_api_key|incorrect api key|\b401\b|\b403\b|unauthori[sz]ed|forbidden'),
    ('model-unavailable', r'model_not_found|model[^\n]{0,80}does not exist|\b404\b'),
    ('rate-limited', r'\b429\b|rate[ _-]?limit'),
    ('provider-server-error', r'\b50[0-4]\b|internal server error|bad gateway|service unavailable'),
    ('guard-refused', r'failed closed'),
    ('network-error', r'no such host|name or service not known|could not resolve|connection refused|ssl|tls'),
    ('timeout-no-answer', r'timed? ?out|timeoutexception|no answer from|within \d+ ?s\b'),
]

RUN_URL = re.compile(r'^https://github\.com/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+/actions/runs/\d+(/attempts/\d+)?$')
REPO = re.compile(r'^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$')
SHA = re.compile(r'^[0-9a-f]{40}$')
FAILURE = re.compile(r'^[a-z_]+:[a-z-]+$')


# ---------------------------------------------------------------- trx

def trx_counts(folder):
    """(total, executed, passed) summed over every .trx under folder."""
    total = executed = passed = 0
    for f in sorted(pathlib.Path(folder).rglob('*.trx')):
        root = ET.parse(f).getroot()
        for c in root.iter():
            if c.tag.endswith('Counters'):
                total += int(c.get('total', 0))
                executed += int(c.get('executed', 0))
                passed += int(c.get('passed', 0))
    return total, executed, passed


def trx_check(folder, out=print):
    try:
        total, executed, passed = trx_counts(folder)
    except (OSError, ET.ParseError, ValueError) as e:
        out(f'::error::{RULE} could not read the test results ({type(e).__name__})')
        return 1
    if (total, executed, passed) != (1, 1, 1):
        out(f'::error::{RULE} expected exactly 1 test run and passed; got total={total} executed={executed} '
            f'passed={passed}')
        return 1
    out(f'{RULE} one test ran and passed')
    return 0


# ---------------------------------------------------------------- classify

def class_from_http(code):
    code = (code or '').strip()
    if not code.isdigit() or code == '0':
        return 'network-error'
    n = int(code)
    if n in (401, 403):
        return 'auth-rejected'
    if n == 404:
        return 'model-unavailable'
    if n == 429:
        return 'quota-or-billing'
    if 500 <= n <= 599:
        return 'provider-server-error'
    return 'provider-http-error'


def class_from_log(text):
    low = (text or '').lower()
    for name, pattern in LOG_PATTERNS:
        if re.search(pattern, low):
            return name
    return 'answer-check-failed'


def classify(outcomes, job_status, models_http='', logs=None):
    """outcomes: [(id, outcome)] in run order. logs: {id: text}. Returns [(id, class)] for every failed step."""
    logs = logs or {}
    failed = []
    for sid, outcome in outcomes:
        if sid not in STEPS:
            continue
        if outcome == 'cancelled':
            failed.append((sid, 'timeout-or-cancelled'))
        elif outcome == 'failure':
            if sid == 'models':
                failed.append((sid, class_from_http(models_http)))
            elif sid in ('typed', 'voice'):
                failed.append((sid, class_from_log(logs.get(sid, ''))))
            else:
                failed.append((sid, FIXED_CLASS[sid]))
    if not failed and job_status not in ('success', ''):
        failed.append(('setup', 'timeout-or-cancelled' if job_status == 'cancelled' else 'unknown'))
    return failed


def format_failures(failed):
    return ';'.join(f'{s}:{c}' for s, c in failed)


def parse_failures(text):
    """Only well-formed id:class pairs from the fixed lists survive; anything else becomes setup:unknown."""
    out = []
    for part in (text or '').split(';'):
        part = part.strip()
        if not part:
            continue
        if FAILURE.match(part):
            sid, cls = part.split(':')
            if sid in STEPS and cls in CLASSES:
                out.append((sid, cls))
                continue
        out.append(('setup', 'unknown'))
    return out


# ---------------------------------------------------------------- issue text

def failure_body(failed, run_url, sha, date):
    rows = '\n'.join(f'| {STEPS[s]} | `{c}`: {CLASSES[c]} |' for s, c in failed) or \
        f'| {STEPS["setup"]} | `unknown`: {CLASSES["unknown"]} |'
    return (f'The daily live sentinel ({RULE}) could not finish a real OpenAI round trip on the `lane/face` tip.\n\n'
            f'- Date (UTC): {date}\n- Run: {run_url}\n- Tested commit: {sha}\n\n'
            f'| Step that failed | Error class |\n| --- | --- |\n{rows}\n\n'
            f'The error class is picked from a fixed list; open the run for the details. Never paste the key or a raw '
            f'provider reply here. What to do: `{DOC}`.\n')


def success_comment(run_url, sha, date):
    return (f'Passed again on {date} (UTC): {run_url} (commit {sha}). The real OpenAI round trip works on the '
            f'`lane/face` tip, so the sentinel closes this issue.')


def plan(result, open_issues, failed, run_url, sha, date):
    """The issue actions for one run. open_issues: [number] with the exact TITLE, oldest first.
    Returns [(action, number_or_None, text)] with action in create / comment / close."""
    target = open_issues[0] if open_issues else None
    if result == 'failure':
        body = failure_body(failed, run_url, sha, date)
        return [('comment', target, body)] if target else [('create', None, body)]
    if result == 'success' and target:
        return [('comment', target, success_comment(run_url, sha, date)), ('close', target, '')]
    return []


def gh(*args):
    return subprocess.run(['gh', *args], check=True, capture_output=True, text=True).stdout


def open_issue_numbers(repo):
    data = json.loads(gh('issue', 'list', '--repo', repo, '--state', 'open', '--limit', '500',
                         '--json', 'number,title'))
    return sorted(i['number'] for i in data if i.get('title') == TITLE)


def report(args, out=print):
    result = args.get('--result', '')
    run_url, sha, repo = args.get('--run-url', ''), args.get('--sha', ''), args.get('--repo', '')
    if not RUN_URL.match(run_url) or not REPO.match(repo):
        out(f'::error::{RULE} report: bad run link or repository name')
        return 2
    sha = sha if SHA.match(sha) else 'unknown'
    if result not in ('success', 'failure'):
        out(f'{RULE} report: job result is {result!r}; the issue is left as it is')
        return 0
    date = datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%d')
    failed = parse_failures(args.get('--failures', ''))
    existing = [] if '--dry-run' in args else open_issue_numbers(repo)
    for action, number, text in plan(result, existing, failed, run_url, sha, date):
        out(f'{RULE} issue action: {action} {number or ""}'.rstrip())
        if '--dry-run' in args:
            out(text)
            continue
        with tempfile.NamedTemporaryFile('w', suffix='.md', delete=False, encoding='utf-8') as f:
            f.write(text)
            path = f.name
        try:
            if action == 'create':
                out(gh('issue', 'create', '--repo', repo, '--title', TITLE, '--body-file', path).strip())
            elif action == 'comment':
                out(gh('issue', 'comment', str(number), '--repo', repo, '--body-file', path).strip())
            elif action == 'close':
                gh('issue', 'close', str(number), '--repo', repo)
        finally:
            os.unlink(path)
    return 0


# ---------------------------------------------------------------- self-test

def self_test(out=print):
    bad = []

    def check(name, cond):
        if not cond:
            bad.append(name)

    key = 'sk-proj-' + 'Ab3' * 20
    raw = f'HTTP 401 Incorrect API key provided: {key}. Body: {{"error": {{"code": "invalid_api_key"}}}}'
    check('log 401 -> auth', class_from_log(raw) == 'auth-rejected')
    check('quota wins over timeout', class_from_log("No answer from OpenAI within 150s. status: 'insufficient_quota'")
          == 'quota-or-billing')
    check('model_not_found', class_from_log('error code model_not_found') == 'model-unavailable')
    check('429 -> rate', class_from_log('HTTP 429 Too Many Requests') == 'rate-limited')
    check('plain timeout', class_from_log("No answer from OpenAI within 150s. status: 'Thinking'") ==
          'timeout-no-answer')
    check('guard', class_from_log('LIVE SMOKE FAILED CLOSED: proxy in the way') == 'guard-refused')
    check('assert only', class_from_log("Assert.True() Failure: annotation empty") == 'answer-check-failed')
    check('http 404', class_from_http('404') == 'model-unavailable')
    check('http blank', class_from_http('') == 'network-error')
    check('http 503', class_from_http('503') == 'provider-server-error')
    check('http 401', class_from_http('401') == 'auth-rejected')

    ok = classify([('runner', 'success'), ('key', 'success'), ('models', 'success'), ('typed', 'success')], 'success')
    check('all green -> no failures', ok == [])
    red = classify([('models', 'failure'), ('typed', 'skipped')], 'failure', models_http='404')
    check('forced bad model -> models:model-unavailable', red == [('models', 'model-unavailable')])
    both = classify([('typed', 'failure'), ('typed_gate', 'failure'), ('voice', 'failure')], 'failure',
                    logs={'typed': raw, 'voice': 'insufficient_quota'})
    check('typed + voice classes', both == [('typed', 'auth-rejected'), ('typed_gate', 'test-did-not-run'),
                                            ('voice', 'quota-or-billing')])
    check('cancelled', classify([('typed', 'cancelled')], 'cancelled') == [('typed', 'timeout-or-cancelled')])
    check('setup failure', classify([('runner', 'success')], 'failure') == [('setup', 'unknown')])
    check('unknown step ignored', classify([('evil', 'failure')], 'success') == [])

    # Round trip through the job output, and planted junk in it.
    text = format_failures(both)
    check('round trip', parse_failures(text) == both)
    planted = parse_failures(f'typed:{key};voice:quota-or-billing;x:y;typed:not-a-class;[link](http://e)')
    check('junk -> setup:unknown, key never kept', planted[1] == ('voice', 'quota-or-billing') and
          all(p == ('setup', 'unknown') for i, p in enumerate(planted) if i != 1) and key not in str(planted))

    run = 'https://github.com/o/r/actions/runs/123'
    sha = 'a' * 40
    body = failure_body(both, run, sha, '2026-11-14')
    check('body has run, date, step names, classes', all(s in body for s in (run, '2026-11-14', sha,
                                                                             STEPS['typed'], 'quota-or-billing')))
    check('body never carries the key or raw text', key not in body and 'Incorrect API key' not in body)
    check('failure, no issue -> create', [a for a, _, _ in plan('failure', [], both, run, sha, 'd')] == ['create'])
    check('failure, issue open -> comment on oldest', [(a, n) for a, n, _ in plan('failure', [7, 9], both, run, sha,
                                                                                   'd')] == [('comment', 7)])
    check('success after failure -> comment + close', [(a, n) for a, n, _ in plan('success', [7], [], run, sha, 'd')]
          == [('comment', 7), ('close', 7)])
    check('success, nothing open -> nothing', plan('success', [], [], run, sha, 'd') == [])
    check('cancelled -> nothing', plan('cancelled', [7], [], run, sha, 'd') == [])

    lines = []
    check('report refuses a bad run link', report({'--result': 'failure', '--run-url': 'https://evil.example/x',
                                                   '--repo': 'o/r', '--dry-run': ''}, out=lines.append) == 2)
    lines = []
    report({'--result': 'failure', '--run-url': run, '--repo': 'o/r', '--sha': key,
            '--failures': f'typed:{key}', '--dry-run': ''}, out=lines.append)
    check('dry-run report: planted key in sha and failures never printed', key not in '\n'.join(lines) and
          'issue action: create' in '\n'.join(lines))

    with tempfile.TemporaryDirectory() as d:
        def trx(name, total, executed, passed):
            pathlib.Path(d, name).write_text(
                '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary>'
                f'<Counters total="{total}" executed="{executed}" passed="{passed}" failed="0"/></ResultSummary>'
                '</TestRun>', encoding='utf-8')
        quiet = []
        check('no trx -> fail', trx_check(d, out=quiet.append) == 1)
        trx('a.trx', 1, 1, 1)
        check('one passed -> ok', trx_check(d, out=quiet.append) == 0)
        trx('b.trx', 1, 1, 0)
        check('two runs -> fail', trx_check(d, out=quiet.append) == 1)
        pathlib.Path(d, 'b.trx').unlink()
        trx('a.trx', 1, 0, 0)
        check('not executed -> fail', trx_check(d, out=quiet.append) == 1)
        pathlib.Path(d, 'a.trx').write_text('<not xml', encoding='utf-8')
        check('broken trx -> fail', trx_check(d, out=quiet.append) == 1)

    for b in bad:
        out(f'SELF_TEST_FAILED: {b}')
    out(f'{RULE} self-test: {"FAILED " + str(len(bad)) if bad else "passed"} ({len(bad)} failure(s))')
    return 1 if bad else 0


# ---------------------------------------------------------------- CLI

def parse_args(argv):
    """--name value pairs (repeatable ones collected in lists) and bare flags."""
    args, multi, i = {}, {'--outcome': [], '--log': []}, 0
    while i < len(argv):
        a = argv[i]
        if a in ('--dry-run',):
            args[a] = ''
            i += 1
        elif a.startswith('--') and i + 1 < len(argv):
            if a in multi:
                multi[a].append(argv[i + 1])
            else:
                args[a] = argv[i + 1]
            i += 2
        else:
            raise SystemExit(f'{RULE}: unexpected argument {a!r}')
    args.update(multi)
    return args


def main(argv):
    if argv == ['--self-test']:
        return self_test()
    if len(argv) == 2 and argv[0] == 'trx':
        return trx_check(argv[1])
    if argv and argv[0] == 'classify':
        args = parse_args(argv[1:])
        outcomes = [tuple(o.split('=', 1)) for o in args['--outcome'] if '=' in o]
        logs = {}
        for item in args['--log']:
            sid, _, path = item.partition('=')
            try:
                logs[sid] = pathlib.Path(path).read_text(encoding='utf-8', errors='replace')
            except OSError:
                logs[sid] = ''
        failed = classify(outcomes, args.get('--job-status', ''), args.get('--models-http', ''), logs)
        line = 'failures=' + format_failures(failed)
        print(line)
        if os.environ.get('GITHUB_OUTPUT'):
            with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as f:
                f.write(line + '\n')
        return 0
    if argv and argv[0] == 'report':
        return report(parse_args(argv[1:]))
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
