"""Trust metrics for Boo from git history and GitHub PR / Actions history (agent-trust plan, section 4.3, PR 1.12).

  python -I scripts/quality/trust_metrics.py --since 2026-11-08T00:00:00Z --until 2026-11-13T06:00:00Z \
      [--repo vikas53953/bluey] [--remote origin] [--save-snapshot snap.json] [--out table.md]
  python -I scripts/quality/trust_metrics.py --from-snapshot snap.json --since ... --until ... [--out table.md]
  python -I scripts/quality/trust_metrics.py --self-test

Read-only. It only runs `gh api` GET requests and `git log` / `git show` / `git rev-list`; it never reads a secret
(the `gh` login or the workflow's GITHUB_TOKEN is used by `gh` itself, never by this script).

Reproducible:
  * the window is two fixed UTC times passed as arguments ([since, until), until excluded); nothing uses "now";
  * every `gh` / `git` command is printed to stderr, exactly as run, before it runs; the table's appendix lists each
    command shape with how many times it ran;
  * all raw data goes into one JSON snapshot (sorted keys); every number is computed from the snapshot alone, so
    `--from-snapshot` gives the same table byte for byte, and the table prints the snapshot's sha256;
  * all lists are sorted before printing.
GitHub data can still change after the window (a run re-run later, a PR description edited): a later fetch can then
differ, and the snapshot sha256 shows that it did.

Definitions (each also printed in the table):
  * base branches: lane/face and main (--branches). A PR counts when its base is one of them.
  * a run belongs to a PR when GitHub lists the PR on the run, or the run's branch is the PR's branch and it started
    while the PR was open.
  * verdict lines are read from the PR description and its comments. `QA PASS` / `QA FAIL` / `REVIEW FAIL` followed by
    a commit id (7-40 hex with at least one letter, so run ids never count) on the same line. A FAIL line that says "blocked" is a blocked round (storage, budget),
    not a reviewer FAIL. Rounds are counted once per (kind, commit). Descriptions are often rewritten, so FAIL rounds
    are a LOWER bound.
  * flaky: the same workflow on the same commit gave both success and failure (re-run attempts included, push and
    pull_request runs of one commit included). Rate = flaky (workflow, commit) pairs / pairs with any success or
    failure. Cancelled and skipped results are ignored.
"""
import argparse
import collections
import datetime as dt
import hashlib
import json
import re
import shlex
import statistics
import subprocess
import sys
import urllib.parse

QUERIES = []          # every command run, in order (exact text)
QUERY_SHAPES = collections.Counter()   # command with ids replaced, for the appendix

SHA_RE = r'\b(?=[0-9a-f]*[a-f])([0-9a-f]{7,40})\b'   # a commit id has a letter, so run ids never match
VERDICT_GAP = r'[:*\s]{1,6}'   # "QA PASS", "QA: **PASS**", "QA:PASS" are all written in PR history
QA_PASS = re.compile(r'\bQA' + VERDICT_GAP + r'PASS\**\W{1,6}(?:(?:at|head|on)\s+)*`?' + SHA_RE)
FAIL_LINE = re.compile(r'\b(QA|REVIEW)' + VERDICT_GAP + r'FAIL\b(.*)')
LIVE_WORKFLOW = 'live-ai-smoke'
UNVERIFIED = 'UNVERIFIED'


# ---------------------------------------------------------------- time helpers

def parse_time(text):
    text = text.strip()
    if re.fullmatch(r'\d{4}-\d{2}-\d{2}', text):
        text += 'T00:00:00Z'
    if text.endswith('Z'):
        text = text[:-1] + '+00:00'
    value = dt.datetime.fromisoformat(text)
    if value.tzinfo is None:
        raise ValueError(f'time needs a zone (use Z for UTC): {text}')
    return value.astimezone(dt.timezone.utc)


def iso(value):
    return value.strftime('%Y-%m-%dT%H:%M:%SZ')


def in_window(text, since, until):
    return bool(text) and since <= parse_time(text) < until


def hours(a, b):
    return (parse_time(b) - parse_time(a)).total_seconds() / 3600


# ---------------------------------------------------------------- fetching (read-only)

def shape_of(args):
    text = ' '.join(args)
    text = re.sub(r'/runs/\d+', '/runs/{id}', text)
    text = re.sub(r'/attempts/\d+', '/attempts/{n}', text)
    text = re.sub(r'/commits/[0-9a-f]{40}', '/commits/{sha}', text)
    text = re.sub(r'created=[^&\s\']+', 'created={day}', text)
    return text


def run(args, allow_fail=False):
    printable = ' '.join(shlex.quote(a) for a in args)
    QUERIES.append(printable)
    QUERY_SHAPES[shape_of(args)] += 1
    print('+ ' + printable, file=sys.stderr)
    proc = subprocess.run(args, capture_output=True, text=True, encoding='utf-8')
    if proc.returncode != 0:
        if allow_fail:
            return None
        raise SystemExit(f'command failed ({proc.returncode}): {printable}\n{proc.stderr.strip()}')
    return proc.stdout


