"""Issue #124 diagnostic summary: turns scripts/perf/idle_diag.ps1 samples into one table per launch.

For each launch it takes the same 15 s window the perf budget test measures (starting 6 s after the notch appears)
and a later 15 s window (30 s on), and splits the CPU the test sees from outside into what the probe reports from
inside: user vs kernel time, JIT compilation (methods and ms), the probe's own writes, and GC. It also prints the
worst single second, so a burst shows where it sits. It never fails on a CPU number.

--strict fails (exit 1) when the data cannot support the table, whatever the CPU (Codex review BLOCK on 5a3922b: a
strict run with no usable measurement must not pass):
  - a launch with fewer than 5 probe snapshots, or a test or late window its samples do not cover (a sample within
    1.5 s of each end of the window);
  - with --expect-launches N, any launch 1..N without a sample file;
  - a measured window without about 30 probe writes (one every 500 ms), or a launch whose JIT counters never move
    (a probe field read or written as zero).
Without --strict the same problems are printed as notes and the exit code is 0.

--self-test runs the table maths on a built-in fixture with uneven CPU, separate test and late windows, missing
snapshots and too-short launches, compares every row with exact expected text, and exits 1 on any difference.

    python -I scripts/perf/idle_diag_summary.py <dir with samples-*.jsonl> [--out summary.md] [--strict]
                                                [--expect-launches N]
    python -I scripts/perf/idle_diag_summary.py --self-test
"""
import argparse
import glob
import json
import os
import sys
import tempfile

TICK_MS = 15.625  # the Windows clock tick that process CPU time is charged in


def load(path):
    head, samples, misses = None, [], []
    with open(path, encoding='utf-8-sig') as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            row = json.loads(line)
            if head is None:
                head = row
            elif row.get('diag'):
                samples.append(row)
            else:
                misses.append(row.get('readError') or 'no diag')
    return head, samples, misses


def at(samples, t_ms):
    return min(samples, key=lambda s: abs(s['tMs'] - t_ms))


def delta(a, b):
    da, db = a['diag'], b['diag']
    wall = (b['tMs'] - a['tMs']) / 1000.0
    d = lambda k: (db.get(k) or 0) - (da.get(k) or 0)
    ext = b['extCpuMs'] - a['extCpuMs']
    return {
        'wallS': wall,
        'cpuPct': ext / wall / 10.0 if wall > 0 else float('nan'),
        'ticks': round(ext / TICK_MS),
        'userMs': d('userCpuMs'),
        'kernelMs': d('kernelCpuMs'),
        'jitMethods': d('jitMethods'),
        'jitMs': d('jitMs'),
        'probeWrites': d('probeWrites'),
        'probeWriteMs': d('probeWriteMs'),
        'gen0': d('gen0'),
        'gen1': d('gen1'),
        'gen2': d('gen2'),
    }


def fmt(x):
    return f'{x:.3f}' if isinstance(x, float) else str(x)


PROBE_WRITES_PER_WINDOW = (25, 35)  # 15 s at one write every 500 ms is 30
EDGE_MS = 1500  # a window counts as covered when a sample sits within this of each end


def render(directory, expect_launches=None):
    """Returns (markdown text, data problems). None for the text when there are no sample files."""
    files = sorted(glob.glob(os.path.join(directory, 'samples-*.jsonl')),
                   key=lambda p: int(os.path.basename(p).split('-')[1].split('.')[0]))
    if not files:
        return None, []
    problems = []
    if expect_launches:
        have = {int(os.path.basename(p).split('-')[1].split('.')[0]) for p in files}
        for n in range(1, expect_launches + 1):
            if n not in have:
                problems.append(f'launch {n}: no samples-{n}.jsonl')
    cols = ['wallS', 'cpuPct', 'ticks', 'userMs', 'kernelMs', 'jitMethods', 'jitMs', 'probeWrites', 'probeWriteMs',
            'gen0', 'gen1', 'gen2']
    out = ['# Idle CPU diagnostic (issue #124)', '',
           'Window "test" = the perf test\'s window (15 s from 6 s after the notch appears). "late" = 15 s from 30 s '
           'after it. cpuPct and ticks are what the test sees from outside; the rest is the probe\'s own split.', '',
           '| launch | window | ' + ' | '.join(cols) + ' | worst second |', '|' + ' --- |' * (len(cols) + 3)]
    for path in files:
        head, s, misses = load(path)
        if misses:
            out.append(f"| {head['launch']} | note | {len(misses)} sample(s) without a probe snapshot, first: "
                       f"{str(misses[0])[:120]} |")
        if len(s) < 5:
            out.append(f"| {head['launch']} | too few samples ({len(s)}) |")
            problems.append(f"launch {head['launch']}: only {len(s)} probe snapshot(s), {len(misses)} missing; "
                            "no window can be measured")
            continue
        start = head['notchFoundMs'] + 6000
        for name, t0 in (('test', start), ('late', head['notchFoundMs'] + 30000)):
            a, b = at(s, t0), at(s, t0 + 15000)
            if abs(a['tMs'] - t0) > EDGE_MS or abs(b['tMs'] - (t0 + 15000)) > EDGE_MS:
                out.append(f"| {head['launch']} | {name} | skipped: samples cover {s[0]['tMs']:.0f} to "
                           f"{s[-1]['tMs']:.0f} ms only |")
                problems.append(f"launch {head['launch']} {name} window ({t0:.0f} to {t0 + 15000:.0f} ms) is not "
                                f"covered: samples run {s[0]['tMs']:.0f} to {s[-1]['tMs']:.0f} ms")
                continue
            row = delta(a, b)
            lo, hi = PROBE_WRITES_PER_WINDOW
            if not lo <= row['probeWrites'] <= hi:
                problems.append(f"launch {head['launch']} {name} window: {row['probeWrites']} probe writes, expected "
                                f"{lo} to {hi} (one every 500 ms)")
            seconds = [x for x in s if a['tMs'] <= x['tMs'] <= b['tMs']]
            worst = max((delta(p, q) for p, q in zip(seconds, seconds[1:])), key=lambda r: r['ticks'], default=None)
            w = (f"{worst['ticks']} ticks (user {worst['userMs']:.0f} / kernel {worst['kernelMs']:.0f} ms, "
                 f"jit {worst['jitMethods']} m / {worst['jitMs']:.1f} ms)") if worst else '-'
            out.append(f"| {head['launch']} | {name} | " + ' | '.join(fmt(row[c]) for c in cols) + f' | {w} |')
        if not s[-1]['diag'].get('jitMethods') or s[-1]['diag'].get('jitMs', 0) <= 0:
            problems.append(f"launch {head['launch']}: the JIT counters never moved (jitMethods "
                            f"{s[-1]['diag'].get('jitMethods')}, jitMs {s[-1]['diag'].get('jitMs')})")
    return '\n'.join(out) + '\n', problems


