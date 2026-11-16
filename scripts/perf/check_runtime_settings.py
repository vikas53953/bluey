"""Pin the runtime settings Boo is published with (issue #124, PR #142).

The perf fix for #124 is a publish setting: TieredPGO off. A setting is easy to flip back without any test noticing (the
Codex mutation run on 61361c2 flipped it and the perf budget still passed by chance), so this reads the PUBLISHED
Boo.runtimeconfig.json and fails unless every pinned setting has exactly the pinned value. It pins the whole set of
startup/idle choices recorded in docs/verification/2026-11-08-perf-budgets.md, not only the one that changed.

    python -I scripts/perf/check_runtime_settings.py check <publish dir>
    python -I scripts/perf/check_runtime_settings.py --self-test

Changing a pinned value is a decision: change it here, in Boo.App.csproj and in docs/DECISIONS.md together.
"""
import json
import os
import sys
import tempfile

CONFIG = 'Boo.runtimeconfig.json'

# setting -> (pinned value, why)
PINNED = {
    'System.Runtime.TieredPGO': (False, 'issue #124: dynamic PGO re-compiled hot code inside the idle window'),
    'System.Runtime.TieredCompilation': (True, 'ReadyToRun start-up code is replaced by optimised code later'),
    'System.GC.Concurrent': (False, 'one fewer always-on thread while Boo idles; its heap is small'),
    'System.GC.Server': (False, 'a desktop app: workstation GC'),
}


def check(publish_dir):
    """Returns a list of problems (empty = every pinned setting is as pinned)."""
    path = os.path.join(publish_dir, CONFIG)
    try:
        with open(path, encoding='utf-8-sig') as f:
            doc = json.load(f)
    except (OSError, ValueError) as e:
        return [f'{path}: cannot read the published runtime config ({type(e).__name__})']
    props = (doc.get('runtimeOptions') or {}).get('configProperties')
    if not isinstance(props, dict):
        return [f'{path}: runtimeOptions.configProperties is missing']
    problems = []
    for key, (want, why) in PINNED.items():
        if key not in props:
            problems.append(f'{key} is not set in the published app (pinned: {json.dumps(want)}; {why})')
        elif props[key] is not want:
            problems.append(f'{key} is {json.dumps(props[key])} in the published app, pinned {json.dumps(want)} ({why})')
    return problems


def _config(props):
    return {'runtimeOptions': {'tfm': 'net10.0', 'configProperties': props}}


def self_test():
    good = {k: v for k, (v, _) in PINNED.items()}
    cases = [
        ('the pinned settings pass', _config(good), None),
        ('TieredPGO switched back on fails', _config({**good, 'System.Runtime.TieredPGO': True}), 'System.Runtime.TieredPGO is true'),
        ('a pinned setting that is missing fails', _config({k: v for k, v in good.items() if k != 'System.GC.Concurrent'}),
         'System.GC.Concurrent is not set'),
        ('a string "false" is not false', _config({**good, 'System.Runtime.TieredPGO': 'false'}), 'System.Runtime.TieredPGO is "false"'),
        ('no configProperties fails', {'runtimeOptions': {}}, 'configProperties is missing'),
        ('no runtime config file fails', None, 'cannot read'),
    ]
    failures = 0
    for name, doc, expect in cases:
        with tempfile.TemporaryDirectory() as d:
            if doc is not None:
                with open(os.path.join(d, CONFIG), 'w', encoding='utf-8') as f:
                    json.dump(doc, f)
            problems = check(d)
        right = (not problems) if expect is None else any(expect in p for p in problems)
        failures += not right
        print(f'  [{"ok" if right else "WRONG"}] {name}' + ('' if right else f': got {problems}'))
    print(f'self-test: {len(cases) - failures}/{len(cases)}')
    return 1 if failures else 0


def main(argv):
    if argv == ['--self-test']:
        return self_test()
    if len(argv) == 2 and argv[0] == 'check':
        problems = check(argv[1])
        for p in problems:
            print(f'::error::{p}')
        if problems:
            print(f'RUNTIME_SETTINGS_FAILED {len(problems)} problem(s) in {os.path.join(argv[1], CONFIG)}')
            return 1
        print('RUNTIME_SETTINGS_OK ' + ', '.join(f'{k}={json.dumps(v)}' for k, (v, _) in PINNED.items()))
        return 0
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
