"""Prove the #124 / PR #142 guards bite: apply one known mutant and require its guard to FAIL FOR THE INTENDED REASON
(or, for `control`, require every guard to PASS on the real code).

A guard is setup steps (publish, build) followed by one check. The verdict comes from evidence, never from "some
command exited non-zero" (Codex review BLOCK on 5a3922b: a build or publish failure must not count as a kill):

  KILLED    every setup step succeeded, the check failed, AND its output shows the intended failure: the named
            runtime setting mismatch, a completed TRX in which the named test failed and nothing was skipped or
            aborted, or a summary self-test result line with a [WRONG] row and no crash.
  SURVIVED  setup succeeded and the check passed with the fault in place.
  INVALID   anything else: a setup step failed, the check crashed, the TRX is missing/empty/has skips, a different
            test failed, the expected text is missing. Counts as a failure, never as a kill.

The mutants are the ones the Codex mutation run found surviving on 61361c2 (M01 to M07), plus the same faults at the
new reader site (M02r, M03r, M04r). Each is an exact text replacement; if the text is not there (the code moved) the
cell is INVALID.

    python -I scripts/perf/guard_mutants.py list
    python -I scripts/perf/guard_mutants.py run <id|control>     exit 0 = KILLED (or control all PASS), 1 = SURVIVED
                                                                 (or control failed a check), 3 = INVALID
    python -I scripts/perf/guard_mutants.py --self-test          proves setup failures and missing evidence are INVALID

Runs in CI (.github/workflows/perf-guards-mutants.yml) on a throwaway checkout: it edits files and never restores them.
"""
import os
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

CSPROJ = 'src/Boo.App/Boo.App.csproj'
SNAPSHOT = 'src/Boo.Core/PerfProbeSnapshot.cs'
READER = 'src/Boo.Windows/RuntimeProbeCounters.cs'
SUMMARY = 'scripts/perf/idle_diag_summary.py'
TRX_DIR = 'artifacts/guard-trx'

KILLED, SURVIVED, INVALID = 'KILLED', 'SURVIVED', 'INVALID'
EXIT = {KILLED: 0, SURVIVED: 1, INVALID: 3}


class Guard:
    def __init__(self, name, setup, check, kind, total=None):
        self.name, self.setup, self.check, self.kind, self.total = name, setup, check, kind, total


def _test_guard(name, project, filter_, total):
    trx = f'{TRX_DIR}/{name}.trx'
    return Guard(name,
                 setup=[['dotnet', 'build', project, '-c', 'Release']],
                 check=['dotnet', 'test', project, '-c', 'Release', '--no-build', '--filter', filter_,
                        '--logger', f'trx;LogFileName={os.path.abspath(trx)}'],
                 kind=('trx', trx), total=total)


GUARDS = {
    'runtime-settings': Guard('runtime-settings',
                              setup=[['dotnet', 'publish', CSPROJ, '-c', 'Release', '-r', 'win-x64', '--self-contained',
                                      'true', '-o', 'artifacts/guard-publish']],
                              check=[sys.executable, '-I', 'scripts/perf/check_runtime_settings.py', 'check',
                                     'artifacts/guard-publish'],
                              kind=('text', 'RUNTIME_SETTINGS_OK', 'RUNTIME_SETTINGS_FAILED')),
    'snapshot-tests': _test_guard('snapshot-tests', 'tests/Boo.Core.Tests', 'FullyQualifiedName~PerfProbeSnapshotTests', 3),
    'reader-tests': _test_guard('reader-tests', 'tests/Boo.Integration.Tests', 'FullyQualifiedName~RuntimeProbeCountersTests', 3),
    'summary-self-test': Guard('summary-self-test', setup=[], check=[sys.executable, '-I', SUMMARY, '--self-test'],
                               kind=('selftest',)),
}

