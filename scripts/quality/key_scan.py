"""Key scan (BOO-SAFE-002): refuse to upload, or add in a PR, any API key. Never prints a key, only where it is.

  python -I scripts/quality/key_scan.py --preflight [--secret-env NAME ...] --delete PATH
      What every upload step runs (through the exact PowerShell wrapper from `step_run()`, enforced by
      --check-workflows). First the self-test and the workflow check; then scan every file under PATH and every file
      name. Exit 10 on a hit (the hit files are deleted). FAILS CLOSED: if anything else goes wrong (self-test,
      workflow check, crash, unreadable folder, read-only file, wildcard path), every file under PATH is deleted and
      the exit code is 3; the PowerShell wrapper also empties PATH on any exit code other than 0 and 10 (including
      Python failing to start), so an upload after it has nothing unscanned to send.
      --secret-env NAME   also look for the exact value of environment variable NAME (a real secret the job holds),
                          plain, UTF-16 (LE/BE), URL-encoded and base64 (standard and URL-safe, any alignment).
                          Unset or empty variables are skipped.
  python -I scripts/quality/key_scan.py --diff BASE
      Scan the lines added by `git diff BASE...HEAD` (text, binary and UTF-16 files). Prints file:line, never the match.
  python -I scripts/quality/key_scan.py --check-workflows
      Every step in .github/workflows that uses an `upload` action must come right after a key-scan step whose run
      block is exactly `step_run(path)` for the same single, literal path, with the same `if:`, and no other keys.
  python -I scripts/quality/key_scan.py --self-test
      Prove every pattern on fakes built at run time (no key-shaped text is stored in this file), every failure path,
      that clean files pass, and that no value is ever printed.

Rule BOO-SAFE-002 (details: docs/quality/RULES.md), two kinds of hit:
  key-shaped text   OpenAI / Anthropic `sk-`, GitHub `ghp_`.. and `github_pat_`, Google `AIza`, AWS access key ids,
                    private-key blocks, Slack, Tailscale, Hugging Face tokens, JWTs, long bearer tokens. Read as raw bytes
                    (binaries count), as UTF-16 at both byte offsets, and URL-decoded. A match passes only if exactly that
                    text appears inside a FROZEN test file whose content still matches its hash in
                    docs/verification/test-oracle.json: the redaction tests' deliberate fakes, also when xUnit shortens
                    them in a test name. No hand-kept allow-list.
  real secret       the exact value of a secret the job holds (--secret-env). Never allowed.
"""
import base64
import hashlib
import json
import os
import pathlib
import random
import re
import stat
import string
import subprocess
import sys
import tempfile
import urllib.parse

root = pathlib.Path(__file__).resolve().parent.parent.parent
ORACLE = root / 'docs' / 'verification' / 'test-oracle.json'
WORKFLOWS = root / '.github' / 'workflows'
RULE = 'BOO-SAFE-002'
RULES_DOC = 'docs/quality/RULES.md#boo-safe-002'
EXIT_HIT, EXIT_FAIL_CLOSED = 10, 3
UPLOAD_IFS = ('', '${{ !cancelled() }}')     # never `always()`: a cancelled scan must not be followed by an upload
GLOB = re.compile(r'[*?\[\]!{}]')

# Plain `sk-` keeps a word edge (so `risk-...` is not a key); every other prefix is distinctive and is found anywhere,
# also glued to other bytes in a binary. The edge also accepts an escaped control character written as text (`\n`).
_EDGE = rb'(?:(?<![A-Za-z0-9_])|(?<=\\[ntr0]))'
SHAPES = [
    ('OpenAI/Anthropic API key',
     rb'(?:sk-(?:proj|svcacct|admin|ant-[a-z]+\d*)-[A-Za-z0-9_-]{20,}|' + _EDGE + rb'sk-[A-Za-z0-9_-]{20,})'),
    ('GitHub token', rb'(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,}'),
    ('GitHub fine-grained token', rb'github_pat_[A-Za-z0-9_]{60,}'),
    ('Google API key', rb'AIza[0-9A-Za-z_-]{35}'),
    ('AWS access key id', rb'(?:AKIA|ASIA)[0-9A-Z]{16}(?![0-9A-Z])'),
    ('private key block', rb'-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----'),
    ('Slack token', rb'xox[abprs]-[A-Za-z0-9-]{10,}'),
    ('Tailscale key', rb'tskey-[A-Za-z0-9-]{16,}'),
    ('Hugging Face token', rb'hf_[A-Za-z0-9]{34,}'),
    ('JSON Web Token', rb'eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}'),
    ('bearer token', rb'(?i:\bbearer\s+)[A-Za-z0-9._~+/-]{32,}=*'),
]
COMBINED = re.compile(b'|'.join(b'(?P<s%d>%s)' % (i, rx) for i, (_, rx) in enumerate(SHAPES)))
LABELS = {f's{i}': label for i, (label, _) in enumerate(SHAPES)}

