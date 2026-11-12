# Boo decision log

One line of context per decision: **what** was decided, **why**, and **where it is recorded**. Newest last. Add a
row in every PR that makes or changes a decision (see [CONTRIBUTING.md](../CONTRIBUTING.md)). A decision that only
lives in a chat with Vikas is copied here with the date he gave it, so it survives the chat.

Status words: **in force** (applies on `lane/face`), **proposed** (in an open PR, not merged), **superseded**.

| Date | Decision | Why | Source | Status |
| --- | --- | --- | --- | --- |
| 6 Nov 2026 | Build Boo natively in **C# / WPF on .NET 10**, not Electron or Tauri. | Windows-only product that needs HWND ownership, native overlays, audio and UI Automation; WPF vector rendering suits the avatar and annotations. Electron would add a Chromium runtime, and Tauri a Rust/web boundary. Chosen for platform fit, not a measured memory claim. | `docs/plans/2026-11-06-2242-feat-boo-windows-companion-plan.md` (KTD1) | in force |
| 6 Nov 2026 | Pin the .NET SDK exactly (`10.0.403`, `rollForward: disable`). | Same build everywhere; CI and local agree. | `global.json`; plan U8 "Bootstrap the native build and verification harness" | in force |
| 7 Nov 2026 | The idle notch shows **only eyes**: no wordmark, no profile name (never read from the Windows account). Black body, white eyes by default; colours adjustable and saved locally. It retracts when idle, hover reveals it, and a click opens the full window. Capture and microphone always visibly disclosed. | Owner's product direction for the companion's look and privacy. | `docs/STATUS.md` ("Latest product decisions") | in force (look refined by Living ink, below) |
| 8 Nov 2026 | **Separate roles per change**: an independent test author freezes failing tests first, a different builder implements, a third agent reviews. | Prevents agents grading their own work. | `docs/STATUS.md` (8 Nov additions); README | in force |
| 8 Nov 2026 | Installer is **Velopack** (per-user, no admin), not MSIX. | Works unsigned (SmartScreen prompt only); MSIX would need a trusted signing certificate or every tester importing a root cert. | `docs/plans/2026-11-08-installer-auto-update.md`; PR #8 | in force |
| 8 Nov 2026 | Updates are **opt-in**: a daily check of GitHub Releases with a DPAPI-protected read token; nothing downloads until **Install and restart**; never downgrade. | Boo must not contact the network unless the user turns it on. | README ("Updates"); PR #8 | in force |
| 8 Nov 2026 | Text AI provider is selectable (OpenAI default, Anthropic Claude, Google Gemini). **Voice always uses OpenAI Realtime.** One DPAPI key file per provider; a key only ever goes to that vendor's official origin. | Choice for text answers without rebuilding voice. | `docs/plans/2026-11-08-multi-provider.md`; README ("AI providers") | in force |
| 10 Nov 2026 | **Frozen-test oracle and no-vacuous-pass gate run in CI.** Tests change only through `--extend` (new files) or a reviewed `--rebaseline`. No `Skip =`; a run with zero or skipped tests fails. SF-006 symlink tests never skip. | An emptied test file had once let CI stay green. A skipped security test is an untested one. | PR #10; `scripts/test_oracle.py`; `scripts/test_gate.py` | in force |
| 12 Nov 2026 | **Mock green = false green.** demo-ci (mock AI) is a blocking gate for UI behaviour, but a feature that talks to AI is only proven by a **real-key live round trip** (`live-ai-smoke`, manual, fails closed without a key). | The mock proves Boo's screens and policies, not that Boo can talk to the real AI. | PR #11; `docs/LIVE_AI_SMOKE.md` ("Why it exists") | in force |
| 12 Nov 2026 | Close PRs #1, #2, #5, #6 as **superseded**. | All their commits were already in `lane/face`, which passed full checks and a real-key live run (43194762101). | Closing comments on #1, #2, #5, #6 | done |
| 12 Nov 2026 | **The notch window draws in software** (`RenderMode = SoftwareOnly`), panel and overlay stay on the graphics card. | Removed about 65 MB of graphics-driver memory; frames and CPU unchanged. | PR #12; detailed record D-2026-11-12-notch-software-drawing below | in force (merged 13 Nov) |
| 12 Nov 2026 | **Oracle revision 20** re-freezes four face tests for Living ink and freezes two new test files. | Face D ships; assertions unchanged. | PR #12; `docs/verification/test-oracle-revision-20.md` | in force (merged 13 Nov) |
| 12 Nov 2026 | Notch look: **Living ink** (organic ink-blot pocket, cream eyes, wake-up, celebrate, light/dark). | Picked by Vikas on 12 Nov. | PR #12 description ("the Living ink look picked on 12 Nov 2026") | in force (PR #12 merged 13 Nov, `8474556`) |
| 12 Nov 2026 | **Branch protection** on `lane/face` and `main`: required checks `demos` and `build-test-publish`, up-to-date branch, no force push, no deletion; no required approvals; admins not enforced. `live-ai-smoke` not required (manual only). | Vikas asked for gates. He is the only reviewer, so approvals would block him. | GitHub branch-protection API, set and read back 12 Nov; HANDOFF.md §4 | in force |
| 12 Nov 2026 | **Merging, tagging and releasing stay with Vikas.** Agents never merge, tag, release, or push to `lane/face` / `main`. | The owner signs off what ships. | Vikas's standing instruction (chat); stated in PR #12's body and in the QA comments on PRs #3 and #14 | in force |
| 12 Nov 2026 | **Live QA uses only the OpenAI key for now.** Claude, Gemini and voice are reported as **UNVERIFIED: not tested, by choice**, not BLOCKED and not green. | Owner's scope choice. | Vikas instruction, 12 Nov 2026, 7:18 PM IST | in force |
| 12 Nov 2026 | **Missing evidence stays red.** No `continue-on-error` on upload or test steps to get green while artifact storage is full. Wait for storage, then re-run. | A green without evidence is not proof. | Vikas instruction, 12 Nov 2026, 7:18 PM IST | in force |
| 12 Nov 2026 | Free artifact storage by **deleting Actions artifacts older than 7 days** (artifacts only: not runs, logs, branches, releases, caches or secrets). | Storage quota full (22.45 GB). | Vikas instruction, 12 Nov 2026, 6:46 PM IST | superseded by the next row (nothing qualified before about 15 Nov) |
| 12 Nov 2026 | Instead, **delete only the 8 Nov app and installer copies** (rebuildable) and keep every screenshot, test result and evidence artifact. Done: 168 artifacts, 15.93 GB, about 14:21–14:27 UTC (time from the verify-boo agent thread's log). | Unblock CI the same day instead of waiting for the 7-day rule. | Vikas approval, 12 Nov 2026, 7:50 PM IST (verify-boo agent thread) | done; waiting for GitHub's 6–12 h recount |
| 12 Nov 2026 | **Keep CI artifacts 7 days** (`retention-days: 7` on every upload) instead of the 90-day default. | Stops storage filling up again. | PR #15 | **proposed** |
| 12 Nov 2026 | The live smoke's **anti-mock guard must reject every private and LAN address** (10/8, 172.16/12, 192.168/16, 169.254/16, fc00::/7, fe80::/10, loopback) and hostnames resolving to them. PR #14 also rejects proxies and other local or translation ranges. | A mock on the LAN would otherwise pass as the real provider. | Vikas instruction, 12 Nov 2026, 7:18 PM IST; PR #14 | in force (PR #14 merged 13 Nov, `d0dfd0e`) |
| 12 Nov 2026 | **Never capture the owner's real screen.** Evidence comes only from test windows, CI/VM runners or Boo's offscreen gallery. The UI/perf/demo suites (full-screen captures) run only in CI or a VM. | Privacy. | Vikas's standing instruction | in force |
| 12 Nov 2026 | Keep **HANDOFF.md** and this log up to date in **every PR**. | Any agent must be able to pick up the project cold. | Vikas instruction, 12 Nov 2026, 8:00 PM IST; PR #16 | proposed (PR #16) |
| 12 Nov 2026 | **Agent-trust plan, in this order:** (1) PR #15 storage fix, (2) close the screen-capture risk, (3) Phase 1 deterministic checks as small PRs, (4) merge PR #13 verify-boo, (5) Phase 2, (6) Phase 3. Phase 2 and 3 wait for Vikas's explicit go after Phase 1; Phase 2 also needs a monthly GitHub budget and an OpenAI spend cap from him. Phase 1 PRs open only after #15 and the screen-risk PR are merged. | Trust comes from checks a machine runs every time, not from prompt rules; storage and privacy come first. | Vikas decision, 12 Nov 2026, 10:02 PM IST; `docs/plans/agent-trust-plan.md` (PR #17) | in force (plan doc proposed in PR #17) |
| 13 Nov 2026 | **The repository is public** (`vikas53953/bluey`, MIT licence in `9729a14`). Standard GitHub runners are free; CI logs and uploaded evidence are public too, so the live smoke's evidence shows Boo's own windows only and secrets stay names-only. | Removed the Actions budget block. | Vikas, relayed by Grok Bot, 13 Nov 2026, 12:15 AM IST | in force |
| 12 Nov 2026 | **PR #18, PR #17 and every agent-trust Phase 1/2/3 PR may be merged without asking once every gate passes**; since 13 Nov 2026, 12:15 AM IST the verifying bot (Grok Bot) merges, never the agent that built the PR: required checks green on the head, a real-key `live-ai-smoke` pass on the head, a fresh reviewer's PASS, the evidence table, no skips. Releases and tags stay with Vikas. Every PR, including docs-only, gets a real-key live run. | Lets finished, fully proven work land without a manual click, while releases stay his. | Vikas instruction, 12 Nov 2026, 11:20 PM IST | in force (supersedes the 'Merging, tagging and releasing stay with Vikas' row for these PRs only) |
| 12 Nov 2026 | **The live AI smoke runs only on GitHub's `windows-latest` VM, never on Vikas's PC.** No runner choice; the job refuses any non-GitHub runner; no workflow may name a self-hosted runner (`scripts/ci/check_runners.py`). Test screenshots are of Boo's own windows only (drawn from the window's own content, never copied from the screen) unless the run is on a GitHub VM, never the whole desktop in the live smoke, and rectangle screenshots run only on a GitHub VM. The key stays a secret referenced by name (`BOO_LIVE_OPENAI_KEY`), never printed. | The live test drives the mouse and keyboard and sends a picture of the screen to a real AI; on his PC that would capture his real screen. | Vikas decision, 12 Nov 2026, 10:02 PM IST; PR #18; `docs/LIVE_AI_SMOKE.md` §2 | **proposed** (PR open) |

