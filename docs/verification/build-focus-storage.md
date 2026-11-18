# Focus and storage build receipt

## Scope

Builder worktree: `%USERPROFILE%\.codex\worktrees\boo-focus-storage\blue--fo`

Base revision: `b2b515b` (detached managed worktree). The implementation is
limited to `src/Boo.Core/FocusSession.cs`, `src/Boo.Storage/SessionStore.cs`,
`src/Boo.Storage/SettingsStore.cs`, and `src/Boo.Storage/CredentialStore.cs`.
No tests, contracts, project manifests, credentials, or provider calls were
changed or used.

## RED evidence

Before implementation, the pinned wrapper ran against the stubs:

```text
scripts/build.ps1 -Action test -Project tests/Boo.Core.Tests -Filter FullyQualifiedName~FocusPolicyTests
Failed! - Failed: 13, Passed: 0, Total: 13
```

The storage filter also discovered the six storage cases against stubs; the
initial host did not emit a final summary before the process ended. The later
post-implementation run is the authoritative storage result below.

## GREEN evidence

The wrapper used the canonical SDK `%USERPROFILE%\Downloads\blue--fo\.dotnet\dotnet.exe`.

```text
python scripts/test_oracle.py
ORACLE_VERIFIED revision 1 20 files

scripts/build.ps1 -Action test -Project tests/Boo.Core.Tests -Filter FullyQualifiedName~FocusPolicyTests
Passed! - Failed: 0, Passed: 13, Skipped: 0, Total: 13

scripts/build.ps1 -Action test -Project tests/Boo.Integration.Tests -Filter FullyQualifiedName~StoragePrivacyTests
Passed! - Failed: 0, Passed: 6, Skipped: 0, Total: 6
```

The oracle was rerun after implementation and remained verified at revision 1.

## Behavior implemented

- Focus timing, duration clamping, developer 4x clock, pause/resume/end, and
  paused timeline markers follow the pinned Swift behavior.
- Nudge evidence applies the 20-second age limit, 45-second gap reset,
  120–240-second strength span, confidence-as-diagnostic rule, and 120-second
  cooldown.
- History is a version-1 SQLite schema with focused snapshot columns, JSON
  observation values, ID upsert, newest-first listing, and ID-scoped delete.
  Future `PRAGMA user_version` values are rejected without schema changes.
- Settings use JSON and atomic replacement. Credentials use Windows CurrentUser
  DPAPI and atomic replacement; missing credentials return null and malformed
  protected data yields a sanitized actionable error. No raw frame/audio/OCR
  field is persisted.

## Remaining gaps

This is focused unit/integration evidence only. Native Windows lifecycle,
screen capture exclusion, microphone behavior, live provider access, packaged
build behavior, and independent QA remain outside this receipt.

## Canonical integration verification

Root integrated only the four owned production files and this receipt. The
first canonical test run exposed stale incremental output: Copy-Item retained
source timestamps older than the previously compiled stub DLL. The source was
correct but the focus test loaded the old stub (13 failures). Root preserved
that failure, rewrote the same source bytes to refresh build inputs, and reran
the affected solution tests. Focus: 13 passed. Storage: 6 passed. Canonical TRX
receipts end in `2026-11-07_10_21_54_net10.0.trx` and
`2026-11-07_10_21_56_net10.0.trx`. Subsequent worktree integration writes bytes
with current timestamps before verification; it does not reuse compiled output.
