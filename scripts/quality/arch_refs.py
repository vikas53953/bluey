"""Architecture references: every src/ project may reference only the projects, packages and frameworks that
docs/architecture/layers.json allows. Never reads secrets.

  python -I scripts/quality/arch_refs.py                CI runs this: the tree against docs/architecture/layers.json
  python -I scripts/quality/arch_refs.py --self-test    prove the judging logic and the MSBuild reader (CI runs this)
  python -I scripts/quality/arch_refs.py --show         print the reference graph MSBuild reports, project by project
  python -I <base copy of arch_refs.py> --root . --layers <base layers.json> --rise-only --pr-layers <PR layers.json>
                                                       CI on a pull request: the BASE branch's own script and layers.json
                                                       judge the PR's tree, and refuse a PR layers.json that allows more
                                                       than the base one, so editing the PR's copies changes nothing.
  --dotnet PATH                                        the dotnet to run (default: `dotnet` on PATH)

The references are read by MSBuild itself (`dotnet msbuild <project> -getItem:... -getProperty:...`, evaluation only,
no restore, no build), not by parsing the .csproj text, so Directory.Build.props/.targets, <Import>s, conditions,
Directory.Packages.props and per-target-framework items are all seen exactly as the build sees them. A project with
several target frameworks is evaluated once per framework. When `dotnet` cannot be run the check FAILS; there is no
weaker text fallback, because a text reader would miss exactly the build-file routes this check exists to close.

Exit 1 on any finding. Each finding prints what broke, why it matters, the root-cause fix, the wrong fix to avoid,
and the rule's anchor in docs/quality/RULES.md (BOO-REF-001..008). Known gaps are listed there.
"""
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor

root = pathlib.Path(__file__).resolve().parent.parent.parent
LAYERS = root / 'docs' / 'architecture' / 'layers.json'
RULES_DOC = 'docs/quality/RULES.md'
PROJECT_FILE = re.compile(r'\.\w*proj$', re.IGNORECASE)  # .csproj, .CSPROJ, .vbproj, .fsproj, .shproj, .proj, ...
ITEMS = ('ProjectReference', 'PackageReference', 'FrameworkReference', 'Reference', 'COMReference', 'Analyzer')
PROJECT_KEYS = {'projects', 'packages', 'framework_references', 'target_frameworks'}
TOP_KEYS = {'about', 'rules', 'all_src_projects', 'projects'}


class Rule:
    def __init__(self, rule_id, title, why, fix, dont):
        self.id, self.title, self.why, self.fix, self.dont = rule_id, title, why, fix, dont


RULES = {rule.id: rule for rule in [
    Rule('BOO-REF-001', 'a project reference that layers.json does not allow',
         'Layers keep each part testable and replaceable. One reference added "to reuse a helper" ties two layers '
         'together for good; the next change builds on it and the layers stop meaning anything.',
         'Move the shared code down into a project both may reference (usually Boo.Core, as plain values or an '
         'interface), or do the wiring in Boo.App, which may reference every layer.',
         'Do NOT add the edge to docs/architecture/layers.json to make this pass: a new edge is an architecture '
         'decision for the owner, and the base branch\'s copy of this check refuses it anyway.'),
    Rule('BOO-REF-002', 'a project reference that leaves src/',
         'tools/ and tests/ are not shipped and not judged (tests/ is the frozen oracle). Product code that depends on '
         'them ships tool or test code, and breaks when they change.',
         'Move the code the product needs into a src/ project (as layers.json allows), or turn the dependency '
         'around: the tool or test references the product.',
         'Do NOT copy the project into src/ under a new name, and do NOT link its files in with <Compile Include>.'),
    Rule('BOO-REF-003', 'a package, assembly or analyzer that layers.json does not allow',
         'Every package is code Boo ships, updates and trusts. A package in the wrong layer (an HTTP, Windows or '
         'database library in Core) breaks the layer from below without any project reference.',
         'Use what the layer already has, or put the code that needs the package in the layer that owns that kind of '
         'work (Storage: database and encryption, Windows: OS integration, Providers: network and AI, App: installer).',
         'Do NOT reference the DLL directly (<Reference>, <Analyzer>) or add the package from Directory.Build.props: '
         'MSBuild reports those too.'),
    Rule('BOO-REF-004', 'a src/ project that layers.json does not declare, or in the wrong place',
         'A project nobody declared has no rules, so anything it references would pass.',
         'Put it at src/<Name>/<Name>.csproj and add a row for it to docs/architecture/layers.json with the smallest '
         'set of references it needs, in the same PR; the owner approves a new layer (the base branch\'s copy fails '
         'until the row exists on the base).',
         'Do NOT put product code in tools/ or tests/ and reference it from src/ (BOO-REF-002).'),
    Rule('BOO-REF-005', 'a target framework or framework reference that layers.json does not allow',
         'Boo.Core and Boo.Providers target plain net10.0 so they can never use WPF, WinForms or Windows APIs; that '
         'keeps them testable anywhere. A framework reference (WPF, WinForms, Windows SDK) is a dependency like any '
         'other.',
         'Keep Windows-only code in Boo.Windows or Boo.App and hand the result to Core as plain values.',
         'Do NOT add -windows to the target framework, or a <FrameworkReference>, to reach one Windows type.'),
    Rule('BOO-REF-006', 'docs/architecture/layers.json is not valid',
         'The check can only be as good as its rule file: a cycle means there are no layers, and an allowance '
         'naming a project that does not exist is a typo waiting to pass something else.',
         'Fix the file: known keys only, lists of names, no project allowed to reference itself, no cycle.',
         'Do NOT delete the rule file or a project row to silence this.'),
    Rule('BOO-REF-007', 'layers.json allows something the tree no longer has (good news)',
         'The file must say what is true today; an unused allowance is a free pass for the next change.',
         'Remove the unused entry from docs/architecture/layers.json in this PR, so the gain is locked in.',
         'Do NOT add a dummy reference to keep the allowance.'),
    Rule('BOO-REF-008', 'the PR\'s layers.json allows more than the base branch\'s',
         'A PR that loosens its own rule file could pass with any reference; loosening it in one PR and using it in '
         'the next would pass both. So the base branch\'s copy refuses any loosening.',
         'If the new reference, package, framework or project is really wanted, the owner approves it and overrides '
         'this check once, with the reason in the PR (see docs/quality/RULES.md, Changing the layers).',
         'Do NOT split the loosening and its use into two PRs to get past this.'),
]}