HELP = {
    'key-shaped text': (
        'A file about to be uploaded (or a line this PR adds) contains text shaped like an API key or token.',
        'The repo and its uploaded evidence are public. A real key there must be revoked; a key-shaped fake is '
        'indistinguishable from a real one to anyone reading it.',
        'Find what wrote the text (log line, screenshot text, test output) and redact it at the source. A deliberate '
        'fake from a redaction test passes automatically once it is written in a frozen test file (that change goes '
        'through a reviewed oracle revision).',
        'Do NOT skip the scan, narrow a pattern, move the file out of the upload path, or add an allow-list.'),
    'real secret': (
        'A file about to be uploaded contains the exact value of a secret this job holds (plain, UTF-16, URL-encoded '
        'or base64).',
        'That is a real key leaking into public evidence.',
        'Revoke and rotate the key, then fix the code that wrote it (log lengths, never values).',
        'Do NOT allow-list it, rename the secret or drop --secret-env; a real secret is never allowed.'),
}


def shape_matches(blob):
    for m in COMBINED.finditer(blob):
        yield LABELS[m.lastgroup], m.start(), m.group(0)


def oracle_digest(raw):
    """The oracle's hash format (scripts/test_oracle.py): UTF-8 text with BOM removed and CRLF as LF, else raw bytes."""
    try:
        content = raw.decode('utf-8-sig').replace('\r\n', '\n').encode('utf-8')
    except UnicodeDecodeError:
        content = raw
    return hashlib.sha256(content).hexdigest()


class FrozenTests:
    """The bytes of every frozen test file whose content still matches its oracle hash. A key-shaped match is a known
    fake when it appears inside one of them verbatim (whole, or shortened the way xUnit shortens test arguments)."""

    def __init__(self, oracle=None):
        oracle = oracle or ORACLE
        self.blobs = []
        try:
            frozen = json.loads(oracle.read_text(encoding='utf-8')).get('files', {})
        except (OSError, ValueError):
            return
        base = oracle.parent.parent.parent
        for rel, digest in frozen.items():
            path = base / rel
            if rel.startswith('tests/') and path.is_file():
                raw = path.read_bytes()
                if oracle_digest(raw) == digest:          # an edited (unfrozen) copy allows nothing
                    self.blobs.append(raw)

    def __contains__(self, text):
        return any(text in blob for blob in self.blobs)


def views(data):
    """Byte views of one blob: as written, UTF-16 LE at both byte offsets (Windows strings, .NET string literals),
    UTF-16 BE when marked or mostly zero bytes, and URL-decoded."""
    out = [('raw', data)]
    if len(data) > 1:
        for start in (0, 1):
            out.append(('utf-16le', data[start:].decode('utf-16-le', errors='ignore').encode('utf-8', errors='ignore')))
        if data[:2] == b'\xfe\xff' or data.count(b'\0') * 4 >= len(data):
            for start in (0, 1):
                out.append(('utf-16be', data[start:].decode('utf-16-be', errors='ignore').encode('utf-8', errors='ignore')))
    if b'%' in data:
        out.append(('url-decoded', urllib.parse.unquote_to_bytes(data)))
    return out


