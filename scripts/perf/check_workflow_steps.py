"""No masked failures in a workflow step (Codex review BLOCK on 5a3922b, perf-guards.yml).

On Windows a `run:` block is PowerShell (the default there, or `shell: pwsh` / `shell: powershell`). When it runs two native commands (python, dotnet, ...) one after the other,
the step's result is the LAST command's exit code: a failing first command is hidden by a passing second one
(ErrorActionPreference=Stop does not change that for native programs). This check fails any `run:` block with more
than one command line unless it either turns native failures into errors
(`$PSNativeCommandUseErrorActionPreference = $true`) or handles exit codes itself (`$LASTEXITCODE`, as the generated
key-scan steps do). Fix: one command per step, or one of those two.

    python -I scripts/perf/check_workflow_steps.py <workflow.yml> [...]
    python -I scripts/perf/check_workflow_steps.py --self-test

Only PowerShell steps are checked: GitHub runs bash steps with `-e`, which already stops at the first failing command.
Limits: a line counts as a command unless it is blank, a comment, or a lone brace; it does not parse PowerShell. A
pipeline on one line is one command (its earlier stages can still fail silently).
"""
import os
import sys
import tempfile

import yaml

SAFE_MARKERS = ('$PSNativeCommandUseErrorActionPreference = $true', '$LASTEXITCODE')


def command_lines(run):
    return [ln.strip() for ln in run.splitlines() if ln.strip() and not ln.strip().startswith('#') and ln.strip() not in ('{', '}')]


def check_text(text, label):
    try:
        doc = yaml.safe_load(text)
    except yaml.YAMLError as e:
        return [f'{label}: not valid YAML ({type(e).__name__})']
    problems = []
    top_shell = (((doc or {}).get('defaults') or {}).get('run') or {}).get('shell')
    for job_name, job in ((doc or {}).get('jobs') or {}).items():
        job = job or {}
        job_shell = ((job.get('defaults') or {}).get('run') or {}).get('shell') or top_shell
        on_windows = 'windows' in str(job.get('runs-on', '')).lower()
        for i, step in enumerate(job.get('steps') or [], 1):
            run = (step or {}).get('run')
            if not isinstance(run, str):
                continue
            shell = str(step.get('shell') or job_shell or ('pwsh' if on_windows else 'bash')).lower()
            if not shell.startswith(('pwsh', 'powershell')):
                continue
            lines = command_lines(run)
            if len(lines) > 1 and not any(m in run for m in SAFE_MARKERS):
                name = step.get('name') or f'step {i}'
                problems.append(f'{label}: job {job_name}, "{name}" runs {len(lines)} commands in one step; a failing '
                                f'earlier command is masked by the last one (first: {lines[0][:60]})')
    return problems


def self_test():
    head = 'on: push\njobs:\n  j:\n    runs-on: windows-latest\n    steps:\n'
    cases = [
        ('two python commands in one step fail (the 5a3922b perf-guards step)',
         head + '      - name: Self-tests\n        run: |\n          python -I a.py --self-test\n          python -I b.py --self-test\n', True),
        ('one command per step passes', head + '      - run: python -I a.py\n      - run: python -I b.py\n', False),
        ('a command and a pipe on a second line fail (the 5a3922b perf-idle-diag Summary step)',
         head + '      - run: |\n          python -I s.py --strict\n          Get-Content s.md | Add-Content $env:GITHUB_STEP_SUMMARY\n', True),
        ('native errors turned on pass',
         head + '      - run: |\n          $PSNativeCommandUseErrorActionPreference = $true\n          python a.py\n          python b.py\n', False),
        ('explicit $LASTEXITCODE handling passes (generated key-scan steps)',
         head + '      - run: |\n          $rc = 98\n          try { python k.py; $rc = $LASTEXITCODE } catch { $rc = 99 }\n          exit $rc\n', False),
        ('comments and blank lines are not commands',
         head + '      - run: |\n          # explain\n\n          python -I a.py\n', False),
        ('a bash step is not checked (bash -e stops at the first failure)',
         head + '      - shell: bash\n        run: |\n          python a.py\n          python b.py\n', False),
        ('a Linux job defaults to bash',
         head.replace('windows-latest', 'ubuntu-latest') + '      - run: |\n          python a.py\n          python b.py\n', False),
        ('shell: pwsh on a Linux job is checked',
         head.replace('windows-latest', 'ubuntu-latest') + '      - shell: pwsh\n        run: |\n          python a.py\n          python b.py\n', True),
    ]
    failures = 0
    for name, text, want_problem in cases:
        got = bool(check_text(text, 'fixture'))
        ok = got == want_problem
        failures += not ok
        print(f'  [{"ok" if ok else "WRONG"}] {name}')
    with tempfile.TemporaryDirectory() as d:
        p = os.path.join(d, 'w.yml')
        open(p, 'w', encoding='utf-8').write(cases[0][1])
        code = main([p])
    ok = code == 1
    failures += not ok
    print(f'  [{"ok" if ok else "WRONG"}] the command line exits 1 on a masked step' + ('' if ok else f': exit {code}'))
    total = len(cases) + 1
    print(f'self-test: {total - failures}/{total}')
    return 1 if failures else 0


def main(argv):
    if argv == ['--self-test']:
        return self_test()
    if not argv:
        print(__doc__, file=sys.stderr)
        return 2
    problems = []
    for path in argv:
        try:
            with open(path, encoding='utf-8') as f:
                problems += check_text(f.read(), path)
        except OSError as e:
            problems.append(f'{path}: cannot read ({type(e).__name__})')
    for p in problems:
        print(f'::error::{p}')
    if problems:
        print(f'WORKFLOW_STEPS_FAILED {len(problems)} masked step(s)')
        return 1
    print(f'WORKFLOW_STEPS_OK {len(argv)} workflow(s), no step can mask a failing command')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