class Finding:
    def __init__(self, rule_id, subject, what, where=''):
        self.rule_id, self.subject, self.what, self.where = rule_id, subject, what, where

    def text(self):
        rule = RULES[self.rule_id]
        lines = [f'{self.rule_id}  {self.subject}  ({rule.title})', f'  What:   {self.what}', f'  Why:    {rule.why}',
                 f'  Fix:    {rule.fix}', f'  Do NOT: {rule.dont[len("Do NOT "):]}',
                 f'  Rule:   {RULES_DOC}#{self.rule_id.lower()}']
        if self.where:
            lines.append(f'  Where:  {self.where}')
        return '\n'.join(lines)


class ArchError(Exception):
    pass


# ---------------------------------------------------------------------------------------------------------------------
# Reading the tree: which project files exist (git), and what MSBuild says each one references.
# ---------------------------------------------------------------------------------------------------------------------
def files(base, top):
    """Every file git would commit under base/top (tracked, plus new files that are not ignored), as posix paths."""
    run = subprocess.run(['git', '-C', str(base), 'ls-files', '-z', '-c', '-o', '--exclude-standard', '--', top],
                         capture_output=True)
    if run.returncode:
        raise ArchError(f'cannot list files with git in {base} ({run.stderr.decode(errors="replace").strip()}).')
    return sorted({name for name in run.stdout.decode('utf-8').split('\0') if name and (base / name).is_file()})


def project_files(base):
    return [path for path in files(base, 'src') if PROJECT_FILE.search(path)]


def judged_name(path):
    """The project's name when it sits at src/<Name>/<Name>.csproj, else None."""
    parts = path.split('/')
    if len(parts) == 3 and parts[2].lower().endswith('.csproj') and parts[2][:-len('.csproj')] == parts[1]:
        return parts[1]
    return None


def find_dotnet(given):
    exe = given or shutil.which('dotnet')
    if not exe:
        raise ArchError('`dotnet` is not on PATH. This check reads references through MSBuild and has no weaker '
                        'fallback; install the SDK named in global.json (CI: actions/setup-dotnet) or pass --dotnet.')
    return exe


def msbuild(dotnet, project, extra=()):
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1', MSBUILDDISABLENODEREUSE='1',
               DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1')
    command = [dotnet, 'msbuild', str(project), '-nologo', '-p:EnableWindowsTargeting=true',
               '-getProperty:TargetFramework', '-getProperty:TargetFrameworks'] + [f'-getItem:{i}' for i in ITEMS]
    run = subprocess.run(command + list(extra), capture_output=True, cwd=str(project.parent), env=env)
    out = run.stdout.decode('utf-8', errors='replace')
    start = out.find('{')
    if run.returncode or start < 0:
        tail = (out + run.stderr.decode('utf-8', errors='replace')).strip().splitlines()[-6:]
        raise ArchError(f'MSBuild could not evaluate {project.name} (exit {run.returncode}):\n    ' + '\n    '.join(tail))
    try:
        return json.loads(out[start:])
    except ValueError as error:
        raise ArchError(f'MSBuild output for {project.name} is not the expected JSON ({error}).')


def where_defined(base, item):
    """Repo-relative file that defined the item (e.g. Directory.Build.props), or the SDK file's name."""
    defining = item.get('DefiningProjectFullPath', '')
    if not defining:
        return ''
    try:
        return pathlib.Path(defining).resolve().relative_to(base.resolve()).as_posix()
    except ValueError:
        return f'{pathlib.Path(defining).name}, outside the repo'


