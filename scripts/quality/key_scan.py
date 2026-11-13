"""Key scan (BOO-SAFE-002): refuse to upload, or add in a PR, any API key. Never prints a key, only where it is.

  python -I scripts/quality/key_scan.py --preflight [--secret-env NAME ...] --delete PATH
      What every upload step runs (through the exact PowerShell wrapper from `step_run()`, enforced by
      --check-workflows). First the self-test and the workflow check; then scan every file under PATH and every file
      name. Exit 10 on a hit (the hit files are deleted). FAILS CLOSED: if anything else goes wrong (self-test,
      workflow check, crash, unreadable folder, read-only file, wildcard path), every file under PATH is deleted and
      the exit code is 3; the PowerShell wrapper also empties PATH on any exit code other than 0 and 10 (including
      Python failing to start). Above all, the upload step runs ONLY when its scan step reported `safe=true`, which
      the wrapper writes only after exit 0, or exit 10 with every hit deleted; any other outcome skips the upload.
      --secret-env NAME   also look for the exact value of environment variable NAME (a real secret the job holds),
                          plain, UTF-16 (LE/BE), URL-encoded and base64 (standard and URL-safe, any alignment).
                          Unset or empty variables are skipped.
  python -I scripts/quality/key_scan.py --diff BASE
      Scan the lines added by `git diff BASE...HEAD` (text, binary and UTF-16 files). Prints file:line, never the match.
  python -I scripts/quality/key_scan.py --check-workflows
      Every step in .github/workflows that uses an `upload` action must come right after (same job) a key-scan step
      with `id: scan_id(path)` whose run block is exactly `step_run(path)` for the same single, literal path (letters,
      digits, `._/-` only), and the upload's `if:` must require that step's `safe` output. No `working-directory` or
      `defaults:` anywhere in such a workflow.
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
import shutil
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
GLOB = re.compile(r'[*?\[\]!{}]')
SAFE_PATH = re.compile(r'^[A-Za-z0-9._/-]+$')
# An upload path in a workflow must also start with a letter: PyYAML (YAML 1.1) and GitHub (YAML 1.2 core) read some
# plain values differently (`08` is text to one and the number 8 to the other), and no reading of a value that starts
# with a letter is a number; true/false/null/yes/no are refused because PyYAML does not read them as text.
UPLOAD_PATH = re.compile(r'^[A-Za-z][A-Za-z0-9._/-]*$')

# `sk-` forms keep a word edge (so `risk-...`, `flask-admin-...` are not keys) and `AIza` must not sit inside base64
# text; every other prefix is distinctive and is found anywhere, also glued to other bytes in a binary. The edge also
# accepts an escaped control character written as text (`\n`).
_EDGE = rb'(?:(?<![A-Za-z0-9_])|(?<=\\[ntr0]))'
SHAPES = [
    ('OpenAI/Anthropic API key',
     rb'(?:(?<![A-Za-z0-9_/-])|(?<=\\[ntr0]))sk-(?:(?:proj|svcacct|admin|ant-[a-z]+\d*)-)?[A-Za-z0-9_-]{20,}'),
    ('GitHub token', rb'(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,}'),
    ('GitHub fine-grained token', rb'github_pat_[A-Za-z0-9_]{60,}'),
    ('Google API key', rb'(?:(?<![A-Za-z0-9+/])|(?<=\\[ntr0]))AIza[0-9A-Za-z_-]{35}'),   # not inside base64 text
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
    whole = text.encode('utf-8', errors='replace')
    if scan_bytes(whole, (), secrets) and not any(scan_bytes(x.encode('utf-8', errors='replace'), (), secrets)
                                                  for x in text.split('/')):
        return f'<hidden path sha256:{hashlib.sha256(whole).hexdigest()[:12]}>'   # a key across a "/": hide it all
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
    if not p.exists():
        return
    seen = set()
    def fail(error):
        raise error
    for top, dirs, files in os.walk(p, followlinks=True, onerror=fail):
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
        names = list(f.parts) + [f.as_posix()]     # each folder/file name on its own, and the path joined with "/"
        hits = [(k, label, 'file name', o, n) for part in names
                for k, label, _, o, n in scan_bytes(part.encode('utf-8', errors='ignore'), allowed, secrets)]
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

def scan_id(path):
    """The step id of the scan for one upload path (the upload's `if:` reads its `safe` output)."""
    return 'keyscan_' + re.sub(r'[^A-Za-z0-9]+', '_', path).strip('_').lower()


def upload_if(path, cancelled_guard):
    cond = f"steps.{scan_id(path)}.outputs.safe == 'true'"
    return '${{ ' + ('!cancelled() && ' if cancelled_guard else '') + cond + ' }}'


def step_run(path, secret_envs=()):
    """The exact `run:` block of a scan step for one upload path. --check-workflows accepts nothing else. It writes
    `safe=true` (the only thing that lets the upload run) after exit 0, or exit 10 with every hit deleted."""
    extra = ''.join(f' --secret-env {name}' for name in secret_envs)
    return '\n'.join([
        '$rc = 98',
        f'try {{ python -I scripts/quality/key_scan.py --preflight{extra} --delete {path}; $rc = $LASTEXITCODE }} '
        'catch { $rc = 99 }',
        f"if ($rc -eq 0 -or $rc -eq {EXIT_HIT}) {{ Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value 'safe=true' }}",
        'if ($rc -eq 0) { exit 0 }',
        f"if ($rc -ne {EXIT_HIT}) {{ Remove-Item -LiteralPath '{path}' -Recurse -Force -ErrorAction SilentlyContinue }}",
        f"if ($rc -ne {EXIT_HIT} -and (Test-Path -LiteralPath '{path}')) "
        "{ '::error::BOO-SAFE-002 the upload path could not be emptied after a failed key scan' }",
        'exit 1',
    ])


BLOCK_KEY = re.compile(r'^( *)(- )?run: *\|-? *$')          # the only block scalar allowed: a run: | script
KEY_LINE = re.compile(r'^ *(- )?[A-Za-z][A-Za-z0-9_-]*:(?: +(?P<v>.*))?$')
ITEM_LINE = re.compile(r'^ *- (?P<v>.*)$')
FLOW_LIST = re.compile(r'^\[\s*(?:"?[\w./*-]+"?\s*(?:,\s*"?[\w./*-]+"?\s*)*)?\]$')


def _plain(value):
    """A simple value: empty, a comment, a plain or quoted scalar without escapes, a ${{ expression }}, or a flow
    list of plain words."""
    v = (value or '').strip()
    if not v or v.startswith('#') or v.startswith('${{'):
        return True
    if v[0] in '"\'':                                # one quoted scalar without escapes, then at most a comment
        end = v.find(v[0], 1)
        rest = v[end + 1:].strip() if end > 0 else 'x'
        return end > 0 and '\\' not in v[1:end] and (not rest or rest.startswith('#'))
    m = re.search(r'\s#', v)                         # a trailing comment is not part of a plain value
    if m:
        v = v[:m.start()].rstrip()
    if v[0] == '[':
        return bool(FLOW_LIST.match(v))
    return v[0] not in '!&*?|>%@`{,]}'               # tags, anchors, aliases, ? keys, flow mappings start a value


def grammar_problems(text):
    """Allow-list: every line outside `run: |` scripts must be blank, a comment, `key: plain-value`,
    `- key: plain-value`, or `- plain-item`, with unquoted keys of letters, digits, `_` and `-`. Anything else
    (other block scalars such as `path: |`, quoted or escaped keys, `? ` complex keys, `!` tags, anchors, aliases,
    merges, flow mappings, documents, tabs) fails. Defence in depth: the meaning is read by PyYAML below."""
    bad, block = [], None
    for n, line in enumerate(text.splitlines(), 1):
        indent = len(line) - len(line.lstrip(' '))       # plain spaces; KEY_LINE / ITEM_LINE refuse any other space
        if not line.strip() or line.lstrip().startswith('#'):
            continue
        if block is not None:
            if indent > block:
                continue
            block = None
        m = BLOCK_KEY.match(line)
        if m:
            block = len(m.group(1)) + (2 if m.group(2) else 0)
            continue
        k = KEY_LINE.match(line)
        if k:
            if not _plain(k.group('v')):
                bad.append(n)
            continue
        it = ITEM_LINE.match(line)
        if it:
            item = it.group('v')
            head = re.split(r'\s#', item, maxsplit=1)[0]
            # a plain list item only: no extra spaces after the dash, and no `key:` (that would be a mapping the text
            # reader does not see as a step)
            if item == item.lstrip() and not re.search(r':(\s|$)', head) and _plain(item):
                continue
        bad.append(n)
    return bad


def _yaml_steps(text):
    """(doc, [(job, body, steps)]) read by PyYAML exactly as a YAML parser reads it; None when PyYAML is missing,
    'invalid' when the text is not YAML."""
    try:
        import yaml
    except ImportError:
        return None
    try:
        class _NoDuplicates(yaml.SafeLoader):
            pass

        def _mapping(loader, node, deep=False):
            seen = set()
            for key_node, _ in node.value:
                key = loader.construct_object(key_node, deep=deep)
                if isinstance(key, (list, dict, set)):     # a complex key: reported as a problem, not a crash
                    raise yaml.constructor.ConstructorError(None, None, 'a list or mapping used as a key',
                                                            key_node.start_mark)
                folded = key.lower() if isinstance(key, str) else key
                if folded in seen:
                    raise yaml.constructor.ConstructorError(None, None, f'duplicate key {key!r}', key_node.start_mark)
                seen.add(folded)
            return yaml.SafeLoader.construct_mapping(loader, node, deep=deep)

        _NoDuplicates.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, _mapping)
        doc = yaml.load(text, Loader=_NoDuplicates)  # noqa: S506 (a SafeLoader subclass)
    except Exception:                                 # noqa: BLE001 - any loader failure (a malformed !!int, !!timestamp,
        return 'invalid'                              # ...) is "not valid YAML", reported as a problem, never a traceback
    out = []
    jobs = doc.get('jobs') if isinstance(doc, dict) else None
    for job, body in (jobs or {}).items():
        steps = body.get('steps', []) if isinstance(body, dict) else []
        out.append((job, body if isinstance(body, dict) else {}, steps if isinstance(steps, list) else []))
    return doc, out


