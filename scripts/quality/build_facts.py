"""Build facts: check that build-facts.json describes the SHIPPED build, completely, before any judge reads it.

  python -I scripts/quality/build_facts.py check FACTS --solution Boo.sln --publish-dir artifacts/ci
                                     validate FACTS (written by scripts/quality/buildfacts, the extractor) and reconcile
                                     every managed .dll/.exe in the publish folder with a judged compile or a package
  python -I scripts/quality/build_facts.py --self-test
                                     prove every failure below with planted facts and a planted publish folder
  python -I scripts/quality/build_facts.py fixtures --extractor EXE [--dotnet DOTNET] --work DIR
                                     build every must-fail fixture in scripts/quality/buildfacts/fixtures with a binary
                                     log, extract its facts and show that each bypass is VISIBLE in them (quotes the
                                     facts lines); the tamper fixtures must also fail `check`
  python -I scripts/quality/build_facts.py learn FACTS
                                     print task keys and csc switches in FACTS that known.json does not list yet

Why: two quality gates (banned APIs, architecture references) once judged a different build from the one that ships,
and were bypassed by build code that only acts in the real Release build/publish. The facts come from the binary logs
of windows-ci's own Release build and publish, so the judges built on them read what was really compiled.

`check` fails closed (exit 1) when:
  - the facts are malformed (wrong schema, missing or mistyped field), or a binary log's build failed;
  - any text still holds a machine path or the user name;
  - a project of the solution has no Csc call from CoreCompile in the `build` log, or a Csc call has no output hash,
    a compiler outside the SDK install, a Roslyn command-line error, a response file (`@file`; the SDK passes none,
    so one means build code added it), or a switch that scripts/quality/buildfacts/known.json does not list;
  - any task ran that known.json does not list as `Task | Target | file defining the target | task assembly` (so a
    target added by repo build code, a NuGet package or an MSBuild SDK that runs any task is unknown until reviewed);
  - a managed .dll/.exe in the publish folder does not trace, hash for hash, through recorded copies and ReadyToRun
    compiles to a recorded Csc output or to a restored package / runtime pack, or carries the name of a repo-built
    assembly without coming from its compile.
Exit 2 means the check could not run (unreadable input). Rules and known holes: docs/quality/RULES.md "Build facts".
"""
import hashlib
import json
import os
import pathlib
import re
import shutil
import struct
import subprocess
import sys
import tempfile

root = pathlib.Path(__file__).resolve().parent.parent.parent
TOOL_DIR = root / 'scripts' / 'quality' / 'buildfacts'
KNOWN = TOOL_DIR / 'known.json'
FIXTURES = TOOL_DIR / 'fixtures'
SCHEMA = 'boo-build-facts/1'
RULES = 'docs/quality/RULES.md#build-facts-the-shipped-build'

BINLOG_KEYS = {'label': str, 'succeeded': bool, 'msbuildVersion': str, 'commandLine': (str, type(None)), 'units': list,
               'tasks': list, 'copies': list, 'readyToRun': list, 'runtimePacks': list}
UNIT_KEYS = {'project': str, 'targetFramework': str, 'runtimeIdentifier': str, 'configuration': str,
             'globalProperties': list, 'properties': dict, 'imports': list, 'importsSkipped': list,
             'packageReferences': list, 'packageDownloads': list, 'projectReferenceItems': list,
             'projectReferencesFollowed': list, 'referenceItems': list, 'frameworkReferences': list,
             'editorConfigItems': list, 'assets': (dict, type(None)), 'targetsWithTasks': list, 'csc': list}
CSC_KEYS = {'target': (str, type(None)), 'targetFile': str, 'taskAssembly': str, 'tool': list, 'arguments': list,
            'switches': list, 'responseFiles': list, 'noConfig': bool, 'analyzerConfigs': list, 'analyzers': list,
            'additionalFiles': list, 'sources': list, 'nowarn': list, 'warnaserror': list,
            'ruleset': (str, type(None)), 'errorLog': (dict, type(None)), 'roslyn': dict, 'references': list,
            'output': dict, 'refOutput': (dict, type(None))}
COPY_KEYS = {'source': str, 'destination': str, 'target': (str, type(None)), 'targetFile': str,
             'package': (str, type(None)), 'sourceSha256': (str, type(None))}
R2R_KEYS = {'input': str, 'inputSha256': (str, type(None)), 'output': str, 'outputSha256': (str, type(None)),
            'package': (str, type(None)), 'compiler': str}
