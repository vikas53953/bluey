# Boo for Windows

A local Windows desktop assistant with a top-center companion, screen explanation, voice conversation, requested tasks and focus sessions.

**Current phase: integration and native Windows QA. This is a development build, not an accepted release.** See [project status](docs/STATUS.md) for what is implemented, demonstrated and still unverified.

## How to try

This is the quickest path from download to the three demos and a focus session. It takes about 35 minutes, 25 of them for the focus session.

### 1. Get the portable build

1. On GitHub, open **Actions > release**, pick the latest green run on `main`, and download the **boo-portable-win-x64** artifact. Tagged versions (`v*`) are also attached to **Releases**.
2. Unzip the artifact, then extract the inner `Boo-<version>-win-x64.zip` to a normal folder such as `Downloads\Boo`. Extract the whole archive; `Boo.exe` will not start from inside the ZIP.
3. Optional: compare the ZIP against the `.sha256` file next to it (`Get-FileHash Boo-*.zip`).
4. Open `Boo.exe`. No .NET install is needed. The build is not code-signed, so SmartScreen may ask: choose **More info > Run anyway**.

### 2. One-time setup

1. Press **Ctrl+Alt+B** to open the full window (Boo starts in the background; the eyes sit in the top-center notch).
2. In **Settings**, paste your OpenAI key into **Protected credential** and press **Save settings**. The key stays on this PC, protected by Windows; it is never part of the package or the repository.
3. Pick the **Display** Boo may look at and your **Microphone**. Screen sharing and the microphone show a visible indicator whenever they are active.

Controls you will use: hold **Ctrl+Alt+Space** (or press and hold Boo's eyes) to talk, release to send. **Ctrl+Alt+F12** or **Stop** cancels capture, audio and pending work at any time.

### 3. Demo 1: explain what is on screen

1. Open any app or web page with a busy layout on the selected display.
2. Hold **Ctrl+Alt+Space** and ask, for example, "Why is the sign-up button hard to find?" Typing the question in **Ask Boo** and pressing **Send** works too.
3. Expected: a spoken and written explanation, with the pointer and ring marking the element on screen. The marks clear when you scroll, resize or switch windows.

### 4. Demo 2: edit with approval, then Undo

1. Open Notepad (or any app with a text field) on the selected display.
2. Type in **Ask Boo**: `type Hello from Boo into the text box`. Requests that start with `type`, `click`, `open`, `create` or `task` are treated as actions.
3. Expected: Boo shows **Review action** with the requested target and the resolved destination. Nothing changes until you press **Approve**; **Cancel** discards it.
4. Press **Approve**, check the text appeared, then undo it. Use Boo's **Undo** if your build shows it; otherwise press Ctrl+Z in the target app. Undo inside Boo is still being finished (see [status](docs/STATUS.md)).

### 5. Demo 3: research with sources

1. Type or say: `research the best free tools for team retrospectives and make a shortlist`. Requests starting with `research`, `find` or `search` use web research.
2. Expected: progress while it works, then a shortlist with clickable links under **Sources**.
3. Optional file task: choose a **Task folder**, then ask `create a file shortlist.md with that list`. Boo shows the action for approval and reports done only after it has checked the file exists.

### 6. A 25-minute focus session

1. In **Focus**, enter what you are working on (for example "Write the Q4 firewall migration plan"), set **25** minutes and press **Start**.
2. Work normally. Boo takes a screenshot about every 30 seconds and asks the AI whether you are on task. A short detour is fine; only sustained unrelated activity triggers a nudge (bubble from the notch plus a chirp, at most once every 2 minutes).
3. **Pause** stops screenshots; **Resume** continues. When the timer ends (or you press **End**), the session appears in **Session history** with any paused gaps marked.

If something does not behave as described, note the build label from the ZIP name and what you saw; the [status page](docs/STATUS.md) lists what has and has not been verified on a real Windows PC yet.

## Try the current build

1. Open `artifacts/candidate15/Boo.exe`. This self-contained Windows x64 build does not need a separate .NET installation. The portable ZIP is `artifacts/Boo-0.1.0-candidate15-win-x64.zip`; extract the whole archive before opening `Boo.exe`.
2. Press **Ctrl+Alt+B** to open the full app. It starts in the background. The revised mini is designed to reveal when hovering the top-center edge.
3. In the opened app, use **Appearance** to change the body and eye colors. **Save settings** persists them; **Reset appearance** restores black with white eyes.
4. Use **Stop**, the tray's **Stop**, or **Ctrl+Alt+F12** to stop capture, audio and pending work. Closing the main window hides it; use the tray's **Quit** to exit.

The mini contains no product or profile text. Capture and microphone activity keep a disclosure visible while active. Native hover, appearance, window lifecycle and accessibility acceptance are still in progress; the exact observed evidence is linked from the status page.

The package contains no API key. Existing local credentials are separate from the executable. If setup is needed, use the app's masked credential field, never a source file or chat message. Cloud-assisted features require the configured OpenAI account.

## Evidence and development

- [Latest package identity](docs/verification/candidate15-package.md)
- [Test and build results](docs/verification/appearance-feedback-integration.md): 242 passing Release tests and one existing host-dependent skip; this does not certify the native experience.
- [Current plan](docs/plans/2026-11-06-2242-feat-boo-windows-companion-plan.md)
- [Open review findings](docs/verification/review-residuals.md)

Compound Engineering coordinates independent test authors, isolated builders, reviewers and native QA. Tests are frozen before implementation. Portable ZIPs are built by the `release` workflow (`.github/workflows/release.yml`); a `v*` tag publishes one as a pre-release.
