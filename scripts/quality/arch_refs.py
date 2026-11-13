"""Architecture references, judged on the SHIPPED build: every src/ project may reference only the projects, packages,
frameworks and package build code that docs/architecture/layers.json allows. Never reads secrets.

  python -I scripts/quality/arch_refs.py judge artifacts/build-facts/build-facts.json
        windows-ci runs this right after "Build facts": the facts of its own Release build of Boo.sln and its win-x64
        publish of Boo.App (both binary logs), against docs/architecture/layers.json.
  python -I <base copy of arch_refs.py> judge FACTS --root . --layers <base layers.json> --rise-only --pr-layers <PR layers.json>
        windows-ci on a pull request: the BASE branch's own script and layers.json judge the same facts, and refuse a
        PR layers.json that allows more than the base one, so editing the PR's copies changes nothing.
  python -I scripts/quality/arch_refs.py show FACTS     print what the facts say each src/ project references
  python -I scripts/quality/arch_refs.py format          layers.json well formed (BOO-REF-006); no build needed
  python -I scripts/quality/arch_refs.py --self-test     the judging logic on planted facts (no build needed)

Why the facts and not an evaluation: a gate that evaluates each project alone (Debug, no runtime identifier, another
OS) never sees a reference that exists only under Configuration == Release, a RuntimeIdentifier, _IsPublishing,
SolutionName or IsOSPlatform('Windows'), nor build code from a PackageDownload + Exists() import or a NuGet MSBuild SDK.
The facts (scripts/quality/buildfacts, docs/quality/RULES.md "Build facts") are what the real build did, per project,
target framework and runtime identifier, in both logs: the ProjectReference items, the ProjectReferences really
followed into the compile, the PackageReference / PackageDownload / Reference / FrameworkReference items, the restore
results (project.assets.json: restored libraries, direct dependencies, downloads), every imported build file, and every
reference and analyzer csc was given, with where it came from. Anything the judge cannot place fails (fail closed).

Exit 1 on any finding. Each finding prints what broke, why it matters, the root-cause fix, the wrong fix to avoid,
and the rule's anchor in docs/quality/RULES.md (BOO-REF-001..008). Known gaps are listed there.
"""
import json
import pathlib
import posixpath
import re
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parent.parent.parent
LAYERS = root / 'docs' / 'architecture' / 'layers.json'
RULES_DOC = 'docs/quality/RULES.md'
SCHEMA = 'boo-build-facts/1'
LOGS = ('build', 'publish')
PROJECT_FILE = re.compile(r'\.\w*proj$', re.IGNORECASE)  # .csproj, .CSPROJ, .vbproj, .fsproj, .shproj, .proj, ...
ROW_KEYS = {'target_frameworks', 'projects', 'packages', 'package_build_files', 'framework_references'}
USE_KEYS = ('projects', 'packages', 'package_build_files', 'framework_references')
TOP_KEYS = {'about', 'rules', 'sdk_packs', 'all_src_projects', 'projects'}
ALL_KEYS = {'packages', 'package_build_files'}
# WPF's temporary markup-compile project: <dir>/<Name>_wpftmp.csproj (the facts reader removes its random part).
WPF_TEMP = re.compile(r'^(?P<dir>.+)/(?P<name>[^/]+)_wpftmp\.csproj$')
NUGET = re.compile(r'^<nuget>/(?P<id>[^/<>]+)/(?P<version>[^/<>]+)/(?P<rest>.+)$')
PACK_REF = re.compile(r'^<dotnet>/packs/(?P<pack>[^/]+)\.Ref/[^/]+/ref/')
SRC_OUTPUT = re.compile(r'^src/(?P<folder>[^/]+)/(?:bin|obj)/(?:[^/]+/)*(?P<file>[^/]+)\.dll$', re.IGNORECASE)
PROFILES = {'microsoft.windowsdesktop.app.wpf': 'Microsoft.WindowsDesktop.App',
            'microsoft.windowsdesktop.app.windowsforms': 'Microsoft.WindowsDesktop.App'}
LIBRARY = re.compile(r'^(?P<id>[^/ ]+)/(?P<version>[^ ]+) \((?P<type>[a-z]+)(?: (?P<path>.+))?\)$')
UNIT_TYPES = {'project': str, 'targetFramework': str, 'runtimeIdentifier': str, 'imports': list,
              'packageReferences': list, 'packageDownloads': list, 'projectReferenceItems': list,
              'projectReferencesFollowed': list, 'referenceItems': list, 'frameworkReferences': list,
              'assets': (dict, type(None)), 'csc': list}


class Rule:
    def __init__(self, rule_id, title, why, fix, dont):
        self.id, self.title, self.why, self.fix, self.dont = rule_id, title, why, fix, dont


