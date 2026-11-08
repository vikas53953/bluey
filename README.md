# Boo for Windows

A local Windows desktop assistant with a top-center companion, screen explanation, voice conversation, requested tasks and focus sessions.

**Current phase: integration and native Windows QA. This is a development build, not an accepted release.** See [project status](docs/STATUS.md) for what is implemented, demonstrated and still unverified.

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

Compound Engineering coordinates independent test authors, isolated builders, reviewers and native QA. Tests are frozen before implementation. The source is held locally on `feat/boo-windows`; no Git remote or public release is configured.
