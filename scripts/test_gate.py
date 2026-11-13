"""Fail the build when tests can pass vacuously. Never read secrets.

  python -I scripts/test_gate.py static
      Source check over every xUnit test project anywhere in the repo (tests/, tools/, ...; rule BOO-TEST-001):
      - no empty or whitespace-only .cs file (8185c1d emptied a 474-line test file and CI stayed green);
      - every *Tests.cs / *Tests.<part>.cs file declares at least one [Fact] or [Theory];
      - every test project declares at least one test;
      - no Skip = on any test attribute (a skipped security test is an untested one).

  python -I scripts/test_gate.py trx DIR [DIR ...]
      Result check after `dotnet test --logger trx --results-directory DIR`:
      each DIR must hold at least one .trx, and every .trx must report tests that ran (total > 0,
      executed > 0) and none that were skipped or not executed.
"""
import pathlib
import re
import sys
import xml.etree.ElementTree as ElementTree

root = pathlib.Path(__file__).resolve().parent.parent
GENERATED_PARTS = {'bin', 'obj', 'TestResults'}
TEST_ATTRIBUTE = re.compile(r'^\s*\[\s*(?:Xunit\.)?(?:Fact|Theory)\b', re.MULTILINE)
SKIP_ARGUMENT = re.compile(r'\[\s*(?:Xunit\.)?(?:Fact|Theory|InlineData|MemberData|ClassData)\b[^\]]*?\bSkip\s*=', re.DOTALL)
BLOCK_COMMENT = re.compile(r'/\*.*?\*/', re.DOTALL)
TEST_FILE_NAME = re.compile(r'Tests(\.[A-Za-z0-9_]+)?\.cs$')

def test_projects():
    # Every project in the repo, not only tests/: a test project under tools/ (live smoke, verify-live) must not be
    # able to skip or empty a test either (BOO-TEST-001).
    for csproj in sorted(root.rglob('*.csproj')):
        if GENERATED_PARTS.intersection(csproj.relative_to(root).parts) or '.git' in csproj.relative_to(root).parts:
            continue
        text = csproj.read_text(encoding='utf-8-sig')
        if '<IsTestProject>true</IsTestProject>' in text or 'xunit' in text.lower():
            yield csproj.parent

def static_check():
    failures, rows = [], []
    for project in test_projects():
        sources = sorted(p for p in project.rglob('*.cs') if not GENERATED_PARTS.intersection(p.relative_to(project).parts))
        project_tests = 0
        for source in sources:
            relative = source.relative_to(root).as_posix()
            text = source.read_text(encoding='utf-8-sig')
            if not text.strip():
                failures.append(f'{relative}: file is empty')
                continue
            code = BLOCK_COMMENT.sub('', text)
            count = len(TEST_ATTRIBUTE.findall(code))
            project_tests += count
            if TEST_FILE_NAME.search(source.name) and count == 0:
                failures.append(f'{relative}: a test file with no [Fact] or [Theory]')
            for match in SKIP_ARGUMENT.finditer(code):
                line = code.count('\n', 0, match.start()) + 1
                failures.append(f'{relative}:{line}: Skip = on a test attribute; tests must run or fail, never skip')
        rows.append((project.name, len(sources), project_tests))
        if project_tests == 0:
            failures.append(f'{project.relative_to(root).as_posix()}: test project declares no tests')
    for name, files, tests in rows:
        print(f'  {name:<26} {files:>3} source files  {tests:>4} test attributes')
    print(f'  {len(rows)} test project(s) checked')
    return failures

def trx_check(directories):
    failures = []
    if not directories:
        return ['trx mode needs at least one results directory']
    for directory in directories:
        folder = pathlib.Path(directory)
        trx_files = sorted(folder.rglob('*.trx')) if folder.is_dir() else []
        if not trx_files:
            failures.append(f'{directory}: no .trx test results (did the test step run?)')
            continue
        for trx in trx_files:
            tree = ElementTree.parse(trx)
            counters = next((e for e in tree.iter() if e.tag.endswith('Counters')), None)
            if counters is None:
                failures.append(f'{trx}: no result counters')
                continue
            def counter(name):
                return int(counters.get(name, '0'))
            total, executed, passed, failed = counter('total'), counter('executed'), counter('passed'), counter('failed')
            not_executed = counter('notExecuted')
            skipped = [r.get('testName') for r in tree.iter() if r.tag.endswith('UnitTestResult') and r.get('outcome') == 'NotExecuted']
            print(f'  {trx.parent.name}/{trx.name}: total={total} executed={executed} passed={passed} failed={failed} notExecuted={not_executed}')
            if total == 0 or executed == 0:
                failures.append(f'{trx}: zero tests ran')
            if not_executed or skipped:
                failures.append(f'{trx}: {max(not_executed, len(skipped))} test(s) skipped or not executed: ' + ', '.join(filter(None, skipped)))
    return failures

if __name__ == '__main__':
    mode = sys.argv[1] if len(sys.argv) > 1 else ''
    if mode == 'static':
        problems = static_check()
    elif mode == 'trx':
        problems = trx_check(sys.argv[2:])
    else:
        raise SystemExit('usage: test_gate.py static | trx DIR [DIR ...]')
    if problems:
        print('TEST_GATE_FAILED:')
        for problem in problems:
            print('  ' + problem)
        raise SystemExit(1)
    print('TEST_GATE_PASSED', mode)
