"""BOO-DOCS-001: parallel PRs never edit the same bookkeeping lines (docs/quality/RULES.md).

  python -I scripts/quality/doc_fragments.py --self-test                every rule on planted samples (offline)
  python -I scripts/quality/doc_fragments.py check --event "$GITHUB_EVENT_PATH"   (in the doc-fragments workflow)
  python -I scripts/quality/doc_fragments.py check --base REV --pr N     local check of HEAD as PR #N against REV
  python -I scripts/quality/doc_fragments.py assemble [--out PATH]      the combined view, for reading (never commit)

Why: three tables were edited by almost every PR (the PR table in HANDOFF.md, the decision log docs/DECISIONS.md and
docs/FEATURE_MAP.md, whose counter ids like A7/A8 collide), so nearly every sync of a parallel PR hit a merge conflict
there. Those tables are now FROZEN HISTORY. New rows go in fragment files, one file per row owner, so two PRs never
touch the same file:

  docs/decisions/<pr>-<slug>.md   that PR's decision row(s)      | Date | Decision | Why | Source | Status |
  docs/handoff/<pr>.md            that PR's status row (one)     | PR | Branch → base | State | Notes |
  docs/feature-map/<name>.md      one feature-map row, named id  | # | Feature | How verified | Proof |
                                  plus `Section: <### heading of the map>` and, optionally, `Replaces: <old id>`

The check (PR mode) fails closed when:
  1. a row of a frozen table is added, edited or removed (HANDOFF.md under `### Pull requests`, docs/DECISIONS.md
     above `## Detailed decision records`, docs/FEATURE_MAP.md under `## The map`), a frozen table's heading is gone,
     or a `> **Frozen table.**` pointer line is edited or removed;
  2. any fragment in the PR's tree is malformed: bad file name, other text than the table, wrong header, wrong number
     of cells, an empty cell, more than one row where one is allowed; a handoff row whose PR link is not its own
     number; a decision date not like `16 Nov 2026`; a feature-map row whose id is not its file name, whose Section is
     not a heading of the map, or whose Replaces is not a frozen id or is replaced twice;
  3. a NEW decisions/handoff fragment carries another PR's number;
  4. another PR's decisions/handoff fragment is changed in anything but its status cell (Status / State), gains or
     loses a row, or is deleted. A PR may change its own fragments freely. Feature-map fragments belong to the
     feature, not a PR: any PR may add, edit or delete one (Test-FeatureMap.ps1 keeps the map true).
  Rules 3 and 4 treat a PR that this PR carries as its own: a fragment of PR #N passes when a merge commit
  "Merge pull request #N ..." lies between the merge base and the head (an integration PR such as lane/face -> main
  brings every merged lane's fragments; a lane PR that merged lane/face brings nothing new, its merge base moves).
Git, event or parse trouble fails closed. Stdlib only, safe under `python -I`.
"""
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile

RULE = 'BOO-DOCS-001'
HANDOFF, DECISIONS, FEATURE_MAP = 'HANDOFF.md', 'docs/DECISIONS.md', 'docs/FEATURE_MAP.md'
FROZEN_FILES = (HANDOFF, DECISIONS, FEATURE_MAP)
POINTER = '> **Frozen table.**'
KINDS = {
    'decisions': {
        'dir': 'docs/decisions/', 'pattern': '<pr number>-<slug>.md (slug: lowercase words joined by -)',
        'name': re.compile(r'^([1-9][0-9]{0,5})-([a-z0-9]+(?:-[a-z0-9]+)*)\.md$'),
        'header': ['Date', 'Decision', 'Why', 'Source', 'Status'], 'status': 4, 'one_row': False,
    },
    'handoff': {
        'dir': 'docs/handoff/', 'pattern': '<pr number>.md',
        'name': re.compile(r'^([1-9][0-9]{0,5})\.md$'),
        'header': ['PR', 'Branch → base', 'State', 'Notes'], 'status': 2, 'one_row': True,
    },
    'feature-map': {
        'dir': 'docs/feature-map/', 'pattern': '<named-id>.md (lowercase words joined by -, at least two words)',
        'name': re.compile(r'^([a-z][a-z0-9]*(?:-[a-z0-9]+)+)\.md$'),
        'header': ['#', 'Feature', 'How verified', 'Proof'], 'status': None, 'one_row': True,
    },
}
FRAGMENT_DIRS = tuple(spec['dir'] for spec in KINDS.values())
DATE = re.compile(r'^([0-9]{1,2}) (Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) ([0-9]{4})$')
MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
PR_LINK = re.compile(r'^\[#([0-9]+)\]\(https://github\.com/[\w.-]+/[\w.-]+/pull/([0-9]+)\)$')
FROZEN_ID = re.compile(r'^[A-Z]{1,2}[0-9]+$')
MAX_NAME = 72


def _short(text, n=90):
    text = text.strip()
    return text if len(text) <= n else text[:n - 3] + '...'


def cells(line):
    """The cells of a pipe-table row (split on unescaped |), or None when the line is not a row."""
    s = line.strip()
    if len(s) < 2 or not s.startswith('|') or not s.endswith('|'):
        return None
    return [c.strip() for c in re.split(r'(?<!\\)\|', s[1:-1])]


def _find(lines, test, start=0):
    for i in range(start, len(lines)):
        if test(lines[i]):
            return i
    return None


def _region(path, lines):
    """(start, end) line indexes of the frozen table region of one of the three files, or None when its heading is
    gone (then the check cannot tell what is frozen, so it fails closed)."""
    if path == DECISIONS:
        end = _find(lines, lambda l: l.startswith('## Detailed decision records'))
        return None if end is None else (0, end)
    if path == HANDOFF:
        start = _find(lines, lambda l: l.strip() == '### Pull requests')
        if start is None:
            return None
        end = _find(lines, lambda l: l.startswith('#'), start + 1)
    else:
        start = _find(lines, lambda l: re.match(r'^##\s+The map\s*$', l) is not None)
        if start is None:
            return None
        end = _find(lines, lambda l: re.match(r'^##\s', l) is not None, start + 1)
    return start, len(lines) if end is None else end


def frozen_rows(path, text):
    """Every table line of the frozen region, exactly as written (None when the region's heading is gone)."""
    lines = text.splitlines()
    region = _region(path, lines)
    if region is None:
        return None
    return [l for l in lines[region[0]:region[1]] if l.lstrip().startswith('|')]


