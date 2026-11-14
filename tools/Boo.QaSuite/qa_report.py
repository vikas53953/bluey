"""Full-app QA suite report: reads every .trx the suite wrote and prints one evidence table.

  python -I tools/Boo.QaSuite/qa_report.py <evidence-folder> [--run-url URL] [--app-sha SHA] [--harness-sha SHA]
                                           [--models TEXT] [--self-test]

Each row is labelled the way docs/FEATURE_MAP.md and BOO-EVID-001 expect: live / BLOCKED / UNVERIFIED. Nothing is
inferred as a pass: a check counts only if its test ran and passed in this run. Writes <evidence>/summary.md and, on
GitHub, appends it to the job summary. Exit 0 = every expected group, required journey and notch state is proven (BLOCKED and
UNVERIFIED required rows fail the report); exit 1 otherwise. Reads no secrets.
"""
import json
import os
import pathlib
import re
import sys
import subprocess
import xml.etree.ElementTree as ET

NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}

# group folder -> plain-words name; every one must have run (a missing group is a red, never a silent gap)
GROUPS = {
    'contracts': 'Mock selected-folder request contract',
    'antimock': 'Anti-mock checks',
    'journeys': 'Suite journeys (launch, hover, chat, Stop, focus, settings)',
    'smoke': 'Live round trip (and voice, once PR #47 lands)',
    'verifylive': 'verify-boo live journeys j0-j6',
    'errors': 'Error paths',
}
OPTIONAL = {'verifylive'}  # skipped only when the dispatch says journeys=false; then listed as UNVERIFIED

