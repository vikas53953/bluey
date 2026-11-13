"""Banned APIs (RS0030): prove the ban list is complete, that every line fires, and judge the SHIPPED build.

  python -I scripts/quality/banned_apis.py generate     rewrite src/BannedSymbols.txt from canary/banned-groups.txt
  python -I scripts/quality/banned_apis.py list-check   CI: regenerate from the reference packs; fail on any difference
  python -I scripts/quality/banned_apis.py canary       CI: each banned line fires exactly once in the canary project
  python -I scripts/quality/banned_apis.py judge FACTS  windows-ci: judge every compile of a src/ project recorded in
                                                        build-facts.json (the real Release build and win-x64 publish):
                                                        what the compiler got, and RS0030 results against the baselines
  python -I scripts/quality/banned_apis.py format       CI: ban list and AnalyzerBaseline.cs format; RS0030 named only there
  python -I scripts/quality/banned_apis.py --self-test  CI: prove the judging logic on planted samples
  python -I <base copy of banned_apis.py> rise-check --root . --base-rev <git rev>
                                                        CI on a pull request: the BASE branch's copy fails if the PR
                                                        removed a banned line, added a baseline entry or raised a count.

generate / list-check / canary need the .NET SDK pinned in global.json (any OS: WPF and Windows Forms compile on Linux
with EnableWindowsTargeting). judge reads only build-facts.json (and the repository's own analyzer config files).
Exit 1 on a finding, 2 when the tool itself could not run. Rule texts: docs/quality/RULES.md#banned-apis-rs0030.
"""
import difflib
import json
import pathlib
import re
import subprocess
import sys
import tempfile
import urllib.parse
import urllib.request

root = pathlib.Path(__file__).resolve().parent.parent.parent
RULES_DOC = 'docs/quality/RULES.md#banned-apis-rs0030'
LIST = 'src/BannedSymbols.txt'
CANARY_DIR = 'scripts/quality/canary'
GROUPS = CANARY_DIR + '/banned-groups.txt'
CANARY_PROJECT = CANARY_DIR + '/Canary.csproj'
LISTGEN_PROJECT = CANARY_DIR + '/ListGen/ListGen.csproj'
CANARY_FILES = [CANARY_DIR + '/Canary.Core.cs', CANARY_DIR + '/Canary.Wpf.cs', CANARY_DIR + '/Canary.WinForms.cs']
BASELINE_FILE = 'AnalyzerBaseline.cs'
# Linux needs EnableWindowsTargeting for WPF; PublishReadyToRun only matters for publish but would make a Linux build
# look for a linux-x64 Windows Desktop runtime pack. Neither changes what the compiler sees.
BUILD_PROPS = ['-c', 'Release', '-nologo', '-p:EnableWindowsTargeting=true', '-p:PublishReadyToRun=false']

MARKER = re.compile(r'//\s*BAN\s+(\S+)\s*$')
RULE_IN_MESSAGE = re.compile(r'^(BOO-[A-Z]+-\d{3}) ')
DIAGNOSTIC_MESSAGE = re.compile(r"^The symbol '.*' is banned in this project: (.*)$", re.S)
ENTRY = re.compile(r'^\[assembly: SuppressMessage\("ApiDesign", "RS0030", Scope = "member", '
                   r'Target = "(?P<target>~[MPFE]:[^"]+)", Justification = "(?P<justification>[^"]+)"\)\]$')
JUSTIFICATION = re.compile(r'^Baselined by PR 1\.3; cleanup (?P<cleanup>[^;]+); (?P<counts>[^;]+); (?P<target>~[MPFE]:.+)$')
COUNT = re.compile(r'^(BOO-[A-Z]+-\d{3}) x([1-9]\d*)$')
BASELINE_HEADER = 'using System.Diagnostics.CodeAnalysis;'


class ToolError(Exception):
    """The check could not run (exit 2), as opposed to a finding (exit 1)."""