SDK_CSC = re.compile(r'^<dotnet>/sdk/[0-9][^/]*/Roslyn/bincore/csc\.(?:dll|exe)$')
HOST_ARGS = {'<dotnet>/dotnet.exe', '<dotnet>/dotnet', 'exec'}
# In the JSON text: a drive-letter path, a UNC path (\\server\ is written \\\\server\\), or a Unix home folder.
LEAK = re.compile(r'(?<![A-Za-z0-9])[A-Za-z]:[\\/]|(?<![\\A-Za-z0-9>])\\\\\\\\[A-Za-z0-9.-]+\\\\'
                  r'|(?:^|[\s"\'=])/(?:home|Users)/[^/\s"]+')


class Problems:
    def __init__(self):
        self.items = []

    def add(self, text):
        self.items.append(text)


# ---------------------------------------------------------------------------------------------------------------------
# Structure
# ---------------------------------------------------------------------------------------------------------------------
def typed(problems, where, obj, keys):
    if not isinstance(obj, dict):
        problems.add(f'{where}: expected an object')
        return False
    ok = True
    for key, kind in keys.items():
        if key not in obj:
            problems.add(f'{where}: missing field "{key}"')
            ok = False
        elif not isinstance(obj[key], kind):
            problems.add(f'{where}: field "{key}" has the wrong type')
            ok = False
    return ok


def structure(facts, problems):
    if not isinstance(facts, dict) or facts.get('schema') != SCHEMA:
        problems.add(f'facts: schema is not {SCHEMA}')
        return False
    logs = facts.get('binlogs')
    if not isinstance(logs, list) or not logs:
        problems.add('facts: no binary logs')
        return False
    ok = True
    labels = set()
    for b in logs:
        if not typed(problems, 'binlog', b, BINLOG_KEYS):
            ok = False
            continue
        label = b['label']
        if label in labels:
            problems.add(f'binlog {label}: label used twice')
            ok = False
        labels.add(label)
        for u in b['units']:
            if not typed(problems, f'{label}: unit', u, UNIT_KEYS):
                ok = False
                continue
            for c in u['csc']:
                where = f'{label}: {u["project"]} Csc'
                if not typed(problems, where, c, CSC_KEYS):
                    ok = False
                    continue
                if not isinstance(c['output'].get('path'), str):
                    problems.add(f'{where}: output has no path')
                    ok = False
        for c in b['copies']:
            ok = typed(problems, f'{label}: copy', c, COPY_KEYS) and ok
        for r in b['readyToRun']:
            ok = typed(problems, f'{label}: ReadyToRun compile', r, R2R_KEYS) and ok
        for t in b['tasks']:
            if not (isinstance(t, dict) and isinstance(t.get('task'), str) and isinstance(t.get('count'), int)):
                problems.add(f'{label}: malformed task entry')
                ok = False
    return ok


def leaks(text, problems):
    for m in list(LEAK.finditer(text))[:10]:
        start = max(0, m.start() - 60)
        problems.add(f'machine path left in the facts: ...{text[start:m.end() + 40]!r}')
    for name in {os.environ.get('USERNAME', ''), os.environ.get('USER', '')}:
        if len(name) >= 3 and name.lower() in text.lower():
            problems.add(f'the user name ({len(name)} letters) is in the facts')