def gh_lines(path, jq):
    out = run(['gh', 'api', '--paginate', path, '--jq', jq])
    return [json.loads(line) for line in out.splitlines() if line.strip()]


def gh_one(path, jq=None, allow_fail=False):
    args = ['gh', 'api', path] + (['--jq', jq] if jq else [])
    out = run(args, allow_fail=allow_fail)
    return None if out is None else json.loads(out)


def fetch(repo, since, until, branches, remote):
    snap = {'repo': repo, 'branches': sorted(branches), 'since': iso(since), 'until': iso(until)}

    snap['pulls'] = sorted(gh_lines(
        f'repos/{repo}/pulls?state=all&per_page=100',
        '.[] | {number, base: .base.ref, head_ref: .head.ref, head_sha: .head.sha, created_at, merged_at, '
        'closed_at, body: (.body // "")}'), key=lambda p: p['number'])
    numbers = {p['number'] for p in snap['pulls']}
    snap['comments'] = sorted((c for c in gh_lines(
        f'repos/{repo}/issues/comments?per_page=100',
        '.[] | {id, issue: (.issue_url | split("/") | last | tonumber), created_at, body: (.body // "")}')
        if c['issue'] in numbers), key=lambda c: c['id'])
    # Keep only the verdict lines: the snapshot is meant to be shared, and no metric reads any other text.
    for item in snap['pulls'] + snap['comments']:
        item['body'] = verdict_lines(item['body'])
    snap['comments'] = [c for c in snap['comments'] if c['body']]

    runs = {}
    day = since.replace(hour=0, minute=0, second=0, microsecond=0)
    while day < until:
        end = day + dt.timedelta(days=1) - dt.timedelta(seconds=1)
        created = urllib.parse.quote(f'{iso(day)}..{iso(end)}', safe='')
        rows = gh_lines(
            f'repos/{repo}/actions/runs?per_page=100&created={created}',
            '.workflow_runs[] | {id, name, event, head_sha, head_branch, status, conclusion, run_attempt, '
            'created_at, updated_at, prs: [.pull_requests[].number]}')
        if len(rows) >= 1000:
            raise SystemExit(f'GitHub caps a filtered run list at 1000; {iso(day)} hit it. Split the day.')
        for row in rows:
            runs[row['id']] = row
        day += dt.timedelta(days=1)
    snap['runs'] = sorted((r for r in runs.values() if in_window(r['created_at'], since, until)),
                          key=lambda r: r['id'])

    # Earlier attempts of re-run runs, then the jobs of every failed attempt (for "failures by job").
    attempts = []
    for r in snap['runs']:
        attempts.append({'run': r['id'], 'attempt': r['run_attempt'], 'conclusion': r['conclusion']})
        for n in range(1, r['run_attempt']):
            a = gh_one(f'repos/{repo}/actions/runs/{r["id"]}/attempts/{n}', '{conclusion}')
            attempts.append({'run': r['id'], 'attempt': n, 'conclusion': a['conclusion']})
    snap['attempts'] = sorted(attempts, key=lambda a: (a['run'], a['attempt']))
    jobs = []
    for a in snap['attempts']:
        if a['conclusion'] != 'failure':
            continue
        for j in gh_lines(f'repos/{repo}/actions/runs/{a["run"]}/attempts/{a["attempt"]}/jobs?per_page=100',
                          '.jobs[] | {name, conclusion}'):
            jobs.append({'run': a['run'], 'attempt': a['attempt'], 'name': j['name'], 'conclusion': j['conclusion']})
    snap['failed_attempt_jobs'] = sorted(jobs, key=lambda j: (j['run'], j['attempt'], j['name']))

    # Required checks (needs a token that may read branch protection; the workflow token cannot -> UNVERIFIED).
    required = {}
    for b in sorted(branches):
        got = gh_one(f'repos/{repo}/branches/{urllib.parse.quote(b, safe="")}/protection/required_status_checks',
                     '{contexts}', allow_fail=True)
        required[b] = sorted(got['contexts']) if got else None
    snap['required_checks'] = required

    # Time to green: check runs on the head each merged PR was merged at.
    heads = {}
    for p in snap['pulls']:
        if p['base'] in branches and in_window(p['merged_at'], since, until):
            heads[p['head_sha']] = sorted(gh_lines(
                f'repos/{repo}/commits/{p["head_sha"]}/check-runs?per_page=100',
                '.check_runs[] | {name, conclusion, completed_at}'),
                key=lambda c: (c['name'], c['completed_at'] or '', c['conclusion'] or ''))
    snap['head_check_runs'] = heads

    got = gh_one(f'repos/{repo}/labels/escaped', '{name}', allow_fail=True)
    snap['escaped_label_exists'] = got is not None
    snap['escaped_issues'] = sorted(gh_lines(
        f'repos/{repo}/issues?labels=escaped&state=all&per_page=100', '.[] | {number, created_at}'),
        key=lambda i: i['number']) if got else []

    # git: reverts on the base branches, and the lane/face commit at the window's end for baseline + feature map.
    reverts = {}
    for b in sorted(branches):
        out = run(['git', 'log', f'{remote}/{b}', f'--since={iso(since)}', f'--until={iso(until)}',
                   '-i', '--grep=^Revert ', '--grep=This reverts commit', '--format=%H%x09%cI%x09%s'])
        for line in out.splitlines():
            sha, when, subject = line.split('\t', 2)
            reverts[sha] = {'sha': sha, 'committed': when, 'subject': subject}
    snap['reverts'] = sorted(reverts.values(), key=lambda r: r['sha'])
    main_line = 'lane/face' if 'lane/face' in branches else sorted(branches)[0]
    ref = run(['git', 'rev-list', '-1', '--first-parent', f'--before={iso(until)}', f'{remote}/{main_line}']).strip()
    snap['end_ref'] = {'branch': main_line, 'sha': ref}
    snap['baseline_json'] = run(['git', 'show', f'{ref}:docs/quality/baseline.json'], allow_fail=True)
    snap['feature_map_md'] = run(['git', 'show', f'{ref}:docs/FEATURE_MAP.md'], allow_fail=True)
    return snap


