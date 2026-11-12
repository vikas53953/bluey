"""Quality ratchet: today's count of each known-bad pattern in src/ may only go DOWN. Never reads secrets.

  python -I scripts/quality/ratchet.py                 check against docs/quality/baseline.json (CI runs this)
  python -I scripts/quality/ratchet.py --self-test     prove every rule and the pass/fail logic (CI runs this)
  python -I scripts/quality/ratchet.py --lower         write lower counts into the baseline (refuses any increase)
  python -I scripts/quality/ratchet.py --list RULE     print every hit of one rule (file:line)
  python -I <base copy of ratchet.py> --root . --baseline <base baseline.json> --rise-only
                                                       CI on a pull request: the BASE branch's own ratchet and baseline
                                                       judge the PR's code, so editing the PR's copy of either changes
                                                       nothing.

Exit 1 when a count rose (new problem), or fell without the baseline being lowered in the same change (so a gain is
locked in). Each failure prints what broke, why it matters, the root-cause fix, the wrong fix to avoid, and the rule's
anchor in docs/quality/RULES.md. Source is read with a small C# lexer (strings, interpolation holes, verbatim and raw
strings, char literals, line and block comments), and every match on a line counts. Known gaps are listed in RULES.md;
the compiler-exact versions (banned APIs, analyzers) come in later Phase 1 PRs.
"""
import json
import pathlib
import re
import sys
import tempfile

root = pathlib.Path(__file__).resolve().parent.parent.parent
BASELINE = root / 'docs' / 'quality' / 'baseline.json'
RULES_DOC = 'docs/quality/RULES.md'
GENERATED_PARTS = {'bin', 'obj'}


# ---------------------------------------------------------------------------------------------------------------------
# Lexer: returns the source with string/char literal text and comments blanked (newlines kept, interpolation holes kept
# as code), plus the comments found, as (line, text, kind) with kind 'doc' (exactly ///), 'line' or 'block'.
# ---------------------------------------------------------------------------------------------------------------------
def lex(text):
    text = text.replace('\r\n', '\n')
    out = list(text)
    comments = []
    n = len(text)

    def blank(a, b):
        for k in range(a, b):
            if out[k] != '\n':
                out[k] = ' '

    def line_of(index):
        return text.count('\n', 0, index) + 1

    def scan_code(i, stop_at_brace=False):
        """Scan code from i. With stop_at_brace, return at the unmatched '}' that closes an interpolation hole."""
        depth = 0
        while i < n:
            c = text[i]
            if c == '/' and text.startswith('//', i):
                end = text.find('\n', i)
                end = n if end < 0 else end
                body = text[i:end]
                kind = 'doc' if body.startswith('///') and not body.startswith('////') else 'line'
                comments.append((line_of(i), body, kind))
                blank(i, end)
                i = end
                continue
            if c == '/' and text.startswith('/*', i):
                end = text.find('*/', i + 2)
                end = n if end < 0 else end + 2
                first = line_of(i)
                for offset, part in enumerate(text[i:end].split('\n')):
                    comments.append((first + offset, part, 'block'))
                blank(i, end)
                i = end
                continue
            if c == "'":
                j = i + 1
                while j < n and text[j] != "'" and text[j] != '\n':
                    j += 2 if text[j] == '\\' else 1
                blank(i + 1, min(j, n))
                i = j + 1
                continue
            m = re.match(r'(\$*)(@?)(\$*)("{3,}|")', text[i:i + 12]) if c in '$@"' else None
            if m and (m.group(1) or m.group(2) or m.group(3) or c == '"'):
                dollars = len(m.group(1)) + len(m.group(3))
                verbatim = bool(m.group(2))
                quotes = m.group(4)
                i = scan_string(i + m.end(), dollars, verbatim, len(quotes))
                continue
            if stop_at_brace:
                if c == '{':
                    depth += 1
                elif c == '}':
                    if depth == 0:
                        return i
                    depth -= 1
            i += 1
        return i

    def scan_string(i, dollars, verbatim, quote_count):
        """i is just after the opening quotes. Blank literal text, keep interpolation holes as code."""
        raw = quote_count >= 3
        close = '"' * quote_count
        open_brace = '{' * max(dollars, 1)
        start = i
        while i < n:
            if raw and text.startswith(close, i):
                blank(start, i)
                return i + quote_count
            if not raw:
                if text[i] == '\\' and not verbatim:
                    i += 2
                    continue
                if text[i] == '"':
                    if verbatim and text.startswith('""', i):
                        i += 2
                        continue
                    blank(start, i)
                    return i + 1
                if text[i] == '\n' and not verbatim:
                    blank(start, i)
                    return i  # unterminated; let the compiler complain
            if dollars and text.startswith(open_brace, i):
                if not raw and dollars == 1 and text.startswith('{{', i):
                    i += 2
                    continue
                blank(start, i + len(open_brace))
                i = scan_code(i + len(open_brace), stop_at_brace=True)
                i += len(open_brace) if raw else 1
                start = i
                continue
            i += 1
        blank(start, n)
        return n

    scan_code(0)
    return ''.join(out), comments


