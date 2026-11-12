"""Quality ratchet: today's count of each known-bad pattern in src/ may only go DOWN. Never reads secrets.

  python -I scripts/quality/ratchet.py               check against docs/quality/baseline.json (CI runs this)
  python -I scripts/quality/ratchet.py --self-test   prove every rule fires on a planted bad sample (CI runs this)
  python -I scripts/quality/ratchet.py --lower       write lower counts into the baseline (refuses any increase)
  python -I scripts/quality/ratchet.py --list RULE   print every hit of one rule (file:line)

Exit 1 when a count rose (new problem), or fell without the baseline being lowered in the same change (so a gain is
locked in). Each failure prints what broke, why it matters, the root-cause fix and the rule's anchor in
docs/quality/RULES.md. Counts are text-level and slightly crude by design; the compiler-level checks (banned APIs,
analyzers) come in later Phase 1 PRs and are exact.
"""
import json
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parent.parent.parent
BASELINE = root / 'docs' / 'quality' / 'baseline.json'
RULES_DOC = 'docs/quality/RULES.md'
GENERATED_PARTS = {'bin', 'obj'}

STRING = re.compile(r'@?\$?"(?:[^"\\\n]|\\.)*"')


class Rule:
    def __init__(self, rule_id, what, why, fix, pattern=None, counter=None, sample=None):
        self.id, self.what, self.why, self.fix = rule_id, what, why, fix
        self.pattern = re.compile(pattern) if pattern else None
        self.counter = counter
        self.sample = sample

    def hits(self, source):
        """List of 1-based line numbers where the rule fires in one file's CodeView."""
        if self.counter:
            return self.counter(source)
        return [n for n, code in enumerate(source.code_lines, 1) if self.pattern.search(code)]


class CodeView:
    """One C# file split into code (strings blanked, comments removed) and comment lines."""

    def __init__(self, text):
        self.text = text.replace('\r\n', '\n')
        self.code_lines, self.comment_lines, self.todo_lines = [], [], []
        in_block = False
        for number, raw in enumerate(self.text.split('\n'), 1):
            line = STRING.sub('""', raw)
            code, comment = '', False
            i = 0
            while i < len(line):
                if in_block:
                    end = line.find('*/', i)
                    comment = True
                    if end < 0:
                        i = len(line)
                    else:
                        in_block, i = False, end + 2
                    continue
                if line.startswith('///', i):
                    if re.search(r'\b(TODO|FIXME|HACK|XXX)\b', line[i:]):
                        self.todo_lines.append((number, line[i:]))
                    break
                if line.startswith('//', i):
                    comment = True
                    if re.search(r'\b(TODO|FIXME|HACK|XXX)\b', line[i:]):
                        self.todo_lines.append((number, line[i:]))
                    break
                if line.startswith('/*', i):
                    in_block, comment = True, True
                    i += 2
                    continue
                code += line[i]
                i += 1
            self.code_lines.append(code)
            if comment:
                self.comment_lines.append(number)
        self.code = '\n'.join(self.code_lines)


def broad_catches(source):
    """(line, kind) for every bare `catch` or `catch (Exception ...)`: kind is empty / handles / rethrows."""
    found = []
    text = source.code
    for match in re.finditer(r'\bcatch\b\s*(\(\s*(?:System\.)?Exception\b[^)]*\)|(?=\s*\{)|(?=\s*$))', text, re.M):
        start = match.end()
        brace = text.find('{', start)
        if brace < 0:
            continue
        # A `when (...)` filter may contain braces (`is { } x`); skip to the block that follows the balanced parens.
        between = text[start:brace]
        if re.match(r'\s*when\b', between):
            j = text.find('(', start)
            depth, k = 0, j
            while k < len(text):
                if text[k] == '(':
                    depth += 1
                elif text[k] == ')':
                    depth -= 1
                    if depth == 0:
                        break
                k += 1
            brace = text.find('{', k)
        depth, k = 1, brace + 1
        while depth and k < len(text):
            if text[k] == '{':
                depth += 1
            elif text[k] == '}':
                depth -= 1
            k += 1
        body = text[brace + 1:k - 1]
        line = text.count('\n', 0, match.start()) + 1
        kind = 'rethrows' if re.search(r'\bthrow\b', body) else ('empty' if not body.strip() else 'handles')
        found.append((line, kind))
    return found