WORKFLOW_KEYWORDS = {'jobs', 'steps', 'uses', 'with', 'path', 'run', 'if', 'id', 'name', 'env', 'shell',
                     'working-directory', 'defaults', 'needs', 'runs-on', 'on', 'continue-on-error', 'strategy',
                     'container', 'services', 'outputs', 'timeout-minutes', 'permissions', 'concurrency'}


def case_problems(node, where='top'):
    """Keys that are not strings (other than YAML's `on:` read as true) or that differ only in case from a workflow
    keyword (`Uses:`, `STEPS:`): the check reads keys exactly, so such a key must fail rather than be skipped."""
    bad = []
    if isinstance(node, dict):
        for k, v in node.items():
            if k is True and where == 'top':
                pass
            elif not isinstance(k, str):
                bad.append(f'{where}: a key that is not text ({type(k).__name__})')
            elif k.lower() in WORKFLOW_KEYWORDS and k != k.lower():
                bad.append(f'{where}: key {k!r} differs only in case from {k.lower()!r}')
            bad += case_problems(v, f'{where}.{k}')
    elif isinstance(node, list):
        for i, v in enumerate(node):
            bad += case_problems(v, f'{where}[{i}]')
    return bad


def _check_pair(wf_name, prev, s, problems):
    """One upload step and the step right before it, both as read by PyYAML."""
    name = s.get('name', s.get('uses'))
    w = s.get('with') if isinstance(s.get('with'), dict) else {}
    path = w.get('path') if isinstance(w.get('path'), str) else ''
    if not UPLOAD_PATH.match(path) or GLOB.search(path):
        problems.append(f'{wf_name}: "{name}" must upload one literal path of letters, digits and ._/- only, starting '
                        'with a letter')
        return
    env = prev.get('env', {})
    if not isinstance(env, dict):
        env = {'<unreadable>': ''}
    bad_env = [k for k, v in env.items() if not (isinstance(k, str) and re.fullmatch(r'[A-Z0-9_]+_KEY', k)
                                                and isinstance(v, str)
                                                and re.fullmatch(r'\$\{\{ secrets\.[A-Z0-9_]+ \}\}', v))]
    if bad_env:
        problems.append(f'{wf_name}: "{name}": the scan step env may only hold NAME_KEY: ${{{{ secrets.X }}}}')
        return
    secret_envs = sorted(env)
    extra_keys = {str(k) for k in prev} - {'name', 'id', 'if', 'shell', 'env', 'run'}
    run = prev.get('run')
    if (not isinstance(run, str) or run.rstrip('\n') != step_run(path, secret_envs) or prev.get('shell') != 'pwsh'
            or prev.get('id') != scan_id(path) or extra_keys):
        problems.append(f'{wf_name}: "{name}" must come right after a key-scan step with id: {scan_id(path)}, '
                        f'shell: pwsh and a run block exactly key_scan.step_run({path!r}), no other keys'
                        + (f' (found keys: {", ".join(sorted(extra_keys))})' if extra_keys else ''))
        return
    allowed = {upload_if(path, False): '', upload_if(path, True): '${{ !cancelled() }}'}
    cond = s.get('if', '')
    if cond not in allowed:
        problems.append(f'{wf_name}: "{name}" must have if: {upload_if(path, True)} (or without the '
                        '!cancelled() guard) so it runs only after its scan reported safe')
    elif prev.get('if', '') != allowed.get(cond):
        problems.append(f'{wf_name}: "{name}": its scan step must have if: {allowed.get(cond) or "(none)"}')