def secret_needles(value):
    raw = value.encode('utf-8')
    needles = {raw, value.encode('utf-16-le'), value.encode('utf-16-be'),
               urllib.parse.quote(value, safe='').encode('ascii')}
    for encode in (base64.b64encode, base64.urlsafe_b64encode):
        for pad in (0, 1, 2):
            enc = encode(b'\0' * pad + raw)
            enc = enc[4 * ((pad + 2) // 3):].rstrip(b'=')   # drop the chunk that holds the padding bytes
            if len(enc) > 8:
                needles.add(enc[:-4])                       # the tail chunk depends on what follows the key
    return [n for n in needles if len(n) >= 8]


def scan_bytes(data, allowed, secrets):
    """Hits as (kind, label, view, offset, length). Never the matched text."""
    hits = []
    for view, blob in views(data):
        for label, start, text in shape_matches(blob):
            if text in allowed:
                continue
            hits.append(('key-shaped text', label, view, start, len(text)))
    for name, needles in secrets:
        for needle in needles:
            at = data.find(needle)
            if at >= 0:
                hits.append(('real secret', f'value of ${name}', 'raw/encoded', at, len(needle)))
                break
    return hits


def safe(path, secrets=()):
    """The path for printing: any part whose name holds a key in ANY view (raw, URL-decoded, UTF-16) or a secret value
    is replaced by `<hidden name sha256:...>`, so a key in a file name is never printed in any encoding."""
    text = path.as_posix() if isinstance(path, pathlib.Path) else str(path)
    parts = []
    for part in text.split('/'):
        raw = part.encode('utf-8', errors='replace')
        if scan_bytes(raw, (), secrets):
            part = f'<hidden name sha256:{hashlib.sha256(raw).hexdigest()[:12]}>'
        parts.append(part)
    return '/'.join(parts)


def files_under(path):
    """Every file under the path, following folder and file links (upload-artifact follows them); each folder once."""
    p = pathlib.Path(path)
    if p.is_file():
        yield p
        return
    seen = set()
    for top, dirs, files in os.walk(p, followlinks=True):
        real = os.path.realpath(top)
        if real in seen:
            dirs[:] = []
            continue
        seen.add(real)
        dirs.sort()
        for name in sorted(files):
            f = pathlib.Path(top) / name
            if f.is_file():
                yield f


def remove(f):
    try:
        os.chmod(f, stat.S_IWRITE | stat.S_IREAD)
    except OSError:
        pass
    f.unlink()


def remaining(path):
    p = pathlib.Path(path)
    if p.is_file() or p.is_symlink():
        return 1
    return sum(1 for _ in files_under(p)) if p.exists() else 0


def purge(path, out=print):
    """Fail closed: delete every file under the path (read-only ones too), then check nothing is left."""
    p = pathlib.Path(path)
    for f in list(files_under(p)) if p.exists() else []:
        try:
            remove(f)
        except OSError:
            pass
    if p.is_file() or p.is_symlink():
        try:
            remove(p)
        except OSError:
            pass
    left = remaining(p)
    if left:
        out(f'::error::{RULE} fail closed: {left} file(s) could not be deleted under {safe(p)}')
    else:
        out(f'KEY_SCAN_FAIL_CLOSED: every file under {safe(p)} was deleted; nothing unscanned can be uploaded')
    return left


def scan(path, secret_envs=(), delete=False, out=print, oracle=None):
    allowed = FrozenTests(oracle)
    secrets = []
    for name in secret_envs:
        value = (os.environ.get(name) or '').strip()
        if len(value) >= 8:
            secrets.append((name, secret_needles(value)))
    count, bad = 0, {}
    for f in files_under(path):
        count += 1
        hits = [(k, label, 'file name', o, n)
                for k, label, _, o, n in scan_bytes(str(f).encode('utf-8', errors='ignore'), allowed, secrets)]
        hits += scan_bytes(f.read_bytes(), allowed, secrets)   # an unreadable file raises: the caller fails closed
        if hits:
            bad[f] = hits
    for f, hits in bad.items():
        shown, seen = safe(f, secrets), set()
        for kind, label, view, offset, length in hits:
            if (kind, label, view) in seen:
                continue
            seen.add((kind, label, view))
            out(f'::error file={shown}::{RULE} {kind}: {label} in {shown} ({view}, byte {offset}, {length} chars). '
                'The value is not shown.')
        if delete:
            remove(f)                                          # failure raises: the caller fails closed
            out(f'  deleted {shown} so it cannot be uploaded')
    if bad:
        for kind in sorted({h[0] for hits in bad.values() for h in hits}):
            what, why, fix, dont = HELP[kind]
            out(f'What: {what}\nWhy: {why}\nFix: {fix}\n{dont}\nRule: {RULES_DOC}')
        out(f'KEY_SCAN_FAILED {len(bad)} of {count} files')
        return EXIT_HIT
    note = '' if pathlib.Path(path).exists() else f' (path absent: {safe(path, secrets)})'
    out(f'KEY_SCAN_PASSED {count} files, {len(secrets)} secret value(s) checked{note}')
    return 0


# ---------------------------------------------------------------- the one allowed scan step

def step_run(path, secret_envs=()):
    """The exact `run:` block of a scan step for one upload path. --check-workflows accepts nothing else."""
    extra = ''.join(f' --secret-env {name}' for name in secret_envs)
    return '\n'.join([
        '$rc = 98',
        f'try {{ python -I scripts/quality/key_scan.py --preflight{extra} --delete {path}; $rc = $LASTEXITCODE }} '
        'catch { $rc = 99 }',
        'if ($rc -eq 0) { exit 0 }',
        f"if ($rc -ne {EXIT_HIT}) {{ Remove-Item -LiteralPath '{path}' -Recurse -Force -ErrorAction SilentlyContinue }}",
        f"if ($rc -ne {EXIT_HIT} -and (Test-Path -LiteralPath '{path}')) "
        "{ '::error::BOO-SAFE-002 the upload path could not be emptied after a failed key scan' }",
        'exit 1',
    ])


def _parse_steps(text):
    """Steps of every job as dicts of their top-level keys. Block scalars keep their exact text; nested mappings
    (`with:`, `env:`) become dicts of their keys. Anything this reader does not understand makes the check fail."""
    lines = text.splitlines()
    steps, i = [], 0
    while i < len(lines):
        m = re.match(r'^(\s*)- (\S.*)$', lines[i])
        if not m or not re.match(r'^[A-Za-z][\w-]*:', m.group(2)):
            i += 1
            continue
        indent = len(m.group(1)) + 2
        body = [' ' * indent + m.group(2)]
        i += 1
        while i < len(lines) and (not lines[i].strip() or len(lines[i]) - len(lines[i].lstrip()) >= indent):
            body.append(lines[i])
            i += 1
        step, j = {}, 0
        while j < len(body):
            line = body[j]
            km = re.match(r'^ {%d}([A-Za-z][\w-]*):\s*(.*)$' % indent, line)
            j += 1
            if not km:
                continue
            key, value = km.group(1), km.group(2).strip()
            child = []
            while j < len(body) and (not body[j].strip() or len(body[j]) - len(body[j].lstrip()) > indent):
                child.append(body[j])
                j += 1
            while child and not child[-1].strip():
                child.pop()
            if value in ('|', '|-', '>', '>-'):
                pad = min(len(c) - len(c.lstrip()) for c in child if c.strip()) if child else 0
                step[key] = '\n'.join(c[pad:] for c in child)
            elif not value and child:
                sub = {}
                for c in child:
                    cm = re.match(r'^\s*([A-Za-z][\w-]*):\s*(.*)$', c)
                    if cm:
                        sub[cm.group(1)] = cm.group(2).strip()
                    elif c.strip():
                        sub['<unreadable>'] = c.strip()
                step[key] = sub
            else:
                step[key] = value + (' <multi-line>' if child else '')
        steps.append(step)
    return steps


def check_workflows(folder=None, out=print):
    folder = folder or WORKFLOWS
    problems, uploads = [], 0
    for wf in sorted(pathlib.Path(folder).glob('*.y*ml')):
        steps = _parse_steps(wf.read_text(encoding='utf-8'))
        for i, s in enumerate(steps):
            uses = str(s.get('uses', ''))
            if 'upload' not in uses.lower():
                continue
            uploads += 1
            if wf.name == 'release.yml':
                continue                                   # documented gap (RULES.md): release uploads not scanned yet
            name = s.get('name', uses)
            path = str(s.get('with', {}).get('path', '')) if isinstance(s.get('with'), dict) else ''
            cond = s.get('if', '')
            prev = steps[i - 1] if i else {}
            if not path or path in ('|', '|-', '>', '>-') or GLOB.search(path) or ' ' in path or path.startswith(('"', "'")):
                problems.append(f'{wf.name}: "{name}" must upload one literal path (no list, wildcard or quotes)')
                continue
            if cond not in UPLOAD_IFS:
                problems.append(f'{wf.name}: "{name}" has if: {cond}; use no if or `${{{{ !cancelled() }}}}`')
            secret_envs = sorted(prev.get('env', {})) if isinstance(prev.get('env'), dict) else []
            expected = step_run(path, secret_envs)
            extra_keys = set(prev) - {'name', 'if', 'shell', 'env', 'run'}
            if prev.get('run') != expected or prev.get('shell') != 'pwsh' or extra_keys:
                problems.append(f'{wf.name}: "{name}" must come right after a key-scan step whose run block is exactly '
                                f'key_scan.step_run({path!r}) with shell: pwsh and no other keys'
                                + (f' (found keys: {", ".join(sorted(extra_keys))})' if extra_keys else ''))
            elif prev.get('if', '') != cond:
                problems.append(f'{wf.name}: "{name}" has if: {cond or "(none)"} but its scan has '
                                f'if: {prev.get("if") or "(none)"}')
    for p in problems:
        out(f'::error::{RULE} workflow check: {p}')
    if problems:
        out(f'KEY_SCAN_WORKFLOWS_FAILED {len(problems)} problem(s)')
        return 1
    out(f'KEY_SCAN_WORKFLOWS_OK {uploads} upload step(s) checked')
    return 0


# ---------------------------------------------------------------- PR diff

def scan_diff(base, out=print, cwd=None, oracle=None):
    allowed = FrozenTests(oracle)
    diff = subprocess.run(['git', '-c', 'core.quotePath=false', 'diff', '--no-color', '--no-ext-diff', '--text',
                           '-U0', f'{base}...HEAD'], capture_output=True, cwd=cwd or root)
    if diff.returncode != 0:
        out(f'::error::{RULE} could not read the diff against {base}')
        return EXIT_FAIL_CLOSED
    file, line_no, in_hunk, hits = None, 0, False, []
    for raw in diff.stdout.split(b'\n'):
        if raw.startswith(b'diff --git '):
            file, in_hunk = None, False
        elif not in_hunk and raw.startswith(b'+++ '):
            name = raw[4:].rstrip(b'\t')
            file = name[2:].decode('utf-8', errors='replace') if name.startswith(b'b/') else None
        elif raw.startswith(b'@@'):
            m = re.search(rb'\+(\d+)', raw)
            line_no, in_hunk = (int(m.group(1)) if m else 0), True
        elif in_hunk and raw.startswith(b'+') and file:
            for _, label, _, _, length in scan_bytes(raw[1:], allowed, ()):
                hits.append((file, line_no, label, length))
            line_no += 1
    for file, line_no, label, length in hits:
        out(f'::error file={safe(file)},line={line_no}::{RULE} key-shaped text: {label} ({length} chars) added at '
            f'{safe(file)}:{line_no}. The value is not shown.')
    if hits:
        what, why, fix, dont = HELP['key-shaped text']
        out(f'What: {what}\nWhy: {why}\nFix: {fix}\n{dont}\nRule: {RULES_DOC}')
        out(f'KEY_SCAN_DIFF_FAILED {len(hits)} added line(s)')
        return EXIT_HIT
    out('KEY_SCAN_DIFF_PASSED')
    return 0


# ---------------------------------------------------------------- self-test

def self_test(out=print):
    rnd = random.Random(20261113)
    def chars(alphabet, n):
        return ''.join(rnd.choice(alphabet) for _ in range(n))
    alnum = string.ascii_letters + string.digits
    fakes = {
        'OpenAI/Anthropic API key': 's' + 'k-' + 'proj-' + chars(alnum, 48),
        'GitHub token': 'gh' + 'p_' + chars(alnum, 36),
        'GitHub fine-grained token': 'github' + '_pat_' + chars(alnum + '_', 82),
        'Google API key': 'AI' + 'za' + chars(alnum + '_-', 35),
        'AWS access key id': 'AK' + 'IA' + chars(string.ascii_uppercase + string.digits, 16),
        'private key block': '-----BEGIN ' + 'RSA PRIVATE ' + 'KEY-----',
        'Slack token': 'xo' + 'xb-' + chars(alnum, 24),
        'Tailscale key': 'ts' + 'key-' + chars(alnum, 30),
        'Hugging Face token': 'h' + 'f_' + chars(alnum, 34),
        'JSON Web Token': 'ey' + 'J' + chars(alnum, 20) + '.ey' + 'J' + chars(alnum, 20) + '.' + chars(alnum, 30),
        'bearer token': 'Authorization: Bear' + 'er ' + chars(alnum, 40),
    }
    results = []
    def check(name, cond):
        results.append((name, bool(cond)))
    quiet = lambda *_: None

    fake_oracle = None

    def run(path, secret_envs=(), delete=False):
        lines = []
        try:
            code = scan(path, secret_envs, delete, out=lines.append, oracle=fake_oracle)
        except Exception as e:                       # a guard that raises counts as a failed check, not a crash
            return -1, type(e).__name__
        return code, '\n'.join(lines)

    env = {k: v for k, v in os.environ.items() if k not in ('PYTHONUTF8', 'PYTHONIOENCODING')}

    def cli(*args, extra_env=None):
        return subprocess.run([sys.executable, '-I', __file__, *map(str, args)], capture_output=True,
                              env=dict(env, **(extra_env or {})))

    with tempfile.TemporaryDirectory() as tmp:
        tmp = pathlib.Path(tmp)
        clean = tmp / 'clean'
        clean.mkdir()
        (clean / 'log.txt').write_text('risk-assessment sk-short ghp_ abc AIza tskey hf_x Bearer token eyJx\nok\n',
                                       encoding='utf-8')
        (clean / 'shot.png').write_bytes(bytes(range(256)) * 64)
        (clean / 'utf16.trx').write_text('<TestRun>passed</TestRun>', encoding='utf-16')
        code, text = run(clean)
        check('clean files pass', code == 0 and 'KEY_SCAN_PASSED 3 files' in text)
        code, text = run(clean / 'does-not-exist')
        check('missing path passes but is reported', code == 0 and 'path absent' in text)

        forms = ('plain', 'escaped-newline', 'glued', 'utf16-even', 'utf16-odd', 'binary', 'url', 'name', 'url-name')
        for label, fake in fakes.items():
            for form in forms:
                case = tmp / f'{abs(hash(label)) % 10**8}-{form}'
                case.mkdir()
                if form == 'plain':
                    (case / 'out.log').write_text(f'line before\n{fake}\nline after\n', encoding='utf-8')
                elif form == 'escaped-newline':
                    (case / 'out.json').write_text('{"log": "first\\n' + fake + '\\tsecond"}', encoding='utf-8')
                elif form == 'glued':
                    if label in ('OpenAI/Anthropic API key', 'bearer token', 'AWS access key id'):
                        continue                    # plain sk- / bearer / AKIA need an edge by design
                    (case / 'blob.bin').write_bytes(b'\x01Qz' + fake.encode() + b'\x02')
                elif form == 'utf16-even':
                    (case / 'app.dll').write_bytes(b'MZ\x90\0' + fake.encode('utf-16-le') + b'\0' * 8 + b'x' * 400)
                elif form == 'utf16-odd':
                    (case / 'app.dll').write_bytes(b'MZ\x90' + fake.encode('utf-16-le') + b'\0' * 8 + b'x' * 400)
                elif form == 'binary':
                    (case / 'out.png').write_bytes(b'\x89PNG\r\n\x1a\n\0\0' + fake.encode() + b'\0\xff' * 50)
                elif form == 'url':
                    (case / 'req.txt').write_text('GET /x?k=' + urllib.parse.quote(fake, safe=''), encoding='utf-8')
                elif form == 'name':
                    if label in ('private key block', 'bearer token'):
                        continue                    # cannot be a file name on Windows
                    (case / f'{fake}.txt').write_text('x', encoding='utf-8')
                else:
                    if label in ('private key block', 'bearer token'):
                        continue
                    (case / (urllib.parse.quote(fake, safe='').replace('-', '%2D') + '.txt')).write_text(
                        'x', encoding='utf-8')
                code, text = run(case)
                check(f'{label} ({form}) fails', code == EXIT_HIT and RULE in text and label in text)
                core = fake.split()[-1]
                encoded = urllib.parse.quote(core, safe='').replace('-', '%2D')
                check(f'{label} ({form}) value not printed (any encoding)',
                      core not in text and core[6:30] not in text and encoded not in text)

        # allowed only when written verbatim in a frozen test file that matches its oracle hash
        allowed_fake, other_fake = fakes['GitHub token'], 'gh' + 'p_' + chars(alnum, 36)
        secret = 'live-' + chars(alnum, 40) + '>>>???'      # its base64 holds + and / (URL-safe differs)
        repo = tmp / 'repo'
        (repo / 'tests' / 'T').mkdir(parents=True)
        (repo / 'docs' / 'verification').mkdir(parents=True)
        frozen_text = f'[InlineData("{allowed_fake}")] // and "{secret}"'
        (repo / 'tests' / 'T' / 'RedactionTests.cs').write_text(frozen_text, encoding='utf-8')
        (repo / 'tests' / 'T' / 'Unfrozen.cs').write_text(f'"{other_fake}"', encoding='utf-8')
        fake_oracle = repo / 'docs' / 'verification' / 'test-oracle.json'
        fake_oracle.write_text(json.dumps({'files': {
            'tests/T/RedactionTests.cs': oracle_digest(frozen_text.encode()),
            'tests/T/Unfrozen.cs': '0' * 64}}), encoding='utf-8')
        try:
            case = tmp / 'allowed'
            case.mkdir()
            (case / 'a.trx').write_text(allowed_fake, encoding='utf-8')
            check('fake written in a frozen test passes', run(case)[0] == 0)
            (case / 'a.trx').write_text(allowed_fake[:-3] + '...', encoding='utf-8')
            check('fake shortened like an xUnit test name passes', run(case)[0] == 0)
            (case / 'b.trx').write_text(other_fake, encoding='utf-8')
            check('fake in a test file whose hash does not match the oracle still fails', run(case)[0] == EXIT_HIT)
            (case / 'b.trx').unlink()

            os.environ['BOO_KEYSCAN_SELFTEST_SECRET'] = secret
            encodings = {
                'plain': secret.encode(),
                'utf16le': secret.encode('utf-16-le'),
                'utf16be': secret.encode('utf-16-be'),
                'url': urllib.parse.quote(secret, safe='').encode(),
            }
            for shift in (0, 1, 2):
                for nm, enc in (('base64', base64.b64encode), ('base64url', base64.urlsafe_b64encode)):
                    encodings[f'{nm}-shift{shift}'] = enc(b'xy'[:shift] + secret.encode() + b':tail')
            check('standard and URL-safe base64 of the secret differ',
                  base64.b64encode(secret.encode()) != base64.urlsafe_b64encode(secret.encode()))
            for form, blob in encodings.items():
                case = tmp / f'secret-{form}'
                case.mkdir()
                (case / 'e.bin').write_bytes(b'head ' + blob + b' tail')
                code, text = run(case, ['BOO_KEYSCAN_SELFTEST_SECRET'])
                check(f'real secret ({form}) fails although its text is in a frozen test',
                      code == EXIT_HIT and 'real secret' in text)
                check(f'real secret ({form}) value not printed', secret not in text)
            os.environ['BOO_KEYSCAN_SELFTEST_EMPTY'] = ''
            check('empty secret variable is skipped',
                  run(clean, ['BOO_KEYSCAN_SELFTEST_EMPTY', 'BOO_KEYSCAN_UNSET'])[0] == 0)
        finally:
            fake_oracle = None
            os.environ.pop('BOO_KEYSCAN_SELFTEST_SECRET', None)
            os.environ.pop('BOO_KEYSCAN_SELFTEST_EMPTY', None)

        # --delete removes exactly the files with hits, read-only ones too
        case = tmp / 'delete'
        case.mkdir()
        (case / 'leak.log').write_text(fakes['Google API key'], encoding='utf-8')
        os.chmod(case / 'leak.log', stat.S_IREAD)
        (case / 'fine.log').write_text('fine', encoding='utf-8')
        code, _ = run(case, delete=True)
        check('--delete removes a read-only leaking file and nothing else',
              code == EXIT_HIT and not (case / 'leak.log').exists() and (case / 'fine.log').exists())

        # the command line: a hit exits 10; a file name outside the console code page does not crash; never a value
        case = tmp / 'cli'
        case.mkdir()
        (case / 'x.log').write_text(fakes['OpenAI/Anthropic API key'], encoding='utf-8')
        (case / 'r\u2192\u4e2d.log').write_text(fakes['Slack token'], encoding='utf-8')
        proc = cli('--delete', case)
        printed = proc.stdout + proc.stderr
        check('command line exits 10 on a hit', proc.returncode == EXIT_HIT and b'KEY_SCAN_FAILED 2 of 2' in printed)
        check('non-ASCII file name does not crash and is deleted', b'Traceback' not in printed
              and not any(case.iterdir()))
        check('command line never prints the value', fakes['OpenAI/Anthropic API key'].encode() not in printed
              and fakes['Slack token'].encode() not in printed)

        # fail closed: an internal error deletes everything (read-only included) and exits 3
        case = tmp / 'failclosed'
        (case / 'sub').mkdir(parents=True)
        (case / 'sub' / 'a.log').write_text('fine', encoding='utf-8')
        (case / 'b.log').write_text('fine', encoding='utf-8')
        os.chmod(case / 'b.log', stat.S_IREAD)
        proc = cli('--delete', case, extra_env={'BOO_KEYSCAN_SELFTEST_CRASH': '1'})
        check('an internal error fails closed: exit 3, nothing left (read-only included)',
              proc.returncode == EXIT_FAIL_CLOSED and remaining(case) == 0 and b'KEY_SCAN_FAIL_CLOSED' in proc.stdout)
        if os.name == 'nt':                          # Windows refuses to delete a file that is open
            case = tmp / 'held'
            case.mkdir()
            (case / 'held.log').write_text('fine', encoding='utf-8')
            lines = []
            with open(case / 'held.log', 'rb'):
                left = purge(case, out=lines.append)
            check('purge reports a file it could not delete', left == 1 and 'could not be deleted' in '\n'.join(lines))
        for bad in ('out/**/*.log', 'out/[ab].log', 'one two'):
            proc = cli('--delete', bad)
            check(f'a wildcard or list path is refused ({bad})', proc.returncode == EXIT_FAIL_CLOSED)

        # workflow check: the scan step must be exactly step_run(path) right before the upload, same if:
        def wf_text(scan_lines, upload_if='${{ !cancelled() }}', path='out/x', uses='actions/upload-artifact@v4'):
            run_block = '\n'.join('          ' + line for line in step_run('out/x').splitlines())
            scan = ('      - name: Key scan before upload (x)\n'
                    + ('        if: ${{ !cancelled() }}\n' if 'no-if' not in scan_lines else '')
                    + ('        working-directory: sub\n' if 'workdir' in scan_lines else '')
                    + '        shell: pwsh\n        run: |\n'
                    + (run_block.replace(' --delete', '') if 'no-delete' in scan_lines else run_block) + '\n')
            if 'no-scan' in scan_lines:
                scan = ''
            return ('jobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n' + scan
                    + f'      - name: Upload x\n' + (f'        if: {upload_if}\n' if upload_if else '')
                    + f'        uses: {uses}\n        with:\n          name: x\n          path: {path}\n')
        wf = tmp / 'wf'
        wf.mkdir()
        (wf / 'a.yml').write_text(wf_text(''), encoding='utf-8')
        check('workflow check passes the generated scan step', check_workflows(wf, out=quiet) == 0)
        for name, text in (('no scan step', wf_text('no-scan')),
                           ('scan without --delete', wf_text('no-delete')),
                           ('scan with working-directory', wf_text('workdir')),
                           ('different if', wf_text('no-if')),
                           ('upload with always()', wf_text('', upload_if='always()')),
                           ('wrong path', wf_text('', path='out/y')),
                           ('wildcard path', wf_text('', path='out/**/*.log')),
                           ('multi-line path', wf_text('', path='|\n            out/x\n            out/y')),
                           ('another upload action unscanned', wf_text('no-scan', uses='actions/upload-pages-artifact@v3'))):
            (wf / 'a.yml').write_text(text, encoding='utf-8')
            check(f'workflow check fails: {name}', check_workflows(wf, out=quiet) == 1)

        # PR diff: added keys fail at file:line (no value): plain, non-ASCII name, a "++" line, binary, UTF-16
        g = tmp / 'git'
        g.mkdir()
        def git(*a):
            return subprocess.run(['git', '-c', 'user.name=t', '-c', 'user.email=t@t', *a], cwd=g, capture_output=True)
        git('init', '-q', '-b', 'base')
        (g / 'a.txt').write_text('hello\n', encoding='utf-8')
        git('add', '.')
        git('commit', '-q', '-m', 'base')
        git('checkout', '-q', '-b', 'pr')
        (g / 'a.txt').write_text('hello\nconfig = "' + fakes['Hugging Face token'] + '"\n', encoding='utf-8')
        (g / 'n\u00e4me.txt').write_text(fakes['Google API key'] + '\n', encoding='utf-8')
        (g / 'plus.txt').write_text('++ header-looking line\n' + fakes['Slack token'] + '\n', encoding='utf-8')
        (g / 'blob.bin').write_bytes(b'\0\x01' + fakes['Tailscale key'].encode() + b'\0')
        (g / 'wide.txt').write_bytes(fakes['GitHub token'].encode('utf-16-le'))
        git('add', '.')
        git('commit', '-q', '-m', 'pr')
        lines = []
        code = scan_diff('base', out=lines.append, cwd=g, oracle=tmp / 'no-oracle.json')
        text = '\n'.join(lines)
        check('PR diff: added key fails at file:line', code == EXIT_HIT and 'a.txt:2' in text)
        check('PR diff: non-ASCII file name is scanned', 'n\u00e4me.txt:1' in text)
        check('PR diff: a line starting with ++ does not hide the next one', 'plus.txt:2' in text)
        check('PR diff: binary file is scanned', 'blob.bin' in text)
        check('PR diff: UTF-16 file is scanned', 'wide.txt' in text)
        check('PR diff never prints a value', not any(f.split()[-1] in text for f in fakes.values()))
        check('PR diff against a missing base fails closed',
              scan_diff('no-such-base', out=quiet, cwd=g) == EXIT_FAIL_CLOSED)

    # the scanner's own source holds no key-shaped text (the fakes above are built from pieces)
    check('this file holds no key-shaped text', not list(shape_matches(pathlib.Path(__file__).read_bytes())))

    failed = [name for name, ok in results if not ok]
    if failed:
        out(f'KEY_SCAN_SELF_TEST_FAILED {len(failed)} of {len(results)}:')
        for name in failed:
            out('  ' + name)
        return 1
    out(f'KEY_SCAN_SELF_TEST_PASSED {len(results)} checks')
    return 0


# ---------------------------------------------------------------- command line

def main(argv):
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')
    except (AttributeError, ValueError):
        pass
    if argv[:1] == ['--self-test']:
        return self_test()
    if argv[:1] == ['--check-workflows']:
        return check_workflows()
    if argv[:1] == ['--diff']:
        return scan_diff(argv[1]) if len(argv) > 1 else 2
    secret_envs, paths, delete, preflight = [], [], False, False
    it = iter(argv)
    for arg in it:
        if arg == '--secret-env':
            secret_envs.append(next(it, ''))
        elif arg == '--delete':
            delete = True
        elif arg == '--preflight':
            preflight = True
        else:
            paths.append(arg)
    if not paths:
        print(__doc__)
        return 2
    try:
        if len(paths) != 1 or GLOB.search(paths[0]) or ' ' in paths[0]:
            raise ValueError('scan exactly one literal path (no list, wildcard or spaces)')
        if os.environ.get('BOO_KEYSCAN_SELFTEST_CRASH') == '1':
            raise RuntimeError('simulated internal error (self-test)')
        if preflight and (self_test() != 0 or check_workflows() != 0):
            raise RuntimeError('the key scan could not prove itself')
        return scan(paths[0], secret_envs, delete)
    except BaseException as e:                       # fail closed on anything: crash, unreadable path, bad encoding
        print(f'::error::{RULE} key scan failed ({type(e).__name__}: {e}); failing closed')
        if delete:
            for p in paths:
                if not GLOB.search(p):
                    purge(p)
        return EXIT_FAIL_CLOSED


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
