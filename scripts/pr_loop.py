"""BOO-LOOP-001: a deterministic PR loop (no AI). Puts every open PR in exactly one state, then takes at most one
allowed action per PR. Rules, states and the never-list: docs/quality/pr-loop.md.

  python -I scripts/pr_loop.py --self-test           every rule on planted fixtures (offline, no network)
  python -I scripts/pr_loop.py                       read GitHub, print the plan, do NOTHING (dry run is the default)
  python -I scripts/pr_loop.py --apply               read GitHub, print the plan, then take the planned actions
  options: --repo OWNER/NAME (default vikas53953/bluey), --base BRANCH (default lane/face),
           --bot-login LOGIN (whose marker comment is the ledger; default github-actions[bot])

States (first unmet gate wins, so each PR gets exactly one):
  blocked         out of scope (draft, other base, fork), mergeability not computed yet, or the verifier refused the
                  head (latest `codex-verified` status on it is failure/error)
  needs-rebase    behind the base branch, or conflicting
  ci-red          a check run or commit status on the head failed
  waiting-QA      checks still running / a required check not reported yet, or no trusted `QA PASS at <full head>`
  waiting-review  no trusted `CODEX REVIEW PASS — <full head>` comment
  waiting-stamp   no `codex-verified` SUCCESS status on the head that a trusted `CODEX VERIFIED <full head>` comment
                  names by its ID (`Returned status ID **<id>**`, context `codex-verified`)
  stamped-ready   all of the above met

Allowed actions (at most one per PR per run):
  arm       `gh pr merge <n> --auto --merge --match-head-commit <head>`, only in stamped-ready, only when the repository
            allows auto-merge and auto-merge is not already armed
  comment   one marker comment per PR `pr-loop: state=<s> head=<sha> next=<who>`, created once, then edited in place;
            the same state and head means no edit
Branch updates are NOT an action (option (i), docs/quality/pr-loop.md): a push made with the workflow's GITHUB_TOKEN
does not start CI, so the orchestrator/lane keeps branch syncing.

Idempotency ledger = GitHub's own state, keyed by (PR, head, action): `arm` is skipped when auto-merge is already armed;
`comment` is skipped when the marker comment already says this state and head. No file to lose or forge.

Never: PR #3 (lane/face -> main) is skipped before any read and refused again by the executor; no tags, releases,
installer or signing; no merge without the stamp; no branch push; never prints a token. Any API error while reading
fails the whole run closed (no action at all, exit 1). Stdlib only, safe under `python -I`.
"""
import importlib.util
import json
import os
import re
import subprocess
import sys

RULE = 'BOO-LOOP-001'
REPO = 'vikas53953/bluey'
BASE = 'lane/face'
ALLOWED_BASES = (BASE,)  # an allowlist of one: a PR on any other base (main included) is never acted on
BOT_LOGIN = 'github-actions[bot]'
EXCLUDED = {3: 'PR #3 (lane/face -> main) is never touched by the loop'}
# The required checks of lane/face's branch protection on 16 Nov 2026 (codex-verified is judged separately).
REQUIRED_CHECKS = ('demos', 'build-test-publish', 'live-ai-smoke', 'evidence', 'package', 'preflight', 'quality',
                   'smoke')
STAMP_CONTEXT = 'codex-verified'
TRUSTED = {'OWNER', 'MEMBER', 'COLLABORATOR'}
MARKER = '<!-- pr-loop:v1 -->'
STATES = ('blocked', 'needs-rebase', 'ci-red', 'waiting-QA', 'waiting-review', 'waiting-stamp', 'stamped-ready')
FAIL_CONCLUSIONS = {'failure', 'cancelled', 'timed_out', 'action_required', 'startup_failure', 'stale'}
# A REQUIRED check (REQUIRED_CHECKS) is green only on 'success'. Any other check (e.g. github-release, which skips
# by design) may also be skipped or neutral.
OPTIONAL_OK_CONCLUSIONS = {'success', 'skipped', 'neutral'}
SHA = re.compile(r'^[0-9a-f]{40}$')
QA_PASS = re.compile(r'^QA PASS at `?([0-9a-f]{40})`?(?![0-9a-f])')
REVIEW_PASS = re.compile(r'^CODEX REVIEW PASS\s*[—–-]+\s*`?([0-9a-f]{40})`?(?![0-9a-f])')
VERIFIED = re.compile(r'^CODEX VERIFIED `?([0-9a-f]{40})`?(?![0-9a-f])')
STATUS_ID = re.compile(r'Returned status ID \*\*(\d+)\*\*, context `codex-verified`')
# A comment newer than the stamp comment (created or edited later) holding one of these whole, case-sensitive
# phrases vetoes the arm: `BLOCK` (so `BLOCKED` and `blocked` do not), `NOT VERIFIED`, `SURVIVED`.
VETO = re.compile(r'(?<![A-Za-z0-9_])(BLOCK|NOT VERIFIED|SURVIVED)(?![A-Za-z0-9_])')
# After a merge made by a bot (auto-merge armed with the workflow token), GitHub starts no push workflows on the base,
# so the loop dispatches these on the base once (they all have a workflow_dispatch trigger).
TIP_WORKFLOWS = ('windows-ci.yml', 'perf-guards.yml')
LEDGER_LINE = re.compile(r'^pr-loop: state=(\S+) head=([0-9a-f]{40}) next=(\S+)$', re.M)


class ApiError(Exception):
    """Any failure talking to GitHub. The run fails closed: no action, exit 1."""


# Log safety. gh prints request headers (Authorization) when debug/verbose is on, so the environment handed to gh
# never carries a debug or trace switch, a verbose flag on a gh call is refused, and gh's error text is redacted
# line by line before it reaches any output. The loop never prints the environment.
QUIET_ENV_DROP = ('GH_DEBUG', 'GIT_CURL_VERBOSE', 'DEBUG', 'GH_VERBOSE')
REFUSED_FLAGS = ('--verbose', '--debug')
SENSITIVE_LINE = re.compile(
    r'(?i)authori[sz]ation|bearer|token|password|secret|cookie|api[\s_-]*key|private[\s_-]*key|access[\s_-]*key|'
    r'(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{8,}|github_pat_[A-Za-z0-9_]{8,}|sk-[A-Za-z0-9_-]{12,}|'
    r'AIza[0-9A-Za-z_-]{16,}|A[KS]IA[0-9A-Z]{12,}|xox[abeoprs]-|-----BEGIN[A-Z ]*PRIVATE KEY|'
    r'eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}|hf_[A-Za-z0-9]{16,}|tskey-')


# ONE redaction for ALL output (stdout, stderr, marker comment payloads, exception text, dry-run plans). Two kinds:
#  1. every key shape that scripts/quality/key_scan.py knows (its SHAPES list, loaded from that file: one list, no
#     fork), replaced wherever it appears;
#  2. a sensitive label (API key / api-key / API_KEY, Authorization, Bearer, token, secret, password, cookie,
#     Set-Cookie, private key, access key; any case, spaces/hyphens/underscores) followed by `:` / `=` / a space:
#     the rest of that line is the value and is replaced, the label stays as context.
# The script writes output ONLY through emit() (and the comment payloads through safe()); the startup wrapper around
# sys.stdout/sys.stderr redacts again (defence in depth). The workflow's final key scan of the captured log is a
# detection backstop only: the text has already been streamed by then.
REDACTED = '[redacted]'
SENSITIVE_LABEL = re.compile(
    r'(?im)\b(api[\s_-]*key|authori[sz]ation|bearer|token|secret|password|passwd|set[\s_-]*cookie|cookie|'
    r'private[\s_-]*key|access[\s_-]*key)\b(\s*[:=]\s*|\s+)(?=\S)[^\n]*')
# A whole private-key block: from its BEGIN line to its END line across lines, or, with no END line, to the end of
# the text (fail closed). key_scan's own shape matches only the BEGIN delimiter, which is enough for a scanner that
# fails a file, but not for redaction, which must remove the key material too.
PEM_BLOCK = re.compile(r'-----BEGIN[^\n]*?PRIVATE KEY[^\n]*?-----.*?(?:-----END[^\n]*?PRIVATE KEY[^\n]*?-----|\Z)',
                       re.S)
_SHAPES = []


