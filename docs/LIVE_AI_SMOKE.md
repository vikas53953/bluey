# Live AI smoke test — how to run it

**What it is:** one real conversation between the built Boo app and a real AI service. Boo is started, a question
("Explain what is on my screen in one short sentence.") is typed into it, Boo sends a screenshot plus the question to
the real AI, and the test passes only if a real answer shows up in Boo's conversation list.

**Why it exists:** the `demo-ci` check uses a fake AI server (`tools/Boo.QaMockAi`). That proves Boo's screens and
buttons work, but not that Boo can talk to the real AI. This test is the missing half.

**When it runs:** on **every pull request**, automatically, and whenever you press the button. Its final job,
`live-ai-smoke`, is a **required check** on `lane/face` and `main`: a PR cannot merge unless a real round trip
passed on its newest commit (Vikas, 12 Nov 2026, 11:20 PM IST). A newer push to the same PR cancels the older run.

**What it costs:** one paid AI request per run (a screenshot and a short answer), plus about 10–14 minutes of a
Windows VM, which GitHub bills at 2× for a private repo (about 20–28 billed minutes per PR push).

**Why a separate final job:** GitHub counts a *skipped* required job as passing. When the key check fails, the
Windows job is skipped, so it cannot be the required check itself. The final `live-ai-smoke` job always runs and is
green only when both the key check and the real round trip succeeded. A PR from a fork gets no secrets and fails.

**Files:**

| File | Plain-words role |
| --- | --- |
| `.github/workflows/live-ai-smoke.yml` | The button on GitHub: checks the key, builds Boo, runs the test, uploads the evidence. |
| `tools/Boo.LiveSmoke/LiveRoundTripTests.cs` | The test itself: starts Boo, asks one question, waits for the real answer. |
| `tests/Boo.Demo.Tests/BooAppFixture.cs` | The existing demo harness (start Boo in a clean temp folder, take screenshots). Linked in, not copied. Its screenshots are of Boo's own windows only unless it runs on a GitHub VM (and never for this test). |
| `scripts/ci/assert-tests-ran.ps1` | Refuses a "green" where no test actually ran. |
| `scripts/ci/check_runners.py` | Fails `windows-ci` if any workflow could run on a machine that is not GitHub's own VM. |

---

## 1. Add the key (once)

1. On GitHub open the repo **vikas53953/bluey** → **Settings** → **Secrets and variables** → **Actions**.
2. Press **New repository secret**.
3. Name: `BOO_LIVE_OPENAI_KEY`. Value: your OpenAI API key (starts with `sk-`). Press **Add secret**.
4. Optional, only if you want to test those services too:
   - `BOO_LIVE_ANTHROPIC_KEY` — an Anthropic (Claude) API key.
   - `BOO_LIVE_GEMINI_KEY` — a Google Gemini API key.

Tip: make a separate key just for this test, with a small monthly spend limit set in the provider's billing page.
You can delete it any time and the test will simply go red again.

**How the key is protected:** GitHub hides secret values in every log. The run hides them a second time, never
prints them (only their length), and never types them into Boo's window. Before upload it scans every **text**
evidence file — a text file containing a key is deleted and the run goes red. It cannot read text inside
screenshots, which is one more reason the test runs only on GitHub's empty test machine (section 2).

**Only the chosen provider's key is used.** Boo is started with only that one key and with speech turned off, and the
test checks the answer came through Boo's screen-help path. So a run with `anthropic` really tests Claude, not
OpenAI.

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
2. Choose the branch and the **text provider** (`openai` unless you added another key).
3. Leave **model** empty unless you want to try a specific model id.
4. Press **Run workflow**.

From a terminal instead: `gh workflow run live-ai-smoke.yml --ref <branch> -f text_provider=openai`

## 4. Read the result

| You see | It means |
| --- | --- |
| `preflight` red: **"Live AI smoke FAILED CLOSED: Repository secret BOO_LIVE_OPENAI_KEY is missing"** | No key added yet (step 1). Nothing else ran. This is never counted as a pass. |
| `smoke` red at **"Screenshots are Boo windows only"** | A screenshot was copied from the screen instead of drawn from a Boo window, or none was written. The log lists every file with its size. Fix the capture code; never loosen the step. |
| `smoke` red at **"Refuse unless this is GitHub's own VM"** | Someone changed `runs-on` to a machine that is not GitHub's. Put it back to `windows-latest`. |
| `smoke` red only at an **Upload** step, with "Artifact storage quota has been hit" | The test itself ran (check the "Real AI round trip" step), but GitHub's storage for evidence is full, so the run stays red: no evidence, no green. Wait for storage to free up and run again. |
| `smoke` red at **"Refuse if any AI host is redirected"** | The machine's hosts file points an AI address at a fake server. Remove that line and run again. |
| `smoke` red at **"Real AI round trip"** | Boo did not get a real answer through the chosen provider. The log and `live-01-round-trip.txt` show Boo's own message (for example "OpenAI rejected the API key" or "quota is used up"). |
| Both green | Boo sent a real question to the real AI and showed the real answer. |

The evidence (screenshots, the answer text, test results) is attached to the run as **live-ai-evidence** — open the
run, scroll to **Artifacts**.
