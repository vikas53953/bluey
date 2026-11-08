# Audio integration review

Date: 2026-11-07

Scope: read-only review of the current working tree against `a809e53`, using
`docs/plans/2026-11-07-audio-backlog-repair.md`,
`src/Boo.Providers/AudioUploadQueue.cs`, `src/Boo.Windows/AudioSession.cs`,
and the current `MainWindow.xaml.cs`. The frozen `AudioBacklogTests` result and
revision-4 oracle are transport-boundary evidence only; this note is not an
acceptance claim.

## Findings

### 1. High — `DisposeAsync` can deadlock on the queue gate

`AudioUploadQueue.DisposeAsync` takes `_gate` and invokes `DisposeCoreAsync`
before releasing it (`AudioUploadQueue.cs:112-119`). The core path calls
`Stop`, and the stop/overflow paths synchronously call
`_transportCancellation.Cancel()` (`AudioUploadQueue.cs:91-110,
230-245`). Cancellation callbacks run inline. More importantly, if the queue
was already stopped and both awaited tasks are complete, the entire core path
can run synchronously while the caller still owns `_gate`; it then calls
`_parentRegistration.Dispose()` (`AudioUploadQueue.cs:121-134`).

Concrete deadlock: one thread starts parent-token cancellation and its
registered callback blocks trying to enter `Stop`'s `_gate`; another thread
calls `DisposeAsync` after an explicit stop, reaches
`_parentRegistration.Dispose()` while holding `_gate`, and waits for that
callback. Neither thread can progress. A transport cancellation callback that
re-enters or waits on queue APIs has the same lock-order hazard on the fresh
queue path.

Minimal fix direction: publish a single disposal task/placeholder while under
the lock, then start/execute the disposal core after releasing `_gate`; never
hold `_gate` across `Cancel`, registration disposal, or user callbacks. Keep
resource disposal behind an explicit cancellation-completed task.

### 2. High — `Stop` is not nonblocking for synchronous transport work or cancellation callbacks

The worker invokes the caller-provided `_send` delegate while holding `_gate`
(`AudioUploadQueue.cs:142-165`). A valid `Func<byte[], CancellationToken,
Task>` may execute synchronous work before returning its task. If that work
blocks, a concurrent `Stop` cannot acquire `_gate` to linearize the stop or
cancel the in-flight operation. Even after the lock is released,
`CancelTransport` invokes `CancellationTokenSource.Cancel()` synchronously,
so a transport callback can make `Stop` wait arbitrarily or wait on a queue
operation that needs the same lifecycle progress (`AudioUploadQueue.cs:230-245`).
This is directly on the UI path through `MainWindow.StopListening` and
`StopAll`.

Minimal fix direction: reserve/linearize the worker's send with an atomic
state transition, invoke the user delegate outside `_gate`, and let `Stop`
win the transition before any not-yet-started send. Schedule cancellation
callbacks independently of the caller and make `DisposeAsync` await their
completion; do not rely on synchronous `Cancel` for a nonblocking stop.

### 3. Medium — realtime setup failure does not always invalidate the voice lease

`TalkButton_OnClick` catches connection/setup failures and calls
`StopListening` (`MainWindow.xaml.cs:452-487`). Before the queue is created and
before `_realtimeConnected` is set, `StopListening` computes
`hadVoiceSession` from only `_listening`, `_realtimeConnected`, and
`_audioUploads` (`MainWindow.xaml.cs:490-505`). That is false for a failed
`ConnectAsync`, so it does not stop the coordinator, cancel the request lease,
clear `_requestLease`, or bump `_generation`; it only nulls `_realtime` later
(`MainWindow.xaml.cs:519-524`). The failed voice setup therefore leaves its
request lease current until some later request or global stop.

Minimal fix direction: use an explicit setup-failure teardown that invalidates
the current voice lease even when capture/queue activation never completed (or
include the active request/realtime setup state in the invalidation condition),
and dispose the failed realtime instance in a guaranteed teardown path.

### 4. Medium — fire-and-forget realtime interruption can fault or never reach disposal

The new stop/disconnect paths start `InterruptRealtimeAsync` without awaiting
it (`MainWindow.xaml.cs:519-524` and `1348-1358`). The helper sends an
interrupt with `CancellationToken.None`, awaits that send before disposal, and
only catches `InvalidOperationException` (`MainWindow.xaml.cs:1432-1442`). A
closed or half-open socket can produce `WebSocketException`,
`ObjectDisposedException`, or an unbounded send wait. Those cases leave the
discarded task faulted or prevent `RealtimeClient.DisposeAsync` from running,
which defeats shutdown/stale-callback cleanup.

Minimal fix direction: interrupt with a bounded cancellation/timeout or abort
the client first, always attempt/await disposal in `finally`, and observe all
expected transport failures on the fire-and-forget path.

## Verification boundary

The current evidence proves the bounded queue overflow tracer only. No
AudioUploadLifecycleTests result was available in this review, and no native
microphone, provider-disconnect UX, dispatcher-shutdown, playback-device, or
spoken-turn latency behavior was proven.

## Re-review appendendum

Date: 2026-11-07

The four findings above were rechecked against the repaired working tree. The
reported final lifecycle result is eight audio tests green; the conclusions
below are still transport/source review, not native acceptance.

- Finding 1 is resolved in the inspected queue. `DisposeAsync` publishes a
  single owner task under `_gate`, starts `DisposeCoreAsync` after releasing
  the gate, awaits asynchronous transport cancellation, and awaits
  `_parentRegistration.DisposeAsync` outside the queue lock
  (`AudioUploadQueue.cs:113-149, 245-262`). The prior parent-callback versus
  registration-disposal deadlock path is no longer present.
- Finding 2 is resolved for the documented public contract. `Stop` now
  schedules `CancelAsync` and returns without synchronously running transport
  cancellation callbacks (`AudioUploadQueue.cs:92-110, 245-262`). The worker
  still invokes `_send` under `_gate` (`AudioUploadQueue.cs:159-180`), but the
  public XML contract now requires an asynchronous transport that returns
  promptly and honors cancellation. A delegate with a blocking synchronous
  prefix remains outside that supported contract; preserving send/stop
  linearization here is intentional.
- Finding 3 is resolved in both paths reviewed. `StopListening` treats an
  existing `_realtime` setup as a voice session and invalidates the lease
  (`MainWindow.xaml.cs:496-524`). Typed requests detach and dispose a failed
  realtime instance while retaining the request lease for the Responses
  fallback (`MainWindow.xaml.cs:379-417`).
- Finding 4 is resolved in the inspected helper. Realtime interruption has a
  two-second cancellation bound, catches detached-session transport failures,
  always attempts disposal in `finally`, and performs no stale UI writes
  (`MainWindow.xaml.cs:1438-1454`).

No reviewed lifecycle defect remains within the supported transport contract.
Native microphone/device behavior, real provider disconnect UX, dispatcher
shutdown, reusable playback hardware, and spoken-turn latency remain
unproven and require packaged/native acceptance.