def check_workflows(folder=None, out=print, use_grammar=True):
    """Two layers, both must pass: (1) the allow-list grammar on every line of every workflow; (2) the meaning, read by
    PyYAML (hash-pinned from scripts/ci/requirements-ci.txt in every workflow that scans). No PyYAML = fail closed."""
    folder = folder or WORKFLOWS
    problems, uploads = [], 0
    for wf in sorted(pathlib.Path(folder).glob('*.y*ml')):
        text = wf.read_text(encoding='utf-8')
        exempt = wf.name == 'release.yml'                 # documented gap (RULES.md): release uploads not scanned yet
        for n in (grammar_problems(text) if use_grammar and not exempt else []):
            problems.append(f'{wf.name}:{n}: the key-scan check only accepts plain block YAML here (no quoted or '
                            'escaped keys, ? keys, tags, anchors, aliases, merges, flow mappings, or block scalars '
                            'other than run: |)')
        parsed = _yaml_steps(text)
        if parsed is None:
            problems.append('PyYAML is not installed: install scripts/ci/requirements-ci.txt (hash-pinned) first')
            break
        if parsed == 'invalid':
            problems.append(f'{wf.name}: not valid YAML')
            continue
        doc, jobs = parsed
        problems += [f'{wf.name}: {b}' for b in case_problems(doc)]
        scanned = False
        for job, body, steps in jobs:
            if 'uses' in body and not exempt:
                problems.append(f'{wf.name}: job {job}: a reusable workflow (job-level uses:) cannot be checked for '
                                'unscanned uploads')
            for i, s in enumerate(steps):
                if isinstance(s, dict) and isinstance(s.get('uses'), str) and s['uses'].startswith('.') and not exempt:
                    problems.append(f'{wf.name}: job {job}: a local action ({s["uses"]}) cannot be checked for '
                                    'unscanned uploads')
                if not isinstance(s, dict):
                    problems.append(f'{wf.name}: job {job}: step {i + 1} is not a mapping')
                    continue
                uses = s.get('uses')
                if not (isinstance(uses, str) and 'upload' in uses.lower()):
                    continue
                uploads += 1
                if wf.name == 'release.yml':
                    continue                               # documented gap (RULES.md): release uploads not scanned yet
                scanned = True
                prev = steps[i - 1] if i and isinstance(steps[i - 1], dict) else {}
                _check_pair(wf.name, prev, s, problems)
        if scanned:
            nested = any(isinstance(b, dict) and ('defaults' in b or any(
                isinstance(st, dict) and 'working-directory' in st for st in steps)) for _, b, steps in jobs)
            if nested or (isinstance(doc, dict) and 'defaults' in doc) or re.search(
                    r'^\s*(working-directory|defaults)\s*:', text, re.M):
                problems.append(f'{wf.name}: no working-directory or defaults: in a workflow with scanned uploads')
    for p in problems:
        out(f'::error::{RULE} workflow check: {p}')
    if problems:
        out(f'KEY_SCAN_WORKFLOWS_FAILED {len(problems)} problem(s)')
        return 1
    out(f'KEY_SCAN_WORKFLOWS_OK {uploads} upload step(s) checked (grammar + PyYAML)')
    return 0


