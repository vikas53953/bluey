"""BOO-TEST-002: a pull request that changes product code may not also weaken existing tests unless a reviewer signs it.

  python -I scripts/quality/test_guard.py --self-test               planted base/head pairs in throwaway git repos
  python -I scripts/quality/test_guard.py --event "$GITHUB_EVENT_PATH"   (in the test-guard workflow)
  python -I scripts/quality/test_guard.py --base SHA --head SHA [--body FILE]   (local check of a branch)

Why: the builder must not be able to make its own work pass by loosening the tests that judge it. So in ONE pull
request these two may not meet without a signed justification:
  1. a product change: anything under src/, anything under tools/ outside a test project, any Directory.Build.* file;
  2. a weakened test: an existing file under tests/ or in a test project under tools/ deleted or edited (a line
     removed or changed), fewer assertion-shaped calls or test attributes ([Fact], [Theory], [InlineData], ...) in a
     changed file, a skip added (Skip =, a Skip trait, Assert.Skip, SkippableFact, ...), a new #if, a test that now
     starts with return, or a non-additive change to the frozen oracle (docs/verification/test-oracle.json,
     docs/verification/test-oracle-history/**). A pure `test_oracle.py --extend` (only new files frozen, every frozen
     hash unchanged) is adding tests, not weakening them.
Adding new test files, or only adding lines (new tests) to an existing one, never fails. A PR with no product change is
out of scope here (the frozen oracle guards it).

It passes anyway when the PR body has a section `## Test change justification` that names every weakened file (its
path) and every weakened test method (its name), each on a line that also says why, and carries the line
`Test change reviewed: <full 40-character head SHA> by <reviewer>` for the CURRENT head, so a later push needs a new
sign-off. HTML comments and code blocks in the body do not count. Anything this check cannot read (the event, git)
fails closed. Never reads secrets.
"""
import difflib
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile

RULE = 'BOO-TEST-002'
ORACLE = 'docs/verification/test-oracle.json'
ORACLE_HISTORY = 'docs/verification/test-oracle-history/'
SECTION = re.compile(r'^\s{0,3}##\s+Test change justification\s*#*\s*$', re.IGNORECASE)
NEXT_SECTION = re.compile(r'^\s{0,3}#{1,2}\s')
SIGN_OFF = re.compile(r'^\W*Test change reviewed:\s*([0-9A-Fa-f]{7,40})\b\s+by\s+(\S.*?)\s*$', re.IGNORECASE)

TEST_ATTRIBUTE = re.compile(r'\[\s*(?:Xunit\s*\.\s*)?(?:Fact|Theory|InlineData|MemberData|ClassData|SkippableFact|'
                            r'SkippableTheory|TestMethod|DataRow|Test|TestCase)\b')
ASSERTION = re.compile(r'(?<![\w.])Assert\w*\s*\.\s*\w+\s*[(<]'      # Assert.Equal(, AssertEx.Same(, Assert.Throws<
                       r'|\.\s*Should\w*\s*\('                           # .Should() (FluentAssertions), .ShouldBe(
                       r'|\.\s*Verify\w*\s*\('                           # Moq .Verify(, .VerifyAll(
                       r'|(?<![\w.])Verify\w*\s*\('                      # Verify snapshot tests, Verify helpers
                       r'|(?<![\w.])(?:Expect|Ensure)\w*\s*\(')          # Expect(...), EnsureX(...) helpers
SKIP = re.compile(r'\bSkip\s*=|\bExplicit\s*=\s*true|Trait\s*\(\s*"Category"\s*,\s*"(?:Skip|Ignore|Manual|Explicit)'
                  r'|\bAssert\s*\.\s*Skip\w*\s*\(|\bSkip\s*\.\s*(?:If|IfNot|Unless|When)\s*\('
                  r'|\bSkippable(?:Fact|Theory)\b'
                  r'|\[\s*Ignore\b', re.IGNORECASE)
IF_DIRECTIVE = re.compile(r'^\s*#\s*if\b', re.MULTILINE)
TEST_METHOD = re.compile(r'\[\s*(?:Xunit\s*\.\s*)?(?:Fact|Theory|SkippableFact|SkippableTheory)\b[^\]]*\]'
                         r'(?:\s*\[[^\]]*\])*\s*[\w<>\[\],.?\s]*?\b(\w+)\s*\([^)]*\)\s*\{')