def _write_fixture(directory):
    """Three launches. 1: uneven CPU, 31 ms/s up to 22 s then 62 ms/s; 2: three missing snapshots, two good;
    3: samples only from 2 to 9 s. Every per-second number is a whole number so the expected text is exact."""
    def line(obj):
        return json.dumps(obj) + '\n'

    def snap(t):
        sec = t / 1000
        early, late = min(sec, 22), max(sec - 22, 0)
        return {
            'tMs': float(t), 'extCpuMs': float(31 * early + 62 * late),
            'diag': {'userCpuMs': float(20 * early + 40 * late), 'kernelCpuMs': float(11 * early + 22 * late),
                     'jitMethods': int(10 * early + 2 * late), 'jitMs': float(5 * early + 1 * late),
                     'probeWrites': int(2 * sec), 'probeWriteMs': float(0.5 * sec),
                     'gen0': (t >= 10000) + (t >= 40000), 'gen1': 0, 'gen2': 0},
        }
    with open(os.path.join(directory, 'samples-1.jsonl'), 'w', encoding='utf-8') as f:
        f.write(line({'launch': 1, 'notchFoundMs': 1000, 'pid': 1}))
        for t in range(2000, 53000, 1000):
            f.write(line(snap(t)))
    with open(os.path.join(directory, 'samples-2.jsonl'), 'w', encoding='utf-8') as f:
        f.write(line({'launch': 2, 'notchFoundMs': 1000, 'pid': 2}))
        for t in (2000, 3000, 4000):
            f.write(line({'tMs': float(t), 'extCpuMs': 0.0, 'diag': None, 'readError': 'IOException'}))
        for t in (5000, 6000):
            f.write(line(snap(t)))
    with open(os.path.join(directory, 'samples-3.jsonl'), 'w', encoding='utf-8') as f:
        f.write(line({'launch': 3, 'notchFoundMs': 1000, 'pid': 3}))
        for t in range(2000, 10000, 1000):
            f.write(line(snap(t)))


# Worked by hand from the fixture, not by this script: test window 7 to 22 s, 15 s x 31 ms = 465 ms = 3.1 %, 29.76 ticks;
# late window 31 to 46 s, 15 s x 62 ms = 930 ms = 6.2 %, 59.52 ticks.
EXPECTED_ROWS = [
    '| 1 | test | 15.000 | 3.100 | 30 | 300.000 | 165.000 | 150 | 75.000 | 30 | 7.500 | 1 | 0 | 0 | '
    '2 ticks (user 20 / kernel 11 ms, jit 10 m / 5.0 ms) |',
    '| 1 | late | 15.000 | 6.200 | 60 | 600.000 | 330.000 | 30 | 15.000 | 30 | 7.500 | 1 | 0 | 0 | '
    '4 ticks (user 40 / kernel 22 ms, jit 2 m / 1.0 ms) |',
    '| 2 | note | 3 sample(s) without a probe snapshot, first: IOException |',
    '| 2 | too few samples (2) |',
    '| 3 | test | skipped: samples cover 2000 to 9000 ms only |',
    '| 3 | late | skipped: samples cover 2000 to 9000 ms only |',
]