class Source:
    def __init__(self, text):
        self.code, self.comments = lex(text)
        self.code_lines = self.code.split('\n')
        self.using_static_thread = bool(re.search(r'using\s+static\s+(global::)?System\.Threading\.Thread\s*;', self.code))


def pattern_hits(pattern):
    compiled = re.compile(pattern)

    def count(source):
        return [number for number, line in enumerate(source.code_lines, 1) for _ in compiled.finditer(line)]
    return count


def thread_sleep(source):
    hits = pattern_hits(r'\bThread\s*\.\s*Sleep\b')(source)
    if source.using_static_thread:
        hits += pattern_hits(r'(?<![\w.])Sleep\s*\(')(source)
    return hits


def broad_catches(source):
    """(line, kind) for every bare `catch` or `catch (Exception ...)`: kind is empty / handles / rethrows."""
    found, text = [], source.code
    for match in re.finditer(r'\bcatch\b\s*(\(\s*(?:global::)?(?:System\s*\.\s*)?Exception\b[^)]*\))?', text):
        if match.group(1) is None and not re.match(r'\s*(\{|when\b)', text[match.end():]):
            continue
        k = match.end()
        rest = text[k:]
        if re.match(r'\s*when\b', rest):
            j = text.find('(', k)
            depth = 0
            while j < len(text):
                depth += text[j] == '('
                depth -= text[j] == ')'
                if depth == 0:
                    break
                j += 1
            k = j + 1
        brace = text.find('{', k)
        if brace < 0:
            continue
        depth, j = 1, brace + 1
        while depth and j < len(text):
            depth += text[j] == '{'
            depth -= text[j] == '}'
            j += 1
        body = text[brace + 1:j - 1]
        kind = 'rethrows' if re.search(r'\bthrow\b', body) else ('empty' if not body.strip() else 'handles')
        found.append((text.count('\n', 0, match.start()) + 1, kind))
    return found


def static_mutable(source):
    hits = []
    for number, code in enumerate(source.code_lines, 1):
        if not re.search(r'\bstatic\b', code):
            continue
        if re.search(r'\bstatic\s+(readonly|class|partial|extern|async|implicit|explicit)\b|\bconst\b|using\s+static|\boperator\b|=>', code):
            continue
        head = code.split('=')[0]
        if '(' in head and not re.search(r'\bstatic\s+\(', head):
            continue
        if re.search(r'\{\s*get\s*;\s*\}', code) or re.search(r'\{\s*get\s*;\s*init\s*;\s*\}', code):
            continue
        if re.search(r';\s*$', code) or '=' in code or re.search(r'\bset\s*;', code):
            hits.append(number)
    return hits


ALLOWED_COMMENT = re.compile(r'^//\s*(INVARIANT|SECURITY):.*\bsee\s+[\w.]+|\bTODO\(#\d+\)')
TODO = re.compile(r'\b(TODO|FIXME|HACK|XXX)\b(?!\(#\d+\))')


def comment_lines(source):
    lines = set()
    for line, body, kind in source.comments:
        if kind == 'doc' or not body.strip() or (kind == 'line' and ALLOWED_COMMENT.search(body.strip())):
            continue
        lines.add(line)
    return sorted(lines)


def untracked_todos(source):
    return [line for line, body, _ in source.comments for _ in TODO.finditer(body)]