def pointers(text):
    return [l for l in text.splitlines() if l.startswith(POINTER)]


def map_sections(text):
    """The `### ` headings under `## The map` of FEATURE_MAP.md, in order."""
    lines = text.splitlines()
    region = _region(FEATURE_MAP, lines)
    if region is None:
        return []
    return [l[4:].strip() for l in lines[region[0]:region[1]] if l.startswith('### ')]


def frozen_ids(text):
    rows = frozen_rows(FEATURE_MAP, text) or []
    return [c[0] for c in (cells(r) for r in rows) if c and FROZEN_ID.match(c[0])]


def fragment_kind(path):
    for kind, spec in KINDS.items():
        if path.startswith(spec['dir']):
            return kind
    return None


class Fragment:
    def __init__(self, kind, path, key, meta, rows):
        self.kind, self.path, self.key, self.meta, self.rows = kind, path, key, meta, rows


def parse_fragment(kind, path, text):
    """(meta, rows, problems) for one fragment file's text."""
    spec = KINDS[kind]
    problems, meta, rows = [], {}, []
    lines = [l for l in text.splitlines() if l.strip()]
    i = 0
    while i < len(lines) and not lines[i].lstrip().startswith('|'):
        m = re.match(r'^(Section|Replaces):[ \t]*(\S.*?)\s*$', lines[i])
        if kind != 'feature-map' or not m or m.group(1) in meta:
            allowed = '`Section:` and `Replaces:` lines and ' if kind == 'feature-map' else ''
            problems.append(f'{path}: the line "{_short(lines[i])}" is not allowed; a {kind} fragment holds only '
                            f'{allowed}one table')
        else:
            meta[m.group(1)] = m.group(2)
        i += 1
    table = lines[i:]
    want = '| ' + ' | '.join(spec['header']) + ' |'
    if len(table) < 3:
        problems.append(f'{path}: needs a table: the header line `{want}`, the `| --- |` line and '
                        f'{"exactly one row" if spec["one_row"] else "at least one row"}')
        return meta, rows, problems
    if cells(table[0]) != spec['header']:
        problems.append(f'{path}: the header must be exactly `{want}` (it is `{_short(table[0])}`)')
    sep = cells(table[1])
    if sep is None or len(sep) != len(spec['header']) or not all(re.fullmatch(r':?-{3,}:?', c) for c in sep):
        problems.append(f'{path}: the second line must be the `| --- |` separator with {len(spec["header"])} cells')
    for line in table[2:]:
        row = cells(line)
        if row is None:
            problems.append(f'{path}: "{_short(line)}" is not a table row (text after the table is not allowed)')
        elif len(row) != len(spec['header']):
            problems.append(f'{path}: a row has {len(row)} cells; expected {len(spec["header"])} '
                            f'({" | ".join(spec["header"])}): "{_short(line)}"')
        elif not all(row):
            problems.append(f'{path}: a row has an empty cell: "{_short(line)}"')
        else:
            rows.append(row)
    count = len(table) - 2
    if spec['one_row'] and count != 1:
        problems.append(f'{path}: a {kind} fragment holds exactly one row (it has {count})')
    return meta, rows, problems


def validate(tree, path):
    """(Fragment or None, problems) for one file under a fragment folder of `tree` (path -> text)."""
    kind = fragment_kind(path)
    spec = KINDS[kind]
    name = path[len(spec['dir']):]
    m = spec['name'].match(name)
    if not m or len(name) > MAX_NAME:
        return None, [f'{path}: bad file name; {kind} fragments are {spec["dir"]}{spec["pattern"]} '
                      f'(at most {MAX_NAME} characters, no sub-folders)']
    meta, rows, problems = parse_fragment(kind, path, tree[path])
    key = m.group(1)
    if kind == 'handoff' and rows:
        link = PR_LINK.match(rows[0][0])
        if not link or link.group(1) != key or link.group(2) != key:
            problems.append(f'{path}: the PR cell must be `[#{key}](https://github.com/<owner>/<repo>/pull/{key})`, '
                            f'the number in the file name (it is `{_short(rows[0][0], 60)}`)')
    if kind == 'decisions':
        for row in rows:
            if not DATE.match(row[0]):
                problems.append(f'{path}: the Date cell must look like `16 Nov 2026` (it is `{_short(row[0], 40)}`)')
    if kind == 'feature-map':
        if rows and rows[0][0] != key:
            problems.append(f'{path}: the id cell must be the file name `{key}` (it is `{_short(rows[0][0], 40)}`)')
        sections = map_sections(tree.get(FEATURE_MAP, ''))
        if 'Section' not in meta:
            problems.append(f'{path}: needs a `Section: <heading>` line naming a `###` heading of the map '
                            f'({", ".join(sections)})')
        elif meta['Section'] not in sections:
            problems.append(f'{path}: Section `{meta["Section"]}` is not a `###` heading under `## The map` '
                            f'({", ".join(sections)})')
        if 'Replaces' in meta and meta['Replaces'] not in frozen_ids(tree.get(FEATURE_MAP, '')):
            problems.append(f'{path}: Replaces `{meta["Replaces"]}`, which is not an id of the frozen map table')
    return Fragment(kind, path, key, meta, rows), problems


def check_tree(tree):
    """(fragments, problems) for every file under the fragment folders of `tree`."""
    problems, frags = [], []
    for path in sorted(p for p in tree if fragment_kind(p)):
        frag, found = validate(tree, path)
        problems += found
        if frag is not None:
            frags.append(frag)
    ids = {i.lower(): i for i in frozen_ids(tree.get(FEATURE_MAP, ''))}
    replaced = {}
    for frag in frags:
        if frag.kind != 'feature-map':
            continue
        if frag.key.lower() in ids:
            problems.append(f'{frag.path}: the id `{frag.key}` duplicates `{ids[frag.key.lower()]}` of the map')
        target = frag.meta.get('Replaces')
        if target:
            if target in replaced:
                problems.append(f'{frag.path}: Replaces `{target}`, which {replaced[target]} already replaces; '
                                f'one fragment per old row')
            else:
                replaced[target] = frag.path
    return frags, problems


