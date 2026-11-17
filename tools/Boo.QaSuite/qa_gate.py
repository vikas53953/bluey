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

def theory_metadata(contract):
    metadata = contract.get('theory_cases', {})
    assert isinstance(metadata, dict), 'theory_metadata_object'
    methods = [m for ids in contract['groups'].values() for m in ids.values()]
    for method, cases in metadata.items():
        assert method in methods, 'theory_method_unmapped'
        assert isinstance(cases, list) and cases, 'theory_cases_empty'
        names, tuples = set(), set()
        for case in cases:
            assert isinstance(case, dict) and set(case) == {'name','arguments'}, 'theory_case_shape'
            assert isinstance(case['name'], str) and case['name'].startswith(method + '('), 'theory_case_name'
            args = case['arguments']
            assert isinstance(args, list) and args and all(type(a) in (bool,str) for a in args), 'theory_arguments'
            key = json.dumps(args)
            assert case['name'] not in names and key not in tuples, 'theory_case_duplicate'
            names.add(case['name']); tuples.add(key)
    discovered = {}
    for folder in ['tools/Boo.QaSuite','tools/Boo.LiveSmoke','tools/Boo.VerifyLive']:
        for path in Path(folder).rglob('*Tests.cs'):
            if 'obj' in path.parts or 'bin' in path.parts: continue
            text = path.read_text(encoding='utf-8-sig')
            ns = re.search(r'namespace ([\w.]+)',text)
            cls = re.search(r'public (?:sealed )?class (\w+)',text)
            if ns and cls: discovered.update(discover_theories(text,ns[1],cls[1]))
    required = {m:cases for m,cases in discovered.items() if m in methods}
    assert set(metadata) == set(required), 'theory_method_set'
    for method,cases in required.items():
        assert sorted(metadata[method],key=lambda c:c['name']) == sorted(cases,key=lambda c:c['name']), 'theory_source_mismatch'
    return metadata


def discover_theories(source, namespace, cls):
    found = {}
    for match in re.finditer(r'((?:\s*\[[^\]]*\]\s*)+)public (?:async )?(?:void|Task) (\w+)\(([^)]*)\)', source):
        attrs, name, signature = match.groups()
        if not re.search(r'\[Theory\b', attrs): continue
        assert re.search(r'\[Theory\s*\]', attrs), 'unsupported_theory_options'
        assert not re.search(r'MemberData|ClassData', attrs), 'unsupported_theory_data'
        assert all(a in ('Theory','InlineData') for a in re.findall(r'\[([\w]+)',attrs)), 'unsupported_theory_attribute'
        parameters = [re.fullmatch(r'\s*(bool|string)\s+(\w+)\s*', x) for x in signature.split(',')]
        assert all(parameters), 'unsupported_theory_parameters'
        method = namespace + '.' + cls + '.' + name
        cases = []
        for raw in re.findall(r'\[InlineData\((.*?)\)\]', attrs, re.S):
            try: args = json.loads('[' + raw + ']')
            except ValueError: raise AssertionError('unsupported_inline_literal')
            assert len(args) == len(parameters), 'theory_arity'
            assert all(type(a) is (bool if p.group(1)=='bool' else str) for a,p in zip(args,parameters)), 'unsupported_inline_literal'
            values = [p.group(2) + ': ' + (str(a) if type(a) is bool else json.dumps(a,ensure_ascii=False)) for p,a in zip(parameters,args)]
            cases.append({'name':method + '(' + ', '.join(values) + ')','arguments':args})
        assert cases, 'theory_inline_missing'
        assert method not in found, 'duplicate_theory_method'
        found[method] = cases
    return found


def theory_results(cases, declared):
    return (isinstance(cases,list) and len(cases)==len(declared)
            and all(isinstance(c,dict) and set(c)=={'name','outcome'} and isinstance(c['name'],str) and c['outcome']=='Passed' for c in cases)
            and len({c['name'] for c in cases})==len(cases)
            and {c['name'] for c in cases}=={c['name'] for c in declared})


