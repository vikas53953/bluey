"""Prove the #124 / PR #142 guards bite: apply one known mutant and require its guard to FAIL FOR THE INTENDED REASON
(or, for `control`, require every guard to PASS on the real code).

A guard is setup steps (publish, build) followed by one check. The verdict comes from evidence, never from "some
command exited non-zero" (Codex review BLOCK on 5a3922b: a build or publish failure must not count as a kill):

  KILLED    every setup step succeeded, the check failed, AND its output shows the intended failure: the named
            runtime setting mismatch, a valid TRX receipt in which exactly the named test failed (see below), or a
            summary self-test result line with a [WRONG] row and no crash.
  SURVIVED  setup succeeded and the check passed with the fault in place.
  INVALID   anything else: a setup step failed, the check crashed, the TRX is missing/empty/has skips, a different
            test failed, the expected text is missing. Counts as a failure, never as a kill.

A TRX receipt is valid only when (Codex review BLOCK on 1b1260d): it has exactly one ResultSummary with exactly one
Counters; the summary outcome is "Completed" for an all-pass run or "Failed" for a run with failures (never Aborted or
anything else); error, timeout, aborted, inconclusive, passedButRunAborted, notRunnable, notExecuted, disconnected,
inProgress and pending are all 0; total == executed == passed + failed == the guard's test count; and there is one
UnitTestResult per test, each name unique, the set of names exactly the guard's tests, every outcome Passed or Failed,
and the Passed/Failed record counts equal the counters. A duplicated record is a repeated executionId (unique per result in real
receipts; testId and testName can repeat across theory cases); the guards' tests are plain facts, so their names must
also match the expected set one to one.

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
    def __init__(self, name, setup, check, kind, tests=()):
        self.name, self.setup, self.check, self.kind, self.tests = name, setup, check, kind, tuple(tests)


def _test_guard(name, project, filter_, tests):
    trx = f'{TRX_DIR}/{name}.trx'
    return Guard(name,
                 setup=[['dotnet', 'build', project, '-c', 'Release']],
                 check=['dotnet', 'test', project, '-c', 'Release', '--no-build', '--filter', filter_,
                        '--logger', f'trx;LogFileName={os.path.abspath(trx)}'],
                 kind=('trx', trx), tests=tests)


SNAPSHOT_TESTS = [f'Boo.Core.Tests.PerfProbeSnapshotTests.{m}' for m in (
    'Each_process_counter_is_written_to_its_own_field_in_milliseconds',
    'The_fields_the_perf_test_reads_keep_their_names_order_and_values',
    'The_write_meter_counts_every_write_and_adds_up_their_time')]
READER_TESTS = [f'Boo.Integration.Tests.RuntimeProbeCountersTests.{m}' for m in (
    'Busy_managed_work_is_read_as_user_cpu_not_kernel_cpu',
    'Compiling_a_new_method_raises_the_jit_counters',
    'The_probe_write_meter_is_passed_through')] + [
    f'Boo.Integration.Tests.RuntimeProbeCountersThreadTests.{m}' for m in (
        'Jit_methods_compiled_on_another_thread_are_counted',
        'Jit_time_spent_on_another_thread_is_counted')]


GUARDS = {
    'runtime-settings': Guard('runtime-settings',
                              setup=[['dotnet', 'publish', CSPROJ, '-c', 'Release', '-r', 'win-x64', '--self-contained',
                                      'true', '-o', 'artifacts/guard-publish']],
                              check=[sys.executable, '-I', 'scripts/perf/check_runtime_settings.py', 'check',
                                     'artifacts/guard-publish'],
                              kind=('text', 'RUNTIME_SETTINGS_OK', 'RUNTIME_SETTINGS_FAILED')),
    'snapshot-tests': _test_guard('snapshot-tests', 'tests/Boo.Core.Tests', 'FullyQualifiedName~PerfProbeSnapshotTests',
                                  SNAPSHOT_TESTS),
    # matches RuntimeProbeCountersTests and RuntimeProbeCountersThreadTests
    'reader-tests': _test_guard('reader-tests', 'tests/Boo.Integration.Tests', 'FullyQualifiedName~RuntimeProbeCounters',
                                READER_TESTS),
    'summary-self-test': Guard('summary-self-test', setup=[], check=[sys.executable, '-I', SUMMARY, '--self-test'],
                               kind=('selftest',)),
}

# id -> (file, exact text, replacement, guard, the evidence its failure must show). For a TRX guard the evidence is the
# test name, or a tuple of names, that must fail: exactly that set, no more and no fewer.
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
             'JitMethods: 0,\n            JitTime: TimeSpan.Zero,', 'reader-tests',
             ('Compiling_a_new_method_raises_the_jit_counters', 'Jit_methods_compiled_on_another_thread_are_counted',
              'Jit_time_spent_on_another_thread_is_counted')),
    'M04r': (READER, 'ProbeWrites: meter.Writes,\n            ProbeWriteTime: meter.Total);',
             'ProbeWrites: 0,\n            ProbeWriteTime: TimeSpan.Zero);', 'reader-tests',
             'The_probe_write_meter_is_passed_through'),
    'M11': (READER, 'System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: false)',
            'System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true)', 'reader-tests',
            'Jit_methods_compiled_on_another_thread_are_counted'),
    'M12': (READER, 'System.Runtime.JitInfo.GetCompilationTime(currentThread: false)',
            'System.Runtime.JitInfo.GetCompilationTime(currentThread: true)', 'reader-tests',
            'Jit_time_spent_on_another_thread_is_counted'),
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


BAD_COUNTERS = ('error', 'timeout', 'aborted', 'inconclusive', 'passedButRunAborted', 'notRunnable', 'notExecuted',
                'disconnected', 'inProgress', 'pending')


def read_trx(path):
    """Returns (receipt, None) or (None, problem). receipt = {'outcome', 'counters', 'results': [(name, outcome)]}."""
    try:
        root = ET.parse(path).getroot()
    except FileNotFoundError:
        return None, f'no TRX at {path}'
    except (OSError, ET.ParseError) as e:
        return None, f'unreadable TRX ({type(e).__name__})'
    local = lambda el: el.tag.rsplit('}', 1)[-1]
    if local(root) != 'TestRun':
        return None, f'root element is {local(root)}, not TestRun'
    summaries = [el for el in root.iter() if local(el) == 'ResultSummary']
    if len(summaries) != 1:
        return None, f'{len(summaries)} ResultSummary elements, expected exactly 1'
    counters = [el for el in summaries[0] if local(el) == 'Counters']
    if len(counters) != 1:
        return None, f'{len(counters)} Counters in the ResultSummary, expected exactly 1'
    values = {}
    for k, v in counters[0].attrib.items():
        if not v.isdigit():
            return None, f'counter {k}="{v}" is not a whole number'
        values[k] = int(v)
    records = [el for el in root.iter() if local(el) == 'UnitTestResult']
    results = [(el.get('testName', ''), el.get('outcome', '')) for el in records]
    execution_ids = [el.get('executionId', '') for el in records]
    return {'outcome': summaries[0].get('outcome', ''), 'counters': values, 'results': results,
            'executionIds': execution_ids}, None


def trx_problem(receipt, tests, run_failed):
    """Why this receipt is not a complete, valid run of exactly `tests` (None when it is)."""
    c, results = receipt['counters'], receipt['results']
    want_outcome = 'Failed' if run_failed else 'Completed'
    if receipt['outcome'] != want_outcome:
        return f'summary outcome {receipt["outcome"]!r}, expected {want_outcome!r}'
    for k in ('total', 'executed', 'passed', 'failed'):
        if k not in c:
            return f'counter {k} is missing'
    bad = {k: c[k] for k in BAD_COUNTERS if c.get(k, 0) != 0}
    if bad:
        return f'counters show an incomplete run: {bad}'
    if not c['total'] == c['executed'] == c['passed'] + c['failed'] == len(tests):
        return (f'counters total {c["total"]} executed {c["executed"]} passed {c["passed"]} failed {c["failed"]}, '
                f'expected all {len(tests)} tests run')
    names = [n for n, _ in results]
    ids = receipt.get('executionIds') or []
    if any(not i for i in ids) or len(ids) != len(set(ids)):
        return 'a result record has no executionId or appears twice'
    if len(names) != len(tests) or set(names) != set(tests):
        missing, extra = sorted(set(tests) - set(names)), sorted(set(names) - set(tests))
        return f'result records do not match the guard tests (missing {missing}, unexpected {extra})'
    outcomes = [o for _, o in results]
    if any(o not in ('Passed', 'Failed') for o in outcomes):
        return f'a result outcome is neither Passed nor Failed: {sorted(set(outcomes))}'
    if outcomes.count('Passed') != c['passed'] or outcomes.count('Failed') != c['failed']:
        return 'result records and counters disagree on passed/failed'
    return None


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
        receipt, problem = read(kind[1])
        if problem:
            return INVALID, f'{problem} (exit {rc})'
        failed = [n for n, o in receipt['results'] if o == 'Failed']
        problem = trx_problem(receipt, guard.tests, run_failed=bool(failed))
        if problem:
            return INVALID, f'invalid TRX receipt: {problem} (exit {rc})'
        if (rc == 0) == bool(failed):
            return INVALID, f'exit {rc} contradicts the receipt ({len(failed)} failed)'
        if expect is None:
            return (KILLED, f'all {len(guard.tests)} tests passed') if not failed else (SURVIVED, f'the real code failed: {failed}')
        if not failed:
            return SURVIVED, f'all {len(guard.tests)} tests passed with the fault in place'
        names = (expect,) if isinstance(expect, str) else tuple(expect)
        wanted = sorted(t for t in guard.tests if t.rsplit('.', 1)[-1] in names)
        if len(wanted) == len(names) and sorted(failed) == wanted:
            return KILLED, f'{", ".join(names)} failed, the other {len(guard.tests) - len(names)} passed'
        return INVALID, f'failing tests {failed}, expected exactly {list(names)}'
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
    print(f'applied {target}; guard {guard_name} must fail with {expect!r}', flush=True)
    verdict, why = run_guard(GUARDS[guard_name], expect, runner, read)
    print(f'MUTANT_{verdict} {target} ({guard_name}): {why}')
    return EXIT[verdict]


COUNTER_NAMES = ('total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'inconclusive',
                 'passedButRunAborted', 'notRunnable', 'notExecuted', 'disconnected', 'warning', 'completed', 'inProgress',
                 'pending')


def make_trx(results, outcome=None, counters=None, summaries=1, root='TestRun', duplicate_id=False):
    """A TRX file in the exact shape `dotnet test --logger trx` writes (namespace, attribute set, fully qualified names);
    counters default to the ones that match `results`."""
    passed = sum(o == 'Passed' for _, o in results)
    failed = sum(o == 'Failed' for _, o in results)
    c = {k: 0 for k in COUNTER_NAMES}
    c.update(total=len(results), executed=len(results), passed=passed, failed=failed)
    c.update(counters or {})
    outcome = outcome or ('Failed' if failed else 'Completed')
    rows = ''.join(f'<UnitTestResult executionId="e{0 if duplicate_id else i}" testId="t{i}" testName="{n}" computerName="vm" '
                   f'duration="00:00:00.001" outcome="{o}" testListId="l" />' for i, (n, o) in enumerate(results))
    summary = (f'<ResultSummary outcome="{outcome}"><Counters '
               + ' '.join(f'{k}="{v}"' for k, v in c.items()) + ' /></ResultSummary>')
    return ('<?xml version="1.0" encoding="utf-8"?>'
            f'<{root} id="r" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
            f'<Results>{rows}</Results>' + summary * summaries + f'</{root}>')


def self_test():
    """Fake runs and real-shaped TRX files: no build, no tests. Every way a cell can end without its guard asserting
    must be INVALID; only a valid receipt with exactly the named failure is KILLED."""
    expect_c = MUTANTS['M02'][4]
    named = next(t for t in SNAPSHOT_TESTS if t.endswith('.' + expect_c))
    all_pass = [(t, 'Passed') for t in SNAPSHOT_TESTS]
    one_fail = [(t, 'Failed' if t == named else 'Passed') for t in SNAPSHOT_TESTS]
    other_fail = [(t, 'Failed' if t == SNAPSHOT_TESTS[1] else 'Passed') for t in SNAPSHOT_TESTS]
    T = lambda **kw: ('xml', kw)  # a TRX file built by make_trx
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
        # Valid receipts.
        ('valid receipt, exactly the named test fails: KILLED', 'snapshot-tests', expect_c, [(0, ''), (1, '')], T(results=one_fail), KILLED),
        ('valid receipt, all pass with the fault: SURVIVED', 'snapshot-tests', expect_c, [(0, ''), (0, '')], T(results=all_pass), SURVIVED),
        ('control: valid all-pass receipt passes', 'snapshot-tests', None, [(0, ''), (0, '')], T(results=all_pass), KILLED),
        ('control: a build failure is INVALID, not a pass', 'snapshot-tests', None, [(1, 'error')], T(results=all_pass), INVALID),
        ('a different test fails: INVALID', 'snapshot-tests', expect_c, [(0, ''), (1, '')], T(results=other_fail), INVALID),
        ('a set of named tests fails, exactly that set: KILLED', 'snapshot-tests', (expect_c, SNAPSHOT_TESTS[1].rsplit('.', 1)[-1]),
         [(0, ''), (1, '')], T(results=[(t, 'Passed' if t == SNAPSHOT_TESTS[2] else 'Failed') for t in SNAPSHOT_TESTS]), KILLED),
        ('only part of a named set fails: INVALID', 'snapshot-tests', (expect_c, SNAPSHOT_TESTS[1].rsplit('.', 1)[-1]),
         [(0, ''), (1, '')], T(results=one_fail), INVALID),
        ('a named set plus one more fails: INVALID', 'snapshot-tests', (expect_c, SNAPSHOT_TESTS[1].rsplit('.', 1)[-1]),
         [(0, ''), (1, '')], T(results=[(t, 'Failed') for t in SNAPSHOT_TESTS]), INVALID),
        ('a named test that is not in the guard: INVALID', 'snapshot-tests', 'No_such_test', [(0, ''), (1, '')],
         T(results=one_fail), INVALID),
        # Invalid receipts (Codex review BLOCK on 1b1260d): each one used to be KILLED.
        ('summary outcome Aborted: INVALID', 'snapshot-tests', expect_c, [(0, ''), (1, '')], T(results=one_fail, outcome='Aborted'), INVALID),
        ('no ResultSummary at all: INVALID', 'snapshot-tests', expect_c, [(0, ''), (1, '')], T(results=one_fail, summaries=0), INVALID),
        ('two ResultSummary elements: INVALID', 'snapshot-tests', expect_c, [(0, ''), (1, '')], T(results=one_fail, summaries=2), INVALID),
        ('only the failing record while counters claim three: INVALID', 'snapshot-tests', expect_c, [(0, ''), (1, '')],
         T(results=[(named, 'Failed')], counters={'total': 3, 'executed': 3, 'passed': 2, 'failed': 1}), INVALID),
        ('aborted counter 1: INVALID', 'snapshot-tests', expect_c, [(0, ''), (1, '')], T(results=one_fail, counters={'aborted': 1}), INVALID),
        ('error counter 1: INVALID', 'snapshot-tests', expect_c, [(0, ''), (1, '')], T(results=one_fail, counters={'error': 1}), INVALID),
        ('a skipped test (notExecuted 1, executed 2): INVALID', 'snapshot-tests', expect_c, [(0, ''), (1, '')],
         T(results=one_fail[:2] + [(SNAPSHOT_TESTS[2], 'NotExecuted')], counters={'executed': 2, 'notExecuted': 1}), INVALID),
        ('the same test recorded twice: INVALID', 'snapshot-tests', expect_c, [(0, ''), (1, '')],
         T(results=one_fail[:2] + [one_fail[0]]), INVALID),
        ('a record repeated with the same executionId: INVALID', 'snapshot-tests', expect_c, [(0, ''), (1, '')],
         T(results=one_fail, duplicate_id=True), INVALID),
        ('a result for a test outside the guard: INVALID', 'snapshot-tests', expect_c, [(0, ''), (1, '')],
         T(results=one_fail[:2] + [('Boo.Core.Tests.Other.Test', 'Passed')]), INVALID),
        ('counters disagree with the records (failed 2): INVALID', 'snapshot-tests', expect_c, [(0, ''), (1, '')],
         T(results=one_fail, counters={'passed': 1, 'failed': 2}), INVALID),
        ('Completed outcome with a failed record: INVALID', 'snapshot-tests', expect_c, [(0, ''), (1, '')],
         T(results=one_fail, outcome='Completed'), INVALID),
        ('exit 0 although the receipt shows a failure: INVALID', 'snapshot-tests', expect_c, [(0, ''), (0, '')], T(results=one_fail), INVALID),
        ('control: exit 1 with an all-pass receipt: INVALID', 'snapshot-tests', None, [(0, ''), (1, '')], T(results=all_pass), INVALID),
        ('a root element that is not TestRun: INVALID', 'snapshot-tests', expect_c, [(0, ''), (1, '')], T(results=one_fail, root='Other'), INVALID),
        # Summary self-test.
        ('summary self-test crashes: INVALID', 'summary-self-test', '[WRONG] row 1', [(1, 'Traceback (most recent call last):\n')], None, INVALID),
        ('summary self-test fails without a result line: INVALID', 'summary-self-test', '[WRONG] row 1', [(1, 'error')], None, INVALID),
        ('summary self-test reports the wrong row: KILLED', 'summary-self-test', '[WRONG] row 1',
         [(1, '  [WRONG] row 1\nself-test: 8/9\n')], None, KILLED),
        ('summary self-test passes with the fault: SURVIVED', 'summary-self-test', '[WRONG] row 1', [(0, 'self-test: 9/9\n')], None, SURVIVED),
    ]
    failures = 0
    with tempfile.TemporaryDirectory() as d:
        for i, (name, guard_name, expect, results, trx, want) in enumerate(cases):
            queue = list(results)

            def fake_run(cmd, queue=queue):
                return queue.pop(0) if queue else (99, 'unexpected extra command')
            path = os.path.join(d, f'case{i}.trx')  # absent unless the case has a receipt
            if trx is not None:
                with open(path, 'w', encoding='utf-8') as f:
                    f.write(make_trx(**trx[1]))
            verdict, why = run_guard(GUARDS[guard_name], expect, fake_run, lambda _p, path=path: read_trx(path))
            ok = verdict == want
            failures += not ok
            print(f'  [{"ok" if ok else "WRONG"}] {name}' + ('' if ok else f': got {verdict} ({why})'))
    # The fixture shape is what dotnet really writes: the same parser reads a real-format receipt's counters.
    with tempfile.TemporaryDirectory() as d:
        p = os.path.join(d, 'r.trx')
        open(p, 'w', encoding='utf-8').write(make_trx(one_fail))
        got, problem = read_trx(p)
        _, missing = read_trx(os.path.join(d, 'none.trx'))
    ok = (problem is None and got['outcome'] == 'Failed' and got['counters']['total'] == 3
          and sorted(got['results']) == sorted(one_fail) and missing and missing.startswith('no TRX'))
    failures += not ok
    print(f'  [{"ok" if ok else "WRONG"}] the TRX reader returns outcome, counters and records; a missing file is a problem'
          + ('' if ok else f': got {got} {problem} {missing}'))
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