COMMENTS = re.compile(r'//[^\n]*|/\*.*?\*/', re.DOTALL)


class Unreadable(Exception):
    """Git or the event could not be read: the check fails closed."""


def git(args, cwd):
    out = subprocess.run(['git', '-c', 'core.quotePath=false'] + args, cwd=cwd, capture_output=True)
    if out.returncode != 0:
        raise Unreadable(f'git {args[0]} failed')
    return out.stdout


def show(sha, path, cwd):
    """The file at a commit, as text with LF line ends (None when the commit has no such file)."""
    out = subprocess.run(['git', 'cat-file', '-e', f'{sha}:{path}'], cwd=cwd, capture_output=True)
    if out.returncode != 0:
        return None
    return git(['show', f'{sha}:{path}'], cwd).decode('utf-8-sig', 'replace').replace('\r\n', '\n')


def test_project_dirs(shas, cwd):
    """Folders under tools/ that hold a test project (xUnit or IsTestProject) at any of the given commits."""
    dirs = set()
    for sha in shas:
        names = git(['ls-tree', '-r', '-z', '--name-only', sha, '--', 'tools'], cwd).decode('utf-8', 'replace')
        for name in filter(None, names.split('\0')):
            if name.lower().endswith('.csproj'):
                text = show(sha, name, cwd) or ''
                if '<IsTestProject>true</IsTestProject>' in text or 'xunit' in text.lower():
                    dirs.add(name.rsplit('/', 1)[0] + '/')
    return dirs


def is_test_file(path, test_dirs):
    return path.startswith('tests/') or any(path.startswith(d) for d in test_dirs)


def is_product_file(path, test_dirs):
    if is_test_file(path, test_dirs):
        return False
    return (path.startswith(('src/', 'tools/')) or path.rsplit('/', 1)[-1].startswith('Directory.Build.'))


def counts(text):
    code = COMMENTS.sub('', text)
    early = sum(1 for m in TEST_METHOD.finditer(code) if re.match(r'\s*return\b', code[m.end():]))
    return {'assertion-shaped calls': len(ASSERTION.findall(code)),
            'test attributes': len(TEST_ATTRIBUTE.findall(code)),
            'skip markers': len(SKIP.findall(code)), '#if directives': len(IF_DIRECTIVE.findall(code)),
            'tests that start with return': early}


def test_methods(text):
    """{method name: body text} for every [Fact]/[Theory] method (comments removed)."""
    code = COMMENTS.sub('', text)
    out = {}
    for m in TEST_METHOD.finditer(code):
        depth, i = 1, m.end()
        while i < len(code) and depth:
            depth += {'{': 1, '}': -1}.get(code[i], 0)
            i += 1
        out[m.group(1)] = ' '.join(code[m.start():i].split())
    return out


def weakened_file(path, old, new):
    """(reasons, weakened method names) for one existing test file; empty reasons when nothing was weakened."""
    if new is None:
        return ['an existing test file was deleted'], sorted(test_methods(old)) if path.endswith('.cs') else []
    reasons = []
    removed = [line for line in difflib.ndiff(old.split('\n'), new.split('\n'))
               if line.startswith('- ') and line[2:].strip()]
    if removed:
        reasons.append(f'{len(removed)} existing line(s) removed or changed')
    methods = []
    if path.endswith('.cs'):
        before, after = counts(old), counts(new)
        for name in ('assertion-shaped calls', 'test attributes'):
            if after[name] < before[name]:
                reasons.append(f'fewer {name} ({before[name]} -> {after[name]})')
        for name in ('skip markers', '#if directives', 'tests that start with return'):
            if after[name] > before[name]:
                reasons.append(f'more {name} ({before[name]} -> {after[name]})')
        old_m, new_m = test_methods(old), test_methods(new)
        methods = sorted(n for n, body in old_m.items() if new_m.get(n) != body)
    return reasons, methods


