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
               'tasks': list, 'copies': list, 'writes': list, 'readyToRun': list, 'runtimePacks': list}
UNIT_KEYS = {'project': str, 'targetFramework': str, 'runtimeIdentifier': str, 'configuration': str,
             'globalProperties': list, 'properties': dict, 'imports': list, 'importsSkipped': list,
             'packageReferences': list, 'packageDownloads': list, 'projectReferenceItems': list,
             'projectReferencesFollowed': list, 'referenceItems': list, 'frameworkReferences': list,
             'editorConfigItems': list, 'assets': (dict, type(None)), 'targetsWithTasks': list, 'csc': list}
CSC_KEYS = {'target': (str, type(None)), 'targetFile': str, 'taskAssembly': str, 'tool': list, 'arguments': list,
            'switches': list, 'responseFiles': list, 'noConfig': bool, 'analyzerConfigs': list, 'analyzers': list,
            'additionalFiles': list, 'sources': list, 'nowarn': list, 'warnaserror': list,
            'ruleset': (str, type(None)), 'errorLog': (dict, type(None)), 'roslyn': dict, 'references': list,
            'output': dict, 'refOutput': (dict, type(None)), 'replay': (dict, type(None))}
WRITE_KEYS = {'file': str, 'task': str, 'target': (str, type(None)), 'targetFile': str, 'project': str}
COPY_KEYS = {'source': str, 'destination': str, 'target': (str, type(None)), 'targetFile': str,
             'package': (str, type(None)), 'sourceSha256': (str, type(None))}
R2R_KEYS = {'input': str, 'inputSha256': (str, type(None)), 'output': str, 'outputSha256': (str, type(None)),
            'package': (str, type(None)), 'compiler': str}
SDK_CSC = re.compile(r'^<dotnet>/sdk/[0-9][^/]*/Roslyn/bincore/csc\.(?:dll|exe)$')
HOST_ARGS = {'<dotnet>/dotnet.exe', '<dotnet>/dotnet', 'exec'}
# On each string value (not the JSON text): a drive-letter path, a UNC path, or a Unix home folder.
# Any absolute-path shape (the same shapes as the reader's PathTokens guard): a drive letter followed by a backslash or
# a single slash, even glued after a letter or digit (also inside \\?\C:\; a URL's scheme:// is not one); a URL-encoded
# drive (C%3A%5C, C%3A%2F, C%3A/); and after any delimiter a UNC path (\\srv\, \\?\UNC\), //host/ (not a URL's
# scheme://), a POSIX root (/home, /users, /root, /opt, /tmp, any case), an msys drive (/c/); for these a path character
# right before means a relative path. A drive-relative C:Users is not refused.
NOT_IN_PATH = r'(?<![A-Za-z0-9_.~$>)\]\\/-])'
LEAKS = [re.compile(r'[A-Za-z]:(?:\\|/(?!/))'),
         re.compile(r'[A-Za-z]%3[Aa](?:%5[Cc]|%2[Ff]|[\\/])'),
         re.compile(NOT_IN_PATH + r'\\\\[^\\/\s"\'<>|]+[\\/]'),
         re.compile(NOT_IN_PATH + r'/(?:home|users|root|opt|tmp)/[^/\s"\']', re.IGNORECASE),
         re.compile(NOT_IN_PATH + r'/[A-Za-z]/[^/\s"\']')]
SLASH_SLASH = re.compile(NOT_IN_PATH + r'//[A-Za-z0-9._$?-]+/')
URL_SCHEME = re.compile(r'(?:^|[\s"\'(\[<=,;])[A-Za-z][A-Za-z0-9+.-]*:$')
# WPF's temporary markup-compile project: <dir>/<Name>_wpftmp.csproj (the reader removes its random part).
WPF_TEMP = re.compile(r'^(?P<dir>.+)/(?P<name>[^/]+)_wpftmp\.csproj$')