def changes(base, head):
    """(status, path) of every file that differs between two trees, like `git diff --name-status --no-renames`."""
    out = []
    for path in sorted(set(base) | set(head)):
        if path not in base:
            out.append(('A', path))
        elif path not in head:
            out.append(('D', path))
        elif base[path] != head[path]:
            out.append(('M', path))
    return out


def judge(base, head, changed, pr, merged=frozenset()):
    """Problems of PR #pr whose merge-base tree is `base` and head tree is `head` (path -> text).

    `merged`: numbers of the PRs whose merge commits this PR carries (merge base..head); their fragments count as
    this PR's own, so an integration PR (lane/face -> main) is not blamed for the lanes it integrates."""
    problems = []
    for path in FROZEN_FILES:
        if path not in head:
            problems.append(f'{path} is missing; it holds a frozen table and must stay')
            continue
        now = frozen_rows(path, head[path])
        if now is None:
            problems.append(f'{path}: the heading that marks its frozen table is gone, so the check cannot tell what '
                            f'is frozen; put the heading back')
            continue
        if path in base:
            before = frozen_rows(path, base[path])
            if before is not None and before != now:
                old, new = set(before), set(now)
                added = [r for r in now if r not in old]
                removed = [r for r in before if r not in new]
                where = {HANDOFF: 'docs/handoff/<pr>.md', DECISIONS: 'docs/decisions/<pr>-<slug>.md',
                         FEATURE_MAP: 'docs/feature-map/<named-id>.md'}[path]
                example = added[0] if added else (removed[0] if removed else now[0] if now else '')
                problems.append(f'{path}: this PR changes its frozen table ({len(added)} row(s) added or edited, '
                                f'{len(removed)} removed or edited, or rows moved), e.g. "{_short(example)}". The table '
                                f'is frozen history: put the row in {where} instead')
            if pointers(base[path]) and pointers(base[path]) != pointers(head[path]):
                problems.append(f'{path}: the `{POINTER}` pointer line was edited or removed; it must stay as it is')
    problems += check_tree(head)[1]
    for status, path in changed:
        kind = fragment_kind(path)
        if kind not in ('decisions', 'handoff'):
            continue
        spec = KINDS[kind]
        m = spec['name'].match(path[len(spec['dir']):])
        if not m:
            continue                                   # a bad name in the head is reported above
        owner = int(m.group(1))
        if owner == pr or owner in merged:
            continue
        if status == 'A':
            problems.append(f'{path} is a new {kind} fragment named for PR #{owner}, but this is PR #{pr}; a PR adds '
                            f'only its own fragments ({spec["dir"]}{pr}{"-<slug>" if kind == "decisions" else ""}.md)')
        elif status == 'D':
            problems.append(f'{path} belongs to PR #{owner}; this PR (#{pr}) may not delete it')
        elif status == 'M':
            before = parse_fragment(kind, path, base[path])[1]
            after = parse_fragment(kind, path, head[path])[1]
            idx = spec['status']
            strip = lambda rows: [r[:idx] + r[idx + 1:] for r in rows]
            if len(before) != len(after) or strip(before) != strip(after):
                problems.append(f'{path} belongs to PR #{owner}; this PR (#{pr}) may change only its '
                                f'{spec["header"][idx]} cell, not add, remove or reword rows')
        else:
            problems.append(f'{path}: change type {status} is not allowed on another PR\'s fragment')
    return problems


# ---------------------------------------------------------------------------------------------------- assemble

def _date_key(cell):
    m = DATE.match(cell)
    return (int(m.group(3)), MONTHS.index(m.group(2)), int(m.group(1))) if m else (0, 0, 0)


def _row(c):
    return '| ' + ' | '.join(c) + ' |'


def assemble_text(tree):
    """(markdown, problems): the frozen tables with every fragment merged in. For reading only; never committed."""
    frags, problems = check_tree(tree)
    out = ['# Combined bookkeeping view (generated; never commit this file)', '',
           'Made by `python -I scripts/quality/doc_fragments.py assemble` from the frozen tables plus the fragment '
           'files. Edit the fragment files, never this view.', '']
    handoff = {int(f.key): f.rows[0] for f in frags if f.kind == 'handoff' and f.rows}
    out += ['## Pull requests (HANDOFF.md + docs/handoff/)', '', _row(KINDS['handoff']['header']),
            '| --- | --- | --- | --- |']
    frozen = [r for r in frozen_rows(HANDOFF, tree.get(HANDOFF, '')) or [] if (cells(r) or ['#'])[0] not in ('PR',)
              and not re.match(r'^\|\s*-', r)]
    used = set()
    body = []
    for r in frozen:
        link = re.match(r'^\[#([0-9]+)\]', (cells(r) or [''])[0])
        number = int(link.group(1)) if link else None
        if number in handoff:
            body.append(_row(handoff[number]))
            used.add(number)
        else:
            body.append(r)
    out += [_row(handoff[n]) for n in sorted(handoff, reverse=True) if n not in used] + body + ['']
    out += ['## Decisions (docs/DECISIONS.md + docs/decisions/)', '', _row(KINDS['decisions']['header']),
            '| --- | --- | --- | --- | --- |']
    out += [r for r in frozen_rows(DECISIONS, tree.get(DECISIONS, '')) or []
            if (cells(r) or ['Date'])[0] != 'Date' and not re.match(r'^\|\s*-', r)]
    decided = sorted(((_date_key(row[0]), int(f.key), f.path, row) for f in frags if f.kind == 'decisions'
                      for row in f.rows), key=lambda t: t[:3])
    out += [_row(t[3]) for t in decided] + ['']
    out += ['## Feature map (docs/FEATURE_MAP.md + docs/feature-map/)', '']
    features = [f for f in frags if f.kind == 'feature-map' and f.rows]
    replace = {f.meta['Replaces']: f for f in features if f.meta.get('Replaces')}
    lines = tree.get(FEATURE_MAP, '').splitlines()
    region = _region(FEATURE_MAP, lines) or (0, 0)
    section = None

    def close(name):
        added = sorted((f for f in features if f.meta.get('Section') == name and not f.meta.get('Replaces')),
                       key=lambda f: f.key)
        return [_row(f.rows[0]) for f in added]

    for line in lines[region[0] + 1:region[1]]:
        if line.startswith('### '):
            if section is not None:
                out += close(section) + ['']
            section = line[4:].strip()
            out += [line, '', _row(KINDS['feature-map']['header']), '| --- | --- | --- | --- |']
            continue
        c = cells(line)
        if section is None or not c or not FROZEN_ID.match(c[0]):
            continue
        out.append(_row(replace[c[0]].rows[0]) if c[0] in replace else line)
    if section is not None:
        out += close(section) + ['']
    return '\n'.join(out) + '\n', problems


