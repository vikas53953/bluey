# Capture deadline tracer: genuine RED

Date: 2026-11-07 (Asia/Calcutta)

The public capture-boundary tracer is present in
`tests/Boo.Integration.Tests/CaptureDeadlineTests.cs`. It injects a fake
`IFrameCaptureBackend` that receives deadline cancellation while no frame
arrives, then holds a cleanup handshake open. The test requires the public
`ScreenCapture` call to remain pending until cleanup completes and finally
return a readable `CaptureLimitationException`. It uses no native capture,
microphone, provider, credential, or UI calls.

## Command

```powershell
./scripts/build.ps1 -Action test -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -Configuration Release -NoRestore -Filter CaptureDeadlineTests
```

## Result against the approved throwing stub

The referenced projects and integration-test assembly built successfully. The
filtered test then failed with the expected stub signal:

```text
Failed Boo.Integration.Tests.CaptureDeadlineTests.Missing_frame_deadline_cancels_backend_and_waits_for_cleanup_before_limitation
System.NotImplementedException : The method or operation is not implemented.
at Boo.Windows.ScreenCapture.CaptureAsync(...)
in src/Boo.Windows/ScreenCapture.cs:line 35

Failed: 1, Passed: 0, Skipped: 0, Total: 1
```

The command exited with code `1`. Raw test results:
`TestResults/<user>_<machine>_2026-11-07_11_24_42_net10.0.trx`.

This is genuine RED evidence for the missing injected capture lifetime and
deadline behavior. No production, existing-test, contract, or manifest files
were changed by this test-author slice.