# id -> (file, exact text, replacement, guard, the evidence its failure must show)
MUTANTS = {
    'M01': (CSPROJ, '<TieredPGO>false</TieredPGO>', '<TieredPGO>true</TieredPGO>', 'runtime-settings',
            'System.Runtime.TieredPGO is true'),
    'M02': (SNAPSHOT, 'counters.UserCpu.TotalMilliseconds,\n            counters.KernelCpu.TotalMilliseconds,',
            'counters.KernelCpu.TotalMilliseconds,\n            counters.UserCpu.TotalMilliseconds,', 'snapshot-tests',
            'Each_process_counter_is_written_to_its_own_field_in_milliseconds'),
    'M03': (SNAPSHOT, 'counters.JitMethods,\n            counters.JitTime.TotalMilliseconds,', '0,\n            0,',
            'snapshot-tests', 'Each_process_counter_is_written_to_its_own_field_in_milliseconds'),
    'M04': (SNAPSHOT, 'counters.ProbeWrites,\n            counters.ProbeWriteTime.TotalMilliseconds);', '0,\n            0);',
            'snapshot-tests', 'Each_process_counter_is_written_to_its_own_field_in_milliseconds'),
    'M02r': (READER, 'UserCpu: cpu.UserTime,\n            KernelCpu: cpu.PrivilegedTime,',
             'UserCpu: cpu.PrivilegedTime,\n            KernelCpu: cpu.UserTime,', 'reader-tests',
             'Busy_managed_work_is_read_as_user_cpu_not_kernel_cpu'),
    'M03r': (READER, 'JitMethods: System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: false),\n'
                     '            JitTime: System.Runtime.JitInfo.GetCompilationTime(currentThread: false),',
             'JitMethods: 0,\n            JitTime: TimeSpan.Zero,', 'reader-tests', 'Compiling_a_new_method_raises_the_jit_counters'),
    'M04r': (READER, 'ProbeWrites: meter.Writes,\n            ProbeWriteTime: meter.Total);',
             'ProbeWrites: 0,\n            ProbeWriteTime: TimeSpan.Zero);', 'reader-tests',
             'The_probe_write_meter_is_passed_through'),
    'M05': (SUMMARY, "wall = (b['tMs'] - a['tMs']) / 1000.0", "wall = (b['tMs'] - a['tMs']) / 100.0", 'summary-self-test',
            '[WRONG] row 1'),
    'M06': (SUMMARY, "start = head['notchFoundMs'] + 6000", "start = head['notchFoundMs'] + 30000", 'summary-self-test',
            '[WRONG] row 1'),
    'M07': (SUMMARY, '        if misses:\n', '        if False and misses:\n', 'summary-self-test', '[WRONG] row 3'),
}


def execute(cmd):
    """Runs one command; returns (exit code, combined output). Replaced in the self-test."""
    print('$ ' + ' '.join(cmd), flush=True)
    p = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors='replace')
    print(p.stdout, flush=True)
    return p.returncode, p.stdout


def read_trx(path):
    """Returns (counters dict, {test method name: outcome}) or None when the file is missing or unreadable."""
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError):
        return None
    ns = {'t': root.tag.split('}')[0].strip('{')} if root.tag.startswith('{') else {}
    q = (lambda tag: f't:{tag}') if ns else (lambda tag: tag)
    counters = root.find(f'.//{q("Counters")}', ns)
    if counters is None:
        return None
    outcomes = {}
    for r in root.iter():
        if r.tag.endswith('UnitTestResult'):
            outcomes[r.get('testName', '').rsplit('.', 1)[-1]] = r.get('outcome', '')
    return {k: int(v) for k, v in counters.attrib.items() if v.isdigit()}, outcomes