def absolute_path(text):
    """The first absolute-path shape in text, or None."""
    for regex in LEAKS:
        m = regex.search(text)
        if m:
            return m
    for m in SLASH_SLASH.finditer(text):
        if not URL_SCHEME.search(text[:m.start()]):
            return m
    return None


def wpf_temp_of(project, projects):
    """The solution project a WPF temporary project belongs to, or None. Only <dir>/<Name>_wpftmp.csproj next to a
    solution project <dir>/<Name>.csproj counts; any other *_wpftmp.csproj is an ordinary project."""
    m = WPF_TEMP.match(project)
    if not m:
        return None
    owner = f'{m.group("dir")}/{m.group("name")}.csproj'
    return owner if owner in projects else None


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
        for w in b['writes']:
            ok = typed(problems, f'{label}: write', w, WRITE_KEYS) and ok
        for r in b['readyToRun']:
            ok = typed(problems, f'{label}: ReadyToRun compile', r, R2R_KEYS) and ok
        for t in b['tasks']:
            if not (isinstance(t, dict) and isinstance(t.get('task'), str) and isinstance(t.get('count'), int)):
                problems.add(f'{label}: malformed task entry')
                ok = False
    return ok


def leaks(facts, problems):
    """Every string (and key) in the facts, by its JSON path; the reader refuses these too, this is the second lock."""
    names = {n for n in (os.environ.get('USERNAME', ''), os.environ.get('USER', '')) if len(n) >= 3}
    found = []

    def visit(node, where):
        if isinstance(node, dict):
            for k, v in node.items():
                visit_text(k, f'{where}.<key {k}>')
                visit(v, f'{where}.{k}')
        elif isinstance(node, list):
            for i, v in enumerate(node):
                visit(v, f'{where}[{i}]')
        elif isinstance(node, str):
            visit_text(node, where)

    def visit_text(text, where):
        m = absolute_path(text)
        if m:
            start = max(0, m.start() - 50)
            found.append(f'machine path left in the facts at {where}: ...{text[start:start + 110]!r}...')
            return
        for n in names:
            if n.lower() in text.lower():
                found.append(f'the user name ({len(n)} letters) is in the facts at {where}')

    visit(facts, '$')
    for f in found[:40]:
        problems.add(f)
    if len(found) > 40:
        problems.add(f'... and {len(found) - 40} more machine paths')


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
        names = {}                                    # solution project -> its compile output file names
        for b in facts['binlogs']:
            for u in b['units']:
                if u['project'] in projects:
                    names.setdefault(u['project'], set()).update(
                        pathlib.PurePosixPath(c['output']['path']).name.lower() for c in u['csc'])
        for b in facts['binlogs']:
            for u in b['units']:
                if not u['csc'] or u['project'] in projects:
                    continue
                owner = wpf_temp_of(u['project'], projects)
                outs = {pathlib.PurePosixPath(c['output']['path']).name.lower() for c in u['csc']}
                if owner is None or not outs <= names.get(owner, set()):
                    problems.add(f'binlog {b["label"]}: {u["project"]} was compiled but is not a project of the solution '
                                 f'(only WPF\'s <dir>/<Name>_wpftmp.csproj of a solution project <dir>/<Name>.csproj, '
                                 f'building that project\'s own assembly, may be): its output could stand in for a '
                                 f'judged one')
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


