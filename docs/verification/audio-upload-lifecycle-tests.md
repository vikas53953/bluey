# Audio upload lifecycle boundary tests

Date: 2026-11-07

`tests/Boo.Integration.Tests/AudioUploadLifecycleTests.cs` adds public-boundary
coverage for accepted-frame order and payload ownership, the one-writer
constraint, invalid PCM16 bounds before and after stop, explicit stop and
parent cancellation, sanitized exactly-once transport failure, and repeated
stop/disposal after a pre-cancelled construction. It also probes the plan's
nonblocking `Stop()` contract with a cancellation callback that deliberately
blocks until released. All synchronization uses task handshakes and bounded
`WaitAsync` timeouts; cleanup releases blocked callbacks before disposing.

## Command

```powershell
./scripts/build.ps1 -Action test -Configuration Release -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -NoRestore -Filter AudioUploadLifecycleTests
```

## Result

The Release build succeeded. The focused suite found six passing tests and one
genuine lifecycle defect:

```text
Failed Boo.Integration.Tests.AudioUploadLifecycleTests.Stop_does_not_wait_for_a_blocked_transport_cancellation_callback
System.TimeoutException: The operation has timed out.
at AudioUploadLifecycleTests.Stop_does_not_wait_for_a_blocked_transport_cancellation_callback()

Failed: 1, Passed: 6, Skipped: 0, Total: 7
```

The timeout is at the handshake that waits for `Stop()` to return while its
transport cancellation callback is blocked. The test cleanup releases that
callback and completes the runner. This indicates that the current queue
invokes cancellation synchronously on the caller path; it is not a provider,
microphone, UI, credential, or native acceptance test.
