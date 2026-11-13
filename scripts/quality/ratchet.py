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
import subprocess
import sys
import tempfile
import unicodedata
import xml.etree.ElementTree as ElementTree

root = pathlib.Path(__file__).resolve().parent.parent.parent
BASELINE = root / 'docs' / 'quality' / 'baseline.json'
RULES_DOC = 'docs/quality/RULES.md'


# ---------------------------------------------------------------------------------------------------------------------
# Lexer: returns the source with string/char literal text and comments blanked (newlines kept, interpolation holes kept
# as code), plus the comments found, as (line, text, kind) with kind 'doc' (exactly ///), 'line' or 'block'.
# ---------------------------------------------------------------------------------------------------------------------
STRING_START = re.compile(r'(\$*)(@?)(\$*)("{3,}|")')  # no length limit: a raw string may open with any number of quotes


def lex(text):
    # Every C# line break (CR LF, lone CR, NEL, LINE SEPARATOR, PARAGRAPH SEPARATOR) becomes \n, so none can hide code
    # inside a `//` comment.
    text = re.sub('\r\n?|[\u0085\u2028\u2029]', '\n', text)
    out = list(text)
    comments = []
    n = len(text)

    def blank(a, b):
        for k in range(a, b):
            if out[k] != '\n':
                out[k] = ' '

    def line_of(index):
        return text.count('\n', 0, index) + 1

    def at_line_start(i):
        """True when only spaces/tabs precede index i on its line."""
        j = text.rfind('\n', 0, i) + 1
        return not text[j:i].strip()

    def scan_code(i, stop_at_brace=False):
        """Scan code from i. With stop_at_brace, return at the unmatched '}' that closes an interpolation hole; a
        top-level ':' there starts the format clause (literal text up to that '}')."""
        depth = 0
        parens = 0
        while i < n:
            c = text[i]
            if c == '/' and text.startswith('//', i):
                end = text.find('\n', i)
                end = n if end < 0 else end
                body = text[i:end]
                kind = 'doc' if body.startswith('///') and not body.startswith('////') else 'line'
                comments.append((line_of(i), body, kind, at_line_start(i)))
                blank(i, end)
                i = end
                continue
            if c == '/' and text.startswith('/*', i):
                end = text.find('*/', i + 2)
                end = n if end < 0 else end + 2
                first = line_of(i)
                for offset, part in enumerate(text[i:end].split('\n')):
                    comments.append((first + offset, part, 'block', False))
                blank(i, end)
                i = end
                continue
            if c == '#' and not stop_at_brace and at_line_start(i):
                # Preprocessor directive (#region, #if, #error ...): its text is not code and holds no strings. A `//`
                # on it is a real comment (counted like any other), except on #region/#endregion, whose text is a name.
                end = text.find('\n', i)
                end = n if end < 0 else end
                line = text[i:end]
                slash = line.find('//')
                if slash >= 0 and not re.match(r'#\s*(end)?region\b', line):
                    comments.append((line_of(i), line[slash:], 'line', False))
                blank(i, end)
                i = end
                continue
            if stop_at_brace and depth == 0 and parens == 0 and c == ':' and not text.startswith('::', i) \
                    and (i == 0 or text[i - 1] != ':'):
                end = text.find('}', i)
                end = n if end < 0 else end
                blank(i, end)
                return end
            if c == "'":
                j = i + 1
                while j < n and text[j] != "'" and text[j] != '\n':
                    j += 2 if text[j] == '\\' else 1
                blank(i + 1, min(j, n))
                i = j + 1
                continue
            m = STRING_START.match(text, i) if c in '$@"' else None
            if m and (m.group(1) or m.group(2) or m.group(3) or c == '"'):
                dollars = len(m.group(1)) + len(m.group(3))
                verbatim = bool(m.group(2))
                quote_count = 1 if verbatim else len(m.group(4))
                i = scan_string(m.start(4) + quote_count, dollars, verbatim, quote_count)
                continue
            if stop_at_brace:
                if c == '{':
                    depth += 1
                elif c == '}':
                    if depth == 0:
                        return i
                    depth -= 1
                elif c in '([':
                    parens += 1
                elif c in ')]':
                    parens = max(parens - 1, 0)
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