def oracle_weakened(changes, base, head, cwd):
    """Reasons the frozen oracle changed in a way other than a pure `--extend` (new files only)."""
    touched = [(s, p) for s, p in changes if p == ORACLE or p.startswith(ORACLE_HISTORY)]
    if not touched:
        return []
    reasons = [f'{p} {"deleted" if s == "D" else "edited"} (oracle history may only grow)'
               for s, p in touched if p.startswith(ORACLE_HISTORY) and s != 'A']
    if any(p == ORACLE for _, p in touched):
        try:
            old = json.loads(show(base, ORACLE, cwd) or 'null')
            new = json.loads(show(head, ORACLE, cwd) or 'null')
            old_files, new_files = (old or {}).get('files') or {}, (new or {}).get('files') or {}
            lost = sorted(p for p, h in old_files.items() if new_files.get(p) != h)
            if new is None:
                reasons.append(f'{ORACLE} deleted')
            elif lost or new.get('changedFiles') or new.get('removedFiles'):
                names = lost or (new.get('changedFiles') or []) + (new.get('removedFiles') or [])
                reasons.append(f'{ORACLE} changes or removes frozen files ({", ".join(names[:5])}'
                               f'{" ..." if len(names) > 5 else ""}), not only adds new ones')
        except (ValueError, AttributeError, TypeError):
            reasons.append(f'{ORACLE} could not be read as an oracle manifest')
    return reasons


def visible_lines(body):
    """Body lines a reader of the PR page sees as text: HTML comments and fenced code blocks removed."""
    body = re.sub(r'<!--.*?(?:-->|$)', '', (body or '').replace('\r\n', '\n'), flags=re.DOTALL)
    lines, fence = [], None
    for line in body.split('\n'):
        m = re.match(r'^\s{0,3}(`{3,}|~{3,})', line)
        if fence:
            if m and m.group(1)[0] == fence[0] and len(m.group(1)) >= len(fence):
                fence = None
            continue
        if m:
            fence = m.group(1)
            continue
        if not re.match(r'^(?: {4}|\t)', line):
            lines.append(line)
    return lines


def justification(body):
    """Lines of the `## Test change justification` section (None when there is no such section)."""
    lines = visible_lines(body)
    for i, line in enumerate(lines):
        if SECTION.match(line):
            out = []
            for later in lines[i + 1:]:
                if NEXT_SECTION.match(later):
                    break
                out.append(later)
            return out
    return None


def judge(body, base, head, cwd='.'):
    """(problems, summary). problems empty means the PR passes. Raises Unreadable when git cannot answer."""
    head_sha = git(['rev-parse', '--verify', f'{head}^{{commit}}'], cwd).decode().strip()
    merge_base = git(['merge-base', base, head_sha], cwd).decode().strip()
    raw = git(['diff', '--name-status', '--no-renames', '-z', merge_base, head_sha], cwd).decode('utf-8', 'replace')
    parts = [p for p in raw.split('\0') if p]
    changes = list(zip(parts[0::2], parts[1::2]))
    test_dirs = test_project_dirs([merge_base, head_sha], cwd)
    product = sorted(p for _, p in changes if is_product_file(p, test_dirs))
    if not product:
        return [], 'no product change (src/, tools/ outside test projects, Directory.Build.*): out of scope'
    weakened = []   # (path, reasons, methods)
    for status, path in changes:
        if status != 'A' and is_test_file(path, test_dirs):
            old = show(merge_base, path, cwd)
            if old is None:
                continue
            reasons, methods = weakened_file(path, old, None if status == 'D' else show(head_sha, path, cwd))
            if reasons:
                weakened.append((path, reasons, methods))
    oracle = oracle_weakened(changes, merge_base, head_sha, cwd)
    if oracle:
        weakened.append((ORACLE, oracle, []))
    if not weakened:
        return [], f'product change ({len(product)} file(s)) and no existing test weakened'
    more = f' and {len(product) - 1} more' if len(product) > 1 else ''
    where = f'this PR changes product code ({product[0]}{more})'
    problems = [f'{where} and weakens {path}: {"; ".join(reasons)}'
                + (f' (test methods: {", ".join(methods[:8])}{" ..." if len(methods) > 8 else ""})' if methods else '')
                for path, reasons, methods in weakened]
    section = justification(body)
    if section is None:
        return problems + ['the PR body has no "## Test change justification" section naming each weakened test and '
                           'why, with a "Test change reviewed: <head SHA> by <reviewer>" line'], ''
    missing = []
    for path, _, methods in weakened:
        for name in [path] + methods:
            pattern = re.compile(r'(?<![\w/.-])' + re.escape(name) + r'(?![\w/-])')
            lines = [line for line in section if pattern.search(line)]
            if not lines:
                missing.append(f'"{name}" is not named in the justification section')
            elif not any(len(re.findall(r'[A-Za-z]{2,}', pattern.sub(' ', line))) >= 3 for line in lines):
                missing.append(f'"{name}" is named but no reason is given on its line (say why, in words)')
    signs = [SIGN_OFF.match(line) for line in section]
    signs = [m for m in signs if m]
    current = [m for m in signs if m.group(1).lower() == head_sha.lower()]
    if not current:
        if signs:
            missing.append(f'the sign-off is for {signs[0].group(1)[:12]}, not the current head {head_sha[:12]}: every '
                           f'push needs a fresh "Test change reviewed: {head_sha} by <reviewer>" line')
        else:
            missing.append(f'no reviewer sign-off: add "Test change reviewed: {head_sha} by <reviewer>" to the section')
    if missing:
        return problems + missing, ''
    return [], (f'{len(weakened)} weakened test file(s) justified and signed by {current[0].group(2)} '
                f'for {head_sha[:12]}')