# ---------------------------------------------------------------- computing (pure, from the snapshot only)

def verdict_lines(text):
    return '\n'.join(line for line in text.splitlines() if QA_PASS.search(line) or FAIL_LINE.search(line))


def verdict_text(pr, comments):
    parts = [pr['body']] + [c['body'] for c in comments if c['issue'] == pr['number']]
    return '\n'.join(parts)


def fail_rounds(text):
    """Return (reviewer FAIL rounds, blocked rounds) as sets of (kind, commit-prefix-7)."""
    fails, blocked = set(), set()
    for line in text.splitlines():
        for m in FAIL_LINE.finditer(line):
            sha = re.search(SHA_RE, m.group(2))
            if not sha:
                continue
            key = (m.group(1), sha.group(1)[:7])
            (blocked if 'blocked' in m.group(2).lower() else fails).add(key)
    return fails, blocked


def qa_pass_shas(text):
    return sorted({m.group(1) for m in QA_PASS.finditer(text)})


def pct(n, d):
    return 'n/a (0 in window)' if d == 0 else f'{n}/{d} = {100 * n / d:.1f}%'


def quantile(values, q):
    ordered = sorted(values)
    idx = max(0, min(len(ordered) - 1, round(q * (len(ordered) - 1))))
    return ordered[idx]


def runs_of_pr(pr, runs):
    end = pr['merged_at'] or pr['closed_at']
    out = []
    for r in runs:
        if pr['number'] in r['prs']:
            out.append(r)
        elif (r['head_branch'] == pr['head_ref'] and parse_time(r['created_at']) >= parse_time(pr['created_at'])
              and (end is None or parse_time(r['created_at']) <= parse_time(end))):
            out.append(r)
    return out