# Static members the rules guard, by type: `using static System.GC;` then `Collect()` is still GC.Collect.
STATIC_MEMBERS = {'GC': ['Collect'], 'Thread': ['Sleep'], 'Task': ['Delay', 'WaitAll', 'WaitAny'],
                  'DateTime': ['Now', 'UtcNow', 'Today'], 'DateTimeOffset': ['Now', 'UtcNow', 'Today'],
                  'Environment': ['GetEnvironmentVariable'], 'Process': ['Start']}
# A using directive anywhere a statement can start (line start, or after ; { }), with spaces allowed around the dots.
USING = re.compile(r'(?:^|(?<=[;{}]))\s*(global\s+)?using\s+(static\s+)?(?:(\w+)\s*=\s*)?(?:global\s*::\s*)?'
                   r'(\w+(?:\s*\.\s*\w+)*)\s*(?:<[^;]*>)?\s*;', re.M)


def plain_identifiers(code):
    """Spell identifiers the plain way: `\\u0043ollect` -> `Collect`, `@GC` -> `GC`, and drop invisible formatting
    characters (Unicode Cf, such as a soft hyphen), which the compiler ignores inside names. String text is blank."""
    def decode(m):
        char = chr(int(m.group(1) or m.group(2), 16))
        if unicodedata.category(char) == 'Cf':
            return ''
        return char if char.isprintable() else ' '
    code = ''.join(c for c in code if unicodedata.category(c) != 'Cf')
    code = re.sub(r'\\u([0-9A-Fa-f]{4})|\\U([0-9A-Fa-f]{8})', decode, code)
    return re.sub(r'@(?=[A-Za-z_])', '', code)


def expand_usings(code, usings):
    """Rewrite aliases and `using static` members to the full Type.Member form the rules look for."""
    for _, static, alias, target in usings:
        name = target.split('.')[-1]
        if alias:
            code = re.sub(r'(?<![\w.])' + re.escape(alias) + r'\b(?!\s*=[^=>])', name, code)
        elif static and name in STATIC_MEMBERS:
            code = re.sub(r'(?<![\w.])(' + '|'.join(STATIC_MEMBERS[name]) + r')\b', name + r'.\1', code)
    return code


class Source:
    def __init__(self, text, project_usings=()):
        code, self.comments = lex(text)
        code = plain_identifiers(code)
        self.usings = [(bool(m.group(1)), bool(m.group(2)), m.group(3), re.sub(r'\s', '', m.group(4)))
                       for m in USING.finditer(code)]
        self.code = expand_usings(code, list(project_usings) + self.usings)
        self.code_lines = self.code.split('\n')


def pattern_hits(pattern):
    compiled = re.compile(pattern)

    def count(source):
        return [number for number, line in enumerate(source.code_lines, 1) for _ in compiled.finditer(line)]
    return count


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


TAGGED_RULE = re.compile(r'^//\s*(INVARIANT|SECURITY):.*\bsee\s+(?P<cls>[A-Z]\w*Tests)\.(?P<method>[A-Z]\w*)\b')
TRACKED_TODO = re.compile(r'^//\s*TODO\(#\d+\):')
# Test classes found under tests/ ({class: source text}); filled by scan(). A tag naming a missing test is not exempt.
KNOWN_TESTS = {}


def exempt_comment(body):
    body = body.strip()
    if TRACKED_TODO.search(body):
        return True
    match = TAGGED_RULE.search(body)
    if not match:
        return False
    source = KNOWN_TESTS.get(match.group('cls'))
    return bool(source) and re.search(r'\b' + re.escape(match.group('method')) + r'\s*\(', source) is not None