# ---------------------------------------------------------------- PR diff

def _unquote(name):
    """git C-quotes a path holding `"`, `\\`, control characters (and non-ASCII unless core.quotePath=false)."""
    if not (name.startswith(b'"') and name.endswith(b'"')):
        return name
    out, body, i = bytearray(), name[1:-1], 0
    simple = {ord('n'): 10, ord('t'): 9, ord('r'): 13, ord('a'): 7, ord('b'): 8, ord('f'): 12, ord('v'): 11}
    while i < len(body):
        c = body[i]
        if c == 0x5c and i + 1 < len(body):
            n = body[i + 1]
            if 0x30 <= n <= 0x37 and i + 3 < len(body) + 1:
                out.append(int(body[i + 1:i + 4], 8))
                i += 4
                continue
            out.append(simple.get(n, n))
            i += 2
            continue
        out.append(c)
        i += 1
    return bytes(out)


def scan_diff(base, out=print, cwd=None, oracle=None):
    allowed = FrozenTests(oracle)
    diff = subprocess.run(['git', '-c', 'core.quotePath=false', 'diff', '--no-color', '--no-ext-diff', '--no-textconv',
                           '--text', '-U0', f'{base}...HEAD'], capture_output=True, cwd=cwd or root)
    names = subprocess.run(['git', 'diff', '--name-only', '--diff-filter=ACR', '-z', f'{base}...HEAD'],
                           capture_output=True, cwd=cwd or root)
    if diff.returncode != 0 or names.returncode != 0:
        out(f'::error::{RULE} could not read the diff against {base}')
        return EXIT_FAIL_CLOSED
    file, line_no, in_hunk, hits = None, 0, False, []
    for name in filter(None, names.stdout.split(b'\0')):          # a key in an added or renamed file NAME
        for part in name.split(b'/') + [name]:                    # each name on its own, and the whole path
            for _, label, _, _, length in scan_bytes(part, allowed, ()):
                hits.append((name.decode('utf-8', errors='replace'), 0, label, length))
    for raw in diff.stdout.split(b'\n'):
        if raw.startswith(b'diff --git '):
            file, in_hunk = None, False
        elif not in_hunk and raw.startswith(b'+++ '):
            name = _unquote(raw[4:].rstrip(b'\t'))
            file = name[2:].decode('utf-8', errors='replace') if name.startswith(b'b/') else None
        elif raw.startswith(b'@@'):
            m = re.search(rb'\+(\d+)', raw)
            line_no, in_hunk = (int(m.group(1)) if m else 0), True
        elif in_hunk and raw.startswith(b'+') and file:
            for _, label, _, _, length in scan_bytes(raw[1:], allowed, ()):
                hits.append((file, line_no, label, length))
            line_no += 1
    for file, line_no, label, length in hits:
        where = f'{safe(file)}:{line_no}' if line_no else f'{safe(file)} (in the file name)'
        out(f'::error file={safe(file)},line={line_no or 1}::{RULE} key-shaped text: {label} ({length} chars) added '
            f'at {where}. The value is not shown.')
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
    tmp_root = [None]

    def cli(*args, extra_env=None):
        here = pathlib.Path(tmp_root[0])
        args = [a.relative_to(here).as_posix() if isinstance(a, pathlib.Path) else str(a) for a in args]
        return subprocess.run([sys.executable, '-I', os.path.abspath(__file__), *args], capture_output=True,
                              cwd=here, env=dict(env, **(extra_env or {})))

    with tempfile.TemporaryDirectory() as tmp:
        tmp = pathlib.Path(tmp)
        tmp_root[0] = tmp
        clean = tmp / 'clean'
        clean.mkdir()
        tail = chars(alnum, 40)
        negatives = ['risk-assessment sk-short ghp_ abc AIza tskey hf_x Bearer token eyJx',
                     'pip install flask-admin-' + tail, 'risk-proj-' + tail + '.xlsx',     # sk- edge
                     'img/png;base64,Qk9' + 'AI' + 'za' + chars(alnum, 35),              # AIza inside base64
                     'id=' + 'AK' + 'IA' + chars(string.ascii_uppercase, 20),           # AWS id runs on: not 16
                     'b64url-' + 's' + 'k-' + tail, 'path/' + 's' + 'k-' + tail]        # -sk- and /sk-
        (clean / 'log.txt').write_text('\n'.join(negatives) + '\nok\n', encoding='utf-8')
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
                    if label in ('OpenAI/Anthropic API key', 'bearer token', 'Google API key'):
                        continue                    # sk- / bearer / AIza keep a leading edge by design
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
                    (case / 'sub').mkdir()          # inside a folder: the separator must not hide the key
                    (case / 'sub' / f'{fake}.txt').write_text('x', encoding='utf-8')
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

        # a key that crosses a "/" in the path is still found, and never printed
        span = tmp / 'span'
        bearer = 'Bear' + 'er ' + chars(alnum, 20)
        (span / 'x' / bearer).mkdir(parents=True)
        (span / 'x' / bearer / (chars(alnum, 20) + '.txt')).write_text('x', encoding='utf-8')
        code, text = run(span)
        check('a bearer token split over a folder and a file name fails', code == EXIT_HIT)
        check('a bearer token across a "/" is never printed', bearer[7:] not in text)
        slash_secret = chars(alnum, 16) + '/' + chars(alnum, 16)
        span2 = tmp / 'span2'
        a, b = slash_secret.split('/')
        (span2 / 'x' / a).mkdir(parents=True)
        (span2 / 'x' / a / (b + '.txt')).write_text('x', encoding='utf-8')
        os.environ['KEYSCAN_SELFTEST_SLASH_KEY'] = slash_secret
        try:
            code, text = run(span2, secret_envs=['KEYSCAN_SELFTEST_SLASH_KEY'])
        finally:
            del os.environ['KEYSCAN_SELFTEST_SLASH_KEY']
        check('a secret value holding "/" spread over folder and file names fails', code == EXIT_HIT)
        check('a secret value across a "/" is never printed (whole path hidden)', a not in text and b not in text)

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
        for args in (['--bogus', clean], ['--help', clean], ['--delete', '--Delete', clean], ['--secret-env'],
                     ['--secret-env', clean], ['--secret-env', 'lower', clean], ['-x'], ['--self-test', 'extra'],
                     ['--check-workflows', clean], ['--diff'], ['--diff', 'a', 'b'], ['--preflight', '--help']):
            proc = cli(*args)
            check(f'the command line {[a if isinstance(a, str) else "PATH" for a in args]} is refused (non-zero, '
                  'never a pass)', proc.returncode not in (0, EXIT_HIT))
        check('--help alone prints usage and exits 2', cli('--help').returncode == 2)
        keep = tmp / 'keep'
        (keep / 'sub').mkdir(parents=True)
        (keep / 'sub' / 'a.txt').write_text('x', encoding='utf-8')
        for bad in ('', '.', './x', '../x', 'out/../x', '/abs', '-x', 'out/x\t', 'a b'):
            proc = subprocess.run([sys.executable, '-I', os.path.abspath(__file__), '--delete', '--bogus', bad], capture_output=True,
                                  cwd=keep, env=env)
            check(f'the path {bad!r} is refused and nothing is deleted', proc.returncode == EXIT_FAIL_CLOSED
                  and (keep / 'sub' / 'a.txt').exists())
            proc = subprocess.run([sys.executable, '-I', os.path.abspath(__file__), '--delete', bad], capture_output=True,
                                  cwd=keep, env=env)
            check(f'the path {bad!r} alone is refused, never a pass', proc.returncode == EXIT_FAIL_CLOSED
                  and (keep / 'sub' / 'a.txt').exists())

        # workflow check: the scan step (id + exact run) right before the upload in the same job, upload gated on safe
        def wf_text(variant='', path='out/x', uses='actions/upload-artifact@v4', guard=True, scan_path='out/x'):
            run_block = '\n'.join('          ' + line for line in step_run(scan_path).splitlines())
            scan = ('      - name: Key scan before upload (x)\n'
                    + ('        id: ' + scan_id(scan_path) + '\n' if variant != 'no-id' else '')
                    + ('        if: ${{ !cancelled() }}\n' if guard and variant != 'scan-no-if' else '')
                    + ('        working-directory: sub\n' if variant == 'workdir' else '')
                    + '        shell: pwsh\n        run: |\n'
                    + (run_block.replace(' --delete', '') if variant == 'no-delete' else run_block) + '\n')
            cond = upload_if(scan_path, guard)
            if variant == 'upload-not-gated':
                cond = '${{ !cancelled() }}'
            upload = (f'      - name: Upload x\n        if: {cond}\n        uses: {uses}\n'
                      f'        with:\n          name: x\n          path: {path}\n')
            head = 'jobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n'
            if variant == 'no-scan':
                scan = ''
            if variant == 'other-job':
                return head + scan + '  b:\n    runs-on: ubuntu-latest\n    steps:\n' + upload
            if variant == 'defaults':
                head = 'jobs:\n  a:\n    runs-on: ubuntu-latest\n    defaults:\n      run:\n        shell: pwsh\n    steps:\n'
            return head + scan + upload
        wf = tmp / 'wf'
        wf.mkdir()
        (wf / 'a.yml').write_text(wf_text(), encoding='utf-8')
        check('workflow check passes the generated scan step', check_workflows(wf, out=quiet) == 0)
        (wf / 'a.yml').write_text(wf_text(guard=False), encoding='utf-8')
        check('workflow check passes the generated scan step without the cancelled guard',
              check_workflows(wf, out=quiet) == 0)
        for name, text in (('no scan step', wf_text('no-scan')),
                           ('scan without --delete', wf_text('no-delete')),
                           ('scan with working-directory', wf_text('workdir')),
                           ('workflow defaults', wf_text('defaults')),
                           ('scan without its id', wf_text('no-id')),
                           ('upload not gated on the scan result', wf_text('upload-not-gated')),
                           ('scan if differs from upload', wf_text('scan-no-if')),
                           ('scan in another job', wf_text('other-job')),
                           ('wrong path', wf_text(path='out/y')),
                           ('wildcard path', wf_text(path='out/**/*.log')),
                           ('quote in path', wf_text(path="out/x'y")),
                           ('quote in path, scan generated for it', wf_text(path="out/x'y", scan_path="out/x'y")),
                           ('dollar in path, scan generated for it', wf_text(path='out/$x', scan_path='out/$x')),
                           ('multi-line path', wf_text(path='|\n            out/x\n            out/y')),
                           ('another upload action unscanned', wf_text('no-scan', uses='actions/upload-pages-artifact@v3')),
                           ('quoted uses key', wf_text('no-scan').replace('        uses:', '        "uses":')),
                           ('flow-style upload step', wf_text().replace(
                               '      - name: Upload x\n', '      - {name: Up, uses: actions/upload-artifact@v4, with: {path: s}}\n'
                               '      - name: Upload x\n')),
                           ('dash alone on its line', wf_text().replace('      - name: Upload x', '      -\n        name: Upload x')),
                           ('extra spaces after the dash', wf_text().replace('      - name: Upload x', '      -   name: Upload x')),
                           ('anchor in a step', wf_text().replace('          name: x\n', '          name: &n x\n')),
                           ('scan env not a *_KEY secret (run block generated to match)', wf_text().replace(
                               '        shell: pwsh\n', '        shell: pwsh\n        env:\n          PATH: tools\n', 1)
                            .replace('--preflight --delete', '--preflight --secret-env PATH --delete')),
                           ('scan env *_KEY but not a secret (run block generated to match)', wf_text().replace(
                               '        shell: pwsh\n', '        shell: pwsh\n        env:\n          X_KEY: plain\n', 1)
                            .replace('--preflight --delete', '--preflight --secret-env X_KEY --delete'))):
            (wf / 'a.yml').write_text(text, encoding='utf-8')
            check(f'workflow check fails: {name}', check_workflows(wf, out=quiet) == 1)
            if not grammar_problems(text):                 # valid plain YAML: the meaning layer alone must catch it
                check(f'the PyYAML meaning layer alone catches: {name}',
                      check_workflows(wf, out=quiet, use_grammar=False) == 1)
        try:
            import yaml  # noqa: F401
            yaml_here = True
        except ImportError:
            yaml_here = False
        check('PyYAML is installed (the workflow check needs it)', yaml_here)
        flow = '      - {uses: actions/upload-artifact@v4, with: {path: s}}\n'
        scan_marker = '        id: ' + scan_id('out/x') + '\n'
        for name, text in (('jobs: with a comment, then a flow step',
                            wf_text().replace('jobs:\n', 'jobs: # c\n').replace('      - name: Upload x\n', flow + '      - name: Upload x\n')),
                           ('quoted jobs key, then a flow step',
                            wf_text().replace('jobs:\n', '"jobs":\n').replace('      - name: Upload x\n', flow + '      - name: Upload x\n')),
                           ('escaped key in a new upload step', wf_text() + '      - name: z\n        "u\\x73es": actions/upload-artifact@v4\n'),
                           ('escaped value in a new upload step', wf_text() + '      - name: z\n        uses: "actions/\\x75pload-artifact@v4"\n'),
                           ('complex key in a new upload step', wf_text() + '      - name: z\n        ? uses\n        : actions/upload-artifact@v4\n'),
                           ('tagged key in a new upload step', wf_text() + '      - name: z\n        !!str uses: actions/upload-artifact@v4\n'),
                           ('flow step list on the next line', wf_text().replace('    steps:\n', '    steps:\n      [{uses: actions/upload-artifact@v4, with:\n        {path: s}}]\n    other:\n', 1)),
                           ('escaped working-directory on the scan step', wf_text().replace(scan_marker, scan_marker + '        "w\\x6frking-directory": decoy\n')),
                           ('complex-key working-directory on the scan step', wf_text().replace(scan_marker, scan_marker + '        ? working-directory\n        : decoy\n')),
                           ('tagged env on the scan step', wf_text().replace(scan_marker, scan_marker + '        !!str env:\n          PATH: tools\n')),
                           ('upload path hidden in a path: | block', wf_text().replace(
                               '          path: out/x\n', '          path: |\n            out\n            path: out/x\n')),
                           ('quoted uses key', wf_text('no-scan').replace('        uses:', '        "uses":')),
                           ('flow-style upload step', wf_text().replace('      - name: Upload x\n', flow + '      - name: Upload x\n'))):
            (wf / 'a.yml').write_text(text, encoding='utf-8')
            check(f'workflow check fails: {name}', check_workflows(wf, out=quiet) == 1)
            check(f'the grammar alone rejects: {name}', bool(grammar_problems(text)))
            check(f'the PyYAML meaning layer alone catches: {name}', check_workflows(wf, out=quiet, use_grammar=False) == 1)
        for bad_path in ('08', '0o10', '1e3', '.5e3', '-08', 'yes'):
            text = wf_text(path=bad_path, scan_path=bad_path)
            (wf / 'a.yml').write_text(text, encoding='utf-8')
            check(f'workflow check fails: number-shaped upload path {bad_path} (scan generated for it)',
                  check_workflows(wf, out=quiet) == 1)
        for name, text in (('duplicate uses key in a step', wf_text().replace(
                               '        uses: actions/upload-artifact@v4\n',
                               '        uses: actions/upload-artifact@v4\n        uses: actions/upload-artifact@v4\n')),
                           ('duplicate key differing only in case', wf_text().replace(
                               '          name: x\n', '          name: x\n          Name: y\n')),
                           ('duplicate non-keyword key differing only in case', wf_text().replace(
                               '          name: x\n', '          name: x\n          retention-days: 7\n          Retention-days: 7\n')),
                           ('reusable workflow job', wf_text() + '  b:\n    uses: org/repo/.github/workflows/up.yml@v1\n'),
                           ('local action step', wf_text() + '      - uses: ./.github/actions/up\n'),
                           ('non-ASCII letter in a key', wf_text('no-scan').replace('        uses:', '        u\u017fes:')),
                           ('Unicode space as indentation', wf_text().replace('        shell: pwsh\n', '\u3000       shell: pwsh\n'))):
            (wf / 'a.yml').write_text(text, encoding='utf-8')
            check(f'workflow check fails: {name}', check_workflows(wf, out=quiet) == 1)
        (wf / 'a.yml').write_text(wf_text().replace('          name: x\n', '          name: x\n          ? [a]\n          : b\n'),
                                  encoding='utf-8')
        check('a list used as a key is a reported problem, not a crash (meaning layer alone)',
              check_workflows(wf, out=quiet, use_grammar=False) == 1)
        for space in ('\t', '　', ' '):
            check(f'the grammar alone rejects indentation by {space!r}',
                  bool(grammar_problems(wf_text().replace('        shell: pwsh\n', '      ' + space + ' shell: pwsh\n'))))
        for name, text in (('capital Uses: in an unscanned upload', wf_text('no-scan').replace('        uses:', '        Uses:')),
                           ('capital Steps: hides the steps', wf_text().replace('    steps:\n', '    Steps:\n')),
                           ('folded run: > scan block', wf_text().replace('        run: |\n', '        run: >\n')),
                           ('continuation line after the path',
                            wf_text().replace('          path: out/x\n', '          path: out/x\n\n            - evil\n'))):
            (wf / 'a.yml').write_text(text, encoding='utf-8')
            check(f'workflow check fails: {name}', check_workflows(wf, out=quiet) == 1)
            check(f'the PyYAML meaning layer alone catches: {name}', check_workflows(wf, out=quiet, use_grammar=False) == 1)
        for name, tagged in (('!!int', 'x: !!int abc\n'), ('!!float', 'x: !!float abc\n'),
                             ('!!timestamp', 'x: !!timestamp xx\n'), ('!!int key', '!!int abc: 1\n')):
            (wf / 'a.yml').write_text(wf_text() + tagged, encoding='utf-8')
            try:
                check(f'a malformed {name} value is reported as not valid YAML (no crash)',
                      check_workflows(wf, out=quiet, use_grammar=False) == 1)
            except Exception:                         # noqa: BLE001
                check(f'a malformed {name} value is reported as not valid YAML (no crash)', False)
        (wf / 'a.yml').write_text(wf_text(), encoding='utf-8')
        check('generated scan step passes the PyYAML layer alone', check_workflows(wf, out=quiet, use_grammar=False) == 0)
        check('the grammar alone accepts the generated scan step', grammar_problems(wf_text()) == [])
        saved = sys.modules.get('yaml', None)
        sys.modules['yaml'] = None                        # import yaml now raises ImportError
        try:
            check('a missing PyYAML fails the workflow check closed', check_workflows(wf, out=quiet) == 1)
        finally:
            if saved is None:
                del sys.modules['yaml']
            else:
                sys.modules['yaml'] = saved

        # the PowerShell wrapper itself: safe=true only after exit 0 or 10; anything else empties the path
        shell = shutil.which('pwsh') or shutil.which('powershell')
        if os.name == 'nt':
            check('PowerShell is available to test the scan wrapper', bool(shell))
        if shell:
            invocation = re.search(r'python -I scripts/quality/key_scan\.py[^;]*', step_run('out/x')).group(0)
            exe = sys.executable.replace("'", "''")

            def exits(n):
                return f"& '{exe}' -I -c 'import sys; sys.exit({n})'"
            for label, fake_call, want_safe, want_files in (('exit 0', exits(0), True, 1),
                                                            ('exit 10', exits(10), True, 1),
                                                            ('exit 3', exits(3), False, 0),
                                                            ('exit 1 (python traceback)', exits(1), False, 0),
                                                            ('python missing', 'no-such-python-xyz', False, 0)):
                w = tmp / ('wrap-' + re.sub(r'\W', '', label))
                (w / 'out' / 'x').mkdir(parents=True)
                (w / 'out' / 'x' / 'a.log').write_text('fine', encoding='utf-8')
                gh_out = w / 'gh_output.txt'
                gh_out.write_text('', encoding='utf-8')
                script = "$ErrorActionPreference = 'stop'\n" + step_run('out/x').replace(invocation, fake_call)
                subprocess.run([shell, '-NoProfile', '-NonInteractive', '-Command', script], cwd=w,
                               env=dict(env, GITHUB_OUTPUT=str(gh_out)), capture_output=True)
                wrote = 'safe=true' in gh_out.read_text(encoding='utf-8')
                check(f'wrapper on {label}: safe written={want_safe}, files left={want_files}',
                      wrote == want_safe and remaining(w / 'out' / 'x') == want_files)

        # a folder that cannot be listed fails closed (not skipped)
        case = tmp / 'unlistable'
        (case / 'sub').mkdir(parents=True)
        (case / 'a.log').write_text('fine', encoding='utf-8')
        real_scandir = os.scandir
        def deny(path='.'):
            if str(path).endswith('sub'):
                raise PermissionError(13, 'denied', str(path))
            return real_scandir(path)
        os.scandir = deny
        try:
            check('a folder that cannot be listed is an error, not skipped', run(case)[0] == -1)
        finally:
            os.scandir = real_scandir

        # a key-named file that cannot be deleted: fail closed, and its name is never printed
        if os.name == 'nt':
            case = tmp / 'heldkey'
            case.mkdir()
            held = case / (fakes['GitHub token'] + '.log')
            held.write_text('x', encoding='utf-8')
            with open(held, 'rb'):
                proc = cli('--delete', case)
            printed = proc.stdout + proc.stderr
            check('an undeletable hit fails closed (exit 3)', proc.returncode == EXIT_FAIL_CLOSED)
            check('the name of an undeletable key-named file is never printed',
                  fakes['GitHub token'].encode() not in printed and fakes['GitHub token'][4:30].encode() not in printed)

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
        if os.name != 'nt':
            (g / 'q"uote.txt').write_text(fakes['Google API key'] + '\n', encoding='utf-8')
        (g / 'back\\slash.txt').write_text(fakes['Google API key'] + '\n', encoding='utf-8') if os.name != 'nt' else None
        (g / (fakes['JSON Web Token'] + '.txt')).write_text('innocent\n', encoding='utf-8')
        (g / 'dir').mkdir()
        (g / 'dir' / (fakes['OpenAI/Anthropic API key'] + '.txt')).write_text('innocent\n', encoding='utf-8')
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
        check('PR diff: a key in an added file NAME fails', '(in the file name)' in text)
        check('PR diff: a key-named file inside a folder fails (sk- after the / separator)',
              sum('(in the file name)' in x and 'OpenAI/Anthropic' in x for x in lines) >= 1)
        check('PR diff never prints a value', not any(f.split()[-1] in text for f in fakes.values()))
        check('git C-quoted names are decoded', _unquote(b'"q\\"uote\\tx\\303\\244.txt"') == 'q"uote\tx\u00e4.txt'.encode())
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