def static_mutable(source):
    hits = []
    for number, code in enumerate(source.code_lines, 1):
        if not re.search(r'\bstatic\b', code):
            continue
        if re.search(r'\bstatic\s+(readonly|class|partial|extern|async)\b|\bconst\b|using\s+static|\boperator\b|=>', code):
            continue
        head = code.split('=')[0]
        if '(' in head:
            continue
        if re.search(r'\{\s*get\s*;\s*\}', code):
            continue  # get-only auto-property: initialised once
        if re.search(r';\s*$', code) or '=' in code or re.search(r'\bset\s*;', code):
            hits.append(number)
    return hits


RULES = [
    Rule('BOO-ASYNC-001', 'Thread.Sleep', 'A sleep hides a race instead of fixing it.',
         'Await the thing you are waiting for (Task, TaskCompletionSource, SemaphoreSlim.WaitAsync) or inject '
         'TimeProvider. Do NOT make the sleep longer.', r'\bThread\.Sleep\b', sample='Thread.Sleep(10);'),
    Rule('BOO-ASYNC-002', 'Blocking wait on async work (Task.Wait/WaitAll/WaitAny, GetAwaiter().GetResult())',
         'Blocking on a task from the WPF UI thread deadlocks.',
         'Make the caller async and await it. Do NOT wrap it in Task.Run to hide the block.',
         r'\bTask\.(WaitAll|WaitAny)\b|GetAwaiter\(\)\s*\.\s*GetResult\(\)|\btask\w*\.Wait\s*\(',
         sample='t.GetAwaiter().GetResult();'),
    Rule('BOO-ASYNC-003', 'async void method',
         'An exception in an async void method cannot be caught by its caller and can end the app.',
         'Return Task. For a WPF event handler, make it a one-liner that hands an async Func<Task> to one shared '
         'helper which awaits it and reports errors. Do NOT add a try/catch that swallows everything.',
         r'\basync\s+void\b', sample='private async void Go() { }'),
    Rule('BOO-UI-001', 'Dispatcher.Invoke (synchronous)',
         'A synchronous cross-thread call deadlocks when the UI thread is itself waiting.',
         'Use await Dispatcher.InvokeAsync(...).', r'\bDispatcher\.Invoke\b',
         sample='Dispatcher.Invoke(() => { });'),
    Rule('BOO-UI-002', 'Dispatcher.BeginInvoke (fire and forget)',
         'Exceptions thrown inside are lost and the ordering is a guess.',
         'Use Dispatcher.InvokeAsync and observe the result. Do NOT add a delay to "fix" ordering.',
         r'\bDispatcher\.BeginInvoke\b', sample='Dispatcher.BeginInvoke(Go);'),
    Rule('BOO-TIME-001', 'Hidden clock (DateTime/DateTimeOffset .Now/.UtcNow)',
         'Time-based behaviour cannot be tested with a fake clock.',
         'Take a TimeProvider (inject it; tests pass a fake) and call GetUtcNow()/GetLocalNow().',
         r'\bDateTime(Offset)?\.(Now|UtcNow)\b', sample='var t = DateTime.UtcNow;'),
    Rule('BOO-TIME-002', 'Task.Delay', 'A delay used to "let things settle" hides a race.',
         'Wait for the real signal. For a real timeout, use the TimeProvider overload so tests control time.',
         r'\bTask\.Delay\b', sample='await Task.Delay(100);'),
    Rule('BOO-TIME-003', 'new DispatcherTimer', 'Timers are a common "fix" for ordering bugs, and are untestable '
         'without a fake clock.', 'Prefer an event or an awaited signal; if a real periodic timer is needed, keep it '
         'in one owner class and say why in the PR.', r'\bnew\s+DispatcherTimer\b',
         sample='var x = new DispatcherTimer();'),
    Rule('BOO-RT-001', 'GC.Collect', 'Forcing a garbage collection hides a leak.',
         'Find what holds the memory and dispose it.', r'\bGC\.Collect\b', sample='GC.Collect();'),
    Rule('BOO-RT-002', 'Environment.GetEnvironmentVariable',
         'Hidden switches (for example BOO_QA_CAPTURABLE) change privacy behaviour and must be auditable.',
         'Read environment variables in one place and pass the value in.',
         r'\bEnvironment\.GetEnvironmentVariable\b', sample='Environment.GetEnvironmentVariable("X");'),
    Rule('BOO-SAFE-003', 'Process.Start', 'Launching a program is a permissioned, security-sensitive action.',
         'Go through the ActionPolicy gate (DesktopTools). Do NOT call Process.Start directly.',
         r'\bProcess\.Start\b', sample='Process.Start("x");'),
    Rule('BOO-ERR-001', 'Broad catch with an empty body (error thrown away)',
         'The failure vanishes; the next symptom shows up somewhere unrelated.',
         'Catch the specific exception you expect and handle it, or let it propagate. Do NOT widen the catch.',
         counter=lambda s: [line for line, kind in broad_catches(s) if kind == 'empty'],
         sample='try { Go(); } catch (Exception) { }'),
    Rule('BOO-ERR-002', 'Broad catch that handles and carries on (no rethrow)',
         'Catching every exception also catches the bugs you did not expect.',
         'Narrow the type (or add a `when` filter for the exceptions you really expect).',
         counter=lambda s: [line for line, kind in broad_catches(s) if kind == 'handles'],
         sample='try { Go(); } catch (Exception ex) { Log(ex); }'),
    Rule('BOO-STATE-001', 'Mutable static state (static field, or static property with a setter)',
         'Shared mutable state leaks between tests and windows and hides ordering bugs.',
         'Make it an instance member owned by one object, or readonly.', counter=static_mutable,
         sample='private static int s_count;'),
    Rule('BOO-CMT-001', 'Comment line that is not an XML doc comment (///)',
         'Agents use comments to justify workarounds; rules written in comments are not checked.',
         'Fix the cause instead of explaining it. If it states a rule, write a test and use '
         '"// INVARIANT: ... see Test.Name". API docs use ///.',
         counter=lambda s: list(s.comment_lines), sample='// retry because it is flaky'),
    Rule('BOO-CMT-002', 'TODO / FIXME / HACK / XXX without an issue link',
         'Untracked debt is invisible.', 'Open a GitHub issue and write TODO(#123): ...',
         counter=lambda s: [n for n, text in s.todo_lines if not re.search(r'\bTODO\(#\d+\)', text)],
         sample='// TODO: fix later'),
]
RULES_BY_ID = {rule.id: rule for rule in RULES}