def relative(base, full_path):
    """Repo-relative posix path of a referenced file, or None when it is outside the repo."""
    try:
        return pathlib.Path(full_path).resolve().relative_to(base.resolve()).as_posix()
    except ValueError:
        return None


def read_project(dotnet, base, path):
    """What MSBuild says one project references, merged over all its target frameworks."""
    project = base / path
    first = msbuild(dotnet, project)
    many = [t.strip() for t in first['Properties'].get('TargetFrameworks', '').split(';') if t.strip()]
    tfms = many or [first['Properties'].get('TargetFramework', '')]
    passes = [msbuild(dotnet, project, [f'-p:TargetFramework={t}']) for t in many] if many else [first]
    seen = {'tfms': sorted(tfms), 'project_refs': [], 'packages': [], 'frameworks': [], 'assemblies': []}
    keys = set()
    for data in passes:
        items = data.get('Items', {})
        for item in items.get('ProjectReference', []):
            full = item.get('FullPath') or str(project.parent / item['Identity'])
            entry = ('project_refs', relative(base, full) or full, where_defined(base, item))
            if entry not in keys:
                keys.add(entry)
                seen['project_refs'].append(entry[1:])
        for kind, bucket in [('PackageReference', 'packages'), ('FrameworkReference', 'frameworks')]:
            for item in items.get(kind, []):
                entry = (bucket, item['Identity'], where_defined(base, item))
                if entry not in keys:
                    keys.add(entry)
                    seen[bucket].append(entry[1:])
        for kind in ('Reference', 'COMReference', 'Analyzer'):
            for item in items.get(kind, []):
                if kind == 'Analyzer' and item.get('IsImplicitlyDefined', '').lower() == 'true':
                    continue  # the SDK's own code analyzers, added to every project
                entry = ('assemblies', f'<{kind} Include="{item["Identity"]}">', where_defined(base, item))
                if entry not in keys:
                    keys.add(entry)
                    seen['assemblies'].append(entry[1:])
    return seen


def read_tree(base, dotnet, names):
    """{path: evaluated} for the given project paths, evaluated in parallel."""
    with ThreadPoolExecutor(max_workers=8) as pool:
        futures = {path: pool.submit(read_project, dotnet, base, path) for path in names}
        return {path: future.result() for path, future in futures.items()}


# ---------------------------------------------------------------------------------------------------------------------
# Judging: pure functions over the rule file and what was read, so the self-test can plant any graph.
# ---------------------------------------------------------------------------------------------------------------------
def validate_layers(layers):
    """Findings for a malformed rule file (BOO-REF-006). Empty means the file can be trusted."""
    bad = []

    def problem(subject, what):
        bad.append(Finding('BOO-REF-006', subject, what, 'docs/architecture/layers.json'))

    if not isinstance(layers, dict) or not isinstance(layers.get('projects'), dict):
        problem('layers.json', 'the file must be a JSON object with a "projects" object.')
        return bad
    for key in sorted(set(layers) - TOP_KEYS):
        problem('layers.json', f'unknown top-level key "{key}" (known: {", ".join(sorted(TOP_KEYS))}).')
    extra = layers.get('all_src_projects', {'packages': []})
    if not isinstance(extra, dict) or set(extra) - {'packages'} or not is_names(extra.get('packages', [])):
        problem('all_src_projects', 'must be {"packages": [names]}.')
    projects = layers['projects']
    for name, row in sorted(projects.items()):
        if not isinstance(row, dict):
            problem(name, 'a project row must be an object.')
            continue
        for key in sorted(set(row) - PROJECT_KEYS):
            problem(name, f'unknown key "{key}" (known: {", ".join(sorted(PROJECT_KEYS))}).')
        for key in sorted(PROJECT_KEYS - {'target_frameworks'}):
            if key not in row:
                problem(name, f'missing "{key}" (write [] for none).')
        for key in sorted(PROJECT_KEYS & set(row)):
            if not is_names(row[key]):
                problem(name, f'"{key}" must be a list of distinct names.')
        for target in row.get('projects', []) if is_names(row.get('projects', [])) else []:
            if target == name:
                problem(name, 'a project may not be allowed to reference itself.')
            elif target not in projects:
                problem(name, f'allows a reference to "{target}", which has no row in layers.json.')
    if not bad:
        cycle = find_cycle({name: row['projects'] for name, row in projects.items()})
        if cycle:
            problem(' -> '.join(cycle), 'the allowed references form a cycle, so there are no layers.')
    return bad


def is_names(value):
    return isinstance(value, list) and all(isinstance(v, str) and v for v in value) and len(set(value)) == len(value)


def find_cycle(graph):
    state, stack = {}, []

    def visit(node):
        state[node] = 'open'
        stack.append(node)
        for nxt in graph.get(node, []):
            if state.get(nxt) == 'open':
                return stack[stack.index(nxt):] + [nxt]
            if nxt not in state:
                found = visit(nxt)
                if found:
                    return found
        stack.pop()
        state[node] = 'done'
        return None

    for node in sorted(graph):
        if node not in state:
            found = visit(node)
            if found:
                return found
    return None