class Rule:
    def __init__(self, rule_id, what, why, fix, dont, counter, samples):
        self.id, self.what, self.why, self.fix, self.dont = rule_id, what, why, fix, dont
        self.counter = counter
        self.samples = samples  # list of (snippet inside a method, expected hit count)

    def hits(self, source):
        return self.counter(source)


DISPATCHER = r'\b\w*[Dd]ispatcher\s*\??\.\s*'
RULES = [
    Rule('BOO-ASYNC-001', 'Thread.Sleep', 'A sleep hides a race instead of fixing it.',
         'Await the thing you are waiting for (Task, TaskCompletionSource, SemaphoreSlim.WaitAsync) or inject TimeProvider.',
         'Do NOT make the sleep longer.', thread_sleep,
         [('Thread.Sleep(10);', 1), ('Thread .Sleep(1); System.Threading.Thread.Sleep(2);', 2),
          ('Action a = Thread.Sleep;', 1)]),
    Rule('BOO-ASYNC-002', 'Blocking wait on async work (Task.Wait/WaitAll/WaitAny, .Result, GetAwaiter().GetResult())',
         'Blocking on a task from the WPF UI thread deadlocks.', 'Make the caller async and await it.',
         'Do NOT wrap it in Task.Run to hide the block.',
         pattern_hits(r'\bTask\s*\.\s*(WaitAll|WaitAny)\b|GetAwaiter\s*\(\s*\)\s*\.\s*GetResult\s*\('
                      r'|\b\w*[Tt]ask\w*\s*\.\s*(Wait\s*\(|Result\b)'),
         [('t.GetAwaiter().GetResult();', 1), ('Task.WaitAll(a, b);', 1), ('_task.Wait(); myTask.Wait(5);', 2),
          ('var r = _pump.Task.Result;', 1), ('await _pump.Task.WaitAsync(ct);', 0)]),
    Rule('BOO-ASYNC-003', 'async void (method or event-handler lambda)',
         'An exception in async void cannot be caught by its caller and can end the app.',
         'Return Task. For a WPF event, hand an async Func<Task> to one shared helper that awaits it and reports errors.',
         'Do NOT add a catch-everything block.', pattern_hits(r'\basync\s+void\b|\+=\s*async\b'),
         [('async void Go() { }', 1), ('b.Click += async (s, e) => await Go();', 1)]),
    Rule('BOO-UI-001', 'Dispatcher.Invoke (synchronous)',
         'A synchronous cross-thread call deadlocks when the UI thread is itself waiting.',
         'Use await Dispatcher.InvokeAsync(...).', 'Do NOT add a timeout to "unstick" it.',
         pattern_hits(DISPATCHER + r'Invoke\b'),
         [('Dispatcher.Invoke(() => { });', 1), ('dispatcher.Invoke(Go); d?.Dispatcher.Invoke(Go);', 2),
          ('await Dispatcher.InvokeAsync(Go);', 0)]),
    Rule('BOO-UI-002', 'Dispatcher.BeginInvoke (fire and forget)',
         'Exceptions thrown inside are lost and the ordering is a guess.',
         'Use Dispatcher.InvokeAsync and observe the result.', 'Do NOT add a delay to "fix" ordering.',
         pattern_hits(DISPATCHER + r'BeginInvoke\b'),
         [('Dispatcher.BeginInvoke(Go);', 1), ('dispatcher.BeginInvoke(Go);', 1)]),
    Rule('BOO-TIME-001', 'Hidden clock (DateTime/DateTimeOffset .Now/.UtcNow/.Today)',
         'Time-based behaviour cannot be tested with a fake clock.',
         'Take a TimeProvider (inject it; tests pass a fake) and call GetUtcNow()/GetLocalNow().',
         'Do NOT wrap the clock in a static helper; that is the same hidden clock.',
         pattern_hits(r'\bDateTime(Offset)?\s*\.\s*(Now|UtcNow|Today)\b'),
         [('var t = DateTime.UtcNow;', 1), ('var s = $"{DateTime.Now:HH:mm} {DateTimeOffset.UtcNow}";', 2),
          ('var s = "DateTime.Now";', 0)]),
    Rule('BOO-TIME-002', 'Task.Delay', 'A delay used to "let things settle" hides a race.',
         'Wait for the real signal; for a real timeout use the TimeProvider overload so tests control time.',
         'Do NOT make the delay longer.', pattern_hits(r'\bTask\s*\.\s*Delay\b'), [('await Task.Delay(100);', 1)]),
    Rule('BOO-TIME-003', 'new DispatcherTimer', 'Timers are a common "fix" for ordering bugs and are untestable '
         'without a fake clock.', 'Prefer an event or an awaited signal; keep real periodic timers in one owner class.',
         'Do NOT add a timer to wait for something to finish.',
         pattern_hits(r'\bnew\s+(?:[\w.]+\.)?DispatcherTimer\b|\bDispatcherTimer\s+\w+\s*=\s*new\s*\('),
         [('var x = new DispatcherTimer();', 1), ('DispatcherTimer t = new(DispatcherPriority.Normal);', 1),
          ('var y = new System.Windows.Threading.DispatcherTimer();', 1)]),
    Rule('BOO-RT-001', 'GC.Collect', 'Forcing a garbage collection hides a leak.',
         'Find what holds the memory and dispose it.', 'Do NOT call it "just in case".',
         pattern_hits(r'\bGC\s*\.\s*Collect\b'), [('GC.Collect();', 1), ('Action a = GC.Collect;', 1),
                                                    ('var n = GC.CollectionCount(0);', 0)]),
    Rule('BOO-RT-002', 'Environment.GetEnvironmentVariable',
         'Hidden switches (for example BOO_QA_CAPTURABLE) change privacy behaviour and must be auditable.',
         'Read environment variables in one place and pass the value in.', 'Do NOT add another reader.',
         pattern_hits(r'\bEnvironment\s*\.\s*GetEnvironmentVariable\b'),
         [('Environment.GetEnvironmentVariable("X");', 1), ('Func<string, string?> f = Environment.GetEnvironmentVariable;', 1)]),
    Rule('BOO-SAFE-003', 'Process.Start', 'Launching a program is a permissioned, security-sensitive action.',
         'Go through the ActionPolicy gate (DesktopTools).', 'Do NOT call Process.Start directly.',
         pattern_hits(r'\bProcess\s*\.\s*Start\b'), [('Process.Start("x");', 1)]),
    Rule('BOO-ERR-001', 'Broad catch with an empty body (error thrown away)',
         'The failure vanishes; the next symptom shows up somewhere unrelated.',
         'Catch the specific exception you expect and handle it, or let it propagate.', 'Do NOT widen the catch.',
         lambda s: [line for line, kind in broad_catches(s) if kind == 'empty'],
         [('try { Go(); } catch (Exception) { }', 1), ('try { Go(); } catch { }', 1),
          ('try { Go(); } catch (global::System.Exception) { }', 1), ('try { Go(); } catch (IOException) { }', 0)]),
    Rule('BOO-ERR-002', 'Broad catch that handles and carries on (no rethrow)',
         'Catching every exception also catches the bugs you did not expect.',
         'Narrow the type, or add a `when (...)` filter for the exceptions you really expect.',
         'Do NOT log and continue as a habit.',
         lambda s: [line for line, kind in broad_catches(s) if kind == 'handles'],
         [('try { Go(); } catch (Exception ex) { Log(ex); }', 1),
          ('try { } catch (Exception) when (x is { } y\n && y.Ok) { Drop(); }', 1),
          ('try { } catch (Exception ex) { throw new X(ex); }', 0)]),
    Rule('BOO-STATE-001', 'Mutable static state (static field, or static property with a setter)',
         'Shared mutable state leaks between tests and windows and hides ordering bugs.',
         'Make it an instance member owned by one object, or readonly.', 'Do NOT add a lock around it as the fix.',
         static_mutable, [('}\n private static int s_count;\n void N() {', 1),
                          ('}\n public static bool Started { get; private set; }\n void N() {', 1),
                          ('}\n private static readonly int s_x = 1;\n void N() {', 0)]),
    Rule('BOO-CMT-001', 'Comment line that is not an XML doc comment (///)',
         'Agents use comments to justify workarounds; rules written in comments are not checked.',
         'Fix the cause instead of explaining it. If it states a rule, write a test and use '
         '"// INVARIANT: ... see TestClass.TestName". API docs use ///.',
         'Do NOT reword the comment to dodge the count.', comment_lines,
         [('// retry because it is flaky', 1), ('/* a\n b */', 2), ('//// four slashes', 1),
          ('// INVARIANT: undo restores bytes, see UndoJournalTests.Roundtrip', 0),
          ('var u = "http://a//b /* x */";', 0), ('var r = """\n // not a comment\n """;', 0)]),
    Rule('BOO-CMT-002', 'TODO / FIXME / HACK / XXX without an issue link',
         'Untracked debt is invisible.', 'Open a GitHub issue and write TODO(#123): ...', 'Do NOT delete the note '
         'and keep the debt.', untracked_todos,
         [('// TODO: fix later', 1), ('/* FIXME */', 1), ('// TODO(#12): tracked', 0), ('/// HACK in a doc', 1)]),
]
RULES_BY_ID = {rule.id: rule for rule in RULES}