def compute(snap):
    since, until = parse_time(snap['since']), parse_time(snap['until'])
    branches = set(snap['branches'])
    pulls = [p for p in snap['pulls'] if p['base'] in branches]
    merged = [p for p in pulls if in_window(p['merged_at'], since, until)]
    opened = [p for p in pulls if in_window(p['created_at'], since, until)]
    runs = snap['runs']
    m = collections.OrderedDict()

    weeks = collections.Counter()
    for p in merged:
        y, w, _ = parse_time(p['merged_at']).isocalendar()
        weeks[f'{y}-W{w:02d}'] += 1
    m['merged_per_week'] = sorted(weeks.items())
    m['merged_total'] = len(merged)
    m['opened_total'] = len(opened)

    m['reverts'] = [r['sha'][:7] + ' ' + r['subject'] for r in snap['reverts']]
    m['revert_rate'] = pct(len(snap['reverts']), len(merged))

    qa_any, qa_head, qa_rows = 0, 0, []
    for p in sorted(merged, key=lambda p: p['number']):
        shas = qa_pass_shas(verdict_text(p, snap['comments']))
        at_head = any(p['head_sha'].startswith(s) for s in shas)
        qa_any += bool(shas)
        qa_head += at_head
        qa_rows.append((p['number'], 'at merged head' if at_head else ('other head' if shas else 'none')))
    m['qa_pass_any'] = pct(qa_any, len(merged))
    m['qa_pass_head'] = pct(qa_head, len(merged))
    m['qa_rows'] = qa_rows

    fails_per_pr, blocked_total = {}, 0
    for p in sorted(opened, key=lambda p: p['number']):
        fails, blocked = fail_rounds(verdict_text(p, snap['comments']))
        fails_per_pr[p['number']] = len(fails)
        blocked_total += len(blocked)
    m['fail_rounds_total'] = sum(fails_per_pr.values())
    m['fail_rounds_prs'] = sorted((n, c) for n, c in fails_per_pr.items() if c)
    m['fail_rounds_mean'] = (f'{m["fail_rounds_total"] / len(opened):.2f}' if opened else 'n/a')
    m['blocked_rounds'] = blocked_total

    # Results per (workflow, commit), all attempts.
    by_run = {r['id']: r for r in runs}
    results = collections.defaultdict(set)
    for a in snap['attempts']:
        r = by_run.get(a['run'])
        if r and a['conclusion'] in ('success', 'failure'):
            results[(r['name'], r['head_sha'])].add(a['conclusion'])
    flaky = sorted(k for k, v in results.items() if len(v) == 2)
    m['flaky_rate'] = pct(len(flaky), len(results))
    flaky_by_wf = collections.Counter(k[0] for k in flaky)
    m['flaky_by_workflow'] = sorted(flaky_by_wf.items())
    reruns = [r for r in runs if r['run_attempt'] > 1]
    m['reruns'] = pct(len(reruns), len(runs))
    # Strict flaky: one run (same trigger, same code) whose attempts disagree.
    per_run = collections.defaultdict(set)
    for a in snap['attempts']:
        if a['run'] in by_run and a['conclusion'] in ('success', 'failure'):
            per_run[a['run']].add(a['conclusion'])
    flipped = sorted(k for k, v in per_run.items() if len(v) == 2)
    m['flaky_strict'] = pct(len(flipped), len(per_run))

    # Red then green: a PR where a workflow failed on an earlier commit and passed on the PR's final commit.
    rtg = []
    for p in sorted(merged, key=lambda p: p['number']):
        pr_runs = runs_of_pr(p, runs)
        final = {r['name'] for r in pr_runs if r['head_sha'] == p['head_sha'] and r['conclusion'] == 'success'}
        earlier_red = {r['name'] for r in pr_runs if r['head_sha'] != p['head_sha'] and r['conclusion'] == 'failure'}
        if final & earlier_red:
            rtg.append(p['number'])
    m['red_then_green'] = pct(len(rtg), len(merged))
    m['red_then_green_prs'] = rtg

    req = snap['required_checks']
    known = [set(v) for v in req.values() if v is not None]
    m['required_known'] = all(v is not None for v in req.values()) and bool(req)
    required_names = set().union(*known) if known else set()
    m['required_names'] = {b: (', '.join(v) if v is not None else UNVERIFIED) for b, v in sorted(req.items())}
    by_job = collections.Counter()
    all_failed_jobs = collections.Counter()
    for j in snap['failed_attempt_jobs']:
        if j['conclusion'] == 'failure':
            all_failed_jobs[j['name']] += 1
            if j['name'] in required_names:
                by_job[j['name']] += 1
    m['required_failures'] = sorted(by_job.items())
    m['failed_jobs_all'] = sorted(all_failed_jobs.items(), key=lambda kv: (-kv[1], kv[0]))
    m['failed_attempts'] = sum(1 for a in snap['attempts'] if a['conclusion'] == 'failure')

    live = [r for r in runs if r['name'] == LIVE_WORKFLOW]
    lc = collections.Counter(r['conclusion'] or r['status'] for r in live)
    m['live_runs'] = len(live)
    m['live_share'] = pct(len(live), len(runs))
    m['live_by_result'] = sorted(lc.items())
    m['runs_total'] = len(runs)
    m['runs_by_workflow'] = sorted(collections.Counter(r['name'] for r in runs).items())

    open_to_merge = [hours(p['created_at'], p['merged_at']) for p in merged]
    m['open_to_merge'] = (f'median {statistics.median(open_to_merge):.1f} h, 90th percentile '
                          f'{quantile(open_to_merge, 0.9):.1f} h, longest {max(open_to_merge):.1f} h '
                          f'(n={len(open_to_merge)})') if open_to_merge else 'n/a'

    ttg, ttg_missing = [], []
    for p in sorted(merged, key=lambda p: p['number']):
        names = req.get(p['base'])
        checks = snap['head_check_runs'].get(p['head_sha'], [])
        if names is None:
            ttg_missing.append(p['number'])
            continue
        firsts = []
        for name in names:
            done = sorted(c['completed_at'] for c in checks
                          if c['name'] == name and c['conclusion'] == 'success' and c['completed_at'])
            if not done:
                break
            firsts.append(done[0])
        if len(firsts) == len(names) and names:
            ttg.append(max(0.0, hours(p['created_at'], max(firsts))))
        else:
            ttg_missing.append(p['number'])
    m['time_to_green'] = (f'median {statistics.median(ttg):.1f} h, 90th percentile {quantile(ttg, 0.9):.1f} h '
                          f'(n={len(ttg)})') if ttg else UNVERIFIED
    m['time_to_green_missing'] = ttg_missing

    m['baseline_total'] = UNVERIFIED
    if snap.get('baseline_json'):
        counts = json.loads(snap['baseline_json']).get('counts', {})
        m['baseline_total'] = sum(v for rule in counts.values() for v in rule.values())
    m['proof_coverage'] = UNVERIFIED
    if snap.get('feature_map_md'):
        m['proof_coverage'] = proof_coverage(snap['feature_map_md'])

    m['escaped'] = (len(snap['escaped_issues']) if snap['escaped_label_exists'] else UNVERIFIED)
    return m


