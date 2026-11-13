"""Key scan: refuse to upload any file that holds an API key. Never prints a key, only where it is.

  python -I scripts/quality/key_scan.py [--secret-env NAME ...] [--delete] PATH [PATH ...]
      Scan every file under each PATH (and every file name). Exit 1 on any hit.
      --secret-env NAME   also look for the exact value of environment variable NAME (a real secret the job holds),
                          as plain text, UTF-16, URL-encoded and base64. Unset or empty variables are skipped.
      --delete            delete every file with a hit, so an `if: always()` upload step cannot send it anyway.
  python -I scripts/quality/key_scan.py --self-test
      Prove every rule on fake keys built at run time (no key-shaped text is stored in this file), that clean files
      pass, that allowed test fakes pass, that real-secret hits can never be allowed, and that no value is printed.

Rules (details: docs/quality/RULES.md):
  BOO-SEC-001  key-shaped text: OpenAI / Anthropic `sk-`, GitHub `ghp_` and `github_pat_`, Google `AIza`, AWS access
               key ids, private-key blocks, Slack and Tailscale tokens, long bearer tokens. Read as raw bytes (so
               binaries count), as UTF-16, and URL-decoded. A match passes only if exactly that text appears inside a
               FROZEN test file (listed in docs/verification/test-oracle.json): the redaction tests' deliberate
               fakes, which can only change through a reviewed oracle revision, also when xUnit shortens them in a
               test name. No hand-kept allow-list.
  BOO-SEC-002  the exact value of a secret the job holds (--secret-env). Never allowed.
"""
import base64
import hashlib
import json
import os
import pathlib
import random
import re
import string
import subprocess
import sys
import tempfile
import urllib.parse

root = pathlib.Path(__file__).resolve().parent.parent.parent
ORACLE = root / 'docs' / 'verification' / 'test-oracle.json'
RULES_DOC = 'docs/quality/RULES.md'

_EDGE = rb'(?<![A-Za-z0-9_])'
SHAPES = [
    ('OpenAI/Anthropic API key', re.compile(_EDGE + rb'sk-(?:proj-|svcacct-|admin-|ant-[a-z]+\d*-)?[A-Za-z0-9_-]{20,}')),
    ('GitHub token', re.compile(_EDGE + rb'(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,}')),
    ('GitHub fine-grained token', re.compile(_EDGE + rb'github_pat_[A-Za-z0-9_]{60,}')),
    ('Google API key', re.compile(_EDGE + rb'AIza[0-9A-Za-z_-]{35}')),
    ('AWS access key id', re.compile(_EDGE + rb'(?:AKIA|ASIA)[0-9A-Z]{16}(?![0-9A-Z])')),
    ('private key block', re.compile(rb'-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----')),
    ('Slack token', re.compile(_EDGE + rb'xox[abprs]-[A-Za-z0-9-]{10,}')),
    ('Tailscale key', re.compile(_EDGE + rb'tskey-[A-Za-z0-9-]{16,}')),
    ('bearer token', re.compile(rb'(?i:\bbearer\s+)[A-Za-z0-9._~+/-]{32,}=*')),
]

COMBINED = re.compile(b'|'.join(b'(?P<s%d>%s)' % (i, rx.pattern) for i, (_, rx) in enumerate(SHAPES)))
LABELS = {f's{i}': label for i, (label, _) in enumerate(SHAPES)}

HELP = {
    'BOO-SEC-001': (
        'A file about to be uploaded contains text shaped like an API key or token.',
        'Uploaded evidence is public (the repo is public). A real key there must be revoked; a key-shaped fake is '
        'indistinguishable from a real one to anyone reading it.',
        'Find where the text came from (log line, screenshot text, test output) and stop writing it: redact it at '
        'the source. A deliberate fake from a redaction test passes automatically once it is written in a frozen '
        'test file (that change goes through a reviewed oracle revision).',
        'Do NOT skip the scan, narrow the patterns, move the file out of the upload path, or add an allow-list.'),
    'BOO-SEC-002': (
        'A file about to be uploaded contains the exact value of a secret this job holds (plain, UTF-16, URL-encoded '
        'or base64).',
        'That is a real key leaking into public evidence.',
        'Revoke and rotate the key, then fix the code that wrote it (it must log lengths, never values).',
        'Do NOT allow-list it, rename the secret, or drop --secret-env; a real secret is never allowed.'),
}


