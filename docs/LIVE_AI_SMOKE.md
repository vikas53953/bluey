# Live AI smoke test — how to run it

**What it is:** two real conversations with OpenAI (Boo is OpenAI-only, for chat and voice; Vikas, 13 Nov 2026).
1. **Typed:** Boo is started, a question ("Explain what is on my screen in one short sentence.") is typed into it, Boo
   sends a screenshot plus the question to the real AI, and the test passes only if a real answer shows up in Boo's
   conversation list.
2. **Voice:** a synthetic spoken question ("What is two plus three? Please answer with just the number.", a WAV made
   offline by Windows' speech synthesizer and checked in, never a microphone) goes through Boo's own OpenAI Realtime
   client in push-to-talk mode. It passes only if OpenAI's transcript of the question says "two plus three" and the
   spoken answer (real audio) says "five". Microphone capture and speaker playback are not covered (a VM has neither).

**Why it exists:** the `demo-ci` check uses a fake AI server (`tools/Boo.QaMockAi`). That proves Boo's screens and
buttons work, but not that Boo can talk to the real AI. This test is the missing half.

**When it runs:** on **every pull request**, automatically, and whenever you press the button. Its final job,
`live-ai-smoke`, becomes a **required check** on `lane/face` once PR #20 merges (and on `main` when this reaches
`main`): then a PR cannot merge unless a real round trip passed on its newest commit (Vikas, 12 Nov 2026, 11:20 PM IST). A newer push to the same PR cancels the older run.

**What it costs:** two small paid requests per run, on cheap OpenAI models (section 6): about **US$0.004 per run**.
At about 400 runs a month (13 Nov 2026 pace: 173 runs in the first 13 days) that is about US$1.60 a month, well under
the US$10 monthly cap. A run cancelled by a newer push may already have spent its requests. GitHub's standard runners are free
because the repository is public (since 13 Nov 2026); each run takes about 10–14 minutes of a Windows VM.

**Known limits:**
- A PR from a fork, or opened by Dependabot, gets no access to the key, so its live run fails by design. Re-push the
  change from a branch in this repository.
- Changing a PR's base branch (for example retargeting a stacked PR) does not start a new run; the earlier result on
  the same commit still counts. Branch protection requires the branch to be up to date, which usually forces a new
  commit and so a fresh run.
- A pull request runs **its own copy** of this workflow, so a PR could change the check itself. Only a GitHub setting
  closes that (see HANDOFF.md, known issues); until then the verifying bot must treat any PR that touches
  `.github/workflows/`, `scripts/ci/`, `scripts/quality/`, `scripts/test_*.py`, `docs/quality/` or the oracle files
  (`docs/verification/test-oracle*`, `test-oracle-history/`) as needing Vikas's eyes.

**Why a separate final job:** GitHub counts a *skipped* required job as passing. When the key check fails, the
Windows job is skipped, so it cannot be the required check itself. The final `live-ai-smoke` job always runs and is
green only when both the key check and the real round trip succeeded. A PR from a fork gets no secrets and fails.

**Files:**

| File | Plain-words role |
| --- | --- |
| `.github/workflows/live-ai-smoke.yml` | The button on GitHub: checks the key, builds Boo, runs the test, uploads the evidence. |
| `tools/Boo.LiveSmoke/LiveRoundTripTests.cs` | The typed test: starts Boo, asks one question, waits for the real answer. |
| `tools/Boo.LiveSmoke/LiveVoiceRoundTripTests.cs` | The voice test: sends the synthetic spoken question through Boo's voice client, checks what OpenAI heard and said. Refuses to run anywhere but a GitHub VM or a throwaway VM declared with verify-boo `-ThrowawayVm`, never a personal PC. |
| `tools/Boo.LiveSmoke/Voice/` | The synthetic question (`what-is-two-plus-three.wav`, 24 kHz 16-bit mono) and the script that makes it offline (`New-VoiceFixture.ps1`). |
| `tests/Boo.Demo.Tests/BooAppFixture.cs` | The existing demo harness (start Boo in a clean temp folder, take screenshots). Linked in, not copied. Its screenshots are of Boo's own windows only unless it runs on a GitHub VM (and never for this test). |
| `tools/Boo.LiveSmoke/LiveAppFixture.cs` | Reads the key (fails closed), seeds it into Boo's own protected key file, turns speech off. Shared with the live journeys. |
| `tools/Boo.VerifyLive/LiveJourneyTests.cs` | Optional (**journeys** box): the seven live user journeys of the verify-boo skill. See section 5. |
| `scripts/ci/assert-tests-ran.ps1` | Refuses a "green" where no test actually ran. |
| `scripts/ci/check_runners.py` | Fails `windows-ci` if any workflow could run on a machine that is not GitHub's own VM. |