def proof_coverage(text):
    labels = collections.Counter()
    for line in text.splitlines():
        cells = [c.strip() for c in line.strip().strip('|').split('|')]
        if len(cells) >= 4 and re.fullmatch(r'[A-Z]{1,3}\d+', cells[0]):
            how = cells[2].lower()
            for key, word in (('live', 'automated live'), ('mock', 'automated mock'), ('manual', 'manual'),
                              ('UNVERIFIED', 'unverified')):
                if how.startswith(word):
                    labels[key] += 1
                    break
            else:
                labels['other'] += 1
    total = sum(labels.values())
    return ', '.join(f'{k} {labels[k]}' for k in ('live', 'mock', 'manual', 'UNVERIFIED', 'other')
                     if labels[k]) + f' (of {total} rows, best label per row)'


# ---------------------------------------------------------------- output

def render(snap, m, snap_sha):
    def fmt(pairs):
        return ', '.join(f'{k}: {v}' for k, v in pairs) or 'none'

    rows = [
        ('PRs merged per week (into lane/face or main)', fmt(m['merged_per_week']) + f' (total {m["merged_total"]})',
         'How much change lands; every other rate below is per this.'),
        ('Revert rate', m['revert_rate'] + ('; ' + '; '.join(m['reverts']) if m['reverts'] else ''),
         'A revert means a merged change was wrong and the gates let it through.'),
        ('Merged PRs with a `QA PASS <commit>` line', m['qa_pass_any'],
         'The pipeline says no merge without a QA verdict; this shows how often that held.'),
        ('... naming the exact head that merged', m['qa_pass_head'],
         'A PASS on an older commit does not prove the code that merged.'),
        ('Reviewer FAIL rounds (PRs opened in window)',
         f'{m["fail_rounds_total"]} rounds, mean {m["fail_rounds_mean"]} per PR; PRs with any: '
         f'{fmt(("#" + str(n), c) for n, c in m["fail_rounds_prs"])} (lower bound); '
         f'plus {m["blocked_rounds"]} rounds blocked by storage or budget',
         'A fresh reviewer catching problems is the safety net working; a falling count means authors got better.'),
        ('PRs that went red, then green at the merged head', m['red_then_green'] +
         (' (' + ', '.join('#' + str(n) for n in m['red_then_green_prs']) + ')' if m['red_then_green_prs'] else ''),
         'Shows the checks are actually stopping bad commits, not just passing.'),
        ('Flaky rate (same workflow + same commit, both pass and fail)',
         m['flaky_rate'] + '; by workflow: ' + fmt(m['flaky_by_workflow']),
         'Target under 2%. A flaky check trains people to re-run instead of read; above 2% the checks are noise.'),
        ('Flaky, strict (one run whose re-run attempt flipped the result)', m['flaky_strict'],
         'The broad rate above also counts a push run against a pull-request run of one commit, and a pull-request '
         'run tests the merge with the base; the strict rate is same code, same trigger.'),
        ('Runs that were re-run', m['reruns'], 'Re-runs hide red; each one should have a reason.'),
        ('Required-check failures by job (failed attempts)',
         (fmt(m['required_failures']) if m['required_known'] else UNVERIFIED + ' (branch protection not readable)')
         + f'; required: {fmt(m["required_names"].items())}',
         'Which gate stops the most work tells where the pain (or the flakiness) is.'),
        ('All failed jobs (any workflow)', fmt(m['failed_jobs_all']) + f' over {m["failed_attempts"]} failed attempts',
         'Context for the line above: failures outside required checks.'),
        ('Live real-AI runs (`live-ai-smoke`)',
         f'{m["live_runs"]} runs, share of all runs {m["live_share"]}; results: {fmt(m["live_by_result"])}',
         'Only a live run proves the AI features work; mock runs never do.'),
        ('Time from PR open to merge', m['open_to_merge'], 'Watch only: too fast can mean a skipped review.'),
        ('Time to green (PR open to all required checks green on the merged head)',
         m['time_to_green'] + (f'; not measurable for {", ".join("#" + str(n) for n in m["time_to_green_missing"])}'
                               ' (judged against the required checks as read at fetch time; a PR merged before a '
                               'check became required, or under its old job name, cannot show it green)'
                               if m['time_to_green_missing'] else ''),
         'Watch only: how long a correct change waits on the gates.'),
        ('Baseline total (sum of `docs/quality/baseline.json`)', str(m['baseline_total']),
         'Known-bad code patterns; may only go down.'),
        ('Proof coverage (`docs/FEATURE_MAP.md` rows)', str(m['proof_coverage']),
         'UNVERIFIED must reach 0 for shipped features.'),
        ('Escaped bugs (issues labelled `escaped`)',
         str(m['escaped']) + ('' if m['escaped'] != UNVERIFIED else ' (the label does not exist yet)'),
         'Bugs found after merge show which layer is missing a check.'),
        ('Prose-to-check ratio', UNVERIFIED + ' (no rule-to-check mapping exists yet; not in git or Actions data)',
         'Rules only in prose are not enforced.'),
        ('Fuzz yield', 'not started (Phase 3)', 'New failures per fuzz hour; measured once the fuzz exists.'),
    ]
    out = [
        f'Window: {snap["since"]} to {snap["until"]} (UTC; start included, end excluded). Repo `{snap["repo"]}`; '
        f'base branches: {", ".join(snap["branches"])}. End-of-window `{snap["end_ref"]["branch"]}` commit: '
        f'`{snap["end_ref"]["sha"]}`. Snapshot sha256: `{snap_sha}`.',
        '',
        '| Metric | Value | Why it matters |',
        '| --- | --- | --- |',
    ]
    out += [f'| {a} | {b} | {c} |' for a, b, c in rows]
    out += ['', f'Workflow runs in window: {m["runs_total"]} ({fmt(m["runs_by_workflow"])}).',
            'QA verdict per merged PR: ' + ', '.join(f'#{n} {v}' for n, v in m['qa_rows']) + '.']
    return '\n'.join(out) + '\n'