def projects():
    return sorted(p for p in (root / 'src').iterdir() if p.is_dir() and any(p.glob('*.csproj')))


def scan():
    """{rule: {project: [(relative file, line), ...]}}"""
    result = {rule.id: {} for rule in RULES}
    for project in projects():
        for rule in RULES:
            result[rule.id][project.name] = []
        for path in sorted(project.rglob('*.cs')):
            if GENERATED_PARTS.intersection(path.relative_to(project).parts):
                continue
            source = CodeView(path.read_text(encoding='utf-8-sig'))
            relative = path.relative_to(root).as_posix()
            for rule in RULES:
                result[rule.id][project.name].extend((relative, n) for n in rule.hits(source))
    return result


def counts(result):
    return {rule: {project: len(hits) for project, hits in by_project.items()} for rule, by_project in result.items()}


def anchor(rule_id):
    return f'{RULES_DOC}#{rule_id.lower()}'


def check():
    if not BASELINE.is_file():
        print(f'RATCHET FAILED: {BASELINE.relative_to(root).as_posix()} is missing.')
        return 1
    baseline = json.loads(BASELINE.read_text(encoding='utf-8'))['counts']
    result = scan()
    now = counts(result)
    rose, fell = [], []
    for rule in RULES:
        if rule.id not in baseline:
            rose.append(f'{rule.id}: rule has no baseline row. Add it with --lower in the PR that adds the rule.')
            continue
        for project, count in now[rule.id].items():
            allowed = baseline[rule.id].get(project, 0)
            if count > allowed:
                hits = ', '.join(f'{f}:{n}' for f, n in result[rule.id][project])
                rose.append(f'{rule.id}  {project}: {allowed} -> {count}\n'
                            f'  What:  {rule.what}\n  Why:   {rule.why}\n  Fix:   {rule.fix}\n'
                            f'         Do NOT raise the baseline to make this pass.\n'
                            f'  Rule:  {anchor(rule.id)}\n  Hits:  {hits}')
            elif count < allowed:
                fell.append(f'{rule.id}  {project}: {allowed} -> {count}')
    if rose:
        print('RATCHET FAILED: a known-bad pattern went UP. Fix the cause in the code.\n')
        print('\n\n'.join(rose))
        return 1
    if fell:
        print('RATCHET FAILED (good news): counts went DOWN. Lock the gain in: run\n'
              '  python -I scripts/quality/ratchet.py --lower\nand commit docs/quality/baseline.json in this PR.')
        print('\n'.join('  ' + line for line in fell))
        return 1
    total = sum(sum(by.values()) for by in now.values())
    print(f'RATCHET OK: {len(RULES)} rules, {len(projects())} projects, {total} baselined hits, none new')
    return 0


