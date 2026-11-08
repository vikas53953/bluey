# Boo security review (lane/face, PR #3) — 8 Nov 2026

Scope: API key storage, screenshot handling, prompt injection driving actions, ActionPolicy and
approval gates, Undo, start-with-Windows, CI/release secret handling. Fix branch:
`claude/project-thread-we43x9` (off `lane/face`). No frozen test was edited; new tests are in
`tests/Boo.Core.Tests/SecurityHardeningTests.cs` (21 cases).

## Fixed

| # | Severity | Finding | Fix |
|---|----------|---------|-----|
| 1 | High | **Approval card showed the model's free-text target, not the real window.** For `click`/`type_text` the "Resolved destination" was the model string (e.g. "Notepad") while input went to whatever external window was foreground. A prompt-injected page could propose `type_text` "notepad" while the user was in a terminal, and the card would look harmless. Multi-step plans re-target with `TryGetTarget()` for later steps, so this also hit step 2+. | New `WindowActionGuard`; `ActionPreviewBuilder.Create(request, authority, window, booPid)` now shows "<process> window (process N)" and blocks when no window is known. App uses the new overload. |
| 2 | High | **Boo could click or type into shells/admin consoles.** `type_text` sends Unicode `SendInput` including `\r`/`\n`, so text typed into cmd/PowerShell/Windows Terminal/WSL runs as a command after one approval click. | Guard blocks `click`/`type_text` into cmd, powershell, pwsh, Windows Terminal, conhost, WSL/bash, mintty, PuTTY, regedit, mmc, taskmgr, msiexec, consent, CredentialUIBroker. Enforced on the card **and** again in `DesktopTools.ExecuteAsync`. |
| 3 | Medium | **Hidden characters in typed text.** ESC sequences, backspace, bidi overrides (U+202E) and zero-width characters make the card show different text from what is typed. | Guard rejects control characters other than tab/CR/LF, plus bidi and zero-width characters. Length limit (1024) is now checked before approval, not only at execution. |
| 4 | Medium | **No self-target check in the executor.** `TargetObserver` excludes Boo windows, but `DesktopTools` itself did not, so a future caller could drive Boo's own Approve button. | Executor refuses any target whose PID is Boo's. |
| 5 | Medium | **`open_url` accepted user-info URLs** (`https://bank.example@evil.test`), which read as a trusted host on the card. Raw model string was passed to `ShellExecute`. | `ActionPolicy` and `DesktopTools` deny URLs with user-info; the browser receives `Uri.AbsoluteUri`. |
| 6 | Low | **Start with Windows could point the Run key at a temp copy** (Boo.exe opened straight from the ZIP lives under `%TEMP%`, which any user-level process can replace). | `StartupRegistration.UnstableLocationReason`; Save refuses to enable startup from the temp folder, unticks the box and says why. Removing the entry still always works. |
| 7 | Low | **CI: job token left in `.git/config`** for every later step, and the tag name flowed into inline `${{ }}` PowerShell (script injection via a tag like `v1$(...)`). | `persist-credentials: false` on both workflows; tag label restricted to `[0-9A-Za-z._-]`; script steps read `ZIP_NAME`/`PACKAGE_LABEL` from env instead of inline expressions. |

## Checked and fine

- **API key:** DPAPI CurrentUser, atomic temp-file write, entered via `PasswordBox` (`SecurePassword`), cleared after save, never shown in errors (`SanitizedError` maps to fixed strings). Only sent as a Bearer header to `https://api.openai.com` (origin pinned by `ProviderEndpoint.IsOfficial`). No key found anywhere in git history (`sk-…` scan of all branches).
- **Screenshots:** JPEG held in memory only; never written to disk; OCR in memory; `store: false` on Responses calls. Boo's own windows use display-affinity exclusion and capture pauses if exclusion is unavailable.
- **ActionPolicy:** closed allowlist, generation binding, SHA-256 digest over length-prefixed fields with constant-time compare; non-read-only tools need an exact-match ticket. Only `ApproveButton_OnClick` executes, and it re-checks generation, lease, controls and target HWND/PID/bounds. Cancel (not Approve) gets keyboard focus.
- **Prompt injection:** actions are only offered when the user's own words classify as a task (`RequestIntent`), every action needs a human click, and model click/type IDs are bound to observed UI Automation controls. System prompts label screen/OCR/control data as untrusted.
- **Files:** `ScopedFiles` blocks traversal, ADS, drive paths and reparse points; writes are staged and re-resolved at commit; overwrite needs explicit approval.
- **Undo:** planned locally (no model call), blocked on conflicts/ambiguity, and every step goes through the same approval card.
- **Release:** `contents: read` by default; `contents: write` only on the tag job; package scanned for `.env`/keys/certs.

## Residual risks (not changed)

1. `open_url`, `search`, `inspect` are "read-only" in `ActionPolicy` (frozen test AP-006), so the policy itself does not require a ticket. Today the only executor path still needs an Approve click, but a URL can carry screen data in its query string to an attacker's site; the user must read the URL on the card. Consider making `open_url` approval-gated when AP-006 is next unfrozen.
2. DPAPI CurrentUser without extra entropy: any program running as the same Windows user can decrypt `credential.bin`. That is the normal desktop-app trade-off; Windows Credential Manager would not change it.
3. The Run dialog (Win+R) belongs to `explorer.exe`, so the shell denylist cannot tell it apart from File Explorer.
4. Focus checks call `/v1/decisions` without a `store` flag; confirm that endpoint's retention policy.
5. Actions in workflows are pinned by tag (`@v4`), not commit SHA. The build is not code-signed (SmartScreen warning is expected).
6. Voice tasks pass the realtime model's paraphrase (not the literal transcript) to the Responses call; the user's own transcript still decides whether actions are allowed.
