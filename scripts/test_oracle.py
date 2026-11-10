"""Freeze or verify the independently authored test contract. Never read secrets.

Modes
  (none)          Verify: every oracle file must hash to its frozen value, no frozen file may be
                  missing, no new file may be unfrozen, and the revision history chain must be intact.
                  Exit 1 on any difference. CI runs this on every push and pull request.
  --extend REASON Additive revision: freeze new files only; refuses if any frozen file changed.
  --rebaseline --reason REASON --accept-changed a,b,c
                  Reviewed revision: freeze new files AND accept changes to exactly the frozen files
                  named in --accept-changed. Refuses if the named set differs from the files that
                  actually changed or were removed, so nothing can slip in unnamed. The reason and the
                  file lists are written into the manifest for review.
  --freeze        First revision only.
"""
import hashlib
import json
import pathlib
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parent.parent
manifest_path = root / 'docs/verification/test-oracle.json'
GENERATED_PARTS = {'bin', 'obj', 'TestResults'}

def oracle_paths():
    paths = [p for p in (root / 'tests').rglob('*') if p.is_file() and not GENERATED_PARTS.intersection(p.relative_to(root).parts)]
    paths += list((root / 'docs/verification').glob('acceptance-*.md'))
    paths += [root / 'docs/verification/public-contracts.md', root / 'src/Boo.Core/Contracts.cs']
    return sorted(p.relative_to(root).as_posix() for p in paths)

def digest(relative):
    path = (root / relative).resolve()
    if not path.is_relative_to(root):
        raise ValueError('Oracle path escapes checkout')
    raw = path.read_bytes()
    try:
        content = raw.decode('utf-8-sig').replace('\r\n', '\n').encode('utf-8')
    except UnicodeDecodeError:
        content = raw  # Not UTF-8 text (an image or other binary fixture): hash the exact bytes.
    return hashlib.sha256(content).hexdigest()

def manifest_digests(path):
    """Hashes a manifest may have been chained with: LF and CRLF forms of the same text.
    Early revisions were written on Windows (CRLF) and git stores them as LF, so a raw-byte hash
    depended on the checkout. Newline style is not tampering; any other byte change is."""
    lf = path.read_bytes().replace(b'\r\n', b'\n')
    return {hashlib.sha256(lf).hexdigest(), hashlib.sha256(lf.replace(b'\n', b'\r\n')).hexdigest()}

def verify_chain(data):
    """Walk previousManifest links back to revision 1; each link must match its recorded hash."""
    links, current = 0, data
    while current.get('previousManifest'):
        previous = (root / current['previousManifest']).resolve()
        if not previous.is_relative_to(root) or not previous.is_file():
            return f"revision {current['revision']}: previous manifest {current['previousManifest']} is missing"
        if current.get('previousManifestSha256') not in manifest_digests(previous):
            return f"revision {current['revision']}: {current['previousManifest']} does not match its recorded hash"
        current = json.loads(previous.read_text(encoding='utf-8'))
        links += 1
    if current.get('revision') != 1:
        return f"chain ends at revision {current.get('revision')}, not revision 1"
    return links

def compare(data, actual):
    frozen = data['files']
    changed = sorted(p for p in frozen if p in actual and actual[p] != frozen[p])
    removed = sorted(p for p in frozen if p not in actual)
    added = sorted(set(actual) - set(frozen))
    return changed, removed, added

def git_head():
    return subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()

def argument_value(flag):
    if flag not in sys.argv:
        return None
    index = sys.argv.index(flag)
    if index + 1 >= len(sys.argv) or not sys.argv[index + 1].strip() or sys.argv[index + 1].startswith('--'):
        raise SystemExit(f'{flag} needs a value')
    return sys.argv[index + 1].strip()