def shape_matches(blob):
    for m in COMBINED.finditer(blob):
        yield LABELS[m.lastgroup], m.start(), m.group(0)


class FrozenTests:
    """The bytes of every frozen test file. A key-shaped match is a known fake when it appears inside one of them
    verbatim (whole, or shortened the way xUnit shortens long arguments in test names)."""

    def __init__(self, oracle=None):
        oracle = oracle or ORACLE
        self.blobs = []
        try:
            frozen = json.loads(oracle.read_text(encoding='utf-8')).get('files', {})
        except (OSError, ValueError):
            return
        base = oracle.parent.parent.parent
        for rel in frozen:
            path = base / rel
            if rel.startswith('tests/') and path.is_file():
                self.blobs.append(path.read_bytes())

    def __contains__(self, text):
        return any(text in blob for blob in self.blobs)


def load_allow(oracle=None):
    return FrozenTests(oracle)


def views(data):
    """Byte views of one file: as written, UTF-16 (LE and BE) narrowed to bytes, and URL-decoded."""
    out = [('raw', data)]
    if data.count(b'\0') * 4 >= len(data) > 0:
        for name, codec in (('utf-16le', 'utf-16-le'), ('utf-16be', 'utf-16-be')):
            for start in (0, 1):
                text = data[start:].decode(codec, errors='ignore')
                out.append((name, text.encode('utf-8', errors='ignore')))
    if b'%' in data:
        out.append(('url-decoded', urllib.parse.unquote_to_bytes(data)))
    return out