def projects(base):
    return sorted(p for p in (base / 'src').iterdir() if p.is_dir() and any(p.glob('*.csproj')))


def scan(base):
    """{rule: {project: [(relative file, line), ...]}}"""
    result = {rule.id: {} for rule in RULES}
    for project in projects(base):
        for rule in RULES:
            result[rule.id][project.name] = []
        for path in sorted(project.rglob('*.cs')):
            if GENERATED_PARTS.intersection(path.relative_to(project).parts):
                continue
            source = Source(path.read_text(encoding='utf-8-sig'))
            relative = path.relative_to(base).as_posix()
            for rule in RULES:
                result[rule.id][project.name].extend((relative, n) for n in rule.hits(source))
    return result


def counts(result):
    return {rule: {project: len(hits) for project, hits in by_project.items()} for rule, by_project in result.items()}


def compare(result, baseline, rise_only=False):
    """Return (rose, fell) message lists."""
    now = counts(result)
    rose, fell = [], []
    for rule in RULES:
        if rule.id not in baseline:
            if not rise_only:
                rose.append(f'{rule.id}: rule has no baseline row. Add it with --lower in the PR that adds the rule.')
            continue
        for project, count in now[rule.id].items():
            allowed = baseline[rule.id].get(project, 0)
            if count > allowed:
                hits = ', '.join(f'{f}:{n}' for f, n in result[rule.id][project])
                rose.append(f'{rule.id}  {project}: {allowed} -> {count}\n'
                            f'  What:  {rule.what}\n  Why:   {rule.why}\n  Fix:   {rule.fix}\n'
                            f'         {rule.dont} Do NOT raise the baseline to make this pass.\n'
                            f'  Rule:  {RULES_DOC}#{rule.id.lower()}\n  Hits:  {hits}')
            elif count < allowed:
                fell.append(f'{rule.id}  {project}: {allowed} -> {count}')
    return rose, ([] if rise_only else fell)