RULES = {rule.id: rule for rule in [
    Rule('BOO-REF-001', 'a project reference that layers.json does not allow',
         'Layers keep each part testable and replaceable. One reference added "to reuse a helper" ties two layers '
         'together for good; the next change builds on it and the layers stop meaning anything.',
         'Move the shared code down into a project both may reference (usually Boo.Core, as plain values or an '
         'interface), or do the wiring in Boo.App, which may reference every layer.',
         'Do NOT add the edge to docs/architecture/layers.json to make this pass, and do NOT hide it behind a '
         'condition (Configuration, RuntimeIdentifier, _IsPublishing, SolutionName, the OS): the real build\'s facts '
         'show it anyway, and the base branch\'s copy of this check refuses a new edge.'),
    Rule('BOO-REF-002', 'a project reference that leaves src/',
         'tools/ and tests/ are not shipped and not judged (tests/ is the frozen oracle). Product code that depends on '
         'them ships tool or test code, and breaks when they change.',
         'Move the code the product needs into a src/ project (as layers.json allows), or turn the dependency '
         'around: the tool or test references the product.',
         'Do NOT copy the project into src/ under a new name, and do NOT link its files in with <Compile Include>.'),
    Rule('BOO-REF-003', 'a package, assembly, analyzer or package build code that layers.json does not allow',
         'Every package is code Boo ships, updates and trusts, and a package\'s build files run inside the build. A '
         'package in the wrong layer (an HTTP, Windows or database library in Core) breaks the layer from below '
         'without any project reference; a downloaded package or a NuGet MSBuild SDK can change the build unseen.',
         'Use what the layer already has, or put the code that needs the package in the layer that owns that kind of '
         'work (Storage: database and encryption, Windows: OS integration, Providers: network and AI, App: installer).',
         'Do NOT reference the DLL directly (<Reference>, <Analyzer>, a path under bin/ or obj/), add the package from '
         'Directory.Build.props or a target, download it with <PackageDownload> and import its build files, or pull '
         'build code in with <Sdk Name=... Version=...>: the build facts show all of these.'),
    Rule('BOO-REF-004', 'a src/ project that cannot be judged: undeclared, misplaced, or missing from the build facts',
         'A project nobody declared has no rules, so anything it references would pass; a declared project the build '
         'facts do not show (not compiled, or without restore results) is not judged at all.',
         'Put it at src/<Name>/<Name>.csproj, in Boo.sln, and add a row for it to docs/architecture/layers.json with '
         'the smallest set of references it needs, in the same PR; the owner approves a new layer (the base branch\'s '
         'copy fails until the row exists on the base).',
         'Do NOT put product code in tools/ or tests/ and reference it from src/ (BOO-REF-002), and do NOT take a '
         'project out of Boo.sln to keep it out of the facts.'),
    Rule('BOO-REF-005', 'a target framework or framework reference that layers.json does not allow',
         'Boo.Core and Boo.Providers target plain net10.0 so they can never use WPF, WinForms or Windows APIs; that '
         'keeps them testable anywhere. A framework reference (WPF, WinForms, Windows SDK) is a dependency like any '
         'other, and a new target framework is a new build of the project.',
         'Keep Windows-only code in Boo.Windows or Boo.App and hand the result to Core as plain values.',
         'Do NOT add -windows or a second target framework, or a <FrameworkReference>, to reach one Windows type.'),
    Rule('BOO-REF-006', 'docs/architecture/layers.json is not valid',
         'The check can only be as good as its rule file: a cycle means there are no layers, and an allowance '
         'naming a project that does not exist is a typo waiting to pass something else.',
         'Fix the file: known keys only, lists of distinct names, every row complete, no project allowed to '
         'reference itself, no cycle.',
         'Do NOT delete the rule file or a project row to silence this.'),
    Rule('BOO-REF-007', 'layers.json allows something the shipped build no longer has (good news)',
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
        self.rule_id, self.subject, self.what = rule_id, subject, what
        self.wheres = [where] if where else []

    def text(self):
        rule = RULES[self.rule_id]
        lines = [f'{self.rule_id}  {self.subject}  ({rule.title})', f'  What:   {self.what}', f'  Why:    {rule.why}',
                 f'  Fix:    {rule.fix}', f'  Do NOT: {rule.dont[len("Do NOT "):]}',
                 f'  Rule:   {RULES_DOC}#{self.rule_id.lower()}']
        shown = self.wheres[:12] + ([f'... and {len(self.wheres) - 12} more'] if len(self.wheres) > 12 else [])
        for index, where in enumerate(shown):
            lines.append(('  Where:  ' if index == 0 else '          ') + where)
        return '\n'.join(lines)


class Findings:
    """Findings, one per (rule, subject); the same problem seen in several units lists every place it was seen."""

    def __init__(self):
        self.items = {}

    def add(self, rule_id, subject, what, where=''):
        key = (rule_id, subject)
        if key not in self.items:
            self.items[key] = Finding(rule_id, subject, what, where)
        elif where and where not in self.items[key].wheres:
            self.items[key].wheres.append(where)

    def extend(self, findings):
        for f in findings:
            for where in f.wheres or ['']:
                self.add(f.rule_id, f.subject, f.what, where)

    def list(self):
        return list(self.items.values())


class ArchError(Exception):
    pass


# ---------------------------------------------------------------------------------------------------------------------
# The rule file
# ---------------------------------------------------------------------------------------------------------------------
def is_names(value, nonempty=False):
    return (isinstance(value, list) and all(isinstance(v, str) and v for v in value)
            and len({v.lower() for v in value}) == len(value) and (bool(value) or not nonempty))


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
    if not is_names(layers.get('sdk_packs')):
        problem('sdk_packs', 'must be a list of distinct names (the packs the SDK downloads for the frameworks and the '
                'runtime identifier; write [] for none).')
    extra = layers.get('all_src_projects', {})
    if not isinstance(extra, dict) or set(extra) - ALL_KEYS or not all(is_names(extra.get(k, [])) for k in ALL_KEYS):
        problem('all_src_projects', 'must be {"packages": [names], "package_build_files": [names]} (both optional).')
    projects = layers['projects']
    for name, row in sorted(projects.items()):
        if not isinstance(row, dict):
            problem(name, 'a project row must be an object.')
            continue
        for key in sorted(set(row) - ROW_KEYS):
            problem(name, f'unknown key "{key}" (known: {", ".join(sorted(ROW_KEYS))}).')
        for key in sorted(ROW_KEYS - set(row)):
            problem(name, f'missing "{key}" (write [] for none; target_frameworks needs at least one).')
        for key in sorted(ROW_KEYS & set(row)):
            if not is_names(row[key], nonempty=key == 'target_frameworks'):
                problem(name, f'"{key}" must be a list of distinct names'
                        + (' with at least one entry.' if key == 'target_frameworks' else '.'))
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


def looser(base_layers, pr_layers):
    """BOO-REF-008 findings: everything the PR's layers.json allows that the base branch's does not."""
    bad = validate_layers(pr_layers)
    if bad:
        return bad
    found = []

    def lower(names):
        return {n.lower() for n in names}

    for package in sorted(n for n in pr_layers['sdk_packs'] if n.lower() not in lower(base_layers['sdk_packs'])):
        found.append(Finding('BOO-REF-008', f'sdk_packs: {package}', f'the PR allows SDK pack {package}; the base '
                             'branch does not.'))
    old_all, new_all = base_layers.get('all_src_projects', {}), pr_layers.get('all_src_projects', {})
    for key in sorted(ALL_KEYS):
        for package in sorted(n for n in new_all.get(key, []) if n.lower() not in lower(old_all.get(key, []))):
            found.append(Finding('BOO-REF-008', f'all_src_projects {key}: {package}', f'the PR allows {key} entry '
                                 f'{package} in every src/ project; the base branch does not.'))
    old, new = base_layers['projects'], pr_layers['projects']
    for name, row in sorted(new.items()):
        if name not in old:
            found.append(Finding('BOO-REF-008', name, f'the PR declares a new project {name}; the base branch does '
                                 'not (a new layer needs the owner).'))
            continue
        for key in USE_KEYS:
            for extra in sorted(n for n in row[key] if n.lower() not in lower(old[name].get(key, []))):
                found.append(Finding('BOO-REF-008', f'{name}: {extra}', f'the PR allows {name} {key} entry "{extra}"; '
                                     'the base branch does not.'))
        if sorted(row['target_frameworks']) != sorted(old[name].get('target_frameworks', [])):
            found.append(Finding('BOO-REF-008', f'{name} target', f'the PR changes {name}\'s target frameworks to '
                                 f'{listing(row["target_frameworks"])} (base: '
                                 f'{listing(old[name].get("target_frameworks", []))}).'))
    return found


# ---------------------------------------------------------------------------------------------------------------------
# The tree (git) and the facts
# ---------------------------------------------------------------------------------------------------------------------
def project_files(base):
    """Every project file git would commit under base/src (tracked, plus new files that are not ignored)."""
    try:
        run = subprocess.run(['git', '-C', str(base), 'ls-files', '-z', '-c', '-o', '--exclude-standard', '--', 'src'],
                             capture_output=True)
    except OSError as error:
        raise ArchError(f'cannot run git ({error}).')
    if run.returncode:
        raise ArchError(f'cannot list files with git in {base} ({run.stderr.decode(errors="replace").strip()}).')
    names = {name for name in run.stdout.decode('utf-8').split('\0') if name and (base / name).is_file()}
    return sorted(name for name in names if PROJECT_FILE.search(name))


def judged_name(path):
    """The project's name when it sits at src/<Name>/<Name>.csproj, else None."""
    parts = path.split('/')
    if len(parts) == 3 and parts[2].lower().endswith('.csproj') and parts[2][:-len('.csproj')] == parts[1]:
        return parts[1]
    return None


def load_json(path, what):
    if not path.is_file():
        raise ArchError(f'{what} {path.name} is missing.')
    try:
        return json.loads(path.read_text(encoding='utf-8'))
    except ValueError as error:
        raise ArchError(f'{what} {path.name} is not valid JSON ({error}).')


def check_facts(facts):
    """Fail closed on facts this judge cannot read: wrong schema, a missing or failed log, a malformed unit."""
    if not isinstance(facts, dict) or facts.get('schema') != SCHEMA or not isinstance(facts.get('binlogs'), list):
        raise ArchError(f'the build facts are not schema {SCHEMA} (scripts/quality/buildfacts).')
    labels = [b.get('label') for b in facts['binlogs'] if isinstance(b, dict)]
    for label in LOGS:
        if labels.count(label) != 1:
            raise ArchError(f'the build facts must hold exactly one "{label}" log (the windows-ci Release build of '
                            f'Boo.sln and the win-x64 publish of Boo.App); found {labels}.')
    for b in facts['binlogs']:
        if b.get('label') not in LOGS:
            continue
        if b.get('succeeded') is not True or not isinstance(b.get('units'), list):
            raise ArchError(f'the "{b.get("label")}" log did not succeed or has no units; a failed build is not judged.')
        for index, unit in enumerate(b['units']):
            for key, kind in UNIT_TYPES.items():
                if not isinstance(unit, dict) or not isinstance(unit.get(key), kind):
                    raise ArchError(f'unit {index} of the "{b["label"]}" log has no valid "{key}".')
    return facts


def after_colon(text):
    """'net10.0: Name [1.0, )' -> 'Name [1.0, )'."""
    return text.split(': ', 1)[1] if ': ' in text else text


def item_name(text):
    """'Name 1.2.3', 'Name [1.0]', 'Name [1.0, )' -> 'Name'."""
    return text.strip().split(' ', 1)[0]


def unit_place(label, unit):
    rid = f', {unit["runtimeIdentifier"]}' if unit['runtimeIdentifier'] else ''
    return f'{label} log: {unit["project"]} [{unit["targetFramework"]}{rid}]'


# ---------------------------------------------------------------------------------------------------------------------
# Judging: a pure function over the rule file, the project files git lists and the facts, so the self-test can plant
# any build.
# ---------------------------------------------------------------------------------------------------------------------
def judge(layers, paths, facts, rise_only=False):
    """(findings, summary) for the facts. paths: every project file git lists under src/. rise_only (the base branch's
    copy on a PR): skip BOO-REF-007, which only asks to tighten the file."""
    bad = validate_layers(layers)
    if bad:
        return bad, {}
    out = Findings()
    rows = layers['projects']
    every = layers.get('all_src_projects', {})
    ctx = {'sdk_packs': {n.lower() for n in layers['sdk_packs']},
           'every_packages': {n.lower() for n in every.get('packages', [])},
           'every_build': {n.lower() for n in every.get('package_build_files', [])},
           'used_every': {'packages': set(), 'package_build_files': set()}, 'used_packs': set(), 'by_path': {}}
    by_path = ctx['by_path']  # lower-case project path -> row name, for the judged src/<Name>/<Name>.csproj files
    for path in paths:
        name = judged_name(path)
        if name is None:
            out.add('BOO-REF-004', path, f'{path} is not at src/<Name>/<Name>.csproj, so it has no layer and its '
                    'references are not judged.', path)
        elif name not in rows:
            out.add('BOO-REF-004', name, f'{path} has no row in docs/architecture/layers.json.', path)
        else:
            by_path[path.lower()] = name
    present = set(by_path.values())
    if not rise_only:
        for name in sorted(set(rows) - present):
            out.add('BOO-REF-007', name, f'layers.json declares {name}, but there is no src/{name}/{name}.csproj. '
                    'Remove its row (and every allowance naming it).')

    used = {name: {k: set() for k in USE_KEYS} for name in rows}
    tfms = {name: set() for name in rows}
    compiled = set()
    summary = {name: {'units': [], 'projects': set(), 'packages': set(), 'frameworks': set(), 'imports': set(),
                      'downloads': set()} for name in rows}

    def owner(project):
        """Row name for a unit's project file: the judged project itself, or WPF's temporary markup project of one."""
        name = by_path.get(project.lower())
        if name is None:
            m = WPF_TEMP.match(project)
            if m:
                name = by_path.get(f'{m["dir"]}/{m["name"]}.csproj'.lower())
        return name

    for b in facts['binlogs']:
        if b['label'] not in LOGS:
            continue
        for unit in b['units']:
            project = unit['project']
            if not project.lower().startswith('src/'):
                continue  # tests/ and tools/ are not judged here; a src/ reference to them is (BOO-REF-002)
            place = unit_place(b['label'], unit)
            name = owner(project)
            if name is None:
                out.add('BOO-REF-004', project, f'the {b["label"]} log builds {project}, which is not a declared '
                        'src/<Name>/<Name>.csproj project (or its WPF markup project).', place)
                continue
            judge_unit(out, ctx, name, rows[name], unit, place, used[name], summary[name])
            tfms[name].add(unit['targetFramework'])
            if b['label'] == 'build' and unit['csc'] and project.lower() == f'src/{name}/{name}.csproj'.lower():
                compiled.add(name)

    for name in sorted(present):
        row = rows[name]
        if name not in compiled:
            out.add('BOO-REF-004', name, f'the build log has no compile of src/{name}/{name}.csproj, so nothing it '
                    'references is judged (is it in Boo.sln?).', f'src/{name}/{name}.csproj')
        if tfms[name] and sorted(tfms[name]) != sorted(row['target_frameworks']):
            out.add('BOO-REF-005', f'{name} targets {";".join(sorted(tfms[name]))}', f'{name} is built for '
                    f'{listing(sorted(tfms[name]))}; layers.json says {listing(row["target_frameworks"])}.',
                    f'src/{name}/{name}.csproj')
    if not rise_only:
        for name in sorted(compiled):
            for key in USE_KEYS:
                for unused in sorted(n for n in rows[name][key] if n.lower() not in used[name][key]):
                    out.add('BOO-REF-007', f'{name}: {unused}', f'layers.json allows {name} {key} entry "{unused}", '
                            f'but the shipped build of {name} no longer uses it.')
        for key in sorted(ALL_KEYS):
            for unused in sorted(n for n in every.get(key, []) if n.lower() not in ctx['used_every'][key]):
                out.add('BOO-REF-007', f'all_src_projects {key}: {unused}', f'layers.json allows {key} entry '
                        f'"{unused}" in every src/ project, but no src/ project uses it.')
        for unused in sorted(n for n in layers['sdk_packs'] if n.lower() not in ctx['used_packs']):
            out.add('BOO-REF-007', f'sdk_packs: {unused}', f'layers.json allows SDK pack "{unused}", but no src/ '
                    'project downloads or uses it.')
    return out.list(), summary


def judge_unit(out, ctx, name, row, unit, place, used, seen):
    """Every reference fact of one unit (one project, framework and runtime identifier in one log)."""
    seen['units'].append(place)
    sdk_packs, by_path = ctx['sdk_packs'], ctx['by_path']
    allowed_packages = {n.lower() for n in row['packages']} | ctx['every_packages']
    allowed_build = {n.lower() for n in row['package_build_files']} | ctx['every_build']
    allowed_frameworks = {n.lower() for n in row['framework_references']}
    project_dir = posixpath.dirname(unit['project'])

    assets = unit['assets']
    if assets is None or assets.get('missing') or not all(isinstance(assets.get(k), list) for k in
                                                          ('libraries', 'directDependencies', 'downloadDependencies')):
        out.add('BOO-REF-004', f'{name} restore results', f'the facts have no restore results (project.assets.json) '
                f'for {unit["project"]}, so its packages cannot be judged.', place)
        assets = {'libraries': [], 'directDependencies': [], 'downloadDependencies': []}
    restored = set()  # 'id/version', lower case: exactly what the restore resolved for this unit
    edges = []  # (target path, how)
    frameworks = [(f, 'FrameworkReference item') for f in unit['frameworkReferences']]
    for library in assets['libraries']:
        m = LIBRARY.match(library)
        if m and m['type'] == 'package':
            restored.add(f"{m['id']}/{m['version']}".lower())
        elif m and m['type'] == 'project' and m['path']:
            edges.append((posixpath.normpath(posixpath.join(project_dir, m['path'])), 'restored project library'))
        else:
            out.add('BOO-REF-003', f'{name} restores {library}', f'{name} restores "{library}", which is neither a '
                    'package nor a project; the judge cannot place it.', place)

    downloads = set()
    for text, how in ([(d, 'PackageDownload item') for d in unit['packageDownloads']]
                      + [(after_colon(d), 'restore download') for d in assets['downloadDependencies']]):
        package = item_name(text)
        downloads.add(package.lower())
        seen['downloads'].add(package)
        if package.lower() in sdk_packs:
            ctx['used_packs'].add(package.lower())
        else:
            out.add('BOO-REF-003', f'{name} downloads {package}', f'{name} downloads package {package} ({how}); only '
                    f'the SDK packs in layers.json "sdk_packs" may be downloaded, never a package to import build '
                    'code from.', place)

    def from_package(package_id, version):
        """A file of a package this unit restores (that id at that version), or of an SDK pack it downloads."""
        if f'{package_id}/{version}'.lower() in restored:
            return True
        if package_id.lower() in sdk_packs and package_id.lower() in downloads:
            ctx['used_packs'].add(package_id.lower())
            return True
        return False

    for text, how in ([(p, 'PackageReference item') for p in unit['packageReferences']]
                      + [(after_colon(d), 'restore direct dependency') for d in assets['directDependencies']]):
        package = item_name(text)
        seen['packages'].add(package)
        if package.lower() in allowed_packages:
            used['packages'].add(package.lower())
            if package.lower() in ctx['every_packages']:
                ctx['used_every']['packages'].add(package.lower())
        else:
            out.add('BOO-REF-003', f'{name} uses {package}', f'{name} references package {package} ({how}); '
                    f'layers.json allows {name} only: {listing(row["packages"] + sorted(ctx["every_packages"]))}.',
                    place)

    for item in unit['projectReferenceItems']:
        edges.append((item, 'ProjectReference item'))
    for item in unit['projectReferencesFollowed']:
        edges.append((item, 'ProjectReference followed into the compile'))

    def place_file(path, how):
        """A reference given as a plain file path (a <Reference> item, or WPF's markup compile, which passes plain
        paths without metadata): a framework pack, a restored package, or another src/ project's output; else 003."""
        pack = PACK_REF.match(path)
        nuget = NUGET.match(path)
        output = SRC_OUTPUT.match(path)
        if pack:
            frameworks.append((pack['pack'], f'{how} {path}'))
        elif nuget and from_package(nuget['id'], nuget['version']):
            pass
        elif output and output['file'] == output['folder']:
            edges.append((f'src/{output["folder"]}/{output["folder"]}.csproj', f'{how} {path}'))
        else:
            out.add('BOO-REF-003', f'{name} uses {path}', f'{name} is given {path} ({how}), which is neither a '
                    'framework pack, a file of a package it restores, nor the output of a src/ project.', place)

    for item in unit['referenceItems']:
        # '<Include> hint=<HintPath>' (the facts reader's form); WPF's markup project lists its references this way.
        include, _, hint = item.partition(' hint=')
        place_file((hint or include).replace('\\', '/'), '<Reference> item')

    for call in unit['csc']:
        for ref in call.get('references', []):
            origin, path = ref.get('origin'), ref.get('path') or ''
            if origin == 'projectReference':
                edges.append((ref.get('project') or path, 'compile reference from a ProjectReference'))
            elif origin == 'frameworkReference':
                frameworks.append(((ref.get('package') or '').split('/')[0], f'compile reference {path}'))
            elif origin == 'packageReference':
                package = ref.get('package') or ''
                if package.lower() not in restored:
                    out.add('BOO-REF-003', f'{name} compiles against {package}', f'{name} is compiled against {path} '
                            f'from package {package}, which is not in its restore results (that id at that version).',
                            place)
            elif origin == 'reference':
                place_file(path, 'compile reference')
            else:
                out.add('BOO-REF-003', f'{name} compiles against {path}', f'{name} is compiled against {path} with an '
                        f'unknown origin "{origin}"; the judge cannot place it.', place)
        for analyzer in call.get('analyzers', []):
            if analyzer.startswith('<dotnet>/'):
                continue  # the SDK's own analyzers and source generators (the SDK is pinned by global.json)
            m = NUGET.match(analyzer)
            if m and from_package(m['id'], m['version']):
                continue
            out.add('BOO-REF-003', f'{name} runs analyzer {analyzer}', f'{name} is compiled with analyzer {analyzer}, '
                    'which is neither the SDK\'s nor from a package this project restores.', place)

    for text in unit['imports']:
        imported = text.split(' <= ', 1)[0]
        if imported.startswith('<dotnet>/'):
            continue  # the SDK's own build files (the SDK is pinned by global.json)
        m = NUGET.match(imported)
        if m:
            package = m['id']
            exact = f"{package}/{m['version']}".lower() in restored
            if package.lower() in allowed_build and exact:
                used['package_build_files'].add(package.lower())
                if package.lower() in ctx['every_build']:
                    ctx['used_every']['package_build_files'].add(package.lower())
                seen['imports'].add(package)
            else:
                why = ('it is not in the restore results (a PackageDownload, a NuGet MSBuild SDK or a hand-written '
                       'path), or not at the version restored' if not exact else
                       f'layers.json allows {name} package build files only from: '
                       f'{listing(row["package_build_files"] + sorted(ctx["every_build"]))}')
                out.add('BOO-REF-003', f'{name} imports build code from {package}', f'{name}\'s build imports '
                        f'{imported}; {why}.', place)
        elif imported.startswith(('<', '/', '\\')) or re.match(r'^[A-Za-z]:', imported) or imported.startswith('../'):
            out.add('BOO-REF-003', f'{name} imports {imported}', f'{name}\'s build imports {imported}, which is '
                    'neither in the repository, nor the SDK, nor an allowed package.', place)
        # else: a file in this repository (Directory.Build.props, obj/*.nuget.g.props, ...), reviewed in the diff

    for profile, how in frameworks:
        # WPF and WinForms are profiles of the one WindowsDesktop framework (same reference pack); WPF's markup
        # project names the profile it needs.
        framework = PROFILES.get(profile.lower(), profile)
        seen['frameworks'].add(framework)
        if framework.lower() in allowed_frameworks:
            used['framework_references'].add(framework.lower())
        else:
            out.add('BOO-REF-005', f'{name} uses {framework}', f'{name} references framework {framework} ({how}); '
                    f'layers.json allows {name} only: {listing(row["framework_references"])}.', place)

    for target, how in edges:
        target_name = by_path.get(target.lower())
        if target_name is None:
            out.add('BOO-REF-002', f'{name} -> {target}', f'{name} references {target}, which is not a project in '
                    'src/ (only src/<Name>/<Name>.csproj projects may be referenced from src/).', f'{place}: {how}')
        elif target_name not in row['projects']:
            out.add('BOO-REF-001', f'{name} -> {target_name}', f'{name} references {target_name}; layers.json allows '
                    f'{name} only: {listing(row["projects"])}.', f'{place}: {how}')
        else:
            used['projects'].add(target_name.lower())
            seen['projects'].add(target_name)


def listing(names):
    return ', '.join(names) if names else 'none'


# ---------------------------------------------------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------------------------------------------------
def cmd_judge(facts_path, base, layers_path, rise_only=False, pr_layers_path=None):
    label = 'ARCH REFS (the base branch\'s own script and layers.json)' if rise_only else 'ARCH REFS'
    try:
        layers = load_json(layers_path, 'the rule file')
        facts = check_facts(load_json(facts_path, 'the build facts'))
        findings, summary = judge(layers, project_files(base), facts, rise_only)
        if pr_layers_path is not None:
            findings += looser(layers, load_json(pr_layers_path, 'the PR rule file'))
    except ArchError as error:
        print(f'{label} FAILED: {error}')
        return 1
    if findings:
        print(f'{label} FAILED: {len(findings)} finding(s) in the shipped build\'s facts. Each one says what to fix.\n')
        print('\n\n'.join(f.text() for f in findings))
        return 1
    units = sum(len(s['units']) for s in summary.values())
    count = lambda key: sum(len(s[key]) for s in summary.values())  # noqa: E731
    print(f'{label} OK: {len(summary)} src/ projects in {units} units of the build and publish logs; '
          f'{count("projects")} project references, {count("packages")} packages, {count("frameworks")} framework '
          f'references, {count("imports")} package build-file imports, {count("downloads")} SDK pack downloads, all '
          f'allowed by {layers_path.name}' + ('' if rise_only else ' and all of it in use')
          + ' (read from the shipped build\'s facts)'
          + ('; the PR\'s layers.json allows nothing more' if pr_layers_path is not None else ''))
    return 0


def cmd_show(facts_path, base, layers_path):
    try:
        layers = load_json(layers_path, 'the rule file')
        facts = check_facts(load_json(facts_path, 'the build facts'))
        findings, summary = judge(layers, project_files(base), facts)
    except ArchError as error:
        print(f'ARCH REFS FAILED: {error}')
        return 1
    for name, seen in sorted(summary.items()):
        print(name)
        for key in ('projects', 'packages', 'frameworks', 'imports', 'downloads'):
            print(f'  {key:10} {listing(sorted(seen[key]))}')
        for place in seen['units']:
            print(f'  unit       {place}')
    print(f'{len(findings)} finding(s); run `judge` for the details.')
    return 0


def cmd_format(layers_path):
    try:
        findings = validate_layers(load_json(layers_path, 'the rule file'))
    except ArchError as error:
        print(f'ARCH REFS FORMAT FAILED: {error}')
        return 1
    if findings:
        print(f'ARCH REFS FORMAT FAILED: {len(findings)} finding(s).\n')
        print('\n\n'.join(f.text() for f in findings))
        return 1
    print(f'ARCH REFS FORMAT OK: {layers_path.name} is well formed (known keys, complete rows, no self edge, no cycle)')
    return 0


# ---------------------------------------------------------------------------------------------------------------------
# Self-test: planted facts in the shape of today's shipped build, each mutated the way a bypass would.
# ---------------------------------------------------------------------------------------------------------------------
WIN = 'net10.0-windows10.0.19041.0'
NETCORE, WPF, WINSDK = 'Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App', 'Microsoft.Windows.SDK.NET.Ref.Windows'
PACKS = ['Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64',
         'Microsoft.Windows.SDK.NET.Ref']
SQLITE_TARGETS = '<nuget>/sqlitepclraw.lib.e_sqlite3/1.0.0/buildTransitive/net9.0/SQLitePCLRaw.lib.e_sqlite3.targets'
SHARED_PROPS = '<nuget>/shared.analyzer/1.0.0/buildTransitive/Shared.Analyzer.props'


def planted_layers():
    return {
        'sdk_packs': list(PACKS),
        'all_src_projects': {'packages': ['Shared.Analyzer'], 'package_build_files': ['Shared.Analyzer']},
        'projects': {
            'Boo.Core': {'target_frameworks': ['net10.0'], 'projects': [], 'packages': [], 'package_build_files': [],
                         'framework_references': [NETCORE]},
            'Boo.Providers': {'target_frameworks': ['net10.0'], 'projects': ['Boo.Core'], 'packages': [],
                              'package_build_files': [], 'framework_references': [NETCORE]},
            'Boo.Storage': {'target_frameworks': [WIN], 'projects': ['Boo.Core'], 'packages': ['Microsoft.Data.Sqlite'],
                            'package_build_files': ['SQLitePCLRaw.lib.e_sqlite3'],
                            'framework_references': [NETCORE, WINSDK]},
            'Boo.Windows': {'target_frameworks': [WIN], 'projects': ['Boo.Core'], 'packages': ['NAudio'],
                            'package_build_files': [], 'framework_references': [NETCORE, WINSDK, WPF]},
            'Boo.App': {'target_frameworks': [WIN], 'projects': ['Boo.Core', 'Boo.Windows', 'Boo.Providers',
                                                                 'Boo.Storage'],
                        'packages': ['Velopack'], 'package_build_files': ['SQLitePCLRaw.lib.e_sqlite3'],
                        'framework_references': [NETCORE, WINSDK, WPF]},
        }}


def planted_unit(name, tfm, rid='', refs=(), packages=(), frameworks=(NETCORE,), libraries=(), imports=(),
                 compiled=True, wpftmp=False):
    project = f'src/{name}/{name}_wpftmp.csproj' if wpftmp else f'src/{name}/{name}.csproj'
    windows = tfm != 'net10.0'
    downloads = ['Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64'] + (
        ['Microsoft.Windows.SDK.NET.Ref'] if windows else [])
    csc_refs = [{'origin': 'frameworkReference', 'project': None, 'package': f'{f}/10.0.14',
                 'path': f'<dotnet>/packs/{f}.Ref/10.0.14/ref/net10.0/X.dll'} for f in frameworks]
    csc_refs += [{'origin': 'projectReference', 'project': f'src/{r}/{r}.csproj', 'package': None,
                  'path': f'src/{r}/obj/Release/net10.0/ref/{r}.dll'} for r in refs]
    csc_refs += [{'origin': 'packageReference', 'project': None, 'package': f'{p}/1.0.0',
                  'path': f'<nuget>/{p.lower()}/1.0.0/lib/net8.0/{p}.dll'} for p in libraries]
    if wpftmp:  # WPF's markup compile passes plain paths without metadata
        csc_refs = [{'origin': 'reference', 'project': None, 'package': None,
                     'path': f'src/{r["project"].split("/")[1]}/bin/Release/net10.0/{r["project"].split("/")[1]}.dll'
                     if r['origin'] == 'projectReference' else r['path']} for r in csc_refs]
    analyzers = ['<dotnet>/sdk/10.0.403/Sdks/Microsoft.NET.Sdk/analyzers/Microsoft.CodeAnalysis.NetAnalyzers.dll']
    if windows:
        analyzers.append('<nuget>/microsoft.windows.sdk.net.ref/10.0.19041.57/analyzers/dotnet/cs/WinRT.SourceGenerator.dll')
    return {
        'project': project, 'targetFramework': tfm, 'runtimeIdentifier': rid, 'configuration': 'Release',
        'imports': ['<dotnet>/sdk/10.0.403/Sdks/Microsoft.NET.Sdk/Sdk/Sdk.props <= ' + project,
                    'Directory.Build.props <= <dotnet>/sdk/10.0.403/Current/Microsoft.Common.props'] + list(imports),
        'packageReferences': [f'{p} 1.0.0' for p in packages], 'packageDownloads': [],
        'projectReferenceItems': [f'src/{r}/{r}.csproj' for r in refs],
        'projectReferencesFollowed': [] if wpftmp else [f'src/{r}/{r}.csproj' for r in refs],
        'referenceItems': [], 'frameworkReferences': list(frameworks),
        'assets': {'path': f'src/{name}/obj/project.assets.json',
                   'libraries': sorted([f'{p}/1.0.0 (package)' for p in libraries]
                                       + [f'{r}/1.0.0 (project ../{r}/{r}.csproj)' for r in refs]),
                   'directDependencies': [f'{tfm}: {p} [1.0.0, )' for p in packages],
                   'downloadDependencies': [f'{tfm}: {d} [10.0.14, 10.0.14]' for d in downloads]},
        'csc': [{'references': csc_refs, 'analyzers': analyzers}] if compiled else []}


def planted_facts():
    sqlite = ['Microsoft.Data.Sqlite', 'Microsoft.Data.Sqlite.Core', 'SQLitePCLRaw.lib.e_sqlite3']
    app = dict(refs=['Boo.Core', 'Boo.Providers', 'Boo.Storage', 'Boo.Windows'], packages=['Velopack'],
               frameworks=[NETCORE, WINSDK, WPF], libraries=['Velopack', 'NAudio'] + sqlite,
               imports=[SQLITE_TARGETS + ' <= src/Boo.App/obj/Boo.App.csproj.nuget.g.targets'])
    others = [
        ('Boo.Core', 'net10.0', dict(packages=['Shared.Analyzer'], libraries=['Shared.Analyzer'],
                                     imports=[SHARED_PROPS + ' <= src/Boo.Core/obj/Boo.Core.csproj.nuget.g.props'])),
        ('Boo.Providers', 'net10.0', dict(refs=['Boo.Core'])),
        ('Boo.Storage', WIN, dict(refs=['Boo.Core'], packages=['Microsoft.Data.Sqlite'], frameworks=[NETCORE, WINSDK],
                                  libraries=sqlite,
                                  imports=[SQLITE_TARGETS + ' <= src/Boo.Storage/obj/Boo.Storage.csproj.nuget.g.targets'])),
        ('Boo.Windows', WIN, dict(refs=['Boo.Core'], packages=['NAudio'], frameworks=[NETCORE, WINSDK, WPF],
                                  libraries=['NAudio'])),
    ]
    build = [planted_unit('Boo.App', WIN, **app), planted_unit('Boo.App', WIN, wpftmp=True, **app)]
    build += [planted_unit(n, t, **kw) for n, t, kw in others]
    build.append({**planted_unit('Boo.Core', 'net10.0'), 'project': 'tests/Boo.Core.Tests/Boo.Core.Tests.csproj'})
    markup = planted_unit('Boo.App', WIN, rid='win-x64', wpftmp=True, **app)
    # WPF's markup project of the publish, as the real facts show it: its references as <Reference> items with hint
    # paths into the restored packages, and the WinForms profile of the WindowsDesktop framework.
    markup['referenceItems'] = [f'<nuget>/{p.lower()}\\1.0.0\\lib\\{p}.dll hint=<nuget>/{p.lower()}/1.0.0/lib/{p}.dll'
                                for p in app['libraries']]
    markup['frameworkReferences'].append('Microsoft.WindowsDesktop.App.WindowsForms')
    publish = [planted_unit('Boo.App', WIN, rid='win-x64', **app), markup]
    publish += [planted_unit(n, t, rid=r, compiled=False, **kw) for n, t, kw in others for r in ('', 'win-x64')]
    return {'schema': SCHEMA, 'binlogs': [{'label': 'build', 'succeeded': True, 'units': build},
                                          {'label': 'publish', 'succeeded': True, 'units': publish}]}


PLANTED_PATHS = [f'src/{n}/{n}.csproj' for n in ('Boo.App', 'Boo.Core', 'Boo.Providers', 'Boo.Storage', 'Boo.Windows')]


def units_of(facts, name, label=None, wpftmp=False):
    want = f'src/{name}/{name}_wpftmp.csproj' if wpftmp else f'src/{name}/{name}.csproj'
    return [u for b in facts['binlogs'] if label in (None, b['label']) for u in b['units'] if u['project'] == want]


def logic_cases():
    """(name, mutate(layers, paths, facts) in place, expected rule ids[, rise_only])."""
    def each(name, fn, label=None, wpftmp=False):
        def run(layers, paths, facts):
            for unit in units_of(facts, name, label, wpftmp):
                fn(unit)
        return run

    def edge_item(target):
        return lambda u: u['projectReferenceItems'].append(target)

    def edge_followed(target):
        def fn(u):
            u['projectReferencesFollowed'].append(target)
            for call in u['csc']:
                call['references'].append({'origin': 'projectReference', 'project': target, 'package': None,
                                           'path': target.rsplit('/', 1)[0] + '/obj/ref/x.dll'})
        return fn

    def csc_ref(path, origin='reference', package=None):
        def fn(u):
            for call in u['csc']:
                call['references'].append({'origin': origin, 'project': None, 'package': package, 'path': path})
        return fn

    def append(key, value):
        return lambda u: u[key].append(value)

    def assets(key, value):
        return lambda u: u['assets'][key].append(value)

    def analyzer(path):
        def fn(u):
            for call in u['csc']:
                call['analyzers'].append(path)
        return fn

    def set_layers(fn):
        return lambda layers, paths, facts: fn(layers)

    def both(*fns):
        def run(layers, paths, facts):
            for fn in fns:
                fn(layers, paths, facts)
        return run

    def new_file(path):
        return lambda layers, paths, facts: paths.append(path)

    def new_unit(project):
        def run(layers, paths, facts):
            unit = planted_unit('Boo.Providers', 'net10.0')
            unit['project'] = project
            facts['binlogs'][0]['units'].append(unit)
        return run

    def drop_units(name):
        def run(layers, paths, facts):
            for b in facts['binlogs']:
                b['units'] = [u for u in b['units'] if not u['project'].startswith(f'src/{name}/')]
        return run

    def drop_project(name):
        def run(layers, paths, facts):
            paths.remove(f'src/{name}/{name}.csproj')
            drop_units(name)(layers, paths, facts)
            for b in facts['binlogs']:
                for u in b['units']:
                    u['projectReferenceItems'] = [r for r in u['projectReferenceItems'] if name not in r]
                    u['projectReferencesFollowed'] = [r for r in u['projectReferencesFollowed'] if name not in r]
                    u['assets']['libraries'] = [r for r in u['assets']['libraries'] if not r.startswith(name + '/')]
                    for call in u['csc']:
                        call['references'] = [r for r in call['references'] if name not in r['path']]
        return run

    def add_unit(label, unit):
        return lambda layers, paths, facts: next(b for b in facts['binlogs'] if b['label'] == label)['units'].append(unit)

    providers = 'src/Boo.Providers/Boo.Providers.csproj'
    sdk_import = '<nuget>/microsoft.build.artifacts/6.1.63/Sdk/Sdk.props <= src/Boo.Core/Boo.Core.csproj'
    download_import = ('<nuget>/microsoft.sourcelink.common/8.0.0/build/Microsoft.SourceLink.Common.props '
                       '<= src/Boo.Core/Boo.Core.csproj')
    return [
        ('today\'s shape passes', lambda *a: None, []),
        # BOO-REF-001: the same forbidden edge, wherever the real build shows it
        ('Boo.Windows -> Boo.Providers as a ProjectReference item fails', each('Boo.Windows', edge_item(providers)),
         ['BOO-REF-001']),
        ('the edge only in the publish log (RuntimeIdentifier, _IsPublishing) fails',
         each('Boo.Windows', edge_item(providers), 'publish'), ['BOO-REF-001']),
        ('the edge only in the build log (Configuration, SolutionName) fails',
         each('Boo.Windows', edge_item(providers), 'build'), ['BOO-REF-001']),
        ('the edge only followed into the compile (added by a target) fails',
         each('Boo.Windows', edge_followed(providers), 'build'), ['BOO-REF-001']),
        ('the edge only in the restore results fails',
         each('Boo.Windows', assets('libraries', 'Boo.Providers/1.0.0 (project ../Boo.Providers/Boo.Providers.csproj)')),
         ['BOO-REF-001']),
        ('the edge as a plain path to the other project\'s DLL fails',
         each('Boo.Windows', csc_ref('src/Boo.Providers/bin/Release/net10.0/Boo.Providers.dll'), 'build'),
         ['BOO-REF-001']),
        ('a self reference fails', each('Boo.Core', edge_item('src/Boo.Core/Boo.Core.csproj')), ['BOO-REF-001']),
        ('a markup-compile path to a forbidden project fails',
         each('Boo.Windows', csc_ref('src/Boo.App/bin/Release/Boo.App.dll')), ['BOO-REF-001']),
        # BOO-REF-002
        ('a reference into tools/ fails', each('Boo.App', edge_item('tools/Boo.LiveProbe/Boo.LiveProbe.csproj')),
         ['BOO-REF-002']),
        ('a reference into tests/ fails', each('Boo.Core', edge_followed('tests/Boo.Core.Tests/Boo.Core.Tests.csproj')),
         ['BOO-REF-002']),
        ('a reference outside the repository fails', each('Boo.Core', edge_item('<repo-parent-1>/X/X.csproj')),
         ['BOO-REF-002']),
        ('a reference to a nested src/ project fails', each('Boo.App', edge_item('src/Group/Hack/Hack.csproj')),
         ['BOO-REF-002']),
        # BOO-REF-003: packages, downloads, direct assemblies, analyzers, build code
        ('a package not allowed fails', each('Boo.Core', append('packageReferences', 'System.Net.Http.Json 9.0.0')),
         ['BOO-REF-003']),
        ('a package only in the restore results (added by a target before restore) fails',
         each('Boo.Core', assets('directDependencies', 'net10.0: Newtonsoft.Json [13.0.3, )'), 'publish'),
         ['BOO-REF-003']),
        ('a package allowed in another layer fails here', each('Boo.Core', append('packageReferences', 'NAudio 2.2.1')),
         ['BOO-REF-003']),
        ('a package allowed for every project passes anywhere',
         both(each('Boo.Providers', append('packageReferences', 'Shared.Analyzer 1.0.0')),
              each('Boo.Providers', assets('libraries', 'Shared.Analyzer/1.0.0 (package)')),
              each('Boo.Providers', append('imports', SHARED_PROPS + ' <= x'))), []),
        ('PackageDownload of a package fails',
         each('Boo.Core', append('packageDownloads', 'Microsoft.SourceLink.Common [8.0.0]')), ['BOO-REF-003']),
        ('a download only in the restore results fails',
         each('Boo.Core', assets('downloadDependencies', 'net10.0: Evil.Tasks [1.0.0, 1.0.0]')), ['BOO-REF-003']),
        ('PackageDownload + Exists() import fails twice (the download and the import)',
         both(each('Boo.Core', append('packageDownloads', 'Microsoft.SourceLink.Common [8.0.0]')),
              each('Boo.Core', append('imports', download_import), 'build')), ['BOO-REF-003', 'BOO-REF-003']),
        ('a NuGet MSBuild SDK import fails', each('Boo.Core', append('imports', sdk_import), 'build'),
         ['BOO-REF-003']),
        ('an allowed package\'s build files in a project that does not restore it fail',
         each('Boo.Core', append('imports', SQLITE_TARGETS + ' <= src/Boo.Core/obj/x.nuget.g.targets')),
         ['BOO-REF-003']),
        ('build files of an allowed package at a version the project does not restore fail',
         each('Boo.Storage', append('imports', '<nuget>/sqlitepclraw.lib.e_sqlite3/9.9.9/build/x.targets <= x')),
         ['BOO-REF-003']),
        ('a file of a restored package at another version fails',
         each('Boo.Windows', csc_ref('<nuget>/naudio/9.9.9/lib/net8.0/NAudio.dll'), 'build'), ['BOO-REF-003']),
        ('build files of a restored package that is not allowed fail',
         each('Boo.Windows', append('imports', '<nuget>/naudio/2.2.1/build/NAudio.targets <= x')), ['BOO-REF-003']),
        ('an import from outside the repository fails',
         each('Boo.Core', append('imports', '<home>/evil.targets <= Directory.Build.props')), ['BOO-REF-003']),
        ('a repository build file passes',
         each('Boo.Core', append('imports', 'build/extra.props <= Directory.Build.props')), []),
        ('a direct <Reference> to a DLL in the repository fails',
         each('Boo.Core', append('referenceItems', 'x hint=lib/x.dll')), ['BOO-REF-003']),
        ('a direct <Reference> by name only fails', each('Boo.Core', append('referenceItems', 'System.Windows.Forms')),
         ['BOO-REF-003']),
        ('a <Reference> to a file of a package the project does not restore fails',
         each('Boo.Core', append('referenceItems', 'NAudio hint=<nuget>/naudio/2.2.1/lib/net6.0-windows7.0/NAudio.dll')),
         ['BOO-REF-003']),
        ('a <Reference> to another src/ project\'s output fails like the edge',
         each('Boo.Windows', append('referenceItems', 'src\\Boo.Providers\\bin\\Release\\net10.0\\Boo.Providers.dll')),
         ['BOO-REF-001']),
        ('the WPF profile of the WindowsDesktop framework in Core fails',
         each('Boo.Core', append('frameworkReferences', 'Microsoft.WindowsDesktop.App.WPF')), ['BOO-REF-005']),
        ('a compile reference to a DLL elsewhere in the repository fails',
         each('Boo.Core', csc_ref('tools/Boo.LiveProbe/bin/Release/Boo.LiveProbe.dll'), 'build'), ['BOO-REF-003']),
        ('a compile reference to another DLL in a src/ output folder fails',
         each('Boo.Core', csc_ref('src/Boo.Providers/obj/Release/Interop.Evil.dll'), 'build'), ['BOO-REF-003']),
        ('a compile reference to an unrestored NuGet DLL fails',
         each('Boo.Core', csc_ref('<nuget>/evil/1.0.0/lib/net8.0/Evil.dll'), 'build'), ['BOO-REF-003']),
        ('a compile reference claiming an unrestored package fails',
         each('Boo.Core', csc_ref('<nuget>/evil/1.0.0/lib/Evil.dll', 'packageReference', 'Evil/1.0.0'), 'build'),
         ['BOO-REF-003']),
        ('a compile reference into the SDK folder fails',
         each('Boo.Core', csc_ref('<dotnet>/sdk/10.0.403/Microsoft.Build.dll'), 'build'), ['BOO-REF-003']),
        ('a compile reference with an unknown origin fails',
         each('Boo.Core', csc_ref('x.dll', 'magic'), 'build'), ['BOO-REF-003']),
        ('an analyzer from the repository fails', each('Boo.Core', analyzer('src/Boo.Core/evil.dll'), 'build'),
         ['BOO-REF-003']),
        ('an analyzer from an unrestored package fails',
         each('Boo.Core', analyzer('<nuget>/evil/1.0.0/analyzers/Evil.dll'), 'build'), ['BOO-REF-003']),
        ('an SDK pack analyzer the project does not download fails',
         each('Boo.Core', analyzer('<nuget>/microsoft.windows.sdk.net.ref/10.0.19041.57/analyzers/W.dll'), 'build'),
         ['BOO-REF-003']),
        ('a restored library of an unknown type fails',
         each('Boo.Core', assets('libraries', 'X/1.0.0 (msbuildproject)')), ['BOO-REF-003']),
        # BOO-REF-004
        ('a new src/ project file without a row fails', new_file('src/Boo.Extra/Boo.Extra.csproj'), ['BOO-REF-004']),
        ('a project nested deeper fails', new_file('src/Group/Hack/Hack.csproj'), ['BOO-REF-004']),
        ('a project whose name differs from its folder fails', new_file('src/Boo.Core/Other.csproj'), ['BOO-REF-004']),
        ('a .vbproj in src/ fails', new_file('src/Boo.Vb/Boo.Vb.vbproj'), ['BOO-REF-004']),
        ('a src/ unit in the facts that git does not list fails', new_unit('src/Boo.Gen/Boo.Gen.csproj'),
         ['BOO-REF-004']),
        ('a fake WPF markup project of an undeclared project fails', new_unit('src/Extra/Extra_wpftmp.csproj'),
         ['BOO-REF-004']),
        ('a declared project missing from the build facts fails', drop_units('Boo.Providers'), ['BOO-REF-004']),
        ('a unit without restore results fails (and so does its package build file)',
         each('Boo.Core', lambda u: u.update(assets=None), 'publish'), ['BOO-REF-004', 'BOO-REF-003']),
        # BOO-REF-005
        ('Core moved to -windows fails', each('Boo.Core', lambda u: u.update(targetFramework=WIN)), ['BOO-REF-005']),
        ('Core with a second target framework in the publish only fails',
         add_unit('publish', planted_unit('Boo.Core', 'net9.0', compiled=False, packages=['Shared.Analyzer'],
                                          libraries=['Shared.Analyzer'])), ['BOO-REF-005']),
        ('WPF framework in Core fails', each('Boo.Core', append('frameworkReferences', WPF)), ['BOO-REF-005']),
        ('a WPF reference-pack DLL compiled into Core fails',
         each('Boo.Core', csc_ref('<dotnet>/packs/Microsoft.WindowsDesktop.App.Ref/10.0.14/ref/net10.0/X.dll'),
              'build'), ['BOO-REF-005']),
        # BOO-REF-006
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
        ('a missing key fails', set_layers(lambda l: l['projects']['Boo.Core'].pop('package_build_files')),
         ['BOO-REF-006']),
        ('an empty target_frameworks fails',
         set_layers(lambda l: l['projects']['Boo.Core'].update(target_frameworks=[])), ['BOO-REF-006']),
        ('a duplicate name (any case) fails',
         set_layers(lambda l: l['projects']['Boo.Windows']['packages'].append('naudio')), ['BOO-REF-006']),
        ('a missing sdk_packs fails', set_layers(lambda l: l.pop('sdk_packs')), ['BOO-REF-006']),
        # BOO-REF-007
        ('an unused allowance fails (lock the gain in)',
         set_layers(lambda l: l['projects']['Boo.Providers']['packages'].append('Unused')), ['BOO-REF-007']),
        ('an unused package build-file allowance fails',
         set_layers(lambda l: l['projects']['Boo.Windows']['package_build_files'].append('NAudio')), ['BOO-REF-007']),
        ('an unused everywhere package fails',
         set_layers(lambda l: l['all_src_projects']['packages'].append('Unused')), ['BOO-REF-007']),
        ('an unused SDK pack fails',
         set_layers(lambda l: l['sdk_packs'].append('Microsoft.NETCore.App.Crossgen2.win-x64')), ['BOO-REF-007']),
        ('a declared project that is gone fails', drop_project('Boo.Providers'),
         ['BOO-REF-007', 'BOO-REF-007']),  # its row, and App's now-unused allowance
        ('rise-only skips unused allowances', drop_project('Boo.Providers'), [], True),
        ('rise-only still fails a forbidden edge', each('Boo.Windows', edge_item(providers), 'publish'),
         ['BOO-REF-001'], True),
    ]


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
        ('a new package build-file allowance fails',
         changed(lambda l: l['projects']['Boo.Core']['package_build_files'].append('X')), ['BOO-REF-008']),
        ('a new everywhere package fails', changed(lambda l: l['all_src_projects']['packages'].append('X')),
         ['BOO-REF-008']),
        ('a new SDK pack fails', changed(lambda l: l['sdk_packs'].append('Microsoft.Build.Artifacts')),
         ['BOO-REF-008']),
        ('a new framework fails',
         changed(lambda l: l['projects']['Boo.Core']['framework_references'].append(WPF)), ['BOO-REF-008']),
        ('a new project fails', changed(lambda l: l['projects'].update({'Boo.New': {
            'target_frameworks': ['net10.0'], 'projects': [], 'packages': [], 'package_build_files': [],
            'framework_references': []}})), ['BOO-REF-008']),
        ('a changed target fails', changed(lambda l: l['projects']['Boo.Core'].update(target_frameworks=[WIN])),
         ['BOO-REF-008']),
        ('a broken PR file fails', changed(lambda l: l['projects']['Boo.Core'].update(x=1)), ['BOO-REF-006']),
    ]


def fact_errors():
    """(name, mutate(facts)): facts the judge must refuse to read."""
    def drop_log(label):
        return lambda f: f.update(binlogs=[b for b in f['binlogs'] if b['label'] != label])
    return [
        ('no publish log', drop_log('publish')),
        ('no build log', drop_log('build')),
        ('two build logs', lambda f: f['binlogs'].append(dict(f['binlogs'][0]))),
        ('a failed build', lambda f: f['binlogs'][0].update(succeeded=False)),
        ('another schema', lambda f: f.update(schema='other/1')),
        ('a unit without imports', lambda f: f['binlogs'][1]['units'][0].pop('imports')),
    ]


def self_test():
    failures = []
    if judge(planted_layers(), list(PLANTED_PATHS), planted_facts())[0]:
        failures.append('planted: today\'s shape must pass before anything is mutated: '
                        + '; '.join(f'{f.rule_id} {f.subject}' for f in judge(planted_layers(), list(PLANTED_PATHS),
                                                                               planted_facts())[0]))
    for name, mutate, expected, *rise_only in logic_cases():
        layers, paths, facts = planted_layers(), list(PLANTED_PATHS), planted_facts()
        mutate(layers, paths, facts)
        got = sorted(f.rule_id for f in judge(layers, paths, facts, bool(rise_only))[0])
        if got != sorted(expected):
            failures.append(f'logic: {name}: expected {expected}, got {got}')
    for name, pr_layers, expected in looser_cases():
        got = sorted(f.rule_id for f in looser(planted_layers(), pr_layers))
        if got != sorted(expected):
            failures.append(f'base check: {name}: expected {expected}, got {got}')
    for name, mutate in fact_errors():
        facts = planted_facts()
        mutate(facts)
        try:
            check_facts(facts)
            failures.append(f'facts: {name}: was read; it must fail closed')
        except ArchError:
            pass
    findings, _ = judge(planted_layers(), list(PLANTED_PATHS) + ['src/Boo.Extra/Boo.Extra.csproj'], planted_facts())
    for finding in findings:
        text = finding.text()
        if not all(f'\n  {label}' in text for label in ('What:', 'Why:', 'Fix:', 'Do NOT:', 'Rule:', 'Where:')):
            failures.append(f'message: {finding.rule_id} lacks What/Why/Fix/Do NOT/Rule/Where')
    facts = planted_facts()
    for unit in units_of(facts, 'Boo.Windows'):
        unit['projectReferenceItems'].append('src/Boo.Providers/Boo.Providers.csproj')
    merged = [f for f in judge(planted_layers(), list(PLANTED_PATHS), facts)[0] if f.rule_id == 'BOO-REF-001']
    if len(merged) != 1 or len(merged[0].wheres) != 3:
        failures.append(f'merge: one edge in 3 units must be one finding listing 3 places, got '
                        f'{[(f.subject, len(f.wheres)) for f in merged]}')
    try:
        if validate_layers(load_json(LAYERS, 'the rule file')):
            failures.append(f'{LAYERS.name}: the committed rule file is not well formed (run `format`)')
    except ArchError as error:
        failures.append(str(error))
    if failures:
        print('SELF-TEST FAILED:\n  ' + '\n  '.join(failures))
        return 1
    print(f'SELF-TEST OK: {len(logic_cases())} planted builds (the review\'s bypasses: conditions seen in one log only, '
          f'edges added by targets, restore-only packages, PackageDownload + import, NuGet MSBuild SDK, plain DLL '
          f'paths), {len(looser_cases())} base-branch loosening cases, {len(fact_errors())} unreadable facts refused, '
          f'{len(RULES)} rules')
    return 0


def argument(argv, flag):
    if flag in argv:
        index = argv.index(flag)
        if index + 1 >= len(argv):
            raise SystemExit(f'{flag} needs a value')
        return argv[index + 1]
    return None


USAGE = ('usage: arch_refs.py judge FACTS [--root DIR] [--layers FILE] [--rise-only] [--pr-layers FILE]\n'
         '       arch_refs.py show FACTS [--root DIR] [--layers FILE]\n'
         '       arch_refs.py format [--layers FILE]\n'
         '       arch_refs.py --self-test')


def main(argv):
    if '--self-test' in argv:
        return self_test()
    base = pathlib.Path(argument(argv, '--root')).resolve() if argument(argv, '--root') else root
    layers = pathlib.Path(argument(argv, '--layers')).resolve() if argument(argv, '--layers') else LAYERS
    pr_layers = argument(argv, '--pr-layers')
    if argv[:1] == ['format']:
        return cmd_format(layers)
    if argv[:1] in (['judge'], ['show']) and len(argv) > 1 and not argv[1].startswith('--'):
        facts = pathlib.Path(argv[1]).resolve()
        if argv[0] == 'show':
            return cmd_show(facts, base, layers)
        return cmd_judge(facts, base, layers, rise_only='--rise-only' in argv,
                         pr_layers_path=pathlib.Path(pr_layers).resolve() if pr_layers else None)
    print(USAGE, file=sys.stderr)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