---

## 1. Add the key (once)

1. On GitHub open the repo **vikas53953/bluey** → **Settings** → **Secrets and variables** → **Actions**.
2. Press **New repository secret**.
3. Name: `BOO_LIVE_OPENAI_KEY`. Value: your OpenAI API key (starts with `sk-`). Press **Add secret**.
4. Not needed: Claude and Gemini are out of scope since 13 Nov 2026 (Boo uses only OpenAI). The old optional secrets,
   kept here only so an existing setup is recognised:
   - `BOO_LIVE_ANTHROPIC_KEY` — an Anthropic (Claude) API key.
   - `BOO_LIVE_GEMINI_KEY` — a Google Gemini API key.

Tip: make a separate key just for this test, with a small monthly spend limit set in the provider's billing page.
You can delete it any time and the test will simply go red again.

**How the key is protected:** GitHub hides secret values in every log. The run hides them a second time, never
prints them (only their length), and never types them into Boo's window. Before upload it scans every **text**
evidence file — a text file containing a key is deleted and the run goes red. It cannot read text inside
screenshots, which is one more reason the test runs only on GitHub's empty test machine (section 2).

**Only the chosen provider's key is used.** Boo is started with only that one key and with speech turned off, and the
test checks the answer came through Boo's screen-help path. So a run with `anthropic` would really test Claude, not
OpenAI. (Out of scope since 13 Nov 2026: use `openai`.)

## 2. Where it runs: GitHub's own Windows machine, never your PC

The test needs a Windows desktop, because it clicks Boo's real window, and Boo sends a picture of that screen to the
AI. So it runs **only** on GitHub's own throwaway Windows machine (`windows-latest`), whose screen is an empty test
desktop. Decided by Vikas on 12 Nov 2026 (`docs/DECISIONS.md`). Three things enforce it:

- the workflow has no runner choice and always asks for `windows-latest`;
- its first step refuses any runner that is not GitHub-hosted;
- `scripts/ci/check_runners.py` (run by `windows-ci` on every push) fails unless every job in every workflow runs on
  one GitHub-hosted label. It checks twice: the raw YAML node tree (PyYAML 6.0.2, plain keys only, no anchors, tags
  or duplicates) and strict plain-text rules (one plain ASCII document, `jobs:` last, no multi-line quoted strings,
  2-space indentation). Four independent reviews tried to trick it; every trick they found is now in its self-test.
  It is a guard, not the guarantee: the guarantee is that the repo has no registered self-hosted runner (keep it 0).

Evidence screenshots show **Boo's own windows only**, never the whole desktop (`BOO_CAPTURE_SCOPE=boo-windows`). Each
Boo window is drawn from its own content (`PrintWindow`), not copied from the screen, so nothing behind or on top of it
can appear. `demo-ci` proves it on every push with `tools/Boo.CaptureProof`: Boo's transparent explanation overlay is
opened over a magenta decoy window, and the window-only screenshots must contain the overlay and zero magenta pixels. Files are named `<step>.boo-<n>.png`; a step in the workflow fails the run if any screenshot has another
name. The same harness, run on anyone's own PC (for example a local UI or demo run), also draws Boo's windows only, and
refuses rectangle screenshots; it copies the whole desktop only on a GitHub VM.

Earlier versions of this page offered "your own PC as a self-hosted runner". That option is gone. No self-hosted
runner is registered on the repo (checked 12 Nov 2026: 0 runners). **Keep it at 0**: a registered runner is the one
thing a workflow edit on some branch could still reach.