def out_allowed(root, out):
    """A combined view may be written outside the repository or under its artifacts/ folder (git-ignored), never
    anywhere else in the repository, where it could be committed and the conflict would just move there."""
    root = os.path.normcase(os.path.realpath(root))
    target = os.path.normcase(os.path.realpath(out))
    try:
        inside = os.path.commonpath([root, target]) == root
    except ValueError:
        inside = False                                  # another drive
    if not inside:
        return True
    rel = os.path.relpath(target, root).replace('\\', '/')
    return rel.startswith('artifacts/')


# ---------------------------------------------------------------------------------------------------- git plumbing

def _git(args, cwd=None):
    out = subprocess.run(['git', '-c', 'core.quotePath=false', *args], cwd=cwd, capture_output=True, check=True)
    return out.stdout


def git_tree(rev, cwd=None):
    """The three frozen files and every fragment file at `rev`, as path -> text."""
    names = _git(['ls-tree', '-r', '-z', '--name-only', rev, '--', *FROZEN_FILES,
                  *(d.rstrip('/') for d in FRAGMENT_DIRS)], cwd).decode('utf-8', 'replace')
    tree = {}
    for path in (p for p in names.split('\0') if p):
        tree[path] = _git(['show', f'{rev}:{path}'], cwd).decode('utf-8', 'replace')
    return tree


MERGE_SUBJECT = re.compile(r'^Merge pull request #(\d+)\b')


def merged_prs(merge_base, head, cwd=None):
    """Numbers of the PRs merged between `merge_base` and `head` (GitHub's "Merge pull request #N" subjects)."""
    subjects = _git(['log', '--merges', '--format=%s', f'{merge_base}..{head}'], cwd).decode('utf-8', 'replace')
    return frozenset(int(m.group(1)) for m in (MERGE_SUBJECT.match(line) for line in subjects.splitlines()) if m)


def run_git(base, head, pr, cwd=None, out=print):
    try:
        merge_base = _git(['merge-base', base, head], cwd).decode().strip()
        merged = merged_prs(merge_base, head, cwd)
        raw = _git(['diff', '--name-status', '--no-renames', '-z', merge_base, head], cwd).decode('utf-8', 'replace')
        parts = [p for p in raw.split('\0') if p]
        if len(parts) % 2:
            raise ValueError('odd git diff output')
        changed = [(parts[i][0], parts[i + 1]) for i in range(0, len(parts), 2)]
        before, after = git_tree(merge_base, cwd), git_tree(head, cwd)
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        return report([f'git could not read the PR\'s commits ({type(error).__name__}); failing closed'], out)
    return report(judge(before, after, changed, pr, merged), out)


def run_event(event_path, out=print, cwd=None):
    try:
        event = json.loads(pathlib.Path(event_path).read_text(encoding='utf-8'))
        pr = event['pull_request']
        number, base, head = int(pr['number']), pr['base']['sha'], pr['head']['sha']
        if not (isinstance(base, str) and isinstance(head, str) and base and head and number > 0):
            raise ValueError('empty field')
    except (OSError, ValueError, KeyError, TypeError) as error:
        return report([f'cannot read the PR number, base and head from the event ({type(error).__name__}); '
                       f'failing closed'], out)
    return run_git(base, head, number, cwd, out)


def report(problems, out=print):
    for p in problems:
        out(f'::error::{RULE} {p}')
    if problems:
        out(f'DOC_FRAGMENTS_FAILED {len(problems)} problem(s). Fix: docs/quality/RULES.md, {RULE}')
        return 1
    out('DOC_FRAGMENTS_OK')
    return 0


# ---------------------------------------------------------------------------------------------------- self-test

HANDOFF_SAMPLE = '''# Handoff

## 7. Current status

### Pull requests

> **Frozen table.** New rows go in docs/handoff/.

| PR | Branch → base | State (13 Nov) | Notes |
| --- | --- | --- | --- |
| [#5](https://github.com/o/r/pull/5) | `a` → `lane/face` | **open** | five |
| [#4](https://github.com/o/r/pull/4) | `b` → `lane/face` | merged | four |

### Known issues

| Issue | Note |
| --- | --- |
| x | y |
'''
DECISIONS_SAMPLE = '''# Decisions

> **Frozen table.** New rows go in docs/decisions/.

| Date | Decision | Why | Source | Status |
| --- | --- | --- | --- | --- |
| 1 Nov 2026 | one | why | PR #1 | in force |

A stray paragraph inside the table region.
| 2 Nov 2026 | two | why | PR #2 | in force |

## Detailed decision records

| Not | frozen |
'''
MAP_SAMPLE = '''# Feature map

## Where the proofs run

| a | b |

## The map

> **Frozen table.** New rows go in docs/feature-map/.

### Notch and presence

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| N1 | Notch wakes | manual | runbook |

### Talking and typing

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| T1 | Talk | manual | runbook |
'''
DEC_FRAG = '''| Date | Decision | Why | Source | Status |
| --- | --- | --- | --- | --- |
| 16 Nov 2026 | Fragments | conflicts | PR #160 | proposed |
'''
HO_FRAG = '''| PR | Branch → base | State | Notes |
| --- | --- | --- | --- |
| [#160](https://github.com/o/r/pull/160) | `q` → `lane/face` | **open** | notes |
'''
FM_FRAG = '''Section: Talking and typing

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| type-text-hidden-chars | Hidden characters | UNVERIFIED | not yet |
'''


