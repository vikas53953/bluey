"""Comment lint (BOO-CMT-003): a pull request may not ADD comment lines that are not /// doc comments. Never reads secrets.

  python -I scripts/quality/comment_lint.py --base REV [--head REV] [--root DIR] [--ratchet FILE]
      Compare the merge base of REV and --head (default HEAD) with --head. For every C# file under src/ (`.cs` in any
      letter case) a comment line on the head is NEW when the same file on the base has no equal line (whitespace
      normalised). A renamed file is compared with the base file it was renamed from (git rename detection); a whole
      new file's comments are all new. Exit 1 when any new comment line is found, 2 when the check cannot run.
  python -I scripts/quality/comment_lint.py --self-test
      Planted base/head pairs in throwaway git repositories, each with an exact expected result.
  python -I <base copy of comment_lint.py> --root . --base BASE_SHA
      CI on a pull request: the BASE branch's own copy (with the base branch's ratchet.py next to it) judges the PR,
      so editing the PR's copy changes nothing.

What counts as a comment, and what is exempt, is decided by the quality ratchet's own C# lexer and rule BOO-CMT-001
(scripts/quality/ratchet.py, imported, never copied): strings, interpolation holes, verbatim and raw strings and `#`
directives behave exactly as there; whole-line `// INVARIANT: ... see NameTests.Method` naming a real test under the
head's tests/, and whole-line `// TODO(#123): ...`, are allowed. A `///` line counts as a doc comment only in a block
of whole-line `///` lines directly above a declaration (type, member, delegate, enum member; attribute lines may sit
between); anywhere else (a method body, an accessor, before a blank line or a `}`) it is a new comment too. Old comments are not judged here: the ratchet counts
them and that count may only go down. Rule page: docs/quality/comment-lint.md.
"""
import importlib.util
import pathlib
import re
import subprocess
import sys
import tempfile
from collections import Counter

HERE = pathlib.Path(__file__).resolve().parent
RULE_ID = 'BOO-CMT-003'
RULE_DOC = 'docs/quality/comment-lint.md'
WHAT = 'New comment line that is not an XML doc comment (///).'
WHY = ('Agents write comments to justify a workaround ("retry because flaky") instead of fixing it, and a rule written '
       'in a comment is never checked.')
FIX = ('If it explains a workaround, fix the cause and delete the comment. If it states a rule, write a test and use '
       '"// INVARIANT: ... see NameTests.Method" (a real test under tests/). If it documents an API, use ///. '
       'Debt: open an issue and write "// TODO(#123): ..." on its own line.')
DONT = ('reword, split or hide the comment (a string, a #region name, an attribute) to dodge the check, and do '
        'not edit this script or its workflow to make the PR pass: the base branch\'s copy judges it too.')
LINE_BREAKS = re.compile('\r\n?|[\u0085\u2028\u2029]')  # the same line breaks ratchet.lex turns into \n


class LintError(Exception):
    pass