# ---------------------------------------------------------------------------------------------------------------------
# Solution, compiler calls, tasks
# ---------------------------------------------------------------------------------------------------------------------
def compiler_calls(facts, problems, known, projects, build_label):
    switches = set(known.get('cscSwitches', []))
    by_label = {b['label']: b for b in facts['binlogs']}
    if build_label not in by_label:
        problems.add(f'no binary log labelled "{build_label}" (the Release build of the solution)')
    else:
        compiled = {u['project'] for u in by_label[build_label]['units']
                    if any(c['target'] == 'CoreCompile' for c in u['csc'])}
        for p in projects:
            if p not in compiled:
                problems.add(f'{p}: no Csc call from CoreCompile in the "{build_label}" log (project missing from the '
                             f'judged build, or its compile was skipped)')
    for b in facts['binlogs']:
        if not b['succeeded']:
            problems.add(f'binlog {b["label"]}: the build failed')
        for u in b['units']:
            for c in u['csc']:
                where = f'{b["label"]}: {u["project"]} ({u["targetFramework"]} {u["runtimeIdentifier"]}) Csc'
                if not c['output'].get('sha256'):
                    problems.add(f'{where}: output {c["output"].get("path")} has no hash (file missing after build)')
                if not c['tool'] or not SDK_CSC.match(c['tool'][-1]) or not set(c['tool'][:-1]) <= HOST_ARGS:
                    problems.add(f'{where}: compiler is not from the SDK install: {c["tool"]}')
                if not c['taskAssembly'].startswith('<dotnet>/sdk/'):
                    problems.add(f'{where}: Csc task class is not the SDK\'s: {c["taskAssembly"]}')
                if c['responseFiles'] or any(a.startswith('@') for a in c['arguments']):
                    problems.add(f'{where}: response file passed to the compiler ({[r.get("path") for r in c["responseFiles"]]})'
                                 f'; the SDK passes none, so build code added it (its text is expanded in the facts)')
                for s in c['switches']:
                    if s not in switches and s != '@':
                        problems.add(f'{where}: unknown compiler switch {s} (not in scripts/quality/buildfacts/known.json)')
                for e in c['roslyn'].get('errors', []):
                    problems.add(f'{where}: the compiler command line does not parse: {e}')


def tasks(facts, problems, known):
    allowed = set(known.get('tasks', []))
    for b in facts['binlogs']:
        for t in b['tasks']:
            if t['task'] not in allowed:
                problems.add(f'binlog {b["label"]}: unknown task ran: {t["task"]} (x{t["count"]}); review it and add '
                             f'it to scripts/quality/buildfacts/known.json, or remove the build code that runs it')


# ---------------------------------------------------------------------------------------------------------------------
# Publish reconciliation
# ---------------------------------------------------------------------------------------------------------------------
def is_managed(data):
    """True for a PE file with a CLI (.NET) header."""
    try:
        if data[:2] != b'MZ':
            return False
        pe = struct.unpack_from('<I', data, 0x3C)[0]
        if data[pe:pe + 4] != b'PE\0\0':
            return False
        opt = pe + 24
        magic = struct.unpack_from('<H', data, opt)[0]
        if magic == 0x10B:
            count_at, dirs = opt + 92, opt + 96
        elif magic == 0x20B:
            count_at, dirs = opt + 108, opt + 112
        else:
            return False
        if struct.unpack_from('<I', data, count_at)[0] <= 14:
            return False
        rva, size = struct.unpack_from('<II', data, dirs + 14 * 8)
        return rva != 0 and size != 0
    except struct.error:
        return False


def package_of(path):
    m = re.match(r'<nuget>/([^/]+)/([^/]+)/', path)
    return (m.group(1).lower(), m.group(2).lower()) if m else None