def anchor(head, base):
    errors = []
    try:
        current = theory_metadata(head)
        previous = base.get('theory_cases', {}) if base else {}
    except (AssertionError, TypeError, KeyError) as e: return ['theory_metadata:' + str(e)]
    for method, cases in previous.items():
        if any(c not in current.get(method, []) for c in cases): errors.append('theory_history_changed:' + method)
        old_ids = [(g,i) for g,ids in base['groups'].items() for i,m in ids.items() if m==method]
        if any(head['groups'].get(g,{}).get(i)!=method for g,i in old_ids): errors.append('theory_mapping_changed:' + method)
    if base and current != previous and head['contract_version'] <= base['contract_version']: errors.append('version_bump_required')
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
    try: metadata = theory_metadata(contract)
    except (AssertionError, TypeError, KeyError) as e: return ['theory_metadata:' + str(e)]
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
            method = ids.get(sid)
            if method in metadata and not theory_results(row.get('cases'), metadata[method]):
                errors.append('theory_cases_not_pass:' + g + ':' + sid)
            ev = row['evidence']
            if not ev: errors.append('evidence_missing:' + g + ':' + sid)
            if method in metadata and len(ev) != 1:
                errors.append('theory_receipt_count:' + g + ':' + sid)
            for receipt in ev:
                p = (root / receipt['path']).resolve()
                if not p.is_relative_to(root.resolve()) or not p.is_file() or p.stat().st_size != receipt['size'] or receipt['size'] <= 0:
                    errors.append('evidence_missing:' + g + ':' + sid)
                elif receipt['run_id'] != run_id or not fresh(receipt['started'], started):
                    errors.append('evidence_stale:' + g + ':' + sid)
                elif method in metadata:
                    try:
                        tree = ET.parse(p)
                        times = tree.find('t:Times', NS)
                        actual = [{'name':r.get('testName',''), 'outcome':r.get('outcome','')}
                                  for r in tree.findall('.//t:UnitTestResult', NS)
                                  if re.sub(r'\(.*$', '', r.get('testName','')) == method]
                        if (times is None or times.get('start','') != receipt['started']
                                or not theory_results(actual, metadata[method])
                                or sorted(actual, key=lambda c:c['name']) != sorted(row.get('cases',[]), key=lambda c:c['name'])):
                            errors.append('theory_receipt_mismatch:' + g + ':' + sid)
                    except (ET.ParseError, OSError, ValueError, TypeError, KeyError):
                        errors.append('theory_receipt_invalid:' + g + ':' + sid)
        for sid in set(ids) - seen: errors.append('missing_id:' + g + ':' + sid)
    return errors

def source_mapping(contract):
    methods = []
    theories = {}
    for root in ['tools/Boo.QaSuite','tools/Boo.LiveSmoke','tools/Boo.VerifyLive']:
        for p in Path(root).rglob('*Tests.cs'):
            if 'obj' in p.parts or 'bin' in p.parts: continue
            s = p.read_text(encoding='utf-8-sig')
            s = re.sub(r'/\*.*?\*/|//[^\n]*', '', s, flags=re.S)
            ns = re.search(r'namespace ([\w.]+)', s)
            if not ns: continue
            cls_match = re.search(r'public (?:sealed )?class (\w+)', s)
            if cls_match: theories.update(discover_theories(s,ns.group(1),cls_match.group(1)))
            for m in re.finditer(r'\[(?:Fact|Theory)\b[^\]]*\](?:(?!public).)*public (?:async )?(?:void|Task) (\w+)\(', s, re.S):
                cls = list(re.finditer(r'public (?:sealed )?class (\w+)', s[:m.start()]))[-1].group(1)
                methods.append(ns.group(1)+'.'+cls+'.'+m.group(1))
    metadata = theory_metadata(contract)
    assert set(metadata) == set(theories), 'theory_method_set'
    for method, cases in theories.items():
        assert sorted(metadata[method],key=lambda c:c['name']) == sorted(cases,key=lambda c:c['name']), 'theory_source_mismatch:' + method
    expected = [m for ids in contract['groups'].values() for m in ids.values()]
    assert len(expected) == len(set(expected)), 'mapping_duplicate_method'
    assert sorted(expected) == sorted(methods), ('mapping_not_bijective', set(expected)-set(methods), set(methods)-set(expected))

def emit(root, contract):
    metadata = theory_metadata(contract)
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
                cases.setdefault((name,p),[]).append({'name':raw,'outcome':r.get('outcome','')})
        for (name,p), outcomes in cases.items():
            sid = inverse.get(name, 'UNKNOWN:' + name)
            # Duplicate method results across TRX files remain duplicate IDs and fail.
            rows.append({'id':sid,'status':'PASS' if (theory_results(outcomes,metadata[name]) if name in metadata else all(x['outcome']=='Passed' for x in outcomes)) else 'FAIL',
                         'evidence':[{'path':p.relative_to(root).as_posix(),'size':p.stat().st_size,
                                      'run_id':os.environ.get('GITHUB_RUN_ID',''),
                                      'started':(ET.parse(p).find('t:Times',NS).get('start','') if ET.parse(p).find('t:Times',NS) is not None else '')}]})
            if name in metadata: rows[-1]['cases'] = outcomes
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
    assert set(contract) == {'contract_version','groups','retired','theory_cases'}
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

