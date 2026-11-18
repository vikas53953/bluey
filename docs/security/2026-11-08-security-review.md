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

---

# Second pass — updater, Claude/Gemini providers, voice focus commands (8 Nov 2026, 20:15 UTC)

Scope: Velopack installer + GitHub-token auto-updater (`src/Boo.Core/Updates.cs`, `src/Boo.App/UpdateService.cs`,
`MainWindow.Updates.cs`), Anthropic/Gemini clients and per-provider key storage, voice focus tools
(`VoiceCommands.cs`, `MainWindow.Voice.cs`). Fix branch: `claude/project-thread-rv7rji` (off `lane/face`).
No existing test was edited; new tests are in `tests/Boo.Core.Tests/SecurityHardening2Tests.cs`.

## Threat model

| Asset | Attacker | Path | Control |
|---|---|---|---|
| Installed binary | Anyone who can write a Release to the repo | Publish a higher version; Boo installs it (unsigned) | Repo is private and pinned in code; only repo collaborators can publish. **Token scope matters** (below). |
| Installed binary | Network attacker | Tamper feed or package in transit | HTTPS to github.com only (`IsTrustedRepositoryUrl` rejects any other host/scheme/user-info); Velopack checks package SHA against `releases.win.json`. |
| Installed binary | Attacker with a stale release | Downgrade / reinstall | `UpdateVersionGate.IsUpgrade`: strict SemVer 2 upgrade only, pre-releases only when opted in, unparsable current version = no update. |
| GitHub token | Same-user malware, log scraping | Read `update-token.bin`, find it in status text | DPAPI CurrentUser (same trade-off as API keys). Status line passes every error through `SanitizeError` (token-shaped text becomes `[token]`); hint shows prefix + last 4 only. |
| API keys (3 vendors) | Same-user malware, wrong-host send, header injection | Read `credential*.bin`; key sent elsewhere; CR/LF in a header value | DPAPI per vendor; origin pinned per vendor (`AiProviders.IsOfficialOrigin`, https + exact host + 443) before every request; **new shape check at save time** (below). |
| User's screen data / actions | Prompt injection in Claude/Gemini replies or web-search results | Model proposes actions or URLs | Unchanged pipeline: replies are structured JSON decoded into the same `StructuredReplies` types, actions still need `RequestIntent` = task, an Approve click, `WindowActionGuard`, and `ActionPolicy` tickets. Error bodies never reach exceptions (`ProviderHttpFailures` classifies by status/code only). |
| Focus session, listening state | Audio from other apps (video, call, TV) in hands-free Talk mode | Model hears "stop focusing" / "bye" and calls `focus_end` / `go_to_sleep` | **New hands-free gate** (below). |

## Fixed

| # | Severity | Finding | Fix |
|---|----------|---------|-----|
| 8 | Medium | **Hands-free voice could end or pause the user's focus session.** In Talk mode (server VAD) the microphone is open continuously and the Realtime model cannot tell the user from a speaker, so a video saying "ok stop, that's enough" could call `focus_end` and silently end a 25-minute session. Hold-to-talk turns are deliberate and were never the problem. | `VoiceFocusPolicy.Decide(..., handsFree)`: when the session is hands-free (`!RealtimeClient.PushToTalk`) and a session is running/paused, `focus_pause` and `focus_end` are refused with a spoken hint to hold the eyes or the shortcut. `focus_start`, `focus_resume`, `focus_status` and `go_to_sleep` are unchanged (going quiet only reduces attack surface). |
| 9 | Low | **Provider keys were stored unchecked.** Any pasted text (with spaces, line breaks, control or non-ASCII characters) went straight to DPAPI and later into `Authorization` / `x-api-key` / `x-goog-api-key` headers (Gemini uses `TryAddWithoutValidation`). .NET rejects CR/LF in header values, but the failure then surfaces as an opaque request error and the bad key stays on disk. | `AiProviders.CredentialRejectReason(provider, key)` refuses whitespace, control, non-printable-ASCII and keys shorter than 20 chars before saving; Settings reports "the Gemini key was not saved: …" without echoing the text. Saved keys are trimmed. |
| 10 | Low | **No guidance on GitHub token scope.** A classic `ghp_` token cannot be limited to one repository or to read-only contents; if it leaks (DPAPI is same-user), the holder can publish a release the updater would install. The UI accepted it silently. | `GitHubAccessToken.ScopeNote` adds a warning to the Settings hint for classic/OAuth tokens and recommends a fine-grained token limited to this repository with Contents: read. Existing tokens keep working (the token tests are frozen). |

## Checked and fine

- **Updater**: repository URL is a code constant validated by regex (https, github.com, exactly owner/repo); `GithubSource` gets the token only for api.github.com; download redirects to `objects.githubusercontent.com` drop the Authorization header (HttpClient behaviour). Opt-in, 24 h interval, manual Install click, `_onStop()` before `WaitExitThenApplyUpdates`. Portable builds never update. Token entry uses `PasswordBox`, cleared after read, written only after settings saved.
- **Anthropic/Gemini**: key in header only, origin pinned, error bodies read at most `MaxErrorBodyChars` and never surfaced; fixed-string exception messages; Gemini model id URL-escaped; grounding source URIs go through the shared source normaliser (same as OpenAI).
- **Voice tools**: tool arguments are untrusted (`VoiceCommandParser` cleans and caps the task at 120 chars, clamps minutes 1-240); no voice tool can type, click or open anything; the handler is only honoured for the current `RealtimeClient` instance.

## Residual risks (added)

7. The update feed trusts whoever can publish a GitHub Release in `vikas53953/bluey`; the package is not code-signed, so repository collaborator hygiene (2FA, no `repo`-scoped classic tokens lying around, branch protection on tags) is the real control. Consider signing before a public release.
8. DPAPI without entropy still applies to `update-token.bin` and all three `credential*.bin` files.
9. Hands-free `focus_start` from a stray sentence is still possible (it only starts a timer). A transcript check against the model's tool call would tighten this further.
10. Nothing in this pass was run on Windows; CI (`windows-latest`) is the verification.
