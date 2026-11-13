"""Full-app QA suite report: reads every .trx the suite wrote and prints one evidence table.

  python -I tools/Boo.QaSuite/qa_report.py <evidence-folder> [--run-url URL] [--app-sha SHA] [--harness-sha SHA]
                                           [--models TEXT] [--self-test]

Each row is labelled the way docs/FEATURE_MAP.md and BOO-EVID-001 expect: live / BLOCKED / UNVERIFIED. Nothing is
inferred as a pass: a check counts only if its test ran and passed in this run. Writes <evidence>/summary.md and, on
GitHub, appends it to the job summary. Exit 0 = every expected group ran and every test passed (BLOCKED and
UNVERIFIED rows are listed, never counted as passes); exit 1 otherwise. Reads no secrets.
"""
import os
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}

# group folder -> plain-words name; every one must have run (a missing group is a red, never a silent gap)
GROUPS = {
    'antimock': 'Anti-mock checks',
    'journeys': 'Suite journeys (launch, hover, chat, Stop, focus, settings)',
    'smoke': 'Live round trip (and voice, once PR #47 lands)',
    'verifylive': 'verify-boo live journeys j0-j6',
    'errors': 'Error paths',
}
OPTIONAL = {'verifylive'}  # skipped only when the dispatch says journeys=false; then listed as UNVERIFIED

PLAIN = {
    'AM0': 'Shipped build holds no mock or test code',
    'AM1': 'No AI host redirected, no proxy, no hosts pin',
    'AM2': 'No planted root certificate; real OpenAI certificate validates',
    'S0': 'Launch: only the notch, Ctrl+Alt+B opens the panel',
    'S1': 'Hover wakes the notch, it retracts after',
    'S2': 'Real OpenAI chat echoes a code made at run time; notch Thinking then Responding',
    'S3': 'Stop while an answer is coming: face clears, no answer lands',
    'S4': 'Focus Start / Pause (notch Paused) / Resume / End',
    'S5': 'Settings saved to file; a new Boo starts with the same choices',
    'E0': 'Empty question: "Type a question first."',
    'E1': 'No key: "No OpenAI key yet..."',
    'E2': 'Bad key: real OpenAI 401, "OpenAI rejected the API key..."',
    'E3': 'Network down (firewall): "Can\'t reach OpenAI...", then recovers',
    'E4': 'Provider never answers: Boo times out honestly, then recovers',
    'J0': 'j0 notch wakes on Ctrl+Alt+B',
    'J1': 'j1 notch wakes on hover',
    'J2': 'j2 typed chat, real answer',
    'J3': 'j3 screen explain, pointer + ring on the target',
    'J4': 'j4 research with real source links',
    'J5': 'j5 Apply then Undo, only after approval',
    'J6': 'j6 25-minute focus session (Developer Mode clock)',
}
STATES = ['Hidden', 'Hover', 'Idle', 'Listening', 'Thinking', 'Responding', 'Capture', 'Nudge', 'Paused', 'Happy']


def read_trx(folder):
    """[(group, test name, outcome, message)] for every result in every .trx under folder/<group>/trx."""
    rows = []
    for group in GROUPS:
        for trx in sorted((folder / group / 'trx').glob('*.trx')) if (folder / group / 'trx').is_dir() else []:
            root = ET.parse(trx).getroot()
            for r in root.iterfind('.//t:UnitTestResult', NS):
                msg = r.find('.//t:Message', NS)
                rows.append((group, r.get('testName', ''), r.get('outcome', ''),
                             (msg.text or '').strip().splitlines()[0][:220] if msg is not None and msg.text else ''))
    return rows


def short_id(test_name):
    m = re.search(r'\.([A-Z]{1,2}\d+)_', test_name)
    return m.group(1) if m else None


def states_seen(folder):
    seen = {}
    for f in folder.rglob('*.txt'):
        if not (f.name.startswith('qa-notch-states') or 'face-timeline' in f.name or f.name.startswith('j6-focus')
                or f.name.startswith('j1-hover')):
            continue
        text = f.read_text(encoding='utf-8', errors='replace')
        for s in STATES:
            if re.search(rf'(?<![A-Za-z]){s}(?![A-Za-z])', text):
                seen.setdefault(s, f.name)
    return seen


