# Capture indicator tracker build receipt

Date: 2026-11-07
Base: `26384038bef352237059ceb4f86a5025c0b4cb39`

The Core unit implements `CaptureActivityTracker` and
`CaptureActivityOperation` at the public seam defined by the frozen capture
indicator tests. A single tracker lock protects operation state, active
membership, and snapshot reads. Each operation can be started once; disposal
is idempotent and removes only that operation. Snapshots copy the oldest
still-active metadata for each display ID in activation order and are exposed
through a read-only collection. No reset or clear-all API was added.

## Verification

Focused Release tests:

```powershell
scripts/build.ps1 -Action test -Project tests/Boo.Core.Tests/Boo.Core.Tests.csproj -Configuration Release -Filter FullyQualifiedName~CaptureActivityTests
```

Result: **PASS** — 11 passed, 0 failed, 0 skipped.

TRX: `TestResults/<user>_<machine>_2026-11-07_13_17_58_net10.0.trx`

Core Release build:

```powershell
scripts/build.ps1 -Action build -Project src/Boo.Core/Boo.Core.csproj -Configuration Release -NoRestore
```

Result: **PASS** — 0 warnings, 0 errors.

Frozen oracle:

```powershell
python scripts/test_oracle.py
```

Result: **ORACLE_VERIFIED revision 11 34 files**.

This receipt covers the Core tracker only. WPF indicator integration, native
capture timing, provider waits, cancellation cleanup, and packaged acceptance
remain outside this unit and are owned by the root integration work.

Release ownership: the Core tracker implementation is complete and released
to the root for integration and review. No commit was created.
