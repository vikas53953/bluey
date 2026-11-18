"""Fail if any GitHub Actions job could run anywhere except GitHub's own hosted VMs. Never reads secrets.

  python -I scripts/ci/check_runners.py [--self-test]
  Needs PyYAML 6.0.2 in the interpreter's own site-packages, hash-pinned in scripts/ci/requirements-ci.txt
  (`python -m pip install --require-hashes --only-binary=:all: -r scripts/ci/requirements-ci.txt`; `python -I`
  ignores PYTHONPATH and user site-packages). Without it every workflow fails the check; it never passes.

Why: Boo's UI, demo, perf and live tests drive the real mouse and keyboard, take screenshots, and the live smoke sends a
picture of the screen to a real AI. On a self-hosted runner (for example the owner's own PC) that would capture a real
person's screen.

Two independent layers; a workflow must pass both:
  1. MEANING (PyYAML's raw node tree, no type resolution, so `yes`/`on` stay text): one mapping document; every key
     plain unquoted text, no duplicates, no merge keys, no anchors, aliases or explicit tags; job ids plain names;
     job keys only from JOB_KEYS (so no `uses:` reusable workflow); exactly one plain `runs-on` from ALLOWED.
  2. PLAIN SHAPE (text rules, so YAML tricks fail closed instead of being interpreted): the file is one document of
     printable ASCII and newlines only; no quoted string spans lines; even 2-space indentation inside jobs; `jobs:` is the last top-level key; each job is a
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
    lines, quote, block_indent, block_content = [], None, None, []
    for number, raw in enumerate(text.split('\n'), 1):
        indent = len(raw) - len(raw.lstrip(' '))
        if block_indent is not None:
            # Inside a `|` / `>` block scalar every character is literal text (quotes and # included), so no quote or
            # comment rules apply; the whole line is kept for the runs-on / self-hosted scan below.
            if not raw.strip() or indent > block_indent:
                block_content.append(number - 1)
                lines.append(raw.rstrip())
                continue
            block_indent = None
        if quote is None and re.match(r'^(---|\.\.\.)(\s|$)', raw):
            failures.append(f'{relative}:{number}: a second YAML document marker; keep one document per file')
        code, quote = split_comment(raw, quote)
        if quote is not None:
            failures.append(f'{relative}:{number}: a quoted string runs onto the next line; keep quoted text on one line')
            quote = None
        if re.search(r'(:\s+|^\s*-\s+)[|>][-+0-9]*\s*$', code):
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
    block_lines = set(block_content)
    for i, line in body:
        indent = len(line) - len(line.lstrip(' '))
        if i not in block_lines and ((indent - job_indent) % 2 or (job_indent < indent < job_indent + 2)):
            failures.append(f'{relative}:{i + 1}: inside jobs:, indent by steps of 2 spaces: {line.strip()}')
            continue
        if i in block_lines:
            continue
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
    """Layer 1: the raw YAML node tree (PyYAML compose/parse, no type resolution, so a key like `yes` stays the text
    "yes" instead of becoming True as YAML 1.1 values would). Rejects anything whose meaning parsers may disagree on."""
    import yaml  # PyYAML; imported here so a missing install is reported as a check failure, not a crash
    failures = []
    try:
        for event in yaml.parse(text, Loader=yaml.SafeLoader):
            if isinstance(event, yaml.AliasEvent) or getattr(event, 'anchor', None):
                failures.append(f'{relative}: uses a YAML anchor or alias; write values out in full')
                break
            tag = getattr(event, 'tag', None)
            if tag and not (isinstance(event, yaml.ScalarEvent) and event.implicit[0]) and tag not in ('!', None):
                if not isinstance(event, (yaml.MappingStartEvent, yaml.SequenceStartEvent)) or not event.implicit:
                    failures.append(f'{relative}: uses an explicit YAML tag ({tag}); remove it')
                    break
        documents = list(yaml.compose_all(text, Loader=yaml.SafeLoader))
    except yaml.YAMLError as error:
        return [f'{relative}: not valid YAML: {str(error).splitlines()[0]}']
    if len(documents) != 1 or not isinstance(documents[0], yaml.MappingNode):
        return failures + [f'{relative}: must be exactly one YAML mapping document (found {len(documents)})']

    def plain(node):
        return isinstance(node, yaml.ScalarNode) and node.style is None

    def walk(node):
        if isinstance(node, yaml.MappingNode):
            seen = set()
            for key, value in node.value:
                if not plain(key):
                    failures.append(f'{relative}:{key.start_mark.line + 1}: every key must be plain unquoted text')
                elif key.value in seen or key.value == '<<':
                    failures.append(f'{relative}:{key.start_mark.line + 1}: duplicate or merge key {key.value!r}')
                else:
                    seen.add(key.value)
                walk(value)
        elif isinstance(node, yaml.SequenceNode):
            for item in node.value:
                walk(item)

    root_node = documents[0]
    walk(root_node)
    jobs = [value for key, value in root_node.value if plain(key) and key.value == 'jobs']
    if len(jobs) != 1 or not isinstance(jobs[0], yaml.MappingNode) or not jobs[0].value:
        return failures + [f'{relative}: a parser finds no single jobs mapping']
    for key, job in jobs[0].value:
        name = key.value if isinstance(key, yaml.ScalarNode) else '?'
        if not plain(key) or not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_-]*', name):
            failures.append(f'{relative}:{key.start_mark.line + 1}: job id {name!r} must be a plain name')
        if not isinstance(job, yaml.MappingNode):
            failures.append(f'{relative}:{key.start_mark.line + 1}: job {name!r} is not a mapping')
            continue
        runs_on = []
        for job_key, value in job.value:
            if not plain(job_key) or job_key.value not in JOB_KEYS:
                label = job_key.value if isinstance(job_key, yaml.ScalarNode) else '?'
                failures.append(f'{relative}:{job_key.start_mark.line + 1}: job {name!r} has a key that is not an '
                                f'allowed plain job key: {label!r}')
            elif job_key.value == 'runs-on':
                runs_on.append(value)
        if len(runs_on) != 1 or not plain(runs_on[0]) or runs_on[0].value not in ALLOWED:
            shown = [v.value if isinstance(v, yaml.ScalarNode) else type(v).__name__ for v in runs_on]
            failures.append(f'{relative}:{key.start_mark.line + 1}: job {name!r} runs on {shown}, '
                            f'not exactly one plain GitHub-hosted label')
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
    'yaml 1.1 boolean job ids': 'on: push\njobs:\n  yes:\n   env:\n    runs-on: windows-latest\n'
                                '   "runs\\x2Don": X64\n   steps:\n     - run: echo hi\n  on:\n    runs-on: windows-latest\n',
    'odd indent hides uses': 'on: push\njobs:\n  yes:\n   uses: someone/repo/.github/workflows/x.yml@main\n'
                             '   with:\n    runs-on: windows-latest\n  on:\n    runs-on: windows-latest\n',
    'duplicate job id': 'on: push\njobs:\n  a:\n    runs-on: windows-latest\n  a:\n    runs-on: windows-latest\n',
    'anchor and alias': 'on: push\njobs:\n  a:\n    runs-on: &r windows-latest\n  b:\n    runs-on: *r\n',
    'explicit tag': 'on: push\njobs:\n  a:\n    runs-on: !!str windows-latest\n',
}
SELF_TEST_GOOD = ('on: push\n# self-hosted is fine in a comment\njobs:\n  a:\n    runs-on: windows-latest\n'
                  '    timeout-minutes: 5\n    steps:\n      - uses: actions/checkout@v4\n'
                  "      - run: echo 'it''s fine' \"and \\\" this\"  # trailing comment\n"
                  '      - run: |\n          echo "runs on GitHub"\n  b:\n    needs: a\n    runs-on: ubuntu-latest\r\n')


# Each layer must catch these cases ON ITS OWN, so a regression that silently disables one layer fails the self-test.
TEXT_LAYER_MUST_CATCH = ['quoted key', 'tab indentation', 'single-quoted string at column 0', 'unicode line break',
                         'second document', 'top-level key after jobs', 'odd indent hides uses', 'flow mapping']
PARSER_LAYER_MUST_CATCH = ['yaml 1.1 boolean job ids', 'reusable workflow', 'unknown label', 'anchor and alias',
                           'explicit tag', 'duplicate job id', 'job without runs-on', 'expression', 'quoted key']


def self_test():
    problems = [name for name, text in SELF_TEST_BAD.items() if not check_text(name, text)[0]]
    for name in TEXT_LAYER_MUST_CATCH:
        if not shape_failures(name, SELF_TEST_BAD[name].replace('\r\n', '\n'))[0]:
            problems.append(f'text layer alone missed: {name}')
    for name in PARSER_LAYER_MUST_CATCH:
        if not meaning_failures(name, SELF_TEST_BAD[name].replace('\r\n', '\n')):
            problems.append(f'parser layer alone missed: {name}')
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