def judge(layers, paths, evaluated, rise_only=False):
    """Findings for the tree. paths: every project file under src/; evaluated: {path: read_project result} for the
    judged ones. rise_only (the base branch's copy on a PR): skip BOO-REF-007, which only asks to tighten the file."""
    findings = validate_layers(layers)
    if findings:
        return findings
    allowed = layers['projects']
    everywhere = set(layers.get('all_src_projects', {}).get('packages', []))
    by_path = {}
    for path in paths:
        name = judged_name(path)
        if name is None:
            findings.append(Finding('BOO-REF-004', path, f'{path} is not at src/<Name>/<Name>.csproj, so it has no '
                                    'layer and its references are not judged.', path))
        elif name not in allowed:
            findings.append(Finding('BOO-REF-004', name, f'{path} has no row in docs/architecture/layers.json.', path))
        else:
            by_path[path.lower()] = name
    present = set(by_path.values())
    for name in sorted(set(allowed) - present):
        if not rise_only:
            findings.append(Finding('BOO-REF-007', name, f'layers.json declares {name}, but there is no '
                                    f'src/{name}/{name}.csproj. Remove its row (and every allowance naming it).'))
    for path, seen in sorted(evaluated.items()):
        name = by_path.get(path.lower())
        if name is None:
            continue
        row = allowed[name]
        used = {'projects': set(), 'packages': set(), 'framework_references': set()}
        for target, defined in seen['project_refs']:
            target_name = by_path.get(target.lower())
            if target_name is None:
                findings.append(Finding('BOO-REF-002', f'{name} -> {target}', f'{name} references {target}, which is '
                                        'not a project in src/ (only src/<Name>/<Name>.csproj projects may be '
                                        'referenced from src/).', f'{path}{via(defined, path)}'))
            elif target_name not in row['projects']:
                findings.append(Finding('BOO-REF-001', f'{name} -> {target_name}', f'{name} references {target_name}; '
                                        f'layers.json allows {name} only: {listing(row["projects"])}.',
                                        f'{path}{via(defined, path)}'))
            else:
                used['projects'].add(target_name)
        for package, defined in seen['packages']:
            if package in row['packages'] or package in everywhere:
                used['packages'].add(package)
            else:
                findings.append(Finding('BOO-REF-003', f'{name} uses {package}', f'{name} references package {package}; '
                                        f'layers.json allows {name} only: {listing(row["packages"] + sorted(everywhere))}.',
                                        f'{path}{via(defined, path)}'))
        for assembly, defined in seen['assemblies']:
            findings.append(Finding('BOO-REF-003', f'{name} uses {assembly}', f'{name} references an assembly or '
                                    f'analyzer directly ({assembly}); src/ projects use packages listed in '
                                    'layers.json only.', f'{path}{via(defined, path)}'))
        for framework, defined in seen['frameworks']:
            if framework in row['framework_references']:
                used['framework_references'].add(framework)
            else:
                findings.append(Finding('BOO-REF-005', f'{name} uses {framework}', f'{name} references framework '
                                        f'{framework}; layers.json allows {name} only: '
                                        f'{listing(row["framework_references"])}.', f'{path}{via(defined, path)}'))
        if 'target_frameworks' in row and sorted(seen['tfms']) != sorted(row['target_frameworks']):
            findings.append(Finding('BOO-REF-005', f'{name} targets {";".join(seen["tfms"])}',
                                    f'{name} targets {listing(seen["tfms"])}; layers.json says it must stay on '
                                    f'{listing(row["target_frameworks"])}.', path))
        if not rise_only:
            for key in ('projects', 'packages', 'framework_references'):
                for unused in sorted(set(row[key]) - used[key]):
                    findings.append(Finding('BOO-REF-007', f'{name}: {unused}', f'layers.json allows {name} {key} '
                                            f'entry "{unused}", but {name} no longer uses it.'))
    if not rise_only:
        used_everywhere = {p for seen in evaluated.values() for p, _ in seen['packages']}
        for unused in sorted(everywhere - used_everywhere):
            findings.append(Finding('BOO-REF-007', f'all_src_projects: {unused}', f'layers.json allows package '
                                    f'"{unused}" in every src/ project, but no src/ project uses it.'))
    return findings


