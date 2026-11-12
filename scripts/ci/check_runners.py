"""Fail if any GitHub Actions job could run anywhere except GitHub's own hosted VMs. Never reads secrets.

  python -I scripts/ci/check_runners.py [--self-test]

Why: Boo's UI, demo, perf and live tests drive the real mouse and keyboard, take screenshots, and the live smoke sends a
picture of the screen to a real AI. On a self-hosted runner (for example the owner's own PC) that would capture a real
person's screen. So, in every workflow file:
  - every job has exactly one block-style `runs-on: <label>` line, with <label> one literal from ALLOWED;
  - no other spelling of runs-on (quoted key, flow mapping, list, `${{ }}` expression, `? runs-on`);
  - no job calls a reusable workflow (`uses: .../.github/workflows/...`), whose runner this check cannot see;
  - the text "self-hosted" appears nowhere outside comments (also inside run: strings; reword those);
  - at least one job.
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
STRICT_RUNS_ON = re.compile(r'^(?P<indent>\s+)runs-on:\s*(?P<label>[A-Za-z0-9.\-]+)\s*$')
FIX = ('Fix: give every job one line `runs-on: windows-latest` (or ubuntu-latest). Never a personal PC, never '
       'self-hosted, never an expression or list, never a reusable workflow. See docs/LIVE_AI_SMOKE.md section 2.')


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
    lines = [strip_comment(raw).rstrip() for raw in text.splitlines()]
    jobs_line = next((i for i, line in enumerate(lines) if re.match(r'^["\']?jobs["\']?\s*:', line)), None)
    if jobs_line is None:
        return [f'{relative}: no top-level `jobs:` block'], 0
    if lines[jobs_line].split(':', 1)[1].strip():
        failures.append(f'{relative}:{jobs_line + 1}: `jobs:` must be a block mapping, not inline')
    job_indent, jobs = None, []
    for i in range(jobs_line + 1, len(lines)):
        line = lines[i]
        if not line.strip():
            continue
        indent = len(line) - len(line.lstrip())
        if indent == 0:
            break
        if job_indent is None:
            job_indent = indent
        if indent == job_indent:
            jobs.append(i)
    strict = 0
    for number, line in enumerate(lines, 1):
        if re.search(r'self-hosted', line, re.IGNORECASE):
            failures.append(f'{relative}:{number}: mentions a self-hosted runner: {line.strip()}')
        if 'runs-on' in line:
            match = STRICT_RUNS_ON.match(line)
            if not match:
                failures.append(f'{relative}:{number}: runs-on must be the plain form `runs-on: <label>`: {line.strip()}')
            elif match.group('label') not in ALLOWED:
                failures.append(f'{relative}:{number}: runs-on {match.group("label")} is not a GitHub-hosted label')
            elif job_indent is None or len(match.group('indent')) != job_indent + 2:
                failures.append(f'{relative}:{number}: runs-on is not directly under a job')
            else:
                strict += 1
        if re.search(r'\buses\s*:\s*\S*\.github/workflows/', line):
            failures.append(f'{relative}:{number}: calls a reusable workflow, whose runner cannot be checked here')
    if not jobs:
        failures.append(f'{relative}: has no jobs')
    elif strict != len(jobs):
        failures.append(f'{relative}: {len(jobs)} jobs but {strict} valid runs-on lines; every job needs exactly one')
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
    'flow mapping': 'jobs: {a: {runs-on: my-pc}}\n',
    'explicit key': 'jobs:\n  a:\n    ? runs-on\n    : my-pc\n',
    'reusable workflow': 'jobs:\n  a:\n    uses: org/repo/.github/workflows/x.yml@main\n',
    'job without runs-on': 'jobs:\n  a:\n    runs-on: ubuntu-latest\n  b:\n    steps: []\n',
    'no jobs': 'on: push\n',
}
SELF_TEST_GOOD = 'on: push\n# self-hosted is fine in a comment\njobs:\n  a:\n    runs-on: windows-latest\n    steps:\n' \
                 '      - run: echo "runs on GitHub"\n  b:\n    needs: a\n    runs-on: ubuntu-latest\n'


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
