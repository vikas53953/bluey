"""Fail if any GitHub Actions job could run anywhere except GitHub's own hosted VMs. Never reads secrets.

  python -I scripts/ci/check_runners.py [--self-test]      (needs PyYAML: pip install pyyaml==6.0.2)

Why: Boo's UI, demo, perf and live tests drive the real mouse and keyboard, take screenshots, and the live smoke sends a
picture of the screen to a real AI. On a self-hosted runner (for example the owner's own PC) that would capture a real
person's screen.

Two independent layers; a workflow must pass both:
  1. MEANING (a real YAML parser, PyYAML): every job's `runs-on` is one literal label from ALLOWED, and no job calls a
     reusable workflow (`uses:` at job level).
  2. PLAIN SHAPE (text rules, so YAML tricks fail closed instead of being interpreted): the file is one document of
     printable ASCII and newlines only; no quoted string spans lines; `jobs:` is the last top-level key; each job is a
     plain-word key whose own keys are plain words from JOB_KEYS; each job has exactly one plain `runs-on: <label>`
     line; `runs-on` appears nowhere else; "self-hosted" appears nowhere outside comments.
To slip past, a trick would have to fool PyYAML and the text rules the same way GitHub's own parser reads it.
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
FIX = ('Fix: give every job one plain line `runs-on: windows-latest` (or ubuntu-latest) and only ordinary job keys; '
       'keep the file plain ASCII with `jobs:` last. Never a personal PC, never self-hosted, never an expression, '
       'list, quoted key or reusable workflow. See docs/LIVE_AI_SMOKE.md section 2.')


def split_comment(line, quote):
    """Return (code without comment, quote still open at end of line). quote is None, "'" or '"' on entry."""
    i = 0
    while i < len(line):
        char = line[i]
        if quote == "'":
            if char == "'" and line[i + 1:i + 2] == "'":
                i += 2
                continue
            if char == "'":
                quote = None
        elif quote == '"':
            if char == '\\':
                i += 2
                continue
            if char == '"':
                quote = None
        elif char in '"\'' and (i == 0 or line[i - 1] in ' \t:[{,-'):
            quote = char
        elif char == '#' and (i == 0 or line[i - 1] in ' \t'):
            return line[:i], quote
        i += 1
    return line, quote


def shape_failures(relative, text):
    """Layer 2: text rules. Returns (failures, job count)."""
    failures = []
    odd = sorted({c for c in text if not (c == '\n' or ' ' <= c <= '~')})
    if odd:
        failures.append(f'{relative}: characters outside printable ASCII and newline: '
                        + ', '.join(f'U+{ord(c):04X}' for c in odd[:6]))
    lines, quote, block_indent = [], None, None
    for number, raw in enumerate(text.split('\n'), 1):
        indent = len(raw) - len(raw.lstrip(' '))
        if block_indent is not None:
            # Inside a `|` / `>` block scalar every character is literal text (quotes and # included), so no quote or
            # comment rules apply; the whole line is kept for the runs-on / self-hosted scan below.
            if not raw.strip() or indent > block_indent:
                lines.append(raw.rstrip())
                continue
            block_indent = None
        if quote is None and re.match(r'^(---|\.\.\.)(\s|$)', raw):
            failures.append(f'{relative}:{number}: a second YAML document marker; keep one document per file')
        code, quote = split_comment(raw, quote)
        if quote is not None:
            failures.append(f'{relative}:{number}: a quoted string runs onto the next line; keep quoted text on one line')
            quote = None
        if re.search(r'(:|^\s*-)\s*[|>][-+0-9]*\s*$', code):
            block_indent = indent
        lines.append(code.rstrip())
    jobs_lines = [i for i, line in enumerate(lines) if re.match(r'^jobs:\s*$', line)]
    if len(jobs_lines) != 1:
        return failures + [f'{relative}: needs exactly one top-level block `jobs:` line (found {len(jobs_lines)})'], 0
    start = jobs_lines[0] + 1
    for i in range(start, len(lines)):
        if lines[i] and not lines[i].startswith(' '):
            failures.append(f'{relative}:{i + 1}: `jobs:` must be the last top-level key: {lines[i].strip()}')
    body = [(i, lines[i]) for i in range(start, len(lines)) if lines[i].strip() and lines[i].startswith(' ')]
    if not body:
        return failures + [f'{relative}: has no jobs'], 0
    job_indent = len(body[0][1]) - len(body[0][1].lstrip(' '))
    jobs, current, accounted = [], None, set()
    for i, line in body:
        indent = len(line) - len(line.lstrip(' '))
        if indent < job_indent:
            failures.append(f'{relative}:{i + 1}: line is outside any job but inside jobs:')
            continue
        if indent == job_indent:
            if not PLAIN_KEY.match(line) or line.split(':', 1)[1].strip():
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


def meaning_failures(relative, text):
    """Layer 1: what a real YAML parser reads."""
    import yaml  # PyYAML; imported here so a missing install is reported as a check failure, not a crash
    try:
        documents = list(yaml.safe_load_all(text))
    except yaml.YAMLError as error:
        return [f'{relative}: not valid YAML: {str(error).splitlines()[0]}']
    if len(documents) != 1 or not isinstance(documents[0], dict):
        return [f'{relative}: must be exactly one YAML mapping document (found {len(documents)} documents)']
    jobs = documents[0].get('jobs')
    if not isinstance(jobs, dict) or not jobs:
        return [f'{relative}: a parser finds no jobs mapping']
    failures = []
    for name, job in jobs.items():
        if not isinstance(job, dict):
            failures.append(f'{relative}: job {name!r} is not a mapping')
            continue
        if 'uses' in job:
            failures.append(f'{relative}: job {name!r} calls a reusable workflow ({job["uses"]!r})')
        runs_on = job.get('runs-on')
        if not isinstance(runs_on, str) or runs_on not in ALLOWED:
            failures.append(f'{relative}: job {name!r} runs on {runs_on!r}, not one GitHub-hosted label')
    return failures


def check_text(relative, text):
    text = text.replace('\r\n', '\n')
    try:
        meaning = meaning_failures(relative, text)
    except ImportError:
        meaning = [f'{relative}: PyYAML is not installed (pip install pyyaml==6.0.2); the check cannot run']
    shape, jobs = shape_failures(relative, text)
    return meaning + shape, jobs


def main():
    workflows = sorted((root / '.github' / 'workflows').glob('*.y*ml'))
    if not workflows:
        print('RUNNERS FAILED: no workflow files found under .github/workflows')
        return 1
    failures, jobs = [], 0
    for workflow in workflows:
        found, count = check_text(workflow.relative_to(root).as_posix(), workflow.read_bytes().decode('utf-8'))
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
    'self-hosted label': 'on: push\njobs:\n  a:\n    runs-on: [self-hosted, windows]\n',
    'expression': 'on: push\njobs:\n  a:\n    runs-on: ${{ inputs.r }}\n',
    'unknown label': 'on: push\njobs:\n  a:\n    runs-on: my-pc\n',
    'quoted key': 'on: push\njobs:\n  a:\n    "runs-on": my-pc\n',
    'escaped key': 'on: push\njobs:\n  a:\n    runs-on: windows-latest\n  b:\n    "runs\\x2Don": my-pc\n',
    'alias key': 'x: &k "runs\\x2Don"\njobs:\n  a:\n    *k : my-pc\n',
    'decoy in block scalar': 'env:\n  NOTE: |\n    runs-on: ubuntu-latest\njobs:\n  a:\n    runs-on: windows-latest\n'
                             '  b:\n    "runs\\x2Don": my-pc\n',
    'flow mapping': 'on: push\njobs: {a: {runs-on: my-pc}}\n',
    'explicit key': 'on: push\njobs:\n  a:\n    ? runs-on\n    : my-pc\n',
    'merge key': 'on: push\njobs:\n  a:\n    <<: *base\n',
    'reusable workflow': 'on: push\njobs:\n  a:\n    uses: org/repo/.github/workflows/x.yml@main\n',
    'job without runs-on': 'on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n  b:\n    steps: []\n',
    'runs-on deeper than job level': 'on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    strategy:\n      runs-on: my-pc\n',
    'tab indentation': 'on: push\njobs:\n  a:\n\truns-on: my-pc\n',
    'single-quoted string at column 0': "on: push\njobs:\n  a:\n    runs-on: windows-latest\n    name: 'x\nx'\n"
                                        '  b:\n    uses: org/repo/.github/workflows/x.yml@main\n',
    'quoted string hides jobs': "on: push\nname: 'x\njobs:\n  a:\n    runs-on: windows-latest\n'\n"
                                '"j\\x6fbs":\n  b:\n    "runs\\x2Don": my-pc\n',
    'unicode line break': 'on: push\njobs:\n  a:\n    runs-on: windows-latest\n    steps:\n'
                          '      - run: echo hi #\u0085  b:\u0085    runs-on: my-pc\n',
    'second document': 'on: push\njobs:\n  a:\n    runs-on: windows-latest\n---\n"j\\x6fbs":\n  b:\n    runs-on: my-pc\n',
    'top-level key after jobs': 'on: push\njobs:\n  a:\n    runs-on: windows-latest\nenv:\n  X: 1\n',
    'no jobs': 'on: push\n',
}
SELF_TEST_GOOD = ('on: push\n# self-hosted is fine in a comment\njobs:\n  a:\n    runs-on: windows-latest\n'
                  '    timeout-minutes: 5\n    steps:\n      - uses: actions/checkout@v4\n'
                  "      - run: echo 'it''s fine' \"and \\\" this\"  # trailing comment\n"
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
