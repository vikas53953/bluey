# Capture lifetime repair: build receipt

Date: 2026-11-07 (Asia/Calcutta)

The approved capture repair keeps `ScreenCapture` as the application service,
injects the `IFrameCaptureBackend` boundary, and moves the existing Windows
Graphics Capture implementation to `WindowsFrameCaptureBackend`. The service
copies owned handles before validation, validates nonzero handles and finite
geometry, serializes complete backend lifetimes, starts its five-second
production deadline only after acquiring the gate, and keeps caller
cancellation distinct from deadline limitation. The backend registers frame
cancellation before `StartCapture`, disposes late frames, checks cancellation
after JPEG readback, and restores window capture affinity after WGC cleanup.

## Verification

```powershell
./scripts/build.ps1 -Action restore
```

Result: succeeded against the canonical pinned SDK/cache.

```powershell
./scripts/build.ps1 -Action test -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -Configuration Release -NoRestore -Filter CaptureDeadlineTests
```

Result: `Passed: 1, Failed: 0, Skipped: 0, Total: 1`.
The test held backend cleanup after deadline cancellation and observed the
Raw result: `TestResults/vikasmit_VIKASMIT_2026-11-07_11_31_55_net10.0.trx`.

```powershell
./scripts/build.ps1 -Action build -Configuration Release -NoRestore
```

Result: succeeded with `0 Warning(s)` and `0 Error(s)`.

```powershell
python scripts/test_oracle.py
```

Result: `ORACLE_VERIFIED revision 6 27 files`.

This receipt does not claim native GPU interruption, live capture, physical
latency, or acceptance of the intentionally separate `ActionPreviewTests`
repair lane.