def looser(base_layers, pr_layers):
    """BOO-REF-008 findings: everything the PR's layers.json allows that the base branch's does not."""
    bad = validate_layers(pr_layers)
    if bad:
        return bad
    found = []
    old, new = base_layers['projects'], pr_layers['projects']
    old_all = set(base_layers.get('all_src_projects', {}).get('packages', []))
    for package in sorted(set(pr_layers.get('all_src_projects', {}).get('packages', [])) - old_all):
        found.append(Finding('BOO-REF-008', f'all_src_projects: {package}', f'the PR allows package {package} in '
                             'every src/ project; the base branch does not.'))
    for name, row in sorted(new.items()):
        if name not in old:
            found.append(Finding('BOO-REF-008', name, f'the PR declares a new project {name}; the base branch does '
                                 'not (a new layer needs the owner).'))
            continue
        for key in ('projects', 'packages', 'framework_references'):
            for extra in sorted(set(row[key]) - set(old[name][key])):
                found.append(Finding('BOO-REF-008', f'{name}: {extra}', f'the PR allows {name} {key} entry "{extra}"; '
                                     'the base branch does not.'))
        if 'target_frameworks' in old[name] and sorted(row.get('target_frameworks', [])) != sorted(old[name]['target_frameworks']):
            found.append(Finding('BOO-REF-008', f'{name} target', f'the PR changes or drops {name}\'s required target '
                                 f'frameworks (base: {listing(old[name]["target_frameworks"])}).'))
    return found


def via(defined, path):
    return f' (defined in {defined})' if defined and defined != path else ''


def listing(names):
    return ', '.join(names) if names else 'none'


# ---------------------------------------------------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------------------------------------------------
def load_layers(path):
    if not path.is_file():
        raise ArchError(f'{path} is missing.')
    try:
        return json.loads(path.read_text(encoding='utf-8'))
    except ValueError as error:
        raise ArchError(f'{path} is not valid JSON ({error}).')


def evaluate(base, layers, dotnet):
    paths = project_files(base)
    judged = [p for p in paths if judged_name(p) in layers.get('projects', {})] if not validate_layers(layers) else []
    return paths, read_tree(base, dotnet, judged)


def check(base, layers_path, dotnet, rise_only=False, pr_layers_path=None):
    label = 'ARCH REFS (against the base branch\'s own script and layers.json)' if rise_only else 'ARCH REFS'
    try:
        layers = load_layers(layers_path)
        paths, evaluated = evaluate(base, layers, find_dotnet(dotnet))
        findings = judge(layers, paths, evaluated, rise_only)
        if pr_layers_path is not None:
            findings += looser(layers, load_layers(pr_layers_path))
    except ArchError as error:
        print(f'{label} FAILED: {error}')
        return 1
    if findings:
        print(f'{label} FAILED: {len(findings)} finding(s). Fix the cause in the project files.\n')
        print('\n\n'.join(f.text() for f in findings))
        return 1
    edges = sum(len(seen['project_refs']) for seen in evaluated.values())
    packages = sum(len(seen['packages']) for seen in evaluated.values())
    print(f'{label} OK: {len(evaluated)} projects, {edges} project references, {packages} package references, '
          f'all allowed by {layers_path.name} and all of it in use (read by MSBuild evaluation)'
          + ('; the PR\'s layers.json allows nothing more' if pr_layers_path is not None else ''))
    return 0


def show(base, layers_path, dotnet):
    try:
        layers = load_layers(layers_path)
        _, evaluated = evaluate(base, layers, find_dotnet(dotnet))
    except ArchError as error:
        print(f'ARCH REFS FAILED: {error}')
        return 1
    for path, seen in sorted(evaluated.items()):
        print(f'{path}  [{";".join(seen["tfms"])}]')
        for label, key in [('project', 'project_refs'), ('package', 'packages'), ('framework', 'frameworks'),
                           ('assembly', 'assemblies')]:
            for value, defined in seen[key]:
                print(f'  {label:9} {value}{via(defined, path)}')
    return 0


# ---------------------------------------------------------------------------------------------------------------------
# Self-test: planted graphs for the judging logic, then real MSBuild on throwaway trees for the reader.
# ---------------------------------------------------------------------------------------------------------------------
def planted_layers():
    return {
        'all_src_projects': {'packages': ['Shared.Analyzer']},
        'projects': {
            'Boo.Core': {'projects': [], 'packages': [], 'framework_references': ['Microsoft.NETCore.App'],
                         'target_frameworks': ['net10.0']},
            'Boo.Providers': {'projects': ['Boo.Core'], 'packages': [], 'framework_references': ['Microsoft.NETCore.App']},
            'Boo.Windows': {'projects': ['Boo.Core'], 'packages': ['NAudio'],
                            'framework_references': ['Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App']},
            'Boo.App': {'projects': ['Boo.Core', 'Boo.Windows', 'Boo.Providers'], 'packages': ['Velopack'],
                        'framework_references': ['Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App']},
        }}


def planted_tree():
    def seen(tfm, refs, packages, frameworks):
        return {'tfms': [tfm], 'project_refs': [(f'src/{r}/{r}.csproj', '') for r in refs],
                'packages': [(p, '') for p in packages], 'frameworks': [(f, '') for f in frameworks], 'assemblies': []}
    core = 'Microsoft.NETCore.App'
    wpf = 'Microsoft.WindowsDesktop.App'
    tree = {
        'src/Boo.Core/Boo.Core.csproj': seen('net10.0', [], ['Shared.Analyzer'], [core]),
        'src/Boo.Providers/Boo.Providers.csproj': seen('net10.0', ['Boo.Core'], [], [core]),
        'src/Boo.Windows/Boo.Windows.csproj': seen('net10.0-windows', ['Boo.Core'], ['NAudio'], [core, wpf]),
        'src/Boo.App/Boo.App.csproj': seen('net10.0-windows', ['Boo.Core', 'Boo.Windows', 'Boo.Providers'],
                                           ['Velopack'], [core, wpf]),
    }
    return sorted(tree), tree


