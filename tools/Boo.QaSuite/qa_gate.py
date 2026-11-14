"""Fail-closed acceptance contract. No provider or desktop access."""
import copy
import datetime as dt
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}

def load(path):
    def unique(pairs):
        d = {}
        for k,v in pairs:
            if k in d: raise ValueError('duplicate_contract_key:' + k)
            d[k] = v
        return d
    return json.loads(Path(path).read_text(encoding='utf-8-sig'), object_pairs_hook=unique)

def anchor(head, base):
    errors = []
    if base is None: return errors  # First introduction: no contract exists in the integration base.
    for g, ids in base['groups'].items():
        for sid in ids:
            if sid not in head['groups'].get(g, {}) and not head['retired'].get(g, {}).get(sid):
                errors.append('unretired_id:' + g + ':' + sid)
    added = any(set(ids) - set(base['groups'].get(g, {})) for g,ids in head['groups'].items())
    if added and head['contract_version'] <= base['contract_version']: errors.append('version_bump_required')
    for g, ids in base['retired'].items():
        for sid, reason in ids.items():
            if head['retired'].get(g, {}).get(sid) != reason: errors.append('retired_history_removed:' + g + ':' + sid)
    return errors

def fresh(observed, started):
    try:
        observed = dt.datetime.fromisoformat(observed.replace('Z','+00:00'))
        started = dt.datetime.fromisoformat(started.replace('Z','+00:00'))
        return started <= observed <= dt.datetime.now(dt.timezone.utc) + dt.timedelta(minutes=1)
    except (ValueError, TypeError):
        return False

def validate(report, contract, root, run_id, started):
    from jsonschema import Draft202012Validator
    errors = ['schema:' + e.message for e in Draft202012Validator(load(HERE/'qa_report.schema.json')).iter_errors(report)]
    if errors: return errors
    if report['contract_version'] != contract['contract_version']: errors.append('contract_version_mismatch')
    if set(report['groups']) != set(contract['groups']): errors.append('group_set_mismatch')
    for g, ids in contract['groups'].items():
        rows = report['groups'].get(g, [])
        seen = set()
        for row in rows:
            sid = row['id']
            if sid in seen: errors.append('duplicate_id:' + g + ':' + sid)
            seen.add(sid)
            if sid not in ids: errors.append('unknown_id:' + g + ':' + sid)
            if row['status'] != 'PASS': errors.append('status_not_pass:' + g + ':' + sid + ':' + row['status'])
            ev = row['evidence']
            if not ev: errors.append('evidence_missing:' + g + ':' + sid)
            for receipt in ev:
                p = (root / receipt['path']).resolve()
                if not p.is_relative_to(root.resolve()) or not p.is_file() or p.stat().st_size != receipt['size'] or receipt['size'] <= 0:
                    errors.append('evidence_missing:' + g + ':' + sid)
                elif receipt['run_id'] != run_id or not fresh(receipt['started'], started):
                    errors.append('evidence_stale:' + g + ':' + sid)
        for sid in set(ids) - seen: errors.append('missing_id:' + g + ':' + sid)
    return errors

def source_mapping(contract):
    methods = []
    for root in ['tools/Boo.QaSuite','tools/Boo.LiveSmoke','tools/Boo.VerifyLive']:
        for p in Path(root).rglob('*Tests.cs'):
            if 'obj' in p.parts or 'bin' in p.parts: continue
            s = p.read_text(encoding='utf-8-sig')
            s = re.sub(r'/\*.*?\*/|//[^\n]*', '', s, flags=re.S)
            ns = re.search(r'namespace ([\w.]+)', s)
            if not ns: continue
            for m in re.finditer(r'\[(?:Fact|Theory)\b[^\]]*\](?:(?!public).)*public (?:async )?(?:void|Task) (\w+)\(', s, re.S):
                cls = list(re.finditer(r'public (?:sealed )?class (\w+)', s[:m.start()]))[-1].group(1)
                methods.append(ns.group(1)+'.'+cls+'.'+m.group(1))
    expected = [m for ids in contract['groups'].values() for m in ids.values()]
    assert len(expected) == len(set(expected)), 'mapping_duplicate_method'
    assert sorted(expected) == sorted(methods), ('mapping_not_bijective', set(expected)-set(methods), set(methods)-set(expected))

