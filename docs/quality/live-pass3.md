# Live pass^3 — one green live run can be luck (rule BOO-LIVE-003)

**The rule.** A real-AI path counts as **proven** only when it passes **3 independent live attempts in a row**
(pass^k with k = 3), and the result says the pass rate (for example `PASS^3: 3/3`). One green run is one sample;
three in a row shows it was not luck. Owner: Vikas, 14 Nov 2026, 00:09 IST (relayed by the Grok Bot watcher; idea
from poteto/pstack).

## What runs

Workflow `.github/workflows/live-pass3.yml`, on GitHub's own Windows VM (`windows-latest`) only, never anyone's PC.

1. Build and publish `Boo.exe` **once**.
2. Run the existing typed live round trip (`tools/Boo.LiveSmoke`, `LiveRoundTripTests`: type a question, Boo sends a
   screenshot to OpenAI, a real answer must appear) **k times**. Each attempt is a fresh `dotnet test`, which starts a
   fresh Boo in a clean folder. Every attempt runs once and is recorded; **no retry-until-green**.
3. `scripts/ci/pass_k.py` reads each attempt's exit code and test results, prints `PASS^k: P/k` with the pass rate,
   writes `pass3.json` (each attempt's result, seconds and reason, the pass rate, the verdict) and **fails the job
   unless every attempt passed**. No attempts recorded, a missing attempt, an empty or skipped test run: all fail.
4. Evidence (screenshots of Boo's windows only, observations, test results per attempt) and the JSON summary are
   uploaded, each only after its key scan (BOO-SAFE-002).

## When it runs

| Trigger | When |
| --- | --- |
| Pull request | Only when it changes the AI paths: `src/Boo.Providers/**`, the `src/Boo.App` files that call the AI (`MainWindow.xaml.cs`, `.Talk`, `.Edits`, `.Voice`, `Boo.App.csproj`), `tools/Boo.LiveSmoke/**`, or this workflow and its judge. |
| By hand | Actions → **live-pass3** → Run workflow. Inputs: **attempts** (3 to 5, default 3), **model** (blank = `gpt-6-luna`, the cheap model the live smoke uses). |

From a terminal: `gh workflow run live-pass3.yml --ref <branch> -f attempts=3` (works once the workflow is on the
default branch; before that, the pull request trigger is the way in).

## How it fits with live-ai-smoke

| | `live-ai-smoke` | `live-pass3` |
| --- | --- | --- |
| Required to merge | **yes** | **no** (owner decision) |
| Runs on | every pull request | pull requests that touch the AI paths, and by hand |
| Proves | the real round trip works once on this head (typed + voice) | the typed round trip is **steady**: k in a row |
| Real requests | 2 per run | k per run (3 by default, 5 at most) |

The required single run stays the gate for every PR. pass^3 is the extra proof for changes to the AI path itself,
and the proof to cite before calling a real-AI feature "proven".

## What it costs

Each attempt makes **one** small paid request (one question with a screenshot) on `gpt-6-luna`, the cheap model the
live smoke uses. The whole two-request live smoke costs about US$0.004 a run (`docs/LIVE_AI_SMOKE.md`), so one
attempt costs **at most about US$0.004**: about **US$0.012 per pass^3 run**, at most about US$0.02 at k = 5. At, say,
30 runs a month that is under US$0.40, far inside the US$10 monthly cap. A request with a model id OpenAI does not
know is rejected and not billed. Windows minutes are free (public repository); a green pass^3 run took about 2–3
minutes on 14 Nov 2026 (each attempt 5–8 s); a run whose attempts all fail takes longer (each waits out the test's
150 s answer timeout: about 8 minutes for 3 attempts).

## Read the result

| You see | It means |
| --- | --- |
| `PASS^3: 3/3 (pass rate 100%) verdict: PASS` | Proven: three independent live attempts in a row passed. |
| `PASS^3: 2/3 ... verdict: FAIL` | One attempt failed: the path is flaky or broken. Each attempt's line says why; its screenshots and observations are in the `live-pass3-evidence` artifact under `attempts/attempt-N`. |
| `PASS^3: 0/3` with "no attempts were recorded" | The build or the attempt step never ran. Never a pass. |
| "attempts must be from 3 to 5" | The attempts input was outside the allowed range (cost cap and the pass^3 rule). |
| red at "Require the OpenAI key" | The `BOO_LIVE_OPENAI_KEY` secret is missing or malformed. Never a pass. |

## Files

| File | Plain-words role |
| --- | --- |
| `.github/workflows/live-pass3.yml` | The button on GitHub: builds Boo once, runs the live round trip k times, judges, uploads. |
| `scripts/ci/pass_k.py` | The judge: k attempts in, `PASS^k: P/k` and a JSON summary out; fails unless all passed. `--self-test` proves it on planted results (3/3 pass, 2/3 fail, 0 attempts fail, k > 5 refused, and the empty-run tricks). |
