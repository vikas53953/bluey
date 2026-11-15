"""BOO-LIVE-003 (pass^k): a real-AI path counts as proven only when k independent live attempts in a row all pass.

  python -I scripts/ci/pass_k.py --check-k K
      Exit 0 if K is a whole number from 3 to 5, else exit 2 with a plain message (k <= 5 keeps the cost bounded;
      k >= 3 is the rule).
  python -I scripts/ci/pass_k.py --judge DIR --k K --summary FILE [--model ID] [--test NAME]
      Reads DIR/attempt-1 .. DIR/attempt-K. Each attempt folder holds `result.json` ({"attempt": N, "exitCode": C,
      "seconds": S}, written by the workflow after a fresh `dotnet test` of that attempt) and its `trx/*.trx`.
      An attempt passes only if: its exit code is 0, its trx files report at least one test, every test executed and
      passed (no failed, error, timeout, aborted, skipped or not-run test), and the named live test is among the passed.
      Prints `PASS^K: P/K` and the pass rate, writes FILE (JSON: rule, k, passed, pass rate, verdict, each attempt's
      result, seconds and reason), and exits 0 only when P == K. FAILS CLOSED: no attempt folders, a missing or
      unreadable attempt, an extra attempt folder, or a bad K is never a pass.
  python -I scripts/ci/pass_k.py --self-test
      Planted attempt results: 3/3 pass; 2/3 fail; 0 attempts fail; k > 5 refused; and the vacuous-green tricks.

Never reads secrets and never calls the network.
"""
import json
import pathlib
import shutil
import sys
import tempfile
import xml.etree.ElementTree as ET

RULE = 'BOO-LIVE-003'
K_MIN, K_MAX = 3, 5
DEFAULT_TEST = 'TypedQuestion_GetsAnAnswerFromTheRealProvider'
BAD_COUNTERS = ('failed', 'error', 'timeout', 'aborted', 'notExecuted', 'inconclusive', 'notRunnable',
                'disconnected')


def parse_k(text):
    """K as an int, or a plain reason why it is refused."""
    try:
        k = int(str(text).strip())
    except ValueError:
        return None, f'attempts must be a whole number from {K_MIN} to {K_MAX}, got {text!r}'
    if k < K_MIN or k > K_MAX:
        return None, f'attempts must be from {K_MIN} to {K_MAX} (cost cap and the pass^{K_MIN} rule), got {k}'
    return k, ''


def _local(tag):
    return tag.rsplit('}', 1)[-1]


def read_trx(folder):
    """(counters summed over every .trx, names of passed tests) or raises ValueError with a reason."""
    files = sorted(pathlib.Path(folder).rglob('*.trx')) if pathlib.Path(folder).is_dir() else []
    if not files:
        raise ValueError('no .trx test results (the test did not run)')
    totals, passed_names = {}, set()
    for f in files:
        try:
            root = ET.parse(f).getroot()
        except (ET.ParseError, OSError) as e:
            raise ValueError(f'unreadable test results {f.name}: {e}') from None
        counters = [el for el in root.iter() if _local(el.tag) == 'Counters']
        if len(counters) != 1:
            raise ValueError(f'{f.name} has {len(counters)} result summaries, expected 1')
        for key, value in counters[0].attrib.items():
            try:
                totals[key] = totals.get(key, 0) + int(value)
            except ValueError:
                raise ValueError(f'{f.name}: counter {key} is not a number') from None
        for el in root.iter():
            if _local(el.tag) == 'UnitTestResult' and el.get('outcome') == 'Passed':
                passed_names.add(el.get('testName', ''))
    return totals, passed_names


def judge_attempt(folder, n, test_name):
    """{'attempt', 'result' ('pass'/'fail'), 'seconds', 'reason'} for one attempt folder."""
    out = {'attempt': n, 'result': 'fail', 'seconds': None, 'reason': ''}
    res = pathlib.Path(folder) / 'result.json'
    if not res.is_file():
        out['reason'] = 'missing: the attempt never recorded a result'
        return out
    try:
        data = json.loads(res.read_text(encoding='utf-8-sig'))
    except (OSError, ValueError) as e:
        out['reason'] = f'unreadable result.json: {e}'
        return out
    if not isinstance(data, dict) or data.get('attempt') != n or type(data.get('exitCode')) is not int:
        out['reason'] = 'result.json does not describe this attempt (attempt number or exit code missing)'
        return out
    if isinstance(data.get('seconds'), (int, float)) and not isinstance(data.get('seconds'), bool):
        out['seconds'] = round(float(data['seconds']), 1)
    if data['exitCode'] != 0:
        out['reason'] = f'the live test exited with code {data["exitCode"]}'
        return out
    try:
        c, passed_names = read_trx(pathlib.Path(folder) / 'trx')
    except ValueError as e:
        out['reason'] = str(e)
        return out
    total, executed, passed = c.get('total', 0), c.get('executed', 0), c.get('passed', 0)
    bad = sum(c.get(k, 0) for k in BAD_COUNTERS)
    if total < 1 or executed < 1:
        out['reason'] = 'zero tests ran (vacuous green)'
    elif bad or not (passed == executed == total):
        out['reason'] = f'tests total={total} executed={executed} passed={passed}, failed or skipped={bad}'
    elif not any(n == test_name or n.endswith('.' + test_name) for n in passed_names):
        out['reason'] = f'the live test {test_name} is not among the passed tests'
    else:
        out['result'] = 'pass'
    return out


