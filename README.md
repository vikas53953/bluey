# Boo for Windows

A local Windows desktop assistant with a top-center companion, screen explanation, voice conversation, requested tasks and focus sessions.

**Current phase: integration and native Windows QA. This is a development build, not an accepted release.** See [project status](docs/STATUS.md) for what is implemented, demonstrated and still unverified.

New here? Read the [user guide](docs/USER_GUIDE.md). Recording a video? Use the [demo script](docs/DEMO_SCRIPT.md).

**Working on Boo (people and agents): start with [HANDOFF.md](HANDOFF.md)**: architecture, build and test, branch
model, pipeline rules and current status. Every PR updates HANDOFF.md's status and [docs/DECISIONS.md](docs/DECISIONS.md);
see [CONTRIBUTING.md](CONTRIBUTING.md).

## How to try

This is the quickest path from download to the three demos and a focus session. It takes about 35 minutes, 25 of them for the focus session.

### 1. Install Boo (or use the portable build)

**Installer (recommended).** On GitHub, open **Actions > release**, pick the latest green run (for branch `lane/face` while PR #3 is open, on `main` after it merges), and download the **boo-installer-win-x64** artifact. Tagged versions (`v*`) are also attached to **Releases**.

1. Unzip the artifact and run `Boo-win-Setup.exe`. It installs for the current user only (no admin prompt) into `%LocalAppData%\Boo`, adds **Boo** to the Start menu and to **Settings > Apps** for uninstall, and starts Boo.
2. Optional: compare the installer against `Boo-win-Setup.exe.sha256` (`Get-FileHash Boo-win-Setup.exe`).
3. The build is not code-signed, so SmartScreen may ask: choose **More info > Run anyway**.

**Portable ZIP.** Download the **boo-portable-win-x64** artifact instead, extract the inner `Boo-<version>-win-x64.zip` whole to a normal folder such as `Downloads\Boo`, and open `Boo.exe`. No .NET install is needed. A portable copy cannot update itself.

**Updates (opt-in, installed copies only).** Boo never contacts GitHub unless you turn this on. In **Settings > Updates**:

1. Paste a GitHub fine-grained personal access token with **Contents: read** on this repository (it is private) and press **Save settings**. The token is protected with Windows DPAPI like the OpenAI key and is never shown again.
2. Tick **Check GitHub Releases for updates once a day** and save. Boo checks at most once every 24 hours; **Check for updates** checks right away. **Include pre-releases** also offers tags with a suffix such as `v0.2.0-beta1`; plain `vX.Y.Z` tags are full releases.
3. When a newer release exists, the status line names it and **Install and restart** appears. Nothing is downloaded or installed until you press it. Velopack verifies the package hash against the release feed before applying it. Only strictly newer versions are offered; Boo never downgrades.

### 2. One-time setup

1. Press **Ctrl+Alt+B** to open the full window (Boo starts in the background; the eyes sit in the top-center notch).
2. In **Settings**, paste your OpenAI key into **OpenAI key (voice + OpenAI text)** and press **Save settings**. The key stays on this PC, protected by Windows; it is never part of the package or the repository. Boo uses OpenAI for everything (see [AI providers](#ai-providers)).
3. Pick the **Display** Boo may look at and your **Microphone**. Screen sharing and the microphone show a visible indicator whenever they are active.

Keyboard only: **F6** moves between Ask, Focus and Settings, **Ctrl+Enter** sends, **Escape** hides the window, and every control shows an accent focus ring. Boo follows Windows High contrast, Text size and "Animation effects" settings, and its window, cards and conversation are exposed to screen readers.

Controls you will use: hold **Ctrl+Alt+Space** (or press and hold Boo's eyes) to talk, release to send. **Ctrl+Alt+F12**, **Ctrl+Alt+S** or **Stop** cancels capture, audio and pending work at any time and pauses a running focus session. While talking you can also say what you are working on ("I'm writing the intro for 30 minutes") to start a focus session, ask Boo to pause, resume or end it, or say bye to let Boo go quiet.

### 3. Demo 1: explain what is on screen

1. Open any app or web page with a busy layout on the selected display.
2. Hold **Ctrl+Alt+Space** and ask, for example, "Why is the sign-up button hard to find?" Typing the question in **Ask Boo** and pressing **Send** works too.
3. Expected: a spoken and written explanation, with the pointer and ring marking the element on screen. The marks clear when you scroll, resize or switch windows.

### 4. Demo 2: edit with approval, then Undo

1. Open an app with a text field on the selected display and type something in it first, for example `Draft v1`. Undo needs the original text; Boo cannot restore a field that was empty before its edit.
2. Type in **Ask Boo**: `change the text box to Final v2`. Requests that start with `type`, `edit`, `change`, `set`, `click`, `open`, `create` or `task` are treated as actions.
3. Expected: Boo shows **Review action** with the requested target and the resolved destination. Nothing changes until you press **Approve**; **Cancel** discards it. After applying, Boo reads the field back and reports what it actually contains.
4. Now type or say `undo` (also `revert` or `put it back`). Boo handles this locally without asking the AI: it re-reads the window, shows the Undo as another **Review action**, and restores the original text only after you press **Approve**. If the field changed since Boo's edit, it refuses rather than overwrite your typing. Saying "undo" with nothing recorded gets "Nothing to undo".

### 5. Demo 3: research with sources

1. Type or say: `research the best free tools for team retrospectives and make a shortlist`. Requests starting with `research`, `find`, `search`, `look up` or `compare`, or containing "with sources", use web research.
2. Expected: progress while it works, then a shortlist with clickable links under **Sources**.
3. Optional file task: choose a **Task folder**, then ask `create a file shortlist.md with that list`. Boo shows the action for approval and reports done only after it has checked the file exists.

### 6. A 25-minute focus session

1. In **Focus**, enter what you are working on (for example "Write the Q4 firewall migration plan"), set **25** minutes and press **Start**.
2. Work normally. Boo takes a screenshot about every 30 seconds and asks the AI whether you are on task. A short detour is fine; only sustained unrelated activity triggers a nudge (bubble from the notch plus a chirp, at most once every 2 minutes).
3. **Pause** stops screenshots; **Resume** continues. When the timer ends (or you press **End**), the session appears in **Session history** with any paused gaps marked.

If something does not behave as described, note the build label from the ZIP name and what you saw; the [status page](docs/STATUS.md) lists what has and has not been verified on a real Windows PC yet.

## Everyday controls

1. Boo starts in the background. Hover the top-center edge of the chosen display to reveal the eyes; press **Ctrl+Alt+B** to open the full window.
2. In the full window, use **Settings > Appearance** to change the body and eye colors. **Save settings** persists them; **Reset appearance** restores black with white eyes.
3. Use **Stop**, the tray's **Stop (Ctrl+Alt+F12)**, **Ctrl+Alt+F12** or **Ctrl+Alt+S** to stop capture, audio and pending work. Closing the main window hides it; use the tray's **Quit** to exit.
4. Inside the window: **Alt+A** approve, **Alt+C** cancel, **Alt+S** save settings, **Alt+R** reset appearance, **Ctrl+Enter** send, **Esc** cancel or hide.

The older local build `artifacts/candidate15` (7 Nov, not in this repository) predates every 8 Nov lane; quit it before running a current build, since Boo allows one copy at a time.

The mini contains no product or profile text. Capture and microphone activity keep a disclosure visible while active. Native hover, appearance, window lifecycle and accessibility acceptance are still in progress; the exact observed evidence is linked from the status page.

The package contains no API key. Existing local credentials are separate from the executable. If setup is needed, use the app's masked credential field, never a source file or chat message. Cloud-assisted features require the configured OpenAI account.

## AI providers

- **OpenAI is the default** for everything and needs no extra setup beyond the OpenAI key.
- **Out of scope since 13 Nov 2026 (owner decision): Boo uses only OpenAI, for chat and voice.** The Claude and Gemini choices are still in the code but are not supported or tested; keep **Settings > Text AI** on OpenAI. (The settings can still switch the **Text AI provider** to **Anthropic Claude** or **Google Gemini**.) The chosen provider answers screen explanations, research, focus nudges and focus classification. The model ids are editable; leave a box blank to use the default.
- **Voice always uses OpenAI Realtime and the OpenAI key**, whichever text provider is selected.
- Each provider has its own masked key box. Every key is protected with Windows DPAPI for your Windows account and kept in its own file in Boo's data directory (`credential.bin`, `credential.anthropic.bin`, `credential.gemini.bin`). A key never leaves the PC except to that vendor's official API origin, and it never appears in settings, logs, status text or error messages.

## Evidence and development

- [Latest package identity](docs/verification/candidate15-package.md)
- [Test and build results](docs/verification/appearance-feedback-integration.md): 7 Nov baseline (242 passing Release tests, one host-dependent skip). The 8 Nov lanes are gated by `windows-ci` (Core + Integration + Provider tests) and `release` (perf budgets); see [status](docs/STATUS.md).
- [Current plan](docs/plans/2026-11-06-2242-feat-boo-windows-companion-plan.md)
- [Open review findings](docs/verification/review-residuals.md)

Compound Engineering coordinates independent test authors, isolated builders, reviewers and native QA. Tests are frozen before implementation. Portable ZIPs are built by the `release` workflow (`.github/workflows/release.yml`); a `v*` tag publishes one as a pre-release.