## 3. Run it

1. On GitHub: **Actions** tab → **live-ai-smoke** (left list) → **Run workflow**.
2. Choose the branch and the **text provider** (`openai`; the others are out of scope since 13 Nov 2026).
3. Leave **model** empty unless you want to try a specific model id.
4. Press **Run workflow**.

From a terminal instead: `gh workflow run live-ai-smoke.yml --ref <branch> -f text_provider=openai`

Tick **journeys** (or add `-f journeys=true`) to also run the full live user journeys (section 5).

## 4. Read the result

| You see | It means |
| --- | --- |
| `preflight` red: **"Live AI smoke FAILED CLOSED: Repository secret BOO_LIVE_OPENAI_KEY is missing"** | No key added yet (step 1). Nothing else ran. This is never counted as a pass. |
| `smoke` red at **"Screenshots are Boo windows only"** | A screenshot was copied from the screen instead of drawn from a Boo window, or none was written. The log lists every file with its size. Fix the capture code; never loosen the step. |
| `smoke` red at **"Refuse unless this is GitHub's own VM"** | Someone changed `runs-on` to a machine that is not GitHub's. Put it back to `windows-latest`. |
| `smoke` red only at an **Upload** step, with "Artifact storage quota has been hit" | The test itself ran (check the "Real AI round trip" step), but GitHub's storage for evidence is full, so the run stays red: no evidence, no green. Wait for storage to free up and run again. |
| `smoke` red at **"Refuse if any AI host is redirected"** | The machine's hosts file points an AI address at a fake server. Remove that line and run again. |
| `smoke` red at **"Real AI round trip"** with **"an AI host points at a local, private or LAN address, or a proxy is in the way"** | An AI address resolves to this PC, the local network (10.x, 172.16–31.x, 192.168.x), a link-local, site-local or private IPv6 address, a 100.64.x overlay, a 198.18.x fake-IP proxy or an IPv6 translation of one; or a proxy (HTTPS_PROXY, HTTP_PROXY, ALL_PROXY or the Windows proxy setting) would carry the request. Either way a fake server could answer. The message names the host and the range or proxy. Fix the DNS, VPN, proxy or hosts setting and run again. Boo was never started. (On a green run the log shows the addresses the guard accepted, under `live-00-anti-mock-guard.txt`.) |
| `smoke` red at **"Real AI round trip"** (any other message) | Boo did not get a real answer through the chosen provider. The log and `live-01-round-trip.txt` show Boo's own message (for example "OpenAI rejected the API key" or "quota is used up"). |
| Both green | Boo sent a real question to the real AI and showed the real answer. |

The evidence (screenshots, the answer text, test results) is attached to the run as **live-ai-evidence** — open the
run, scroll to **Artifacts**.

## 5. The live user journeys (journeys = true)

Ticking **journeys** adds a second step on the same machine, with the same key and the same evidence folder (so the
key scan covers it too). It runs Boo's own shipped models (not the cheap CI defaults of section 6) unless `model`
is set, because it proves what users get. It drives Boo like a user through seven steps, in order, on one Boo:

| Step | What it proves | Pass means |
| --- | --- | --- |
| j0 | The notch wakes on **Ctrl+Alt+B** | The full window opens from the hotkey |
| j1 | The notch wakes on **hover** | Mouse at the top-centre strip shows the face within 1.5 s; it hides again after the mouse leaves |
| j2 | **Typed chat** | "What is the capital of France?" gets a real answer containing "Paris" |
| j3 | **Screen explain** with pointer and ring | Boo says "Screen explanation is shown" and a new ring appears on a known test window |
| j4 | **Research** with sources | At least one real "Open source ..." link appears (not the mock's) |
| j5 | **Apply / Undo** with permission | A file appears in the Task folder only after **Approve**; an approved **undo** removes it |
| j6 | A **25-minute focus session** | On Boo's Developer Mode clock (4x): about 6.5 real minutes, happy face, "Session complete", a "25 min ... Completed" history row |

Extra cost: about 45 paid requests (most are small focus checks during j6). Extra time: about 15 minutes. The
journeys have their own test gate ("every live journey ran and passed"); screenshots are Boo's own windows only
(`j*.boo-<n>.png`, never the desktop) in the **live-ai-evidence** artifact under `journeys/`. What this does **not** prove: voice, a real-time 25 minutes, pointer
accuracy at other display scalings, second monitors. Those stay manual (runbook). Full list: `docs/FEATURE_MAP.md`.

## 6. Models and cost

Every model is set per run (the **Run workflow** form, or `-f model=... -f realtime_model=... -f transcribe_model=...`).
Blank means the default below. **Rule for the defaults:** the cheapest model Boo itself already uses for that kind of
request, or the cheapest variant of one (so CI tests a model Boo really calls, or a smaller sibling of it). A cheaper
model that is neither Boo's own nor a variant of one is not used; today the only one is `gpt-5-nano`, which OpenAI
also marks deprecated. Before any paid call, a free step asks OpenAI whether each
model exists for the key; a retired or misspelled model fails there in seconds, with its name.

| Job | Default in CI | Why this one | Override |
| --- | --- | --- | --- |
| Typed answer about a screenshot | `gpt-6-luna` | Boo's focus-check model, which already gets Boo's screenshots; it reads images and takes Boo's `reasoning` setting and structured (JSON schema) output (US$0.10 / 0.50 per 1M input / output tokens). Cheaper: only `gpt-5-nano` (US$0.05 / 0.40), deprecated and never called by Boo | `model` |
| Voice conversation | `gpt-realtime-2.1-mini` | the mini variant of Boo's voice model `gpt-realtime-2.1`, and the cheapest realtime model (audio US$10 / 20 per 1M input / output tokens) | `realtime_model` |
| Speech-to-text of the user's words | `gpt-4o-mini-transcribe` | Boo's own, and the cheapest (US$0.003 per minute) | `transcribe_model` |

Prices: OpenAI's pricing page, 13 Nov 2026 (https://developers.openai.com/api/docs/pricing). OpenAI marks
`gpt-4o-mini-transcribe` as deprecated; when it is retired the model check fails with its name, and the next cheapest is
`gpt-transcribe` (US$0.0045 per minute).

**Rough cost of one run** (the voice test writes its own estimate into `live-voice-01-round-trip.txt`):

| Part | Estimate | Assumption |
| --- | --- | --- |
| Typed answer | about US$0.0006 | about 4,500 input tokens (screenshot plus prompt) and 300 output tokens on `gpt-6-luna` |
| Voice turn | about US$0.003 | 4.7 s of question audio (about 10 tokens/s), a few seconds of answer audio (about 20 tokens/s), about 2,000 text tokens of instructions and tool schemas, plus speech-to-text |
| **One run** | **about US$0.004** | (0.0006 + 0.003, rounded up) |
| A month | about US$1.60 | about 400 runs (every PR push); the journeys (section 5) add about 45 requests, on Boo's own costlier models, each time they are ticked |

**CI models are not the models users get.** Boo ships `gpt-6.1-sol` for typed answers and `gpt-realtime-2.1` for voice
(`src/Boo.Core/Contracts.cs`). The typed round trip drives Boo.exe; the voice round trip proves only RealtimeClient against real OpenAI on cheap default
models; the live journeys (section 5) run Boo's shipped models unless `model` is set. To check the shipped voice model,
run the workflow by hand with `realtime_model=gpt-realtime-2.1`.

**What the voice test checks, and what it does not:** OpenAI's speech-to-text of the question must say "two plus three",
the model's own transcript of its spoken answer must say "five", and at least half a second of answer audio must
arrive. It does not run speech-to-text on the answer audio, and it does not cover the microphone or the speakers. Native app voice stays UNVERIFIED/manual: this provider-client test does not drive published Boo.exe through FlaUI.

The cap is US$10 a month. If PR volume grows past about 2,000 runs a month, or journeys are run often, check OpenAI's
usage page and raise it with Vikas before the cap is reached.
