"""Fail if any GitHub Actions job could run anywhere except GitHub's own hosted VMs. Never reads secrets.

  python -I scripts/ci/check_runners.py [--self-test]

Why: Boo's UI, demo, perf and live tests drive the real mouse and keyboard, take screenshots, and the live smoke sends a
picture of the screen to a real AI. On a self-hosted runner (for example the owner's own PC) that would capture a real
person's screen. So, in every workflow file (checked as plain text, deliberately strict, so YAML tricks fail closed):
  - one top-level block `jobs:`; each job is a plain-word key directly under it;
  - inside each job, every key at the job's own level is a plain word from JOB_KEYS (so no `uses:` reusable workflow,
    no quoted or escaped key such as "runs\\x2Don", no `*alias` key, no `? key`, no `<<:` merge);
  - each job has exactly one `runs-on: <label>` line at its own level, with <label> one literal from ALLOWED;
  - no other line anywhere in the file mentions runs-on, and none says "self-hosted" outside a comment (reword such
    text inside run: scripts too).
Limit: this guards changes that reach windows-ci. The hard guarantee is that the repository has no registered
self-hosted runner (0 on 12 Nov 2026); keep it at 0.
"""
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parent.parent.parent
ALLOWED = {
    'windows-latest', 'windows-2025', 'windows-2022',
    'ubuntu-latest', 'ubuntu-24.04', 'ubuntu-22.04',
}
JOB_KEYS = {
    'name', 'needs', 'runs-on', 'if', 'timeout-minutes', 'env', 'steps', 'permissions', 'concurrency', 'outputs',
    'environment', 'strategy', 'continue-on-error', 'defaults', 'services', 'container',
}
PLAIN_KEY = re.compile(r'^(?P<indent> *)(?P<key>[A-Za-z0-9_-]+):(?:\s|$)')
FIX = ('Fix: give every job one plain line `runs-on: windows-latest` (or ubuntu-latest) and only ordinary job keys. '
       'Never a personal PC, never self-hosted, never an expression, list, quoted key or reusable workflow. '
       'See docs/LIVE_AI_SMOKE.md section 2.')


def strip_comment(line):
    """Drop a YAML comment that starts outside quotes."""
    quote = None
    for index, char in enumerate(line):
        if char in '"\'' and quote in (None, char):
            quote = None if quote == char else char
        elif char == '#' and quote is None and (index == 0 or line[index - 1].isspace()):
            return line[:index]
    return line


def check_text(relative, text):
    """Return (failures, job count) for one workflow file's text."""
    failures = []
    if '\t' in text:
        failures.append(f'{relative}: contains a tab character (YAML indentation must be spaces)')
    lines = [strip_comment(raw).rstrip() for raw in text.replace('\r\n', '\n').split('\n')]
    jobs_lines = [i for i, line in enumerate(lines) if re.match(r'^jobs:\s*$', line)]
    if len(jobs_lines) != 1:
        return failures + [f'{relative}: needs exactly one top-level block `jobs:` line (found {len(jobs_lines)})'], 0
    if any(re.match(r'^\S.*\bjobs\b', line) and i != jobs_lines[0] for i, line in enumerate(lines)):
        failures.append(f'{relative}: another top-level line mentions jobs')
    start = jobs_lines[0] + 1
    end = next((i for i in range(start, len(lines)) if lines[i] and not lines[i].startswith(' ')), len(lines))
    body = [(i, lines[i]) for i in range(start, end) if lines[i].strip()]
    if not body:
        return failures + [f'{relative}: has no jobs'], 0
    job_indent = len(body[0][1]) - len(body[0][1].lstrip(' '))
    jobs, current = [], None
    accounted = set()
    for i, line in body:
        indent = len(line) - len(line.lstrip(' '))
        if indent < job_indent:
            failures.append(f'{relative}:{i + 1}: line is outside any job but inside jobs:')
            continue
        if indent == job_indent:
            match = PLAIN_KEY.match(line)
            if not match or line.split(':', 1)[1].strip():
                failures.append(f'{relative}:{i + 1}: job key must be a plain word followed by a block: {line.strip()}')
            current = {'line': i, 'runs_on': 0}
            jobs.append(current)
            continue
        if indent == job_indent + 2:
            match = PLAIN_KEY.match(line)
            if not match or match.group('key') not in JOB_KEYS:
                failures.append(f'{relative}:{i + 1}: not an allowed plain job key: {line.strip()}')
                continue
            if match.group('key') == 'runs-on':
                label = line.split(':', 1)[1].strip()
                if label not in ALLOWED:
                    failures.append(f'{relative}:{i + 1}: runs-on {label!r} is not one GitHub-hosted label')
                else:
                    current['runs_on'] += 1
                accounted.add(i)
    for job in jobs:
        if job['runs_on'] != 1:
            failures.append(f'{relative}:{job["line"] + 1}: job has {job["runs_on"]} valid runs-on lines; it needs exactly 1')
    for i, line in enumerate(lines):
        if re.search(r'self-hosted', line, re.IGNORECASE):
            failures.append(f'{relative}:{i + 1}: mentions a self-hosted runner: {line.strip()}')
        if 'runs-on' in line and i not in accounted:
            failures.append(f'{relative}:{i + 1}: runs-on outside a job\'s own level: {line.strip()}')
    return failures, len(jobs)