TODO = re.compile(r'\b(TODO|FIXME|HACK|XXX)\b(?!\(#\d+\))')


def comment_lines(source):
    lines = set()
    for line, body, kind, whole in source.comments:
        if kind == 'doc' or not body.strip() or (kind == 'line' and whole and exempt_comment(body)):
            continue
        lines.add(line)
    return sorted(lines)


def untracked_todos(source):
    return [line for line, body, _, _ in source.comments for _ in TODO.finditer(body)]


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
         'Do NOT make the sleep longer.', pattern_hits(r'\bThread\s*\.\s*Sleep\b'),
         [('Thread.Sleep(10);', 1), ('Thread .Sleep(1); System.Threading.Thread.Sleep(2);', 2),
          ('Action a = Thread.Sleep;', 1),
          ('using static System.Threading.Thread;\n Sleep(1); x.Sleep(2);', 1),
          ('using T = System.Threading.Thread;\n T.Sleep(1);', 1),
          ('Thread.\\u0053leep(1); @Thread.@Sleep(2);', 2),
          ('using System; using static System.Threading.Thread;\n Sleep(1);', 1),
          ('{ using static System . Threading . Thread;\n Sleep(1); }', 1)]),
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
          ('var s = "DateTime.Now";', 0),
          ('using static System.DateTime;\n var t = Now;', 1),
          ('using static System.DateTimeOffset;\n var t = UtcNow;', 1)]),
    Rule('BOO-TIME-002', 'Task.Delay', 'A delay used to "let things settle" hides a race.',
         'Wait for the real signal; for a real timeout use the TimeProvider overload so tests control time.',
         'Do NOT make the delay longer.', pattern_hits(r'\bTask\s*\.\s*Delay\b'),
         [('await Task.Delay(100);', 1), ('using static System.Threading.Tasks.Task;\n await Delay(1);', 1)]),
    Rule('BOO-TIME-003', 'new DispatcherTimer', 'Timers are a common "fix" for ordering bugs and are untestable '
         'without a fake clock.', 'Prefer an event or an awaited signal; keep real periodic timers in one owner class.',
         'Do NOT add a timer to wait for something to finish.',
         pattern_hits(r'\bnew\s+(?:[\w.]+\.)?DispatcherTimer\b|\bDispatcherTimer\s+\w+\s*=\s*new\s*\('),
         [('var x = new DispatcherTimer();', 1), ('DispatcherTimer t = new(DispatcherPriority.Normal);', 1),
          ('var y = new System.Windows.Threading.DispatcherTimer();', 1)]),
    Rule('BOO-RT-001', 'GC.Collect', 'Forcing a garbage collection hides a leak.',
         'Find what holds the memory and dispose it.', 'Do NOT call it "just in case".',
         pattern_hits(r'\bGC\s*\.\s*Collect\b'), [('GC.Collect();', 1), ('Action a = GC.Collect;', 1),
                                                    ('var n = GC.CollectionCount(0);', 0),
                                                    # lexer: nothing may hide (or invent) the call after these
                                                    ('var q = @""""; GC.Collect();', 1),
                                                    ('var q = $@""""; GC.Collect();', 1),
                                                    ('var q = @$""""; GC.Collect();', 1),
                                                    ('var c = \'"\'; GC.Collect();', 1),
                                                    ("var c = '\\''; GC.Collect();", 1),
                                                    ('var s = @"a""b"; GC.Collect();', 1),
                                                    ('var s = $"{{"; GC.Collect(); var t = $"}}";', 1),
                                                    ('var s = $"{(x ? "}" : "{")}"; GC.Collect();', 1),
                                                    ('var s = $$"""{{x}} { """; GC.Collect();', 1),
                                                    ('var s = "GC.Collect();"; var r = """\n GC.Collect()\n """;', 0),
                                                    ('var s = $"{GC.Collect}";', 1),
                                                    ('var s = "\\""; GC.Collect();', 1),
                                                    ('var s = @"C:\\"; GC.Collect();', 1),
                                                    ('var s = @"x"" GC.Collect() ""y";', 0),
                                                    ('var s = $"{F(() => { return 1; })} GC.Collect()";', 0),
                                                    ('var s = "a\n GC.Collect();', 1),
                                                    ('/* GC.Collect();\n GC.Collect(); */ var z = 1;', 0),
                                                    ('var s = $"{x:it\'s}"; GC.Collect();', 1),
                                                    ('var s = $"{d:dd//MM}"; GC.Collect();', 1),
                                                    ('var s = $"{d:/*}"; GC.Collect(); var t = $"{d:*/}";', 1),
                                                    ('var s = $@"{d:it\'s}"; GC.Collect();', 1),
                                                    ('var s = $"{(a ? b : c)}"; GC.Collect();', 1),
                                                    ('var s = $"{global::System.String.Empty}"; GC.Collect();', 1),
                                                    ('#region it\'s @" "\"\n GC.Collect();', 1),
                                                    # each of these changes count if one lexer rule breaks:
                                                    ('var s = @"a""" + GC.Collect();', 1),
                                                    ('var s = $"{ new { A = 1 }.A + " GC.Collect() "}";', 0),
                                                    ('var s = $"{(x ? 0 : GC.Collect())}";', 1),
                                                    ('#region @"\n GC.Collect();\n var q = "x";', 1),
                                                    ('var s = @"a""\n GC.Collect(); b";', 0),
                                                    ('// GC.Collect();', 0),
                                                    ('var s = $"{F(x):it\'s}"; GC.Collect();', 1),
                                                    ('var s = $"{global::System.GC.Collect()}";', 1),
                                                    ('var r = """"x""""; GC.Collect();', 1),
                                                    ('// note\u2028GC.Collect(); // a\rGC.Collect(); // b\u0085GC.Collect();', 3),
                                                    ('// c\u2029GC.Collect();', 1),
                                                    ('GC.\\u0043ollect(); @GC.@Collect(); GC.\\U00000043ollect();', 3),
                                                    ('using G = System.GC;\n G.Collect(); var s = "G.Collect()";', 1),
                                                    ('using static System.GC;\n Collect();', 1),
                                                    ('using G = System . GC;\n G.Collect();', 1),
                                                    ('GC.Col\\u00ADlect(); GC.Col\u00adlect(); GC.\u200bCollect();', 3),
                                                    ('var r = ' + '"' * 13 + 'x' + '"' * 13 + '; GC.Collect();', 1),
                                                    ('var r = ' + '"' * 13 + 'a' + '"' * 11 + ' GC.Collect(); ' + '"' * 11
                                                     + 'b' + '"' * 13 + ';', 0),
                                                    ('var s = $"{ new { A = c ? 1 : GC.Collect() }.A }";', 1),
                                                    ('var s = $"{ arr[c ? 1 : GC.Collect()] }";', 1)]),
    Rule('BOO-RT-002', 'Environment.GetEnvironmentVariable',
         'Hidden switches (for example BOO_QA_CAPTURABLE) change privacy behaviour and must be auditable.',
         'Read environment variables in one place and pass the value in.', 'Do NOT add another reader.',
         pattern_hits(r'\bEnvironment\s*\.\s*GetEnvironmentVariable\b'),
         [('Environment.GetEnvironmentVariable("X");', 1), ('Func<string, string?> f = Environment.GetEnvironmentVariable;', 1),
          ('using static System.Environment;\n GetEnvironmentVariable("X");', 1)]),
    Rule('BOO-SAFE-003', 'Process.Start', 'Launching a program is a permissioned, security-sensitive action.',
         'Go through the ActionPolicy gate (DesktopTools).', 'Do NOT call Process.Start directly.',
         pattern_hits(r'\bProcess\s*\.\s*Start\b'),
         [('Process.Start("x");', 1), ('using static System.Diagnostics.Process;\n Start("x");', 1)]),
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
          ('// INVARIANT: undo restores bytes, see MissingTests.Roundtrip', 1),
          ('// INVARIANT: undo restores bytes, see UndoJournalTests.NoSuchMethod', 1),
          ('// INVARIANT: undo restores bytes, see UndoJournal.Roundtrip', 1),
          ('// note TODO(#12): tracked only at the start', 1),
          ('// TODO: fix later', 1), ('// TODO(12): no hash', 1),
          ('#pragma warning disable CS0168 // TODO(#1): tracked here', 1),
          ('// INVARIANT: retry because flaky, see e.g. above', 1),
          ('// INVARIANT: no reference', 1),
          ('Go(); // INVARIANT: undo restores bytes, see UndoJournalTests.Roundtrip', 1),
          ('// TODO(#12): tracked', 0),
          ('#pragma warning disable CS0168 // agent workaround, flaky', 1),
          ('#region // a name, not a comment', 0),
          ('Go(); // TODO(#12): tracked', 1),
          ('var u = "http://a//b /* x */";', 0), ('var r = """\n // not a comment\n """;', 0)]),
    Rule('BOO-CMT-002', 'TODO / FIXME / HACK / XXX without an issue link',
         'Untracked debt is invisible.', 'Open a GitHub issue and write TODO(#123): ...', 'Do NOT delete the note '
         'and keep the debt.', untracked_todos,
         [('// TODO: fix later', 1), ('/* FIXME */', 1), ('// TODO(#12): tracked', 0), ('/// HACK in a doc', 1),
          ('#endif // HACK', 1)]),
]
RULES_BY_ID = {rule.id: rule for rule in RULES}