def secret_needles(value):
    raw = value.encode('utf-8')
    needles = {raw, value.encode('utf-16-le'), urllib.parse.quote(value, safe='').encode('ascii')}
    for pad in (0, 1, 2):
        enc = base64.b64encode(b'\0' * pad + raw)
        enc = enc[4 * ((pad + 2) // 3):]           # drop the chunk that holds the padding bytes
        enc = enc.rstrip(b'=')
        if len(enc) > 8:
            needles.add(enc[:-4])                   # the tail chunk depends on what follows the key
        needles.add(base64.urlsafe_b64encode(raw).rstrip(b'='))
    return [n for n in needles if len(n) >= 8]


def scan_bytes(data, allowed, secrets):
    """Hits as (rule_id, label, view, offset, length). Never the matched text."""
    hits = []
    for view, blob in views(data):
        for label, start, text in shape_matches(blob):
            if text in allowed:
                continue
            hits.append(('BOO-SEC-001', label, view, start, len(text)))
    for name, needles in secrets:
        for needle in needles:
            at = data.find(needle)
            if at >= 0:
                hits.append(('BOO-SEC-002', f'value of ${name}', 'raw/encoded', at, len(needle)))
                break
    return hits


def safe(path, secrets):
    """The path as text with every key-shaped part and every secret value masked (a key can be in a file name)."""
    text = path.as_posix() if isinstance(path, pathlib.Path) else str(path)
    raw = text.encode('utf-8', errors='replace')
    for _, start, match in sorted(shape_matches(raw), key=lambda m: -m[1]):
        raw = raw[:start] + b'<hidden %d chars>' % len(match) + raw[start + len(match):]
    for _, needles in secrets:
        for needle in needles:
            raw = raw.replace(needle, b'<hidden secret>')
    return raw.decode('utf-8', errors='replace')


def files_under(paths):
    for p in paths:
        p = pathlib.Path(p)
        if p.is_file():
            yield p
        elif p.is_dir():
            for f in sorted(p.rglob('*')):
                if f.is_file() and not f.is_symlink():
                    yield f


def scan(paths, secret_envs=(), delete=False, out=print, oracle=None):
    allowed = load_allow(oracle)
    secrets = []
    for name in secret_envs:
        value = (os.environ.get(name) or '').strip()
        if len(value) >= 8:
            secrets.append((name, secret_needles(value)))
    missing = [str(p) for p in paths if not pathlib.Path(p).exists()]
    count, bad = 0, {}
    for f in files_under(paths):
        count += 1
        hits = scan_bytes(str(f).encode('utf-8', errors='ignore'), allowed, secrets)
        hits = [(r, label, 'file name', o, n) for r, label, _, o, n in hits]
        try:
            hits += scan_bytes(f.read_bytes(), allowed, secrets)
        except OSError as e:
            out(f'::error::key scan could not read {safe(f, secrets)}: {e.strerror}')
            hits.append(('BOO-SEC-001', 'unreadable file', 'n/a', 0, 0))
        if hits:
            bad[f] = hits
    for f, hits in bad.items():
        seen = set()
        for rule, label, view, offset, length in hits:
            if (rule, label, view) in seen:
                continue
            seen.add((rule, label, view))
            shown = safe(f, secrets)
            out(f'::error file={shown}::{rule} {label} in {shown} ({view}, byte {offset}, '
                f'{length} chars). The value is not shown.')
        if delete:
            try:
                f.unlink()
                out(f'  deleted {safe(f, secrets)} so it cannot be uploaded')
            except OSError as e:
                out(f'::error::could not delete {safe(f, secrets)}: {e.strerror}')
    if bad:
        for rule in sorted({h[0] for hits in bad.values() for h in hits}):
            what, why, fix, dont = HELP[rule]
            out(f'What: {what}\nWhy: {why}\nFix: {fix}\n{dont}\nRule: {RULES_DOC}#{rule.lower()}')
        out(f'KEY_SCAN_FAILED {len(bad)} of {count} files')
        return 1
    note = f' ({len(missing)} path(s) absent: {", ".join(safe(m, secrets) for m in missing)})' if missing else ''
    out(f'KEY_SCAN_PASSED {count} files, {len(secrets)} secret value(s) checked{note}')
    return 0


def self_test():
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
        'bearer token': 'Authorization: Bear' + 'er ' + chars(alnum, 40),
    }
    failures = []
    def check(name, cond):
        if not cond:
            failures.append(name)

    fake_oracle = None

    def run(paths, secret_envs=(), delete=False):
        lines = []
        code = scan(paths, secret_envs, delete, out=lines.append, oracle=fake_oracle)
        return code, '\n'.join(lines)

    with tempfile.TemporaryDirectory() as tmp:
        tmp = pathlib.Path(tmp)
        clean = tmp / 'clean'
        clean.mkdir()
        (clean / 'log.txt').write_text('risk-assessment sk-short ghp_ abc AIza tskey Bearer token\nok\n', encoding='utf-8')
        (clean / 'shot.png').write_bytes(bytes(range(256)) * 64)
        (clean / 'utf16.trx').write_text('<TestRun>passed</TestRun>', encoding='utf-16')
        code, text = run([clean])
        check('clean files pass', code == 0 and 'KEY_SCAN_PASSED 3 files' in text)
        code, text = run([clean / 'does-not-exist'])
        check('missing path passes but is reported', code == 0 and 'absent' in text)

        for label, fake in fakes.items():
            for form in ('plain', 'utf16', 'binary', 'url', 'name'):
                case = tmp / f'{abs(hash(label)) % 10**8}-{form}'
                case.mkdir()
                if form == 'plain':
                    (case / 'out.log').write_text(f'line before\n{fake}\nline after\n', encoding='utf-8')
                elif form == 'utf16':
                    (case / 'out.trx').write_text(f'<Output>{fake}</Output>', encoding='utf-16')
                elif form == 'binary':
                    (case / 'out.png').write_bytes(b'\x89PNG\r\n\x1a\n\0\0' + fake.encode() + b'\0\xff' * 50)
                elif form == 'url':
                    (case / 'req.txt').write_text('GET /x?k=' + urllib.parse.quote(fake, safe=''), encoding='utf-8')
                else:
                    if label in ('private key block', 'bearer token'):
                        continue                    # cannot be a file name on Windows
                    (case / f'{fake}.txt').write_text('x', encoding='utf-8')
                code, text = run([case])
                check(f'{label} ({form}) fails', code == 1 and 'BOO-SEC-001' in text and label in text)
                core = fake.split()[-1]
                check(f'{label} ({form}) value not printed', core not in text and fake not in text)

        # allowed only when written verbatim in a FROZEN test file; a real secret is never allowed
        allowed_fake, other_fake = fakes['GitHub token'], 'gh' + 'p_' + chars(alnum, 36)
        secret = 'live-' + chars(alnum, 40)
        repo = tmp / 'repo'
        (repo / 'tests' / 'T').mkdir(parents=True)
        (repo / 'docs' / 'verification').mkdir(parents=True)
        (repo / 'tests' / 'T' / 'RedactionTests.cs').write_text(
            f'[InlineData("{allowed_fake}")] // and "{secret}"', encoding='utf-8')
        (repo / 'tests' / 'T' / 'Unfrozen.cs').write_text(f'"{other_fake}"', encoding='utf-8')
        fake_oracle = repo / 'docs' / 'verification' / 'test-oracle.json'
        fake_oracle.write_text('{"files": {"tests/T/RedactionTests.cs": "x"}}', encoding='utf-8')
        try:
            case = tmp / 'allowed'
            case.mkdir()
            (case / 'a.trx').write_text(allowed_fake, encoding='utf-8')
            check('fake written in a frozen test passes', run([case])[0] == 0)
            (case / 'a.trx').write_text(allowed_fake[:-3] + '...', encoding='utf-8')
            check('fake shortened like an xUnit test name passes', run([case])[0] == 0)
            (case / 'b.trx').write_text(other_fake, encoding='utf-8')
            check('fake only in an unfrozen file still fails', run([case])[0] == 1)
            (case / 'b.trx').unlink()

            # real secret (BOO-SEC-002): every encoding fails, even though its text is in a frozen test file
            os.environ['BOO_KEYSCAN_SELFTEST_SECRET'] = secret
            encodings = {
                'plain': secret.encode(),
                'utf16': secret.encode('utf-16-le'),
                'url': urllib.parse.quote(secret, safe='').encode(),
                'base64': base64.b64encode(b'prefix:' + secret.encode() + b':suffix'),
                'base64-shift1': base64.b64encode(b'x' + secret.encode()),
                'base64-shift2': base64.b64encode(b'xy' + secret.encode()),
            }
            for form, blob in encodings.items():
                case = tmp / f'secret-{form}'
                case.mkdir()
                (case / 'e.bin').write_bytes(b'head ' + blob + b' tail')
                code, text = run([case], ['BOO_KEYSCAN_SELFTEST_SECRET'])
                check(f'real secret ({form}) fails', code == 1 and 'BOO-SEC-002' in text)
                check(f'real secret ({form}) value not printed', secret not in text)
            os.environ['BOO_KEYSCAN_SELFTEST_EMPTY'] = ''
            check('empty secret variable is skipped',
                  run([clean], ['BOO_KEYSCAN_SELFTEST_EMPTY', 'BOO_KEYSCAN_UNSET'])[0] == 0)
        finally:
            fake_oracle = None
            os.environ.pop('BOO_KEYSCAN_SELFTEST_SECRET', None)
            os.environ.pop('BOO_KEYSCAN_SELFTEST_EMPTY', None)

        # --delete removes exactly the files with hits
        case = tmp / 'delete'
        case.mkdir()
        (case / 'leak.log').write_text(fakes['Google API key'], encoding='utf-8')
        (case / 'fine.log').write_text('fine', encoding='utf-8')
        code, _ = run([case], delete=True)
        check('--delete removes the leaking file only', code == 1 and not (case / 'leak.log').exists()
              and (case / 'fine.log').exists())

        # the command line exits 1 on a hit (what every workflow step relies on)
        case = tmp / 'cli'
        case.mkdir()
        (case / 'x.log').write_text(fakes['OpenAI/Anthropic API key'], encoding='utf-8')
        proc = subprocess.run([sys.executable, '-I', __file__, str(case)], capture_output=True, text=True)
        check('command line exits 1 on a hit', proc.returncode == 1 and 'KEY_SCAN_FAILED' in proc.stdout)
        check('command line never prints the value', fakes['OpenAI/Anthropic API key'] not in proc.stdout + proc.stderr)

    # the scanner's own source holds no key-shaped text (the fakes above are built from pieces)
    own = pathlib.Path(__file__).read_bytes()
    check('this file holds no key-shaped text', not list(shape_matches(own)))

    total = 2 + 9 * 5 * 2 - 4 + 3 + 6 * 2 + 1 + 1 + 2 + 1
    if failures:
        print(f'KEY_SCAN_SELF_TEST_FAILED {len(failures)} of {total}:')
        for f in failures:
            print('  ' + f)
        return 1
    print(f'KEY_SCAN_SELF_TEST_PASSED {total} checks')
    return 0


def main(argv):
    if argv[:1] == ['--self-test']:
        return self_test()
    secret_envs, paths, delete = [], [], False
    it = iter(argv)
    for arg in it:
        if arg == '--secret-env':
            secret_envs.append(next(it, ''))
        elif arg == '--delete':
            delete = True
        else:
            paths.append(arg)
    if not paths:
        print(__doc__)
        return 2
    return scan(paths, secret_envs, delete)


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