def snapshot_sha(snap):
    return hashlib.sha256(json.dumps(snap, sort_keys=True, ensure_ascii=True).encode()).hexdigest()


def appendix():
    lines = ['| Times run | Command |', '| --- | --- |']
    # A `|` inside a table cell must be escaped, even inside code, or the row splits.
    lines += [f'| {n} | `{shape.replace("|", chr(92) + "|")}` |' for shape, n in sorted(QUERY_SHAPES.items())]
    return '\n'.join(lines) + '\n'


# ---------------------------------------------------------------- self-test with planted data

def planted():
    def pr(n, base, created, merged, head, body='', ref=None):
        return {'number': n, 'base': base, 'head_ref': ref or f'b{n}', 'head_sha': head, 'created_at': created,
                'merged_at': merged, 'closed_at': merged, 'body': body}

    def r(i, name, sha, branch, concl, attempt=1, created='2026-11-09T01:00:00Z', prs=()):
        return {'id': i, 'name': name, 'event': 'push', 'head_sha': sha, 'head_branch': branch, 'status': 'completed',
                'conclusion': concl, 'run_attempt': attempt, 'created_at': created, 'updated_at': created,
                'prs': list(prs)}

    a40, b40, c40 = 'a' * 40, 'b' * 40, 'c' * 40
    snap = {
        'repo': 'o/r', 'branches': ['lane/face', 'main'], 'since': '2026-11-09T00:00:00Z',
        'until': '2026-11-17T00:00:00Z',
        'pulls': [
            pr(1, 'lane/face', '2026-11-09T00:00:00Z', '2026-11-09T10:00:00Z', a40,
               f'QA PASS — {a40}\nREVIEW FAIL — 12ab567 fixed\nREVIEW FAIL at `12ab567` again, see run 43345365124'),
            pr(2, 'lane/face', '2026-11-10T00:00:00Z', '2026-11-10T02:00:00Z', b40, 'QA PASS at `1111aaa`'),
            pr(3, 'main', '2026-11-16T00:00:00Z', '2026-11-16T04:00:00Z', c40, ''),
            pr(4, 'other', '2026-11-10T00:00:00Z', '2026-11-10T01:00:00Z', 'd' * 40, f'QA PASS — {"d" * 40}'),
            pr(5, 'lane/face', '2026-11-01T00:00:00Z', '2026-11-08T00:00:00Z', 'e' * 40, ''),   # merged before window
            pr(6, 'lane/face', '2026-11-11T00:00:00Z', None, 'f' * 40, ''),                    # open
        ],
        'comments': [
            {'id': 1, 'issue': 6, 'created_at': '2026-11-11T01:00:00Z',
             'body': 'QA FAIL (blocked: artifact storage), head 7777aaa.\nQA FAIL — 8888bbb (reviewer)'},
            {'id': 2, 'issue': 2, 'created_at': '2026-11-10T01:00:00Z',
             'body': 'QA FAIL with no commit named, run 43345365124'},
        ],
        'runs': [
            r(10, 'windows-ci', '9' * 40, 'b1', 'failure', prs=[1]),          # PR 1: red on an earlier commit
            r(11, 'windows-ci', a40, 'b1', 'success', attempt=2, prs=[1]),     # ... green at head after a re-run
            r(12, 'live-ai-smoke', a40, 'b1', 'success', prs=[1]),
            r(13, 'live-ai-smoke', b40, 'b2', 'failure', created='2026-11-10T00:30:00Z'),
            r(14, 'demo-ci', b40, 'b2', 'success', created='2026-11-10T00:30:00Z'),
        ],
        'attempts': [
            {'run': 10, 'attempt': 1, 'conclusion': 'failure'},
            {'run': 11, 'attempt': 1, 'conclusion': 'failure'},
            {'run': 11, 'attempt': 2, 'conclusion': 'success'},
            {'run': 12, 'attempt': 1, 'conclusion': 'success'},
            {'run': 13, 'attempt': 1, 'conclusion': 'failure'},
            {'run': 14, 'attempt': 1, 'conclusion': 'success'},
        ],
        'failed_attempt_jobs': [
            {'run': 10, 'attempt': 1, 'name': 'build-test-publish', 'conclusion': 'failure'},
            {'run': 11, 'attempt': 1, 'name': 'build-test-publish', 'conclusion': 'failure'},
            {'run': 13, 'attempt': 1, 'name': 'preflight', 'conclusion': 'failure'},
            {'run': 13, 'attempt': 1, 'name': 'live-ai-smoke', 'conclusion': 'failure'},
        ],
        'required_checks': {'lane/face': ['build-test-publish', 'live-ai-smoke'], 'main': ['build-test-publish']},
        'head_check_runs': {
            a40: [{'name': 'build-test-publish', 'conclusion': 'success', 'completed_at': '2026-11-09T03:00:00Z'},
                  {'name': 'live-ai-smoke', 'conclusion': 'success', 'completed_at': '2026-11-09T05:00:00Z'},
                  {'name': 'live-ai-smoke', 'conclusion': 'success', 'completed_at': '2026-11-09T09:00:00Z'}],
            b40: [{'name': 'build-test-publish', 'conclusion': 'success', 'completed_at': '2026-11-10T01:00:00Z'}],
            c40: [{'name': 'build-test-publish', 'conclusion': 'success', 'completed_at': '2026-11-16T03:00:00Z'}],
        },
        'escaped_label_exists': False, 'escaped_issues': [],
        'reverts': [{'sha': 'f' * 40, 'committed': '2026-11-12T00:00:00Z', 'subject': 'Revert "x"'}],
        'end_ref': {'branch': 'lane/face', 'sha': '0' * 40},
        'baseline_json': json.dumps({'counts': {'R1': {'A': 2, 'B': 3}, 'R2': {'A': 5}}}),
        'feature_map_md': ('| # | Feature | How verified | Proof |\n| --- | --- | --- | --- |\n'
                           '| N1 | a | automated live | x |\n| N2 | b | automated mock + manual | x |\n'
                           '| V1 | c | manual | x |\n| F10 | d | UNVERIFIED | x |\n| Label | x | y | z |\n'),
    }
    return snap