## Detailed decision records (from PR #12)

### D-2026-11-12-notch-software-drawing: the notch window draws in software

**Decision.** The notch window (`NotchWindow`) tells Windows to draw it in software
(`HwndSource.CompositionTarget.RenderMode = SoftwareOnly`, set in `OnSourceInitialized`). Only the notch: the panel
and the annotation overlay still draw on the graphics card.

**Why.** PR #12 review found the Living ink build held ~17 MB more memory than lane/face after one hover. That gap
exists only on the graphics-card drawing path for the notch, a small see-through window that stays on screen all day.
The second reviewer applied the same fix to plain lane/face: in software both builds land within ~2 MB of each other
(73-77 MB after hover). Why Living ink costs more on the card path was not found; with the card path gone it no
longer matters for the notch. Measured on the dev PC (Windows 11, graphics card present), official perf test,
private memory:

| Build | Idle (never hovered) | After one hover |
|---|---:|---:|
| lane/face, graphics card | 92–108 MB | 127–129 MB |
| Living ink, graphics card | 92–94 MB | 137–144 MB |
| Living ink, software (this decision) | 58–62 MB | 75–76 MB |

How it was found, by elimination (two runs each): turning off the wake-up, its fading, its shape changes, the eye
clip, the loose-ink layer, the shine's see-through, the wider window, or forcing a .NET cleanup after hiding changed
nothing. Software drawing removed all of it, for the idle cost as well as the hover cost.