def judge(guard, rc, output, expect, read=read_trx):
    """Verdict for one check run. expect=None: the real code, the check must PASS cleanly. Otherwise the text that
    must show the intended failure. Returns (verdict, reason)."""
    kind = guard.kind
    if kind[0] == 'text':
        ok_marker, fail_marker = kind[1], kind[2]
        if rc == 0 and ok_marker in output:
            return (SURVIVED, 'check passed') if expect else (KILLED, f'{ok_marker}')
        if rc == 1 and fail_marker in output:
            if expect is None:
                return SURVIVED, 'the real code failed the check'
            if expect in output:
                return KILLED, f'{fail_marker}: {expect}'
            return INVALID, f'{fail_marker} without the expected "{expect}"'
        return INVALID, f'check exited {rc} without its pass or fail marker (crash or tool failure)'
    if kind[0] == 'selftest':
        m = re.search(r'^self-test: (\d+)/(\d+)$', output, re.M)
        if 'Traceback (most recent call last)' in output or not m:
            return INVALID, f'self-test crashed or printed no result line (exit {rc})'
        good, total = int(m.group(1)), int(m.group(2))
        if rc == 0 and good == total:
            return (SURVIVED, f'self-test {good}/{total}') if expect else (KILLED, f'self-test {good}/{total}')
        if rc == 1 and good < total:
            if expect is None:
                return SURVIVED, f'the real code failed the self-test {good}/{total}'
            if expect in output:
                return KILLED, f'self-test {good}/{total}, {expect}'
            return INVALID, f'self-test {good}/{total} without the expected "{expect}"'
        return INVALID, f'self-test exit {rc} does not match its result line {good}/{total}'
    if kind[0] == 'trx':
        got = read(kind[1])
        if got is None:
            return INVALID, f'no readable TRX at {kind[1]} (exit {rc})'
        c, outcomes = got
        total, executed = c.get('total', 0), c.get('executed', 0)
        passed, failed = c.get('passed', 0), c.get('failed', 0)
        if total == 0 or total != guard.total or executed != total or passed + failed != total:
            return INVALID, f'TRX counters total {total} executed {executed} passed {passed} failed {failed} (want {guard.total} all run, none skipped)'
        bad = sorted(n for n, o in outcomes.items() if o != 'Passed')
        if expect is None:
            if rc == 0 and failed == 0:
                return KILLED, f'{passed}/{total} passed'
            if rc != 0 and failed > 0:
                return SURVIVED, f'the real code failed: {bad}'
            return INVALID, f'exit {rc} does not match TRX ({failed} failed)'
        if rc == 0 and failed == 0:
            return SURVIVED, f'{passed}/{total} passed with the fault in place'
        if rc != 0 and failed > 0 and bad == [expect]:
            return KILLED, f'{expect} failed, {passed}/{total} others passed'
        return INVALID, f'exit {rc}, failing tests {bad}, expected exactly [{expect}]'
    return INVALID, f'unknown guard kind {kind}'


def run_guard(guard, expect, run=execute, read=read_trx):
    for step in guard.setup:
        rc, _ = run(step)
        if rc != 0:
            return INVALID, f'setup step failed (exit {rc}): {" ".join(step)}'
    if guard.kind[0] == 'trx':
        try:
            os.remove(guard.kind[1])
        except OSError:
            pass
    rc, output = run(guard.check)
    return judge(guard, rc, output, expect, read)


def apply_mutant(target):
    path, old, new = MUTANTS[target][:3]
    with open(path, encoding='utf-8') as f:  # .gitattributes keeps LF; universal newlines read either way
        text = f.read()
    if text.count(old) != 1:
        return f'the target text is in {path} {text.count(old)} times, expected once'
    with open(path, 'w', encoding='utf-8', newline='') as f:
        f.write(text.replace(old, new))
    return None


