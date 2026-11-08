# Capture lifecycle and validation followups

Date: 2026-11-07 (Asia/Calcutta)

The public OS-boundary followups are present in
`tests/Boo.Integration.Tests/CaptureLifecycleValidationTests.cs`. A
deterministic fake `IFrameCaptureBackend` uses task-completion handshakes and
bounded watchdogs to verify:

- serialization through backend cleanup;
- queued caller cancellation without backend entry;
- deadline creation only after lifetime acquisition;
- owned-window snapshot copying before waiting;
- caller-cancellation precedence over a deadline;
- rejection of a late frame from a backend that ignores cancellation;
- propagation of backend cleanup failure without success; and
- pre-backend rejection of empty/zero/owned handles, invalid geometry, and
  timeout values outside 1 through 4,294,967,294 milliseconds.

No native capture, microphone, provider, credential, UI, or external API was
used. Existing tests, production files, contracts, and the oracle were not
changed by this test-author slice.

## Command

```powershell
./scripts/build.ps1 -Action test -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -Configuration Release -NoRestore -Filter CaptureLifecycleValidationTests
```

## Result

The referenced projects and integration-test assembly built successfully. The
focused suite passed:

```text
Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 693 ms
```

Raw test results:
`TestResults/vikasmit_VIKASMIT_2026-11-07_11_41_43_net10.0.trx`.