def report(problems, summary, out=print):
    for p in problems:
        out(f'::error::{RULE} {p}')
    if problems:
        out(f'TEST_GUARD_FAILED {len(problems)} problem(s). Fix: docs/quality/test-guard.md, {RULE}')
        return 1
    out(f'TEST_GUARD_OK {summary}')
    return 0


def run_event(event_path, out=print, cwd='.'):
    try:
        event = json.loads(pathlib.Path(event_path).read_text(encoding='utf-8'))
        pr = event['pull_request']
        base, head = pr['base']['sha'], pr['head']['sha']
        body = pr.get('body') or ''
        if not (isinstance(base, str) and isinstance(head, str) and isinstance(body, str)):
            raise TypeError('unexpected event shape')
    except (OSError, ValueError, KeyError, TypeError) as error:
        return report([f'could not read the pull request from the event ({type(error).__name__}); '
                       'failing closed'], '', out)
    try:
        return report(*judge(body, base, head, cwd), out=out)
    except Unreadable as error:
        return report([f'{error} (base {base[:12]}, head {head[:12]}); failing closed'], '', out)


def self_test():
    results = []
    product = ('src/App/Greeting.cs',
               'namespace App;\npublic static class Greeting { public const string Text = "hi"; }\n')
    test_cs = ('tests/App.Tests/GreetingTests.cs',
               'using Xunit;\npublic class GreetingTests\n{\n    [Fact]\n    public void SaysHi()\n    {\n'
               '        Assert.Equal("hi", Greeting.Text);\n        Assert.NotEmpty(Greeting.Text);\n    }\n\n'
               '    [Theory]\n    [InlineData(1)]\n    [InlineData(2)]\n    public void Counts(int n)\n    {\n'
               '        Assert.True(n > 0);\n    }\n}\n')
    oracle_old = json.dumps({'revision': 2, 'files': {test_cs[0]: 'a' * 64}}, indent=2) + '\n'
    tool_test = ('tools/Live.Tests/Live.Tests.csproj', '<Project><ItemGroup><PackageReference Include="xunit" />'
                 '</ItemGroup></Project>\n')
    tool_case = ('tools/Live.Tests/LiveTests.cs',
                 'public class LiveTests\n{\n    [Fact]\n    public void Works()\n    {\n'
                 '        Assert.True(true);\n    }\n}\n')
    base_files = dict([product, test_cs, tool_test, tool_case, (ORACLE, oracle_old),
                       ('docs/readme.md', 'readme\n')])
    new_product = product[1].replace('"hi"', '"hello"')
    t = test_cs[1]

    with tempfile.TemporaryDirectory() as tmp:
        env = dict(os.environ, GIT_AUTHOR_NAME='t', GIT_AUTHOR_EMAIL='t@example.invalid', GIT_COMMITTER_NAME='t',
                   GIT_COMMITTER_EMAIL='t@example.invalid', GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull)

        def sh(*args):
            subprocess.run(['git'] + list(args), cwd=tmp, env=env, check=True, capture_output=True)

        def commit(files, deletions=()):
            for path, text in files.items():
                target = pathlib.Path(tmp, path)
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(text.encode('utf-8'))
            for path in deletions:
                pathlib.Path(tmp, path).unlink()
            sh('add', '-A')
            sh('commit', '-q', '--allow-empty', '-m', 'c')
            return subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=tmp, capture_output=True, text=True).stdout.strip()

        sh('init', '-q', '-b', 'main')
        base = commit(base_files)

        def case(name, files, should_pass, body='', deletions=(), body_for=None, why=''):
            sh('checkout', '-q', '--detach', base)
            head = commit(files, deletions)
            text = body_for(head) if body_for else body
            problems, _ = judge(text, base, head, tmp)
            # a failing case must fail for the planted reason, not for some other one
            results.append((name, (problems == []) == should_pass and why in ' '.join(problems)))
            return head

        def signed(names, sha_of=lambda head: head, why='the old check was wrong: it compared the wrong field'):
            return lambda head: ('## Summary\nx\n\n## Test change justification\n'
                                 + ''.join(f'- `{n}`: {why}\n' for n in names)
                                 + f'\nTest change reviewed: {sha_of(head)} by Fable reviewer\n\n## Evidence\n')

        no_assert = t.replace('        Assert.NotEmpty(Greeting.Text);\n', '')
        case('a deleted assertion + a product change fails', {product[0]: new_product, test_cs[0]: no_assert}, False,
             why='fewer assertion-shaped calls (3 -> 2)')
        case('a deleted assertion alone (no product change) is out of scope', {test_cs[0]: no_assert}, True)
        skip = t.replace('    [Fact]\n', '    [Fact(Skip = "flaky")]\n')
        case('a skip added + a product change fails', {product[0]: new_product, test_cs[0]: skip}, False,
             why='more skip markers')
        trait = t.replace('    [Fact]\n', '    [Fact]\n    [Trait("Category", "Skip")]\n')
        case('a Skip trait added (lines only added) + a product change fails',
             {product[0]: new_product, test_cs[0]: trait}, False, why='more skip markers')
        early = t.replace('    public void SaysHi()\n    {\n', '    public void SaysHi()\n    {\n        return;\n')
        case('return; added at a test\'s start + a product change fails', {product[0]: new_product, test_cs[0]: early},
             False, why='more tests that start with return')
        if_false = t.replace('    [Theory]\n', '#if false\n    [Theory]\n').replace('    }\n}\n', '    }\n#endif\n}\n')
        case('#if false added around a test + a product change fails', {product[0]: new_product, test_cs[0]: if_false},
             False, why='more #if directives')
        commented = t.replace('    [Theory]\n', '    /*\n    [Theory]\n').replace('    }\n}\n', '    }\n    */\n}\n')
        case('a test commented out (lines only added) + a product change fails',
             {product[0]: new_product, test_cs[0]: commented}, False, why='fewer test attributes')
        case('a deleted test file + a product change fails', {product[0]: new_product}, False, deletions=[test_cs[0]],
             why='an existing test file was deleted')
        case('a weakened test in a tools/ test project + a product change fails',
             {product[0]: new_product, tool_case[0]: tool_case[1].replace('Assert.True(true)', 'var x = 1')}, False,
             why='weakens tools/Live.Tests/LiveTests.cs')
        case('a change in a tools/ test project alone is not a product change',
             {tool_case[0]: tool_case[1] + '// note\n'}, True)
        rebaseline = json.dumps({'revision': 3, 'files': {test_cs[0]: 'b' * 64}, 'changedFiles': [test_cs[0]]},
                                indent=2) + '\n'
        case('an oracle change (rebaseline) + a product change fails', {product[0]: new_product, ORACLE: rebaseline},
             False, why='changes or removes frozen files')
        sh('checkout', '-q', '--detach', base)
        history_base = commit({ORACLE_HISTORY + 'revision-1.json': '{"revision": 1}\n'})
        sh('checkout', '-q', '--detach', history_base)
        history_head = commit({product[0]: new_product, ORACLE_HISTORY + 'revision-1.json': '{"revision": 9}\n'})
        results.append(('an edited oracle history file + a product change fails',
                         'oracle history may only grow' in ' '.join(judge('', history_base, history_head, tmp)[0])))
        extend = json.dumps({'revision': 3, 'files': {test_cs[0]: 'a' * 64, 'tests/App.Tests/NewTests.cs': 'c' * 64},
                             'addedFiles': ['tests/App.Tests/NewTests.cs'], 'changedFiles': [], 'removedFiles': []},
                            indent=2) + '\n'
        case('a pure --extend of the oracle (new tests frozen) + a product change passes',
             {product[0]: new_product, ORACLE: extend, ORACLE_HISTORY + 'revision-2.json': oracle_old,
              'tests/App.Tests/NewTests.cs':
                  'public class NewTests { [Xunit.Fact] public void N() { Xunit.Assert.True(true); } }\n'},
             True)
        names = [test_cs[0], 'SaysHi']
        case('the same deleted assertion with a justification signed for the head passes',
             {product[0]: new_product, test_cs[0]: no_assert}, True, body_for=signed(names))
        case('a justification signed for an OLD sha fails', {product[0]: new_product, test_cs[0]: no_assert}, False,
             body_for=signed(names, sha_of=lambda head: base), why='not the current head')
        case('a justification with a short sha fails', {product[0]: new_product, test_cs[0]: no_assert}, False,
             body_for=signed(names, sha_of=lambda head: head[:12]), why='not the current head')
        case('a justification that does not name the weakened method fails',
             {product[0]: new_product, test_cs[0]: no_assert}, False, body_for=signed([test_cs[0]]),
             why='"SaysHi" is not named')
        case('a justification that names the test but gives no reason fails',
             {product[0]: new_product, test_cs[0]: no_assert}, False, body_for=signed(names, why=''),
             why='no reason is given')
        no_section = 'no "## Test change justification" section'
        case('a justification hidden in an HTML comment fails', {product[0]: new_product, test_cs[0]: no_assert}, False,
             body_for=lambda head: '<!--\n' + signed(names)(head) + '\n-->\n', why=no_section)
        case('a justification inside a code block fails', {product[0]: new_product, test_cs[0]: no_assert}, False,
             body_for=lambda head: '```\n' + signed(names)(head) + '\n```\n', why=no_section)
        case('a sign-off outside the justification section fails', {product[0]: new_product, test_cs[0]: no_assert},
             False, body_for=lambda head: signed(names)(head).replace(f'Test change reviewed: {head} by Fable reviewer',
                                                                     '') + f'Test change reviewed: {head} by X\n',
             why='no reviewer sign-off')
        case('the oracle change with a justification signed for the head passes',
             {product[0]: new_product, ORACLE: rebaseline}, True, body_for=signed([ORACLE]))
        new_test = t.replace('    }\n}\n', '    }\n\n    [Fact]\n    public void SaysHiAgain()\n    {\n'
                                         '        Assert.Equal("hello", Greeting.Text);\n    }\n}\n')
        case('a new test added to an existing file + a product change passes',
             {product[0]: new_product, test_cs[0]: new_test}, True)
        case('a new test file + a product change passes',
             {product[0]: new_product, 'tests/App.Tests/OtherTests.cs': 'public class OtherTests { }\n'}, True)
        case('a test-only change (no product code) passes', {test_cs[0]: no_assert, ORACLE: rebaseline}, True)
        case('a Directory.Build.props change counts as product code',
             {'Directory.Build.props': '<Project />\n', test_cs[0]: no_assert}, False,
             why='changes product code (Directory.Build.props)')
        case('a docs-only change passes', {'docs/readme.md': 'readme 2\n'}, True)
        event = pathlib.Path(tmp, 'event.json')
        event.write_text('{not json', encoding='utf-8')
        results.append(('an unreadable event fails closed', run_event(event, out=lambda *_: None, cwd=tmp) == 1))
        event.write_text(json.dumps({'pull_request': {'body': 'x', 'base': {'sha': base}}}), encoding='utf-8')
        results.append(('an event without a head sha fails closed',
                        run_event(event, out=lambda *_: None, cwd=tmp) == 1))
        event.write_text(json.dumps({'pull_request': {'body': None, 'base': {'sha': base}, 'head': {'sha': '0' * 40}}}),
                         encoding='utf-8')
        results.append(('a head commit git cannot find fails closed',
                        run_event(event, out=lambda *_: None, cwd=tmp) == 1))
    failed = [n for n, ok in results if not ok]
    for n in failed:
        print(f'  FAILED: {n}')
    print(f'TEST_GUARD_SELF_TEST_{"FAILED" if failed else "PASSED"} {len(results) - len(failed)} of {len(results)}')
    return 1 if failed else 0


def main(argv):
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    if argv == ['--self-test']:
        return self_test()
    if len(argv) == 2 and argv[0] == '--event':
        return run_event(argv[1])
    if len(argv) in (4, 6) and argv[0] == '--base' and argv[2] == '--head' and (len(argv) == 4 or argv[4] == '--body'):
        body = pathlib.Path(argv[5]).read_text(encoding='utf-8') if len(argv) == 6 else ''
        try:
            return report(*judge(body, argv[1], argv[3]))
        except Unreadable as error:
            return report([f'{error}; failing closed'], '')
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