def files(base, top):
    """Every file git would commit under base/top (tracked, plus new files that are not ignored), as relative parts.
    Build output (bin/, obj/) is skipped because it is git-ignored; a file forced into git there is judged."""
    run = subprocess.run(['git', '-C', str(base), 'ls-files', '-z', '-c', '-o', '--exclude-standard', '--', top],
                         capture_output=True)
    if run.returncode:
        raise SystemExit(f'RATCHET FAILED: cannot list files with git in {base} '
                         f'({run.stderr.decode(errors="replace").strip()}).')
    names = sorted({name for name in run.stdout.decode('utf-8').split('\0') if name and (base / name).is_file()})
    return [tuple(name.split('/')) for name in names]


def layout(base):
    """(projects, problems): projects are src/<Name>/ folders that hold a .csproj directly; any C# project or file in
    src/ that sits anywhere else is a problem, because the build compiles it but the ratchet would not judge it."""
    listed = files(base, 'src')
    projects = sorted({parts[1] for parts in listed if len(parts) == 3 and is_project(parts[2])})
    problems = []
    for parts in listed:
        path = '/'.join(parts)
        if is_project(parts[-1]) and len(parts) != 3:
            problems.append(f'{path}: a project outside src/<Name>/ is not judged. Put it at src/<Name>/<Name>.csproj.')
        elif is_csharp(parts[-1]) and (len(parts) < 3 or parts[1] not in projects):
            problems.append(f'{path}: C# file outside a src/<Name>/ project is not judged. Move it into a project.')
    return projects, problems