def lower():
    old = json.loads(BASELINE.read_text(encoding='utf-8')) if BASELINE.is_file() else {'counts': {}}
    now = counts(scan())
    for rule, by_project in now.items():
        for project, count in by_project.items():
            previous = old['counts'].get(rule, {}).get(project)
            if previous is not None and count > previous:
                print(f'REFUSED: {rule} {project} rose {previous} -> {count}. Fix the code; the baseline only goes down.')
                return 1
    old['counts'] = now
    old['note'] = ('Counts of known-bad patterns in src/, per project. May only go down. '
                   'Written by scripts/quality/ratchet.py --lower.')
    BASELINE.parent.mkdir(parents=True, exist_ok=True)
    BASELINE.write_text(json.dumps(old, indent=2, sort_keys=True) + '\n', encoding='utf-8', newline='\n')
    print('BASELINE WRITTEN', BASELINE.relative_to(root).as_posix())
    return 0


def self_test():
    failures = []
    for rule in RULES:
        bad = CodeView('class C {\n  void M() {\n    ' + rule.sample + '\n  }\n}\n')
        if not rule.hits(bad):
            failures.append(f'{rule.id} did not fire on its planted sample: {rule.sample}')
    clean = CodeView('class C {\n  /// <summary>Doc.</summary>\n  private static readonly int s_x = 1;\n'
                     '  private const string Url = "http://a//b /* not a comment */";\n'
                     '  async Task M() { try { await N(); } catch (IOException) { } }\n}\n')
    for rule in RULES:
        if rule.hits(clean):
            failures.append(f'{rule.id} fired on clean code (false positive): lines {rule.hits(clean)}')
    group = CodeView('class C { Func<string, string?> f = Environment.GetEnvironmentVariable; Action a = GC.Collect; }')
    for rule_id in ('BOO-RT-002', 'BOO-RT-001'):
        if not RULES_BY_ID[rule_id].hits(group):
            failures.append(f'{rule_id} missed a method group (no brackets)')
    filtered = CodeView('class C { void M() {\n try { } catch (Exception) when (x is { } y\n && y.Ok) { Drop(); }\n'
                        ' try { } catch (Exception ex) { throw new X(ex); }\n} }\n')
    kinds = [kind for _, kind in broad_catches(filtered)]
    if kinds != ['handles', 'rethrows']:
        failures.append(f'broad-catch classifier wrong on a multi-line when filter: {kinds}')
    if failures:
        print('SELF-TEST FAILED:\n  ' + '\n  '.join(failures))
        return 1
    print(f'SELF-TEST OK: all {len(RULES)} rules fire on planted samples and stay quiet on clean code')
    return 0


def list_rule(rule_id):
    if rule_id not in RULES_BY_ID:
        print('unknown rule', rule_id)
        return 1
    for project, hits in scan()[rule_id].items():
        for relative, number in hits:
            print(f'{relative}:{number}')
    return 0


if __name__ == '__main__':
    if '--self-test' in sys.argv:
        sys.exit(self_test())
    if '--lower' in sys.argv:
        sys.exit(lower())
    if '--list' in sys.argv:
        sys.exit(list_rule(sys.argv[sys.argv.index('--list') + 1]))
    sys.exit(check())
