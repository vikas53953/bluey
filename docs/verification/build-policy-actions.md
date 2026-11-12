# Boo policy/actions builder receipt

Worktree: `%USERPROFILE%\.codex\worktrees\boo-policy-actions\blue--fo`
at detached ref `b2b515b` (`b2b515bf7023ac665a2c4f3d159452a6df5c0389`).

## Changed files

- `src/Boo.Core/ActionPolicy.cs`
- `src/Boo.Core/RequestCoordinator.cs`
- `src/Boo.Windows/DesktopTools.cs`

The implementation keeps the initial typed allowlist, explicit task authority,
SHA-256 approval binding, serialized cancellation/commit generation, canonical
contained file paths, staged/read-back-verified writes, and fail-closed native
target checks. Search returns an explicitly unverified provider handoff and
never invokes a shell. UI Automation is attempted before bounded SendInput;
SendInput-only click/type results remain partial/unverified without readback.
SendInput fallback additionally activates only the approved HWND, requires
foreground/focus/process/integrity revalidation, and checks WindowFromPoint for
clicks; missing click coordinates require an observed UI Automation element.

## RED baseline

```powershell
.\scripts\build.ps1 test -Project tests/Boo.Core.Tests -Filter "FullyQualifiedName~ActionPolicyTests|FullyQualifiedName~ScopedFilesTests|FullyQualifiedName~RequestCoordinatorTests"
```

Initial stubs produced 30 failures and 1 skipped reparse fixture. The failures
were the expected `NotImplementedException`/stub RED signal; test discovery
and the pinned SDK were available.

## GREEN checks

```powershell
.\scripts\build.ps1 build -Project src/Boo.Windows/Boo.Windows.csproj
.\scripts\build.ps1 test -Project tests/Boo.Core.Tests -Filter "FullyQualifiedName~ActionPolicyTests|FullyQualifiedName~ScopedFilesTests|FullyQualifiedName~RequestCoordinatorTests"
```

- Windows project build: succeeded, 0 warnings, 0 errors.
- Focused core suite: 30 passed, 0 failed, 1 skipped. The skipped test is the
  host-dependent reparse-link fixture; native reparse acceptance remains
  pending on a fixture-capable environment.
- Native desktop actions, live provider search, protected/elevated target
  handling, and packaged-app acceptance were not run by this unit.

Frozen-oracle check: `py scripts/test_oracle.py` returned
`ORACLE_VERIFIED revision 1 20 files`.

## Canonical integration

Root reviewed the actual changes, including separate committed/readback state
for writes and fresh foreground/focus/hit-window checks for SendInput. On the
canonical checkout the focused suite passed 30 cases with 1 explicit host
skip (`2026-11-07_10_22_27_net10.0.trx`), the Windows project built with zero
warnings/errors, and oracle revision 2 verified all 22 files. Actual desktop
actions remain native-QA work; these checks are not an end-to-end task pass.