def build(folder, run_url='', app_sha='', harness_sha='', models=''):
    rows = read_trx(folder)
    link = f'[run]({run_url})' if run_url else 'this run'
    table = ['| Check | Label | Result | Evidence |', '| --- | --- | --- | --- |']
    problems = []
    for group, title in GROUPS.items():
        mine = [r for r in rows if r[0] == group]
        if not mine:
            if group in OPTIONAL:
                table.append(f'| {title} | UNVERIFIED | not run in this dispatch (journeys=false) | {link} |')
            else:
                problems.append(f'{title}: no results (the group did not run)')
                table.append(f'| {title} | live | FAILED: no results, the group did not run | {link} |')
            continue
        for _, name, outcome, message in mine:
            sid = short_id(name)
            what = PLAIN.get(sid, name.split('.')[-1])
            if group == 'smoke' and 'voice' in name.lower():
                what = 'Voice: real OpenAI voice round trip'
            ok = outcome == 'Passed'
            if not ok:
                problems.append(f'{what}: {outcome} {message}')
            result = 'passed' if ok else f'FAILED ({outcome}): {message}'.replace('|', '/')
            table.append(f'| {what} | live | {result} | {link} |')
    if not any(r[0] == 'smoke' and 'voice' in r[1].lower() for r in rows):
        table.append(f'| Voice: real OpenAI voice round trip | BLOCKED | no live voice test exists on this harness yet; it '
                     f'arrives with PR #47. Not a pass. | {link} |')
    seen = states_seen(folder)
    for s in STATES:
        if s in seen:
            table.append(f'| Notch state {s} | live | seen ({seen[s]}) | {link} |')
        else:
            table.append(f'| Notch state {s} | UNVERIFIED | not driven by this suite yet (plan row 2.11) | {link} |')
    passed = sum(1 for r in rows if r[2] == 'Passed')
    verdict = 'SUITE GREEN' if not problems else 'SUITE RED'
    blocked = [line.split('|')[1].strip() for line in table if '| BLOCKED |' in line]
    head = [f'## Boo full-app QA suite: {verdict}', '',
            f'App under test: `{app_sha or "?"}` - test harness: `{harness_sha or "?"}` - models: {models or "?"}', '',
            f'{passed} of {len(rows)} tests passed.'
            + (f' BLOCKED (not a pass): {", ".join(blocked)}.' if blocked else ''), '']
    tail = [''] + ([f'Problems:', ''] + [f'- {p}' for p in problems] if problems else [])
    return '\n'.join(head + table + tail) + '\n', not problems


def self_test():
    import tempfile
    ok = True

    def check(name, cond):
        nonlocal ok
        print(('ok   ' if cond else 'FAIL ') + name)
        ok = ok and cond

    def trx(path, results):
        path.mkdir(parents=True, exist_ok=True)
        body = ''.join(
            f'<UnitTestResult testName="{n}" outcome="{o}"><Output><ErrorInfo><Message>{m}</Message></ErrorInfo></Output></UnitTestResult>'
            for n, o, m in results)
        (path / 'r.trx').write_text(f'<TestRun xmlns="{NS["t"]}"><Results>{body}</Results></TestRun>', encoding='utf-8')

    with tempfile.TemporaryDirectory() as d:
        root = pathlib.Path(d)
        for g in GROUPS:
            trx(root / g / 'trx', [(f'Boo.QaSuite.X.T{g}.S0_Thing', 'Passed', '')])
        (root / 'journeys' / 'qa-notch-states-journeys.txt').write_text('10 Hidden\n20 Thinking\n', encoding='utf-8')
        text, green = build(root)
        check('all groups passed -> green', green and 'SUITE GREEN' in text)
        check('voice missing -> BLOCKED row, still not counted as a pass', '| Voice: real OpenAI voice round trip | BLOCKED |' in text)
        check('seen state -> live', '| Notch state Thinking | live |' in text)
        check('unseen state -> UNVERIFIED', '| Notch state Listening | UNVERIFIED |' in text)
        check('no "Hidden" substring false hit for Idle', '| Notch state Idle | UNVERIFIED |' in text)
        trx(root / 'errors' / 'trx', [('Boo.QaSuite.E.T.E3_Net', 'Failed', 'Boo did not show | the message')])
        text, green = build(root)
        check('a failed test -> red', not green and 'SUITE RED' in text and 'FAILED (Failed)' in text)
        check('pipe in a message cannot break the table', 'Boo did not show / the message' in text)
    with tempfile.TemporaryDirectory() as d:
        root = pathlib.Path(d)
        trx(root / 'antimock' / 'trx', [('Boo.QaSuite.A.T.AM0_X', 'Passed', '')])
        text, green = build(root)
        check('a group that did not run -> red', not green and 'the group did not run' in text)
        check('optional journeys group missing -> UNVERIFIED, not red by itself',
              '| verify-boo live journeys j0-j6 | UNVERIFIED |' in text)
    with tempfile.TemporaryDirectory() as d:
        root = pathlib.Path(d)
        for g in GROUPS:
            trx(root / g / 'trx', [(f'Boo.QaSuite.X.T.S0_A', 'Passed', '')])
        trx(root / 'smoke' / 'trx', [('Boo.LiveSmoke.LiveVoiceRoundTripTests.Voice_X', 'Passed', '')])
        text, green = build(root)
        check('voice test present -> live row, no BLOCKED row', green and '| Voice: real OpenAI voice round trip | live | passed' in text
              and 'BLOCKED' not in text)
    print('QA_REPORT_SELF_TEST_OK' if ok else 'QA_REPORT_SELF_TEST_FAILED')
    return 0 if ok else 1


def main(argv):
    if '--self-test' in argv:
        return self_test()
    if not argv or argv[0].startswith('--'):
        print(__doc__)
        return 2
    opts = dict(zip(argv[1::2], argv[2::2]))
    folder = pathlib.Path(argv[0])
    text, green = build(folder, opts.get('--run-url', ''), opts.get('--app-sha', ''), opts.get('--harness-sha', ''),
                        opts.get('--models', ''))
    (folder / 'summary.md').write_text(text, encoding='utf-8')
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as f:
            f.write(text)
    print(text)
    return 0 if green else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
