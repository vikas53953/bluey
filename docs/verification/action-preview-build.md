# Action preview repair — GREEN verification

Date: 2026-11-07

This receipt covers the bounded concrete-approval repair after the independent
`ActionPreviewTests` RED tracer. The implementation is limited to the Core
preview/resolver, the Windows file-action execution checks, and this receipt.
The preview keeps an immutable `FrozenDictionary` request copy and ordinal
sorted `ImmutableArray` fields, retains full argument values, and resolves file
destinations through the same `ScopedFiles` path policy used before execution.
The resolver enforces the selected root, existing parent, UTF-8 4 MiB limit,
path containment, regular-file targets, and explicit overwrite semantics. It
is called both before and inside the DesktopTools commit boundary. This does
not claim to repair native file-identity TOCTOU behavior; that remains tracked
separately.

## Focused GREEN

Command:

```text
./scripts/build.ps1 -Action test -Project tests/Boo.Core.Tests/Boo.Core.Tests.csproj -Configuration Release -NoRestore -Filter ActionPreviewTests
```

Result: exit code 0. Release compilation succeeded; 1 test passed, 0 failed,
0 skipped. The test covered the raw target versus path override, canonical
destination, multiline contents, ordinal field order, and mutation of the
original argument dictionary after preview creation.

## Build and oracle checks

The pinned restore completed successfully before the focused run. The assigned
Windows project then built in Release with zero warnings and zero errors:

```text
./scripts/build.ps1 -Action build -Project src/Boo.Windows/Boo.Windows.csproj -Configuration Release -NoRestore
```

The frozen independent oracle and whitespace checks passed after the source
changes:

```text
python scripts/test_oracle.py
ORACLE_VERIFIED revision 6 27 files
git diff --check
```

No live provider, native UI, microphone, credentials, or real user files were
used. The intentionally red `CaptureDeadlineTests` lane was not included in
this focused approval repair.