def check(base, baseline_path, rise_only=False):
    if not baseline_path.is_file():
        print(f'RATCHET FAILED: {baseline_path} is missing.')
        return 1
    baseline = json.loads(baseline_path.read_text(encoding='utf-8'))['counts']
    result = scan(base)
    rose, fell = compare(result, baseline, rise_only)
    label = 'RATCHET (against the base branch\'s own ratchet and baseline)' if rise_only else 'RATCHET'
    if rose:
        print(f'{label} FAILED: a known-bad pattern went UP. Fix the cause in the code.\n')
        print('\n\n'.join(rose))
        return 1
    if fell:
        print(f'{label} FAILED (good news): counts went DOWN. Lock the gain in: run\n'
              '  python -I scripts/quality/ratchet.py --lower\nand commit docs/quality/baseline.json in this PR.')
        print('\n'.join('  ' + line for line in fell))
        return 1
    total = sum(sum(by.values()) for by in counts(result).values())
    print(f'{label} OK: {len(RULES)} rules, {len(projects(base))} projects, {total} hits, none above the baseline')
    return 0


def lower(base, baseline_path):
    old = json.loads(baseline_path.read_text(encoding='utf-8')) if baseline_path.is_file() else {'counts': {}}
    now = counts(scan(base))
    for rule, by_project in now.items():
        for project, count in by_project.items():
            previous = old['counts'].get(rule, {}).get(project)
            if previous is not None and count > previous:
                print(f'REFUSED: {rule} {project} rose {previous} -> {count}. Fix the code; the baseline only goes down.')
                return 1
    old['counts'] = now
    old['note'] = ('Counts of known-bad patterns in src/, per project. May only go down. '
                   'Written by scripts/quality/ratchet.py --lower.')
    baseline_path.parent.mkdir(parents=True, exist_ok=True)
    baseline_path.write_text(json.dumps(old, indent=2, sort_keys=True) + '\n', encoding='utf-8', newline='\n')
    print('BASELINE WRITTEN', baseline_path)
    return 0