def self_test():
    results = []

    def expect(name, ok):
        results.append((name, bool(ok)))

    def fails(name, problems, needle):
        expect(name, any(needle in p for p in problems))

    def passes(name, problems):
        if problems:
            print(f'  ({name}) unexpected: {problems}')
        expect(name, not problems)

    base = {HANDOFF: HANDOFF_SAMPLE, DECISIONS: DECISIONS_SAMPLE, FEATURE_MAP: MAP_SAMPLE,
            'docs/handoff/150.md': HO_FRAG.replace('160', '150'),
            'docs/decisions/150-older.md': DEC_FRAG.replace('160', '150'),
            'docs/feature-map/older-feature.md': FM_FRAG.replace('type-text-hidden-chars', 'older-feature')}

    def pr(changes_, number=160, start=None):
        head = dict(start or base)
        for path, text in changes_.items():
            if text is None:
                head.pop(path, None)
            else:
                head[path] = text
        return judge(start or base, head, changes(start or base, head), number)

    own = {'docs/decisions/160-doc-fragments.md': DEC_FRAG, 'docs/handoff/160.md': HO_FRAG,
           'docs/feature-map/type-text-hidden-chars.md': FM_FRAG}
    passes('a PR adding its own decision, handoff and feature-map fragments passes', pr(own))
    passes('a PR that touches nothing passes', pr({}))
    passes('prose outside the frozen tables may change', pr({
        HANDOFF: HANDOFF_SAMPLE.replace('## 7. Current status', '## 7. Current status\n\nNew prose.'),
        DECISIONS: DECISIONS_SAMPLE + '\nMore detail.\n',
        FEATURE_MAP: MAP_SAMPLE.replace('| a | b |', '| a | b |\n| c | d |')}))

    # 1. frozen tables
    fails('a row appended to the frozen DECISIONS table fails, naming the file', pr({DECISIONS: DECISIONS_SAMPLE.replace(
        '| 2 Nov 2026 | two | why | PR #2 | in force |\n',
        '| 2 Nov 2026 | two | why | PR #2 | in force |\n| 16 Nov 2026 | new | why | PR #160 | proposed |\n')}),
        'docs/DECISIONS.md: this PR changes its frozen table')
    fails('a frozen DECISIONS row edited (status) fails', pr({DECISIONS: DECISIONS_SAMPLE.replace(
        'PR #1 | in force', 'PR #1 | superseded')}), 'docs/DECISIONS.md')
    fails('a frozen DECISIONS row deleted fails', pr({DECISIONS: DECISIONS_SAMPLE.replace(
        '| 1 Nov 2026 | one | why | PR #1 | in force |\n', '')}), 'docs/DECISIONS.md')
    fails('a row added to the frozen HANDOFF PR table fails', pr({HANDOFF: HANDOFF_SAMPLE.replace(
        '| four |\n', '| four |\n| [#160](https://github.com/o/r/pull/160) | `q` → `lane/face` | open | n |\n')}),
        'HANDOFF.md: this PR changes its frozen table')
    fails('a frozen HANDOFF row edited (open -> merged) fails', pr({HANDOFF: HANDOFF_SAMPLE.replace(
        '**open** | five', 'merged | five')}), 'HANDOFF.md')
    fails('frozen HANDOFF rows reordered fail', pr({HANDOFF: HANDOFF_SAMPLE.replace(
        '| [#5](https://github.com/o/r/pull/5) | `a` → `lane/face` | **open** | five |\n'
        '| [#4](https://github.com/o/r/pull/4) | `b` → `lane/face` | merged | four |',
        '| [#4](https://github.com/o/r/pull/4) | `b` → `lane/face` | merged | four |\n'
        '| [#5](https://github.com/o/r/pull/5) | `a` → `lane/face` | **open** | five |')}), 'HANDOFF.md')
    passes('a row in another HANDOFF table (Known issues) is not frozen', pr({HANDOFF: HANDOFF_SAMPLE + '| z | w |\n'}))
    fails('a counter-id row (T2) added to the frozen FEATURE_MAP fails', pr({FEATURE_MAP: MAP_SAMPLE.replace(
        '| T1 | Talk | manual | runbook |', '| T1 | Talk | manual | runbook |\n| T2 | New | manual | runbook |')}),
        'docs/FEATURE_MAP.md: this PR changes its frozen table')
    fails('a frozen FEATURE_MAP row edited fails', pr({FEATURE_MAP: MAP_SAMPLE.replace('| N1 | Notch wakes | manual',
                                                                                    '| N1 | Notch wakes | UNVERIFIED')}),
          'docs/FEATURE_MAP.md')
    fails('renaming `## The map` (hiding the frozen region) fails closed', pr({FEATURE_MAP: MAP_SAMPLE.replace(
        '## The map', '## Map')}), 'heading that marks its frozen table is gone')
    fails('removing `### Pull requests` fails closed', pr({HANDOFF: HANDOFF_SAMPLE.replace('### Pull requests',
                                                                                        '### PRs')}), 'HANDOFF.md')
    fails('removing `## Detailed decision records` fails closed', pr({DECISIONS: DECISIONS_SAMPLE.replace(
        '## Detailed decision records', '## Details')}), 'docs/DECISIONS.md')
    fails('a frozen file deleted fails', pr({DECISIONS: None}), 'docs/DECISIONS.md is missing')
    fails('an edited frozen-table pointer line fails', pr({DECISIONS: DECISIONS_SAMPLE.replace(
        'New rows go in docs/decisions/.', 'Rows may go anywhere.')}), 'pointer line was edited or removed')
    fails('a removed frozen-table pointer line fails', pr({FEATURE_MAP: MAP_SAMPLE.replace(
        '> **Frozen table.** New rows go in docs/feature-map/.\n', '')}), 'pointer line')
    passes('a PR that first adds the pointer lines passes (no pointer in its base)', pr(
        {DECISIONS: DECISIONS_SAMPLE}, start={**base, DECISIONS: DECISIONS_SAMPLE.replace(
            '> **Frozen table.** New rows go in docs/decisions/.\n', '')}))

    # 2. malformed fragments
    fails('a decisions fragment with a missing column fails', pr({'docs/decisions/160-x.md': DEC_FRAG.replace(
        ' | PR #160 | proposed |', ' | proposed |')}), 'a row has 4 cells; expected 5')
    fails('a decisions fragment with an extra column fails', pr({'docs/decisions/160-x.md': DEC_FRAG.replace(
        ' proposed |', ' proposed | extra |')}), 'a row has 6 cells')
    fails('a decisions fragment with an empty cell fails', pr({'docs/decisions/160-x.md': DEC_FRAG.replace(
        '| conflicts |', '|  |')}), 'empty cell')
    fails('a decisions fragment with a wrong header fails', pr({'docs/decisions/160-x.md': DEC_FRAG.replace(
        '| Source |', '| Evidence |', 1)}), 'the header must be exactly')
    fails('a decisions fragment with no separator line fails', pr({'docs/decisions/160-x.md': DEC_FRAG.replace(
        '| --- | --- | --- | --- | --- |\n', '')}), 'needs a table')
    fails('a decisions fragment with text after the table fails', pr({'docs/decisions/160-x.md': DEC_FRAG + 'Note.\n'}),
          'is not a table row')
    fails('a decisions fragment with text before the table fails', pr({'docs/decisions/160-x.md': 'Hi\n' + DEC_FRAG}),
          'is not allowed')
    fails('a decisions fragment with a bad date fails', pr({'docs/decisions/160-x.md': DEC_FRAG.replace(
        '16 Nov 2026', '2026-11-16')}), 'Date cell')
    passes('a decisions fragment may hold two rows', pr({'docs/decisions/160-x.md': DEC_FRAG + DEC_FRAG.splitlines()[2]}))
    for bad in ('docs/decisions/160.md', 'docs/decisions/160-Upper.md', 'docs/decisions/x-160.md',
                'docs/decisions/160-a_b.md', 'docs/decisions/160-x.txt', 'docs/decisions/sub/160-x.md',
                'docs/decisions/0160-x.md', 'docs/decisions/160-' + 'a' * 80 + '.md'):
        fails(f'a decisions fragment named {bad!r} fails', pr({bad: DEC_FRAG}), 'bad file name')
    for bad in ('docs/handoff/160-x.md', 'docs/handoff/pr160.md', 'docs/handoff/160.MD', 'docs/handoff/README.md'):
        fails(f'a handoff fragment named {bad!r} fails', pr({bad: HO_FRAG}), 'bad file name')
    fails('a handoff fragment whose PR link is another number fails', pr({'docs/handoff/160.md': HO_FRAG.replace(
        'pull/160', 'pull/161')}), 'the PR cell must be')
    fails('a handoff fragment whose PR cell is plain text fails', pr({'docs/handoff/160.md': HO_FRAG.replace(
        '[#160](https://github.com/o/r/pull/160)', '#160')}), 'the PR cell must be')
    fails('a handoff fragment with two rows fails', pr({'docs/handoff/160.md': HO_FRAG + HO_FRAG.splitlines()[2]}),
          'exactly one row')
    fails('a handoff fragment with the old dated State header fails', pr({'docs/handoff/160.md': HO_FRAG.replace(
        '| State |', '| State (13 Nov) |')}), 'the header must be exactly')
    for bad in ('docs/feature-map/A9.md', 'docs/feature-map/a9.md', 'docs/feature-map/typetext.md',
                'docs/feature-map/Type-Text.md', 'docs/feature-map/type_text.md', 'docs/feature-map/-type-text.md'):
        fails(f'a feature-map fragment named {bad!r} (counter-like or not a named id) fails', pr({bad: FM_FRAG}),
              'bad file name')
    fails('a feature-map fragment whose id cell is not its file name fails', pr(
        {'docs/feature-map/hidden-chars.md': FM_FRAG}), 'the id cell must be the file name')
    fails('a feature-map fragment whose id cell is a counter fails', pr({'docs/feature-map/type-text-hidden-chars.md':
                                                                         FM_FRAG.replace('| type-text-hidden-chars |',
                                                                                         '| A9 |')}),
          'the id cell must be the file name')
    fails('a feature-map fragment without a Section fails', pr({'docs/feature-map/type-text-hidden-chars.md':
                                                                FM_FRAG.replace('Section: Talking and typing\n', '')}),
          'needs a `Section: <heading>` line')
    fails('a feature-map fragment with an unknown Section fails', pr({'docs/feature-map/type-text-hidden-chars.md':
                                                                     FM_FRAG.replace('Talking and typing', 'Typing')}),
          'is not a `###` heading')
    fails('a feature-map fragment with two Section lines fails', pr({'docs/feature-map/type-text-hidden-chars.md':
                                                                    'Section: Notch and presence\n' + FM_FRAG}),
          'is not allowed')
    fails('a feature-map fragment that replaces an unknown id fails', pr({'docs/feature-map/type-text-hidden-chars.md':
                                                                         'Replaces: T9\n' + FM_FRAG}),
          'which is not an id of the frozen map table')
    passes('a feature-map fragment may replace a frozen id', pr({'docs/feature-map/type-text-hidden-chars.md':
                                                                'Replaces: T1\n' + FM_FRAG}))
    fails('two feature-map fragments replacing the same id fail', pr({
        'docs/feature-map/type-text-hidden-chars.md': 'Replaces: T1\n' + FM_FRAG,
        'docs/feature-map/talk-again.md': 'Replaces: T1\n' + FM_FRAG.replace('type-text-hidden-chars', 'talk-again')}),
        'one fragment per old row')
    fails('a feature-map fragment with two rows fails', pr({'docs/feature-map/type-text-hidden-chars.md':
                                                            FM_FRAG + FM_FRAG.splitlines()[-1]}), 'exactly one row')
    fails('a malformed fragment already in the tree fails every PR (fail closed)', pr({}, start={
        **base, 'docs/handoff/151.md': 'not a table\n'}), 'docs/handoff/151.md')

    # 3. a new fragment for another PR
    fails('a new handoff fragment for another PR fails', pr({'docs/handoff/161.md': HO_FRAG.replace('160', '161')}),
          'named for PR #161, but this is PR #160')
    fails('a new decisions fragment for another PR fails', pr({'docs/decisions/161-x.md': DEC_FRAG}),
          'named for PR #161')

    passes('a new fragment for a PR this PR carries (its merge commit is in merge base..head) passes', judge(
        base, {**base, 'docs/handoff/161.md': HO_FRAG.replace('160', '161')},
        changes(base, {**base, 'docs/handoff/161.md': HO_FRAG.replace('160', '161')}), 160, merged={161}))
    carried = {k: v for k, v in base.items() if k != 'docs/handoff/150.md'}
    carried['docs/decisions/150-older.md'] = base['docs/decisions/150-older.md'].replace('Fragments', 'Reworded')
    passes('a carried PR\'s fragments may be reworded or deleted',
           judge(base, carried, changes(base, carried), 160, merged={150}))
    fails('a merged set that does not name the owner still fails', judge(
        base, {**base, 'docs/handoff/161.md': HO_FRAG.replace('160', '161')},
        changes(base, {**base, 'docs/handoff/161.md': HO_FRAG.replace('160', '161')}), 160, merged={162}),
        'named for PR #161')

    # 4. another PR's fragments
    passes('another PR\'s handoff fragment: State edit only passes', pr({'docs/handoff/150.md': base[
        'docs/handoff/150.md'].replace('**open**', '**merged 16 Nov** (`abc1234`)')}))
    passes('another PR\'s decision: Status edit only passes', pr({'docs/decisions/150-older.md': base[
        'docs/decisions/150-older.md'].replace('| proposed |', '| in force |')}))
    fails('another PR\'s handoff fragment: Notes edit fails', pr({'docs/handoff/150.md': base[
        'docs/handoff/150.md'].replace('| notes |', '| other notes |')}), 'may change only its State cell')
    fails('another PR\'s decision: Decision edit fails', pr({'docs/decisions/150-older.md': base[
        'docs/decisions/150-older.md'].replace('Fragments', 'Something else')}), 'may change only its Status cell')
    fails('another PR\'s decision: a row added fails', pr({'docs/decisions/150-older.md': base[
        'docs/decisions/150-older.md'] + DEC_FRAG.splitlines()[2]}), 'may change only its Status cell')
    fails('another PR\'s handoff fragment deleted fails', pr({'docs/handoff/150.md': None}),
          'belongs to PR #150; this PR (#160) may not delete it')
    passes('the owning PR may reword and delete its own fragments', pr(
        {'docs/handoff/150.md': None, 'docs/decisions/150-older.md': base['docs/decisions/150-older.md'].replace(
            'Fragments', 'Reworded')}, number=150))
    passes('any PR may edit or delete a feature-map fragment', pr({
        'docs/feature-map/older-feature.md': None,
        'docs/feature-map/type-text-hidden-chars.md': FM_FRAG.replace('UNVERIFIED', 'manual')}))

    # assemble
    tree = {**base, **own, 'docs/handoff/5.md': HO_FRAG.replace('160', '5').replace('**open**', '**merged**'),
            'docs/feature-map/notch-wakes-again.md': 'Section: Notch and presence\nReplaces: N1\n\n' + FM_FRAG.replace(
                'Section: Talking and typing\n\n', '').replace('type-text-hidden-chars', 'notch-wakes-again'),
            'docs/decisions/149-earlier.md': DEC_FRAG.replace('16 Nov', '15 Nov').replace('Fragments', 'Earlier')}
    text, problems = assemble_text(tree)
    passes('assemble: a valid tree has no problems', problems)
    lines = text.splitlines()
    expect('assemble: a handoff fragment replaces its PR\'s frozen row in place',
           '| [#5](https://github.com/o/r/pull/5) | `q` → `lane/face` | **merged** | notes |' in lines and
           not any('| five |' in l for l in lines))
    def at(line):
        return lines.index(line) if line in lines else None

    first = at(_row(cells(HO_FRAG.splitlines()[2])))
    second = at(_row(cells(base['docs/handoff/150.md'].splitlines()[2])))
    expect('assemble: new handoff fragments come first, newest PR first',
           first is not None and second is not None and first < second)
    expect('assemble: decisions keep the frozen rows, then fragments by date',
           [l.split('|')[2].strip() for l in lines if re.match(r'^\| [0-9]{1,2} Nov 2026', l)] ==
           ['one', 'two', 'Earlier', 'Fragments', 'Fragments'])
    expect('assemble: a feature fragment with Replaces takes the old row\'s place',
           any(l.startswith('| notch-wakes-again |') for l in lines) and not any(l.startswith('| N1 |') for l in lines))
    expect('assemble: a new feature fragment lands at the end of its Section',
           at('| T1 | Talk | manual | runbook |') is not None and at('| T1 | Talk | manual | runbook |') < next(
               (i for i, l in enumerate(lines) if l.startswith('| type-text-hidden-chars |')), -1))
    expect('assemble: the frozen non-map table is not copied', not any(l == '| Not | frozen |' for l in lines))
    fails('assemble reports a malformed fragment', assemble_text({**tree, 'docs/handoff/7.md': 'x\n'})[1],
          'docs/handoff/7.md')

    # --out guard
    root = tempfile.mkdtemp(prefix='frag-root-')
    expect('--out under artifacts/ is allowed', out_allowed(root, os.path.join(root, 'artifacts', 'view.md')))
    expect('--out outside the repository is allowed', out_allowed(root, os.path.join(tempfile.gettempdir(), 'v.md')))
    expect('--out at docs/ inside the repository is refused', not out_allowed(root, os.path.join(root, 'docs', 'v.md')))
    expect('--out at the repository root is refused', not out_allowed(root, os.path.join(root, 'VIEW.md')))
    expect('--out escaping artifacts/ with .. is refused',
           not out_allowed(root, os.path.join(root, 'artifacts', '..', 'HANDOFF.md')))
    expect('--out named like artifacts but not under it is refused',
           not out_allowed(root, os.path.join(root, 'artifacts-x', 'v.md')))

    # event and git (a real throwaway repository, isolated from the user's git config)
    sink = []
    expect('an event with no pull_request fails closed', run_event(_write_temp('{}'), sink.append) == 1)
    expect('an unreadable event file fails closed', run_event(os.path.join(root, 'none.json'), sink.append) == 1)
    expect('an event with an empty head SHA fails closed', run_event(_write_temp(json.dumps(
        {'pull_request': {'number': 1, 'base': {'sha': 'x'}, 'head': {'sha': ''}}})), sink.append) == 1)
    repo = os.path.join(root, 'repo')
    try:
        _git_fixture(repo, base, own)
        sink = []
        expect('git: a clean PR (own fragments) passes', run_git('main', 'clean', 160, repo, sink.append) == 0)
        sink = []
        expect('git: a PR appending a frozen DECISIONS row fails and names the file',
               run_git('main', 'dirty', 160, repo, sink.append) == 1 and
               any('docs/DECISIONS.md' in s for s in sink))
        sink = []
        expect('git: a PR judged with the wrong number fails on its own fragments',
               run_git('main', 'clean', 999, repo, sink.append) == 1)
        sink = []
        expect('git: an unknown base commit fails closed', run_git('no-such-ref', 'clean', 160, repo, sink.append) == 1
               and any('failing closed' in s for s in sink))
        sink = []
        expect('git: an integration PR carrying "Merge pull request #170" passes on PR #170\'s fragment',
               run_git('main', 'integration', 3, repo, sink.append) == 0)
        sink = []
        expect('git: the same fragment brought by a plain commit (no merge of PR #170) fails',
               run_git('main', 'plain', 3, repo, sink.append) == 1 and any('named for PR #170' in s for s in sink))
        sink = []
        expect('git: a merge subject naming another number does not cover PR #170\'s fragment',
               run_git('main', 'wrong-merge', 3, repo, sink.append) == 1 and
               any('named for PR #170' in s for s in sink))
    except (OSError, subprocess.CalledProcessError) as error:
        expect(f'git fixture could be built ({type(error).__name__})', False)

    failed = [n for n, ok in results if not ok]
    for n in failed:
        print(f'  FAILED: {n}')
    print(f'DOC_FRAGMENTS_SELF_TEST_{"FAILED" if failed else "PASSED"} {len(results) - len(failed)} of {len(results)}')
    return 1 if failed else 0