def self_test():
    snap = planted()
    m = compute(snap)
    expect = {
        'merged_per_week': [('2026-W46', 2), ('2026-W47', 1)],   # PRs 1, 2 (week 46), 3 (week 47); 4 wrong base
        'merged_total': 3,
        'opened_total': 4,                                          # 1, 2, 3, 6 (5 opened before; 4 wrong base)
        'revert_rate': '1/3 = 33.3%',
        'qa_pass_any': '2/3 = 66.7%',                               # PR 1 (full sha), PR 2 (short, other head)
        'qa_pass_head': '1/3 = 33.3%',
        'qa_rows': [(1, 'at merged head'), (2, 'other head'), (3, 'none')],
        'fail_rounds_total': 2,                                     # PR 1: one (dedupe by commit); PR 6: one
        'fail_rounds_prs': [(1, 1), (6, 1)],
        'fail_rounds_mean': '0.50',
        'blocked_rounds': 1,
        'flaky_rate': '1/5 = 20.0%',                               # windows-ci@a40 failed then passed on re-run
        'flaky_by_workflow': [('windows-ci', 1)],
        'reruns': '1/5 = 20.0%',
        'flaky_strict': '1/5 = 20.0%',                              # run 11: attempt 1 failed, attempt 2 passed
        'red_then_green': '1/3 = 33.3%',
        'red_then_green_prs': [1],
        'required_failures': [('build-test-publish', 2), ('live-ai-smoke', 1)],
        'failed_jobs_all': [('build-test-publish', 2), ('live-ai-smoke', 1), ('preflight', 1)],
        'failed_attempts': 3,
        'live_runs': 2,
        'live_share': '2/5 = 40.0%',
        'live_by_result': [('failure', 1), ('success', 1)],
        'open_to_merge': 'median 4.0 h, 90th percentile 10.0 h, longest 10.0 h (n=3)',
        'time_to_green': 'median 4.0 h, 90th percentile 5.0 h (n=2)',   # PR1 5 h, PR3 3 h
        'time_to_green_missing': [2],                                    # live-ai-smoke never green on b40
        'baseline_total': 10,
        'proof_coverage': 'live 1, mock 1, manual 1, UNVERIFIED 1 (of 4 rows, best label per row)',
        'escaped': UNVERIFIED,
    }
    bad = [f'{k}: got {m[k]!r}, want {v!r}' for k, v in expect.items() if m[k] != v]
    # Regex edge cases.
    cases = [
        (qa_pass_shas('QA PASS — ' + 'ab' * 20), ['ab' * 20]),
        (qa_pass_shas('QA PASS — head 1f2a977.'), ['1f2a977']),
        (qa_pass_shas('no QA PASS here'), []),
        (qa_pass_shas('QA: **PASS** at head `' + 'cd' * 20 + '`. Not merged.'), ['cd' * 20]),
        (qa_pass_shas('QA PASS**, live [43345365124](https://x)'), []),
        (fail_rounds('QA: **FAIL** at `abc1234`'), ({('QA', 'abc1234')}, set())),
        (fail_rounds('REVIEW FAIL**: x fixed in `f64f73d`'), ({('REVIEW', 'f64f73d')}, set())),
        (fail_rounds('QA FAIL (blocked: Actions budget), head ' + 'e' * 40), (set(), {('QA', 'eeeeeee')})),
    ]
    bad += [f'regex case {i}: got {got!r}, want {want!r}' for i, (got, want) in enumerate(cases) if got != want]
    # Trimming bodies to verdict lines (what fetch stores) must not change any number.
    trimmed = json.loads(json.dumps(snap))
    for item in trimmed['pulls'] + trimmed['comments']:
        item['body'] = verdict_lines(item['body'] + '\nunrelated prose line\nC:\\some\\path')
    if compute(trimmed) != m:
        bad.append('trimming bodies to verdict lines changed a number')
    # Same snapshot -> same table, byte for byte; a changed snapshot -> a different sha.
    t1 = render(snap, m, snapshot_sha(snap))
    t2 = render(json.loads(json.dumps(snap)), compute(json.loads(json.dumps(snap))), snapshot_sha(snap))
    if t1 != t2:
        bad.append('render is not deterministic')
    changed = json.loads(json.dumps(snap))
    changed['runs'][0]['conclusion'] = 'success'
    if snapshot_sha(changed) == snapshot_sha(snap):
        bad.append('snapshot sha does not change with the data')
    if bad:
        print('SELF-TEST FAIL\n  ' + '\n  '.join(bad))
        return 1
    print(f'SELF-TEST PASS: {len(expect)} planted metrics and {len(cases)} verdict-line cases match.')
    return 0