def is_csharp(name):
    return name.lower().endswith('.cs')  # MSBuild on Windows compiles Evil.CS too


def is_project(name):
    return name.lower().endswith('.csproj')


def load_tests(base):
    KNOWN_TESTS.clear()
    for parts in files(base, 'tests'):
        if is_csharp(parts[-1]):
            text = (base / '/'.join(parts)).read_text(encoding='utf-8-sig', errors='replace')
            for name in re.findall(r'\bclass\s+([A-Z]\w*Tests)\b', text):
                KNOWN_TESTS[name] = KNOWN_TESTS.get(name, '') + text


def msbuild(base, project):
    """(usings, problems) from the project's .csproj files and the Directory.Build.props/.targets above it, read with an
    XML parser. <Using> items are followed (attribute or child-element metadata). Anything that brings code or settings
    in from elsewhere (<Compile Include>, <Import>) or cannot be read fails closed: the ratchet cannot follow it."""
    folder = base / 'src' / project
    paths = sorted(p for p in folder.iterdir() if p.is_file() and is_project(p.name))
    for up in (folder, base / 'src', base):
        paths += [up / name for name in ('Directory.Build.props', 'Directory.Build.targets') if (up / name).is_file()]
    usings, problems = [], []
    for path in paths:
        where = path.relative_to(base).as_posix()
        try:
            elements = list(ElementTree.parse(path).iter())
        except (ElementTree.ParseError, OSError) as error:
            problems.append(f'{where}: cannot be read as XML ({error}), so the ratchet cannot follow it.')
            continue
        for element in elements:
            tag = str(element.tag).split('}')[-1]
            meta = {key.lower(): value for key, value in element.attrib.items()}
            meta.update({str(child.tag).split('}')[-1].lower(): (child.text or '').strip() for child in element})
            if tag == 'Import' or (tag == 'Compile' and 'include' in meta):
                problems.append(f'{where}: <{tag}> brings code or settings in from elsewhere, which the ratchet cannot '
                                f'follow. Keep compiled code as .cs files inside src/<Name>/ (the analyzers of later Phase 1 '
                                f'PRs check compiled code exactly).')
            elif tag == 'Using' and 'include' in meta:
                for target in filter(None, (part.strip() for part in meta['include'].split(';'))):
                    usings.append((True, meta.get('static', '').strip().lower() == 'true', meta.get('alias') or None,
                                   re.sub(r'\s', '', target)))
    return usings, problems