def _cli_path_ok(path):
    """One literal relative path of letters, digits and ._/- with no empty, "." or ".." parts (never "", the current
    folder, an absolute path or a wildcard): the only kind of path the scan reads, and the only kind it may purge."""
    return (bool(SAFE_PATH.match(path)) and not GLOB.search(path)
            and not any(x in ('.', '..', '') for x in path.rstrip('/').split('/')))


def main(argv):
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')
    except (AttributeError, ValueError):
        pass
    if not argv or argv in (['--help'], ['-h']):
        print(__doc__)
        return 2                                      # usage is never a pass
    if argv == ['--self-test']:
        return self_test()
    if argv == ['--check-workflows']:
        return check_workflows()
    if argv[:1] == ['--diff']:
        if len(argv) != 2 or argv[1].startswith('-'):
            print(f'::error::{RULE} usage: key_scan.py --diff BASE (exactly one base); failing closed')
            return EXIT_FAIL_CLOSED
        return scan_diff(argv[1])
    secret_envs, paths, delete, preflight, bad = [], [], False, False, []
    it = iter(argv)
    for arg in it:
        if arg == '--secret-env':
            name = next(it, '')
            if re.fullmatch(r'[A-Z][A-Z0-9_]*', name):
                secret_envs.append(name)
            else:
                bad.append('--secret-env needs an environment variable name')
        elif arg == '--delete':
            delete = True
        elif arg == '--preflight':
            preflight = True
        elif arg.startswith('-'):
            bad.append(f'unknown option {arg[:40]!r}')    # --help with other words, a typo, a mode flag out of place
        else:
            paths.append(arg)
    try:
        if bad:
            raise ValueError('; '.join(bad))
        if len(paths) != 1 or not _cli_path_ok(paths[0]):
            raise ValueError('scan exactly one literal relative path (letters, digits and ._/- only; no "." or '
                             '".." parts, not absolute)')
        if os.environ.get('BOO_KEYSCAN_SELFTEST_CRASH') == '1':
            raise RuntimeError('simulated internal error (self-test)')
        if preflight and (self_test() != 0 or check_workflows() != 0):
            raise RuntimeError('the key scan could not prove itself')
        return scan(paths[0], secret_envs, delete)
    except BaseException as e:                       # fail closed on anything: crash, unreadable path, bad encoding
        code = getattr(e, 'errno', None)            # never the exception text: it can hold a path with a key
        print(f'::error::{RULE} key scan failed ({type(e).__name__}{f" errno {code}" if code else ""}); failing closed')
        if bad:
            print(f'::error::{RULE} the command line was not understood ({len(bad)} problem(s), e.g. an unknown '
                  'option); usage: key_scan.py [--preflight] [--secret-env NAME] [--delete] PATH')
        if delete:
            for p in paths:
                if _cli_path_ok(p):                   # never purge a refused path: "" or "." would be the whole folder
                    purge(p)
        return EXIT_FAIL_CLOSED


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
