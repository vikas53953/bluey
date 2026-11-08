# Audio backlog tracer: genuine RED

Date: 2026-11-07

The public transport-boundary tracer is present in
`tests/Boo.Integration.Tests/AudioBacklogTests.cs`. It waits for the first
transport call to enter a cancellation-cooperative blocked task, accepts eight
pending 960-byte PCM16 frames, rejects the ninth, checks exactly-once failure,
rejects later frames, and observes completion/disposal without inspecting
private queue state.

## Command

```powershell
./scripts/build.ps1 -Action test -Configuration Release -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -NoRestore -Filter AudioBacklogTests
```

## Result

The project built successfully, then the filtered test ran and failed against
the approved throwing `AudioUploadQueue` stub:

```text
Failed Boo.Integration.Tests.AudioBacklogTests.Blocked_transport_rejects_ninth_pending_frame_and_terminates_cleanly
System.NotImplementedException: The method or operation is not implemented.
at Boo.Providers.AudioUploadQueue.TryEnqueue(Byte[] pcm)
  in src/Boo.Providers/AudioUploadQueue.cs:line 9

Failed: 1, Passed: 0, Skipped: 0, Total: 1
```

This is genuine RED evidence for the missing public queue behavior. No live
provider, microphone, UI, credential, or external API was used.