def _write_temp(text):
    handle, path = tempfile.mkstemp(prefix='frag-event-', suffix='.json')
    with os.fdopen(handle, 'w', encoding='utf-8') as f:
        f.write(text)
    return path


def _git_fixture(repo, base, own):
    env = {**os.environ, 'GIT_CONFIG_GLOBAL': os.devnull, 'GIT_CONFIG_NOSYSTEM': '1',
           'GIT_AUTHOR_NAME': 'self-test', 'GIT_AUTHOR_EMAIL': 'self-test@example.invalid',
           'GIT_COMMITTER_NAME': 'self-test', 'GIT_COMMITTER_EMAIL': 'self-test@example.invalid'}

    def git(*args):
        subprocess.run(['git', *args], cwd=repo, env=env, capture_output=True, check=True)

    def write(files):
        for path, text in files.items():
            full = os.path.join(repo, *path.split('/'))
            os.makedirs(os.path.dirname(full), exist_ok=True)
            with open(full, 'w', encoding='utf-8', newline='\n') as f:
                f.write(text)

    os.makedirs(repo)
    git('init', '-q', '-b', 'main')
    write(base)
    git('add', '-A')
    git('commit', '-q', '-m', 'base')
    git('checkout', '-q', '-b', 'clean')
    write(own)
    git('add', '-A')
    git('commit', '-q', '-m', 'own fragments')
    git('checkout', '-q', '-b', 'dirty')
    write({DECISIONS: base[DECISIONS].replace('PR #2 | in force |\n',
                                              'PR #2 | in force |\n| 16 Nov 2026 | n | w | s | proposed |\n')})
    git('commit', '-q', '-am', 'append a frozen row')
    # PR #170's own branch, merged into an integration branch with GitHub's merge subject; the same file arriving by
    # a plain commit, or under a merge subject naming another PR, is not covered.
    git('checkout', '-q', 'main')
    git('checkout', '-q', '-b', 'pr-170')
    write({'docs/handoff/170.md': HO_FRAG.replace('160', '170')})
    git('add', '-A')
    git('commit', '-q', '-m', 'PR 170 fragment')
    git('checkout', '-q', 'main')
    git('checkout', '-q', '-b', 'integration')
    git('merge', '-q', '--no-ff', '-m', 'Merge pull request #170 from o/pr-170', 'pr-170')
    git('checkout', '-q', 'main')
    git('checkout', '-q', '-b', 'plain')
    write({'docs/handoff/170.md': HO_FRAG.replace('160', '170')})
    git('add', '-A')
    git('commit', '-q', '-m', 'bring PR 170 fragment by hand')
    git('checkout', '-q', 'main')
    git('checkout', '-q', '-b', 'wrong-merge')
    git('merge', '-q', '--no-ff', '-m', 'Merge pull request #171 from o/pr-170', 'pr-170')