What it costs: nothing measured. Frames while the face is out: ~51–53 fps both ways. CPU while the face is out and
moving: 26.0 % vs 26.8 % of one core on average (6 samples each, noisy). Hidden CPU 0 % both ways. Threads 44 → 19.
Startup was not slower (1.7–3.8 s, within the old 1.6–11.7 s spread). The face looks the same (screenshots compared;
differences only where the drifting pupils sit).

Side effect: the frozen perf budgets `idlePrivateBytesMb <= 80` and `idleWorkingSetMb <= 160`, which failed on this
PC for every build, now pass (60 MB, 128–130 MB).

Not measured: CPU on a 4K / 200 % or high-refresh screen (software cost grows with pixels), and memory after the
panel has been opened (the panel keeps the graphics card).

**Guard.** `tests/Boo.Perf.Tests/NotchDrawingMemoryTests.cs`: the running notch must report `SoftwareOnly` through
`BOO_PERF_DIAG` (field `notchRenderMode`), and one hover may add at most 30 MB of private memory. The mode check
matters most: CI's virtual machine has no graphics card, so there memory alone cannot tell the two modes apart.
On the old builds the test fails: +30.6 to +46.6 MB pre-fix Living ink, +34 to +36 MB lane/face, mode not reported.
It has not run in CI yet (see HANDOFF.md, artifact storage).

**Revisit if.** The notch ever needs effects that are slow in software (blur, drop shadows, large bitmaps), or a
PC shows the face below ~30 fps. Measure again with the perf suite on a real PC before changing it.

### D-2026-11-12-oracle-revision-20: re-freeze the face tests for Living ink

**Decision.** Accept the PR #12 edits to four frozen face tests and freeze two new test files (oracle revision 20).

**Why.** The shipped spec now chooses face D. The face B tests keep every assertion and now load the shipped spec with
face B selected. Tests and assertion counts did not drop. Full per-file record: `docs/verification/test-oracle-revision-20.md`.
