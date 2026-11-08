# Control read lifetime test-author receipt

Date: 2026-11-07
Base: `592e2cfc1f9c4c4203c6512c6ccb0cddad99a421`

## Scope and ownership

This test-author unit added only:

- `tests/Boo.Core.Tests/ControlReadCoordinatorTests.cs`
- `docs/verification/control-read-lifetime-tests.md`

This receipt records the pre-freeze correction requested during review: the
queued-cancellation counter now counts the succeeding callback; faulted and
callback-canceled cases use one held failing callback plus one queued success;
the synchronous-throw case remains a compatibility check and does not invent a
FIFO or extra serialization requirement; and in-flight cancellation asserts
the held boundary before releasing the callback.

`behavior_changed=false`: no production code, existing tests, contracts, or
native/API/UI code changed. The supplied `ControlReadCoordinator` scaffold in
`src/Boo.Core/ControlReadCoordinator.cs` remains uncoordinated.

The tests call only the public
`ControlReadCoordinator.ReadAsync(Func<Task<ControlObservation>>, CancellationToken)`
seam. Controlled callback tasks and `TaskCompletionSource` entry/release
signals prove the observable admission boundary. Every test has bounded
`WaitAsync` guards and `finally` cleanup that releases and drains controlled
tasks; no sleep-based evidence or private/internal access is used.

## Coverage

The new tests cover:

- one-at-a-time callback admission and exact result-instance forwarding;
- pre-canceled callers never invoking their callback;
- queued cancellation completing without callback invocation and leaving the
  coordinator usable;
- in-flight cancellation retaining admission until callback settlement and
  discarding a late successful result;
- faulted, synchronously throwing, and callback-canceled reads releasing the
  next queued read;
- null callbacks producing `ArgumentNullException`.

Existing tests inspected before authoring were
`ControlObservationTests.cs`, `RequestCoordinatorTests.cs`,
`CaptureActivityTests.cs`, `ControlObservationProviderTests.cs`, and
`CaptureLifecycleValidationTests.cs`. They remain unchanged.

## Focused Release run

Command:

```powershell
./scripts/build.ps1 -Action test -Project tests/Boo.Core.Tests/Boo.Core.Tests.csproj -Configuration Release -Filter FullyQualifiedName~ControlReadCoordinatorTests
```

The project restored/built successfully and test discovery succeeded. The
unchanged scaffold produced genuine behavioral RED:

```text
Failed: 7, Passed: 1, Skipped: 0, Total: 8
```

TRX: `TestResults/vikasmit_VIKASMIT_2026-11-07_14_47_24_net10.0.trx`

The seven expected behavioral failures were:

- `Admits_one_callback_at_a_time_and_forwards_the_exact_result` — second
  callback entered before the first callback was released;
- `A_pre_cancelled_caller_never_invokes_the_callback` — scaffold returned the
  callback result instead of cancellation;
- `A_queued_cancellation_never_invokes_the_callback_and_leaves_the_coordinator_usable`
  — canceled waiter timed out while its scaffold callback remained held;
- `In_flight_cancellation_holds_admission_and_discards_a_late_success` — late
  callback success was returned instead of caller cancellation;
- `A_faulted_callback_releases_admission_for_the_next_read` — faulted callback
  entered before the active callback settled;
- `A_canceled_callback_releases_admission_for_the_next_read` — canceled
  callback entered before the active callback settled;
- `A_null_callback_is_rejected_with_ArgumentNullException` — scaffold raised
  `NullReferenceException` instead.

`A_synchronously_throwing_callback_releases_admission_for_the_next_read`
passed. It deliberately verifies only exception preservation and a following
successful read; because the supplied scaffold also passes this sequential
compatibility check, it is not standalone coordination evidence.

These failures are behavior failures against the known scaffold, not compiler,
restore, discovery, or timeout-only harness failures. The bounded timeout in
the queued-cancellation case is a failure guard; the admission assertions use
controlled callback signals.

## Deliberate limits

This unit does not claim to interrupt a native UI Automation call already
entered, validate Windows UIA behavior, prove `ControlObservationReader`
wiring, exercise WPF/GUI/native APIs, or test packaged delivery. It also does
not add disposal, timeout detachment, retry, or alternate public seams. The
builder owns the Core implementation and Windows integration; root owns
integration review, full Release checks, and final freeze.