def reconcile(facts, problems, publish_dir, publish_rel):
    compiled = {}
    copies = {}
    r2r = {}
    packages = set()
    for b in facts['binlogs']:
        for u in b['units']:
            for c in u['csc']:
                if c['output'].get('sha256'):
                    compiled.setdefault(c['output']['path'], set()).add(c['output']['sha256'])
            assets = u.get('assets') or {}
            for lib in assets.get('libraries', []):
                m = re.match(r'(.+?)/(\S+) \(package\)$', lib)
                if m:
                    packages.add((m.group(1).lower(), m.group(2).lower()))
        for p in b['runtimePacks']:
            name, _, version = p.rpartition('/')
            packages.add((name.lower(), version.lower()))
        for c in b['copies']:
            copies.setdefault(c['destination'], []).append(c)
        for r in b['readyToRun']:
            r2r[r['output']] = r
    repo_names = {pathlib.PurePosixPath(p).name.lower() for p in compiled}

    def trace(path, sha, seen):
        if (path, sha) in seen or len(seen) > 20:
            return None, f'copy loop at {path}', None
        seen = seen | {(path, sha)}
        if path in compiled:
            if sha in compiled[path]:
                return 'compiled', path, path
            return None, f'{path} differs from what the compiler wrote (changed after the judged compile)', None
        if path in r2r:
            r = r2r[path]
            if r['outputSha256'] != sha:
                return None, f'{path} differs from the ReadyToRun compiler\'s output', None
            kind, detail, leaf = trace(r['input'], r['inputSha256'], seen)
            return (kind and kind + '+r2r'), detail, leaf
        pkg = package_of(path)
        if pkg:
            if pkg in packages:
                return 'package', f'package {pkg[0]}/{pkg[1]}', path
            return None, f'{path} comes from package {pkg[0]}/{pkg[1]}, which no restore or runtime pack lists', None
        if path.startswith('<dotnet>/'):
            return 'sdk', path, path
        candidates = copies.get(path, [])
        for c in candidates:
            if c['sourceSha256'] == sha:
                return trace(c['source'], sha, seen)
        if candidates:
            return None, (f'{path} differs from every recorded copy into it '
                          f'({", ".join(sorted({c["source"] for c in candidates}))})'), None
        return None, f'{path}: no recorded compile, copy, ReadyToRun compile or package produced it', None

    if publish_dir is None:
        return
    if not publish_dir.is_dir():
        problems.add(f'publish folder {publish_rel} not found')
        return
    managed = 0
    for f in sorted(publish_dir.rglob('*')):
        if not f.is_file() or f.suffix.lower() not in ('.dll', '.exe'):
            continue
        data = f.read_bytes()
        if not is_managed(data):
            continue
        managed += 1
        rel = publish_rel.rstrip('/') + '/' + f.relative_to(publish_dir).as_posix()
        sha = hashlib.sha256(data).hexdigest()
        kind, detail, leaf = trace(rel, sha, frozenset())
        if kind is None:
            problems.add(f'publish: {rel}: does not trace to a judged compile or a restored package: {detail}')
        elif pathlib.PurePosixPath(leaf).name.lower() != f.name.lower():
            problems.add(f'publish: {rel}: its content is another assembly ({leaf}), copied over it under a different name')
        elif not kind.startswith('compiled') and f.name.lower() in repo_names:
            problems.add(f'publish: {rel}: has the name of a repo-built assembly but comes from {detail}, not its compile')
    if managed == 0:
        problems.add(f'publish: no managed assembly found in {publish_rel}')
    return managed


# ---------------------------------------------------------------------------------------------------------------------
# Entry points
# ---------------------------------------------------------------------------------------------------------------------
def check(facts_text, known, projects, build_label='build', publish_dir=None, publish_rel=None):
    problems = Problems()
    try:
        facts = json.loads(facts_text)
    except json.JSONDecodeError as e:
        problems.add(f'facts are not JSON: {e}')
        return problems.items
    if not structure(facts, problems):
        return problems.items
    leaks(facts_text, problems)
    compiler_calls(facts, problems, known, projects, build_label)
    tasks(facts, problems, known)
    reconcile(facts, problems, publish_dir, publish_rel)
    return problems.items


def load_known():
    return json.loads(KNOWN.read_text(encoding='utf-8'))


def cmd_check(argv):
    if not argv:
        print('check needs FACTS', file=sys.stderr)
        return 2
    facts_path = pathlib.Path(argv[0])
    solution = None
    projects = []
    publish = None
    build_label = 'build'
    repo = root
    i = 1
    while i < len(argv):
        if argv[i] == '--solution':
            solution = argv[i + 1]
        elif argv[i] == '--project':
            projects.append(argv[i + 1])
        elif argv[i] == '--publish-dir':
            publish = argv[i + 1]
        elif argv[i] == '--build-label':
            build_label = argv[i + 1]
        elif argv[i] == '--repo':
            repo = pathlib.Path(argv[i + 1]).resolve()
        else:
            print(f'unknown option {argv[i]}', file=sys.stderr)
            return 2
        i += 2
    if solution:
        sln = (repo / solution).resolve()
        text = sln.read_text(encoding='utf-8-sig')
        found = re.findall(r'"([^"]+\.csproj)"', text) + re.findall(r'Path="([^"]+\.csproj)"', text)
        projects += [(sln.parent / p.replace('\\', '/')).resolve().relative_to(repo).as_posix() for p in found]
    projects = sorted(set(projects))
    if not projects:
        print('check needs --solution or --project', file=sys.stderr)
        return 2
    try:
        text = facts_path.read_text(encoding='utf-8')
    except OSError as e:
        print(f'cannot read facts: {e}', file=sys.stderr)
        return 2
    publish_dir = (repo / publish).resolve() if publish else None
    problems = check(text, load_known(), projects, build_label, publish_dir, publish)
    if problems:
        print(f'BUILD FACTS: FAIL ({len(problems)} problem(s)); rules: {RULES}')
        for p in problems:
            print(f'  - {p}')
        return 1
    facts = json.loads(text)
    calls = sum(len(u['csc']) for b in facts['binlogs'] for u in b['units'])
    print(f'BUILD FACTS: OK ({len(facts["binlogs"])} binary logs, {len(projects)} solution projects compiled, '
          f'{calls} Csc calls, every managed assembly in {publish or "(no publish folder)"} traced)')
    return 0