def _key_scan_shapes():
    """[(label, compiled pattern)] from scripts/quality/key_scan.py itself. Raises if it cannot be loaded."""
    if not _SHAPES:
        path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'quality', 'key_scan.py')
        spec = importlib.util.spec_from_file_location('_boo_key_scan_shapes', path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        _SHAPES.extend((label, re.compile(rx.decode('ascii'))) for label, rx in module.SHAPES)
    return list(_SHAPES)


def safe(text):
    """The redaction choke point. Fails closed: if the patterns cannot be loaded, nothing of the text is shown."""
    text = str(text)
    try:
        shapes = _key_scan_shapes()
    except Exception:  # noqa: BLE001 - any trouble loading the patterns withholds the text
        return '[output withheld: the key-scan patterns could not be loaded]'
    text = PEM_BLOCK.sub(REDACTED, text)
    for _, rx in shapes:
        text = rx.sub(REDACTED, text)
    return SENSITIVE_LABEL.sub(lambda m: f'{m.group(1)}{m.group(2)}{REDACTED}', text)


def emit(text='', err=False):
    """The only place the script writes to stdout or stderr: always redacted."""
    stream = sys.stderr if err else sys.stdout
    stream.write(safe(text) + '\n')


class RedactingStream:
    """Wraps sys.stdout / sys.stderr at startup so even a stray write is redacted (defence in depth)."""

    def __init__(self, stream):
        self.stream = stream

    def write(self, text):
        return self.stream.write(safe(text))

    def feed(self, text):
        return self.write(text)

    def flush(self):
        return self.stream.flush()

    def __getattr__(self, name):
        return getattr(self.stream, name)


class _ListSink:
    """A stand-in stream for the self-test."""

    def __init__(self):
        self.text = ''

    def write(self, text):
        self.text += text
        return len(text)

    def flush(self):
        return None


def _install_redacting_streams():
    sys.stdout = RedactingStream(sys.stdout)
    sys.stderr = RedactingStream(sys.stderr)


def gh_env(parent=None):
    """A copy of the environment without any debug / trace / verbose switch (GH_DEBUG, GIT_TRACE*, ...)."""
    env = dict(os.environ if parent is None else parent)
    for name in list(env):
        if name in QUIET_ENV_DROP or name.startswith('GIT_TRACE'):
            del env[name]
    return env


def redact(text):
    """gh error text with every line that could hold a header, token or key-shaped value removed."""
    kept, dropped = [], 0
    for line in str(text or '').splitlines():
        if SENSITIVE_LINE.search(line):
            dropped += 1
        else:
            kept.append(line.strip())
    note = f' [{dropped} line(s) removed: could hold a credential]' if dropped else ''
    return (' | '.join(k for k in kept if k)[:300] + note).strip()


class GhClient:
    """GitHub through the `gh` CLI and the token it already has (GH_TOKEN in Actions). Never prints the token.
    `runner` stands in for subprocess.run in the self-test."""

    def __init__(self, repo, runner=None):
        self.repo = repo
        self.runner = runner or subprocess.run

    def _gh(self, args, stdin=None):
        if any(a in REFUSED_FLAGS or a.startswith(tuple(f + '=' for f in REFUSED_FLAGS)) for a in args):
            raise ApiError('refused: a verbose/debug flag on a gh call (it would print request headers)')
        try:
            proc = self.runner(['gh', *args], input=stdin, capture_output=True, text=True, encoding='utf-8',
                               timeout=120, env=gh_env())
        except (OSError, subprocess.SubprocessError) as error:
            raise ApiError(f'gh could not run ({type(error).__name__})') from None
        if proc.returncode != 0:
            raise ApiError(f'gh {args[0]} failed: {redact(safe(proc.stderr))}')
        return proc.stdout

    def _json(self, args, stdin=None):
        out = self._gh(args, stdin)
        try:
            return json.loads(out) if out.strip() else None
        except ValueError:
            raise ApiError(f'gh {args[0]} returned text that is not JSON') from None

    def _pages(self, path):
        pages = self._json(['api', '--paginate', '--slurp', path])
        if not isinstance(pages, list):
            raise ApiError(f'unexpected paginated reply for {path}')
        return pages

    def repo_settings(self):
        """allow_auto_merge: True / False, or None when this token may not see it (REST omits the field for a
        read-only token). None never arms; the run says so."""
        data = self._json(['api', f'repos/{self.repo}'])
        if not isinstance(data, dict):
            raise ApiError('the repository reply is not an object')
        if isinstance(data.get('allow_auto_merge'), bool):
            return {'allow_auto_merge': data['allow_auto_merge'], 'source': 'REST'}
        owner, _, name = self.repo.partition('/')
        query = 'query($o:String!,$n:String!){repository(owner:$o,name:$n){autoMergeAllowed}}'
        reply = self._json(['api', 'graphql', '-f', f'query={query}', '-F', f'o={owner}', '-F', f'n={name}'])
        value = (((reply or {}).get('data') or {}).get('repository') or {}).get('autoMergeAllowed')
        return {'allow_auto_merge': value if isinstance(value, bool) else None, 'source': 'GraphQL'}

    def base_sha(self, base):
        data = self._json(['api', f'repos/{self.repo}/commits/{base.replace("/", "%2F")}', '--jq', '{sha: .sha}'])
        sha = (data or {}).get('sha', '')
        if not SHA.match(sha):
            raise ApiError(f'could not read the head of {base}')
        return sha

    def open_prs(self):
        fields = ('number,headRefOid,headRefName,baseRefName,isDraft,isCrossRepository,mergeable,mergeStateStatus,'
                  'autoMergeRequest')
        data = self._json(['pr', 'list', '--repo', self.repo, '--state', 'open', '--limit', '200', '--json', fields])
        if not isinstance(data, list):
            raise ApiError('the open-PR list is not a list')
        if len(data) >= 200:
            raise ApiError('200 or more open PRs: the list may be cut short, refusing to plan on part of it')
        return data

    def pr(self, number):
        fields = ('number,state,headRefOid,headRefName,baseRefName,isDraft,isCrossRepository,mergeable,'
                  'mergeStateStatus,autoMergeRequest')
        data = self._json(['pr', 'view', str(number), '--repo', self.repo, '--json', fields])
        if not isinstance(data, dict):
            raise ApiError(f'the fresh read of PR #{number} is not an object')
        return data

    def merged_prs(self, base):
        data = self._json(['pr', 'list', '--repo', self.repo, '--state', 'merged', '--base', base, '--limit', '20',
                           '--json', 'number,mergeCommit,mergedBy,mergedAt'])
        if not isinstance(data, list):
            raise ApiError('the merged-PR list is not a list')
        return data

    def workflow_runs(self, workflow, sha):
        data = self._json(['api', f'repos/{self.repo}/actions/workflows/{workflow}/runs?head_sha={sha}&per_page=1',
                           '--jq', '{n: .total_count}'])
        if not isinstance((data or {}).get('n'), int):
            raise ApiError(f'could not count the runs of {workflow}')
        return data['n']

    def dispatch(self, workflow, ref):
        self._gh(['workflow', 'run', workflow, '--repo', self.repo, '--ref', ref])

    def comments(self, number):
        return [c for page in self._pages(f'repos/{self.repo}/issues/{number}/comments?per_page=100') for c in page]

    def reviews(self, number):
        """PR reviews with their real edit time. REST reviews carry no edit time, so they are read through GraphQL
        (lastEditedAt is null when a review was never edited). More than one page, an error or a missing field:
        ApiError (fail closed)."""
        owner, _, name = self.repo.partition('/')
        query = ('query($o:String!,$n:String!,$pr:Int!){repository(owner:$o,name:$n){pullRequest(number:$pr){'
                 'reviews(first:100){pageInfo{hasNextPage} nodes{databaseId body submittedAt lastEditedAt '
                 'author{login}}}}}}')
        reply = self._json(['api', 'graphql', '-f', f'query={query}', '-F', f'o={owner}', '-F', f'n={name}',
                            '-F', f'pr={int(number)}'])
        if not isinstance(reply, dict) or reply.get('errors'):
            raise ApiError(f'the review read of PR #{number} returned an error')
        reviews = ((((reply.get('data') or {}).get('repository') or {}).get('pullRequest') or {}).get('reviews'))
        if not isinstance(reviews, dict) or not isinstance(reviews.get('nodes'), list):
            raise ApiError(f'the review read of PR #{number} has no reviews field')
        if (reviews.get('pageInfo') or {}).get('hasNextPage') is not False:
            raise ApiError(f'PR #{number} has more than 100 reviews (or no page info): refusing to judge part of them')
        return [{'id': n.get('databaseId'), 'body': n.get('body'), 'user': {'login': (n.get('author') or {}).get('login')},
                 'submitted_at': n.get('submittedAt'), 'last_edited_at': n.get('lastEditedAt')}
                for n in reviews['nodes']]

    def review_comments(self, number):
        return [c for page in self._pages(f'repos/{self.repo}/pulls/{number}/comments?per_page=100') for c in page]

    def check_runs(self, sha):
        return [r for page in self._pages(f'repos/{self.repo}/commits/{sha}/check-runs?per_page=100')
                for r in page.get('check_runs', [])]

    def statuses(self, sha):
        return [s for page in self._pages(f'repos/{self.repo}/commits/{sha}/statuses?per_page=100') for s in page]

    def behind_by(self, base_sha, head_sha):
        data = self._json(['api', f'repos/{self.repo}/compare/{base_sha}...{head_sha}', '--jq',
                           '{behind_by: .behind_by}'])
        if not isinstance((data or {}).get('behind_by'), int):
            raise ApiError('the compare reply has no behind_by')
        return data['behind_by']

    def arm(self, number, head):
        self._gh(['pr', 'merge', str(number), '--repo', self.repo, '--auto', '--merge', '--match-head-commit', head])

    def create_comment(self, number, body):
        self._gh(['api', '-X', 'POST', f'repos/{self.repo}/issues/{number}/comments', '--input', '-'],
                 json.dumps({'body': body}))

    def edit_comment(self, comment_id, body):
        self._gh(['api', '-X', 'PATCH', f'repos/{self.repo}/issues/comments/{comment_id}', '--input', '-'],
                 json.dumps({'body': body}))


def in_scope(pr, base):
    """Return None when the loop judges this PR, else the plain reason it does not."""
    if pr.get('isDraft'):
        return 'draft PR (out of scope until marked ready)'
    if base not in ALLOWED_BASES or pr.get('baseRefName') != base:
        return f'base is {pr.get("baseRefName")}, not {base} (out of scope; the loop acts only on {BASE})'
    if pr.get('isCrossRepository'):
        return 'PR from a fork (out of scope)'
    if not SHA.match(str(pr.get('headRefOid', ''))):
        return 'head SHA unreadable'
    return None


def snapshot(client, base, bot_login=BOT_LOGIN):
    """Read everything first; any read error raises ApiError before a single action is planned."""
    snap = {'repo': client.repo_settings(), 'base_sha': client.base_sha(base), 'prs': []}
    for pr in sorted(client.open_prs(), key=lambda p: p.get('number', 0)):
        number = pr.get('number')
        if not isinstance(number, int):
            raise ApiError('an open PR has no number')
        entry = {'pr': pr, 'excluded': EXCLUDED.get(number), 'out_of_scope': None, 'bot_login': bot_login}
        if entry['excluded']:  # PR #3: nothing past the list is read
            snap['prs'].append(entry)
            continue
        entry['out_of_scope'] = in_scope(pr, base)
        if not entry['out_of_scope'] and pr.get('mergeable') != 'UNKNOWN':
            _read_gates(client, entry, snap['base_sha'])
        snap['prs'].append(entry)
    snap['tip'] = _tip(client, base, snap['base_sha'])
    return snap


def _tip(client, base, tip):
    """Which tip workflows to dispatch: only when the base's tip IS the merge commit of a PR merged by a bot (a
    merge by a person starts push CI by itself), and only those with no run on that SHA yet (the ledger)."""
    merged_by_bot = [m for m in client.merged_prs(base)
                     if (m.get('mergeCommit') or {}).get('oid') == tip and (m.get('mergedBy') or {}).get('is_bot')]
    if not merged_by_bot:
        return {'sha': tip, 'pr': None, 'dispatch': []}
    missing = [w for w in TIP_WORKFLOWS if client.workflow_runs(w, tip) == 0]
    return {'sha': tip, 'pr': merged_by_bot[0].get('number'), 'dispatch': missing}


def _read_gates(client, entry, base_sha):
    number, head = entry['pr']['number'], entry['pr']['headRefOid']
    entry['comments'] = client.comments(number)
    entry['reviews'] = client.reviews(number)
    entry['review_comments'] = client.review_comments(number)
    entry['check_runs'] = client.check_runs(head)
    entry['statuses'] = client.statuses(head)
    entry['behind_by'] = client.behind_by(base_sha, head)


def preflight(client, planned, base):
    """Fresh reads of everything the arm depends on, right before it. Returns None when the PR still qualifies on
    every point, else the plain reason it does not. Read errors raise ApiError (the run fails closed).
    This NARROWS the window between the plan's read and the arm; it is NOT an atomic compare-and-swap. GitHub's
    branch protection (codex-verified is a required check) and --match-head-commit remain the backstop."""
    number, head = planned['number'], planned['head']
    if number in EXCLUDED or base not in ALLOWED_BASES:
        return 'excluded PR or base not allowed'
    if client.repo_settings()['allow_auto_merge'] is not True:
        return 'allow_auto_merge is no longer on'
    pr = client.pr(number)
    if pr.get('number') != number:
        return f'fresh read returned PR #{pr.get("number")}'
    if pr.get('headRefOid') != head:
        return f'head moved to {pr.get("headRefOid")}'
    if str(pr.get('state', 'OPEN')).upper() != 'OPEN':
        return f'PR is {pr.get("state")}'
    if pr.get('autoMergeRequest'):
        return 'auto-merge is already armed'
    item = {'pr': pr, 'excluded': None, 'out_of_scope': in_scope(pr, base),
            'bot_login': planned.get('bot_login', BOT_LOGIN)}
    if item['out_of_scope']:
        return item['out_of_scope']
    if pr.get('mergeable') != 'MERGEABLE':
        return f'mergeable is {pr.get("mergeable")}'
    _read_gates(client, item, client.base_sha(base))
    state, detail, _ = classify(item, base)
    if state != 'stamped-ready':
        return f'now {state}: {detail}'
    status = _latest_by_context(item['statuses']).get(STAMP_CONTEXT) or {}
    if status.get('id') != planned.get('stamp_id'):
        return f'the latest codex-verified status is now {status.get("id")}, not {planned.get("stamp_id")}'
    return None


def _body(comment):
    return str(comment.get('body') or '').lstrip('﻿').lstrip()


def _trusted(comments):
    return [c for c in comments if c.get('author_association') in TRUSTED]


def _latest_by_context(statuses):
    latest = {}
    for s in sorted(statuses, key=lambda s: (str(s.get('created_at', '')), s.get('id') or 0), reverse=True):
        latest.setdefault(s.get('context'), s)
    return latest


def stamp(entry):
    """(ok, reason). ok only when the LATEST codex-verified status on this exact head is success AND a trusted
    `CODEX VERIFIED <this head>` comment names that status's ID. Anything else never arms."""
    head = entry['pr']['headRefOid']
    status = _latest_by_context(entry['statuses']).get(STAMP_CONTEXT)
    if status is None:
        return False, 'no codex-verified stamp on this head'
    if status.get('state') != 'success':
        return False, f'codex-verified on this head is {status.get("state")} (status {status.get("id")})'
    for comment in _trusted(entry['comments']):
        text = _body(comment)
        match = VERIFIED.match(text)
        if not match or match.group(1) != head:
            continue
        named = STATUS_ID.search(text)
        if named and int(named.group(1)) == status.get('id'):
            items = _veto_items(entry, entry.get('bot_login', BOT_LOGIN))
            stamp_item = next(i for i in items if i['kind'] == 'issue_comment' and i['id'] == comment.get('id'))
            vetoed = _veto(items, stamp_item)
            if vetoed:
                return False, f'VETO: {vetoed}'
            return True, f'codex-verified status {status.get("id")} named by its CODEX VERIFIED comment'
    return False, (f'codex-verified status {status.get("id")} on this head is not named by a trusted '
                   f'"CODEX VERIFIED {head}" comment')


def _loop_owned(comment, bot_login):
    """Only the loop's own ledger comment: written by the loop's login, starting with the marker, in ledger shape.
    A copied marker in anybody else's comment does not count."""
    body = str(comment.get('body') or '')
    return ((comment.get('user') or {}).get('login') == bot_login and body.lstrip().startswith(MARKER)
            and LEDGER_LINE.search(body) is not None)


UNKNOWN_EDIT = 'unknown'
TIMESTAMP = re.compile(r'^[0-9]{4}-[0-9]{2}-[0-9]{2}T')  # only a real time is compared


def _veto_items(entry, bot_login):
    """Every surface a veto can come from, as one shape: kind ('issue_comment', 'review', 'inline'), id (unique only
    within its kind: the three kinds number separately), created (creation or submission time; '' when unknown),
    edited (the last edit time; None when the API says never edited; UNKNOWN_EDIT when the field is missing), body,
    login, and whether it is the loop's own ledger comment. Nothing downstream reads the raw API dicts."""
    def edit_of(raw, key):
        return raw[key] if key in raw else UNKNOWN_EDIT

    items = []
    for c in entry.get('comments', []):
        items.append({'kind': 'issue_comment', 'id': c.get('id'), 'created': str(c.get('created_at') or ''),
                      'edited': edit_of(c, 'updated_at'), 'body': str(c.get('body') or ''),
                      'login': (c.get('user') or {}).get('login'), 'loop_owned': _loop_owned(c, bot_login)})
    for r in entry.get('reviews', []):
        items.append({'kind': 'review', 'id': r.get('id'), 'created': str(r.get('submitted_at') or ''),
                      'edited': edit_of(r, 'last_edited_at'), 'body': str(r.get('body') or ''),
                      'login': (r.get('user') or {}).get('login'), 'loop_owned': False})
    for c in entry.get('review_comments', []):
        items.append({'kind': 'inline', 'id': c.get('id'), 'created': str(c.get('created_at') or ''),
                      'edited': edit_of(c, 'updated_at'), 'body': str(c.get('body') or ''),
                      'login': (c.get('user') or {}).get('login'), 'loop_owned': False})
    return items


def _after_stamp(item, stamp_item):
    """True when the item was created, submitted or edited after the stamp comment, or when the order cannot be
    told (fail closed). Timestamps have one-second resolution. In the same second only an issue comment can be
    ordered against the stamp (same id sequence); any other kind counts as after. An unknown creation or edit time
    counts as after; an edit in the same second as the stamp counts as after."""
    since, stamp_id = stamp_item['created'], stamp_item['id'] or 0
    created = item['created']
    if not created or created > since:
        return True
    if created == since and (item['kind'] != 'issue_comment' or (item['id'] or 0) > stamp_id):
        return True
    edited = item['edited']
    if edited == UNKNOWN_EDIT:
        return True
    return bool(TIMESTAMP.match(str(edited or ''))) and str(edited) > created and str(edited) >= since


def _veto(items, stamp_item):
    """A plain reason when any item other than the stamp comment itself (matched by kind AND id) or the loop's own
    ledger comment was created/edited after the stamp comment and holds a veto phrase."""
    for item in items:
        if (item['kind'], item['id']) == ('issue_comment', stamp_item['id']) or item['loop_owned']:
            continue
        if _after_stamp(item, stamp_item) and VETO.search(item['body']):
            return (f'{item["kind"].replace("_", " ")} {item["id"]} by {item["login"]}, newer than the stamp comment, '
                    'carries a veto word')
    return None


def _listing(items):
    """`(failure, cancelled): name1, name2`. Our own words (the states) come first and the names GitHub reported
    (untrusted text) last, so a redaction that removes the rest of a line never removes the context."""
    states = sorted({str(state) for state, _ in items})
    return f'({", ".join(states)}): ' + ', '.join(sorted(str(name) for _, name in items))


def _ci(entry):
    """(red, pending): lists of plain reasons for the head's check runs and commit statuses."""
    red, pending, reported = [], [], set()
    latest = {}  # a re-run in a new suite (e.g. pr-evidence after a body edit) replaces the older run of that name
    for run_ in sorted(entry['check_runs'], key=lambda r: r.get('id') or 0):
        latest[run_.get('name')] = run_
    for run_ in latest.values():
        name = run_.get('name')
        reported.add(name)
        if run_.get('status') != 'completed':
            pending.append((str(run_.get('status')), name))
        elif name in REQUIRED_CHECKS and run_.get('conclusion') != 'success':
            red.append((f'{run_.get("conclusion")} on a required check', name))
        elif run_.get('conclusion') not in OPTIONAL_OK_CONCLUSIONS:
            red.append((str(run_.get('conclusion')), name))
    for context, status in _latest_by_context(entry['statuses']).items():
        if context == STAMP_CONTEXT:
            continue
        reported.add(context)
        if status.get('state') == 'pending':
            pending.append(('pending', context))
        elif status.get('state') != 'success':
            red.append((str(status.get('state')), context))
    pending += [('not reported yet', name) for name in REQUIRED_CHECKS if name not in reported]
    return red, pending


def classify(entry, base):
    """Return (state, detail, next) for one PR read by snapshot(). The first unmet gate wins."""
    pr = entry['pr']
    head = pr.get('headRefOid')
    if entry.get('out_of_scope'):
        return 'blocked', entry['out_of_scope'], 'owner'
    if pr.get('mergeable') == 'UNKNOWN':
        return 'blocked', 'GitHub has not computed mergeability yet (read again next run)', 'loop'
    if pr.get('mergeable') == 'CONFLICTING' or pr.get('mergeStateStatus') == 'DIRTY':
        return 'needs-rebase', f'conflicts with {base}: the lane merges {base} in and resolves them', 'lane'
    if entry['behind_by'] > 0:
        return 'needs-rebase', f'{entry["behind_by"]} commit(s) behind {base}: the lane syncs the branch', 'lane'
    stamp_status = _latest_by_context(entry['statuses']).get(STAMP_CONTEXT)
    if stamp_status and stamp_status.get('state') in ('failure', 'error'):
        return 'blocked', (f'the verifier refused this head (codex-verified {stamp_status.get("state")}, '
                           f'status {stamp_status.get("id")})'), 'lane'
    red, pending = _ci(entry)
    if red:
        return 'ci-red', 'failed on the head ' + _listing(red), 'lane'
    if pending:
        return 'waiting-QA', 'checks not finished on the head ' + _listing(pending), 'qa'
    trusted = [_body(c) for c in _trusted(entry['comments'])]
    if not any((m := QA_PASS.match(t)) and m.group(1) == head for t in trusted):
        return 'waiting-QA', f'checks green; no QA PASS comment names {head}', 'qa'
    if not any((m := REVIEW_PASS.match(t)) and m.group(1) == head for t in trusted):
        return 'waiting-review', f'QA PASS present; no CODEX REVIEW PASS names {head}', 'reviewer'
    ok, reason = stamp(entry)
    if reason.startswith('VETO: '):
        return 'blocked', f'stamp vetoed: {reason[6:]}', 'lane'
    if not ok:
        return 'waiting-stamp', f'QA and review PASS present; {reason}', 'verifier'
    return 'stamped-ready', reason, 'auto-merge'


def _setting(snap):
    value = snap['repo']['allow_auto_merge']
    return {True: 'on', False: 'off'}.get(value, 'unreadable with this token')


def _ledger(entry, bot_login):
    """The bot's latest marker comment: (comment id, state, head) or None."""
    mine = [c for c in entry['comments']
            if (c.get('user') or {}).get('login') == bot_login and MARKER in str(c.get('body') or '')]
    if not mine:
        return None
    last = max(mine, key=lambda c: (str(c.get('created_at', '')), c.get('id') or 0))
    match = LEDGER_LINE.search(str(last.get('body') or ''))
    return (last.get('id'), match.group(1) if match else None, match.group(2) if match else None)


def plan(snap, base, bot_login):
    """One entry per open PR: state, detail, next, why arming is or is not planned, and at most one action."""
    entries = []
    for item in snap['prs']:
        pr = item['pr']
        number, head = pr['number'], pr.get('headRefOid')
        if item['excluded']:
            entries.append({'number': number, 'head': head, 'state': 'excluded', 'detail': item['excluded'],
                            'next': '-', 'arm': 'no (excluded)', 'action': None})
            continue
        state, detail, who = classify(item, base)
        entry = {'number': number, 'head': head, 'state': state, 'detail': detail, 'next': who, 'action': None}
        judged = 'statuses' in item
        stamp_ok, stamp_reason = stamp(item) if judged else (False, detail)
        if state == 'stamped-ready':
            if snap['repo']['allow_auto_merge'] is not True:
                entry['arm'] = ('no (the repository setting allow_auto_merge is '
                                f'{_setting(snap)}; the loop never changes settings)')
                entry['next'] = 'owner'
            elif pr.get('autoMergeRequest'):
                entry['arm'] = 'no (auto-merge already armed: nothing to repeat)'
            else:
                entry['arm'] = f'YES, after a fresh preflight ({stamp_reason})'
                entry['action'] = 'arm'
                entry['stamp_id'] = _latest_by_context(item['statuses'])[STAMP_CONTEXT].get('id')
                entry['bot_login'] = item.get('bot_login', BOT_LOGIN)
        else:
            entry['arm'] = f'no ({stamp_reason})' if not stamp_ok else f'no (state {state})'
        if entry['action'] is None and judged:
            line = f'pr-loop: state={state} head={head} next={entry["next"]}'
            ledger = _ledger(item, bot_login)
            if not ledger or (ledger[1], ledger[2]) != (state, head):
                entry['body'] = (f'{MARKER}\n{line}\n\n{detail}\n\n_Written by the pr-loop workflow ({RULE}, '
                                 'docs/quality/pr-loop.md). One comment per PR, edited in place; the same state and '
                                 'head means no edit._')
                entry['action'] = 'edit-comment' if ledger else 'create-comment'
                entry['comment_id'] = ledger[0] if ledger else None
        entries.append(entry)
    return entries


def execute(entries, client, apply, out=emit, base=BASE):
    """Take the planned actions (only with apply). PR #3 is refused here again even if a plan names it.
    Every arm is preceded by a fresh preflight; at most ONE arm per run, because an arm can move the base on and
    every later plan was read against the old base (the next run re-reads everything)."""
    done, armed = 0, 0
    for entry in entries:
        if entry['number'] in EXCLUDED:
            if entry.get('action'):
                raise ApiError(f'refused: a plan named an action on excluded PR #{entry["number"]}')
            continue
        action = entry.get('action')
        if not action:
            continue
        if not apply:
            out(f'DRY RUN: would {action} on PR #{entry["number"]} (head {entry["head"]}); nothing done'
                + (' (a fresh preflight would run first)' if action == 'arm' else ''))
            continue
        if action == 'arm':
            if armed:
                out(f'SKIP: arm on PR #{entry["number"]}: at most one arm per run (the base may have moved); '
                    'the next run re-reads it')
                continue
            try:
                why_not = preflight(client, entry, base)
            except ApiError as error:
                raise ApiError(f'preflight read for PR #{entry["number"]} failed, no arm: {error}') from None
            if why_not:
                out(f'SKIP: arm on PR #{entry["number"]}: the fresh preflight no longer qualifies it ({why_not})')
                continue
            out(f'PREFLIGHT OK for PR #{entry["number"]}: fresh reads still qualify it. This narrows the race '
                'window; it is not atomic. Branch protection and --match-head-commit remain the backstop.')
        try:
            if action == 'arm':
                armed += 1
                client.arm(entry['number'], entry['head'])
            elif action == 'create-comment':
                client.create_comment(entry['number'], safe(entry['body']))
            elif action == 'edit-comment':
                client.edit_comment(entry['comment_id'], safe(entry['body']))
            else:
                raise ApiError(f'unknown action {action}')
        except ApiError as error:
            raise ApiError(f'{action} on PR #{entry["number"]} failed, stopping: {error}') from None
        out(f'DONE: {action} on PR #{entry["number"]} (head {entry["head"]})')
        done += 1
    return done


def execute_tip(tip, client, apply, base, out=emit):
    """Dispatch the missing tip workflows on the base, after a fresh check that the tip has not moved and still
    has no run of that workflow. Read errors raise ApiError (fail closed)."""
    done = 0
    for workflow in tip['dispatch']:
        if not apply:
            out(f'DRY RUN: would dispatch {workflow} on {base} (tip {tip["sha"]}, bot merge of PR #{tip["pr"]}); '
                'nothing done')
            continue
        now = client.base_sha(base)
        if now != tip['sha']:
            out(f'SKIP: dispatch {workflow}: {base} moved to {now} (its own push runs cover it)')
            continue
        if client.workflow_runs(workflow, now):
            out(f'SKIP: dispatch {workflow}: a run on {now} already exists')
            continue
        client.dispatch(workflow, base)
        out(f'DONE: dispatch {workflow} on {base} (tip {now}, bot merge of PR #{tip["pr"]})')
        done += 1
    return done


def run(client, apply, base=BASE, bot_login=BOT_LOGIN, out=emit):
    """Snapshot, plan, print, then (only with apply) act. Returns the exit code; 1 = failed closed."""
    raw_out = out

    def out(text):
        raw_out(safe(text))
    try:
        snap = snapshot(client, base, bot_login)
        entries = plan(snap, base, bot_login)
    except ApiError as error:
        out(f'::error::{RULE} fail closed while reading GitHub, no action taken: {error}')
        return 1
    out(f'{RULE} pr-loop: {len(entries)} open PR(s), base {base} at {snap["base_sha"]}, '
        f'allow_auto_merge={_setting(snap)} (read via {snap["repo"].get("source", "?")}), '
        f'mode={"APPLY" if apply else "DRY RUN"}')
    if snap['repo']['allow_auto_merge'] is not True:
        out(f'::warning::the repository setting allow_auto_merge is {_setting(snap)}: the loop will never arm '
            '(it does not change settings)')
    for e in entries:
        out(f'PR #{e["number"]} state={e["state"]} head={e["head"]} next={e["next"]}\n'
            f'    why:  {e["detail"]}\n    arm:  {e["arm"]}\n'
            f'    action: {e["action"] or "no action"}')
    tip = snap['tip']
    out(f'tip of {base} {tip["sha"]}: ' + (f'bot merge of PR #{tip["pr"]}; dispatch: '
                                          f'{", ".join(tip["dispatch"]) or "none (runs already exist)"}'
                                          if tip['pr'] else 'not a bot merge; no dispatch needed'))
    counts = {s: sum(1 for e in entries if e['state'] == s) for s in (*STATES, 'excluded')}
    out('states: ' + ', '.join(f'{k}={v}' for k, v in counts.items() if v))
    try:
        done = execute(entries, client, apply, out, base)
        done += execute_tip(snap['tip'], client, apply, base, out)
    except ApiError as error:
        out(f'::error::{RULE} fail closed: {error}')
        return 1
    planned = sum(1 for e in entries if e.get('action')) + len(snap['tip']['dispatch'])
    out(f'actions: {planned} planned, {done} done' + ('' if apply else ' (dry run)'))
    return 0


# ---------------------------------------------------------------------------------------------------------------- tests

class FakeClient:
    """Planted GitHub for the self-test. Records every read and write; `fail` names a method that raises ApiError."""

    def __init__(self, prs, comments=None, checks=None, statuses=None, behind=None, allow_auto_merge=True, fail=None):
        self.prs, self.comments_by_pr = prs, comments or {}
        self.checks_by_sha, self.statuses_by_sha = checks or {}, statuses or {}
        self.behind, self.allow, self.fail = behind or {}, allow_auto_merge, fail
        self.reads, self.writes = [], []
        self.after = {}  # method name -> callable(self), run once right after that method's FIRST call returns
        self.after_arm = None  # callable(self, number), run after each arm (e.g. the base moves on)
        self.tip = 'b' * 40  # the base branch head
        self.merged = []  # recently merged PRs into the base: {'number', 'mergeCommit': {'oid'}, 'mergedBy': {...}}
        self.runs = {}  # (workflow file, sha) -> number of runs that exist
        self.reviews_by_pr, self.inline_by_pr = {}, {}  # PR reviews and inline review comments

    def _read(self, name, key=None):
        self.reads.append((name, key))
        calls = sum(1 for r in self.reads if r[0] == name)
        if self.fail == name or (isinstance(self.fail, tuple) and self.fail[0] == name and calls > self.fail[1]):
            raise ApiError(getattr(self, 'fail_text', None) or f'planted failure in {name}')

    def _then(self, name, value):
        hook = self.after.pop(name, None)
        if hook:
            hook(self)
        return value

    def pr(self, number):
        self._read('pr', number)
        found = [dict(p) for p in self.prs if p['number'] == number]
        if not found:
            raise ApiError(f'PR #{number} not found')
        found[0].setdefault('state', 'OPEN')
        return found[0]

    def repo_settings(self):
        self._read('repo_settings')
        return {'allow_auto_merge': self.allow, 'source': 'fixture'}

    def base_sha(self, base):
        self._read('base_sha')
        return self.tip

    def open_prs(self):
        self._read('open_prs')
        return [dict(p) for p in self.prs]

    def comments(self, number):
        self._read('comments', number)
        return list(self.comments_by_pr.get(number, []))

    def reviews(self, number):
        self._read('reviews', number)
        return [dict(r) for r in self.reviews_by_pr.get(number, [])]

    def review_comments(self, number):
        self._read('review_comments', number)
        return [dict(r) for r in self.inline_by_pr.get(number, [])]

    def check_runs(self, sha):
        self._read('check_runs', sha)
        runs = [dict(r) for r in self.checks_by_sha.get(sha, [])]
        for r in runs:
            r.setdefault('id', 0)
            r.setdefault('completed_at', '2026-11-16T00:00:00Z' if r.get('status') == 'completed' else None)
        return runs

    def statuses(self, sha):
        self._read('statuses', sha)
        statuses = [dict(s) for s in self.statuses_by_sha.get(sha, [])]
        for s in statuses:
            s.setdefault('updated_at', s.get('created_at'))
            s.setdefault('description', None)
        return statuses

    def behind_by(self, base_sha, head_sha):
        self._read('behind_by', head_sha)
        return self._then('behind_by', self.behind.get(head_sha, 0))

    def _write(self, *call):
        if self.fail == call[0]:
            raise ApiError(f'planted failure in {call[0]}')
        self.writes.append(call)

    def arm(self, number, head):
        self._write('arm', number, head)
        if self.after_arm:
            self.after_arm(self, number)

    def create_comment(self, number, body):
        self._write('create_comment', number, body)

    def edit_comment(self, comment_id, body):
        self._write('edit_comment', comment_id, body)

    def merged_prs(self, base):
        self._read('merged_prs')
        return [dict(m) for m in self.merged]

    def workflow_runs(self, workflow, sha):
        self._read('workflow_runs', (workflow, sha))
        return self._then('workflow_runs', self.runs.get((workflow, sha), 0))

    def dispatch(self, workflow, ref):
        self._write('dispatch', workflow, ref)


def _sha(n):
    return f'{n:040x}'


def _review_node(r):
    if 'databaseId' in r:
        return dict(r)
    node = {'databaseId': r['id'], 'body': r['body'], 'submittedAt': r['submitted_at'], 'author': r['user']}
    if 'last_edited_at' in r:
        node['lastEditedAt'] = r['last_edited_at']
    return node


def _comment(body, author='OWNER', login='vikas53953', cid=1, created='2026-11-16T00:00:00Z'):
    return {'id': cid, 'body': body, 'author_association': author, 'user': {'login': login}, 'created_at': created,
            'updated_at': created}


def _world(number=200, head=None, **edit):
    """A PR that passes every gate (stamped-ready), as a FakeClient. `edit` overrides parts of it."""
    head = head or _sha(number)
    pr = {'number': number, 'headRefOid': head, 'headRefName': f'fix/{number}', 'baseRefName': BASE,
          'isDraft': False, 'isCrossRepository': False, 'mergeable': 'MERGEABLE', 'mergeStateStatus': 'BLOCKED',
          'autoMergeRequest': None}
    pr.update(edit.pop('pr', {}))
    checks = [{'name': n, 'status': 'completed', 'conclusion': 'success'} for n in REQUIRED_CHECKS]
    checks.append({'name': 'github-release', 'status': 'completed', 'conclusion': 'skipped'})
    stamp_id = 58974699495
    statuses = [{'id': stamp_id, 'context': STAMP_CONTEXT, 'state': 'success', 'created_at': '2026-11-16T01:00:00Z'}]
    comments = [
        _comment(f'QA PASS at {head}: all green.', cid=10),
        _comment(f'﻿CODEX REVIEW PASS — {head}\n\nno findings', cid=11),
        _comment(f'CODEX VERIFIED {head}\n\nReturned status ID **{stamp_id}**, context `codex-verified`, SUCCESS.',
                 cid=12, created='2026-11-16T02:00:00Z'),
    ]
    data = {'prs': [pr], 'comments': {number: comments}, 'checks': {head: checks}, 'statuses': {head: statuses},
            'behind': {head: 0}}
    for key, fn in edit.items():
        fn(data)
    return FakeClient(data['prs'], data['comments'], data['checks'], data['statuses'], data['behind'],
                      allow_auto_merge=data.get('allow', True), fail=data.get('fail'))


def self_test():
    results = []

    def check(name, cond):
        results.append((name, bool(cond)))
        emit(('PASS  ' if cond else 'FAIL  ') + name)

    def go(client, apply=False):
        lines = []
        try:
            code = run(client, apply=apply, out=lines.append)
        except Exception as error:  # a stub or a crash is a failed case, never a pass
            lines.append(f'CRASH {type(error).__name__}: {error}')
            code = 'crash'
        return code, '\n'.join(lines)

    def state_of(client):
        try:
            entries = plan(snapshot(client, BASE), BASE, BOT_LOGIN)
        except Exception as error:
            return f'crash {type(error).__name__}'
        return entries[0]['state'] if entries else None

    def arms(client):
        return [w for w in client.writes if w[0] == 'arm']

    head = _sha(200)

    # One fixture per state.
    check('stamped-ready: every gate met', state_of(_world()) == 'stamped-ready')
    check('waiting-stamp: QA and review PASS, no codex-verified status',
          state_of(_world(s=lambda d: d['statuses'].update({head: []}))) == 'waiting-stamp')
    check('waiting-review: QA PASS only',
          state_of(_world(c=lambda d: d['comments'][200].__delitem__(slice(1, 3)))) == 'waiting-review')
    check('waiting-QA: no QA PASS comment',
          state_of(_world(c=lambda d: d['comments'][200].__delitem__(0))) == 'waiting-QA')
    check('waiting-QA: a required check still running',
          state_of(_world(k=lambda d: d['checks'][head].__setitem__(0, {'name': 'demos', 'status': 'in_progress',
                                                                        'conclusion': None}))) == 'waiting-QA')
    check('waiting-QA: a required check not reported at all',
          state_of(_world(k=lambda d: d['checks'][head].pop(0))) == 'waiting-QA')
    check('ci-red: a check run failed on the head',
          state_of(_world(k=lambda d: d['checks'][head].append({'name': 'smoke', 'status': 'completed',
                                                                'conclusion': 'failure'}))) == 'ci-red')
    check('a newer run of the same check (e.g. evidence after a body edit) replaces the older failed one',
          state_of(_world(k=lambda d: d['checks'][head].__setitem__(slice(0, 0), [
              {'id': 1, 'name': 'evidence', 'status': 'completed', 'conclusion': 'failure'}]) or [
              r.__setitem__('id', 100 + i) for i, r in enumerate(d['checks'][head][1:])])) == 'stamped-ready')
    check('an older passing run does not hide a newer failed run of the same check',
          state_of(_world(k=lambda d: d['checks'][head].append(
              {'id': 10 ** 6, 'name': 'evidence', 'status': 'completed', 'conclusion': 'failure'}))) == 'ci-red')
    check('ci-red: a commit status (not codex-verified) is failure',
          state_of(_world(s=lambda d: d['statuses'][head].append({'id': 5, 'context': 'other', 'state': 'failure',
                                                                  'created_at': '2026-11-16T00:00:00Z'}))) == 'ci-red')
    check('needs-rebase: behind the base',
          state_of(_world(b=lambda d: d['behind'].update({head: 3}))) == 'needs-rebase')
    check('needs-rebase: conflicting',
          state_of(_world(pr={'mergeable': 'CONFLICTING', 'mergeStateStatus': 'DIRTY'})) == 'needs-rebase')
    check('blocked: draft PR', state_of(_world(pr={'isDraft': True})) == 'blocked')
    check('blocked: another base branch', state_of(_world(pr={'baseRefName': 'qa/app-explorer'})) == 'blocked')
    check('blocked: mergeability not computed yet', state_of(_world(pr={'mergeable': 'UNKNOWN'})) == 'blocked')

    # Arming: only on a matched stamp.
    c = _world()
    code, text = go(c, apply=True)
    check('stamped-ready + --apply arms exactly once with --match-head-commit on the head',
          code == 0 and arms(c) == [('arm', 200, head)] and len(c.writes) == 1)

    c = _world(c=lambda d: d['comments'][200].__setitem__(2, _comment(
        f'CODEX VERIFIED {_sha(999)}\n\nReturned status ID **58974699495**, context `codex-verified`, SUCCESS.')))
    code, text = go(c, apply=True)
    check('a stamp comment naming another SHA does NOT arm', code == 0 and not arms(c)
          and 'state=waiting-stamp' in text)

    c = _world(c=lambda d: d['comments'][200].__setitem__(2, _comment(
        f'CODEX VERIFIED {head}\n\nReturned status ID **12345**, context `codex-verified`, SUCCESS.')))
    code, text = go(c, apply=True)
    check('a stamp comment naming a different status ID does NOT arm', code == 0 and not arms(c)
          and 'state=waiting-stamp' in text)

    c = _world(s=lambda d: d['statuses'][head].__setitem__(0, {'id': 58974699495, 'context': STAMP_CONTEXT,
                                                               'state': 'failure',
                                                               'created_at': '2026-11-16T01:00:00Z'}))
    code, text = go(c, apply=True)
    check('a failure codex-verified status does NOT arm (blocked)', code == 0 and not arms(c)
          and 'state=blocked' in text)

    c = _world(s=lambda d: d['statuses'][head].insert(0, {'id': 7, 'context': STAMP_CONTEXT, 'state': 'failure',
                                                          'created_at': '2026-11-16T02:00:00Z'}))
    code, text = go(c, apply=True)
    check('a later failure status replaces an earlier success: does NOT arm', code == 0 and not arms(c))

    c = _world(c=lambda d: d['comments'][200].__setitem__(2, dict(d['comments'][200][2], author_association='NONE',
                                                                  user={'login': 'stranger'})))
    code, text = go(c, apply=True)
    check('a stamp comment from an untrusted account does NOT arm', code == 0 and not arms(c))

    c = _world(a=lambda d: d.__setitem__('allow', False))
    code, text = go(c, apply=True)
    check('repository auto-merge setting off: detected and reported, never armed, settings untouched '
          '(only the marker comment, naming the owner)',
          code == 0 and not arms(c) and 'allow_auto_merge' in text
          and [w[0] for w in c.writes] == ['create_comment'] and 'next=owner' in c.writes[0][2])

    c = _world(a=lambda d: d.__setitem__('allow', None))
    code, text = go(c, apply=True)
    check('repository auto-merge setting unreadable with this token: never armed, reported',
          code == 0 and not arms(c) and 'unreadable' in text)

    # PR #3 is never touched, even when it looks stamped-ready.
    c = _world(number=3, pr={'headRefName': 'lane/face', 'baseRefName': 'main'})
    c3 = _world(number=3)  # same number, ordinary-looking base: still excluded
    code, text = go(c, apply=True)
    code3, text3 = go(c3, apply=True)
    check('PR #3 is never read past the list and never acted on (even if it looks stamped-ready)',
          code == 0 and code3 == 0 and not c.writes and not c3.writes
          and not [r for r in c3.reads if r[1] in (3, _sha(3))])
    try:
        execute([{'number': 3, 'head': _sha(3), 'action': 'arm'}], c3, apply=True)
        refused = False
    except Exception:
        refused = True
    check('the executor refuses PR #3 even if a plan names it', refused and not c3.writes)

    # Idempotency: a second run on the same head does nothing.
    c = _world(pr={'autoMergeRequest': {'enabledAt': '2026-11-16T03:00:00Z'}},
               c=lambda d: d['comments'][200].append(_comment(
                   f'{MARKER}\npr-loop: state=stamped-ready head={head} next=auto-merge\n', author='NONE',
                   login=BOT_LOGIN, cid=50)))
    code, text = go(c, apply=True)
    check('second run, same head: auto-merge already armed and marker says the same: no action',
          code == 0 and not c.writes)

    c = _world(s=lambda d: d['statuses'].update({head: []}),
               c=lambda d: d['comments'][200].append(_comment(
                   f'{MARKER}\npr-loop: state=waiting-stamp head={head} next=verifier\n', author='NONE',
                   login=BOT_LOGIN, cid=50)))
    code, text = go(c, apply=True)
    check('second run, same state and head: no new or edited comment', code == 0 and not c.writes)

    c = _world(s=lambda d: d['statuses'].update({head: []}))
    code, text = go(c, apply=True)
    check('first run on a waiting PR: one marker comment created, naming state, head and next',
          code == 0 and len(c.writes) == 1 and c.writes[0][0] == 'create_comment'
          and f'pr-loop: state=waiting-stamp head={head} next=verifier' in c.writes[0][2])

    c = _world(s=lambda d: d['statuses'].update({head: []}),
               c=lambda d: d['comments'][200].append(_comment(
                   f'{MARKER}\npr-loop: state=waiting-review head={head} next=reviewer\n', author='NONE',
                   login=BOT_LOGIN, cid=50)))
    code, text = go(c, apply=True)
    check('state changed: the one marker comment is edited in place, not re-posted',
          code == 0 and len(c.writes) == 1 and c.writes[0][:2] == ('edit_comment', 50))

    c = _world(s=lambda d: d['statuses'].update({head: []}),
               c=lambda d: d['comments'][200].append(_comment(
                   f'{MARKER}\npr-loop: state=waiting-stamp head={head} next=verifier\n', cid=50)))
    code, text = go(c, apply=True)
    check('a marker comment from someone other than the bot is not the ledger (bot writes its own)',
          code == 0 and len(c.writes) == 1 and c.writes[0][0] == 'create_comment')

    c = _world()
    code, text = go(c, apply=True)
    check('one action per PR per run: arming is the only action (no comment in the same run)', len(c.writes) == 1)

    # Stale snapshot: GitHub changes AFTER the plan was read and BEFORE the arm. A fresh preflight must catch it.
    def changed_after_read(change):
        c = _world()
        c.after['behind_by'] = change  # the last snapshot read of the PR; everything later sees the change
        return c

    newer_failure = {'id': 77, 'context': STAMP_CONTEXT, 'state': 'failure', 'created_at': '2026-11-16T09:00:00Z'}
    stale_cases = {
        'same-head stamp revoked (a newer codex-verified FAILURE) and the comments removed':
            lambda f: (f.statuses_by_sha[head].insert(0, dict(newer_failure)), f.comments_by_pr[200].clear()),
        'same-head stamp revoked (a newer codex-verified FAILURE), comments kept':
            lambda f: f.statuses_by_sha[head].insert(0, dict(newer_failure)),
        'the CODEX VERIFIED comment withdrawn': lambda f: f.comments_by_pr[200].pop(2),
        'the CODEX VERIFIED comment edited to another SHA': lambda f: f.comments_by_pr[200].__setitem__(2, _comment(
            f'CODEX VERIFIED {_sha(999)}\n\nReturned status ID **58974699495**, context `codex-verified`, SUCCESS.')),
        'the CODEX VERIFIED comment edited to another status ID': lambda f: f.comments_by_pr[200].__setitem__(
            2, _comment(f'CODEX VERIFIED {head}\n\nReturned status ID **4242**, context `codex-verified`, SUCCESS.')),
        'the same-head PR retargeted to main': lambda f: f.prs[0].__setitem__('baseRefName', 'main'),
        'the same-head PR retargeted to another branch': lambda f: f.prs[0].__setitem__('baseRefName', 'qa/other'),
        'the PR turned into a draft': lambda f: f.prs[0].__setitem__('isDraft', True),
        'the PR fell behind the base': lambda f: f.behind.__setitem__(head, 2),
        'a check turned red on the head': lambda f: f.checks_by_sha[head].append(
            {'id': 10 ** 7, 'name': 'smoke', 'status': 'completed', 'conclusion': 'failure'}),
        'auto-merge got armed by someone else': lambda f: f.prs[0].__setitem__(
            'autoMergeRequest', {'enabledAt': '2026-11-16T09:00:00Z'}),
    }
    for name, change in stale_cases.items():
        c = changed_after_read(change)
        code, text = go(c, apply=True)
        check(f'preflight: {name} after the plan was read: NOT armed', not arms(c) and code in (0, 1))

    c = _world()
    c.fail = ('statuses', 1)  # the plan's read works; the fresh pre-action read fails
    code, text = go(c, apply=True)
    check('preflight: a pre-action read failure: no arm, exit 1', code == 1 and not arms(c))
    c = _world()
    c.fail = ('pr', 0)
    code, text = go(c, apply=True)
    check('preflight: the fresh PR read fails: no arm, exit 1', code == 1 and not arms(c))

    c = _world()
    code, text = go(c, apply=True)
    check('preflight passes on an unchanged PR: armed once, and the output says the preflight narrows (not removes) '
          'the race', arms(c) == [('arm', 200, head)] and 'not atomic' in text.lower())

    two = _world()
    second = _world(number=201)
    two.prs += second.prs
    two.comments_by_pr.update(second.comments_by_pr)
    two.checks_by_sha.update(second.checks_by_sha)
    two.statuses_by_sha.update(second.statuses_by_sha)
    two.behind.update(second.behind)
    two.after_arm = lambda f, n: f.behind.update({p['headRefOid']: 1 for p in f.prs if p['number'] != n})
    code, text = go(two, apply=True)
    check('a first arm moves the base on: no later PR is armed in the same run', len(arms(two)) == 1)

    # Review P2 at d3810d4: two guards that no earlier case could tell apart from their removal.
    def two_ready():
        w, second = _world(), _world(number=201)
        w.prs += second.prs
        for attr in ('comments_by_pr', 'checks_by_sha', 'statuses_by_sha', 'behind'):
            getattr(w, attr).update(getattr(second, attr))
        return w

    c = two_ready()  # the base does NOT move after the first arm
    code, text = go(c, apply=True)
    check('P2: two stamped-ready PRs, base unchanged after the first arm: exactly one arm in the run',
          code == 0 and arms(c) == [('arm', 200, head)])

    def replace_stamp(f):
        f.statuses_by_sha[head].insert(0, {'id': 999, 'context': STAMP_CONTEXT, 'state': 'success',
                                           'created_at': '2026-11-16T05:00:00Z'})
        f.comments_by_pr[200].append(_comment(
            f'CODEX VERIFIED {head}\n\nReturned status ID **999**, context `codex-verified`, SUCCESS.', cid=13,
            created='2026-11-16T05:00:00Z'))
    c = changed_after_read(replace_stamp)
    code, text = go(c, apply=True)
    check('P2: after the read, a NEW latest SUCCESS stamp with its own matching comment replaces the planned one: '
          'NOT armed (same-ID policy)', code == 0 and not arms(c))

    # (b) Every PR whose base is main is out of scope, not only #3.
    c = _world(number=201, pr={'baseRefName': 'main'})
    code, text = go(c, apply=True)
    check('(b) a non-#3 PR with base main that looks stamped-ready: blocked, never armed, nothing written',
          code == 0 and not c.writes and 'PR #201 state=blocked' in text)

    # (c) A comment newer than the stamp comment that says BLOCK / NOT VERIFIED / SURVIVED vetoes the arm.
    for word in ('BLOCK', 'NOT VERIFIED', 'SURVIVED'):
        c = _world(c=lambda d, w=word: d['comments'][200].append(_comment(
            f'CODEX REVIEW {w} — {head}: a finding', cid=20, created='2026-11-16T03:00:00Z')))
        code, text = go(c, apply=True)
        check(f'(c) a comment newer than the stamp says {word}: NOT armed (blocked)',
              code == 0 and not arms(c) and 'state=blocked' in text)
    c = _world(c=lambda d: d['comments'][200].append(_comment(
        f'CODEX REVIEW BLOCK — {head}: old finding', cid=5, created='2026-11-16T01:30:00Z')))
    code, text = go(c, apply=True)
    check('(c) a BLOCK comment OLDER than the stamp comment does not veto: armed', arms(c) == [('arm', 200, head)])
    c = _world(c=lambda d: d['comments'][200].append(_comment(
        'this was blocked earlier; BLOCKED is not the word either', cid=20, created='2026-11-16T03:00:00Z')))
    code, text = go(c, apply=True)
    check('(c) whole words, case-sensitive: "blocked" / "BLOCKED" do not veto', arms(c) == [('arm', 200, head)])
    c = _world(c=lambda d: d['comments'][200].append(dict(_comment(
        'edited later to say BLOCK', cid=6, created='2026-11-16T01:30:00Z'), updated_at='2026-11-16T03:00:00Z')))
    code, text = go(c, apply=True)
    check('(c) an older comment EDITED after the stamp to say BLOCK vetoes', not arms(c))
    c = changed_after_read(lambda f: f.comments_by_pr[200].append(_comment(
        f'CODEX REVIEW BLOCK — {head}', cid=21, created='2026-11-16T03:00:00Z')))
    code, text = go(c, apply=True)
    check('(c) a BLOCK comment posted after the plan was read: the fresh preflight refuses', not arms(c))

    # (d) After a merge done by a bot (auto-merge armed with the workflow token), dispatch tip CI once.
    tip_workflows = ('windows-ci.yml', 'perf-guards.yml')

    def bot_merged(c, runs=None):
        c.merged = [{'number': 150, 'mergeCommit': {'oid': c.tip}, 'mergedBy': {'login': 'app/github-actions',
                                                                                 'is_bot': True}}]
        c.runs = runs or {}
        return c

    def dispatches(c):
        return [w for w in c.writes if w[0] == 'dispatch']

    c = bot_merged(_world(s=lambda d: d['statuses'].update({head: []})))
    code, text = go(c, apply=True)
    check('(d) tip is a bot merge with no tip runs: windows-ci and perf-guards dispatched once each on the base',
          code == 0 and sorted(dispatches(c)) == [('dispatch', w, BASE) for w in sorted(tip_workflows)])
    c = bot_merged(_world(s=lambda d: d['statuses'].update({head: []})),
                   runs={(w, 'b' * 40): 1 for w in tip_workflows})
    code, text = go(c, apply=True)
    check('(d) the tip already has runs of those workflows: no dispatch (ledger = existing runs on that SHA)',
          code == 0 and not dispatches(c))
    c = bot_merged(_world(s=lambda d: d['statuses'].update({head: []})),
                   runs={(w, 'b' * 40): 1 for w in tip_workflows})
    code, text = go(c, apply=False)
    check('(d) the plan itself reads the ledger: tip runs exist, so even a dry run plans no dispatch',
          code == 0 and 'would dispatch' not in text and 'runs already exist' in text)
    c = bot_merged(_world(s=lambda d: d['statuses'].update({head: []})))
    c.after['workflow_runs'] = lambda f: f.runs.update({(w, 'b' * 40): 1 for w in tip_workflows})
    code, text = go(c, apply=True)
    check('(d) a tip run appears between the read and the dispatch (e.g. another loop run): no dispatch',
          code == 0 and not dispatches(c))
    c = bot_merged(_world(s=lambda d: d['statuses'].update({head: []})))
    code, text = go(c, apply=False)
    check('(d) dry run: the dispatch is planned and printed, nothing done',
          code == 0 and not c.writes and 'dispatch' in text and 'windows-ci' in text)
    c = bot_merged(_world(s=lambda d: d['statuses'].update({head: []})))
    c.merged[0]['mergedBy'] = {'login': 'vikas53953', 'is_bot': False}
    code, text = go(c, apply=True)
    check('(d) a merge by a person starts push CI itself: no dispatch', code == 0 and not dispatches(c))
    c = bot_merged(_world(s=lambda d: d['statuses'].update({head: []})))
    c.merged[0]['mergeCommit'] = {'oid': 'c' * 40}
    code, text = go(c, apply=True)
    check('(d) the bot merge is no longer the tip (a later push has its own CI): no dispatch',
          code == 0 and not dispatches(c))
    c = bot_merged(_world(s=lambda d: d['statuses'].update({head: []})))
    c.after['behind_by'] = lambda f: setattr(f, 'tip', 'd' * 40)
    code, text = go(c, apply=True)
    check('(d) the tip moved between the read and the dispatch: no dispatch', code == 0 and not dispatches(c))
    c = bot_merged(_world())
    c.fail = 'merged_prs'
    code, text = go(c, apply=True)
    check('(d) the merged-PR read fails: exit 1, no action at all', code == 1 and not c.writes)

    # (a) No schedule in this repository's pr-loop workflow (schedules fire only from main); manual dispatch with a
    # dry_run input that defaults to true. (d) every tip workflow can be dispatched.
    flows = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '.github', 'workflows')

    def flow(name):
        try:
            with open(os.path.join(flows, name), encoding='utf-8') as handle:
                return handle.read()
        except OSError:
            return ''
    loop_yml = flow('pr-loop.yml')
    check('(a) pr-loop.yml has no schedule trigger (a scheduler on main dispatches it with --ref lane/face)',
          bool(loop_yml) and not re.search(r'^\s*schedule:', loop_yml, re.M))
    check('(a) pr-loop.yml keeps workflow_dispatch with a dry_run input that defaults to true',
          re.search(r'^  workflow_dispatch:\s*\n    inputs:\s*\n      dry_run:(?:\n        .*)*?\n        default: true',
                    loop_yml, re.M) is not None)
    check('(d) every tip workflow has a workflow_dispatch trigger',
          all(re.search(r'^  workflow_dispatch:', flow(w), re.M) for w in tip_workflows))

    # Review at e567a20, P1: a copied loop marker must not switch a veto off.
    for word in ('BLOCK', 'NOT VERIFIED', 'SURVIVED'):
        c = _world(c=lambda d, w=word: d['comments'][200].append(_comment(
            f'{MARKER}\npr-loop: state=stamped-ready head={head} next=auto-merge\n{w}', author='NONE',
            login='stranger', cid=30, created='2026-11-16T03:00:00Z')))
        code, text = go(c, apply=True)
        check(f'(c) a stranger comment carrying a copied loop marker and {word} still vetoes: NOT armed', not arms(c))
    c = changed_after_read(lambda f: f.comments_by_pr[200].append(_comment(
        f'{MARKER}\nBLOCK', author='NONE', login='stranger', cid=31, created='2026-11-16T03:00:00Z')))
    code, text = go(c, apply=True)
    check('(c) the same forged-marker veto posted after the plan was read: the fresh preflight refuses', not arms(c))
    c = _world(c=lambda d: d['comments'][200].append(_comment(
        f'{MARKER}\npr-loop: state=waiting-stamp head={head} next=verifier\n', author='NONE', login=BOT_LOGIN,
        cid=32, created='2026-11-16T03:00:00Z')))
    code, text = go(c, apply=True)
    check('(c) the loop\'s own marker comment (bot author, ledger shape) is not a veto: armed',
          arms(c) == [('arm', 200, head)])

    # Review at e567a20, P2: a veto in the same second as the stamp comment must not fail open.
    for word in ('BLOCK', 'NOT VERIFIED', 'SURVIVED'):
        c = _world(c=lambda d, w=word: d['comments'][200].append(_comment(
            f'CODEX REVIEW {w} — {head}', cid=40, created='2026-11-16T02:00:00Z')))
        code, text = go(c, apply=True)
        check(f'(c) {word} created in the same second as the stamp comment, with a later id: NOT armed', not arms(c))
    c = _world(c=lambda d: d['comments'][200].insert(0, _comment(
        'CODEX REVIEW BLOCK — an older head', cid=9, created='2026-11-16T02:00:00Z')))
    code, text = go(c, apply=True)
    check('(c) BLOCK created in the same second but with an EARLIER id (posted before the stamp): armed',
          arms(c) == [('arm', 200, head)])
    c = _world(c=lambda d: d['comments'][200].append(dict(_comment(
        'edited to BLOCK', cid=7, created='2026-11-16T01:00:00Z'), updated_at='2026-11-16T02:00:00Z')))
    code, text = go(c, apply=True)
    check('(c) an older comment edited in the same second as the stamp to say BLOCK (order unknown): NOT armed',
          not arms(c))

    # Review at e567a20, P2: the base allowlist itself, through the command line.
    def cli(argv, client):
        lines = []
        try:
            import contextlib
            import io
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                code = main(argv, client_factory=lambda repo: client)
            return code, buf.getvalue()
        except Exception as error:
            return f'crash {type(error).__name__}', '\n'.join(lines)
    for other in ('main', 'qa/other'):
        c = _world(pr={'baseRefName': other})
        code, text = cli(['--base', other, '--apply'], c)
        check(f'CLI --base {other} --apply on a PR that is stamped-ready against {other}: refused (exit 2), '
              'nothing read or written', code == 2 and not c.writes and not c.reads)
    c = _world()
    code, text = cli(['--apply'], c)
    check('CLI default base --apply on a stamped-ready lane/face PR: armed once (the fake stands in for gh)',
          code == 0 and arms(c) == [('arm', 200, head)])

    # Review at a3bdbe0, P1: ONE redaction for ALL output, driven by key_scan's own shapes plus sensitive labels.
    # Every simulated value is built here at run time; none is stored key-shaped in this file, and none is printed.
    import subprocess as _sp_redact
    a_, b_ = 'A1' * 40, 'Zq' * 40
    shapes = {
        'OpenAI/Anthropic API key': 's' + 'k-' + 'proj-' + a_[:28],
        'GitHub token': 'gh' + 'p_' + a_[:38],
        'GitHub fine-grained token': 'github' + '_pat_' + a_[:64],
        'Google API key': 'AI' + 'za' + b_[:35],
        'AWS access key id': 'AK' + 'IA' + 'QWERTYUIOPASDFGH',
        'private key block': '-----BEGIN ' + 'RSA PRIVATE' + ' KEY-----',
        'Slack token': 'xo' + 'xb-' + '1234567890-abcdef',
        'Tailscale key': 'ts' + 'key-' + 'abcd1234' * 3,
        'Hugging Face token': 'h' + 'f_' + a_[:36],
        'JSON Web Token': 'ey' + 'J' + b_[:14] + '.ey' + 'J' + a_[:14] + '.' + b_[:14],
        'bearer token': 'Bear' + 'er ' + a_[:40],
    }
    labelled = {f'{label}{sep}': f'zvq{n}PLAINVALUE{n}' for n, (label, sep) in enumerate([
        ('API key', ': '), ('api-key', '='), ('API_KEY', ': '), ('Authorization', ': Basic '),
        ('authorization', ': token '), ('token', ': '), ('Secret', ' = '), ('password', ': '),
        ('Cookie', ': sid='), ('Set-Cookie', ': id=')])}
    try:
        known = _key_scan_shapes()
        check('redaction: the patterns come from key_scan.py itself, and every key_scan shape has a test value '
              'that key_scan matches', {lbl for lbl, _ in known} == set(shapes)
              and all(rx.search(shapes[lbl]) for lbl, rx in known))
    except Exception as error:
        check(f'redaction: key_scan shapes loadable ({type(error).__name__})', False)

    secrets = [(f'shape {k}', v, v) for k, v in shapes.items()] + \
              [(f'label {k.strip()}', f'{k}{v}', v) for k, v in labelled.items()]

    def captured(fn):
        import contextlib
        import io
        o, e = io.StringIO(), io.StringIO()
        try:
            with contextlib.redirect_stdout(o), contextlib.redirect_stderr(e):
                code = fn()
        except Exception as error:
            code = f'crash {type(error).__name__}'
        return code, o.getvalue() + e.getvalue()

    def leaks(texts, value):
        return any(value in t for t in texts)

    def comment_payloads(c):
        return [w[2] for w in c.writes if w[0] in ('create_comment', 'edit_comment')]

    paths = {}
    for name, text, value in secrets:
        # 1. gh error text (stderr of a failing gh call), through the real GhClient and main().
        def runner(args, _t=text, **kwargs):
            return _sp_redact.CompletedProcess(args, 1, '', f'gh: Bad credentials (HTTP 401)\n{_t}\n')
        code, out_text = captured(lambda: main(['--dry-run'], client_factory=lambda repo: GhClient(repo, runner)))
        paths.setdefault('gh error text', []).append(
            not leaks([out_text], value) and 'Bad credentials (HTTP 401)' in out_text and code == 1)
        # 2./3. a failing check run name and a failing status context/description, dry run and apply.
        for label, edit in (('check name', lambda d, _t=text: d['checks'][head].append(
                                {'id': 10 ** 8, 'name': f'build {_t}', 'status': 'completed',
                                 'conclusion': 'failure'})),
                            ('status context/description', lambda d, _t=text: d['statuses'][head].append(
                                {'id': 6, 'context': f'lint {_t}', 'description': _t, 'state': 'failure',
                                 'created_at': '2026-11-16T00:00:00Z'}))):
            for apply in (False, True):
                c = _world(k=edit)
                code, out_text = captured(lambda: main(['--apply'] if apply else ['--dry-run'],
                                                       client_factory=lambda repo: c))
                keep = 'build' if label == 'check name' else 'lint'
                payloads = comment_payloads(c)
                paths.setdefault(f'{label} ({"apply, marker comment" if apply else "dry-run plan"})', []).append(
                    not leaks([out_text, *payloads], value) and keep in out_text and 'failure' in out_text
                    and (not apply or (bool(payloads) and keep in payloads[0])))
        # 4. a comment read back: a veto comment whose author login and body carry the value.
        c = _world(c=lambda d, _t=text: d['comments'][200].append(_comment(
            f'BLOCK {_t}', author='NONE', login=f'x {_t}', cid=50, created='2026-11-16T03:00:00Z')))
        code, out_text = captured(lambda: main(['--apply'], client_factory=lambda repo: c))
        paths.setdefault('comment read back (veto author/body), marker comment', []).append(
            not leaks([out_text, *comment_payloads(c)], value) and 'state=blocked' in out_text)
        # 5. an exception message raised while reading.
        c = _world()
        c.fail, c.fail_text = 'comments', f'reading comments broke: {text}'
        code, out_text = captured(lambda: main(['--dry-run'], client_factory=lambda repo: c))
        paths.setdefault('exception message', []).append(
            code == 1 and not leaks([out_text], value) and 'reading comments broke' in out_text)
    for path, oks in paths.items():
        check(f'redaction end to end: none of {len(secrets)} simulated secrets reaches the output via {path}; '
              'the non-sensitive context stays', len(oks) == len(secrets) and all(oks))

    # Each layer on its own (so no layer can be dropped because another one still covers it).
    spaced = f'API key: {labelled["API key: "]}'
    check('redaction layer: the gh stderr line filter drops a spaced "API key:" line on its own',
          labelled['API key: '] not in redact(f'gh: failed\n{spaced}\n') and 'gh: failed' in redact('gh: failed'))
    code, text = captured(lambda: emit(f'ctx {spaced} / {shapes["GitHub token"]}'))
    check('redaction layer: emit() redacts on its own', labelled['API key: '] not in text
          and shapes['GitHub token'] not in text and 'ctx' in text)
    lines = []
    c = _world(k=lambda d: d['checks'][head].append({'id': 10 ** 8, 'name': f'build {spaced}', 'status': 'completed',
                                                    'conclusion': 'failure'}))
    run(c, apply=False, out=lines.append)
    check('redaction layer: run() redacts every line before handing it to any output function',
          not leaks(lines, labelled['API key: ']) and any('build' in line for line in lines))

    try:
        sink = _ListSink()
        stream = RedactingStream(sink)
        stream.feed(f'note {shapes["GitHub token"]} and API key: {labelled["API key: "]}\n')
        check('redaction: the stdout/stderr wrapper installed at startup redacts too (defence in depth)',
              shapes['GitHub token'] not in sink.text and labelled['API key: '] not in sink.text
              and 'note' in sink.text)
    except Exception:
        check('redaction: the stdout/stderr wrapper installed at startup redacts too (defence in depth)', False)

    import ast
    try:
        with open(os.path.abspath(__file__), encoding='utf-8') as handle:
            tree = ast.parse(handle.read())
        allowed = {'emit', 'RedactingStream', '_install_redacting_streams', '_ListSink'}
        bad = []

        def visit(node, owners):
            for child in ast.iter_child_nodes(node):
                inner = owners | ({child.name} if isinstance(child, (ast.FunctionDef, ast.ClassDef)) else set())
                hit = (isinstance(child, ast.Name) and child.id == 'print') or (
                    isinstance(child, ast.Attribute) and child.attr in ('write', 'writelines')) or (
                    isinstance(child, ast.Attribute) and child.attr in ('stdout', 'stderr')
                    and isinstance(child.value, ast.Name) and child.value.id == 'sys')
                if hit and not (inner & allowed):
                    bad.append(getattr(child, 'lineno', 0))
                visit(child, inner)
        visit(tree, set())
        check('redaction: no print / write / sys.stdout / sys.stderr outside the one choke point '
              f'(emit, RedactingStream){" - found at lines " + ", ".join(map(str, sorted(set(bad)))) if bad else ""}',
              not bad)
    except Exception as error:
        check(f'redaction: structural scan ran ({type(error).__name__})', False)

    # Review at 7f251f4, P1: a whole private-key block (BEGIN ... END, across lines) never reaches any output.
    pem_line = 'MIIEpQ' + 'zvqPEMBODY' + 'Lq9' * 12
    pem_full = ('-----BEGIN ' + 'RSA PRIVATE' + ' KEY-----\n' + pem_line + '\n' + pem_line[::-1] + '\n'
                + '-----END ' + 'RSA PRIVATE' + ' KEY-----')
    pem_open = pem_full.split('\n-----END')[0]

    def pem_leaks(texts):
        return any(s in t for t in texts for s in (pem_line, pem_line[::-1], '-----END'))

    for kind, block in (('complete', pem_full), ('incomplete (no END line)', pem_open)):
        for apply in (False, True):
            c = _world(k=lambda d, _b=block: d['checks'][head].append(
                {'id': 10 ** 8, 'name': f'build {_b}', 'status': 'completed', 'conclusion': 'failure'}))
            code, text = captured(lambda: main(['--apply'] if apply else ['--dry-run'], client_factory=lambda r: c))
            payloads = comment_payloads(c)
            check(f'PEM: a {kind} PEM block in a check name never reaches the '
                  f'{"output or the posted marker comment (apply)" if apply else "dry-run plan"}; context stays',
                  not pem_leaks([text, *payloads]) and 'build' in text and 'failure' in text
                  and (not apply or bool(payloads)))

        def pem_runner(args, _b=block, **kwargs):
            return _sp_redact.CompletedProcess(args, 1, '', f'gh: Bad credentials (HTTP 401)\n{_b}\n')
        code, text = captured(lambda: main(['--dry-run'], client_factory=lambda r: GhClient(r, pem_runner)))
        check(f'PEM: a {kind} PEM block in gh error text never reaches the output',
              code == 1 and not pem_leaks([text]) and 'Bad credentials (HTTP 401)' in text)

    # Review at 7f251f4, P2: a required check must conclude SUCCESS; skipped/neutral on a required name is not green.
    def required_as(conclusion, names=REQUIRED_CHECKS):
        def edit(d):
            for r in d['checks'][head]:
                if r['name'] in names:
                    r['conclusion'] = conclusion
        return edit

    for concl in ('skipped', 'neutral'):
        c = _world(k=required_as(concl))
        code, text = go(c, apply=True)
        check(f'required checks all {concl}: not green, NOT armed', not arms(c) and 'stamped-ready' not in text)
    c = _world(k=required_as('skipped', ('smoke',)))
    code, text = go(c, apply=True)
    check('one required check skipped: NOT armed', not arms(c))
    c = _world()
    code, text = go(c, apply=True)
    check('an OPTIONAL check skipped (github-release) is fine: armed', arms(c) == [('arm', 200, head)])
    for concl in ('skipped', 'neutral'):
        def turn(f, _c=concl):
            for r in f.checks_by_sha[head]:
                if r['name'] in REQUIRED_CHECKS:
                    r['conclusion'] = _c
        c = changed_after_read(turn)
        code, text = go(c, apply=True)
        check(f'preflight: required checks turn {concl} after the plan was read (stamp unchanged): NOT armed',
              not arms(c) and c.statuses_by_sha[head][0]['state'] == 'success')

    # Review at 7f251f4, P2: the veto also covers PR review bodies and inline review comments.
    def review(body, when, rid=900, login='reviewer'):
        return {'id': rid, 'body': body, 'state': 'COMMENTED', 'submitted_at': when, 'last_edited_at': None,
                'author_association': 'NONE', 'user': {'login': login}}

    def inline(body, when, cid=800, updated=None):
        return {'id': cid, 'body': body, 'created_at': when, 'updated_at': updated or when,
                'author_association': 'NONE', 'user': {'login': 'reviewer'}}

    for word in ('BLOCK', 'NOT VERIFIED', 'SURVIVED'):
        c = _world()
        c.reviews_by_pr = {200: [review(f'CODEX REVIEW {word} — {head}', '2026-11-16T03:00:00Z')]}
        code, text = go(c, apply=True)
        check(f'(c) a later PR review body saying {word}: NOT armed', not arms(c) and 'state=blocked' in text)
        c = _world()
        c.inline_by_pr = {200: [inline(f'{word}: this line', '2026-11-16T03:00:00Z')]}
        code, text = go(c, apply=True)
        check(f'(c) a later inline review comment saying {word}: NOT armed', not arms(c) and 'state=blocked' in text)
    c = _world()
    c.reviews_by_pr = {200: [review('CODEX REVIEW BLOCK — old head', '2026-11-16T01:00:00Z')]}
    c.inline_by_pr = {200: [inline('BLOCK: old', '2026-11-16T01:00:00Z')]}
    code, text = go(c, apply=True)
    check('(c) an OLDER review body and an OLDER inline comment do not veto: armed', arms(c) == [('arm', 200, head)])
    c = _world()
    c.reviews_by_pr = {200: [review('BLOCK', '2026-11-16T02:00:00Z', rid=1)]}
    code, text = go(c, apply=True)
    check('(c) a review submitted in the same second as the stamp comment (ids not comparable across kinds): '
          'NOT armed (fail closed)', not arms(c))
    c = _world()
    c.inline_by_pr = {200: [inline('fine', '2026-11-16T01:00:00Z', updated='2026-11-16T03:00:00Z')]}
    c.inline_by_pr[200][0]['body'] = 'edited later: BLOCK'
    code, text = go(c, apply=True)
    check('(c) an older inline comment edited after the stamp to say BLOCK: NOT armed', not arms(c))
    c = _world()
    c.reviews_by_pr = {200: [review('BLOCK', None)]}
    code, text = go(c, apply=True)
    check('(c) a review with no submission time carrying BLOCK: NOT armed (order unknown, fail closed)', not arms(c))
    c = changed_after_read(lambda f: setattr(f, 'reviews_by_pr', {200: [review(
        f'CODEX REVIEW BLOCK — {head}', '2026-11-16T03:00:00Z')]}))
    code, text = go(c, apply=True)
    check('(c) a review BLOCK posted after the plan was read: the fresh preflight refuses', not arms(c))
    c = changed_after_read(lambda f: setattr(f, 'inline_by_pr', {200: [inline('BLOCK', '2026-11-16T03:00:00Z')]}))
    code, text = go(c, apply=True)
    check('(c) an inline BLOCK posted after the plan was read: the fresh preflight refuses', not arms(c))
    for endpoint in ('reviews', 'review_comments'):
        c = _world()
        c.fail = endpoint
        code, text = go(c, apply=True)
        check(f'(c) reading {endpoint} fails: exit 1, no action at all', code == 1 and not c.writes)
        c = _world()
        c.fail = (endpoint, 1)  # the plan's read works; the fresh preflight read fails
        code, text = go(c, apply=True)
        check(f'(c) the fresh preflight read of {endpoint} fails: exit 1, no arm', code == 1 and not arms(c))
    c = _world()
    c.reviews_by_pr = {200: [review(f'BLOCK {shapes["GitHub token"]}', '2026-11-16T03:00:00Z',
                                    login=f'r {shapes["GitHub token"]}')]}
    code, text = captured(lambda: main(['--apply'], client_factory=lambda r: c))
    check('(c) review text and author are redacted on output and in the marker comment',
          not leaks([text, *comment_payloads(c)], shapes['GitHub token']) and 'state=blocked' in text)

    # Review at e32b72e, P2: an older review EDITED after the stamp must veto; a missing edit time fails closed.
    def review_e(body, submitted, edited, rid=901):
        r = review(body, submitted, rid=rid)
        if edited is not Ellipsis:  # Ellipsis = the edit-time field is absent (cannot be read)
            r['last_edited_at'] = edited
        else:
            r.pop('last_edited_at', None)
        return r

    c = _world()
    c.reviews_by_pr = {200: [review_e('CODEX REVIEW BLOCK — late edit', '2026-11-16T01:00:00Z',
                                      '2026-11-16T03:00:00Z')]}
    code, text = go(c, apply=True)
    check('(c) a review submitted BEFORE the stamp but EDITED after it to say BLOCK: NOT armed', not arms(c))
    c = _world()
    c.reviews_by_pr = {200: [review_e('CODEX REVIEW BLOCK — old', '2026-11-16T01:00:00Z', None)]}
    code, text = go(c, apply=True)
    check('(c) control: an older review never edited (lastEditedAt empty) does not veto: armed',
          arms(c) == [('arm', 200, head)])
    c = _world()
    c.reviews_by_pr = {200: [review_e('CODEX REVIEW BLOCK — old', '2026-11-16T01:00:00Z', Ellipsis)]}
    code, text = go(c, apply=True)
    check('(c) an older review whose edit time cannot be read, carrying BLOCK: NOT armed (fail closed)', not arms(c))
    c = changed_after_read(lambda f: setattr(f, 'reviews_by_pr', {200: [review_e(
        'edited to BLOCK', '2026-11-16T01:00:00Z', '2026-11-16T03:00:00Z')]}))
    code, text = go(c, apply=True)
    check('(c) an older review edited to BLOCK after the plan was read: the fresh preflight refuses', not arms(c))

    # The review read itself (GraphQL, with edit times): pagination and errors fail closed; edit times are kept.
    def graphql_runner(reply):
        def runner(args, **kwargs):
            if args[1:3] == ['api', 'graphql'] and any('reviews(' in a for a in args):
                return _sp_redact.CompletedProcess(args, 0, json.dumps(reply), '')
            return _sp_redact.CompletedProcess(args, 1, '', 'not used in this test')
        return runner

    def reviews_reply(nodes, more=False, errors=None):
        reply = {'data': {'repository': {'pullRequest': {'reviews': {
            'pageInfo': {'hasNextPage': more}, 'nodes': nodes}}}}}
        if errors:
            reply['errors'] = errors
        return reply

    node = {'databaseId': 7, 'body': 'BLOCK', 'submittedAt': '2026-11-16T01:00:00Z',
            'lastEditedAt': '2026-11-16T03:00:00Z', 'author': {'login': 'reviewer'}}
    try:
        got = GhClient(REPO, graphql_runner(reviews_reply([node]))).reviews(200)
        ok = got == [{'id': 7, 'body': 'BLOCK', 'user': {'login': 'reviewer'}, 'submitted_at': '2026-11-16T01:00:00Z',
                      'last_edited_at': '2026-11-16T03:00:00Z'}]
    except Exception:
        ok = False
    check('(c) the review read keeps each review\'s edit time (GraphQL lastEditedAt)', ok)
    for label, reply in (('more than one page of reviews', reviews_reply([node], more=True)),
                         ('a GraphQL error', reviews_reply([node], errors=[{'message': 'something broke'}])),
                         ('a reply with no reviews field', {'data': {'repository': {'pullRequest': None}}})):
        try:
            GhClient(REPO, graphql_runner(reply)).reviews(200)
            ok = False
        except ApiError:
            ok = True
        except Exception:
            ok = False
        check(f'(c) the review read fails closed on {label}', ok)

    # Review at e32b72e, P2: only the stamp ISSUE COMMENT is exempt, never a review or inline item with its id.
    stamp_cid = 12
    c = _world()
    c.reviews_by_pr = {200: [review('CODEX REVIEW BLOCK — late', '2026-11-16T03:00:00Z', rid=stamp_cid)]}
    code, text = go(c, apply=True)
    check('(c) a later BLOCK review that has the stamp comment\'s numeric id still vetoes: NOT armed', not arms(c))
    c = _world()
    c.inline_by_pr = {200: [inline('BLOCK here', '2026-11-16T03:00:00Z', cid=stamp_cid)]}
    code, text = go(c, apply=True)
    check('(c) a later BLOCK inline comment that has the stamp comment\'s numeric id still vetoes: NOT armed',
          not arms(c))
    c = changed_after_read(lambda f: setattr(f, 'reviews_by_pr', {200: [review(
        'BLOCK', '2026-11-16T03:00:00Z', rid=stamp_cid)]}))
    code, text = go(c, apply=True)
    check('(c) the same-id BLOCK review posted after the plan was read: the fresh preflight refuses', not arms(c))
    c = changed_after_read(lambda f: setattr(f, 'inline_by_pr', {200: [inline(
        'BLOCK', '2026-11-16T03:00:00Z', cid=stamp_cid)]}))
    code, text = go(c, apply=True)
    check('(c) the same-id BLOCK inline comment posted after the plan was read: the fresh preflight refuses',
          not arms(c))

    import ast
    # Review at 25f6a97, P2: strict validation at the API boundary, tested THROUGH the real GhClient: a raw-JSON
    # gh stand-in serves the planted world exactly as gh would (JSON text), and GhClient + the adapters parse it.
    def raw_runner(world):
        def run_gh(args, **kwargs):
            a = args[1:]
            try:
                out = None
                if a[:2] == ['pr', 'list']:
                    state = a[a.index('--state') + 1]
                    out = world.open_prs() if state == 'open' else world.merged_prs(a[a.index('--base') + 1])
                elif a[:2] == ['pr', 'view']:
                    out = world.pr(int(a[2]))
                elif a[:2] == ['pr', 'merge']:
                    world.arm(int(a[2]), a[a.index('--match-head-commit') + 1])
                elif a[:2] == ['workflow', 'run']:
                    world.dispatch(a[2], a[a.index('--ref') + 1])
                elif a[:2] == ['api', 'graphql']:
                    query = [x for x in a if x.startswith('query=')][0]
                    if 'reviews(' in query:
                        number = int([x for x in a if x.startswith('pr=')][0][3:])
                        nodes = [_review_node(r) for r in world.reviews(number)]
                        out = {'data': {'repository': {'pullRequest': {'reviews': {
                            'pageInfo': {'hasNextPage': False}, 'nodes': nodes}}}}}
                    else:
                        out = {'data': {'repository': {'autoMergeAllowed': world.repo_settings()['allow_auto_merge']}}}
                elif a[:2] == ['api', '-X']:
                    body = json.loads(kwargs['input'])['body']
                    if a[2] == 'POST':
                        world.create_comment(int(a[3].split('/')[-2]), body)
                    else:
                        world.edit_comment(int(a[3].split('/')[-1]), body)
                    out = {}
                elif a[0] == 'api':
                    path = a[3] if a[1] == '--paginate' else a[1]
                    rest = path.split('?')[0].split('/')[3:]
                    if not rest:
                        allow = world.repo_settings()['allow_auto_merge']
                        out = {'allow_auto_merge': allow} if isinstance(allow, bool) else {}
                    elif rest[0] == 'commits' and len(rest) == 2:
                        out = {'sha': world.base_sha(rest[1])}
                    elif rest[0] == 'issues':
                        out = [world.comments(int(rest[1]))]
                    elif rest[0] == 'pulls':
                        out = [world.review_comments(int(rest[1]))]
                    elif rest[0] == 'commits' and rest[2] == 'check-runs':
                        runs = world.check_runs(rest[1])
                        out = [{'total_count': len(runs), 'check_runs': runs}]
                    elif rest[0] == 'commits' and rest[2] == 'statuses':
                        out = [world.statuses(rest[1])]
                    elif rest[0] == 'compare':
                        base_sha, head_sha = rest[1].split('...')
                        out = {'behind_by': world.behind_by(base_sha, head_sha)}
                    elif rest[0] == 'actions':
                        sha = path.split('head_sha=')[1].split('&')[0]
                        out = {'n': world.workflow_runs(rest[2], sha)}
                    else:
                        return _sp_redact.CompletedProcess(args, 1, '', f'raw stand-in: no route for {path}')
                else:
                    return _sp_redact.CompletedProcess(args, 1, '', 'raw stand-in: no route')
            except ApiError as error:
                return _sp_redact.CompletedProcess(args, 1, '', str(error))
            return _sp_redact.CompletedProcess(args, 0, '' if out is None else json.dumps(out), '')
        return run_gh

    def raw_go(c, apply=True):
        lines = []
        try:
            code = run(GhClient(REPO, raw_runner(c)), apply=apply, out=lines.append)
        except Exception as error:
            lines.append(f'CRASH {type(error).__name__}')
            code = 'crash'
        return code, '\n'.join(lines)

    c = _world()
    code, text = raw_go(c)
    check('raw adapter path: an ordinary stamped-ready PR served as raw JSON through GhClient is armed once '
          '(the stand-in works)', code == 0 and arms(c) == [('arm', 200, head)])

    def raw_review(edited):
        r = review('CODEX REVIEW BLOCK — old head', '2026-11-16T01:00:00Z', rid=902)
        if edited is Ellipsis:
            r.pop('last_edited_at', None)
        else:
            r['last_edited_at'] = edited
        return r

    for label, edited in (('lastEditedAt missing', Ellipsis), ('lastEditedAt malformed', 'not-a-time')):
        c = _world()
        c.reviews_by_pr = {200: [raw_review(edited)]}
        code, text = raw_go(c)
        check(f'raw adapter: an older BLOCK review with {label}: exit 1, no action at all', code == 1 and not c.writes)
        c = changed_after_read(lambda f, _e=edited: setattr(f, 'reviews_by_pr', {200: [raw_review(_e)]}))
        code, text = raw_go(c)
        check(f'raw adapter: {label} appears after the plan was read: the preflight read fails closed, no arm, '
              'exit 1', code == 1 and not arms(c))
    c = _world()
    c.reviews_by_pr = {200: [raw_review(None)]}
    code, text = raw_go(c)
    check('raw adapter control: an older BLOCK review with lastEditedAt explicitly null (never edited) does not '
          'veto: armed', code == 0 and arms(c) == [('arm', 200, head)])

    def set_field(path_fn):
        def edit(d):
            path_fn(d)
        return edit

    malformed = {
        'issue comment created_at': lambda d: d['comments'][200][0].__setitem__('created_at', 'yesterday'),
        'issue comment updated_at missing': lambda d: d['comments'][200][0].pop('updated_at'),
        'status created_at': lambda d: d['statuses'][head][0].__setitem__('created_at', '2026-11-16 01:00:00'),
        'status updated_at': lambda d: d['statuses'][head][0].__setitem__('updated_at', '2026-13-40T99:00:00Z'),
        'check run completed_at': lambda d: d['checks'][head][0].__setitem__('completed_at', 'soon'),
        'check run id a string': lambda d: d['checks'][head][0].__setitem__('id', '7'),
        'PR isDraft missing': lambda d: d['prs'][0].pop('isDraft'),
        'PR mergeable not text': lambda d: d['prs'][0].__setitem__('mergeable', True),
    }
    for label, edit in malformed.items():
        c = _world(m=edit)
        code, text = raw_go(c)
        check(f'raw adapter: malformed {label}: exit 1, no action at all, the run says the data is malformed',
              code == 1 and not c.writes and 'malformed' in text)
    c = _world()
    c.inline_by_pr = {200: [inline('fine', '2026-11-16T01:00:00Z', updated='11/16/2026')]}
    code, text = raw_go(c)
    check('raw adapter: malformed inline comment updated_at: exit 1, no action at all', code == 1 and not c.writes)
    c = _world(s=lambda d: d['statuses'].update({head: []}))
    c.merged = [{'number': 150, 'mergeCommit': {'oid': c.tip}, 'mergedBy': {'login': 'x'}}]
    code, text = raw_go(c)
    check('raw adapter: a merged PR whose mergedBy has no is_bot: exit 1, no action at all',
          code == 1 and not c.writes)
    c = _world(k=lambda d: d['checks'][head].append({'id': 5, 'name': 'extra', 'status': 'completed',
                                                    'conclusion': None, 'completed_at': '2026-11-16T00:00:00Z'}))
    code, text = raw_go(c)
    check('raw adapter: a completed check run with no conclusion: exit 1, no action at all',
          code == 1 and not c.writes)
    try:
        ok = (parse_time('2026-11-16T03:00:00Z', 'x') == '2026-11-16T03:00:00Z'
              and parse_time('2026-11-16T03:00:00.123Z', 'x') == '2026-11-16T03:00:00Z')
        for bad in ('not-a-time', '2026-11-16T03:00:00', '2026-11-16T03:00:00+00:00', '2026-02-30T00:00:00Z', 5):
            try:
                parse_time(bad, 'x')
                ok = False
            except ApiError:
                pass
    except Exception:
        ok = False
    check('the one time parser accepts ISO-8601 UTC only (no offset-less, no other zone, no impossible date)', ok)

    try:
        with open(os.path.abspath(__file__), encoding='utf-8') as handle:
            tree = ast.parse(handle.read())
        allowed_get = {'gh_env', 'main', 'self_test', 'FakeClient', '_world', '_comment'}
        bad = []

        def visit_get(node, owners):
            for child in ast.iter_child_nodes(node):
                inner = owners | ({child.name} if isinstance(child, (ast.FunctionDef, ast.ClassDef)) else set())
                if isinstance(child, ast.Call) and isinstance(child.func, ast.Attribute) and child.func.attr == 'get' \
                        and not (inner & allowed_get):
                    bad.append(child.lineno)
                visit_get(child, inner)
        visit_get(tree, set())
        check('API boundary: no `.get(` on data outside the accessors (only gh_env, main and the test code may use '
              f'it){" - found at lines " + ", ".join(map(str, sorted(set(bad)))) if bad else ""}', not bad)
    except Exception as error:
        check(f'API boundary: structural scan ran ({type(error).__name__})', False)

    # Log safety: gh's own output can carry request headers; the loop must never pass them on.
    import subprocess as _sp
    fake_key = 'gh' + 'p_' + 'Q7' * 18  # built at run time, never stored key-shaped in this file; never printed
    noisy = ('GH_DEBUG', 'GIT_TRACE', 'GIT_TRACE_CURL', 'GIT_CURL_VERBOSE', 'DEBUG')
    seen = {}

    def leaky_gh(args, **kwargs):
        seen['env'], seen['args'] = kwargs.get('env'), args
        return _sp.CompletedProcess(args, 1, '', f'* Request to api.github.com\n> Authorization: token {fake_key}\n'
                                                 f'> authorization: Bearer {fake_key}\nX-Token: {fake_key}\n'
                                                 f'gh: Bad credentials (HTTP 401)\n{fake_key}\n')
    try:
        lines = []
        code = run(GhClient(REPO, runner=leaky_gh), apply=False, out=lines.append)
        text = '\n'.join(lines)
        ok = (code == 1 and fake_key not in text and 'uthorization' not in text and 'Bearer' not in text
              and 'Bad credentials (HTTP 401)' in text)
    except Exception as error:
        ok, text = False, f'crash {type(error).__name__}'
    check('log safety: a gh failure whose stderr holds an Authorization header and a token is reported WITHOUT them '
          '(the rest of the message stays)', ok)

    saved = {k: os.environ.get(k) for k in noisy}
    try:
        os.environ.update({k: '1' for k in noisy})
        seen.clear()
        try:
            GhClient(REPO, runner=leaky_gh).repo_settings()
        except ApiError:
            pass
        env = seen.get('env')
        ok = isinstance(env, dict) and not [k for k in noisy if k in env] and 'PATH' in env
    except Exception:
        ok = False
    finally:
        for k, v in saved.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v
    check('log safety: the environment passed to gh has no GH_DEBUG / GIT_TRACE* / GIT_CURL_VERBOSE / DEBUG even when '
          'the parent sets them', ok)

    try:
        seen.clear()
        GhClient(REPO, runner=leaky_gh)._gh(['api', '--verbose', 'repos/x/y'])
        ok = False
    except ApiError:
        ok = 'args' not in seen
    except Exception:
        ok = False
    check('log safety: a verbose/debug flag on a gh call is refused before gh runs', ok)

    check('log safety: the workflow captures the loop output and key-scans it (with the job token as a secret) '
          'before the job ends',
          'tee -a pr-loop-log/' in loop_yml
          and 'python -I scripts/quality/key_scan.py --secret-env GH_TOKEN pr-loop-log' in loop_yml)

    # Dry run (the default) plans and prints but performs nothing.
    c = _world()
    code, text = go(c, apply=False)
    check('dry run prints the plan (arm would happen) and performs nothing',
          code == 0 and not c.writes and 'DRY RUN' in text and 'arm' in text)
    c = _world(s=lambda d: d['statuses'].update({head: []}))
    code, text = go(c, apply=False)
    check('dry run on an unstamped PR says: no action (no codex-verified stamp)',
          code == 0 and not c.writes and 'no codex-verified stamp' in text)

    # Fail closed on any API error: no action at all.
    for method in ('repo_settings', 'base_sha', 'open_prs', 'comments', 'check_runs', 'statuses', 'behind_by'):
        c = _world(f=lambda d, m=method: d.__setitem__('fail', m))
        code, text = go(c, apply=True)
        check(f'API error in {method}: exit 1, no action, a clear message', code == 1 and not c.writes
              and 'fail' in text.lower())
    c = _world(f=lambda d: d.__setitem__('fail', 'arm'))
    code, text = go(c, apply=True)
    check('API error while arming: exit 1 with a clear message', code == 1 and 'arm' in text)

    failed = [n for n, ok in results if not ok]
    emit(f'\n{len(results) - len(failed)}/{len(results)} self-test cases passed')
    return 1 if failed else 0


def main(argv, client_factory=GhClient):
    args = list(argv)
    if args == ['--self-test']:
        return self_test()
    opts = {'--repo': REPO, '--base': BASE, '--bot-login': BOT_LOGIN}
    apply = False
    while args:
        arg = args.pop(0)
        if arg == '--apply':
            apply = True
        elif arg == '--dry-run':
            apply = False
        elif arg in opts and args:
            opts[arg] = args.pop(0)
        else:
            emit(__doc__)
            return 2
    if opts['--base'] not in ALLOWED_BASES:
        emit(f'::error::{RULE} --base must be one of {", ".join(ALLOWED_BASES)} (got {opts["--base"]})')
        return 2
    try:
        return run(client_factory(opts['--repo']), apply=apply, base=opts['--base'], bot_login=opts['--bot-login'])
    except ApiError as error:
        emit(f'::error::{RULE} fail closed, no action taken: {error}')
        return 1


if __name__ == '__main__':
    _install_redacting_streams()
    sys.exit(main(sys.argv[1:]))
