# Control read lifetime builder receipt

Date: 2026-11-07  
Baseline: `0fa12a87f2088d09a04ebf407076714972b9aab8`

## Scope and ownership

This builder changed only:

- `src/Boo.Core/ControlReadCoordinator.cs`
- `src/Boo.Windows/ControlObservationReader.cs`
- this receipt

The frozen tests, contracts, oracle, and unrelated source files remain
unchanged. The implementation uses an instance-owned `SemaphoreSlim` to admit
one callback at a time. It checks caller cancellation after admission and
before invoking the callback, awaits the callback task through settlement, and
checks cancellation again before forwarding a successful result. The gate is
released in `finally`, preserving callback faults and cancellation while
allowing the next caller to proceed.

`ControlObservationReader` now uses one process-shared coordinator for every
reader instance. Its MTA worker is constructed and started only inside the
admitted callback. Existing target validation, cancellation checks between
native calls, privacy filtering, copied snapshots, fixed errors, and worker
behavior remain unchanged.

## Red baseline

Before implementation, the focused Release command was run against the
scaffold:

```text
./scripts/build.ps1 -Action test -Project tests/Boo.Core.Tests/Boo.Core.Tests.csproj -Configuration Release -Filter FullyQualifiedName~ControlReadCoordinatorTests
```

Result: `Failed: 7, Passed: 1, Skipped: 0, Total: 8`.

TRX: `TestResults/<user>_<machine>_2026-11-07_14_51_25_net10.0.trx`

The failures were the expected missing serial admission, pre-cancellation,
queued cancellation, in-flight cancellation/late-success discard, faulted
callback release, callback-canceled release, and null-callback behavior. The
synchronous-throw case passed as the frozen compatibility check.

## Green focused verification

After implementation, the same focused command passed:

```text
./scripts/build.ps1 -Action test -Project tests/Boo.Core.Tests/Boo.Core.Tests.csproj -Configuration Release -Filter FullyQualifiedName~ControlReadCoordinatorTests
```

Result: `Passed! - Failed: 0, Passed: 8, Skipped: 0, Total: 8`.

TRX: `TestResults/<user>_<machine>_2026-11-07_14_52_13_net10.0.trx`

The scoped Windows Release build also passed with zero warnings and zero
errors:

```text
./scripts/build.ps1 -Action build -Project src/Boo.Windows/Boo.Windows.csproj -Configuration Release
```

## Oracle and diff checks

```text
python scripts/test_oracle.py
ORACLE_VERIFIED revision 14 37 files

git diff --check
```

Both checks passed. No tests or frozen oracle files were edited.

## Limits

This unit proves callback admission and task ownership through controlled
public-seam tests. It does not interrupt a UI Automation provider call already
entered, prove Windows UI Automation behavior, validate packaged delivery, or
change action execution/request-coordinator lifetime. No timeout detachment,
dedicated idle thread, disposal, retry, or alternate public API was added.