def judge(base, k, summary, model='', test_name=DEFAULT_TEST, out=print):
    k_ok, why = parse_k(k)
    if k_ok is None:
        out(f'::error title={RULE} refused::{why}')
        return 2
    k = k_ok
    base = pathlib.Path(base)
    found = sorted(p.name for p in base.glob('attempt-*') if p.is_dir()) if base.is_dir() else []
    attempts = [judge_attempt(base / f'attempt-{n}', n, test_name) for n in range(1, k + 1)]
    problems = []
    if not found:
        problems.append('no attempts were recorded at all (0 attempts is never a pass)')
    extra = sorted(set(found) - {f'attempt-{n}' for n in range(1, k + 1)})
    if extra:
        problems.append(f'unexpected attempt folders {", ".join(extra)} (only attempt-1..attempt-{k} may exist)')
    passed = sum(a['result'] == 'pass' for a in attempts) if found else 0
    ok = passed == k and not problems
    result = {
        'rule': RULE, 'k': k, 'passed': passed, 'passRate': round(passed / k, 4),
        'verdict': 'PASS' if ok else 'FAIL', 'model': model, 'test': test_name,
        'attempts': attempts, 'problems': problems,
    }
    pathlib.Path(summary).parent.mkdir(parents=True, exist_ok=True)
    pathlib.Path(summary).write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    for a in attempts:
        secs = '?' if a['seconds'] is None else f'{a["seconds"]}s'
        out(f'attempt {a["attempt"]}: {a["result"].upper()} ({secs}){" - " + a["reason"] if a["reason"] else ""}')
    for p in problems:
        out(f'::error title={RULE}::{p}')
    out(f'PASS^{k}: {passed}/{k}  (pass rate {passed * 100 // k}%)  verdict: {result["verdict"]}')
    if not ok:
        out(f'::error title={RULE} pass^{k} not met::{passed} of {k} independent live attempts passed; '
            f'all {k} must pass in a row. One green run can be luck.')
        return 1
    return 0


# ---------------------------------------------------------------- self-test

TRX = ('<?xml version="1.0" encoding="utf-8"?>\n'
       '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>'
       '<UnitTestResult testName="{name}" outcome="{outcome}" /></Results>'
       '<ResultSummary outcome="{outcome}"><Counters total="{total}" executed="{executed}" passed="{passed}" '
       'failed="{failed}" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" '
       'notRunnable="0" notExecuted="{notexec}" disconnected="0" warning="0" completed="0" inProgress="0" '
       'pending="0" /></ResultSummary></TestRun>\n')


def _plant(base, n, exit_code=0, outcome='pass', trx=True, name=DEFAULT_TEST):
    d = pathlib.Path(base) / f'attempt-{n}'
    (d / 'trx').mkdir(parents=True, exist_ok=True)
    (d / 'result.json').write_text(json.dumps({'attempt': n, 'exitCode': exit_code, 'seconds': 61.25}),
                                   encoding='utf-8')
    if not trx:
        return
    counts = {'pass': dict(outcome='Passed', total=1, executed=1, passed=1, failed=0, notexec=0),
              'fail': dict(outcome='Failed', total=1, executed=1, passed=0, failed=1, notexec=0),
              'zero': dict(outcome='Completed', total=0, executed=0, passed=0, failed=0, notexec=0),
              'skip': dict(outcome='Passed', total=1, executed=0, passed=0, failed=0, notexec=1)}[outcome]
    (d / 'trx' / 'run.trx').write_text(TRX.format(name=name, **counts), encoding='utf-8')