def self_test():
    failures = []
    for rule in RULES:
        for snippet, expected in rule.samples:
            source = Source('class C {\n  void M() {\n    ' + snippet + '\n  }\n}\n')
            got = len(rule.hits(source))
            if got != expected:
                failures.append(f'{rule.id}: expected {expected} hit(s), got {got} for: {snippet!r}')
    clean = Source('class C {\n  /// <summary>Doc.</summary>\n  private static readonly int s_x = 1;\n'
                   '  private const string Url = "http://a//b /* not a comment */";\n'
                   '  async Task M() { try { await N(); } catch (IOException) { } }\n}\n')
    for rule in RULES:
        if rule.hits(clean):
            failures.append(f'{rule.id} fired on clean code (false positive): lines {rule.hits(clean)}')
    # The pass/fail logic itself, on a throwaway tree.
    with tempfile.TemporaryDirectory() as temp:
        tree = pathlib.Path(temp)
        (tree / 'src' / 'P').mkdir(parents=True)
        (tree / 'src' / 'P' / 'P.csproj').write_text('<Project/>', encoding='utf-8')
        code = tree / 'src' / 'P' / 'A.cs'
        baseline_path = tree / 'baseline.json'
        code.write_text('class A { void M() { Thread.Sleep(1); } }', encoding='utf-8')
        quiet = sys.stdout
        try:
            sys.stdout = open(pathlib.Path(temp) / 'log.txt', 'w', encoding='utf-8')
            steps = [('lower writes', lower(tree, baseline_path), 0), ('check equal passes', check(tree, baseline_path), 0)]
            code.write_text('class A { void M() { Thread.Sleep(1); Thread.Sleep(2); } }', encoding='utf-8')
            steps += [('check rise fails', check(tree, baseline_path), 1), ('lower refuses rise', lower(tree, baseline_path), 1),
                      ('rise-only also fails a rise', check(tree, baseline_path, rise_only=True), 1)]
            code.write_text('class A { void M() { } }', encoding='utf-8')
            steps += [('check fall fails until lowered', check(tree, baseline_path), 1),
                      ('rise-only accepts a fall', check(tree, baseline_path, rise_only=True), 0),
                      ('lower accepts fall', lower(tree, baseline_path), 0), ('check after lower passes', check(tree, baseline_path), 0)]
        finally:
            sys.stdout.close()
            sys.stdout = quiet
        failures += [f'logic: {name} returned {got}, expected {want}' for name, got, want in steps if got != want]
    if failures:
        print('SELF-TEST FAILED:\n  ' + '\n  '.join(failures))
        return 1
    samples = sum(len(rule.samples) for rule in RULES)
    print(f'SELF-TEST OK: {len(RULES)} rules, {samples} samples with exact expected counts, clean code quiet, '
          f'pass/fail logic checked')
    return 0


def list_rule(base, rule_id):
    if rule_id not in RULES_BY_ID:
        print('usage: --list RULE, one of: ' + ', '.join(RULES_BY_ID))
        return 1
    for _, hits in scan(base)[rule_id].items():
        for relative, number in hits:
            print(f'{relative}:{number}')
    return 0


def argument(flag):
    if flag in sys.argv:
        index = sys.argv.index(flag)
        return sys.argv[index + 1] if index + 1 < len(sys.argv) else ''
    return None


if __name__ == '__main__':
    base_root = pathlib.Path(argument('--root')).resolve() if argument('--root') else root
    baseline_file = pathlib.Path(argument('--baseline')).resolve() if argument('--baseline') else BASELINE
    if '--self-test' in sys.argv:
        sys.exit(self_test())
    if '--lower' in sys.argv:
        sys.exit(lower(base_root, baseline_file))
    if '--list' in sys.argv:
        sys.exit(list_rule(base_root, argument('--list') or ''))
    sys.exit(check(base_root, baseline_file, rise_only='--rise-only' in sys.argv))