def write_revision(data, actual, reason, added, changed, removed):
    history = manifest_path.parent / 'test-oracle-history' / f"revision-{data['revision']}.json"
    history.parent.mkdir(exist_ok=True)
    with history.open('x', encoding='utf-8', newline='\n') as snapshot:
        snapshot.write(manifest_path.read_text(encoding='utf-8'))
    data['revision'] += 1
    data['previousManifest'] = history.relative_to(root).as_posix()
    data['previousManifestSha256'] = hashlib.sha256(history.read_bytes().replace(b'\r\n', b'\n')).hexdigest()
    data['revisionReason'] = reason
    data['revisionCommit'] = git_head()
    data['addedFiles'] = added
    data['changedFiles'] = changed
    data['removedFiles'] = removed
    data['files'] = actual
    with manifest_path.open('w', encoding='utf-8', newline='\n') as out:
        out.write(json.dumps(data, indent=2, ensure_ascii=False) + '\n')

if '--extend' in sys.argv:
    reason = argument_value('--extend')
    if not reason:
        raise SystemExit('An additive revision requires a recorded reason')
    data = json.loads(manifest_path.read_text(encoding='utf-8'))
    actual = {p: digest(p) for p in oracle_paths()}
    changed, removed, additions = compare(data, actual)
    if changed or removed:
        raise SystemExit('Refusing to change or remove frozen files: ' + ', '.join(changed + removed))
    if not additions:
        raise SystemExit('No independently authored files to add')
    write_revision(data, actual, reason, additions, [], [])
    print('ORACLE_EXTENDED revision', data['revision'], len(additions), 'additions; existing hashes unchanged')
elif '--rebaseline' in sys.argv:
    reason = argument_value('--reason')
    if not reason:
        raise SystemExit('A rebaseline requires --reason "<why each change is legitimate>"')
    named = sorted(filter(None, (argument_value('--accept-changed') or '').split(',')))
    data = json.loads(manifest_path.read_text(encoding='utf-8'))
    actual = {p: digest(p) for p in oracle_paths()}
    changed, removed, additions = compare(data, actual)
    if set(named) != set(changed) | set(removed):
        unnamed = sorted((set(changed) | set(removed)) - set(named))
        unchanged = sorted(set(named) - set(changed) - set(removed))
        raise SystemExit('Refusing to rebaseline: --accept-changed must name exactly the changed or removed frozen files.'
                         + (('\n  changed but not named: ' + ', '.join(unnamed)) if unnamed else '')
                         + (('\n  named but not changed: ' + ', '.join(unchanged)) if unchanged else ''))
    if not (changed or removed or additions):
        raise SystemExit('Nothing to rebaseline; the oracle already verifies')
    write_revision(data, actual, reason, additions, changed, removed)
    print('ORACLE_REBASELINED revision', data['revision'], f'{len(additions)} added, {len(changed)} changed, {len(removed)} removed')
elif '--freeze' in sys.argv:
    if manifest_path.exists():
        raise SystemExit('Refusing to overwrite an existing oracle revision')
    data = {'revision': 1, 'baselineCommit': git_head(), 'hashFormat': 'SHA-256 of UTF-8 text, BOM removed, CRLF normalized to LF', 'files': {p: digest(p) for p in oracle_paths()}}
    with manifest_path.open('w', encoding='utf-8', newline='\n') as out:
        out.write(json.dumps(data, indent=2) + '\n')
    print('ORACLE_FROZEN', len(data['files']), 'files')
else:
    data = json.loads(manifest_path.read_text(encoding='utf-8'))
    actual = {p: digest(p) for p in oracle_paths()}
    changed, removed, added = compare(data, actual)
    chain = verify_chain(data)
    if changed or removed or added or isinstance(chain, str):
        print('ORACLE_CHANGED: the frozen test contract differs from docs/verification/test-oracle.json')
        for label, files in (('changed', changed), ('missing', removed), ('new, not frozen', added)):
            for path in files:
                print(f'  {label}: {path}')
        if isinstance(chain, str):
            print('  history chain broken:', chain)
        print('Tests, acceptance notes and Contracts.cs are frozen. A legitimate change needs a reviewed revision:')
        print('  python -I scripts/test_oracle.py --extend "<reason>"   (new files only)')
        print('  python -I scripts/test_oracle.py --rebaseline --reason "<reason>" --accept-changed <file,file>')
        raise SystemExit(1)
    print('ORACLE_VERIFIED revision', data['revision'], len(actual), 'files;', chain, 'history links intact')
