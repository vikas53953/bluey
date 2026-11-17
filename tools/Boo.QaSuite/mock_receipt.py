"""Exact same-run gate for the mock C15E receipt (job mock-contracts). No provider or desktop access.

usage: mock_receipt.py RECEIPT_DIR APP_SHA HARNESS_SHA RUN_ID DEST_TRX   copy the one C15E .trx to DEST_TRX after checking
       mock_receipt.py --self-test                                       every refusal below, on planted receipts

RECEIPT_DIR/identity.txt must hold exactly the lines app=, harness=, run= and label= (each once, nothing else): app equal
to the preflight app SHA, harness equal to this workflow's commit, run equal to this run's id, label exactly the mock
label. RECEIPT_DIR/trx must hold exactly one .trx with exactly one result, named exactly the C15E method from
qa_contract.json. Its outcome and freshness are judged later by qa_gate.py (status PASS, same run id, started after
preflight), not here. Exit 0 on success; otherwise exit 1 and print MOCK_RECEIPT:<reason>.
"""
import json
import re
import shutil
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
LABEL = 'mock; real provider calls=0'
SHA = re.compile(r'[0-9a-f]{40}')
RUN = re.compile(r'[1-9][0-9]*')


class Refused(Exception):
    pass


def c15e_method():
    return json.loads((HERE / 'qa_contract.json').read_text(encoding='utf-8'))['groups']['contracts']['C15E']


def read_identity(path):
    fields = {}
    for line in path.read_text(encoding='utf-8-sig').splitlines():
        if line == '':
            continue
        key, sep, value = line.partition('=')
        if not sep:
            raise Refused('identity_line_malformed')
        if key in fields:
            raise Refused('identity_duplicate:' + key)
        fields[key] = value
    if set(fields) != {'app', 'harness', 'run', 'label'}:
        raise Refused('identity_fields')
    return fields


def validate(root, app, harness, run):
    if not SHA.fullmatch(app) or not SHA.fullmatch(harness) or not RUN.fullmatch(run):
        raise Refused('expected_values_malformed')
    identity = read_identity(root / 'identity.txt')
    if identity['app'] != app:
        raise Refused('app_mismatch')
    if identity['harness'] != harness:
        raise Refused('harness_mismatch')
    if identity['run'] != run:
        raise Refused('run_mismatch')
    if identity['label'] != LABEL:
        raise Refused('label_mismatch')
    files = sorted((root / 'trx').glob('*.trx')) if (root / 'trx').is_dir() else []
    if len(files) != 1:
        raise Refused('trx_count:' + str(len(files)))
    try:
        rows = ET.parse(files[0]).getroot().findall('.//{*}UnitTestResult')
    except ET.ParseError:
        raise Refused('trx_unreadable')
    if len(rows) != 1 or rows[0].get('testName') != c15e_method():
        raise Refused('not_exactly_c15e')
    return files[0]


def plant(root, identity, results, trx_files=1):
    if (root / 'trx').exists():
        shutil.rmtree(root / 'trx')
    (root / 'trx').mkdir()
    (root / 'identity.txt').write_text(identity, encoding='utf-8')
    for i in range(trx_files):
        run_el = ET.Element('TestRun')
        for name in results:
            ET.SubElement(run_el, 'UnitTestResult', testName=name, outcome='Passed')
        ET.ElementTree(run_el).write(root / 'trx' / f'r{i}.trx')


def planted_cases(app, harness, run, method):
    """Each refusal case: (identity text, result names in the .trx, number of .trx files)."""
    good = f'app={app}\nharness={harness}\nrun={run}\nlabel={LABEL}\n'
    refusals = {
        'app_mismatch': (good.replace(f'app={app}', 'app=' + 'c' * 40), [method], 1),
        'app_substring_extra_after': (good.replace(f'app={app}', f'app={app}0'), [method], 1),
        'app_substring_extra_before': (good.replace(f'app={app}', f'app=x{app}'), [method], 1),
        'harness_mismatch': (good.replace(f'harness={harness}', 'harness=' + 'c' * 40), [method], 1),
        'harness_missing': (good.replace(f'harness={harness}\n', ''), [method], 1),
        'run_mismatch': (good.replace(f'run={run}', 'run=4243'), [method], 1),
        'run_substring': (good.replace(f'run={run}', 'run=42420'), [method], 1),
        'run_missing': (good.replace(f'run={run}\n', ''), [method], 1),
        'duplicate_app': (good + f'app={app}\n', [method], 1),
        'extra_field': (good + 'note=x\n', [method], 1),
        'malformed_line': (good + 'garbage\n', [method], 1),
        'label_mismatch': (good.replace(LABEL, 'live'), [method], 1),
        'no_trx': (good, [method], 0),
        'two_trx': (good, [method], 2),
        'wrong_test': (good, [method + 'X'], 1),
        'two_results': (good, [method, method], 1),
        'zero_results': (good, [], 1),
    }
    return good, refusals


def self_test():
    app, harness, run = 'a' * 40, 'b' * 40, '4242'
    method = c15e_method()

    good, refusals = planted_cases(app, harness, run, method)
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        plant(root, good, [method])
        validate(root, app, harness, run)
        for case, (identity, results, files) in refusals.items():
            plant(root, identity, results, files)
            try:
                validate(root, app, harness, run)
            except Refused:
                continue
            raise SystemExit(f'MOCK_RECEIPT_SELF_TEST_FAILED: {case} was accepted')
        plant(root, good, [method])
        for bad in [('A' * 40, harness, run), (app, harness, 'x1'), (app, harness, '')]:
            try:
                validate(root, *bad)
            except Refused:
                continue
            raise SystemExit('MOCK_RECEIPT_SELF_TEST_FAILED: malformed expected values were accepted')
    print(f'MOCK_RECEIPT_SELF_TEST_OK {len(refusals) + 3} refusals checked')


def main(argv):
    if argv == ['--self-test']:
        self_test()
        return 0
    if len(argv) != 5:
        print(__doc__)
        return 2
    root, app, harness, run, dest = Path(argv[0]), argv[1], argv[2], argv[3], Path(argv[4])
    try:
        trx = validate(root, app, harness, run)
    except (Refused, OSError) as error:
        print(f'MOCK_RECEIPT:{error}')
        return 1
    dest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(trx, dest)
    print(f'MOCK_RECEIPT_OK app={app} harness={harness} run={run}')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
