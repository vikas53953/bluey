"""Exact native TRX and identity gate. No provider or desktop access."""
import json
from pathlib import Path
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent

def validate(root, app, harness):
    assert len(app) == 40 and app == harness, 'app_harness_mismatch'
    identity = dict(line.split('=', 1) for line in (root/'identity.txt').read_text(encoding='utf-8-sig').splitlines())
    assert identity == {'app':app, 'harness':harness}, 'identity_mismatch'
    assert (root/'app-sha.txt').read_text(encoding='utf-8-sig').strip() == app, 'published_app_mismatch'
    expected = set(json.loads((HERE/'qa_contract.json').read_text())['groups']['native'].values())
    rows = [r for path in (root/'trx').glob('*.trx') for r in ET.parse(path).findall('.//{*}UnitTestResult')]
    names = [r.get('testName') for r in rows]
    assert len(names) == len(expected) and set(names) == expected, 'native_exact_ids'
    assert all(r.get('outcome') == 'Passed' for r in rows), 'native_failed_or_skipped'

def self_test():
    sha = 'a'*40
    names = list(json.loads((HERE/'qa_contract.json').read_text())['groups']['native'].values())
    with tempfile.TemporaryDirectory() as tmp:
        root=Path(tmp); (root/'trx').mkdir()
        (root/'identity.txt').write_text(f'app={sha}\nharness={sha}\n')
        (root/'app-sha.txt').write_text(sha)
        def write(rows):
            tree=ET.Element('TestRun')
            for name,outcome in rows: ET.SubElement(tree,'UnitTestResult',testName=name,outcome=outcome)
            ET.ElementTree(tree).write(root/'trx'/'receipt.trx')
        good=[(n,'Passed') for n in names]
        write(good); validate(root,sha,sha)
        for rows in [[],good[:1],good+[good[0]],[(names[0],'NotExecuted'),good[1]],[(names[0],'Failed'),good[1]],[(names[0]+'wrong','Passed'),good[1]]]:
            write(rows)
            try: validate(root,sha,sha)
            except AssertionError: pass
            else: raise AssertionError('bad receipt accepted')
        write(good)
        (root/'app-sha.txt').write_text('b'*40)
        try: validate(root,sha,sha)
        except AssertionError: pass
        else: raise AssertionError('wrong app accepted')
        (root/'app-sha.txt').write_text(sha)
        try: validate(root,sha,'b'*40)
        except AssertionError: pass
        else: raise AssertionError('wrong harness accepted')
    print('NATIVE_RECEIPTS_SELF_TEST_OK')

if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']: self_test()
    else: validate(Path(sys.argv[1]),sys.argv[2],sys.argv[3]); print('NATIVE_RECEIPTS_OK')