def logic_cases():
    """(name, mutate(layers, paths, tree) in place, expected rule ids[, rise_only])."""
    def add_ref(project, target, defined=''):
        def fn(layers, paths, tree):
            tree[f'src/{project}/{project}.csproj']['project_refs'].append((target, defined))
        return fn

    def set_layers(fn):
        def wrapped(layers, paths, tree):
            fn(layers)
        return wrapped

    def new_project(path):
        def fn(layers, paths, tree):
            paths.append(path)
        return fn

    def add(project, key, value):
        def fn(layers, paths, tree):
            tree[f'src/{project}/{project}.csproj'][key].append((value, ''))
        return fn

    def drop_project(name):
        def fn(layers, paths, tree):
            path = f'src/{name}/{name}.csproj'
            paths.remove(path)
            del tree[path]
            for seen in tree.values():
                seen['project_refs'] = [r for r in seen['project_refs'] if r[0] != path]
        return fn

    def tfm(project, value):
        def fn(layers, paths, tree):
            tree[f'src/{project}/{project}.csproj']['tfms'] = [value]
        return fn

    cases = [
        ('today\'s shape passes', lambda *a: None, []),
        ('an allowed edge defined in Directory.Build.props passes',
         add_ref('Boo.Providers', 'src/Boo.Core/Boo.Core.csproj', 'Directory.Build.props'), []),
        ('a forbidden edge fails: Boo.Windows -> Boo.Providers',
         add_ref('Boo.Windows', 'src/Boo.Providers/Boo.Providers.csproj'), ['BOO-REF-001']),
        ('a forbidden edge from Directory.Build.props fails the same',
         add_ref('Boo.Core', 'src/Boo.App/Boo.App.csproj', 'Directory.Build.props'), ['BOO-REF-001']),
        ('a self reference fails', add_ref('Boo.Core', 'src/Boo.Core/Boo.Core.csproj'), ['BOO-REF-001']),
        ('a reference into tools/ fails', add_ref('Boo.App', 'tools/Boo.LiveProbe/Boo.LiveProbe.csproj'),
         ['BOO-REF-002']),
        ('a reference into tests/ fails', add_ref('Boo.Core', 'tests/Boo.Core.Tests/Boo.Core.Tests.csproj'),
         ['BOO-REF-002']),
        ('a reference outside the repo fails', add_ref('Boo.Core', '/elsewhere/X/X.csproj'), ['BOO-REF-002']),
        ('a reference to a src/ file that is not a judged project fails',
         add_ref('Boo.App', 'src/Group/Hack/Hack.csproj'), ['BOO-REF-002']),
        ('a new src/ project without a row fails', new_project('src/Boo.Extra/Boo.Extra.csproj'), ['BOO-REF-004']),
        ('a project nested deeper fails', new_project('src/Group/Hack/Hack.csproj'), ['BOO-REF-004']),
        ('a project whose name differs from its folder fails', new_project('src/Boo.Core/Other.csproj'),
         ['BOO-REF-004']),
        ('a .vbproj in src/ fails', new_project('src/Boo.Vb/Boo.Vb.vbproj'), ['BOO-REF-004']),
        ('a package not allowed fails', add('Boo.Core', 'packages', 'System.Net.Http.Json'), ['BOO-REF-003']),
        ('a package allowed for every project passes anywhere', add('Boo.Providers', 'packages', 'Shared.Analyzer'),
         []),
        ('a package allowed in another layer fails here', add('Boo.Core', 'packages', 'NAudio'), ['BOO-REF-003']),
        ('a direct DLL reference fails', add('Boo.Core', 'assemblies', '<Reference Include="x.dll">'),
         ['BOO-REF-003']),
        ('Core moved to -windows fails', tfm('Boo.Core', 'net10.0-windows10.0.19041.0'), ['BOO-REF-005']),
        ('WPF framework in Core fails', add('Boo.Core', 'frameworks', 'Microsoft.WindowsDesktop.App'),
         ['BOO-REF-005']),
        ('a layers.json cycle fails',
         set_layers(lambda l: l['projects']['Boo.Core']['projects'].append('Boo.App')), ['BOO-REF-006']),
        ('a two-step layers.json cycle fails',
         set_layers(lambda l: l['projects']['Boo.Core']['projects'].append('Boo.Providers')), ['BOO-REF-006']),
        ('an allowance naming an unknown project fails',
         set_layers(lambda l: l['projects']['Boo.App']['projects'].append('Boo.Ghost')), ['BOO-REF-006']),
        ('a layers.json self allowance fails',
         set_layers(lambda l: l['projects']['Boo.Core']['projects'].append('Boo.Core')), ['BOO-REF-006']),
        ('an unknown key fails', set_layers(lambda l: l['projects']['Boo.Core'].update(allow_all=True)),
         ['BOO-REF-006']),
        ('a missing key fails', set_layers(lambda l: l['projects']['Boo.Core'].pop('packages')), ['BOO-REF-006']),
        ('a duplicate name fails', set_layers(lambda l: l['projects']['Boo.Windows']['packages'].append('NAudio')),
         ['BOO-REF-006']),
        ('an unused allowance fails (lock the gain in)',
         set_layers(lambda l: l['projects']['Boo.Providers']['packages'].append('Unused')), ['BOO-REF-007']),
        ('an unused everywhere package fails',
         set_layers(lambda l: l['all_src_projects']['packages'].append('Unused')), ['BOO-REF-007']),
        ('a declared project that is gone fails', drop_project('Boo.Providers'),
         ['BOO-REF-007', 'BOO-REF-007']),  # its row, and App's now-unused allowance
        ('rise-only skips unused allowances', drop_project('Boo.Providers'), [], True),
        ('rise-only still fails a forbidden edge',
         add_ref('Boo.Windows', 'src/Boo.Providers/Boo.Providers.csproj'), ['BOO-REF-001'], True),
    ]
    return cases


