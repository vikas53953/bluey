# Live AI smoke test — how to run it

**What it is:** one real conversation between the built Boo app and a real AI service. Boo is started, a question
("Explain what is on my screen in one short sentence.") is typed into it, Boo sends a screenshot plus the question to
the real AI, and the test passes only if a real answer shows up in Boo's conversation list.

**Why it exists:** the `demo-ci` check uses a fake AI server (`tools/Boo.QaMockAi`). That proves Boo's screens and
buttons work, but not that Boo can talk to the real AI. This test is the missing half.

**What it costs:** one paid AI request per run (a screenshot and a short answer). It never runs by itself — only when
you press the button.

**Files:**

| File | Plain-words role |
| --- | --- |
| `.github/workflows/live-ai-smoke.yml` | The button on GitHub: checks the key, builds Boo, runs the test, uploads the evidence. |
| `tools/Boo.LiveSmoke/LiveRoundTripTests.cs` | The test itself: starts Boo, asks one question, waits for the real answer. |
| `tests/Boo.Demo.Tests/BooAppFixture.cs` | The existing demo harness (start Boo in a clean temp folder, take screenshots). Reused as is, not changed. |
| `scripts/ci/assert-tests-ran.ps1` | Refuses a "green" where no test actually ran. |

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
prints them (only their length), never types them into Boo's window (so they cannot show up in a screenshot), and
scans every evidence file before upload — a file containing a key is deleted and the run goes red.

## 2. Pick where it runs

The test needs a real Windows desktop, because it clicks Boo's real window.

### Option A (preferred): your own PC as a "self-hosted runner"

A runner is a small GitHub program on your PC that picks up jobs from this repo.

1. Install PowerShell 7 if you do not have it: `winget install Microsoft.PowerShell`.
2. On GitHub: repo → **Settings** → **Actions** → **Runners** → **New self-hosted runner** → **Windows** / **x64**.
3. Follow the commands GitHub shows (download, then `config.cmd --url ... --token ...`).
   - When it asks for **labels**, press Enter — `self-hosted` and `Windows` are added automatically, which is what
     the workflow looks for.
   - When it asks **"Would you like to run the runner as service?"**, answer **N**. A Windows service runs with no
     desktop, so it cannot see or click Boo's window and the test would fail.
4. Start it when you want to run the test: open a terminal in the runner folder and run `run.cmd`. Leave it open.
   Stop it with Ctrl+C when done.

While the test runs (about 5–10 minutes, most of it building Boo):

- **Quit your own Boo first.** Boo only allows one copy; the test stops with a clear message if Boo is already open.
- **Do not use the mouse or keyboard**, and do not lock the screen.
- **The screenshot sent to the AI is of your real screen**, and screenshots are uploaded as evidence. Close anything
  private first.

### Option B (fallback): GitHub's own Windows machine

Choose **windows-latest** in the runner box when you start the run. Nothing to install, and the screen it sends is
an empty test desktop. Use this when your PC is off or you do not want to set up a runner.

If you pick **self-hosted** but no runner is online, the job just waits in the queue ("Waiting for a runner"). Cancel
it and run again with **windows-latest**.

## 3. Run it

1. On GitHub: **Actions** tab → **live-ai-smoke** (left list) → **Run workflow**.
2. Choose the branch, the **text provider** (`openai` unless you added another key), and the **runner**.
3. Leave **model** empty unless you want to try a specific model id.
4. Press **Run workflow**.

From a terminal instead: `gh workflow run live-ai-smoke.yml --ref <branch> -f text_provider=openai -f runner=windows-latest`

## 4. Read the result

| You see | It means |
| --- | --- |
| `preflight` red: **"Live AI smoke FAILED CLOSED: Repository secret BOO_LIVE_OPENAI_KEY is missing"** | No key added yet (step 1). Nothing else ran. This is never counted as a pass. |
| `smoke` red at **"Refuse if any AI host is redirected"** | The machine's hosts file points an AI address at a fake server. Remove that line and run again. |
| `smoke` red at **"Real AI round trip"** | Boo did not get a real answer. The log and `live-01-round-trip.txt` show Boo's own message (for example "OpenAI rejected the API key" or "quota is used up"). |
| Both green | Boo sent a real question to the real AI and showed the real answer. |

The evidence (screenshots, the answer text, test results) is attached to the run as **live-ai-evidence** — open the
run, scroll to **Artifacts**.