def scan(base):
    """({rule: {project: [(relative file, line), ...]}}, layout problems)"""
    load_tests(base)
    projects, problems = layout(base)
    result = {rule.id: {project: [] for project in projects} for rule in RULES}
    listed = files(base, 'src')
    for project in projects:
        paths = ['/'.join(parts) for parts in listed if parts[1:2] == (project,) and is_csharp(parts[-1])]
        texts = {path: (base / path).read_text(encoding='utf-8-sig') for path in paths}
        usings, msbuild_problems = msbuild(base, project)
        problems += [p for p in msbuild_problems if p not in problems]
        usings += [u for text in texts.values() for u in Source(text).usings if u[0]]  # `global using` lines
        for path in paths:
            source = Source(texts[path], usings)
            for rule in RULES:
                result[rule.id][project].extend((path, n) for n in rule.hits(source))
    return result, problems


def counts(result):
    return {rule: {project: len(hits) for project, hits in by_project.items()} for rule, by_project in result.items()}


def compare(result, baseline, rise_only=False, problems=()):
    """Return (rose, fell) message lists."""
    now = counts(result)
    rose, fell = list(problems), []
    scanned = {project for by_project in now.values() for project in by_project}
    for rule_id, by_project in baseline.items():
        for project, allowed in by_project.items():
            if allowed and project not in scanned:
                rose.append(f'{rule_id}  {project}: baselined at {allowed}, but the project is no longer scanned (moved, '
                            f'renamed or its .csproj removed). Restore it under src/. A real rename needs the owner to override '
                            f'this check once (see docs/quality/RULES.md, Renames).')
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
    result, problems = scan(base)
    rose, fell = compare(result, baseline, rise_only, problems)
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
    print(f'{label} OK: {len(RULES)} rules, {len(result[RULES[0].id])} projects, {total} hits, none above the baseline')
    return 0