def cmd_learn(argv):
    facts = json.loads(pathlib.Path(argv[0]).read_text(encoding='utf-8'))
    known = load_known() if KNOWN.exists() else {'tasks': [], 'cscSwitches': []}
    new_tasks = sorted({t['task'] for b in facts['binlogs'] for t in b['tasks']} - set(known['tasks']))
    new_switches = sorted({s for b in facts['binlogs'] for u in b['units'] for c in u['csc'] for s in c['switches']}
                          - set(known['cscSwitches']) - {'@'})
    for t in new_tasks:
        print(f'task    {t}')
    for s in new_switches:
        print(f'switch  {s}')
    if '--write' in argv:
        known['tasks'] = sorted(set(known['tasks']) | set(new_tasks))
        known['cscSwitches'] = sorted(set(known['cscSwitches']) | set(new_switches))
        KNOWN.write_text(json.dumps(known, indent=2, sort_keys=True) + '\n', encoding='utf-8', newline='\n')
        print(f'wrote {KNOWN.relative_to(root).as_posix()}')
    return 0


# ---------------------------------------------------------------------------------------------------------------------
# Self-test: planted facts and a planted publish folder
# ---------------------------------------------------------------------------------------------------------------------
def fake_pe(managed, payload):
    data = bytearray(512)
    data[0:2] = b'MZ'
    struct.pack_into('<I', data, 0x3C, 0x80)
    data[0x80:0x84] = b'PE\0\0'
    opt = 0x80 + 24
    struct.pack_into('<H', data, opt, 0x20B)
    struct.pack_into('<I', data, opt + 108, 16)
    if managed:
        struct.pack_into('<II', data, opt + 112 + 14 * 8, 0x2000, 0x48)
    return bytes(data) + payload


def planted(tmp):
    pub = tmp / 'artifacts' / 'ci'
    pub.mkdir(parents=True)
    app = fake_pe(True, b'app')
    lib = fake_pe(True, b'pkg')
    r2r_in = fake_pe(True, b'core')
    r2r_out = fake_pe(True, b'core-r2r')
    native = fake_pe(False, b'native')
    (pub / 'App.dll').write_bytes(app)
    (pub / 'Pkg.dll').write_bytes(lib)
    (pub / 'Core.dll').write_bytes(r2r_out)
    (pub / 'native.dll').write_bytes(native)
    h = lambda b: hashlib.sha256(b).hexdigest()
    csc = lambda out, sha: {
        'target': 'CoreCompile', 'targetFile': '<dotnet>/sdk/10.0.403/Roslyn/Microsoft.CSharp.Core.targets',
        'taskAssembly': '<dotnet>/sdk/10.0.403/Roslyn/Microsoft.Build.Tasks.CodeAnalysis.dll',
        'tool': ['<dotnet>/dotnet.exe', 'exec', '<dotnet>/sdk/10.0.403/Roslyn/bincore/csc.dll'],
        'arguments': ['/noconfig', '/out:' + out], 'switches': ['/noconfig', '/out'], 'responseFiles': [],
        'noConfig': True, 'analyzerConfigs': [], 'analyzers': [], 'additionalFiles': [], 'sources': ['src/A.cs'],
        'nowarn': [], 'warnaserror': [], 'ruleset': None, 'errorLog': None,
        'roslyn': {'errors': [], 'generalDiagnosticOption': 'Default', 'skipAnalyzers': False,
                   'specificDiagnosticOptions': {}, 'warningLevel': 10},
        'references': [], 'output': {'path': out, 'sha256': sha}, 'refOutput': None}
    unit = lambda project, calls, libs=(): {
        'project': project, 'targetFramework': 'net10.0', 'runtimeIdentifier': '', 'configuration': 'Release',
        'globalProperties': [], 'properties': {}, 'imports': [], 'importsSkipped': [], 'packageReferences': [],
        'packageDownloads': [], 'projectReferenceItems': [], 'projectReferencesFollowed': [], 'referenceItems': [],
        'frameworkReferences': [], 'editorConfigItems': [], 'assets': {'path': 'x', 'libraries': list(libs)},
        'targetsWithTasks': ['CoreCompile'], 'csc': calls}
    copy = lambda src, dst, sha: {'source': src, 'destination': dst, 'target': '_CopyResolvedFilesToPublishPreserveNewest',
                                  'targetFile': '<dotnet>/sdk/10.0.403/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Publish.targets',
                                  'package': None, 'sourceSha256': sha}
    task = 'Csc | CoreCompile | <dotnet>/sdk/10.0.403/Roslyn/Microsoft.CSharp.Core.targets | <dotnet>/sdk/10.0.403/Roslyn/Microsoft.Build.Tasks.CodeAnalysis.dll'
    facts = {'schema': SCHEMA, 'reader': {}, 'binlogs': [
        {'label': 'build', 'succeeded': True, 'msbuildVersion': '18', 'commandLine': 'x', 'runtimePacks': [],
         'units': [unit('src/App/App.csproj', [csc('src/App/obj/App.dll', h(app))]),
                   unit('src/Core/Core.csproj', [csc('src/Core/obj/Core.dll', h(r2r_in))], ['Pkg/1.0.0 (package)'])],
         'tasks': [{'task': task, 'count': 2}], 'copies': [copy('src/Core/obj/Core.dll', 'src/Core/bin/Core.dll', h(r2r_in))],
         'readyToRun': []},
        {'label': 'publish', 'succeeded': True, 'msbuildVersion': '18', 'commandLine': 'x',
         'runtimePacks': ['Microsoft.NETCore.App.Runtime.win-x64/10.0.14'], 'units': [],
         'tasks': [{'task': task, 'count': 1}],
         'copies': [copy('src/App/obj/App.dll', 'artifacts/ci/App.dll', h(app)),
                    copy('<nuget>/pkg/1.0.0/lib/net10.0/Pkg.dll', 'artifacts/ci/Pkg.dll', h(lib)),
                    copy('src/App/obj/R2R/Core.dll', 'artifacts/ci/Core.dll', h(r2r_out))],
         'readyToRun': [{'input': 'src/Core/bin/Core.dll', 'inputSha256': h(r2r_in), 'output': 'src/App/obj/R2R/Core.dll',
                         'outputSha256': h(r2r_out), 'package': None, 'compiler': '<nuget>/crossgen2/tools/crossgen2.exe'}]}]}
    known = {'tasks': [task], 'cscSwitches': ['/noconfig', '/out']}
    return facts, known, pub


