# Full-app QA suite (the phase-gate suite)

One manual workflow, `full-app-qa`, that proves the **whole app works together**, not one PR at a time. It is the
suite every "Boo – Phase N gate" runs (plan section 7, `docs/plans/agent-trust-plan.md`; Vikas, 13 Nov 2026).

## What it does, in order

It builds the whole app from a chosen ref (default: the `lane/face` tip) and drives the published `Boo.exe` on
**GitHub's own Windows VM** (never Vikas's PC or screen; a step refuses any other runner).

| Order | Group | What it proves | Real AI? |
| --- | --- | --- | --- |
| 1 | Anti-mock (`tools/Boo.QaSuite/AntiMock`) | The published folder holds no mock or test code; no AI host is redirected (hosts file, DNS, proxy); no planted root certificate for an AI host, and a real TLS handshake with api.openai.com validates. | no request |
| 2 | Suite journeys (`tools/Boo.QaSuite/Journeys`) | Launch (only the notch), Ctrl+Alt+B, hover wake + retract, a real OpenAI chat that must echo a code made at run time (no scripted mock can know it), the notch showing Thinking then Responding, Stop while an answer is coming, focus Start / Pause (notch shows Paused) / Resume / End, settings saved to file and a **new** Boo starting with the same choices. | yes, ~3 small requests |
| 3 | Live round trip (`tools/Boo.LiveSmoke`) | Typed question + screenshot → real OpenAI → answer in Boo. **Voice** runs here too once PR #47 adds its test; until then the report shows voice as **BLOCKED** (never a pass). | yes, 1 request |
| 4 | verify-boo journeys j0–j6 (`tools/Boo.VerifyLive`) | Hotkey, hover, chat, screen explain with pointer + ring, research with real links, Apply/Undo, a 25-minute focus session on the Developer Mode clock. | yes, ~45 requests |
| 5 | Error paths (`tools/Boo.QaSuite/ErrorPaths`) | Empty question; no key; a key OpenAI rejects (a real 401 from the real OpenAI); **network down** (a firewall rule blocks Boo.exe only) then back; **provider never answers** (api.openai.com pointed at a silent local listener, so Boo's own 45 s timeout must fire) then back. Each checks Boo's exact plain-words message. | only the free 401 |
| — | **Deliberate-failure probe** (job `red-probe`, its own VM, no key) | Plants the classic mock trick (the demo-ci hosts redirect) and runs the anti-mock check and the real-chat journey: both **must fail, for that reason**. | none |
| — | Verdict (job `full-app-qa`) | Green only if the suite passed **and** the probe proved red. | — |

The report step writes an evidence table (`summary.md`, also the job summary): every check labelled **live**,
**BLOCKED** or **UNVERIFIED**, with the run link. Each of the 10 notch states is listed: **live** if this run saw it
(the suite records every notch face change), otherwise **UNVERIFIED** (driving every state is plan row 2.11).

Faults (firewall rule, hosts entry) are undone by each check and again by an always-run clean-up step. They refuse to
run anywhere but a GitHub-hosted VM.

## How to run it

```
gh workflow run full-app-qa.yml --ref <branch-with-the-suite> -f app_ref=lane/face
```

| Input | Default | Meaning |
| --- | --- | --- |
| `app_ref` | `lane/face` | The app to test. The **test harness** is always the workflow's own commit; both SHAs are in the report. |
| `chat_model` | `gpt-6-luna` | Chat model for the suite and the round trip (cheapest that is not deprecated, per PR #48). |
| `journeys_model` | `gpt-6-luna` | Chat model for j0–j6. If j3 (explain) or j4 (research) need a bigger model, set it here and write the reason in the evidence table. |
| `focus_model` | `gpt-6-luna` | Focus-check model (Boo reads it once PR #48 lands; today Boo's own focus default is the same model). |
| `realtime_model` | `gpt-realtime-2.1-mini` | Voice model (used once PRs #47/#48 land). |
| `journeys` | `true` | Run j0–j6. Off = those rows are UNVERIFIED in the report. |
| `plant_failure` | `false` | **Probe run**: plants the mock before the suite; the whole run must end red. Run once per suite change and link it. |

## Cost (estimate; UNVERIFIED until a run logs usage)

Prices from PR #48 (`gpt-6-luna` $0.10 / $0.50 per 1M tokens in / out). About 50 requests per full run, most with a
small screenshot (~1.5k tokens in, ~200 out): about $0.01 of input and $0.005 of output, plus j4's web search (about
$0.01–0.03). **About $0.03–0.05 per full run** (under $0.01 with `journeys=false`). The probe and error paths cost
nothing (no key, or a rejected key). Voice, once added, adds about a cent. Ten full runs a month is about $0.50, far
inside the **$10/month** cap. Never on push or PR: manual only.

## Phase gates

1. Grok Bot says which Phase is complete (all its PRs merged).
2. Dispatch `full-app-qa` with `app_ref=lane/face`, plus one `plant_failure=true` run on the same ref.
3. A **fresh reviewer** (no shared context) reads the runs' raw logs, screenshots and `summary.md`.
4. Record the result in `HANDOFF.md` and `docs/DECISIONS.md` in a small PR: the evidence table (live / mock / manual /
   UNVERIFIED / BLOCKED, run links), the reviewer verdict, and `Phase N gate: PASS` or `FAIL` with the fix PRs needed.
5. A gate with a BLOCKED row that the phase requires (for example voice) is **not a pass**. Fix, then re-run; never
   paper over it.