# ---------------------------------------------------------------------------------------------------- main

def _repo_root():
    return _git(['rev-parse', '--show-toplevel']).decode('utf-8').strip()


def working_tree(root):
    tree = {}
    for path in FROZEN_FILES:
        full = os.path.join(root, *path.split('/'))
        if os.path.isfile(full):
            tree[path] = pathlib.Path(full).read_text(encoding='utf-8')
    for folder in FRAGMENT_DIRS:
        base = os.path.join(root, *folder.rstrip('/').split('/'))
        for dirpath, _, files in os.walk(base):
            for name in files:
                full = os.path.join(dirpath, name)
                rel = os.path.relpath(full, root).replace('\\', '/')
                tree[rel] = pathlib.Path(full).read_text(encoding='utf-8', errors='replace')
    return tree


def main(argv):
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    if argv == ['--self-test']:
        return self_test()
    if len(argv) == 3 and argv[:2] == ['check', '--event']:
        return run_event(argv[2])
    if len(argv) == 5 and argv[0] == 'check' and argv[1] == '--base' and argv[3] == '--pr' and argv[4].isdigit():
        return run_git(argv[2], 'HEAD', int(argv[4]))
    if argv[:1] == ['assemble'] and (len(argv) == 1 or (len(argv) == 3 and argv[1] == '--out')):
        try:
            root = _repo_root()
        except (OSError, subprocess.CalledProcessError):
            print(f'::error::{RULE} assemble runs inside the repository (git rev-parse failed)')
            return 1
        text, problems = assemble_text(working_tree(root))
        for p in problems:
            print(f'::warning::{RULE} {p}', file=sys.stderr)
        if len(argv) == 3:
            if not out_allowed(root, argv[2]):
                print(f'::error::{RULE} --out must be outside the repository or under artifacts/ (the combined view '
                      f'is never committed, or the merge conflict would just move there)')
                return 2
            pathlib.Path(argv[2]).parent.mkdir(parents=True, exist_ok=True)
            pathlib.Path(argv[2]).write_text(text, encoding='utf-8')
            print(f'wrote the combined view to {argv[2]}')
        else:
            sys.stdout.write(text)
        return 1 if problems else 0
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