def self_test(out=print):
    failures = []

    def case(label, setup, k, want_rc, want_line=None, want_passed=None):
        tmp = pathlib.Path(tempfile.mkdtemp(prefix='pass_k_'))
        try:
            setup(tmp / 'attempts')
            lines = []
            rc = judge(tmp / 'attempts', k, tmp / 'summary' / 'pass3.json', model='m', out=lines.append)
            ok = rc == want_rc and (want_line is None or want_line in lines)
            if ok and want_passed is not None:
                s = json.loads((tmp / 'summary' / 'pass3.json').read_text(encoding='utf-8'))
                ok = s['passed'] == want_passed and s['verdict'] == ('PASS' if want_rc == 0 else 'FAIL') \
                    and len(s['attempts']) == int(k)
            out(f'{"ok  " if ok else "FAIL"} {label} (exit {rc})')
            if not ok:
                failures.append(label)
                for line in lines:
                    out(f'      {line}')
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def all_pass(b):
        for n in (1, 2, 3):
            _plant(b, n)

    def two_of_three(b):
        _plant(b, 1)
        _plant(b, 2, exit_code=1, outcome='fail')
        _plant(b, 3)

    def none(b):
        b.mkdir(parents=True)

    case('3/3 pass -> PASS', all_pass, '3', 0, 'PASS^3: 3/3  (pass rate 100%)  verdict: PASS', 3)
    case('2/3 -> FAIL', two_of_three, '3', 1, 'PASS^3: 2/3  (pass rate 66%)  verdict: FAIL', 2)
    case('0 attempts -> FAIL (fail closed)', none, '3', 1, 'PASS^3: 0/3  (pass rate 0%)  verdict: FAIL', 0)
    case('folder never created -> FAIL', lambda b: None, '3', 1, None, 0)
    case('k=6 refused', all_pass, '6', 2)
    case('k=2 refused (rule is pass^3)', all_pass, '2', 2)
    case('k not a number refused', all_pass, 'three', 2)
    case('5/5 pass with k=5', lambda b: [_plant(b, n) for n in range(1, 6)], '5', 0, None, 5)
    case('only 2 of k=3 attempts recorded -> FAIL', lambda b: [_plant(b, n) for n in (1, 2)], '3', 1, None, 2)
    case('extra attempt-4 folder -> FAIL', lambda b: [_plant(b, n) for n in range(1, 5)], '3', 1, None, 3)
    case('exit 0 but no trx -> FAIL', lambda b: [_plant(b, n, trx=(n != 2)) for n in (1, 2, 3)], '3', 1, None, 2)
    case('exit 0 but zero tests ran -> FAIL',
         lambda b: [_plant(b, n, outcome='zero' if n == 3 else 'pass') for n in (1, 2, 3)], '3', 1, None, 2)
    case('exit 0 but test skipped -> FAIL',
         lambda b: [_plant(b, n, outcome='skip' if n == 1 else 'pass') for n in (1, 2, 3)], '3', 1, None, 2)
    case('exit 0 but trx shows a failure -> FAIL',
         lambda b: [_plant(b, n, outcome='fail' if n == 2 else 'pass') for n in (1, 2, 3)], '3', 1, None, 2)
    case('fully qualified test name (as xUnit writes it) -> PASS',
         lambda b: [_plant(b, n, name='Boo.LiveSmoke.LiveRoundTripTests.' + DEFAULT_TEST) for n in (1, 2, 3)], '3', 0,
         None, 3)
    case('a name that only ends with the test name -> FAIL',
         lambda b: [_plant(b, n, name='X' + DEFAULT_TEST if n == 1 else DEFAULT_TEST) for n in (1, 2, 3)], '3', 1,
         None, 2)
    case('a different test passed -> FAIL',
         lambda b: [_plant(b, n, name='SomethingElse' if n == 3 else DEFAULT_TEST) for n in (1, 2, 3)], '3', 1,
         None, 2)

    def corrupt(b):
        all_pass(b)
        (b / 'attempt-2' / 'result.json').write_text('{not json', encoding='utf-8')
    case('unreadable result.json -> FAIL', corrupt, '3', 1, None, 2)

    def wrong_number(b):
        all_pass(b)
        (b / 'attempt-3' / 'result.json').write_text(json.dumps({'attempt': 1, 'exitCode': 0}), encoding='utf-8')
    case('result.json for another attempt -> FAIL', wrong_number, '3', 1, None, 2)

    def bom(b):
        all_pass(b)
        p = b / 'attempt-1' / 'result.json'
        p.write_text(p.read_text(encoding='utf-8'), encoding='utf-8-sig')
    case('result.json with a BOM (PowerShell) still reads', bom, '3', 0, None, 3)

    for text, want in (('3', 3), ('5', 5), (' 4 ', 4), ('6', None), ('0', None), ('-3', None), ('', None)):
        got, _ = parse_k(text)
        label = f'--check-k {text!r} -> {want}'
        out(f'{"ok  " if got == want else "FAIL"} {label}')
        if got != want:
            failures.append(label)

    if failures:
        out(f'PASS_K_SELF_TEST_FAILED {len(failures)} case(s)')
        return 1
    out('PASS_K_SELF_TEST_OK')
    return 0


def main(argv):
    args = list(argv)
    if args == ['--self-test']:
        return self_test()
    if len(args) == 2 and args[0] == '--check-k':
        k, why = parse_k(args[1])
        if k is None:
            print(f'::error title={RULE} refused::{why}')
            return 2
        print(f'k={k} accepted')
        return 0
    if args and args[0] == '--judge':
        opts = {'--judge': None, '--k': None, '--summary': None, '--model': '', '--test': DEFAULT_TEST}
        it = iter(args)
        for a in it:
            if a not in opts:
                print(f'::error title={RULE}::unknown option {a!r}')
                return 2
            v = next(it, None)
            if v is None:
                print(f'::error title={RULE}::{a} needs a value')
                return 2
            opts[a] = v
        if None in (opts['--judge'], opts['--k'], opts['--summary']):
            print(f'::error title={RULE}::--judge DIR --k K --summary FILE are required')
            return 2
        return judge(opts['--judge'], opts['--k'], opts['--summary'], opts['--model'], opts['--test'])
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