def self_test():
    failures = 0
    with tempfile.TemporaryDirectory() as d:
        _write_fixture(d)
        text, problems = render(d)
        rows = [r for r in text.splitlines() if r.startswith('| ') and r[2:3].isdigit()]
        for i in range(max(len(rows), len(EXPECTED_ROWS))):
            want = EXPECTED_ROWS[i] if i < len(EXPECTED_ROWS) else '(no row)'
            got = rows[i] if i < len(rows) else '(no row)'
            ok = want == got
            failures += not ok
            print(f'  [{"ok" if ok else "WRONG"}] row {i + 1}' + ('' if ok else f'\n      want {want}\n      got  {got}'))
        want_problems = [
            'launch 2: only 2 probe snapshot(s), 3 missing; no window can be measured',
            'launch 3 test window (7000 to 22000 ms) is not covered: samples run 2000 to 9000 ms',
            'launch 3 late window (31000 to 46000 ms) is not covered: samples run 2000 to 9000 ms',
        ]
        ok = problems == want_problems
        failures += not ok
        print(f'  [{"ok" if ok else "WRONG"}] the fixture reports exactly its unusable launches as problems'
              + ('' if ok else f': {problems}'))
        # --strict exit codes on whole directories: unusable data fails, a complete launch passes.
        strict_cases = [('the fixture (launches 2 and 3 unusable) fails --strict', d, [], 1)]
        with tempfile.TemporaryDirectory() as good, tempfile.TemporaryDirectory() as missing_only, \
                tempfile.TemporaryDirectory() as gap:
            with open(os.path.join(d, 'samples-1.jsonl'), encoding='utf-8') as f:
                launch1 = f.read()
            with open(os.path.join(good, 'samples-1.jsonl'), 'w', encoding='utf-8') as f:
                f.write(launch1)
            with open(os.path.join(missing_only, 'samples-1.jsonl'), 'w', encoding='utf-8') as f:
                f.write(json.dumps({'launch': 1, 'notchFoundMs': 1000, 'pid': 1}) + '\n')
                for t in range(2000, 53000, 1000):
                    f.write(json.dumps({'tMs': float(t), 'extCpuMs': 0.0, 'diag': None, 'readError': 'IOException'}) + '\n')
            # a hole in the middle: samples stop at 15 s and resume at 40 s, so neither window has both ends
            with open(os.path.join(gap, 'samples-1.jsonl'), 'w', encoding='utf-8') as f:
                for line in launch1.splitlines(keepends=True):
                    row = json.loads(line)
                    if 'tMs' not in row or not 15000 < row['tMs'] < 40000:
                        f.write(line)
            strict_cases += [
                ('one complete launch passes --strict', good, [], 0),
                ('a launch whose every snapshot is missing fails --strict', missing_only, [], 1),
                ('a gap inside the windows fails --strict', gap, [], 1),
                ('a launch file that never appeared fails --strict --expect-launches 2', good, ['--expect-launches', '2'], 1),
                ('without --strict unusable data is reported but exits 0', d, None, 0),
            ]
            for name, directory, extra, want in strict_cases:
                args = [directory] + (['--strict'] + extra if extra is not None else [])
                with open(os.devnull, 'w') as sink:
                    real, sys.stdout = sys.stdout, sink
                    try:
                        code = main(args)
                    finally:
                        sys.stdout = real
                ok = code == want
                failures += not ok
                print(f'  [{"ok" if ok else "WRONG"}] {name}' + ('' if ok else f': exit {code}, want {want}'))
        # --strict: a probe that writes nothing, or whose JIT counters stay at zero, must be caught.
        for field, expect in (('probeWrites', 'probe writes'), ('jitMethods', 'JIT counters never moved')):
            with open(os.path.join(d, 'samples-1.jsonl'), encoding='utf-8') as f:
                lines = f.readlines()
            broken = [lines[0]]
            for raw in lines[1:]:
                row = json.loads(raw)
                row['diag'][field] = 0
                broken.append(json.dumps(row) + '\n')
            with tempfile.TemporaryDirectory() as d2:
                with open(os.path.join(d2, 'samples-1.jsonl'), 'w', encoding='utf-8') as f:
                    f.writelines(broken)
                _, problems = render(d2)
            ok = any(expect in p for p in problems)
            failures += not ok
            print(f'  [{"ok" if ok else "WRONG"}] --strict catches {field} stuck at zero' + ('' if ok else f': {problems}'))
    total = len(EXPECTED_ROWS) + 3 + 6
    print(f'self-test: {total - failures}/{total}')
    return 1 if failures else 0


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    if argv == ['--self-test']:
        return self_test()
    ap = argparse.ArgumentParser()
    ap.add_argument('directory')
    ap.add_argument('--out')
    ap.add_argument('--strict', action='store_true')
    ap.add_argument('--expect-launches', type=int)
    args = ap.parse_args(argv)
    text, problems = render(args.directory, args.expect_launches)
    if text is None:
        print('no samples-*.jsonl found', file=sys.stderr)
        return 2
    print(text)
    if args.out:
        with open(args.out, 'w', encoding='utf-8') as f:
            f.write(text)
    for p in problems:
        print(f'{"::error::" if args.strict else "note: "}{p}')
    return 1 if args.strict and problems else 0


if __name__ == '__main__':
    sys.exit(main())