def looser_cases():
    def changed(fn):
        layers = planted_layers()
        fn(layers)
        return layers
    return [
        ('the same file passes', changed(lambda l: None), []),
        ('a tightened file passes', changed(lambda l: l['projects']['Boo.App']['packages'].clear()), []),
        ('a new edge fails', changed(lambda l: l['projects']['Boo.Windows']['projects'].append('Boo.Providers')),
         ['BOO-REF-008']),
        ('a new package fails', changed(lambda l: l['projects']['Boo.Core']['packages'].append('X')), ['BOO-REF-008']),
        ('a new everywhere package fails', changed(lambda l: l['all_src_projects']['packages'].append('X')),
         ['BOO-REF-008']),
        ('a new framework fails',
         changed(lambda l: l['projects']['Boo.Core']['framework_references'].append('Microsoft.WindowsDesktop.App')),
         ['BOO-REF-008']),
        ('a new project fails', changed(lambda l: l['projects'].update({'Boo.New': {
            'projects': [], 'packages': [], 'framework_references': []}})), ['BOO-REF-008']),
        ('dropping a required target fails', changed(lambda l: l['projects']['Boo.Core'].pop('target_frameworks')),
         ['BOO-REF-008']),
        ('a broken PR file fails', changed(lambda l: l['projects']['Boo.Core'].update(x=1)), ['BOO-REF-006']),
    ]


MSBUILD_TREE = {
    'Directory.Build.props': '<Project/>',
    'src/A/A.csproj': '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                      '</PropertyGroup></Project>',
    'src/B/B.csproj': '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                      '</PropertyGroup><ItemGroup><ProjectReference Include="../A/A.csproj"/></ItemGroup></Project>',
    'tools/T/T.csproj': '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                        '</PropertyGroup></Project>',
}
MSBUILD_LAYERS = {'projects': {
    'A': {'projects': [], 'packages': [], 'framework_references': ['Microsoft.NETCore.App'],
          'target_frameworks': ['net10.0']},
    'B': {'projects': ['A'], 'packages': [], 'framework_references': ['Microsoft.NETCore.App']}}}


