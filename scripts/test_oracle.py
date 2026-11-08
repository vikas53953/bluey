"""Freeze or verify the independently authored test contract. Never read secrets."""
import hashlib
import json
import pathlib
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parent.parent
manifest_path = root / 'docs/verification/test-oracle.json'

def oracle_paths():
    paths = [p for p in (root / 'tests').rglob('*') if p.is_file() and not {'bin', 'obj'}.intersection(p.relative_to(root).parts)]
    paths += list((root / 'docs/verification').glob('acceptance-*.md'))
    paths += [root / 'docs/verification/public-contracts.md', root / 'src/Boo.Core/Contracts.cs']
    return sorted(p.relative_to(root).as_posix() for p in paths)

def digest(relative):
    path = (root / relative).resolve()
    if not path.is_relative_to(root):
        raise ValueError('Oracle path escapes checkout')
    content = path.read_text(encoding='utf-8-sig').replace('\r\n', '\n')
    return hashlib.sha256(content.encode('utf-8')).hexdigest()

if '--extend' in sys.argv:
    argument = sys.argv.index('--extend')
    if argument + 1 >= len(sys.argv) or not sys.argv[argument + 1].strip():
        raise SystemExit('An additive revision requires a recorded reason')
    data = json.loads(manifest_path.read_text(encoding='utf-8'))
    actual = {p: digest(p) for p in oracle_paths()}
    changed = [p for p, value in data['files'].items() if actual.get(p) != value]
    if changed:
        raise SystemExit('Refusing to change or remove frozen files: ' + ', '.join(changed))
    additions = sorted(set(actual) - set(data['files']))
    if not additions:
        raise SystemExit('No independently authored files to add')
    history = manifest_path.parent / 'test-oracle-history' / f"revision-{data['revision']}.json"
    history.parent.mkdir(exist_ok=True)
    with history.open('x', encoding='utf-8') as snapshot:
        snapshot.write(manifest_path.read_text(encoding='utf-8'))
    data['revision'] += 1
    data['previousManifest'] = history.relative_to(root).as_posix()
    data['previousManifestSha256'] = hashlib.sha256(history.read_bytes()).hexdigest()
    data['revisionReason'] = sys.argv[argument + 1]
    data['addedFiles'] = additions
    data['files'] = actual
    manifest_path.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')
    print('ORACLE_EXTENDED revision', data['revision'], len(additions), 'additions; existing hashes unchanged')
elif '--freeze' in sys.argv:
    if manifest_path.exists():
        raise SystemExit('Refusing to overwrite an existing oracle revision')
    baseline = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()
    data = {'revision': 1, 'baselineCommit': baseline, 'hashFormat': 'SHA-256 of UTF-8 text, BOM removed, CRLF normalized to LF', 'files': {p: digest(p) for p in oracle_paths()}}
    manifest_path.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')
    print('ORACLE_FROZEN', len(data['files']), 'files')
else:
    data = json.loads(manifest_path.read_text(encoding='utf-8'))
    actual = {p: digest(p) for p in oracle_paths()}
    differences = sorted(p for p in set(actual) | set(data['files']) if actual.get(p) != data['files'].get(p))
    if differences:
        print('ORACLE_CHANGED:', ', '.join(differences))
        raise SystemExit(1)
    print('ORACLE_VERIFIED revision', data['revision'], len(actual), 'files')