def written_outputs(facts, problems, projects):
    """A file a judge trusts may only be written by the task that produced it: no recorded write (Copy, Move, Delete,
    Touch, WriteLinesToFile, GenerateDepsFile, ...) may target any compile's output, reference output or error log, in
    either log. The one SDK exception is WPF deleting its own temporary markup assembly (target
    CleanupTemporaryTargetAssembly in the SDK's Microsoft.WinFX.targets, on a <Name>_wpftmp compile's output)."""
    judged, wpf_outputs = {}, set()
    for b in facts['binlogs']:
        for u in b['units']:
            twin = wpf_temp_of(u['project'], projects) is not None
            for c in u['csc']:
                paths = [c['output'].get('path'), (c.get('refOutput') or {}).get('path'),
                         (c.get('errorLog') or {}).get('path')]
                for path in paths:
                    if isinstance(path, str):
                        judged.setdefault(path.lower(), f'{b["label"]}: {u["project"]}')
                if twin and isinstance(c['output'].get('path'), str):
                    wpf_outputs.add(c['output']['path'].lower())
    for b in facts['binlogs']:
        for w in b['writes']:
            key = w['file'].lower()
            if key not in judged:
                continue
            if (w['task'] == 'Delete' and w['target'] == 'CleanupTemporaryTargetAssembly'
                    and w['targetFile'].startswith('<dotnet>/sdk/') and w['targetFile'].endswith('/Microsoft.WinFX.targets')
                    and key in wpf_outputs):
                continue
            problems.add(f'binlog {b["label"]}: {w["task"]} (target {w["target"]}, {w["targetFile"]}) writes {w["file"]}, '
                         f'which is a compile output or error log ({judged[key]}); only its own compiler may write it')


def replays(facts, problems, projects):
    """A replayed compile (BuildFacts --replay-under) must give the same assembly as the one on disk: otherwise the
    compile's inputs or output changed after it ran, or the build is not deterministic. WPF's temporary markup
    assembly is deleted by design, so it is not compared."""
    for b in facts['binlogs']:
        for u in b['units']:
            if wpf_temp_of(u['project'], projects) is not None:
                continue
            for c in u['csc']:
                r = c.get('replay')
                if r is None or r.get('status') != 'replayed':
                    continue
                if r.get('exitCode') != 0 or r.get('outputSha256') != c['output'].get('sha256'):
                    problems.add(f'{b["label"]}: {u["project"]} ({u["runtimeIdentifier"]}) the replayed compile (exit '
                                 f'{r.get("exitCode")}) does not give the assembly on disk: an input or the output changed '
                                 f'after the compile, or the build is not deterministic')


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