def self_test():
    failures = []
    with tempfile.TemporaryDirectory() as d:
        tmp = pathlib.Path(d)
        facts, known, pub = planted(tmp)
        projects = ['src/App/App.csproj', 'src/Core/Core.csproj']

        def run(f, k=known, extra=None):
            if extra:
                extra()
            return check(json.dumps(f), k, projects, 'build', pub, 'artifacts/ci')

        base = run(facts)
        if base:
            failures.append(f'clean planted facts must pass, got {base}')

        def case(name, expect, mutate=None, known_mutate=None, fs=None):
            f = json.loads(json.dumps(facts))
            k = json.loads(json.dumps(known))
            backup = {p: p.read_bytes() for p in pub.iterdir()}
            if mutate:
                mutate(f)
            if known_mutate:
                known_mutate(k)
            if fs:
                fs()
            got = check(json.dumps(f), k, projects, 'build', pub, 'artifacts/ci')
            for p in list(pub.iterdir()):
                if p not in backup:
                    p.unlink()
            for p, b in backup.items():
                p.write_bytes(b)
            if not any(expect in g for g in got):
                failures.append(f'{name}: expected a problem containing {expect!r}, got {got}')
            else:
                print(f'  ok  {name}')

        def unit(f, project):
            return next(u for b in f['binlogs'] for u in b['units'] if u['project'] == project)

        case('wrong schema', 'schema', lambda f: f.update(schema='x'))
        case('missing field', 'missing field "csc"', lambda f: unit(f, 'src/App/App.csproj').pop('csc'))
        case('failed build', 'the build failed', lambda f: f['binlogs'][0].update(succeeded=False))
        case('machine path', 'machine path', lambda f: f['binlogs'][0].update(commandLine='C:\\Users\\someone\\x'))
        case('project missing', 'src/Core/Core.csproj: no Csc call',
             lambda f: unit(f, 'src/Core/Core.csproj').update(csc=[]))
        case('csc output without hash', 'has no hash',
             lambda f: unit(f, 'src/App/App.csproj')['csc'][0]['output'].update(sha256=None))
        case('compiler outside the SDK', 'not from the SDK install',
             lambda f: unit(f, 'src/App/App.csproj')['csc'][0].update(tool=['tools/csc.exe']))
        case('Csc task class from elsewhere', "Csc task class is not the SDK's",
             lambda f: unit(f, 'src/App/App.csproj')['csc'][0].update(taskAssembly='<nuget>/evil/1.0.0/tasks/Evil.dll'))
        case('response file', 'response file passed to the compiler',
             lambda f: unit(f, 'src/App/App.csproj')['csc'][0].update(
                 responseFiles=[{'path': 'src/App/x.rsp', 'content': '/analyzerconfig:q.globalconfig'}],
                 arguments=['@src/App/x.rsp', '/analyzerconfig:q.globalconfig']))
        case('unknown switch', 'unknown compiler switch /ruleset',
             lambda f: unit(f, 'src/App/App.csproj')['csc'][0]['switches'].append('/ruleset'))
        case('command line error', 'does not parse',
             lambda f: unit(f, 'src/App/App.csproj')['csc'][0]['roslyn']['errors'].append('error CS2007'))
        case('unknown task', 'unknown task ran: Exec | Tamper',
             lambda f: f['binlogs'][1]['tasks'].append({'task': 'Exec | Tamper | src/App/App.csproj | <dotnet>/x', 'count': 1}))
        case('published assembly changed after compile', 'differs from every recorded copy',
             fs=lambda: (pub / 'App.dll').write_bytes(fake_pe(True, b'tampered')))
        case('compile output changed (hash differs from recorded)', 'differs from what the compiler wrote',
             lambda f: f['binlogs'][1]['copies'][0].update(sourceSha256=hashlib.sha256((pub / 'App.dll').read_bytes()).hexdigest())
             or unit(f, 'src/App/App.csproj')['csc'][0]['output'].update(sha256='0' * 64))
        case('unknown managed file in publish', 'no recorded compile, copy',
             fs=lambda: (pub / 'Extra.dll').write_bytes(fake_pe(True, b'extra')))
        case('package not restored', 'which no restore or runtime pack lists',
             lambda f: unit(f, 'src/Core/Core.csproj')['assets'].update(libraries=[]))
        case('ReadyToRun output changed', "differs from the ReadyToRun compiler's output",
             lambda f: f['binlogs'][1]['readyToRun'][0].update(outputSha256='1' * 64))
        case('repo name from a package', 'has the name of a repo-built assembly',
             lambda f: unit(f, 'src/App/App.csproj')['csc'][0]['output'].update(path='src/App/obj/Pkg.dll')
             or f['binlogs'][1]['copies'][0].update(source='src/App/obj/Pkg.dll'))
        case('a different repo assembly copied over a published one', 'copied over it under a different name',
             lambda f: f['binlogs'][1]['copies'].append({**f['binlogs'][1]['copies'][0], 'source': 'src/App/obj/App.dll',
                                                         'destination': 'artifacts/ci/Pkg.dll'}),
             fs=lambda: (pub / 'Pkg.dll').write_bytes((pub / 'App.dll').read_bytes()))
        case('no managed file at all', 'no managed assembly found',
             fs=lambda: [p.unlink() for p in pub.iterdir() if p.name != 'native.dll'])
        if not is_managed(fake_pe(True, b'')) or is_managed(fake_pe(False, b'')) or is_managed(b'MZ'):
            failures.append('managed-PE detection is wrong')
    if failures:
        print('SELF-TEST: FAIL')
        for f in failures:
            print(f'  - {f}')
        return 1
    print('SELF-TEST: OK (every planted failure was caught; the clean planted facts pass)')
    return 0


