"""Fail if any GitHub Actions job could run anywhere except GitHub's own hosted VMs. Never reads secrets.

  python -I scripts/ci/check_runners.py

Why: Boo's UI, demo, perf and live tests drive the real mouse and keyboard, take screenshots, and the live smoke sends a
picture of the screen to a real AI. On a self-hosted runner (for example the owner's own PC) that would photograph a
real person's screen. Every `runs-on:` must therefore be one literal GitHub-hosted label from ALLOWED below: no
`self-hosted`, no label lists, no `${{ }}` expressions (an input could switch them to a personal machine).
"""
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parent.parent.parent
ALLOWED = {
    'windows-latest', 'windows-2025', 'windows-2022',
    'ubuntu-latest', 'ubuntu-24.04', 'ubuntu-22.04',
}
RUNS_ON = re.compile(r'^\s*runs-on\s*:\s*(.*?)\s*$')
FIX = ('Fix: use a single GitHub-hosted label such as windows-latest or ubuntu-latest. Never a personal PC, never '
       'self-hosted, never an input-driven expression. See .github/workflows/live-ai-smoke.yml header.')


def strip_comment(line):
    """Drop a YAML comment that starts outside quotes."""
    quote = None
    for index, char in enumerate(line):
        if char in '"\'' and quote in (None, char):
            quote = None if quote == char else char
        elif char == '#' and quote is None and (index == 0 or line[index - 1].isspace()):
            return line[:index]
    return line


def main():
    workflows = sorted((root / '.github' / 'workflows').glob('*.y*ml'))
    if not workflows:
        print('RUNNERS FAILED: no workflow files found under .github/workflows')
        return 1
    failures, jobs = [], 0
    for workflow in workflows:
        relative = workflow.relative_to(root).as_posix()
        for number, raw in enumerate(workflow.read_text(encoding='utf-8-sig').splitlines(), 1):
            line = strip_comment(raw)
            if re.search(r'self-hosted', line, re.IGNORECASE):
                failures.append(f'{relative}:{number}: names a self-hosted runner: {line.strip()}')
            match = RUNS_ON.match(line)
            if not match:
                continue
            jobs += 1
            label = match.group(1).strip('\'"')
            if label not in ALLOWED:
                failures.append(f'{relative}:{number}: runs-on "{match.group(1)}" is not one GitHub-hosted label')
    if failures:
        print('RUNNERS FAILED: a job could run on a machine that is not a GitHub-hosted VM.')
        for failure in failures:
            print('  ' + failure)
        print(FIX)
        return 1
    print(f'RUNNERS OK: {jobs} jobs in {len(workflows)} workflows, all on GitHub-hosted labels')
    return 0


if __name__ == '__main__':
    sys.exit(main())
