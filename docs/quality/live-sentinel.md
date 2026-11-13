# Daily live sentinel (BOO-LIVE-004)

**What it is, in one line:** once a day, GitHub's own Windows machine builds Boo from the newest `lane/face` commit and
asks the real OpenAI one typed question and one spoken question. If either fails, one GitHub issue says so.

**Why:** OpenAI can change on its side while nobody pushes code: a model is retired, the API changes shape, the key is
revoked, or quota or billing runs out. The per-PR live smoke only runs when someone opens or updates a PR. The sentinel
notices within a day.

Owner's order: Vikas, 14 Nov 2026, 00:09 IST (relayed by the Grok Bot watcher). Workflow:
`.github/workflows/live-sentinel.yml`. Helper: `scripts/ci/live_sentinel.py` (has `--self-test`).

## 1. When it runs, and on which code

| Question | Answer |
| --- | --- |
| When | Every day at 21:17 UTC (02:47 IST), an off-peak time; GitHub may start scheduled runs a little late. Also by hand: Actions > live-sentinel > Run workflow, or `gh workflow run live-sentinel.yml`. |
| Which code it tests | Always the newest `lane/face` commit. The workflow checks it out into `tip/` and builds Boo.exe from it. |
| Which branch the schedule runs from | GitHub runs scheduled workflows **only from the default branch, which is `main`**. Work lands on `lane/face`, so the workflow checks out `lane/face` explicitly, whichever branch it runs from. |
| When the daily run starts | Only once this workflow file is on `main` (when `lane/face` merges into `main`, PR #3). Until then it does not run on a schedule, and "Run workflow" also needs the file on `main`. The proof runs for this PR were started from a throwaway draft PR (see the PR body). |
| Where | GitHub's own `windows-latest` VM only. A step refuses any other runner; it never runs on Vikas's PC. |

## 2. What it does (at most 2 paid calls per run)

1. Refuses unless it is on a GitHub-hosted runner.
2. Checks that the `BOO_LIVE_OPENAI_KEY` secret is present (length only, never the value) and the model ids are valid.
3. Asks OpenAI, for free, whether each model exists for this key (`GET /v1/models/<id>`). A retired model fails here,
   before any paid call.
4. Refuses if `api.openai.com` is redirected (so no mock can answer).
5. Builds and publishes Boo.exe from the `lane/face` tip.
6. **Paid call 1:** the existing typed-question test (`tools/Boo.LiveSmoke`, `LiveRoundTripTests`) on `gpt-6-luna`,
   run with a filter so exactly one test runs; a second step checks that exactly one test ran and passed.
7. **Paid call 2:** the existing spoken-question test (`LiveVoiceRoundTripTests`, merged in PR #47) on
   `gpt-realtime-2.1-mini` with `gpt-4o-mini-transcribe`, also exactly one test. If that test is not on the tip, the run
   says so with a warning and checks chat only.
8. Key scan of the evidence folder (BOO-SAFE-002), then uploads screenshots, observations and test results (kept 7 days).

Time limits: the whole job 45 minutes, the typed call 10 minutes, the voice call 5 minutes. One run at a time.

## 3. The failure issue

On failure, the `report` job opens **one** issue titled **"Live sentinel: real OpenAI round trip failing"**, or adds a
comment to it if it is already open. It holds:

- the date (UTC), the run link, and the `lane/face` commit that was tested;
- each step that failed, in plain words;
- an **error class** from a fixed list (below).

It never holds the key, a raw provider reply or log text: the helper only picks a class word from a fixed list, and every
value that goes into the issue is checked against a strict pattern. When a later run passes, it comments "Passed again"
with the run link and **closes** the issue. A cancelled run leaves the issue as it is. Only the `report` job has
`issues: write`; the rest of the workflow can only read the repository.

| Error class | What it usually means | What to do |
| --- | --- | --- |
| `model-unavailable` | A model is retired or renamed (HTTP 404, `model_not_found`) | Pick the next cheapest model (docs/LIVE_AI_SMOKE.md section 6) and change the default in a PR |
| `auth-rejected` | The key was revoked or rotated (HTTP 401/403) | Put a new key in the `BOO_LIVE_OPENAI_KEY` secret |
| `quota-or-billing` | Quota or billing problem, or the spend cap was reached (HTTP 429 on the free check, `insufficient_quota`) | Check OpenAI billing and the US$10 a month cap |
| `rate-limited` | OpenAI throttled the call | Usually passes the next day; act only if it repeats |
| `provider-server-error`, `network-error` | OpenAI or the network was down | Usually passes the next day |
| `timeout-no-answer`, `answer-check-failed` | An answer did not come in time or did not pass the check; this may be an API shape change | Open the run and its evidence |
| `build-failed`, `test-did-not-run` | The `lane/face` tip does not build, or the test was renamed | Fix on `lane/face` |
| `key-missing-or-malformed`, `guard-refused`, `ai-host-redirected`, `runner-not-github-hosted` | A safety check refused to run | Fix the secret or the runner |
| `key-found-in-evidence` | The key scan found a key-shaped string, so nothing was uploaded | Treat as a leak report: check the run, rotate the key if needed |

The class is a best guess from fixed patterns; the run log is the evidence.

## 4. Cost and the US$10 cap

| Item | Estimate |
| --- | --- |
| One run | about US$0.004 (one typed answer about US$0.0006 plus one voice turn about US$0.003; docs/LIVE_AI_SMOKE.md section 6) |
| A month of daily runs | about 30 x 2 = 60 paid calls, about **US$0.12** |
| Each extra manual run | about US$0.004 |

The **US$10 a month cap stays** (Vikas, 13 Nov 2026). The sentinel uses about 1% of it.

**Spend is not read by the sentinel.** OpenAI's usage and cost endpoints need an admin key, and adding one would put a
more powerful secret in CI, so the sentinel does not read spend at all. Spend is checked by hand in the OpenAI
dashboard. If the month's spend nears US$10, pause the live tests and say so: turn the sentinel off with
`gh workflow disable live-sentinel.yml` (and back on with `gh workflow enable live-sentinel.yml`). A spend cap that
OpenAI enforces shows up here as `quota-or-billing`.

## 5. Checks and self-test

- `python -I scripts/ci/live_sentinel.py --self-test`: planted inputs (a fake key inside a raw error, junk in the job
  output, broken and double test results) prove the classes, the issue text and the open/comment/close plan; the
  planted key never reaches the output. The `report` job runs the self-test before it touches an issue.
- `python -I scripts/ci/check_runners.py` and `python -I scripts/quality/key_scan.py --check-workflows` pass with this
  workflow (GitHub-hosted runners only; the upload has its generated key-scan step).