def run(target, runner=execute, read=read_trx):
    if target == 'control':
        verdicts = {name: run_guard(g, None, runner, read) for name, g in GUARDS.items()}
        for name, (v, why) in verdicts.items():
            print(f'  {name}: {"PASS" if v == KILLED else v} ({why})')
        if all(v == KILLED for v, _ in verdicts.values()):
            print(f'GUARD_CONTROL_OK: all {len(GUARDS)} guards pass on the real code')
            return 0
        worst = INVALID if any(v == INVALID for v, _ in verdicts.values()) else SURVIVED
        print(f'GUARD_CONTROL_{"INVALID" if worst == INVALID else "FAILED"}')
        return EXIT[worst]
    problem = apply_mutant(target)
    if problem:
        print(f'MUTANT_INVALID {target}: {problem}')
        return EXIT[INVALID]
    guard_name, expect = MUTANTS[target][3], MUTANTS[target][4]
    print(f'applied {target}; guard {guard_name} must fail with "{expect}"', flush=True)
    verdict, why = run_guard(GUARDS[guard_name], expect, runner, read)
    print(f'MUTANT_{verdict} {target} ({guard_name}): {why}')
    return EXIT[verdict]


def self_test():
    """Fake runs: no build, no tests. Every way a cell can fail without its guard asserting must be INVALID."""
    trx_ok = ({'total': 3, 'executed': 3, 'passed': 3, 'failed': 0}, {'A': 'Passed', 'B': 'Passed', 'C': 'Passed'})

    def trx_fail(name):
        return {'total': 3, 'executed': 3, 'passed': 2, 'failed': 1}, {name: 'Failed', 'B': 'Passed', 'C': 'Passed'}

    expect_c = MUTANTS['M02'][4]
    cases = [
        # (name, guard, expect, [results of each command in order], trx, wanted verdict)
        ('publish fails before the settings check: INVALID, not KILLED', 'runtime-settings', 'System.Runtime.TieredPGO is true',
         [(1, 'error NETSDK1004')], None, INVALID),
        ('settings check reports the intended mismatch: KILLED', 'runtime-settings', 'System.Runtime.TieredPGO is true',
         [(0, ''), (1, '::error::System.Runtime.TieredPGO is true in the published app\nRUNTIME_SETTINGS_FAILED 1')], None, KILLED),
        ('settings check fails for another setting: INVALID', 'runtime-settings', 'System.Runtime.TieredPGO is true',
         [(0, ''), (1, '::error::System.GC.Server is true\nRUNTIME_SETTINGS_FAILED 1')], None, INVALID),
        ('settings check crashes (exit 1, no marker): INVALID', 'runtime-settings', 'System.Runtime.TieredPGO is true',
         [(0, ''), (1, 'Traceback (most recent call last):')], None, INVALID),
        ('settings check passes with the fault: SURVIVED', 'runtime-settings', 'System.Runtime.TieredPGO is true',
         [(0, ''), (0, 'RUNTIME_SETTINGS_OK')], None, SURVIVED),
        ('test build fails: INVALID, not KILLED', 'snapshot-tests', expect_c, [(1, 'error CS1002')], None, INVALID),
        ('test run exits 1 with no TRX: INVALID', 'snapshot-tests', expect_c, [(0, ''), (1, '')], None, INVALID),
        ('TRX with a skipped test: INVALID', 'snapshot-tests', expect_c, [(0, ''), (1, '')],
         ({'total': 3, 'executed': 2, 'passed': 1, 'failed': 1, 'notExecuted': 1}, {expect_c: 'Failed', 'B': 'Passed'}), INVALID),
        ('a different test fails: INVALID', 'snapshot-tests', expect_c, [(0, ''), (1, '')], trx_fail('Other_test'), INVALID),
        ('the named test fails, the rest pass: KILLED', 'snapshot-tests', expect_c, [(0, ''), (1, '')], trx_fail(expect_c), KILLED),
        ('all tests pass with the fault: SURVIVED', 'snapshot-tests', expect_c, [(0, ''), (0, '')], trx_ok, SURVIVED),
        ('summary self-test crashes: INVALID', 'summary-self-test', '[WRONG] row 1', [(1, 'Traceback (most recent call last):\n')], None, INVALID),
        ('summary self-test fails without a result line: INVALID', 'summary-self-test', '[WRONG] row 1', [(1, 'error')], None, INVALID),
        ('summary self-test reports the wrong row: KILLED', 'summary-self-test', '[WRONG] row 1',
         [(1, '  [WRONG] row 1\nself-test: 8/9\n')], None, KILLED),
        ('summary self-test passes with the fault: SURVIVED', 'summary-self-test', '[WRONG] row 1', [(0, 'self-test: 9/9\n')], None, SURVIVED),
        ('control: the real code passes cleanly', 'snapshot-tests', None, [(0, ''), (0, '')], trx_ok, KILLED),
        ('control: a build failure is INVALID, not a pass', 'snapshot-tests', None, [(1, 'error')], trx_ok, INVALID),
    ]
    failures = 0
    for name, guard_name, expect, results, trx, want in cases:
        queue = list(results)

        def fake_run(cmd, queue=queue):
            return queue.pop(0) if queue else (99, 'unexpected extra command')
        verdict, why = run_guard(GUARDS[guard_name], expect, fake_run, lambda _p, trx=trx: trx)
        ok = verdict == want
        failures += not ok
        print(f'  [{"ok" if ok else "WRONG"}] {name}' + ('' if ok else f': got {verdict} ({why})'))
    # The TRX reader on the shape `dotnet test --logger trx` writes (namespace, fully qualified test names).
    trx_text = ('<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                '<Results><UnitTestResult testName="Boo.Core.Tests.PerfProbeSnapshotTests.A_test" outcome="Failed" />'
                '<UnitTestResult testName="Boo.Core.Tests.PerfProbeSnapshotTests.B_test" outcome="Passed" /></Results>'
                '<ResultSummary outcome="Failed"><Counters total="2" executed="2" passed="1" failed="1" error="0" '
                'notExecuted="0" /></ResultSummary></TestRun>')
    with tempfile.TemporaryDirectory() as d:
        p = os.path.join(d, 'r.trx')
        open(p, 'w', encoding='utf-8').write(trx_text)
        got = read_trx(p)
        missing = read_trx(os.path.join(d, 'none.trx'))
    want = ({'total': 2, 'executed': 2, 'passed': 1, 'failed': 1, 'error': 0, 'notExecuted': 0},
            {'A_test': 'Failed', 'B_test': 'Passed'})
    ok = got == want and missing is None
    failures += not ok
    print(f'  [{"ok" if ok else "WRONG"}] the TRX reader reads counters and test outcomes; a missing TRX is None'
          + ('' if ok else f': got {got}, missing {missing}'))
    # A mutant whose target text is gone is INVALID, never KILLED.
    with tempfile.TemporaryDirectory() as d:
        cwd = os.getcwd()
        try:
            os.chdir(d)
            os.makedirs(os.path.dirname(SUMMARY))
            open(SUMMARY, 'w', encoding='utf-8').write('# moved code\n')
            code = run('M05', runner=lambda c: (0, ''))
        finally:
            os.chdir(cwd)
    ok = code == EXIT[INVALID]
    failures += not ok
    print(f'  [{"ok" if ok else "WRONG"}] a mutant whose target text is gone is INVALID' + ('' if ok else f': exit {code}'))
    total = len(cases) + 2
    print(f'self-test: {total - failures}/{total}')
    return 1 if failures else 0


def main(argv):
    if argv == ['list']:
        print(' '.join(['control', *MUTANTS]))
        return 0
    if argv == ['--self-test']:
        return self_test()
    if len(argv) != 2 or argv[0] != 'run':
        print(__doc__, file=sys.stderr)
        return 2
    if argv[1] != 'control' and argv[1] not in MUTANTS:
        print(f'unknown mutant {argv[1]}', file=sys.stderr)
        return 2
    os.makedirs(TRX_DIR, exist_ok=True)
    return run(argv[1])


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