def emit(root, contract):
    groups = {}
    for g, ids in contract['groups'].items():
        inverse = {method:sid for sid,method in ids.items()}
        rows = []
        # Theory cases share one method/ID; require every case to pass, with every TRX receipt retained.
        cases = {}
        for p in sorted((root/g/'trx').glob('*.trx')):
            tree = ET.parse(p)
            times = tree.find('t:Times',NS)
            started = times.get('start','') if times is not None else ''
            raw_seen = set()
            for r in tree.findall('.//t:UnitTestResult',NS):
                raw = r.get('testName','')
                if raw in raw_seen:
                    rows.append({'id':inverse.get(re.sub(r'\(.*$', '', raw),raw),'status':'FAIL','evidence':[]})
                raw_seen.add(raw)
                name = re.sub(r'\(.*$', '', r.get('testName',''))
                cases.setdefault((name,p),[]).append(r.get('outcome',''))
        for (name,p), outcomes in cases.items():
            sid = inverse.get(name, 'UNKNOWN:' + name)
            # Duplicate method results across TRX files remain duplicate IDs and fail.
            rows.append({'id':sid,'status':'PASS' if all(x=='Passed' for x in outcomes) else 'FAIL',
                         'evidence':[{'path':p.relative_to(root).as_posix(),'size':p.stat().st_size,
                                      'run_id':os.environ.get('GITHUB_RUN_ID',''),
                                      'started':ET.parse(p).find('t:Times',NS).get('start','')}]})
        groups[g] = rows
    return {'contract_version':contract['contract_version'],'groups':groups}

def main():
    if '--self-test' in sys.argv: return self_test()
    if '--red-proof' in sys.argv:
        c = load(HERE/'qa_contract.json'); h = copy.deepcopy(c)
        del h['groups']['antimock']['AM0']
        errors = anchor(h,c)
        assert errors == ['unretired_id:antimock:AM0'], errors
        print('CONTRACT_GATE:' + errors[0])
        return 1
    contract = load(HERE/'qa_contract.json')
    assert type(contract['contract_version']) is int and contract['contract_version'] > 0
    assert set(contract) == {'contract_version','groups','retired'}
    assert all(isinstance(reason,str) and reason.strip() for ids in contract['retired'].values() for reason in ids.values()), 'retired_reason_missing'
    source_mapping(contract)
    ref = subprocess.check_output(['git','merge-base','origin/lane/face','HEAD'],text=True).strip()
    listing = subprocess.check_output(['git','ls-tree','--name-only',ref,'tools/Boo.QaSuite/qa_contract.json'],text=True).strip()
    base = json.loads(subprocess.check_output(['git','show',ref+':tools/Boo.QaSuite/qa_contract.json'],text=True)) if listing else None
    if '--check-contract' in sys.argv:
        errors = anchor(contract,base)
        print('BASE_ANCHOR:' + ref + (' (contract introduction)' if base is None else ''))
        print('\n'.join(errors))
        return int(bool(errors))
    root = Path(sys.argv[1])
    report = emit(root,contract)
    (root/'checks.json').write_text(json.dumps(report,indent=2)+'\n')
    errors = anchor(contract,base) + validate(report,contract,root,os.environ['GITHUB_RUN_ID'],os.environ['QA_JOB_STARTED'])
    for error in errors: print('CONTRACT_GATE:' + error)
    return int(bool(errors))

def self_test():
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        root=Path(tmp); (root/'a').write_text('evidence')
        c={'contract_version':1,'groups':{'antimock':{'A':'method','B':'method2'}},'retired':{}}
        r={'contract_version':1,'groups':{'antimock':[{'id':i,'status':'PASS','evidence':[{'path':'a','size':8,'run_id':'42','started':'2026-01-02T00:00:00Z'}]} for i in ['A','B']]}}
        assert not validate(r,c,root,'42','2026-01-01T00:00:00Z')
        def check(label, mutate, reason):
            x=copy.deepcopy(r); mutate(x['groups']['antimock'])
            errors=validate(x,c,root,'42','2026-01-01T00:00:00Z')
            assert reason in errors,(label,errors)
            print(label + ': ' + reason)
        check('removed',lambda rows:rows.pop(),'missing_id:antimock:B')
        check('renamed',lambda rows:rows[0].update(id='C'),'missing_id:antimock:A')
        check('duplicate',lambda rows:rows.append(copy.deepcopy(rows[0])),'duplicate_id:antimock:A')
        check('extra',lambda rows:rows.append(dict(rows[0],id='C')),'unknown_id:antimock:C')
        for status in ['SKIP','FAIL']:
            check(status,lambda rows:rows[0].update(status=status),'status_not_pass:antimock:A:'+status)
        check('missing evidence',lambda rows:rows[0].update(evidence=[]),'evidence_missing:antimock:A')
        check('stale evidence',lambda rows:rows[0]['evidence'][0].update(run_id='41'),'evidence_stale:antimock:A')
        check('old timestamp',lambda rows:rows[0]['evidence'][0].update(started='2025-01-01'),'evidence_stale:antimock:A')
        h=copy.deepcopy(c); del h['groups']['antimock']['A']
        assert 'unretired_id:antimock:A' in anchor(h,c)
        h['retired']={'antimock':{'A':'replaced deliberately'}}; assert not anchor(h,c)
        h=copy.deepcopy(c); h['groups']['antimock']['C']='new'; assert 'version_bump_required' in anchor(h,c)
        h['contract_version']=2; assert not anchor(h,c)
        x=copy.deepcopy(r); x['extra']=True; assert any(e.startswith('schema:') for e in validate(x,c,root,'42','0'))
    source_mapping(load(HERE/'qa_contract.json'))
    print('CONTRACT_SELF_TEST_OK')
    return 0

if __name__ == '__main__': sys.exit(main())