def reconcile(facts, problems, publish_dir, publish_rel, projects=()):
    compiled = {}
    compiled_by = {}                                  # compile output path -> projects that wrote it
    owners = {}                                       # assembly file name -> solution projects that compile it
    copies_list = []
    copies = {}
    r2r = {}
    packages = set()
    for b in facts['binlogs']:
        for u in b['units']:
            for c in u['csc']:
                if c['output'].get('sha256'):
                    compiled.setdefault(c['output']['path'], set()).add(c['output']['sha256'])
                compiled_by.setdefault(c['output']['path'], set()).add(u['project'])
                if u['project'] in projects:
                    owners.setdefault(pathlib.PurePosixPath(c['output']['path']).name.lower(), set()).add(u['project'])
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
            copies_list.append(c)
        for r in b['readyToRun']:
            r2r[r['output']] = r
    repo_names = {pathlib.PurePosixPath(p).name.lower() for p in compiled}
    for name, who in sorted(owners.items()):
        if len(who) > 1:
            problems.add(f'two solution projects compile {name}: {", ".join(sorted(who))} (one owner per assembly name)')

    def owned_ok(name, leaf):
        """None when fine: a repo-built assembly name must come from the compile of the solution project owning it."""
        who = owners.get(name.lower())
        if not who:
            return None
        leaf_projects = compiled_by.get(leaf, set()) if leaf else set()
        if leaf_projects & who and pathlib.PurePosixPath(leaf).name.lower() == name.lower():
            return None
        source = ', '.join(sorted(leaf_projects)) or leaf or 'nothing traceable'
        return f'{name} belongs to {", ".join(sorted(who))} but this copy comes from {source}'

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

    # Every recorded copy INTO a file named like a solution project's assembly must come from that project's compile.
    for c in sorted(copies_list, key=lambda c: (c['destination'], c['source'])):
        name = pathlib.PurePosixPath(c['destination']).name
        if name.lower() not in owners or not c['destination'].lower().endswith(('.dll', '.exe')):
            continue
        if c['sourceSha256'] is None:
            problems.add(f'copy into {c["destination"]} ({c["target"]}): its source {c["source"]} is gone, so it cannot '
                         f'be traced to the compile that owns {name}')
            continue
        kind, detail, leaf = trace(c['source'], c['sourceSha256'], frozenset())
        why = detail if kind is None else owned_ok(name, leaf if kind.startswith('compiled') else None)
        if why:
            problems.add(f'copy into {c["destination"]} ({c["target"]}) from {c["source"]}: {why}')

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
        elif owned_ok(f.name, leaf if kind.startswith('compiled') else None):
            problems.add(f'publish: {rel}: {owned_ok(f.name, leaf if kind.startswith("compiled") else None)}')
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
    leaks(facts, problems)
    compiler_calls(facts, problems, known, projects, build_label)
    written_outputs(facts, problems, projects)
    replays(facts, problems, projects)
    tasks(facts, problems, known)
    reconcile(facts, problems, publish_dir, publish_rel, set(projects))
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
        if any('unknown task ran' in p or 'unknown compiler switch' in p for p in problems):
            # Make the candidate additions visible in the log, so a reviewer can judge them (never added blindly).
            print('Candidate known.json additions (python -I scripts/quality/build_facts.py learn FACTS; review each):')
            cmd_learn([str(facts_path)])
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
        'references': [], 'output': {'path': out, 'sha256': sha}, 'refOutput': None,
        'replay': {'status': 'replayed', 'exitCode': 0, 'outputSha256': sha, 'sarif': None}}
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
         'writes': [{'file': 'src/Core/bin/Core.dll', 'task': 'Copy', 'target': 'CopyFilesToOutputDirectory',
                     'targetFile': '<dotnet>/sdk/10.0.403/Microsoft.Common.CurrentVersion.targets',
                     'project': 'src/Core/Core.csproj'}],
         'readyToRun': []},
        {'label': 'publish', 'succeeded': True, 'msbuildVersion': '18', 'commandLine': 'x',
         'runtimePacks': ['Microsoft.NETCore.App.Runtime.win-x64/10.0.14'], 'units': [],
         'tasks': [{'task': task, 'count': 1}], 'writes': [],
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

        def case(name, expect, mutate=None, known_mutate=None, fs=None, proj=None):
            f = json.loads(json.dumps(facts))
            k = json.loads(json.dumps(known))
            backup = {p: p.read_bytes() for p in pub.iterdir()}
            if mutate:
                mutate(f)
            if known_mutate:
                known_mutate(k)
            if fs:
                fs()
            got = check(json.dumps(f), k, proj or projects, 'build', pub, 'artifacts/ci')
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
        case('repo name from a package', 'Pkg.dll belongs to src/App/App.csproj',
             lambda f: unit(f, 'src/App/App.csproj')['csc'][0]['output'].update(path='src/App/obj/Pkg.dll')
             or f['binlogs'][1]['copies'][0].update(source='src/App/obj/Pkg.dll'))
        case('a different repo assembly copied over a published one', 'copied over it under a different name',
             lambda f: f['binlogs'][1]['copies'].append({**f['binlogs'][1]['copies'][0], 'source': 'src/App/obj/App.dll',
                                                         'destination': 'artifacts/ci/Pkg.dll'}),
             fs=lambda: (pub / 'Pkg.dll').write_bytes((pub / 'App.dll').read_bytes()))
        def evil_unit(f, project, out, sha):
            u = json.loads(json.dumps(unit(f, 'src/App/App.csproj')))
            u['project'] = project
            u['csc'][0]['output'] = {'path': out, 'sha256': sha}
            f['binlogs'][0]['units'].append(u)
            return u

        evil = fake_pe(True, b'evil')
        h = lambda b: hashlib.sha256(b).hexdigest()

        case('runner machine path, named by its JSON path', 'at $.binlogs[0].units[0].imports[0]',
             lambda f: f['binlogs'][0]['units'][0]['imports'].append('C:\\Program Files\\dotnet\\sdk\\x.props'))
        case('runner temp path in a key', 'machine path left in the facts at $.binlogs[0].units[0].properties.<key',
             lambda f: f['binlogs'][0]['units'][0]['properties'].update({'D:\\a\\_temp\\x': 'y'}))
        case('compile of a project outside the solution', 'evil/Evil.csproj was compiled but is not a project of the solution',
             lambda f: evil_unit(f, 'evil/Evil.csproj', 'evil/obj/Lib.dll', h(evil)))
        case('same-name compile stands in for a solution project', 'App.dll belongs to src/App/App.csproj but this copy comes from evil/Evil.csproj',
             lambda f: (evil_unit(f, 'evil/Evil.csproj', 'evil/obj/App.dll', h(evil)),
                        f['binlogs'][1]['copies'].append({**f['binlogs'][1]['copies'][0], 'source': 'evil/obj/App.dll',
                                                  'sourceSha256': h(evil)})),
             fs=lambda: (pub / 'App.dll').write_bytes(evil))
        case('copy into a judged output from another chain', 'copy into src/Core/bin/Core.dll',
             lambda f: f['binlogs'][0]['copies'].append({**f['binlogs'][0]['copies'][0],
                                                         'source': '<nuget>/pkg/1.0.0/lib/net10.0/Pkg.dll',
                                                         'sourceSha256': h(fake_pe(True, b'pkg'))}))
        case('two solution projects compile one name', 'two solution projects compile app.dll',
             lambda f: evil_unit(f, 'src/App2/App2.csproj', 'src/App2/obj/App.dll', h(evil)),
             proj=['src/App/App.csproj', 'src/App2/App2.csproj', 'src/Core/Core.csproj'])
        case('a *_wpftmp.csproj that is not WPF\'s', 'extra/Extra_wpftmp.csproj was compiled but is not a project',
             lambda f: evil_unit(f, 'extra/Extra_wpftmp.csproj', 'extra/obj/Extra.dll', h(evil)))
        case('WPF temp project building another assembly name', 'src/App/App_wpftmp.csproj was compiled but is not',
             lambda f: evil_unit(f, 'src/App/App_wpftmp.csproj', 'src/App/obj/Other.dll', h(evil)))
        def write(f, file, task='WriteLinesToFile', target='Evil', target_file='src/Core/Core.csproj', log=0):
            f['binlogs'][log]['writes'].append({'file': file, 'task': task, 'target': target, 'targetFile': target_file,
                                                'project': 'src/Core/Core.csproj'})

        def with_errorlog(f):
            unit(f, 'src/Core/Core.csproj')['csc'][0]['errorLog'] = {'path': 'src/Core/obj/Core.rs0030.sarif', 'sarif': {}}

        case('GenerateDepsFile writes over an error log (review of PR #28: ProjectDepsFilePath)',
             'writes src/Core/obj/Core.rs0030.sarif, which is a compile output or error log',
             lambda f: (with_errorlog(f), write(f, 'src/Core/obj/Core.rs0030.sarif', 'GenerateDepsFile',
                                                'GenerateBuildDependencyFile', '<dotnet>/sdk/10.0.403/Sdks/x.targets')))
        case('publish GenerateDepsFile (PublishDepsFilePath) over an error log',
             'binlog publish: GenerateDepsFile', lambda f: (with_errorlog(f), write(
                 f, 'src/Core/obj/Core.rs0030.sarif', 'GenerateDepsFile', 'GeneratePublishDependencyFile',
                 '<dotnet>/sdk/10.0.403/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Publish.targets', log=1)))
        case('Copy into an error log', 'Copy (target Evil', lambda f: (with_errorlog(f), write(
            f, 'SRC/Core/obj/Core.rs0030.SARIF', 'Copy')))
        case('WriteLinesToFile over a compile output', 'writes src/App/obj/App.dll',
             lambda f: write(f, 'src/App/obj/App.dll'))
        case('Touch on a reference output', 'writes src/Core/obj/ref/Core.dll', lambda f: (
            unit(f, 'src/Core/Core.csproj')['csc'][0].update(refOutput={'path': 'src/Core/obj/ref/Core.dll', 'sha256': 'x'}),
            write(f, 'src/Core/obj/ref/Core.dll', 'Touch')))
        case('the WPF cleanup exception needs the SDK target file', 'writes src/App/obj/App.dll', lambda f: (
            evil_unit(f, 'src/App/App_wpftmp.csproj', 'src/App/obj/App.dll', unit(f, 'src/App/App.csproj')['csc'][0]['output']['sha256']),
            write(f, 'src/App/obj/App.dll', 'Delete', 'CleanupTemporaryTargetAssembly', 'src/App/App.csproj')))
        case('a replayed compile gives another assembly', 'does not give the assembly on disk',
             lambda f: unit(f, 'src/Core/Core.csproj')['csc'][0]['replay'].update(outputSha256='0' * 64))
        case('a replayed compile fails', 'the replayed compile (exit 1)',
             lambda f: unit(f, 'src/Core/Core.csproj')['csc'][0]['replay'].update(exitCode=1))
        wpf = json.loads(json.dumps(facts))
        evil_unit(wpf, 'src/App/App_wpftmp.csproj', 'src/App/obj/App.dll', unit(wpf, 'src/App/App.csproj')['csc'][0]['output']['sha256'])
        write(wpf, 'src/App/obj/App.dll', 'Delete', 'CleanupTemporaryTargetAssembly',
              '<dotnet>/sdk/10.0.403/Sdks/Microsoft.NET.Sdk.WindowsDesktop/targets/Microsoft.WinFX.targets')
        wpf['binlogs'][0]['units'][-1]['csc'][0]['replay'] = {'status': 'replayed', 'exitCode': 0, 'outputSha256': 'other',
                                                              'sarif': None}
        wpf_problems = check(json.dumps(wpf), known, projects, 'build', pub, 'artifacts/ci')
        if wpf_problems:
            failures.append(f'WPF\'s own temp project must pass, got {wpf_problems}')
        else:
            print('  ok  WPF\'s own <Name>_wpftmp.csproj passes')
        for shape in [r'/reference:\\srv\share\a.dll', r'@\\srv\x', r';\\srv\x', r',\\srv\x', r'[\\srv\x',
                      '//srv/share/x', r'\\?\UNC\srv\share\x', r'\\?\C:\x', '/c/Users/someone/x', ';/home/bob/x',
                      '/users/bob/x', '/root/x', 'x=/Users/bob/y', '/reference://srv/share/a.dll', 'D:/a/b',
                      r'/reference:libC:\x\a.dll', r'abc1D:\x', 'file:///C:/x/a.cs', 'C%3A%5Cx', 'file:///c%3A/x',
                      'C%3a%2fx', '/opt/x/y', 'x=/tmp/y', r'"C:\\Users\\x"']:
            case(f'machine path shape {shape}', 'machine path left in the facts at $.binlogs[0].commandLine',
                 lambda f, shape=shape: f['binlogs'][0].update(commandLine='msbuild ' + shape))
        for fine in ['src/Boo.App/users/x.cs', r'<dotnet>/sdk\10.0.403\\Microsoft\x', 'https://github.com/x/y',
                     '<home>/AppData/x', '/noconfig', '/out:obj/Release/Boo.dll', '<repo-parent-1>/c/x', 'tests/root/x',
                     'a//b/c', 'file:///src/Boo.Core/x.cs', 'http://example.com/a', 'src/opt/x', 'a/tmp/b',
                     'x:Class="A"', 'C:Users', 'time 12:30/45']:
            if absolute_path(fine):
                failures.append(f'leak guard refused a relative path or URL: {fine}')
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
