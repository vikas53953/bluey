"""Prove the #124 / PR #142 guards bite: apply one known mutant and require its guard to FAIL (or, for `control`,
require every guard to PASS on the real code).

The mutants are the ones the Codex mutation run found surviving on 61361c2 (M01 to M07), plus the same faults at the
new reader site (M02r, M03r, M04r). Each is an exact text replacement; if the text is not there (the code moved) this
fails loudly instead of testing nothing.

    python -I scripts/perf/guard_mutants.py list
    python -I scripts/perf/guard_mutants.py run <id|control>

Runs in CI (.github/workflows/perf-guards.yml) on a throwaway checkout: it edits files and never restores them.
"""
import subprocess
import sys

CSPROJ = 'src/Boo.App/Boo.App.csproj'
SNAPSHOT = 'src/Boo.Core/PerfProbeSnapshot.cs'
READER = 'src/Boo.Windows/RuntimeProbeCounters.cs'
SUMMARY = 'scripts/perf/idle_diag_summary.py'

PUBLISH = ['dotnet', 'publish', CSPROJ, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', 'artifacts/guard-publish']
GUARDS = {
    'runtime-settings': [PUBLISH, [sys.executable, '-I', 'scripts/perf/check_runtime_settings.py', 'check', 'artifacts/guard-publish']],
    'snapshot-tests': [['dotnet', 'test', 'tests/Boo.Core.Tests', '-c', 'Release', '--filter', 'FullyQualifiedName~PerfProbeSnapshotTests']],
    'reader-tests': [['dotnet', 'test', 'tests/Boo.Integration.Tests', '-c', 'Release', '--filter', 'FullyQualifiedName~RuntimeProbeCountersTests']],
    'summary-self-test': [[sys.executable, '-I', SUMMARY, '--self-test']],
}

# id -> (file, exact text, replacement, guard)
MUTANTS = {
    'M01': (CSPROJ, '<TieredPGO>false</TieredPGO>', '<TieredPGO>true</TieredPGO>', 'runtime-settings'),
    'M02': (SNAPSHOT, 'counters.UserCpu.TotalMilliseconds,\n            counters.KernelCpu.TotalMilliseconds,',
            'counters.KernelCpu.TotalMilliseconds,\n            counters.UserCpu.TotalMilliseconds,', 'snapshot-tests'),
    'M03': (SNAPSHOT, 'counters.JitMethods,\n            counters.JitTime.TotalMilliseconds,', '0,\n            0,', 'snapshot-tests'),
    'M04': (SNAPSHOT, 'counters.ProbeWrites,\n            counters.ProbeWriteTime.TotalMilliseconds);', '0,\n            0);', 'snapshot-tests'),
    'M02r': (READER, 'UserCpu: cpu.UserTime,\n            KernelCpu: cpu.PrivilegedTime,',
             'UserCpu: cpu.PrivilegedTime,\n            KernelCpu: cpu.UserTime,', 'reader-tests'),
    'M03r': (READER, 'JitMethods: System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: false),\n'
                     '            JitTime: System.Runtime.JitInfo.GetCompilationTime(currentThread: false),',
             'JitMethods: 0,\n            JitTime: TimeSpan.Zero,', 'reader-tests'),
    'M04r': (READER, 'ProbeWrites: meter.Writes,\n            ProbeWriteTime: meter.Total);',
             'ProbeWrites: 0,\n            ProbeWriteTime: TimeSpan.Zero);', 'reader-tests'),
    'M05': (SUMMARY, "wall = (b['tMs'] - a['tMs']) / 1000.0", "wall = (b['tMs'] - a['tMs']) / 100.0", 'summary-self-test'),
    'M06': (SUMMARY, "start = head['notchFoundMs'] + 6000", "start = head['notchFoundMs'] + 30000", 'summary-self-test'),
    'M07': (SUMMARY, '        if misses:\n', '        if False and misses:\n', 'summary-self-test'),
}


def run_guard(name):
    for cmd in GUARDS[name]:
        print('$ ' + ' '.join(cmd), flush=True)
        if subprocess.call(cmd) != 0:
            return False
    return True


def main(argv):
    if argv == ['list']:
        print(' '.join(['control', *MUTANTS]))
        return 0
    if len(argv) != 2 or argv[0] != 'run':
        print(__doc__, file=sys.stderr)
        return 2
    target = argv[1]
    if target == 'control':
        failed = [g for g in GUARDS if not run_guard(g)]
        if failed:
            print(f'GUARD_CONTROL_FAILED: on the real code these guards failed: {failed}')
            return 1
        print(f'GUARD_CONTROL_OK: all {len(GUARDS)} guards pass on the real code')
        return 0
    if target not in MUTANTS:
        print(f'unknown mutant {target}', file=sys.stderr)
        return 2
    path, old, new, guard = MUTANTS[target]
    with open(path, encoding='utf-8') as f:  # .gitattributes keeps LF; universal newlines read either way
        text = f.read()
    if text.count(old) != 1:
        print(f'MUTANT_NOT_APPLIED {target}: the target text is in {path} {text.count(old)} times, expected once')
        return 1
    with open(path, 'w', encoding='utf-8', newline='') as f:
        f.write(text.replace(old, new))
    print(f'applied {target} to {path}; running guard {guard} (it must FAIL)', flush=True)
    if run_guard(guard):
        print(f'MUTANT_SURVIVED {target}: guard {guard} passed with the fault in place')
        return 1
    print(f'MUTANT_KILLED {target} by {guard}')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