def theory_self_test():
    import tempfile
    full = load(HERE/'qa_contract.json')
    for method, declared in full['theory_cases'].items():
        group,sid = next((g,i) for g,ids in full['groups'].items() for i,m in ids.items() if m==method)
        c = {'contract_version':16,'groups':{group:{sid:method}},'retired':{},'theory_cases':{method:declared}}
        baseline = [{'name':x['name'],'outcome':'Passed'} for x in declared]
        variants = {'complete':baseline,'only_first':baseline[:1],'omitted':baseline[:-1],
                    'duplicate':baseline+[baseline[0]],
                    'unexpected_extra':baseline+[{'name':method+'(branch: "unexpected")','outcome':'Passed'}],
                    'substitution':baseline[:-1]+[{'name':method+'(branch: "unexpected")','outcome':'Passed'}]}
        for outcome in ['Failed','NotExecuted']:
            variants[outcome] = [dict(x,outcome=outcome if i==0 else 'Passed') for i,x in enumerate(baseline)]
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp); trx=root/group/'trx'; trx.mkdir(parents=True)
            def write(cases,path):
                tree=ET.Element('TestRun',xmlns=NS['t']); ET.SubElement(tree,'Times',start='2026-01-02T00:00:00Z')
                results=ET.SubElement(tree,'Results')
                for case in cases: ET.SubElement(results,'UnitTestResult',testName=case['name'],outcome=case['outcome'])
                ET.ElementTree(tree).write(path,encoding='utf-8')
            old=os.environ.get('GITHUB_RUN_ID'); os.environ['GITHUB_RUN_ID']='42'
            try:
                for label,cases in variants.items():
                    # Independent validator boundary: fabricated complete/PASS JSON must
                    # not authenticate a partial, substituted, failed or skipped TRX.
                    write(baseline,trx/'one.trx'); fabricated=emit(root,c)
                    write(cases,trx/'one.trx')
                    fabricated['groups'][group][0]['evidence'][0]['size']=(trx/'one.trx').stat().st_size
                    bound_errors=validate(fabricated,c,root,'42','2026-01-01T00:00:00Z')
                    assert bool(bound_errors)==(label!='complete'),('receipt_binding',sid,label,bound_errors)
                    write(cases,trx/'one.trx'); report=emit(root,c)
                    errors=validate(report,c,root,'42','2026-01-01T00:00:00Z')
                    assert bool(errors)==(label!='complete'),(sid,label,errors)
                    # Independent boundary: forge PASS on a directly mutated complete report.
                    write(baseline,trx/'one.trx'); forged=emit(root,c)
                    forged['groups'][group][0]['cases']=copy.deepcopy(cases)
                    errors=validate(forged,c,root,'42','2026-01-01T00:00:00Z')
                    assert bool(errors)==(label!='complete'),('forged',sid,label,errors)
                missing=copy.deepcopy(forged); missing['groups'][group][0].pop('cases')
                assert validate(missing,c,root,'42','2026-01-01T00:00:00Z')
                malformed=copy.deepcopy(forged); malformed['groups'][group][0]['cases']=[{'name':42,'outcome':'Passed'}]
                assert validate(malformed,c,root,'42','2026-01-01T00:00:00Z')
                write(baseline,trx/'two.trx')
                assert any(e.startswith('duplicate_id:') for e in validate(emit(root,c),c,root,'42','2026-01-01T00:00:00Z'))
                for label in ['empty','duplicate','missing','removal','substitution','malformed']:
                    h=copy.deepcopy(c); cases=h['theory_cases'][method]
                    if label=='empty': cases.clear()
                    elif label=='duplicate': cases.append(copy.deepcopy(cases[0]))
                    elif label=='missing': del h['theory_cases'][method]
                    elif label=='removal': cases.pop()
                    elif label=='substitution': cases[-1]['arguments'][0]='unexpected'
                    else: cases[0]['arguments']=[1]
                    assert validate(forged,h,root,'42','2026-01-01T00:00:00Z'),label
                    assert anchor(h,c),label
                migration=copy.deepcopy(c); del migration['theory_cases']; migration['contract_version']=15
                assert not anchor(c,migration)
                migration['contract_version']=16
                assert 'version_bump_required' in anchor(c,migration)
            finally:
                if old is None: os.environ.pop('GITHUB_RUN_ID',None)
                else: os.environ['GITHUB_RUN_ID']=old
        print('THEORY_BOUNDARIES_OK:' + sid)
    for data in ['[MemberData("Cases")]','[ClassData(typeof(Cases))]','[InlineData(value: true)]','[InlineData(1)]']:
        try: discover_theories('[Theory]'+data+' public void T(bool value) {}','N','C')
        except AssertionError: pass
        else: raise AssertionError('unsupported_data_accepted:' + data)
    try: discover_theories('[Theory(Skip = "unsupported")][InlineData(true)] public void T(bool value) {}','N','C')
    except AssertionError: pass
    else: raise AssertionError('parameterized_theory_accepted')


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
    theory_self_test()
    source_mapping(load(HERE/'qa_contract.json'))
    print('CONTRACT_SELF_TEST_OK')
    return 0

if __name__ == '__main__': sys.exit(main())