def lower(base, baseline_path):
    old = json.loads(baseline_path.read_text(encoding='utf-8')) if baseline_path.is_file() else {'counts': {}}
    result, problems = scan(base)
    if problems:
        print('REFUSED: fix the layout first:\n  ' + '\n  '.join(problems))
        return 1
    now = counts(result)
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
    KNOWN_TESTS.clear()
    KNOWN_TESTS['UndoJournalTests'] = 'public void Roundtrip() { }'
    KNOWN_TESTS['UndoJournal'] = 'public void Roundtrip() { }'  # not a *Tests class: a tag naming it is not exempt
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
        subprocess.run(['git', 'init', '-q', str(tree)], check=True)
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
            (tree / 'src' / 'P').rename(tree / 'src' / 'Q')
            steps += [('renamed project fails the PR\'s own check', check(tree, baseline_path), 1),
                      ('renamed project fails the base check too', check(tree, baseline_path, rise_only=True), 1)]
            (tree / 'src' / 'Q').rename(tree / 'src' / 'P')
            (tree / 'src' / 'Dummy').mkdir()
            (tree / 'src' / 'Dummy' / 'Dummy.csproj').write_text('<Project/>', encoding='utf-8')
            steps += [('an extra empty project changes nothing', check(tree, baseline_path, rise_only=True), 0)]
            (tree / 'src' / 'P' / 'Sub' / 'bin').mkdir(parents=True)
            (tree / 'src' / 'P' / 'Sub' / 'bin' / 'N.cs').write_text('class N { void M() { Thread.Sleep(8); Thread.Sleep(9); } }', encoding='utf-8')
            steps += [('a nested bin/ folder is scanned', check(tree, baseline_path, rise_only=True), 1)]
            (tree / 'src' / 'P' / 'Sub' / 'bin' / 'N.cs').unlink()
            (tree / '.gitignore').write_text('bin/\nobj/\n', encoding='utf-8')
            (tree / 'src' / 'P' / 'bin').mkdir()
            (tree / 'src' / 'P' / 'bin' / 'Gen.cs').write_text('class G { void M() { Thread.Sleep(1); Thread.Sleep(2); } }', encoding='utf-8')
            steps += [('ignored build output is skipped', check(tree, baseline_path, rise_only=True), 0)]
            subprocess.run(['git', '-C', str(tree), 'add', '-f', 'src/P/bin/Gen.cs'], check=True)
            steps += [('a file forced into git under bin/ is judged', check(tree, baseline_path, rise_only=True), 1)]
            subprocess.run(['git', '-C', str(tree), 'rm', '-q', '--cached', 'src/P/bin/Gen.cs'], check=True)
            for stray, body in [('src/Loose.cs', 'class L { }'), ('src/Shared/S.cs', 'class S { }'),
                                ('src/Group/Hack/Hack.csproj', '<Project/>'), ('src/P/Inner/Inner.csproj', '<Project/>')]:
                (tree / stray).parent.mkdir(parents=True, exist_ok=True)
                (tree / stray).write_text(body, encoding='utf-8')
                steps += [(f'{stray} outside a judged project fails', check(tree, baseline_path, rise_only=True), 1)]
                (tree / stray).unlink()
            (tree / 'src' / 'P' / 'Glob.cs').write_text('global using static System.GC;', encoding='utf-8')
            code.write_text('class A { void M() { Collect(); } }', encoding='utf-8')
            steps += [('a global using static in another file is followed', check(tree, baseline_path, rise_only=True), 1)]
            (tree / 'src' / 'P' / 'Glob.cs').unlink()
            (tree / 'src' / 'P' / 'P.csproj').write_text('<Project><ItemGroup><Using Include="System.GC" Alias="G" />'
                                                         '</ItemGroup></Project>', encoding='utf-8')
            code.write_text('class A { void M() { G.Collect(); } }', encoding='utf-8')
            steps += [('a <Using Alias> in the .csproj is followed', check(tree, baseline_path, rise_only=True), 1)]
            code.write_text('class A { void M() { Collect(); } }', encoding='utf-8')
            for name, xml in [('Static attribute', '<Using Include="System.GC" Static="true" />'),
                              ('child-element metadata, single quotes', "<Using Include='System.GC'><Static>true</Static></Using>")]:
                (tree / 'src' / 'P' / 'P.csproj').write_text(f'<Project><ItemGroup>{xml}</ItemGroup></Project>', encoding='utf-8')
                steps += [(f'a .csproj <Using> with {name} is followed', check(tree, baseline_path, rise_only=True), 1)]
            (tree / 'src' / 'P' / 'P.csproj').write_text('<Project/>', encoding='utf-8')
            (tree / 'src' / 'P' / 'B.cs').write_text('using static System.GC;', encoding='utf-8')
            steps += [('a plain (not global) using in another file is not applied', check(tree, baseline_path, rise_only=True), 0)]
            (tree / 'src' / 'P' / 'B.cs').unlink()
            (tree / 'Directory.Build.props').write_text('<Project><ItemGroup><Using Include="System.GC" Static="true" />'
                                                        '</ItemGroup></Project>', encoding='utf-8')
            steps += [('a <Using> in Directory.Build.props is followed', check(tree, baseline_path, rise_only=True), 1)]
            (tree / 'Directory.Build.props').unlink()
            code.write_text('class A { void M() { } }', encoding='utf-8')
            for name, xml in [('<Import>', '<Project><Import Project="../U.props" /></Project>'),
                              ('<Compile Include>', '<Project><ItemGroup><Compile Include="Evil.txt" /></ItemGroup></Project>'),
                              ('unreadable XML', '<Project><ItemGroup></Project>')]:
                (tree / 'src' / 'P' / 'P.csproj').write_text(xml, encoding='utf-8')
                steps += [(f'a .csproj with {name} fails closed', check(tree, baseline_path, rise_only=True), 1)]
            (tree / 'src' / 'P' / 'P.csproj').write_text('<Project><ItemGroup><Compile Remove="Old.cs" /></ItemGroup></Project>',
                                                         encoding='utf-8')
            steps += [('<Compile Remove> is harmless', check(tree, baseline_path, rise_only=True), 0)]
            (tree / 'src' / 'P' / 'P.csproj').write_text('<Project/>', encoding='utf-8')
            (tree / 'src' / 'P' / 'Evil.CS').write_text('class E { void M() { GC.Collect(); } }', encoding='utf-8')
            steps += [('an upper-case .CS file is judged', check(tree, baseline_path, rise_only=True), 1)]
            (tree / 'src' / 'P' / 'Evil.CS').unlink()
            (tree / 'src' / 'Loose.cs').write_text('class L { }', encoding='utf-8')
            steps += [('--lower refuses while the layout has a problem', lower(tree, baseline_path), 1)]
            (tree / 'src' / 'Loose.cs').unlink()
            code.write_text('class A { void M() { } }', encoding='utf-8')
            steps += [('check fall fails until lowered', check(tree, baseline_path), 1),
                      ('rise-only accepts a fall', check(tree, baseline_path, rise_only=True), 0),
                      ('lower accepts fall', lower(tree, baseline_path), 0), ('check after lower passes', check(tree, baseline_path), 0)]
        finally:
            sys.stdout.close()
            sys.stdout = quiet
        failures += [f'logic: {name} returned {got}, expected {want}' for name, got, want in steps if got != want]
        (tree / 'tests' / 'T').mkdir(parents=True)
        (tree / 'tests' / 'T' / 'UndoJournalTests.cs').write_text(
            'public class UndoJournalTests { [Fact] public void Roundtrip() { } }', encoding='utf-8')
        load_tests(tree)
        tagged = Source('class C {\n  // INVARIANT: undo restores bytes, see UndoJournalTests.Roundtrip\n}\n')
        if comment_lines(tagged):
            failures.append('logic: load_tests did not register a real test class under tests/')
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
    for _, hits in scan(base)[0][rule_id].items():
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
