"""Issue #124 diagnostic summary: turns scripts/perf/idle_diag.ps1 samples into one table per launch.

For each launch it takes the same 15 s window the perf budget test measures (starting 6 s after the notch appears)
and a later 15 s window (30 s on), and splits the CPU the test sees from outside into what the probe reports from
inside: user vs kernel time, JIT compilation (methods and ms), the probe's own writes, and GC. It also prints the
worst single second, so a burst shows where it sits. Measures only; it never fails on a number.

    python -I scripts/perf/idle_diag_summary.py <dir with samples-*.jsonl> [--out summary.md]
"""
import argparse
import glob
import json
import os
import sys

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


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument('directory')
    ap.add_argument('--out')
    args = ap.parse_args(argv)
    files = sorted(glob.glob(os.path.join(args.directory, 'samples-*.jsonl')),
                   key=lambda p: int(os.path.basename(p).split('-')[1].split('.')[0]))
    if not files:
        print('no samples-*.jsonl found', file=sys.stderr)
        return 2
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
            continue
        start = head['notchFoundMs'] + 6000
        for name, t0 in (('test', start), ('late', head['notchFoundMs'] + 30000)):
            a, b = at(s, t0), at(s, t0 + 15000)
            if b['tMs'] - a['tMs'] < 10000:
                out.append(f"| {head['launch']} | {name} | skipped: samples cover {s[0]['tMs']:.0f} to "
                           f"{s[-1]['tMs']:.0f} ms only |")
                continue
            row = delta(a, b)
            seconds = [x for x in s if a['tMs'] <= x['tMs'] <= b['tMs']]
            worst = max((delta(p, q) for p, q in zip(seconds, seconds[1:])), key=lambda r: r['ticks'], default=None)
            w = (f"{worst['ticks']} ticks (user {worst['userMs']:.0f} / kernel {worst['kernelMs']:.0f} ms, "
                 f"jit {worst['jitMethods']} m / {worst['jitMs']:.1f} ms)") if worst else '-'
            out.append(f"| {head['launch']} | {name} | " + ' | '.join(fmt(row[c]) for c in cols) + f' | {w} |')
    text = '\n'.join(out) + '\n'
    print(text)
    if args.out:
        with open(args.out, 'w', encoding='utf-8') as f:
            f.write(text)
    return 0


if __name__ == '__main__':
    sys.exit(main())