def main():
    workflows = sorted((root / '.github' / 'workflows').glob('*.y*ml'))
    if not workflows:
        print('RUNNERS FAILED: no workflow files found under .github/workflows')
        return 1
    failures, jobs = [], 0
    for workflow in workflows:
        found, count = check_text(workflow.relative_to(root).as_posix(), workflow.read_text(encoding='utf-8-sig'))
        failures += found
        jobs += count
    if failures:
        print('RUNNERS FAILED: a job could run on a machine that is not a GitHub-hosted VM.')
        for failure in failures:
            print('  ' + failure)
        print(FIX)
        return 1
    print(f'RUNNERS OK: {jobs} jobs in {len(workflows)} workflows, all on one GitHub-hosted label each')
    return 0


SELF_TEST_BAD = {
    'self-hosted label': 'jobs:\n  a:\n    runs-on: [self-hosted, windows]\n',
    'expression': "jobs:\n  a:\n    runs-on: ${{ inputs.r }}\n",
    'unknown label': 'jobs:\n  a:\n    runs-on: my-pc\n',
    'quoted key': 'jobs:\n  a:\n    "runs-on": my-pc\n',
    'escaped key': 'jobs:\n  a:\n    runs-on: windows-latest\n  b:\n    "runs\\x2Don": my-pc\n',
    'alias key': 'x: &k "runs\\x2Don"\njobs:\n  a:\n    *k : my-pc\n',
    'decoy in block scalar': 'env:\n  NOTE: |\n    runs-on: ubuntu-latest\njobs:\n  a:\n    runs-on: windows-latest\n'
                             '  b:\n    "runs\\x2Don": my-pc\n',
    'flow mapping': 'jobs: {a: {runs-on: my-pc}}\n',
    'explicit key': 'jobs:\n  a:\n    ? runs-on\n    : my-pc\n',
    'merge key': 'jobs:\n  a:\n    <<: *base\n',
    'reusable workflow': 'jobs:\n  a:\n    uses: org/repo/.github/workflows/x.yml@main\n',
    'escaped reusable workflow': 'jobs:\n  a:\n    "uses": "org/repo/\\x2Egithub/workflows/x.yml@main"\n',
    'job without runs-on': 'jobs:\n  a:\n    runs-on: ubuntu-latest\n  b:\n    steps: []\n',
    'two runs-on in one job': 'jobs:\n  a:\n    runs-on: ubuntu-latest\n    runs-on: windows-latest\n  b:\n    steps: []\n',
    'runs-on deeper than job level': 'jobs:\n  a:\n    runs-on: ubuntu-latest\n    strategy:\n      runs-on: my-pc\n',
    'tab indentation': 'jobs:\n  a:\n\truns-on: my-pc\n',
    'no jobs': 'on: push\n',
}
SELF_TEST_GOOD = ('on: push\n# self-hosted is fine in a comment\njobs:\n  a:\n    runs-on: windows-latest\n'
                  '    timeout-minutes: 5\n    steps:\n      - uses: actions/checkout@v4\n'
                  '      - run: |\n          echo "runs on GitHub"\n  b:\n    needs: a\n    runs-on: ubuntu-latest\r\n')


def self_test():
    problems = [name for name, text in SELF_TEST_BAD.items() if not check_text(name, text)[0]]
    good, _ = check_text('good', SELF_TEST_GOOD)
    if good:
        problems.append('false failure on a good workflow: ' + '; '.join(good))
    if problems:
        print('RUNNERS SELF-TEST FAILED, not caught: ' + ', '.join(problems))
        return 1
    print(f'RUNNERS SELF-TEST OK: {len(SELF_TEST_BAD)} bad shapes caught, good workflow passes')
    return 0


if __name__ == '__main__':
    sys.exit(self_test() if '--self-test' in sys.argv else main())