PLAIN = {
    'C66': 'Selected-folder context gated by explicit task and configured root (mock HTTP)',
    'AM0': 'Shipped build holds no mock or test code',
    'AM1': 'No AI host redirected, no proxy, no hosts pin',
    'AM2': 'No planted root certificate; real OpenAI certificate validates',
    'S0': 'Launch: only the notch, Ctrl+Alt+B opens the panel',
    'S1': 'Hover wakes the notch, it retracts after',
    'S2': 'Real OpenAI chat echoes a code made at run time; notch shows Boo working, then Responding',
    'S3': 'Stop while an answer is coming: face clears, no answer lands',
    'S4': 'Focus Start / Pause (notch Paused) / Resume / End',
    'S5': 'Settings saved to file; a new Boo starts with the same choices',
    'Q10': 'Physical eye tap under 200 ms opens panel without Listening (partial j10a)',
    'Q7': 'Hover edges: all four real-clock branches (no AI key)',
    'QD15': 'Two file steps: Next step card and separate approvals (partial j15d)',
    'Q67': 'Issue 67 diagnostic: ten compact-window focus cycles without UIA errors (no key)',
    'Q18': 'Newer typed request only answer during 15-second watch (partial j18)',
    'Q16': 'Second approved file Undo restores exact contents (partial j16; field pending)',
    'Q15': 'Approval Cancel / Escape / replacement (partial j15 a-c)',
    'Q13': 'Focus edges: End from Paused and replace Running (no AI key; partial j13)',
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
# PR #50's journey list (.claude/skills/verify-boo/journeys.md): each journey -> the checks that drive it today.
# A journey is live only if every listed check ran and passed in this run; voice journeys are BLOCKED until PR #47.
JOURNEYS = [
    ('j0', 'Hotkey opens the panel', ['J0', 'S0']),
    ('j1', 'Hover reveals and retracts', ['J1', 'S1']),
    ('j2', 'Typed chat, plain-text path', ['J2', 'S2']),
    ('j3', 'Screen explain: pointer and ring', ['J3']),
    ('j4', 'Research with sources', ['J4']),
    ('j5', 'Apply then Undo', ['J5']),
    ('j6', '25-minute focus on the Developer Mode clock', ['J6']),
    ('j7', 'Hover edge timings', ['Q7']),
    ('j8', 'Typed question handed to voice', 'voice'),
    ('j9', 'Hold-to-talk with Ctrl+Alt+Space', 'voice'),
    ('j10', 'The eyes: tap Q10; hold and capture-loss pending', []),
    ('j11', 'Panel Talk button', 'voice'),
    ('j12', 'Stop from every busy state (only j12d, Stop during a typed answer, is driven: S3)', []),
    ('j13', 'Focus lifecycle (S4 and Q13 cover subsets; full sequence and event proof pending)', []),
    ('j14', 'Focus nudge (off task)', []),
    ('j15', 'Approval card edges (a-c Q15, d QD15; e pending)', []),
    ('j16', 'Second undo (file branch Q16; field branch pending)', []),
    ('j17', 'Voice ends itself and drives focus', 'voice'),
    ('j18', 'Newer request wins (typed Q18; ring and face ownership pending)', []),
]
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
    for f in folder.rglob('qa-notch-states-*.txt'):
        # Recorder contract: elapsed milliseconds followed by exactly one observed face.
        # Expectations, error messages and prose are not observations.
        for line in f.read_text(encoding='utf-8', errors='replace').splitlines():
            match = re.fullmatch(r'(\d+) ([A-Za-z]+)', line.strip())
            if match and match.group(2) in STATES:
                seen.setdefault(match.group(2), f.name)
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
                table.append(f'| {title} | UNVERIFIED | FAILED: no results, the group did not run | {link} |')
            continue
        for _, name, outcome, message in mine:
            sid = short_id(name)
            what = PLAIN.get(sid, name.split('.')[-1])
            if group == 'smoke' and 'voice' in name.lower():
                what = 'Voice provider: real OpenAI synthetic audio round trip (component; no Boo.exe UI)'
            elif name.endswith('TypedQuestion_GetsAnAnswerFromTheRealProvider'):
                what = 'Live round trip: typed question + screenshot, real OpenAI answer'
            ok = outcome == 'Passed'
            if not ok:
                problems.append(f'{what}: {outcome} {message}')
            result = 'passed' if ok else f'FAILED ({outcome}): {message}'.replace('|', '/')
            label = 'mock' if group == 'contracts' else 'live'
            table.append(f'| {what} | {label} | {result} | {link} |')
    if not any(r[0] == 'smoke' and 'voice' in r[1].lower() for r in rows):
        table.append(f'| Voice provider: real OpenAI synthetic audio round trip (component; no Boo.exe UI) | BLOCKED | no live voice test exists on this harness yet; it '
                     f'arrives with PR #47. Not a pass. | {link} |')
    audio_available = None
    audio_path = folder / 'capabilities' / 'audio.json'
    if audio_path.is_file():
        try:
            audio = json.loads(audio_path.read_text(encoding='utf-8-sig'))
            count = audio['wave_in_count']
            devices = audio['inputs']
            if type(count) is not int or count < 0 or not isinstance(devices, list) or len(devices) != count:
                raise ValueError('invalid endpoint count/list')
            if any(not isinstance(d, dict) or type(d.get('pcm24k_mono16_supported')) is not bool for d in devices):
                raise ValueError('invalid format-query result')
            supported = sum(d['pcm24k_mono16_supported'] for d in devices)
            audio_available = supported > 0
            table.append(f'| Audio environment (no app voice proof) | manual | WinMM inputs={count}, outputs={audio["wave_out_count"]}, format-query-supported PCM24k/mono16 inputs={supported} | {link} |')
        except (ValueError, KeyError, TypeError) as error:
            problems.append(f'Invalid audio capability receipt: {error}')
            table.append(f'| Audio environment | UNVERIFIED | invalid capability receipt | {link} |')
    else:
        table.append(f'| Audio environment | UNVERIFIED | capability probe did not produce a receipt | {link} |')
    ran = {short_id(r[1]): r[2] for r in rows if short_id(r[1])}
    voice_results = [r for r in rows if r[0] == 'smoke' and 'voice' in r[1].lower()]
    voice_passed = bool(voice_results) and all(r[2] == 'Passed' for r in voice_results)
    for jid, title, checks in JOURNEYS:
        if checks == 'voice' and audio_available is False:
            table.append(f'| Journey {jid}: {title} | BLOCKED | no WinMM capture endpoint with the required PCM24k/mono16 format; provider component proof does not exercise app microphone capture | {link} |')
        elif checks == 'voice' and voice_passed:
            table.append(f'| Journey {jid}: {title} | UNVERIFIED | provider round trip passed; this app UI journey is not driven yet | {link} |')
        elif checks == 'voice' and voice_results:
            table.append(f'| Journey {jid}: {title} | BLOCKED | provider component did not pass; app UI journey is not driven | {link} |')
        elif checks == 'voice':
            table.append(f'| Journey {jid}: {title} | BLOCKED | needs the live voice test (PR #47). Not a pass. | {link} |')
        elif checks and all(ran.get(c) == 'Passed' for c in checks):
            table.append(f'| Journey {jid}: {title} | live | passed ({", ".join(checks)}) | {link} |')
        elif checks:
            got = ', '.join(c + '=' + ran.get(c, 'not run') for c in checks)
            table.append(f'| Journey {jid}: {title} | live | FAILED or not run ({got}) | {link} |')
        else:
            table.append(f'| Journey {jid}: {title} | UNVERIFIED | not driven by the suite yet (follow-up after PRs #47, #48, #50) | {link} |')
    seen = states_seen(folder)
    for s in STATES:
        if s in seen:
            table.append(f'| Notch state {s} | live | seen ({seen[s]}) | {link} |')
        else:
            table.append(f'| Notch state {s} | UNVERIFIED | not driven by this suite yet (plan row 2.11) | {link} |')
    # Required journeys and faces must all be proven before full-app green.
    for line in table:
        if '| BLOCKED |' in line or '| UNVERIFIED |' in line or '| live | FAILED or not run' in line:
            problems.append('Required coverage incomplete: ' + line.split('|')[1].strip())
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
            trx(root / g / 'trx', [('Boo.QaSuite.Contracts.X.C66_Folder' if g == 'contracts' else f'Boo.QaSuite.X.T{g}.S0_Thing', 'Passed', '')])
        (root / 'journeys' / 'qa-notch-states-journeys.txt').write_text('# expected Listening and Happy\n10 Hidden\n20 Thinking\ninvalid 30 Nudge\n', encoding='utf-8')
        text, green = build(root)
        check('missing required coverage prevents green', not green and 'SUITE RED' in text)
        check('voice missing -> BLOCKED row, still not counted as a pass', '| Voice provider: real OpenAI synthetic audio round trip (component; no Boo.exe UI) | BLOCKED |' in text)
        check('mock contract cannot be labelled live', '| Selected-folder context gated by explicit task and configured root (mock HTTP) | mock | passed' in text)
        check('seen state -> live', '| Notch state Thinking | live |' in text)
        check('expectations cannot count as observations', '| Notch state Happy | UNVERIFIED |' in text and '| Notch state Nudge | UNVERIFIED |' in text)
        check('unseen state -> UNVERIFIED', '| Notch state Listening | UNVERIFIED |' in text)
        check('journey with no driving check -> UNVERIFIED', '| Journey j10: The eyes: tap Q10; hold and capture-loss pending | UNVERIFIED |' in text)
        check('mapped hover journey without result cannot pass', '| Journey j7: Hover edge timings | live | FAILED or not run (Q7=not run)' in text)
        check('voice journey -> BLOCKED', '| Journey j9: Hold-to-talk with Ctrl+Alt+Space | BLOCKED |' in text)
        check('journey whose checks did not run -> not live-passed', '| Journey j3: Screen explain: pointer and ring | live | FAILED or not run' in text)
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
        check('voice alone cannot satisfy undriven journeys', not green and '| Voice provider: real OpenAI synthetic audio round trip (component; no Boo.exe UI) | live | passed' in text
              and '| BLOCKED |' not in text and '| Journey j9: Hold-to-talk with Ctrl+Alt+Space | UNVERIFIED |' in text)
        for outcome in ('Failed', 'NotExecuted'):
            trx(root / 'smoke' / 'trx', [('Boo.LiveSmoke.LiveVoiceRoundTripTests.Voice_X', outcome, '')])
            text, green = build(root)
            check(f'{outcome} voice must never be described as passed', not green
                  and 'provider round trip passed' not in text
                  and 'provider component did not pass' in text)
        trx(root / 'smoke' / 'trx', [('Boo.LiveSmoke.LiveVoiceRoundTripTests.Voice_X', 'Passed', ''),
                                    ('Boo.LiveSmoke.LiveVoiceRoundTripTests.Voice_Y', 'Failed', '')])
        text, green = build(root)
        check('mixed voice outcomes cannot imply component pass', not green and 'provider round trip passed' not in text)
        trx(root / 'smoke' / 'trx', [('Boo.LiveSmoke.LiveVoiceRoundTripTests.Voice_X', 'Passed', '')])
        capability = root / 'capabilities'
        capability.mkdir()
        (capability / 'audio.json').write_text(json.dumps({'wave_in_count': 0, 'wave_out_count': 0, 'inputs': []}), encoding='utf-8')
        text, green = build(root)
        check('missing capture endpoint blocks UI voice despite passing provider', not green and 'no WinMM capture endpoint' in text)
        check('capability evidence links are interpolated', '{link}' not in text)
        (capability / 'audio.json').write_text(json.dumps({'wave_in_count': 1, 'wave_out_count': 1, 'inputs': [{'pcm24k_mono16_supported': True}]}), encoding='utf-8')
        text, green = build(root)
        check('compatible endpoint alone cannot prove app voice UI', not green and 'provider round trip passed; this app UI journey is not driven yet' in text)
        (capability / 'audio.json').write_text('{"wave_in_count": "unknown", "inputs": []}', encoding='utf-8')
        text, green = build(root)
        check('invalid capability receipt fails closed', not green and 'invalid capability receipt' in text)
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
    gate = subprocess.run([sys.executable, str(pathlib.Path(__file__).with_name('qa_gate.py')), str(folder)], check=False)
    green = green and gate.returncode == 0
    (folder / 'summary.md').write_text(text, encoding='utf-8')
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as f:
            f.write(text)
    print(text)
    return 0 if green else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