# ---------------------------------------------------------------------------------------------------------------------
# Pure parsing and judging (covered by --self-test).
# ---------------------------------------------------------------------------------------------------------------------
def parse_list(text):
    """[(doc id, message)] in file order. Raises ToolError on a malformed or duplicate line."""
    entries, seen = [], set()
    for number, raw in enumerate(text.splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith('#'):
            continue
        if ';' not in line:
            raise ToolError(f'{LIST}:{number}: a banned line needs "<doc id>;<message>"')
        doc_id, message = line.split(';', 1)
        if not re.match(r'^[MPTFEN]:\S+$', doc_id):
            raise ToolError(f'{LIST}:{number}: "{doc_id}" is not a documentation id')
        if not RULE_IN_MESSAGE.match(message):
            raise ToolError(f'{LIST}:{number}: the message must start with the rule id (BOO-XXX-NNN)')
        if doc_id in seen:
            raise ToolError(f'{LIST}:{number}: {doc_id} is listed twice')
        seen.add(doc_id)
        entries.append((doc_id, message))
    if not entries:
        raise ToolError(f'{LIST} lists no banned symbol')
    return entries


def parse_markers(files):
    """files: {relative path: text}. Returns ({(path, line): doc id}, problems). One marker per line, at its end."""
    markers, problems = {}, []
    for path, text in sorted(files.items()):
        for number, line in enumerate(text.splitlines(), 1):
            found = MARKER.search(line)
            if found:
                markers[(path, number)] = found.group(1)
            elif re.search(r'//\s*BAN\b', line):
                problems.append(f'{path}:{number}: malformed marker; write "// BAN <doc id>" at the end of the call line')
    return markers, problems


def judge_markers(listed, markers):
    """Every listed doc id has exactly one canary marker, and every marker names a listed doc id."""
    problems = []
    by_id = {}
    for place, doc_id in markers.items():
        by_id.setdefault(doc_id, []).append(place)
    for doc_id in listed:
        places = by_id.get(doc_id, [])
        if not places:
            problems.append(f'{doc_id}: no canary call. Add one line calling it, ending in "// BAN {doc_id}", to '
                            f'{CANARY_DIR}/Canary.*.cs.')
        elif len(places) > 1:
            problems.append(f'{doc_id}: {len(places)} canary calls ({fmt_places(places)}); keep exactly one.')
    for doc_id, places in sorted(by_id.items()):
        if doc_id not in listed:
            problems.append(f'{fmt_places(places)}: the marker names {doc_id}, which {LIST} does not list.')
    return problems


def judge_bindings(markers, refs):
    """refs: [(path, line, doc id)] = what the compiler says each canary line references. Each marker line must
    reference exactly its own banned symbol, once; no other line may reference a banned symbol."""
    problems = []
    at = {}
    for path, line, doc_id in refs:
        at.setdefault((path, line), []).append(doc_id)
    for place, doc_id in sorted(markers.items()):
        found = at.get(place, [])
        if found != [doc_id]:
            problems.append(f'{fmt_places([place])}: marked {doc_id}, but the call binds to '
                            f'{", ".join(found) if found else "no banned symbol"}. Fix the call so it binds to exactly '
                            f'that overload.')
    for place, found in sorted(at.items()):
        if place not in markers:
            problems.append(f'{fmt_places([place])}: references {", ".join(found)} without a "// BAN" marker.')
    return problems


def judge_firing(listed_messages, markers, results, expected_files=None):
    """results: SARIF results (see read_sarif). Each marker line in expected_files (all when None) must carry exactly
    one RS0030, reported as an unsuppressed error with that line's message; no RS0030 may appear on any other line of
    those files."""
    problems = []
    wanted = {place: doc_id for place, doc_id in markers.items() if expected_files is None or place[0] in expected_files}
    files = {place[0] for place in wanted} if expected_files is None else set(expected_files)
    fired = {}
    for result in results:
        if result['rule'] == 'RS0030' and result['file'] in files:
            fired.setdefault((result['file'], result['line']), []).append(result)
    for place, doc_id in sorted(wanted.items()):
        hits = fired.get(place, [])
        if len(hits) != 1:
            problems.append(f'{fmt_places([place])}: {doc_id} fired {len(hits)} times, expected exactly once. '
                            f'A banned line that does not fire protects nothing.')
            continue
        hit = hits[0]
        if hit['level'] != 'error' or hit['suppressions']:
            problems.append(f'{fmt_places([place])}: {doc_id} fired as {hit["level"]}'
                            f'{" (suppressed)" if hit["suppressions"] else ""}; it must be an unsuppressed error.')
        message = DIAGNOSTIC_MESSAGE.match(hit['message'])
        if not message or message.group(1) != listed_messages[doc_id]:
            problems.append(f'{fmt_places([place])}: the RS0030 message is not the one {LIST} gives {doc_id}.')
    for place, hits in sorted(fired.items()):
        if place not in wanted:
            problems.append(f'{fmt_places([place])}: {len(hits)} unexpected RS0030 on a line with no marker.')
    return problems


def parse_baseline(path, text):
    """{target: (justification, {rule: count})} from one AnalyzerBaseline.cs, and problems. The file format is strict
    (one using line, one attribute per line, nothing else) so what is read here is exactly what the compiler reads."""
    entries, problems = {}, []
    for number, raw in enumerate(text.splitlines(), 1):
        line = raw.strip()
        if not line or line == BASELINE_HEADER:
            continue
        entry = ENTRY.match(line)
        if not entry:
            problems.append(f'{path}:{number}: only `{BASELINE_HEADER}` and lines of the form [assembly: '
                            f'SuppressMessage("ApiDesign", "RS0030", Scope = "member", Target = "~M:...", Justification '
                            f'= "Baselined by PR 1.3; cleanup Cn; BOO-XXX-NNN xN; <same target>")] are allowed here.')
            continue
        target, justification = entry.group('target'), entry.group('justification')
        parsed = JUSTIFICATION.match(justification)
        counts = {}
        if parsed:
            for part in parsed.group('counts').split(', '):
                count = COUNT.match(part)
                if not count or count.group(1) in counts:
                    parsed = None
                    break
                counts[count.group(1)] = int(count.group(2))
        if not parsed or parsed.group('target') != target:
            problems.append(f'{path}:{number}: the justification must read "Baselined by PR 1.3; cleanup Cn; '
                            f'BOO-XXX-NNN xN[, BOO-YYY-NNN xN]; {target}" (it ends with the same target).')
            continue
        if target in entries:
            problems.append(f'{path}:{number}: {target} is baselined twice; keep one line per member.')
            continue
        entries[target] = (justification, counts)
    return entries, problems


def judge_baseline(project, entries, results):
    """Every RS0030 of a src/ compile must be suppressed by exactly one AnalyzerBaseline.cs entry of that project,
    and per entry and rule the number of hits must equal the declared count, both ways."""
    problems = []
    by_justification = {justification: target for target, (justification, _) in entries.items()}
    seen = {target: {} for target in entries}
    for result in results:
        if result['rule'] != 'RS0030':
            continue
        place = f'{result["file"]}:{result["line"]}'
        message = DIAGNOSTIC_MESSAGE.match(result['message'])
        rule = RULE_IN_MESSAGE.match(message.group(1)).group(1) if message and RULE_IN_MESSAGE.match(
            message.group(1)) else '?'
        suppressions = result['suppressions']
        if not suppressions:
            problems.append(f'{project} {place}: {rule} is not baselined (a new banned call). {fix_text(rule)}')
            continue
        target = by_justification.get(suppressions[0].get('justification', '')) if len(suppressions) == 1 else None
        if target is None or suppressions[0].get('type') != 'SuppressMessageAttribute':
            how = ', '.join(s.get('type', '?') for s in suppressions)
            problems.append(f'{project} {place}: {rule} is silenced by {how} outside {BASELINE_FILE}. Remove the '
                            f'suppression and fix the call; only {BASELINE_FILE} may baseline, and only today\'s hits.')
            continue
        seen[target][rule] = seen[target].get(rule, 0) + 1
    for target, (_, declared) in sorted(entries.items()):
        found = seen[target]
        for rule in sorted(set(declared) | set(found)):
            want, got = declared.get(rule, 0), found.get(rule, 0)
            if got > want:
                problems.append(f'{project} {target}: {rule} {want} -> {got}: a new banned call in an already '
                                f'baselined member. {fix_text(rule)} Do NOT raise the count.')
            elif got < want:
                problems.append(f'{project} {target}: {rule} {want} -> {got} (good news): lower the count in '
                                f'src/{project}/{BASELINE_FILE} (remove the line at 0) so the gain is locked in.')
    return problems


def judge_rise(base_list_ids, list_ids, base_entries, entries):
    """For a pull request, judged by the BASE branch's copy: no banned line may disappear, no baseline entry may
    appear, no declared count may rise. base_entries / entries: {project: {target: (justification, counts)}}."""
    problems = []
    for doc_id in sorted(set(base_list_ids) - set(list_ids)):
        problems.append(f'{LIST}: {doc_id} was removed. Removing a ban needs the owner to override this check once, '
                        f'with the reason in the PR.')
    for project, by_target in sorted(entries.items()):
        before = base_entries.get(project, {})
        for target, (_, counts) in sorted(by_target.items()):
            if target not in before:
                problems.append(f'src/{project}/{BASELINE_FILE}: new entry {target}. New code is held to zero: fix '
                                f'the call instead of baselining it.')
                continue
            for rule, count in sorted(counts.items()):
                if count > before[target][1].get(rule, 0):
                    problems.append(f'src/{project}/{BASELINE_FILE}: {target} {rule} raised '
                                    f'{before[target][1].get(rule, 0)} -> {count}. Fix the call instead.')
    return problems


CONFIG_KEY = re.compile(r'^\s*(dotnet_diagnostic\.rs0030\.severity|dotnet_analyzer_diagnostic\.category-apidesign\.severity|'
                        r'dotnet_analyzer_diagnostic\.severity)\s*[=:]', re.I)
CONFIG_NAME = re.compile(r'(^|/)(\.editorconfig|\.globalconfig|[^/]*\.editorconfig|[^/]*\.globalconfig)$', re.I)


def config_problems(configs):
    """configs: {path: text} of analyzer config files (.editorconfig / .globalconfig, any section). A line that sets
    the severity of RS0030, of the ApiDesign category or of every diagnostic is refused: none exists today, and the
    stripped build below proves the rest exactly."""
    problems = []
    for path, text in sorted(configs.items()):
        for number, line in enumerate(text.splitlines(), 1):
            if CONFIG_KEY.match(line):
                problems.append(f'{path}:{number}: "{line.strip()}" changes how RS0030 is reported. Remove it; a ban is '
                                f'not configurable per file or per project.')
    return problems


def suppressions_outside_baseline(files):
    """files: {relative path: text} of C# files under src/. RS0030 may be named only in src/<Project>/AnalyzerBaseline.cs."""
    return [f'{path}: names RS0030 outside {BASELINE_FILE}. Only {BASELINE_FILE} may baseline banned calls.'
            for path, text in sorted(files.items())
            if 'RS0030' in text and not (path.count('/') == 2 and path.endswith('/' + BASELINE_FILE))]


BANNED_ANALYZERS = ('microsoft.codeanalysis.bannedapianalyzers.dll', 'microsoft.codeanalysis.csharp.bannedapianalyzers.dll')
BANNED_ANALYZER_PACKAGE = '<nuget>/microsoft.codeanalysis.bannedapianalyzers/5.6.0/analyzers/dotnet/cs/'
SDK_COMPILER = re.compile(r'^<dotnet>/sdk/[^/]+/roslyn/bincore/csc(\.exe|\.dll)?$', re.I)
SDK_HOST = re.compile(r'^<dotnet>/dotnet(\.exe)?$', re.I)
WPF_TEMP = re.compile(r'^src/(?P<name>[^/]+)/(?P=name)_wpftmp\.csproj$')


def owner_of(project, projects):
    """The src/ project a compile belongs to: src/<Name>/<Name>.csproj itself, or WPF's temporary markup project
    src/<Name>/<Name>_wpftmp.csproj next to it (as build_facts.py's wpf_temp_of). None for anything else."""
    m = re.fullmatch(r'src/(?P<name>[^/]+)/(?P=name)\.csproj', project)
    if m and m.group('name') in projects:
        return m.group('name'), m.group('name')
    m = WPF_TEMP.match(project)
    if m and m.group('name') in projects:
        return m.group('name'), m.group('name') + '_wpftmp'
    return None, None


def is_sdk_compiler(tool):
    """The SDK's own csc, as the build facts write it: its apphost, or `<dotnet>/dotnet exec <dotnet>/sdk/.../csc.dll`."""
    if len(tool) == 1:
        return bool(SDK_COMPILER.match(tool[0])) and not tool[0].lower().endswith('.dll')
    return (len(tool) == 3 and bool(SDK_HOST.match(tool[0])) and tool[1] == 'exec'
            and bool(SDK_COMPILER.match(tool[2])) and tool[2].lower().endswith('.dll'))


def judge_compile(where, call):
    """One Csc call of a src/ project, from build-facts.json. Fail closed on anything that changes what RS0030 sees."""
    if call.get('target') != 'CoreCompile':
        return [f'{where}: the compiler ran from target {call.get("target")}, not CoreCompile.']
    if not is_sdk_compiler(call.get('tool') or []):
        # Without the SDK's csc nothing else on its command line can be trusted: stop here.
        return [f'{where}: the compiler is {(call.get("tool") or ["?"])[0]}, not the SDK\'s own csc. Remove CscToolPath / '
                f'CscToolExe or any wrapper.']
    problems = []
    if call.get('responseFiles') or any(a.startswith('@') for a in call.get('arguments', [])) or not call.get('noConfig'):
        problems.append(f'{where}: the compiler reads a response file '
                        f'({", ".join(r.get("path", "?") for r in call.get("responseFiles") or []) or "csc.rsp"}); '
                        f'its options are hidden from review. Remove CompilerResponseFile.')
    analyzers = [a for a in call.get('analyzers', []) if a.lower().startswith(BANNED_ANALYZER_PACKAGE)]
    if sorted(pathlib.PurePosixPath(a).name.lower() for a in analyzers) != sorted(BANNED_ANALYZERS):
        problems.append(f'{where}: the BannedApiAnalyzers 5.6.0 DLLs are not both passed to the compiler.')
    lists = [f for f in call.get('additionalFiles', []) if pathlib.PurePosixPath(f).name.lower().startswith('bannedsymbols')]
    if lists != [LIST]:
        problems.append(f'{where}: the ban lists passed to the compiler are {lists or "none"}; exactly {LIST} is allowed.')
    roslyn = call.get('roslyn') or {}
    specific = {key.upper(): value for key, value in (roslyn.get('specificDiagnosticOptions') or {}).items()}
    if 'RS0030' in specific or roslyn.get('generalDiagnosticOption') != 'Error' or (roslyn.get('warningLevel') or 0) < 1 \
            or call.get('ruleset') or roslyn.get('skipAnalyzers') is not False or roslyn.get('errors'):
        problems.append(f'{where}: compiler options change how RS0030 is reported (RS0030 option '
                        f'{specific.get("RS0030")}, warnings as errors {roslyn.get("generalDiagnosticOption")}, warning '
                        f'level {roslyn.get("warningLevel")}, rule set {call.get("ruleset")}, skip analyzers '
                        f'{roslyn.get("skipAnalyzers")}, parse errors {roslyn.get("errors")}).')
    for config in call.get('analyzerConfigs', []):
        if config.get('content') is None:
            problems.append(f'{where}: analyzer config {config.get("path")} was passed but could not be read.')
    replay = call.get('replay') or {}
    if replay.get('status') != 'replayed' or replay.get('exitCode') != 0:
        problems.append(f'{where}: the compile was not replayed cleanly after the build ({replay.get("status") or "no replay"}, '
                        f'exit {replay.get("exitCode")}); the RS0030 results cannot be judged, so this fails.')
    else:
        problems += [f'{where}: {p}' for p in replay_log_problems(replay.get('sarif'), call.get('sources', []))]
    return problems


CSC_TOOL_NAME = 'Microsoft (R) Visual C# Compiler'


def replay_log_problems(log, sources):
    """The replayed compile's SARIF log must be a real csc log of that compile: SARIF 2.1.0, exactly one run by the C#
    compiler, and every result in one of the compile's own source files. Anything else fails closed (never "0 results")."""
    if not isinstance(log, dict) or log.get('version') != '2.1.0' or not isinstance(log.get('runs'), list) \
            or len(log['runs']) != 1 or not isinstance(log['runs'][0], dict):
        return ['the replay\'s error log is not a SARIF 2.1.0 log with exactly one run.']
    run = log['runs'][0]
    name = ((run.get('tool') or {}).get('driver') or {}).get('name')
    if name != CSC_TOOL_NAME:
        return [f'the replay\'s error log was not written by the C# compiler (tool "{name}").']
    if not isinstance(run.get('results', []), list):
        return ['the replay\'s error log has no result list.']
    known = {s.lower() for s in sources}
    stray = sorted({r['file'] for r in sarif_results(log) if r['rule'] == 'RS0030' and r['file'].lower() not in known})
    return [f'the replay\'s error log reports RS0030 in {f}, which is not a source of this compile.' for f in stray]


def judge_facts(facts, projects, baselines, repo_config_texts):
    """facts: build-facts.json. projects: the src/<Name> names. baselines: {name: {target: (justification, counts)}}.
    Every compile of a src/ project in every binary log (the Release build and the win-x64 publish) is judged."""
    problems, seen, total = [], set(), 0
    configs = dict(repo_config_texts)
    for log in facts.get('binlogs', []):
        if not log.get('succeeded'):
            problems.append(f'binlog {log.get("label")}: the build failed.')
        for unit in log.get('units', []):
            name, compiled_as = owner_of(unit.get('project', ''), projects)
            if name is None:
                continue
            calls = unit.get('csc', [])
            where = f'{log.get("label")}: {unit["project"]} ({unit.get("targetFramework")} {unit.get("runtimeIdentifier")})'
            # A unit with no compile is fine (evaluated only, or up to date: the publish reuses the build's outputs, and
            # build_facts.py traces every shipped assembly to a recorded compile). More than one is not.
            if len(calls) > 1:
                problems.append(f'{where}: {len(calls)} compiler calls, expected at most one.')
            for call in calls:
                if log.get('label') == 'build' and call.get('target') == 'CoreCompile':
                    seen.add(name)
                found = judge_compile(where, call)
                problems += found
                for config in call.get('analyzerConfigs', []):
                    if config.get('content') is not None:
                        configs.setdefault(config['path'], config['content'])
                if found and any('not the SDK' in f for f in found):
                    continue
                # Judged: the SARIF log of the REPLAY (BuildFacts --replay-under src/), made after the build in a
                # temporary folder; never a file the build wrote, which a later build step could have replaced.
                log_data = (call.get('replay') or {}).get('sarif')
                if isinstance(log_data, dict) and not replay_log_problems(log_data, call.get('sources', [])):
                    results = sarif_results(log_data)
                    total += sum(1 for r in results if r['rule'] == 'RS0030')
                    problems += [f'{where}: {p}' for p in judge_baseline(name, baselines.get(name, {}), results)]
    for name in projects:
        if name not in seen:
            problems.append(f'src/{name}/{name}.csproj: no compile from CoreCompile in the "build" log; it cannot be '
                            f'judged, so this fails.')
    problems += config_problems(configs)
    return problems, total


def fix_text(rule):
    return f'Fix the cause (see {RULES_DOC} and the rule {rule}); do not baseline new code.'


def fmt_places(places):
    return ', '.join(f'{path}:{line}' for path, line in sorted(places))


# ---------------------------------------------------------------------------------------------------------------------
# Running the .NET SDK.
# ---------------------------------------------------------------------------------------------------------------------
def dotnet(arguments, base, expect_success=True):
    run = subprocess.run(['dotnet', *arguments], cwd=str(base), capture_output=True, text=True, encoding='utf-8',
                         errors='replace')
    if expect_success and run.returncode:
        tail = '\n'.join((run.stdout + run.stderr).splitlines()[-40:])
        raise ToolError(f'dotnet {" ".join(arguments)} failed (exit {run.returncode}):\n{tail}')
    return run


def json_output(text):
    start = text.find('{')
    if start < 0:
        raise ToolError(f'MSBuild printed no JSON:\n{text[-2000:]}')
    return json.loads(text[start:])


def reference_assemblies(base):
    """The exact reference assemblies the canary compiles against (net10.0 + Microsoft.WindowsDesktop.App)."""
    run = dotnet(['msbuild', CANARY_PROJECT, '-restore', '-nologo', '-p:EnableWindowsTargeting=true',
                  '-t:FindReferenceAssembliesForReferences', '-getItem:ReferencePathWithRefAssemblies'], base)
    items = json_output(run.stdout)['Items']['ReferencePathWithRefAssemblies']
    paths = sorted({item['FullPath'] for item in items})
    if not paths:
        raise ToolError('the canary project resolved no reference assemblies')
    return paths


def listgen(base, arguments, scratch):
    refs = scratch / 'refs.txt'
    refs.write_text('\n'.join(reference_assemblies(base)) + '\n', encoding='utf-8')
    dotnet(['build', LISTGEN_PROJECT, '-c', 'Release', '-nologo', '-v:q'], base)
    return dotnet(['run', '--no-build', '-c', 'Release', '--project', LISTGEN_PROJECT, '--', arguments[0], str(refs),
                   *arguments[1:]], base, expect_success=False)


def read_sarif(path, base):
    """SARIF results of a file the canary build wrote (see sarif_results)."""
    if not path.is_file():
        raise ToolError(f'{path.name} was not written: the build did not compile, so nothing can be proven.')
    return sarif_results(json.loads(path.read_text(encoding='utf-8-sig')), base)


def sarif_results(data, base=None):
    """[{rule, level, file (repo-relative, posix), line, message, suppressions: [{type, justification}]}]. Build facts
    write file URIs repo-relative (file:///src/...); the canary's own log has real paths, made relative to base."""
    results = []
    for run in data.get('runs', []):
        for result in run.get('results', []):
            location = (result.get('locations') or [{}])[0].get('physicalLocation', {})
            uri = location.get('artifactLocation', {}).get('uri', '')
            results.append({
                'rule': result.get('ruleId', ''),
                'level': result.get('level', 'warning'),
                'file': relative(uri, base) if base is not None else re.sub(r'^file:///', '', uri),
                'line': location.get('region', {}).get('startLine', 0),
                'message': result.get('message', {}).get('text', ''),
                'suppressions': [{'type': s.get('properties', {}).get('suppressionType', s.get('kind', '?')),
                                  'justification': s.get('justification', '')}
                                 for s in result.get('suppressions', [])],
            })
    return results


def relative(uri, base):
    if not uri:
        return ''
    path = urllib.request.url2pathname(urllib.parse.urlparse(uri).path) if uri.startswith('file:') else uri
    try:
        return pathlib.Path(path).resolve().relative_to(base.resolve()).as_posix()
    except ValueError:
        return pathlib.Path(path).as_posix()


def read(base, relative_path):
    return (base / relative_path).read_text(encoding='utf-8-sig')


def src_projects(base):
    projects = sorted(path.parent.name for path in base.glob('src/*/*.csproj'))
    if not projects:
        raise ToolError('no src/<Name>/<Name>.csproj found')
    return projects


# ---------------------------------------------------------------------------------------------------------------------
# Commands.
# ---------------------------------------------------------------------------------------------------------------------
def generate(base, out):
    with tempfile.TemporaryDirectory() as scratch:
        run = listgen(base, ['generate', str(base / GROUPS), str(out)], pathlib.Path(scratch))
    if run.returncode:
        raise ToolError(f'the generator failed:\n{run.stdout}{run.stderr}')
    return run.stdout.strip()


def cmd_generate(base):
    print(generate(base, base / LIST))
    parse_list(read(base, LIST))
    return 0


def cmd_list_check(base):
    with tempfile.TemporaryDirectory() as scratch:
        fresh = pathlib.Path(scratch) / 'BannedSymbols.txt'
        generate(base, fresh)
        want = fresh.read_text(encoding='utf-8')
    have = read(base, LIST)
    if have != want:
        diff = ''.join(difflib.unified_diff(have.splitlines(True), want.splitlines(True), LIST, 'regenerated'))
        print(f'BANNED LIST FAILED: {LIST} is not what {GROUPS} gives against the reference packs.\n'
              f'  Why:   a hand edit, or a .NET update added or removed an overload.\n'
              f'  Fix:   edit {GROUPS} if needed, run python -I scripts/quality/banned_apis.py generate, commit both,\n'
              f'         and add a canary call for every new line.\n'
              f'  Do NOT edit {LIST} by hand.\n  Rule:  {RULES_DOC}\n{diff}')
        return 1
    print(f'BANNED LIST OK: {len(parse_list(have))} lines, identical to a fresh expansion of {GROUPS}')
    return 0


def canary_inputs(base):
    listed = dict(parse_list(read(base, LIST)))
    markers, problems = parse_markers({path: read(base, path) for path in CANARY_FILES})
    return listed, markers, problems + judge_markers(listed, markers)


def cmd_canary(base):
    listed, markers, problems = canary_inputs(base)
    if not problems:
        with tempfile.TemporaryDirectory() as scratch:
            run = listgen(base, ['bind', str(base / LIST), *[str(base / path) for path in CANARY_FILES]],
                          pathlib.Path(scratch))
        if run.returncode:
            raise ToolError(f'the canary does not compile cleanly:\n{run.stdout}{run.stderr}')
        refs = []
        for line in run.stdout.splitlines():
            if line.startswith('REF '):
                path, number, doc_id = line[4:].rsplit('|', 2)
                refs.append((relative(path, base), int(number), doc_id))
        problems += judge_bindings(markers, refs)
    if not problems:
        with tempfile.TemporaryDirectory() as scratch:
            sarif_dir = pathlib.Path(scratch)
            # --no-incremental: an up-to-date canary would skip the compiler and write no log. The comma inside the
            # property value is escaped (%2C) so MSBuild does not split it.
            build = dotnet(['build', CANARY_PROJECT, *BUILD_PROPS, '--no-incremental',
                            f'-p:ErrorLog={(sarif_dir / "Canary.sarif").as_posix()}%2Cversion=2.1'], base,
                           expect_success=False)
            if build.returncode == 0:
                problems.append('the canary build succeeded, so RS0030 is not an error (or the analyzer did not run).')
            results = read_sarif(sarif_dir / 'Canary.sarif', base)
        problems += judge_firing(listed, markers, results)
        problems += [f'{r["file"]}:{r["line"]}: {r["rule"]} {r["message"]}' for r in results
                     if r['rule'] != 'RS0030' and r['level'] == 'error']
    if problems:
        print(f'CANARY FAILED: a banned line is not proven to fire.\n  Rule: {RULES_DOC}\n  ' + '\n  '.join(problems))
        return 1
    print(f'CANARY OK: {len(listed)} banned lines, each bound by exactly one canary call and fired exactly once as an '
          f'error')
    return 0


def repo_configs(base):
    run = subprocess.run(['git', '-C', str(base), 'ls-files', '-z', '-c', '-o', '--exclude-standard'],
                         capture_output=True)
    if run.returncode:
        raise ToolError('cannot list files with git')
    return [name for name in run.stdout.decode('utf-8').split('\0') if name and CONFIG_NAME.search(name)]


def cmd_judge(base, facts_path):
    if not facts_path or not pathlib.Path(facts_path).is_file():
        print('usage: judge <build-facts.json>  (written by the "Build facts" step of windows-ci)')
        return 2
    facts = json.loads(pathlib.Path(facts_path).read_text(encoding='utf-8'))
    projects = src_projects(base)
    baselines, problems = {}, []
    for name in projects:
        path = f'src/{name}/{BASELINE_FILE}'
        entries, bad = parse_baseline(path, read(base, path)) if (base / path).is_file() else ({}, [])
        baselines[name] = entries
        problems += bad
    found, total = judge_facts(facts, projects, baselines, {n: read(base, n) for n in repo_configs(base)})
    problems += found
    if problems:
        print(f'BANNED APIS FAILED (judged on the shipped build\'s facts).\n  Rule: {RULES_DOC}\n  '
              + '\n  '.join(problems))
        return 1
    entries = sum(len(e) for e in baselines.values())
    compiles = sum(1 for log in facts['binlogs'] for unit in log['units'] if owner_of(unit['project'], projects)[0]
                   for _ in unit['csc'])
    print(f'BANNED APIS OK: {compiles} compiles of src/ projects in the build and publish logs, each by the SDK csc '
          f'with both ban analyzers, exactly {LIST}, no response file, no RS0030 option, no analyzer config touching '
          f'RS0030; their {total} RS0030 results match {entries} {BASELINE_FILE} entries exactly, per member and rule')
    return 0


def baseline_entries_from(texts):
    entries, problems = {}, []
    for project, (path, text) in sorted(texts.items()):
        entries[project], bad = parse_baseline(path, text)
        problems += bad
    return entries, problems


def git_show(base, rev, path):
    run = subprocess.run(['git', '-C', str(base), 'show', f'{rev}:{path}'], capture_output=True)
    return run.stdout.decode('utf-8-sig') if run.returncode == 0 else None


def src_csharp(base, rev=None):
    """{relative path: text} of every C# file under src/: at a git revision, or what git would commit now."""
    if rev:
        run = subprocess.run(['git', '-C', str(base), 'ls-tree', '-r', '-z', '--name-only', rev, '--', 'src'],
                             capture_output=True)
    else:
        run = subprocess.run(['git', '-C', str(base), 'ls-files', '-z', '-c', '-o', '--exclude-standard', '--', 'src'],
                             capture_output=True)
    if run.returncode:
        raise ToolError(f'cannot list src/ with git{" at " + rev if rev else ""}')
    names = [name for name in run.stdout.decode('utf-8').split('\0') if name.lower().endswith('.cs')]
    if rev:
        return {name: git_show(base, rev, name) or '' for name in names}
    return {name: (base / name).read_text(encoding='utf-8-sig', errors='replace') for name in names
            if (base / name).is_file()}


def baselines_in(files):
    """({project: {target: (justification, counts)}}, problems) from every AnalyzerBaseline.cs in files; a file that
    names RS0030 anywhere else is a problem."""
    texts = {path.split('/')[1]: (path, text) for path, text in files.items()
             if path.count('/') == 2 and path.endswith('/' + BASELINE_FILE)}
    entries, problems = baseline_entries_from(texts)
    return entries, problems + suppressions_outside_baseline(files)


def cmd_format(base):
    """Cheap text checks for the ubuntu job: the ban list parses (doc ids, rule ids, no duplicates), every
    AnalyzerBaseline.cs has the strict format, and RS0030 is named in no other src/ C# file."""
    parse_list(read(base, LIST))
    _, problems = baselines_in(src_csharp(base))
    if problems:
        print(f'BANNED APIS FORMAT FAILED.\n  Rule: {RULES_DOC}\n  ' + '\n  '.join(problems))
        return 1
    print(f'BANNED APIS FORMAT OK: {LIST} parses; every {BASELINE_FILE} has the strict format; RS0030 is named nowhere '
          f'else in src/')
    return 0


def cmd_rise_check(base, rev):
    base_list = git_show(base, rev, LIST)
    if base_list is None:
        print(f'The base ({rev}) has no {LIST} yet: this PR introduces it, so there is no older ban list to apply.')
        return 0
    base_entries, _ = baselines_in(src_csharp(base, rev))
    entries, problems = baselines_in(src_csharp(base))
    problems += judge_rise([d for d, _ in parse_list(base_list)], [d for d, _ in parse_list(read(base, LIST))],
                           base_entries, entries)
    if problems:
        print('BANNED APIS (judged by the base branch\'s own copy) FAILED: this PR loosens the ban.\n  '
              + '\n  '.join(problems))
        return 1
    print('BANNED APIS (judged by the base branch\'s own copy) OK: no ban removed, no baseline entry added or raised, '
          'RS0030 named only in AnalyzerBaseline.cs')
    return 0


# ---------------------------------------------------------------------------------------------------------------------
# Self-test: planted samples for every judging rule. Each case states the exact number of problems expected.
# ---------------------------------------------------------------------------------------------------------------------
def self_test():
    failures = []

    def expect(name, problems, count, needle=''):
        if len(problems) != count or (needle and not any(needle in p for p in problems)):
            failures.append(f'{name}: expected {count} problem(s){" containing " + repr(needle) if needle else ""}, '
                            f'got {len(problems)}: {problems}')

    def raises(name, call):
        try:
            call()
        except ToolError:
            return
        failures.append(f'{name}: expected the input to be rejected')

    a, b = 'M:N.T.A(System.Int32)', 'P:N.T.B'
    good_list = f'# header\n{a};BOO-ASYNC-001 msg a\n\n{b};BOO-TIME-001 msg b\n'
    listed = dict(parse_list(good_list))
    expect('list parses', [] if list(listed) == [a, b] else ['order'], 0)
    raises('list: duplicate line', lambda: parse_list(f'{a};BOO-ASYNC-001 x\n{a};BOO-ASYNC-001 y\n'))
    raises('list: message without rule id', lambda: parse_list(f'{a};no rule id\n'))
    raises('list: missing message', lambda: parse_list(f'{a}\n'))
    raises('list: empty', lambda: parse_list('# only comments\n'))

    f = 'scripts/quality/canary/Canary.Core.cs'
    w = 'scripts/quality/canary/Canary.Wpf.cs'
    files = {f: f'x();\nA(1); // BAN {a}\n_ = B; // BAN {b}\n// BAN\n', w: 'y();\n'}
    markers, bad = parse_markers(files)
    expect('markers parse', bad, 1, 'malformed marker')
    expect('markers found', [] if markers == {(f, 2): a, (f, 3): b} else [markers], 0)
    expect('markers complete', judge_markers(listed, markers), 0)
    expect('marker missing', judge_markers(listed, {(f, 2): a}), 1, 'no canary call')
    expect('marker twice', judge_markers(listed, {(f, 2): a, (f, 3): b, (f, 9): a}), 1, '2 canary calls')
    expect('marker unknown', judge_markers(listed, {(f, 2): a, (f, 3): b, (f, 9): 'M:N.T.Gone'}), 1, 'does not list')

    expect('bind exact', judge_bindings(markers, [(f, 2, a), (f, 3, b)]), 0)
    expect('bind wrong overload', judge_bindings(markers, [(f, 2, b), (f, 3, b)]), 1, 'binds to')
    expect('bind nothing', judge_bindings(markers, [(f, 3, b)]), 1, 'no banned symbol')
    expect('bind twice on a line', judge_bindings(markers, [(f, 2, a), (f, 2, a), (f, 3, b)]), 1, 'binds to')
    expect('bind unmarked line', judge_bindings(markers, [(f, 2, a), (f, 3, b), (f, 1, a)]), 1, 'without')

    def hit(line, doc_id, level='error', suppressions=(), file=f, rule='RS0030', message=None):
        return {'rule': rule, 'level': level, 'file': file, 'line': line, 'suppressions': list(suppressions),
                'message': message if message is not None else f"The symbol 'X' is banned in this project: "
                                                               f"{listed[doc_id]}"}

    expect('fires once each', judge_firing(listed, markers, [hit(2, a), hit(3, b)]), 0)
    expect('a line does not fire', judge_firing(listed, markers, [hit(3, b)]), 1, 'fired 0 times')
    expect('a line fires twice', judge_firing(listed, markers, [hit(2, a), hit(2, a), hit(3, b)]), 1, 'fired 2')
    expect('fires as a warning', judge_firing(listed, markers, [hit(2, a, 'warning'), hit(3, b)]), 1, 'unsuppressed')
    expect('fires suppressed', judge_firing(listed, markers, [hit(2, a, suppressions=[{'type': 'Pragma Directive'}]),
                                                              hit(3, b)]), 1, 'suppressed')
    expect('wrong message', judge_firing(listed, markers, [hit(2, b, message=None), hit(3, b)]), 1, 'message')
    expect('extra RS0030', judge_firing(listed, markers, [hit(2, a), hit(3, b), hit(1, a)]), 1, 'unexpected')
    expect('other file ignored', judge_firing(listed, markers, [hit(2, a), hit(3, b), hit(5, a, file='src/x.cs')]), 0)
    expect('other rule ignored', judge_firing(listed, markers, [hit(2, a), hit(3, b), hit(1, a, rule='CA1000')]), 0)
    expect('probe: only included files judged', judge_firing(listed, markers, [], expected_files=[w]), 0)
    expect('probe: included file judged', judge_firing(listed, markers, [hit(2, a)], expected_files=[f]), 1, 'fired 0')

    t1, t2 = '~M:Boo.A.M1', '~M:Boo.A.M2(System.Int32)'
    j1 = f'Baselined by PR 1.3; cleanup C2; BOO-UI-002 x2, BOO-TIME-001 x1; {t1}'
    j2 = f'Baselined by PR 1.3; cleanup C3; BOO-TIME-001 x1; {t2}'

    def line(target, justification):
        return (f'[assembly: SuppressMessage("ApiDesign", "RS0030", Scope = "member", Target = "{target}", '
                f'Justification = "{justification}")]')

    text = f'{BASELINE_HEADER}\n\n{line(t1, j1)}\n{line(t2, j2)}\n'
    entries, bad = parse_baseline('p', text)
    expect('baseline parses', bad, 0)
    expect('baseline counts', [] if entries[t1][1] == {'BOO-UI-002': 2, 'BOO-TIME-001': 1} else [entries], 0)
    expect('baseline: stray line', parse_baseline('p', text + '// note\n')[1], 1, 'only')
    expect('baseline: pragma line', parse_baseline('p', text + '#pragma warning disable RS0030\n')[1], 1, 'only')
    expect('baseline: other rule', parse_baseline('p', line(t1, j1).replace('RS0030', 'CA1031'))[1], 1, 'only')
    expect('baseline: target mismatch', parse_baseline('p', line(t2, j1))[1], 1, 'same target')
    expect('baseline: zero count', parse_baseline('p', line(t1, j1.replace('x1', 'x0')))[1], 1, 'same target')
    expect('baseline: rule twice', parse_baseline('p', line(t1, j1.replace('BOO-TIME-001', 'BOO-UI-002')))[1], 1,
           'same target')
    expect('baseline: twice', parse_baseline('p', f'{line(t1, j1)}\n{line(t1, j1)}\n')[1], 1, 'twice')

    def product_hit(justification=None, rule='BOO-UI-002', kind='SuppressMessageAttribute'):
        suppressions = [] if justification is None else [{'type': kind, 'justification': justification}]
        return {'rule': 'RS0030', 'level': 'error', 'file': 'src/P/A.cs', 'line': 1, 'suppressions': suppressions,
                'message': f"The symbol 'X' is banned in this project: {rule} text"}

    exact = [product_hit(j1), product_hit(j1), product_hit(j1, 'BOO-TIME-001'), product_hit(j2, 'BOO-TIME-001')]
    expect('baseline exact', judge_baseline('P', entries, exact), 0)
    expect('new call in a baselined member', judge_baseline('P', entries, exact + [product_hit(j1)]), 1, '2 -> 3')
    expect('new rule in a baselined member', judge_baseline('P', entries, exact + [product_hit(j2)]), 1, '0 -> 1')
    expect('hit fixed, count not lowered', judge_baseline('P', entries, exact[1:]), 1, 'good news')
    expect('stale entry', judge_baseline('P', entries, exact[:3]), 1, 'good news')
    expect('unbaselined call', judge_baseline('P', entries, exact + [product_hit()]), 1, 'not baselined')
    expect('pragma suppression', judge_baseline('P', entries, exact + [product_hit('', kind='Pragma Directive')]), 1,
           'outside')
    expect('foreign justification', judge_baseline('P', entries, exact + [product_hit('because')]), 1, 'outside')
    expect('copied justification, other kind', judge_baseline('P', entries, exact[:3] + [
        product_hit(j2, 'BOO-TIME-001', kind='Pragma Directive')]), 2, 'outside')
    expect('non-RS0030 ignored', judge_baseline('P', entries, exact + [dict(product_hit(), rule='CA1031')]), 0)

    base_entries = {'P': entries}
    expect('rise: same', judge_rise([a, b], [a, b], base_entries, base_entries), 0)
    expect('rise: ban removed', judge_rise([a, b], [a], base_entries, base_entries), 1, 'was removed')
    expect('rise: ban added', judge_rise([a], [a, b], base_entries, base_entries), 0)
    raised = {'P': {t1: (j1, {'BOO-UI-002': 3, 'BOO-TIME-001': 1}), t2: entries[t2]}}
    expect('rise: count raised', judge_rise([a], [a], base_entries, raised), 1, 'raised 2 -> 3')
    added = {'P': dict(entries, **{'~M:Boo.A.M3': ('j', {'BOO-UI-001': 1})})}
    expect('rise: entry added', judge_rise([a], [a], base_entries, added), 1, 'new entry')
    expect('rise: new project entry', judge_rise([a], [a], base_entries, dict(base_entries, Q={t1: entries[t1]})), 1,
           'new entry')
    lowered = {'P': {t1: (j1, {'BOO-UI-002': 1, 'BOO-TIME-001': 1})}}
    expect('rise: lowered and removed', judge_rise([a], [a], base_entries, lowered), 0)
    expect('rise: rule added to entry', judge_rise([a], [a], base_entries, {'P': {t2: (j2, {'BOO-TIME-001': 1,
                                                                                            'BOO-UI-001': 1})}}), 1,
           'raised 0 -> 1')

    cfg = {'src/Boo.Core/.editorconfig': '[EvilA.cs]\ndotnet_diagnostic.RS0030.severity = none\n'}
    expect('config: RS0030 severity', config_problems(cfg), 1, 'changes how RS0030')
    expect('config: category, any case', config_problems({'x/.globalconfig': 'is_global = true\n[/a/EvilA.cs]\n'
                                                           'dotnet_analyzer_diagnostic.category-ApiDesign.severity=none'}),
           1, 'changes how RS0030')
    expect('config: all diagnostics', config_problems({'a.editorconfig': 'DOTNET_ANALYZER_DIAGNOSTIC.SEVERITY : none'}), 1,
           'changes how RS0030')
    expect('config: other rules fine', config_problems({'a.editorconfig': 'dotnet_diagnostic.CA1031.severity = none\n'
                                                                          'dotnet_diagnostic.RS0031.severity = none'}), 0)
    expect('RS0030 outside the baseline file', suppressions_outside_baseline({
        'src/P/AnalyzerBaseline.cs': 'RS0030', 'src/P/A.cs': '[SuppressMessage("ApiDesign", "RS0030")]',
        'src/P/Sub/AnalyzerBaseline.cs': 'RS0030', 'src/P/B.cs': 'clean'}), 2, 'outside')
    found, bad = baselines_in({'src/P/AnalyzerBaseline.cs': text, 'src/P/A.cs': 'x'})
    expect('baselines_in reads every project file', bad + ([] if set(found['P']) == {t1, t2} else ['entries']), 0)

    csc = '<dotnet>/sdk/10.0.403/Roslyn/bincore/csc.exe'
    pkg = '<nuget>/microsoft.codeanalysis.bannedapianalyzers/5.6.0/analyzers/dotnet/cs/'
    level = '<dotnet>/sdk/10.0.403/Sdks/Microsoft.NET.Sdk/analyzers/build/config/analysislevel_10_default.globalconfig'
    sarif_ok = {'version': '2.1.0', 'runs': [{'tool': {'driver': {'name': CSC_TOOL_NAME}}, 'results': [{
        'ruleId': 'RS0030', 'level': 'error', 'message': {
            'text': "The symbol 'X' is banned in this project: BOO-UI-002 text"}, 'locations': [{'physicalLocation': {
                'artifactLocation': {'uri': 'file:///src/P/A.cs'}, 'region': {'startLine': 3}}}], 'suppressions': [
            {'justification': j2.replace('BOO-TIME-001', 'BOO-UI-002'), 'kind': 'inSource',
             'properties': {'suppressionType': 'SuppressMessageAttribute'}}]}]}]}
    good = {'target': 'CoreCompile', 'tool': [csc], 'arguments': ['/noconfig'], 'responseFiles': [], 'noConfig': True,
            'analyzerConfigs': [{'path': 'src/P/obj/P.GeneratedMSBuildEditorConfig.editorconfig',
                                 'content': 'is_global = true\nbuild_property.TargetFramework = net10.0\n'},
                                {'path': level, 'content': '# rules\n'}],
            'analyzers': [pkg + 'Microsoft.CodeAnalysis.BannedApiAnalyzers.dll',
                          pkg + 'Microsoft.CodeAnalysis.CSharp.BannedApiAnalyzers.dll'],
            'additionalFiles': [LIST, 'src/P/app.manifest'], 'ruleset': None,
            'roslyn': {'generalDiagnosticOption': 'Error', 'warningLevel': 10, 'skipAnalyzers': False, 'errors': [],
                       'specificDiagnosticOptions': {'CS1701': 'Suppress'}},
            'sources': ['src/P/A.cs'], 'errorLog': None,
            'replay': {'status': 'replayed', 'exitCode': 0, 'outputSha256': 'x', 'sarif': sarif_ok}}

    def compile_(**changes):
        return dict(good, **changes)

    def roslyn(**changes):
        return dict(good['roslyn'], **changes)

    expect('csc: the SDK apphost', judge_compile('w', compile_()), 0)
    expect('csc: dotnet exec csc.dll', judge_compile('w', compile_(tool=['<dotnet>/dotnet.exe', 'exec', csc[:-4] + '.dll'])), 0)
    expect('csc: Linux apphost', judge_compile('w', compile_(tool=[csc[:-4]])), 0)
    expect('csc: other target', judge_compile('w', compile_(target='Evil')), 1, 'not CoreCompile')
    expect('csc: CscToolPath wrapper', judge_compile('w', compile_(tool=['src/P/wrap/csc.cmd'])), 1, "not the SDK")
    expect('csc: look-alike outside the SDK', judge_compile('w', compile_(tool=['<repo-parent-1>/sdk/1/Roslyn/bincore/csc.exe'])),
           1, 'not the SDK')
    expect('csc: dotnet exec of another dll', judge_compile('w', compile_(tool=['<dotnet>/dotnet.exe', 'exec', 'src/csc.dll'])),
           1, 'not the SDK')
    expect('csc: CompilerResponseFile', judge_compile('w', compile_(responseFiles=[{'path': 'src/P/evil.rsp'}])), 1,
           'response file')
    expect('csc: @ argument', judge_compile('w', compile_(arguments=['/noconfig', '@src/P/evil.rsp'])), 1, 'response file')
    expect('csc: default csc.rsp', judge_compile('w', compile_(noConfig=False)), 1, 'response file')
    expect('csc: analyzer missing', judge_compile('w', compile_(analyzers=good['analyzers'][:1])), 1, 'BannedApiAnalyzers')
    expect('csc: analyzer from elsewhere', judge_compile('w', compile_(analyzers=['src/P/' + n for n in BANNED_ANALYZERS])),
           1, 'BannedApiAnalyzers')
    expect('csc: own ban list', judge_compile('w', compile_(additionalFiles=['src/P/BannedSymbols.txt'])), 1, 'ban lists')
    expect('csc: second ban list', judge_compile('w', compile_(additionalFiles=[LIST, 'src/P/bannedsymbols.extra.txt'])), 1,
           'ban lists')
    expect('csc: no ban list', judge_compile('w', compile_(additionalFiles=[])), 1, 'ban lists')
    expect('csc: /nowarn:RS0030', judge_compile('w', compile_(roslyn=roslyn(specificDiagnosticOptions={'RS0030': 'Suppress'}))),
           1, 'options')
    expect('csc: /warnaserror-:rs0030 (any case)', judge_compile('w', compile_(roslyn=roslyn(
        specificDiagnosticOptions={'rs0030': 'Warn'}))), 1, 'options')
    expect('csc: /warnaserror-', judge_compile('w', compile_(roslyn=roslyn(generalDiagnosticOption='Default'))), 1, 'options')
    expect('csc: /warn:0', judge_compile('w', compile_(roslyn=roslyn(warningLevel=0))), 1, 'options')
    expect('csc: /ruleset', judge_compile('w', compile_(ruleset='src/P/x.ruleset')), 1, 'options')
    expect('csc: /skipanalyzers', judge_compile('w', compile_(roslyn=roslyn(skipAnalyzers=True))), 1, 'options')
    expect('csc: parse error', judge_compile('w', compile_(roslyn=roslyn(errors=['CS2007']))), 1, 'options')
    expect('csc: unreadable analyzer config', judge_compile('w', compile_(analyzerConfigs=[{'path': 'x.cfg', 'content': None}])),
           1, 'could not be read')
    expect('csc: not replayed', judge_compile('w', compile_(replay=None)), 1, 'not replayed')
    expect('csc: replay refused (not the SDK csc)', judge_compile('w', compile_(replay={
        'status': 'not replayed: the compiler is not the SDK\'s own csc'})), 1, 'not replayed')
    expect('csc: replay failed', judge_compile('w', compile_(replay=dict(good['replay'], exitCode=1))), 1, 'exit 1')

    def replay_sarif(log):
        return compile_(replay=dict(good['replay'], sarif=log))

    expect('sarif: a deps.json over the log (review of PR #28)', judge_compile('w', replay_sarif(
        {'runtimeTarget': {'name': '.NETCoreApp,Version=v10.0'}, 'libraries': {}})), 1, 'SARIF 2.1.0')
    expect('sarif: text, not JSON', judge_compile('w', replay_sarif('not json')), 1, 'SARIF 2.1.0')
    expect('sarif: two runs', judge_compile('w', replay_sarif(dict(sarif_ok, runs=sarif_ok['runs'] * 2))), 1,
           'exactly one run')
    expect('sarif: wrong version', judge_compile('w', replay_sarif(dict(sarif_ok, version='2.0.0'))), 1, 'SARIF 2.1.0')
    expect('sarif: another tool', judge_compile('w', replay_sarif({'version': '2.1.0', 'runs': [
        {'tool': {'driver': {'name': 'evil'}}, 'results': []}]})), 1, 'not written by the C# compiler')
    expect('sarif: a result in a file this compile does not have', judge_compile('w', compile_(sources=['src/P/B.cs'])),
           1, 'not a source of this compile')

    def facts(*units, ok=True, label='build'):
        return {'binlogs': [{'label': label, 'succeeded': ok, 'units': list(units)}]}

    def unit(project, *calls, rid=''):
        return {'project': project, 'targetFramework': 'net10.0', 'runtimeIdentifier': rid, 'csc': list(calls)}

    base_p = {'P': {'~M:Boo.A.M2(System.Int32)': (j2.replace('BOO-TIME-001', 'BOO-UI-002'), {'BOO-UI-002': 1})}}
    got, total = judge_facts(facts(unit('src/P/P.csproj', compile_())), ['P'], base_p, {})
    expect('facts: exact', got + ([] if total == 1 else ['total']), 0)
    expect('facts: WPF twin judged too', judge_facts(facts(unit('src/P/P.csproj', compile_()),
                                                           unit('src/P/P_wpftmp.csproj', compile_())), ['P'], base_p, {})[0], 0)
    expect('facts: a twin of another name is not a src project', judge_facts(facts(
        unit('src/P/P.csproj', compile_()), unit('src/Q/Q_wpftmp.csproj', compile_(tool=['evil']))), ['P'], base_p, {})[0], 0)
    expect('facts: publish compile judged', judge_facts({'binlogs': [
        {'label': 'build', 'succeeded': True, 'units': [unit('src/P/P.csproj', compile_())]},
        {'label': 'publish', 'succeeded': True, 'units': [unit('src/P/P.csproj', compile_(replay=None), rid='win-x64')]}]},
        ['P'], base_p, {})[0], 1, 'publish')
    expect('facts: project never compiled', judge_facts(facts(), ['P'], base_p, {})[0], 1, 'no compile')
    expect('facts: build failed', judge_facts(facts(unit('src/P/P.csproj', compile_()), ok=False), ['P'], base_p, {})[0], 1,
           'build failed')
    expect('facts: two compiles in one unit', judge_facts(facts(unit('src/P/P.csproj', compile_(), compile_())), ['P'],
                                                         base_p, {})[0], 1, '2 compiler calls')
    expect('facts: an up-to-date unit in the publish is fine', judge_facts({'binlogs': [
        {'label': 'build', 'succeeded': True, 'units': [unit('src/P/P.csproj', compile_())]},
        {'label': 'publish', 'succeeded': True, 'units': [unit('src/P/P.csproj', rid='win-x64')]}]}, ['P'], base_p, {})[0], 0)
    expect('facts: new RS0030 in the SARIF', judge_facts(facts(unit('src/P/P.csproj', compile_())), ['P'], {'P': {}}, {})[0],
           1, 'outside')
    evil = compile_(analyzerConfigs=[{'path': 'src/P/evil.cfg', 'content': '[EvilA.cs]\ndotnet_diagnostic.RS0030.severity = none'}])
    expect('facts: config received by the compiler', judge_facts(facts(unit('src/P/P.csproj', evil)), ['P'], base_p, {})[0],
           1, 'changes how RS0030')
    expect('facts: config in the repo', judge_facts(facts(unit('src/P/P.csproj', compile_())), ['P'], base_p, {
        'tests/.editorconfig': 'dotnet_analyzer_diagnostic.severity = none'})[0], 1, 'changes how RS0030')
    expect('facts: owner_of', [] if (owner_of('src/P/P.csproj', ['P']), owner_of('src/P/P_wpftmp.csproj', ['P']),
                                     owner_of('src/P/Q.csproj', ['P']), owner_of('tools/P/P.csproj', ['P'])) ==
           (('P', 'P'), ('P', 'P_wpftmp'), (None, None), (None, None)) else ['owner'], 0)
    expect('facts sarif uri', [] if sarif_results(sarif_ok)[0]['file'] == 'src/P/A.cs' else ['uri'], 0)

    expect('sarif uri', [] if relative((root / 'src' / 'x.cs').resolve().as_uri(), root) == 'src/x.cs' else ['uri'], 0)

    if failures:
        print('BANNED APIS SELF-TEST FAILED:\n  ' + '\n  '.join(failures))
        return 1
    print('BANNED APIS SELF-TEST OK: list, marker, binding, firing, baseline, compiler-call, build-facts, analyzer-config and '
          'rise judging all behave')
    return 0


def argument(flag):
    if flag in sys.argv:
        index = sys.argv.index(flag)
        return sys.argv[index + 1] if index + 1 < len(sys.argv) else ''
    return None


def main():
    base = pathlib.Path(argument('--root')).resolve() if argument('--root') else root
    if '--self-test' in sys.argv:
        return self_test()
    commands = {'generate': cmd_generate, 'list-check': cmd_list_check, 'canary': cmd_canary, 'format': cmd_format}
    command = next((arg for arg in sys.argv[1:] if arg in commands or arg in ('rise-check', 'judge')), None)
    try:
        if command == 'judge':
            index = sys.argv.index('judge')
            return cmd_judge(base, sys.argv[index + 1] if index + 1 < len(sys.argv) else '')
        if command == 'rise-check':
            if not argument('--base-rev'):
                print('usage: rise-check --base-rev <git rev> [--root <dir>]')
                return 2
            return cmd_rise_check(base, argument('--base-rev'))
        if command is None:
            print(__doc__)
            return 2
        return commands[command](base)
    except ToolError as error:
        print(f'BANNED APIS COULD NOT RUN: {error}')
        return 2


if __name__ == '__main__':
    sys.exit(main())