# ---------------------------------------------------------------------------------------------------------------------
# Fixtures: build each must-fail fixture for real and show the bypass in its facts
# ---------------------------------------------------------------------------------------------------------------------
def walk(node, parts):
    if not parts:
        if isinstance(node, list):
            for x in node:
                yield from walk(x, [])
        elif node is not None:
            yield node if isinstance(node, str) else json.dumps(node, sort_keys=True)
        return
    if isinstance(node, list):
        for x in node:
            yield from walk(x, parts)
    elif isinstance(node, dict) and parts[0] in node:
        yield from walk(node[parts[0]], parts[1:])


def cmd_fixtures(argv):
    extractor = None
    dotnet = 'dotnet'
    work = None
    only = None
    i = 0
    while i < len(argv):
        if argv[i] == '--extractor':
            extractor = argv[i + 1]
        elif argv[i] == '--dotnet':
            dotnet = argv[i + 1]
        elif argv[i] == '--work':
            work = pathlib.Path(argv[i + 1]).resolve()
        elif argv[i] == '--only':
            only = argv[i + 1]
        else:
            print(f'unknown option {argv[i]}', file=sys.stderr)
            return 2
        i += 2
    if not extractor or not work:
        print('fixtures needs --extractor and --work', file=sys.stderr)
        return 2
    extractor = str(pathlib.Path(extractor).resolve())    # the fixture builds run in their own folders
    known = load_known()
    failed = []
    for spec_file in sorted(FIXTURES.glob('*/fixture.json')):
        name = spec_file.parent.name
        if only and name != only:
            continue
        spec = json.loads(spec_file.read_text(encoding='utf-8'))
        print(f'== fixture {name}: {spec["bypass"]}')
        dest = work / name
        if dest.exists():
            shutil.rmtree(dest)
        shutil.copytree(spec_file.parent, dest)
        shutil.copy(root / 'global.json', dest / 'global.json')
        logs = []
        ok = True
        for step in spec['builds']:
            binlog = dest / f'{step["label"]}.binlog'
            run = subprocess.run([dotnet, *step['args'], f'-bl:{binlog}'], cwd=dest, capture_output=True, text=True)
            if run.returncode != 0:
                print(f'  build step {step["label"]} failed:\n' + (run.stdout + run.stderr)[-3000:])
                ok = False
                break
            logs.append(f'{step["label"]}={binlog}')
        if not ok:
            failed.append(f'{name}: build failed')
            continue
        facts_file = dest / 'build-facts.json'
        run = subprocess.run([extractor, '--repo', str(dest), '--out', str(facts_file), *logs], capture_output=True, text=True)
        if run.returncode != 0:
            print('  extractor failed:\n' + (run.stdout + run.stderr)[-3000:])
            failed.append(f'{name}: extractor failed')
            continue
        text = facts_file.read_text(encoding='utf-8')
        facts = json.loads(text)
        for expect in spec['visible']:
            b = next((x for x in facts['binlogs'] if x['label'] == expect['label']), None)
            scope = b
            if b is not None and 'project' in expect:
                scope = [u for u in b['units'] if u['project'] == expect['project']]
            hits = [v for v in walk(scope, expect['field'].split('.')) if expect['contains'] in v] if scope else []
            if hits:
                quote = hits[0]
                at = quote.find(expect['contains'])
                quote = quote[max(0, at - 80): at + len(expect['contains']) + 80].replace('\r', '').replace('\n', '\\n')
                print(f'  VISIBLE  [{expect["label"]}] {expect.get("project", "(whole build)")} {expect["field"]}: ...{quote}...')
            else:
                print(f'  MISSING  [{expect["label"]}] {expect.get("project", "")} {expect["field"]} should contain {expect["contains"]!r}')
                failed.append(f'{name}: facts do not show {expect["contains"]!r} in {expect["field"]}')
        publish = spec.get('publishDir')
        problems = check(text, known, sorted(spec['projects']), 'build', (dest / publish) if publish else None, publish)
        want = spec['check']
        if want['result'] == 'fail':
            missing = [s for s in want['mustInclude'] if not any(s in p for p in problems)]
            for p in problems:
                if any(s in p for s in want['mustInclude']):
                    print(f'  CHECK FAILS AS REQUIRED: {p}')
            if missing:
                failed.append(f'{name}: check did not report {missing}; got {problems}')
        else:
            print(f'  check: {"OK" if not problems else "fails (" + str(len(problems)) + "): " + problems[0]}'
                  f'  (this PR only proves the facts capture it; the judge comes later)')
    if failed:
        print('FIXTURES: FAIL')
        for f in failed:
            print(f'  - {f}')
        return 1
    print('FIXTURES: OK (every bypass is visible in the facts; every tamper fixture fails the check)')
    return 0


def main(argv):
    if argv[:1] == ['--self-test']:
        return self_test()
    if argv[:1] == ['check']:
        return cmd_check(argv[1:])
    if argv[:1] == ['fixtures']:
        return cmd_fixtures(argv[1:])
    if argv[:1] == ['learn'] and len(argv) > 1:
        return cmd_learn(argv[1:])
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