def msbuild_cases():
    """(name, {path: text or None to delete}, expected rule ids): each runs real MSBuild on a throwaway tree."""
    a_with = lambda extra, head='<TargetFramework>net10.0</TargetFramework>': (  # noqa: E731
        f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>{head}</PropertyGroup><ItemGroup>{extra}</ItemGroup></Project>')
    b_with = lambda extra: a_with('<ProjectReference Include="../A/A.csproj"/>' + extra)  # noqa: E731
    return [
        ('MSBuild: an allowed edge passes', {}, []),
        ('MSBuild: a forbidden edge in the .csproj fails', {'src/A/A.csproj': a_with(
            '<ProjectReference Include="../B/B.csproj"/>')}, ['BOO-REF-001']),
        ('MSBuild: a forbidden edge added by Directory.Build.props is seen', {'Directory.Build.props': (
            '<Project><ItemGroup Condition="\'$(MSBuildProjectName)\' == \'A\'">'
            '<ProjectReference Include="$(MSBuildThisFileDirectory)src/B/B.csproj"/></ItemGroup></Project>')},
         ['BOO-REF-001']),
        ('MSBuild: a reference under a false condition is not counted', {'src/A/A.csproj': a_with(
            '<ProjectReference Include="../B/B.csproj" Condition="\'$(NeverSet)\' == \'yes\'"/>')}, []),
        ('MSBuild: a reference for one of several target frameworks is seen', {'src/B/B.csproj': (
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net10.0;net9.0</TargetFrameworks>'
            '</PropertyGroup><ItemGroup><ProjectReference Include="../A/A.csproj"/>'
            '<ProjectReference Include="../../tools/T/T.csproj" Condition="\'$(TargetFramework)\' == \'net9.0\'"/>'
            '</ItemGroup></Project>')}, ['BOO-REF-002']),
        ('MSBuild: references into tools/ and tests/ fail', {'tests/X/X.csproj': MSBUILD_TREE['tools/T/T.csproj'],
                                                             'src/B/B.csproj': b_with(
            '<ProjectReference Include="../../tools/T/T.csproj"/><ProjectReference Include="../../tests/X/X.csproj"/>')},
         ['BOO-REF-002', 'BOO-REF-002']),
        ('MSBuild: a new src/ project fails', {'src/C/C.csproj': MSBUILD_TREE['tools/T/T.csproj']}, ['BOO-REF-004']),
        ('MSBuild: a package, a DLL and an analyzer fail', {'src/B/B.csproj': b_with(
            '<PackageReference Include="Newtonsoft.Json" Version="13.0.3"/><Reference Include="x.dll"/>'
            '<Analyzer Include="evil.dll"/>')}, ['BOO-REF-003', 'BOO-REF-003', 'BOO-REF-003']),
        ('MSBuild: a target framework change fails', {'src/A/A.csproj': a_with(
            '', '<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework><UseWPF>true</UseWPF>')},
         ['BOO-REF-005'] * 3),  # the target, and the WPF and Windows SDK frameworks it brings
    ]


def self_test(dotnet):
    failures = []
    for name, mutate, expected, *rise_only in logic_cases():
        layers, (paths, tree) = planted_layers(), planted_tree()
        mutate(layers, paths, tree)
        got = sorted(f.rule_id for f in judge(layers, paths, tree, bool(rise_only)))
        if got != sorted(expected):
            failures.append(f'logic: {name}: expected {expected}, got {got}')
    for name, pr_layers, expected in looser_cases():
        got = sorted(f.rule_id for f in looser(planted_layers(), pr_layers))
        if got != sorted(expected):
            failures.append(f'base check: {name}: expected {expected}, got {got}')
    for finding in judge(planted_layers(), ['src/Boo.Extra/Boo.Extra.csproj'], {}):
        text = finding.text()
        if not all(f'\n  {label}' in text for label in ('What:', 'Why:', 'Fix:', 'Do NOT:', 'Rule:')):
            failures.append(f'message: {finding.rule_id} lacks What/Why/Fix/Do NOT/Rule')
    try:
        exe = find_dotnet(dotnet)
    except ArchError as error:
        print(f'SELF-TEST FAILED: {error}')
        return 1
    cases = msbuild_cases()
    with tempfile.TemporaryDirectory() as temp:
        def run_case(index, case):
            name, changes, expected = case
            tree = pathlib.Path(temp) / str(index)
            subprocess.run(['git', 'init', '-q', str(tree)], check=True)
            if (root / 'global.json').is_file():
                shutil.copy(root / 'global.json', tree / 'global.json')
            for path, text in {**MSBUILD_TREE, **changes}.items():
                (tree / path).parent.mkdir(parents=True, exist_ok=True)
                (tree / path).write_text(text, encoding='utf-8')
            try:
                paths, evaluated = evaluate(tree, MSBUILD_LAYERS, exe)
                got = sorted(f.rule_id for f in judge(MSBUILD_LAYERS, paths, evaluated))
            except ArchError as error:
                return f'{name}: {error}'
            return None if got == sorted(expected) else f'{name}: expected {expected}, got {got}'
        with ThreadPoolExecutor(max_workers=4) as pool:
            failures += [r for r in pool.map(lambda pair: run_case(*pair), enumerate(cases)) if r]
    if failures:
        print('SELF-TEST FAILED:\n  ' + '\n  '.join(failures))
        return 1
    print(f'SELF-TEST OK: {len(logic_cases())} planted graphs, {len(looser_cases())} base-branch loosening cases, '
          f'{len(cases)} throwaway trees read by real MSBuild (imports, conditions, per-framework items), '
          f'{len(RULES)} rules')
    return 0


def argument(flag):
    if flag in sys.argv:
        index = sys.argv.index(flag)
        return sys.argv[index + 1] if index + 1 < len(sys.argv) else ''
    return None


if __name__ == '__main__':
    base_root = pathlib.Path(argument('--root')).resolve() if argument('--root') else root
    layers_file = pathlib.Path(argument('--layers')).resolve() if argument('--layers') else LAYERS
    pr_layers_file = pathlib.Path(argument('--pr-layers')).resolve() if argument('--pr-layers') else None
    dotnet_exe = argument('--dotnet')
    if '--self-test' in sys.argv:
        sys.exit(self_test(dotnet_exe))
    if '--show' in sys.argv:
        sys.exit(show(base_root, layers_file, dotnet_exe))
    sys.exit(check(base_root, layers_file, dotnet_exe, rise_only='--rise-only' in sys.argv,
                   pr_layers_path=pr_layers_file))