# ---------------------------------------------------------------- main

def main(argv):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--self-test', action='store_true')
    ap.add_argument('--repo', default='vikas53953/bluey')
    ap.add_argument('--since', help='window start, UTC, e.g. 2026-11-08T00:00:00Z (included)')
    ap.add_argument('--until', help='window end, UTC (excluded)')
    ap.add_argument('--branches', default='lane/face,main')
    ap.add_argument('--remote', default='origin', help='git remote holding the base branches')
    ap.add_argument('--save-snapshot', help='write the raw data here (JSON)')
    ap.add_argument('--from-snapshot', help='compute from this snapshot instead of fetching')
    ap.add_argument('--out', help='write the markdown table here (default: stdout)')
    args = ap.parse_args(argv)
    if args.self_test:
        return self_test()
    if not args.since or not args.until:
        ap.error('--since and --until are required (fixed window; nothing uses the current time)')
    since, until = parse_time(args.since), parse_time(args.until)
    if since >= until:
        ap.error('--since must be before --until')
    if args.from_snapshot:
        with open(args.from_snapshot, encoding='utf-8') as f:
            snap = json.load(f)
        if (snap['since'], snap['until']) != (iso(since), iso(until)):
            ap.error(f'snapshot window is {snap["since"]}..{snap["until"]}, not the one asked for')
    else:
        branches = [b.strip() for b in args.branches.split(',') if b.strip()]
        snap = fetch(args.repo, since, until, branches, args.remote)
    if args.save_snapshot:
        with open(args.save_snapshot, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(snap, f, sort_keys=True, indent=1, ensure_ascii=True)
            f.write('\n')
    text = render(snap, compute(snap), snapshot_sha(snap))
    if QUERY_SHAPES:
        text += '\nQueries run (ids replaced by placeholders; every exact command was printed to stderr):\n\n' + appendix()
    if args.out:
        with open(args.out, 'w', encoding='utf-8', newline='\n') as f:
            f.write(text)
    else:
        sys.stdout.write(text)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