def load_ratchet(path):
    """Import ratchet.py from a file (python -I does not put the script's folder on sys.path)."""
    path = pathlib.Path(path)
    if not path.is_file():
        raise LintError(f'the quality ratchet is missing at {path}; its lexer decides what a comment is')
    spec = importlib.util.spec_from_file_location('boo_quality_ratchet', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    for name in ('Source', 'comment_lines', 'load_tests', 'is_csharp', 'KNOWN_TESTS'):
        if not hasattr(module, name):
            raise LintError(f'{path} has no {name}; comment_lint needs the ratchet\'s lexer and BOO-CMT-001 rule')
    return module


def git(repo, *args, data=None):
    run = subprocess.run(['git', '-C', str(repo), *args], capture_output=True, input=data)
    if run.returncode:
        raise LintError(f'git {" ".join(args)} failed: {run.stderr.decode(errors="replace").strip()}')
    return run.stdout


def blob_text(repo, rev, path):
    return git(repo, 'show', f'{rev}:{path}').decode('utf-8-sig', errors='replace')


def normalised_lines(text):
    return [' '.join(line.split()) for line in LINE_BREAKS.sub('\n', text).split('\n')]


def changed_files(repo, base, head):
    """[(head path, base path or None)] for every C# file under src/ that was added, changed, renamed or copied."""
    raw = git(repo, 'diff', '-M', '--name-status', '-z', base, head)
    fields = raw.decode('utf-8').split('\0')
    result, i = [], 0
    while i < len(fields) and fields[i]:
        status = fields[i]
        if status[0] in 'RC':
            old, new = fields[i + 1], fields[i + 2]
            i += 3
        else:
            old = new = fields[i + 1]
            i += 2
        if status[0] == 'D':
            continue
        if not new.startswith('src/') or not ratchet.is_csharp(new.rsplit('/', 1)[-1]):
            continue
        if status[0] == 'A' or status[0] == 'C':
            result.append((new, None))  # a copy is a new file: its comments are new
        else:
            result.append((new, old))
    return result


def load_head_tests(repo, head):
    """Fill ratchet.KNOWN_TESTS with the head's tests/ exactly as the ratchet does, by giving its own load_tests() a
    throwaway git tree holding the head's tests/ files."""
    names = [n for n in git(repo, 'ls-tree', '-r', '-z', '--name-only', head, '--', 'tests').decode('utf-8').split('\0')
             if n and ratchet.is_csharp(n.rsplit('/', 1)[-1])]
    with tempfile.TemporaryDirectory() as temp:
        tree = pathlib.Path(temp)
        git(tree, 'init', '-q')
        for name in names:
            target = tree / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(git(repo, 'show', f'{head}:{name}'))
        ratchet.load_tests(tree)


# A `///` line is a doc comment only in a block of whole-line `///` lines directly above a declaration (attribute lines
# may sit between). The ratchet treats every `///` as doc, so `/// retry because flaky` in a method body would pass it.
MODIFIERS = r'(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|ref|unsafe|new|file)\s+)*'
TYPE_HEAD = re.compile(r'(?:^|[\s;{}\]])' + MODIFIERS + r'(class|struct|interface|enum|record)\s+[A-Za-z_@]')
TYPE_DECL = re.compile(r'(?:^|[\s\]])' + MODIFIERS + r'(class|struct|interface|enum|record|delegate)\s+[A-Za-z_@]')


def line_contexts(code):
    """For each line (1-based index), the kinds of the braces open at its start: 'ns', 'type', 'enum' or 'other'.
    The lexer blanks string text but keeps the quote marks and the closing `}` of interpolation holes, so a brace only
    counts when an even number of `"` precede it (string quotes and hole strings come in pairs)."""
    contexts, stack, quotes, head = [None, ()], [], 0, []
    for char in code:
        if char == '\n':
            contexts.append(tuple(stack))
        if char == '"':
            quotes += 1
        if quotes % 2:
            continue
        if char == '{':
            text = ''.join(head)
            match = TYPE_HEAD.search(text)
            kind = ('enum' if match.group(1) == 'enum' else 'type') if match else (
                'ns' if re.search(r'\bnamespace\b', text) else 'other')
            stack.append(kind)
            head = []
        elif char == '}':
            if stack:
                stack.pop()
            head = []
        elif char == ';':
            head = []
        else:
            head.append(char)
    return contexts


def misplaced_doc_lines(source):
    """Lines holding a `///` comment that is not part of a doc block directly above a declaration."""
    code_lines = source.views[0].code_lines
    contexts = line_contexts(source.views[0].code)
    whole_docs = {line for line, _, kind, whole in source.comments if kind == 'doc' and whole}
    bad = {line for line, _, kind, whole in source.comments if kind == 'doc' and not whole}
    for first in sorted(whole_docs):
        if first - 1 in whole_docs:
            continue
        last = first
        while last + 1 in whole_docs:
            last += 1
        if not declaration_follows(code_lines, contexts, first, last):
            bad.update(range(first, last + 1))
    return sorted(bad)


def declaration_follows(code_lines, contexts, first, last):
    where = contexts[first]
    if any(kind == 'other' for kind in where):
        return False  # inside a method, accessor or initializer
    k = last  # code_lines is 0-based: code_lines[last] is the line after the block
    while k < len(code_lines):
        text = code_lines[k].strip()
        if not text.startswith('['):
            break
        depth, j = 0, 0
        while True:  # skip one attribute section, which may span lines
            for j, char in enumerate(text):
                depth += (char == '[') - (char == ']')
                if depth == 0:
                    break
            if depth == 0 or k + 1 >= len(code_lines):
                break
            k += 1
            text = code_lines[k].strip()
        rest = text[j + 1:].strip() if depth == 0 else ''
        if rest and not rest.startswith('['):
            text = rest
            break
        k += 1
    else:
        return False
    if not text or text.startswith('}') or text.startswith('#'):
        return False
    if where and where[-1] in ('type', 'enum'):
        return True
    return bool(TYPE_DECL.search(' ' + text))


def new_comments(repo, base, head):
    """[(path, line)] of every comment line on head that BOO-CMT-001 counts and that is not on the base."""
    merge_base = git(repo, 'merge-base', base, head).decode().strip()
    if not merge_base:
        raise LintError(f'{base} and {head} share no history')
    load_head_tests(repo, head)
    found = []
    for path, old in changed_files(repo, merge_base, head):
        head_text = blob_text(repo, head, path)
        source = ratchet.Source(head_text)
        flagged = sorted(set(ratchet.comment_lines(source)) | set(misplaced_doc_lines(source)))
        if not flagged:
            continue
        head_lines = normalised_lines(head_text)
        if len(head_lines) != len(source.views[0].code_lines):
            raise LintError(f'{path}: line count differs from the ratchet lexer; line breaks no longer agree')
        available = Counter(normalised_lines(blob_text(repo, merge_base, old))) if old else Counter()
        for line in flagged:
            text = head_lines[line - 1]
            if available[text] > 0:
                available[text] -= 1
            else:
                found.append((path, line))
    return found


def check(repo, base, head):
    try:
        found = new_comments(repo, base, head)
    except LintError as error:
        print(f'COMMENT LINT FAILED TO RUN ({RULE_ID}): {error}')
        return 2
    if found:
        print(f'COMMENT LINT FAILED: {len(found)} new comment line(s) that are not /// doc comments.\n')
        print(f'  What:  {WHAT}\n  Why:   {WHY}\n  Fix:   {FIX}\n  Do NOT: {DONT}\n  Rule:  {RULE_DOC}\n')
        for path, line in found:
            print(f'{RULE_ID}  {path}:{line}  new comment')  # the comment text is never printed (it could hold a key)
        return 1
    print(f'COMMENT LINT OK ({RULE_ID}): no new non-doc comment lines in src/ since the merge base of {base}')
    return 0


# ---------------------------------------------------------------------------------------------------------------------
# Self-test: planted base/head pairs in throwaway git repositories.
# ---------------------------------------------------------------------------------------------------------------------
TEST_FILE = 'public class UndoJournalTests { [Fact] public void Roundtrip() { } }\n'
BASE_A = 'class A {\n  void M() {\n    // explains why the loop exists\n    Go();\n  }\n}\n'
DOCS_OK = '''namespace N
{
    /// <summary>A type.</summary>
    public sealed partial class D
    {
        private readonly string _s = $"{(a ? "}" : "{")} {{ }}";
        private readonly string _r = $$"""{{x}} } { """;
        /// <summary>A field.</summary>
        private int _f;
        /// <summary>A property.</summary>
        public int P { get; set; }
        /// <summary>A method</summary>
        /// <param name="x">X.</param>
        [Obsolete("a ] b")]
        [return: NotNull]
        public void M(int x) { Go(); }
        /// <summary>Attribute and member on one line.</summary>
        [Fact] public void T() { }
        /// <summary>Multi-line attribute.</summary>
        [Theory(
            Skip = "no")]
        public void U() { }
        /// <summary>Nested type.</summary>
        private record struct R(int A);
    }

    /// <summary>A delegate.</summary>
    public delegate void Handler();

    /// <summary>An enum.</summary>
    internal enum E
    {
        /// <summary>First.</summary>
        First,
        /// <summary>Second.</summary>
        Second = 2,
    }
}
'''


def planted(base_files, head_files):
    """Two commits in a fresh repo; returns the new comments found between them."""
    with tempfile.TemporaryDirectory() as temp:
        repo = pathlib.Path(temp)
        git(repo, 'init', '-q')
        git(repo, 'config', 'user.email', 'self-test@example.invalid')
        git(repo, 'config', 'user.name', 'self-test')
        git(repo, 'config', 'core.autocrlf', 'false')
        for files in (base_files, head_files):
            git(repo, 'rm', '-r', '-q', '--ignore-unmatch', '.')
            for name, text in files.items():
                (repo / name).parent.mkdir(parents=True, exist_ok=True)
                (repo / name).write_text(text, encoding='utf-8', newline='')
            git(repo, 'add', '-A')
            git(repo, 'commit', '-q', '--allow-empty', '-m', 'step')
        return new_comments(repo, 'HEAD~1', 'HEAD')


def self_test():
    a = 'src/P/A.cs'
    big = 'class B {\n' + ''.join(f'  int F{k}() => {k};\n' for k in range(30)) + '  // old note kept\n}\n'
    cases = [
        ('an added // retry because flaky fails',
         {a: BASE_A}, {a: BASE_A.replace('    Go();', '    // retry because flaky\n    Go();')}, [(a, 4)]),
        ('a moved, re-indented unchanged comment passes',
         {a: BASE_A}, {a: 'class A {\n  void M() {\n    Go();\n  }\n        // explains why the loop exists\n}\n'}, []),
        ('an added /// doc comment passes',
         {a: BASE_A}, {a: '/// <summary>Doc.</summary>\n' + BASE_A}, []),
        ('an INVARIANT naming a real test passes',
         {a: BASE_A, 'tests/T/UndoJournalTests.cs': TEST_FILE},
         {a: '// INVARIANT: undo restores bytes, see UndoJournalTests.Roundtrip\n' + BASE_A,
          'tests/T/UndoJournalTests.cs': TEST_FILE}, []),
        ('an INVARIANT naming a missing test fails',
         {a: BASE_A, 'tests/T/UndoJournalTests.cs': TEST_FILE},
         {a: '// INVARIANT: undo restores bytes, see UndoJournalTests.Missing\n' + BASE_A,
          'tests/T/UndoJournalTests.cs': TEST_FILE}, [(a, 1)]),
        ('a // inside a string, verbatim or raw string passes',
         {a: BASE_A}, {a: BASE_A.replace('    Go();', '    var u = "http://a//b /* x */";\n'
                                                    '    var v = @"c:\\\\x // y";\n    var r = """\n // no\n """;\n    Go();')},
         []),
        ('a whole new file: every comment line is new',
         {a: BASE_A}, {a: BASE_A, 'src/P/New.cs': 'class N {\n  // one\n  /* two\n     three */\n  /// doc\n}\n'},
         [('src/P/New.cs', 2), ('src/P/New.cs', 3), ('src/P/New.cs', 4), ('src/P/New.cs', 5)]),
        ('a /// in a method body fails',
         {a: BASE_A}, {a: BASE_A.replace('    Go();', '    /// retry because flaky\n    Go();')}, [(a, 4)]),
        ('a /// at the end of a code line fails',
         {a: BASE_A}, {a: BASE_A.replace('    Go();', '    Go(); /// flaky')}, [(a, 4)]),
        ('/// above a class, method, property, field, enum member and attributes passes',
         {a: BASE_A}, {a: BASE_A, 'src/P/D.cs': DOCS_OK}, []),
        ('a /// block, a blank line, then code fails (both lines)',
         {a: BASE_A}, {a: BASE_A, 'src/P/D.cs': 'namespace N;\n/// <summary>x</summary>\n/// y\n\npublic class D { }\n'},
         [('src/P/D.cs', 2), ('src/P/D.cs', 3)]),
        ('a /// above a closing brace, in an accessor and above top-level code fails',
         {a: BASE_A}, {a: BASE_A, 'src/P/D.cs': 'class D {\n  int P {\n    /// in accessor\n    get => 1;\n  }\n'
                                                   '  /// orphan\n}\n/// top-level\nGo();\n'},
         [('src/P/D.cs', 3), ('src/P/D.cs', 6), ('src/P/D.cs', 8)]),
        ('an old misplaced /// that is unchanged passes',
         {a: BASE_A.replace('    Go();', '    /// old\n    Go();')},
         {a: BASE_A.replace('    Go();', '    /// old\n    Go();').replace('class A', 'class A2')}, []),
        ('a renamed file keeps its old comments',
         {'src/P/B.cs': big}, {'src/Q/Renamed.cs': big}, []),
        ('a renamed file with one added comment fails on that line only',
         {'src/P/B.cs': big}, {'src/Q/Renamed.cs': big.replace('  int F3()', '  // added\n  int F3()')},
         [('src/Q/Renamed.cs', 5)]),
        ('a whole-line TODO(#12) passes, a bare TODO fails',
         {a: BASE_A}, {a: '// TODO(#12): tracked\n// TODO: later\n' + BASE_A}, [(a, 2)]),
        ('a duplicated old comment counts once as new',
         {a: BASE_A}, {a: BASE_A.replace('    Go();', '    Go();\n    // explains why the loop exists')}, [(a, 5)]),
        ('a comment added at the end of a code line fails',
         {a: BASE_A}, {a: BASE_A.replace('    Go();', '    Go(); // flaky')}, [(a, 4)]),
        ('a // on a #pragma line fails, a #region name passes',
         {a: BASE_A}, {a: '#region a // name\n#pragma warning disable CS0168 // flaky\n' + BASE_A + '#endregion\n'},
         [(a, 2)]),
        ('an upper-case .CS file is judged; files outside src/ are not',
         {a: BASE_A}, {a: BASE_A, 'src/P/Evil.CS': '// new\nclass E { }\n', 'tools/T/X.cs': '// new\n'},
         [('src/P/Evil.CS', 1)]),
        ('a deleted file and an unchanged file pass',
         {a: BASE_A, 'src/P/Gone.cs': '// old\n'}, {a: BASE_A}, []),
        ('CR LF line ends: an unchanged file passes, an added comment is found',
         {a: BASE_A.replace('\n', '\r\n')}, {a: BASE_A.replace('\n', '\r\n').replace('    Go();', '    Go(); // x')},
         [(a, 4)]),
    ]
    failures = []
    for name, base_files, head_files, expected in cases:
        try:
            got = planted(base_files, head_files)
        except LintError as error:
            got = f'error: {error}'
        if got != expected:
            failures.append(f'{name}: expected {expected}, got {got}')
    if failures:
        print('SELF-TEST FAILED:\n  ' + '\n  '.join(failures))
        return 1
    print(f'SELF-TEST OK ({RULE_ID}): {len(cases)} planted base/head pairs with exact expected results, '
          f'using the ratchet lexer from {ratchet_path.name}')
    return 0


def argument(flag):
    if flag in sys.argv:
        index = sys.argv.index(flag)
        return sys.argv[index + 1] if index + 1 < len(sys.argv) else ''
    return None


if __name__ == '__main__':
    repo_root = pathlib.Path(argument('--root')).resolve() if argument('--root') else HERE.parent.parent
    ratchet_path = pathlib.Path(argument('--ratchet')).resolve() if argument('--ratchet') else HERE / 'ratchet.py'
    try:
        ratchet = load_ratchet(ratchet_path)
    except LintError as problem:
        print(f'COMMENT LINT FAILED TO RUN ({RULE_ID}): {problem}')
        sys.exit(2)
    if '--self-test' in sys.argv:
        sys.exit(self_test())
    if not argument('--base'):
        print(__doc__)
        sys.exit(2)
    sys.exit(check(repo_root, argument('--base'), argument('--head') or 'HEAD'))
