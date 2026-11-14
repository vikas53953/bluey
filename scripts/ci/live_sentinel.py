"""BOO-LIVE-004 daily live sentinel helper (docs/quality/live-sentinel.md). Never reads or prints a key.

  python -I scripts/ci/live_sentinel.py --self-test
  python -I scripts/ci/live_sentinel.py trx DIR --require ID --receipt FILE
      DIR's .trx files hold exactly one test result, it is the named required test ID (typed or voice) and it passed;
      only then is FILE written (the receipt: the test's full name). The run is bounded to one real call per step.
  python -I scripts/ci/live_sentinel.py classify --job-status S [--outcome ID=OUTCOME]... [--models-http CODE]
                                         [--log ID=FILE]... [--receipt-of ID=FILE]...
      prints `failures=<id>:<class>;...` and `receipts=<id>,...`, appends both to $GITHUB_OUTPUT when set, and exits 1
      unless every required test has a valid receipt and nothing failed
  python -I scripts/ci/live_sentinel.py report --result R --failures F --receipts C --run-url U --sha SHA
                                       --repo OWNER/NAME [--dry-run]
      green only with result=success, no failures AND a receipt for every required test: then it comments on and closes
      the open issue. Anything else (result failure, or success without both receipts) opens or updates ONE issue.

Green needs POSITIVE receipts from every required test (REQUIRED below), never "no failure seen": a missing test file,
a skipped step, zero tests, two tests or another test all fail closed as `required-test-missing`, naming the test.

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

# Every required live test, by id: the full test name that must run exactly once and pass. The sentinel is green only
# with a receipt from each of them.
REQUIRED = {
    'typed': 'Boo.LiveSmoke.LiveRoundTripTests.TypedQuestion_GetsAnAnswerFromTheRealProvider',
    'voice': 'Boo.LiveSmoke.LiveVoiceRoundTripTests.SpokenQuestion_GetsASpokenAnswerFromRealOpenAiVoice',
}

# Step ids, in the order the workflow runs them, with the plain name the issue shows.
STEPS = {
    'runner': 'Runner check (GitHub-hosted only)',
    'key': 'OpenAI key is present',
    'models': 'OpenAI models exist (free check, before any paid call)',
    'hosts': 'AI hosts are real (no redirect to a mock)',
    'build': 'Build and publish Boo.exe from the lane/face tip',
    'typed': 'Typed question round trip (real OpenAI chat), required test: ' + REQUIRED['typed'],
    'voice': 'Spoken question round trip (real OpenAI voice), required test: ' + REQUIRED['voice'],
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
    'required-test-missing': ('this required test did not run exactly once and pass: missing from the tip, skipped, '
                              'zero tests, more than one, or a different test'),
    'key-scan-failed': 'the key scan found a key-shaped string in the evidence or could not run, so nothing was uploaded',
    'upload-failed': 'the evidence upload failed',
    'timeout-or-cancelled': 'the step timed out or the run was cancelled',
    'unknown': 'the job failed before any checked step, or in a step without an id',
}

FIXED_CLASS = {'runner': 'runner-not-github-hosted', 'key': 'key-missing-or-malformed', 'build': 'build-failed',
               'hosts': 'ai-host-redirected', 'keyscan': 'key-scan-failed', 'upload': 'upload-failed',
               'setup': 'unknown'}

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


# ---------------------------------------------------------------- trx and receipts

def trx_results(folder):
    """[(testName, outcome)] over every .trx under folder."""
    out = []
    for f in sorted(pathlib.Path(folder).rglob('*.trx')):
        for e in ET.parse(f).getroot().iter():
            if e.tag.endswith('UnitTestResult'):
                out.append((e.get('testName', ''), e.get('outcome', '')))
    return out


# Counters that must be zero in a clean run (issue #128): any of them means the run did not simply execute and pass.
BAD_COUNTERS = ('failed', 'error', 'timeout', 'aborted', 'inconclusive', 'passedButRunAborted', 'notRunnable',
                'notExecuted', 'disconnected', 'inProgress', 'pending')


def trx_run_problems(folder):
    """Reasons the .trx run summaries are not one clean run of exactly one passed test ([] when clean).

    A passed UnitTestResult is not enough: the run itself must have completed (issue #128)."""
    problems = []
    files = sorted(pathlib.Path(folder).rglob('*.trx'))
    for f in files:
        summaries = [e for e in ET.parse(f).getroot().iter() if e.tag.endswith('ResultSummary')]
        if len(summaries) != 1:
            problems.append(f'{f.name}: {len(summaries)} run summaries (expected exactly 1)')
            continue
        outcome = summaries[0].get('outcome', '')
        if outcome != 'Completed':
            problems.append(f'{f.name}: run outcome {outcome or "?"} (expected Completed)')
        counters = [e for e in summaries[0].iter() if e.tag.endswith('Counters')]
        if len(counters) != 1:
            problems.append(f'{f.name}: {len(counters)} counter blocks (expected exactly 1)')
            continue
        c = counters[0]

        def num(name):
            try:
                return int(c.get(name, '0') or '0')
            except ValueError:
                return -1
        for name in ('total', 'executed', 'passed'):
            if num(name) != 1:
                problems.append(f'{f.name}: {name}={c.get(name, "missing")} (expected 1)')
        for name in BAD_COUNTERS:
            if num(name) != 0:
                problems.append(f'{f.name}: {name}={c.get(name)} (expected 0)')
    return problems


def trx_check(folder, test_id, receipt, out=print):
    """Write the receipt only if exactly one result exists, it is REQUIRED[test_id], and it passed."""
    name = REQUIRED.get(test_id)
    if name is None:
        out(f'::error::{RULE} unknown required test id {test_id!r}')
        return 1
    try:
        results = trx_results(folder) if pathlib.Path(folder).is_dir() else []
    except (OSError, ET.ParseError) as e:
        out(f'::error::{RULE} required-test-missing ({test_id}): could not read the test results ({type(e).__name__})')
        return 1
    if results != [(name, 'Passed')]:
        seen = ', '.join(f'{n or "?"}={o or "?"}' for n, o in results[:5]) or 'no test ran'
        out(f'::error::{RULE} required-test-missing ({test_id}): expected exactly {name}=Passed; got {seen}')
        return 1
    try:
        run_problems = trx_run_problems(folder)
    except (OSError, ET.ParseError) as e:
        out(f'::error::{RULE} required-test-missing ({test_id}): could not read the run summary ({type(e).__name__})')
        return 1
    if run_problems:
        out(f'::error::{RULE} required-test-missing ({test_id}): {name} passed, but the test run was not one clean '
            f'completed run: ' + '; '.join(run_problems[:5]))
        return 1
    pathlib.Path(receipt).parent.mkdir(parents=True, exist_ok=True)
    pathlib.Path(receipt).write_text(name, encoding='utf-8')
    out(f'{RULE} receipt ({test_id}): {name} ran once and passed')
    return 0


def valid_receipts(receipt_texts):
    """{id: text or None} -> ids whose receipt names exactly the required test."""
    return [t for t in REQUIRED if (receipt_texts.get(t) or '').strip() == REQUIRED[t]]


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


def classify(outcomes, job_status, models_http='', logs=None, receipts=()):
    """outcomes: [(id, outcome)] in run order; logs: {id: text}; receipts: ids with a valid receipt.
    Returns [(id, class)]. Every required test without a receipt is a failure, whatever its step reported."""
    logs, outcomes, receipts = logs or {}, list(outcomes), set(receipts)
    state = dict(outcomes)
    failed = []
    for sid, outcome in outcomes:
        if sid not in STEPS or sid in REQUIRED:
            continue
        if outcome == 'cancelled':
            failed.append((sid, 'timeout-or-cancelled'))
        elif outcome == 'failure':
            failed.append((sid, class_from_http(models_http) if sid == 'models' else FIXED_CLASS[sid]))
    for tid in REQUIRED:
        if tid in receipts:
            continue
        outcome = state.get(tid, '')
        if outcome == 'failure':
            failed.append((tid, class_from_log(logs.get(tid, ''))))
        elif outcome == 'cancelled':
            failed.append((tid, 'timeout-or-cancelled'))
        else:                                   # success without a receipt, skipped, or never reached
            failed.append((tid, 'required-test-missing'))
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


def parse_receipts(text):
    return [t for t in REQUIRED if t in {p.strip() for p in (text or '').split(',')}]


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
    return (f'Passed again on {date} (UTC): {run_url} (commit {sha}). Every required live test ran once and passed on '
            f'the `lane/face` tip ({", ".join(REQUIRED)}), so the sentinel closes this issue.')


def verdict(result, failed, receipts):
    """('green', []) only with result success, no failures and a receipt from every required test; ('red', failures)
    when the run finished otherwise; ('none', []) for a cancelled or skipped run (the issue is left as it is)."""
    if result not in ('success', 'failure'):
        return 'none', []
    missing = [(t, 'required-test-missing') for t in REQUIRED
               if t not in receipts and not any(s == t for s, _ in failed)]
    if result == 'success' and not failed and not missing:
        return 'green', []
    return 'red', failed + missing


def plan(result, open_issues, failed, receipts, run_url, sha, date):
    """The issue actions for one run. open_issues: [number] with the exact TITLE, oldest first.
    Returns [(action, number_or_None, text)] with action in create / comment / close."""
    kind, failures = verdict(result, failed, receipts)
    target = open_issues[0] if open_issues else None
    if kind == 'red':
        body = failure_body(failures, run_url, sha, date)
        return [('comment', target, body)] if target else [('create', None, body)]
    if kind == 'green' and target:
        return [('comment', target, success_comment(run_url, sha, date)), ('close', target, '')]
    return []


def gh(*args):
    return subprocess.run(['gh', *args], check=True, capture_output=True, text=True).stdout


def open_issue_numbers(repo):
    data = json.loads(gh('issue', 'list', '--repo', repo, '--state', 'open', '--limit', '500',
                         '--json', 'number,title'))
    return sorted(i['number'] for i in data if i.get('title') == TITLE)


def report(args, out=print, existing=None):
    result = args.get('--result', '')
    run_url, sha, repo = args.get('--run-url', ''), args.get('--sha', ''), args.get('--repo', '')
    if not RUN_URL.match(run_url) or not REPO.match(repo):
        out(f'::error::{RULE} report: bad run link or repository name')
        return 2
    sha = sha if SHA.match(sha) else 'unknown'
    failed = parse_failures(args.get('--failures', ''))
    receipts = parse_receipts(args.get('--receipts', ''))
    kind, _ = verdict(result, failed, receipts)
    out(f'{RULE} verdict: {kind} (job result {result!r}; receipts: {", ".join(receipts) or "none"})')
    if kind == 'none':
        out(f'{RULE} report: the issue is left as it is')
        return 0
    date = datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%d')
    if existing is None:
        existing = [] if '--dry-run' in args else open_issue_numbers(repo)
    actions = plan(result, existing, failed, receipts, run_url, sha, date)
    for action, number, text in actions:
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
    return 1 if kind == 'red' else 0


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

    both = ('typed', 'voice')
    green_steps = [('runner', 'success'), ('key', 'success'), ('models', 'success'), ('build', 'success'),
                   ('typed', 'success'), ('voice', 'success')]
    check('all green + both receipts -> no failures', classify(green_steps, 'success', receipts=both) == [])
    check('voice receipt missing, typed passed -> voice:required-test-missing',
          classify(green_steps, 'success', receipts=('typed',)) == [('voice', 'required-test-missing')])
    voice_skipped = green_steps[:-1] + [('voice', 'skipped')]
    check('voice skipped -> voice:required-test-missing',
          classify(voice_skipped, 'success', receipts=('typed',)) == [('voice', 'required-test-missing')])
    check('typed missing -> typed:required-test-missing',
          classify(green_steps, 'success', receipts=('voice',)) == [('typed', 'required-test-missing')])
    skipped = green_steps[:4] + [('typed', 'skipped'), ('voice', 'skipped')]
    check('both skipped -> both required-test-missing', classify(skipped, 'success') ==
          [('typed', 'required-test-missing'), ('voice', 'required-test-missing')])
    check('no outcomes at all -> both missing', classify([], 'success') ==
          [('typed', 'required-test-missing'), ('voice', 'required-test-missing')])
    red = classify([('models', 'failure'), ('typed', 'skipped'), ('voice', 'skipped')], 'failure', models_http='404')
    check('forced bad model -> models:model-unavailable + both missing', red ==
          [('models', 'model-unavailable'), ('typed', 'required-test-missing'), ('voice', 'required-test-missing')])
    mixed = classify([('typed', 'failure'), ('voice', 'failure')], 'failure',
                     logs={'typed': raw, 'voice': 'insufficient_quota'})
    check('typed + voice classes from logs', mixed == [('typed', 'auth-rejected'), ('voice', 'quota-or-billing')])
    check('cancelled', classify([('typed', 'cancelled')], 'cancelled', receipts=('voice',)) ==
          [('typed', 'timeout-or-cancelled')])
    check('setup failure', classify([('runner', 'success')], 'failure', receipts=both) == [('setup', 'unknown')])
    check('unknown step ignored', classify([('evil', 'failure')], 'success', receipts=both) == [])
    check('receipt text must name the exact test', valid_receipts({'typed': REQUIRED['typed'], 'voice': 'Other.Test'})
          == ['typed'])
    check('receipt list keeps only known ids', parse_receipts(f'typed, voice,{key},x') == ['typed', 'voice'])

    # Round trip through the job output, and planted junk in it.
    text = format_failures(mixed)
    check('round trip', parse_failures(text) == mixed)
    planted = parse_failures(f'typed:{key};voice:quota-or-billing;x:y;typed:not-a-class;[link](http://e)')
    check('junk -> setup:unknown, key never kept', planted[1] == ('voice', 'quota-or-billing') and
          all(p == ('setup', 'unknown') for i, p in enumerate(planted) if i != 1) and key not in str(planted))

    run = 'https://github.com/o/r/actions/runs/123'
    sha = 'a' * 40
    body = failure_body(mixed, run, sha, '2026-11-14')
    check('body has run, date, step names, classes', all(s in body for s in (run, '2026-11-14', sha,
                                                                             STEPS['typed'], 'quota-or-billing')))
    check('body never carries the key or raw text', key not in body and 'Incorrect API key' not in body)

    def acts(result, issues, failed, receipts):
        return [(a, n) for a, n, _ in plan(result, issues, failed, receipts, run, sha, 'd')]

    check('failure, no issue -> create', acts('failure', [], mixed, ()) == [('create', None)])
    check('failure, issue open -> comment on oldest', acts('failure', [7, 9], mixed, ()) == [('comment', 7)])
    check('both pass -> comment + close', acts('success', [7], [], both) == [('comment', 7), ('close', 7)])
    check('voice missing + typed pass -> red, comment, NOT close', acts('success', [7], [], ('typed',)) ==
          [('comment', 7)])
    check('voice missing + typed pass, nothing open -> create', acts('success', [], [], ('typed',)) ==
          [('create', None)])
    check('typed missing -> red, not close', acts('success', [7], [], ('voice',)) == [('comment', 7)])
    check('both skipped (no receipts) -> red, not close', acts('success', [7], [], ()) == [('comment', 7)])
    check('job success but a failure listed -> red', acts('success', [7], [('upload', 'upload-failed')], both) ==
          [('comment', 7)])
    check('voice-missing body names voice', 'required-test-missing' in
          plan('success', [7], [], ('typed',), run, sha, 'd')[0][2] and REQUIRED['voice'] in
          plan('success', [7], [], ('typed',), run, sha, 'd')[0][2])
    check('success, nothing open, both receipts -> nothing', acts('success', [], [], both) == [])
    check('cancelled -> nothing', acts('cancelled', [7], [], both) == [])

    lines = []
    check('report refuses a bad run link', report({'--result': 'failure', '--run-url': 'https://evil.example/x',
                                                   '--repo': 'o/r', '--dry-run': ''}, out=lines.append) == 2)
    lines = []
    rc = report({'--result': 'failure', '--run-url': run, '--repo': 'o/r', '--sha': key,
                 '--failures': f'typed:{key}', '--dry-run': ''}, out=lines.append)
    check('dry-run report: planted key in sha and failures never printed', key not in '\n'.join(lines) and
          'issue action: create' in '\n'.join(lines) and rc == 1)
    lines = []
    rc = report({'--result': 'success', '--run-url': run, '--repo': 'o/r', '--sha': sha, '--failures': '',
                 '--receipts': 'typed', '--dry-run': ''}, out=lines.append, existing=[5])
    check('report: typed-only success comments, never closes, exits 1', rc == 1 and
          'issue action: comment 5' in '\n'.join(lines) and 'issue action: close' not in '\n'.join(lines))
    lines = []
    rc = report({'--result': 'success', '--run-url': run, '--repo': 'o/r', '--sha': sha, '--failures': '',
                 '--receipts': 'typed,voice', '--dry-run': ''}, out=lines.append, existing=[5])
    check('report: both receipts close the issue', rc == 0 and 'issue action: close 5' in '\n'.join(lines))

    with tempfile.TemporaryDirectory() as d:
        receipt = pathlib.Path(d, 'r', 'receipt.txt')

        def trx(name, results, summary='Completed', counters=None, summaries=1, counter_blocks=1):
            """A .trx shaped like the one `dotnet test --logger trx` writes: results, then ONE run summary whose
            counters agree with them (unless a case overrides them to plant a defect)."""
            rows = ''.join(f'<UnitTestResult testName="{n}" outcome="{o}"/>' for n, o in results)
            passed = sum(1 for _, o in results if o == 'Passed')
            c = {'total': len(results), 'executed': len(results), 'passed': passed,
                 'failed': len(results) - passed, 'error': 0, 'timeout': 0, 'aborted': 0, 'inconclusive': 0,
                 'passedButRunAborted': 0, 'notRunnable': 0, 'notExecuted': 0, 'disconnected': 0, 'warning': 0,
                 'completed': len(results), 'inProgress': 0, 'pending': 0}
            c.update(counters or {})
            attrs = ' '.join(f'{k}="{v}"' for k, v in c.items())
            block = (f'<ResultSummary outcome="{summary}">' + f'<Counters {attrs}/>' * counter_blocks +
                     '</ResultSummary>') * summaries
            pathlib.Path(d, name).write_text(
                '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>'
                f'{rows}</Results>{block}</TestRun>', encoding='utf-8')
        quiet = []
        check('no trx -> fail', trx_check(d, 'typed', receipt, out=quiet.append) == 1 and not receipt.exists())
        check('missing folder -> fail', trx_check(pathlib.Path(d, 'nope'), 'voice', receipt, out=quiet.append) == 1)
        trx('a.trx', [])
        check('zero tests -> fail', trx_check(d, 'typed', receipt, out=quiet.append) == 1 and not receipt.exists())
        trx('a.trx', [(REQUIRED['typed'], 'Passed')])
        check('wrong id for the result -> fail', trx_check(d, 'voice', receipt, out=quiet.append) == 1)
        check('unknown id -> fail', trx_check(d, 'other', receipt, out=quiet.append) == 1)
        check('the named test passed -> receipt', trx_check(d, 'typed', receipt, out=quiet.append) == 0 and
              receipt.read_text(encoding='utf-8') == REQUIRED['typed'])
        receipt.unlink()
        trx('b.trx', [(REQUIRED['typed'], 'Passed')])
        check('two runs -> fail', trx_check(d, 'typed', receipt, out=quiet.append) == 1 and not receipt.exists())
        pathlib.Path(d, 'b.trx').unlink()
        trx('a.trx', [(REQUIRED['typed'], 'Failed')])
        check('failed -> fail', trx_check(d, 'typed', receipt, out=quiet.append) == 1)
        trx('a.trx', [('Boo.LiveSmoke.Other.Test', 'Passed')])
        check('a different test -> fail', trx_check(d, 'typed', receipt, out=quiet.append) == 1)
        # Issue #128: the run summary must agree. A passed result inside an aborted or failed run, or counters that
        # say something else ran, failed or was cut short, is not a receipt. Typed and voice alike.
        for tid in ('typed', 'voice'):
            ok = [(REQUIRED[tid], 'Passed')]
            planted = [
                ('run aborted', dict(summary='Aborted', counters={'passedButRunAborted': 1})),
                ('summary says Aborted, counters clean', dict(summary='Aborted')),
                ('passedButRunAborted counted', dict(counters={'passedButRunAborted': 1})),
                ('summary says Failed', dict(summary='Failed')),
                ('aborted counted', dict(counters={'aborted': 1})),
                ('error counted', dict(counters={'error': 1})),
                ('timeout counted', dict(counters={'timeout': 1})),
                ('not executed counted', dict(counters={'notExecuted': 1})),
                ('counters say 2 ran', dict(counters={'total': 2, 'executed': 2, 'passed': 2})),
                ('counters say 0 passed', dict(counters={'passed': 0})),
                # One case per counter and per rule, so dropping any single one of them is caught (Codex mutation
                # run on #131: these survived when only the cases above existed).
                ('failed counted', dict(counters={'failed': 1})),
                ('inconclusive counted', dict(counters={'inconclusive': 1})),
                ('not runnable counted', dict(counters={'notRunnable': 1})),
                ('disconnected counted', dict(counters={'disconnected': 1})),
                ('in progress counted', dict(counters={'inProgress': 1})),
                ('pending counted', dict(counters={'pending': 1})),
                ('only total says 2', dict(counters={'total': 2})),
                ('only executed says 2', dict(counters={'executed': 2})),
                ('only passed says 2', dict(counters={'passed': 2})),
                ('two counter blocks in one summary', dict(counter_blocks=2)),
                ('no run summary', dict(summaries=0)),
                ('two run summaries', dict(summaries=2)),
            ]
            for label, kw in planted:
                receipt.unlink(missing_ok=True)  # each case judged on its own: no receipt left from an earlier one
                trx('a.trx', ok, **kw)
                rc = trx_check(d, tid, receipt, out=quiet.append)
                check(f'#128 {tid}: {label} -> fail', rc == 1 and not receipt.exists())
            trx('a.trx', ok)
            check(f'#128 {tid}: clean Completed run -> receipt', trx_check(d, tid, receipt, out=quiet.append) == 0)
            receipt.unlink()
        pathlib.Path(d, 'a.trx').write_text('<not xml', encoding='utf-8')
        check('broken trx -> fail', trx_check(d, 'typed', receipt, out=quiet.append) == 1 and not receipt.exists())

    for b in bad:
        out(f'SELF_TEST_FAILED: {b}')
    out(f'{RULE} self-test: {"FAILED " + str(len(bad)) if bad else "passed"} ({len(bad)} failure(s))')
    return 1 if bad else 0


# ---------------------------------------------------------------- CLI

def parse_args(argv):
    """--name value pairs (repeatable ones collected in lists) and bare flags."""
    args, multi, i = {}, {'--outcome': [], '--log': [], '--receipt-of': []}, 0
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


def _read_pairs(items):
    out = {}
    for item in items:
        sid, _, path = item.partition('=')
        try:
            out[sid] = pathlib.Path(path).read_text(encoding='utf-8', errors='replace')
        except OSError:
            out[sid] = None
    return out


def main(argv):
    if argv == ['--self-test']:
        return self_test()
    if argv and argv[0] == 'trx' and len(argv) == 6:
        args = parse_args(argv[2:])
        if '--require' in args and '--receipt' in args:
            return trx_check(argv[1], args['--require'], args['--receipt'])
    if argv and argv[0] == 'classify':
        args = parse_args(argv[1:])
        outcomes = [tuple(o.split('=', 1)) for o in args['--outcome'] if '=' in o]
        logs = {k: v or '' for k, v in _read_pairs(args['--log']).items()}
        receipts = valid_receipts(_read_pairs(args['--receipt-of']))
        failed = classify(outcomes, args.get('--job-status', ''), args.get('--models-http', ''), logs, receipts)
        lines = ['failures=' + format_failures(failed), 'receipts=' + ','.join(receipts)]
        print('\n'.join(lines))
        if os.environ.get('GITHUB_OUTPUT'):
            with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as f:
                f.write('\n'.join(lines) + '\n')
        if failed:
            for sid, cls in failed:
                print(f'::error::{RULE} {cls} ({sid}): {CLASSES[cls]}')
            return 1
        return 0
    if argv and argv[0] == 'report':
        return report(parse_args(argv[1:]))
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
