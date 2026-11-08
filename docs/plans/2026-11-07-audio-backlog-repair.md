# Audio backlog and session teardown repair

Status: PLAN_APPROVED by fresh Advisor v2. Root owns this plan. It repairs the
voice lifecycle in the accepted Boo Windows plan; it does not change product
scope or relax native acceptance.

## Observed defect

AudioSession posts each PCM frame to the UI context. MainWindow then creates
an unobserved send task for every frame, and RealtimeClient serializes these
tasks with a semaphore. A slow connection can retain unbounded audio and
tasks. Queue failures are not surfaced to the user.

## Public seam and bounded behavior

Add `Boo.Providers.AudioUploadQueue`, constructed with an asynchronous,
cancellation-cooperative `Func<byte[], CancellationToken, Task>` transport
and optional parent cancellation token. Its public surface is `TryEnqueue`,
`Stop`, `Completion`, `Failed` event, and `IAsyncDisposable`.

- Permit eight pending frames and one in-flight frame, each at most 4,800
  bytes of PCM16. Retained payload is bounded at 43,200 bytes. Normal capture
  produces 960-byte, 20 ms frames.
- Null input throws ArgumentNullException; empty/odd input throws
  ArgumentException; oversized input throws ArgumentOutOfRangeException.
  Validate input before checking stopped state. Clone accepted data.
- Preserve accepted order with one writer. A full queue enters terminal
  failure, drains pending data, cancels transport, and emits one sanitized
  failure. Do not silently drop speech and continue.
- Stop linearizes against starting sends, clears pending frames, and returns
  without waiting for transport. No later send begins; an already-started send
  receives cancellation. Completion and DisposeAsync observe the same worker.
- Ordinary Stop/parent cancellation is not a Failed event. Transport errors
  are reported once using a fixed safe message, without raw provider errors.

## Ownership and red-to-green sequence

Root provides only the public throwing stub. A separate test author adds the
blocked-transport overflow tracer and records a genuine behavior failure.
Root extends the hash oracle additively. Only then may a fresh builder
implement the queue and direct AudioSession PCM callback delivery. The
builder does not edit tests, the oracle, or frozen contracts.

Further independent public-boundary tests cover order, no send after Stop,
parent cancellation, single writer, invalid-frame bounds, payload ownership,
and sanitized exactly-once failure. Add each follow-up file before any repair
it requires. Frozen earlier hashes remain unchanged.

Root owns MainWindow integration in disjoint files. Each microphone session
gets a fresh AudioSession whose callback captures that exact queue; old
callbacks cannot route through a newly assigned field. Failed remains
UI-marshaled, with source identity, lease/generation, and shutdown guards.

Stop the queue and microphone before replacing the request/realtime session,
and on StopListening, global Stop, exit, setup failure, overflow, send failure,
or disconnect. Observe asynchronous disposal without blocking the dispatcher.
Dispatch shutdown/rejection must not create unobserved exceptions.

Handle provider disconnect like the existing error stop/pause path. Display
failed/incomplete/cancelled response completion as such, without a success
claim. Existing NAT-009, NAT-010, and NAT-013 remain the native oracle.

## Verification and boundaries

Run independent queue tests, all existing deterministic tests, Release build,
oracle verification, and diff checks. Queue tests establish transport-boundary
behavior only. They do not prove MainWindow callback wiring, microphone
device behavior, native disconnect UX, or spoken-turn latency. Those cases
remain required packaged acceptance checks.

No credentials, paid API calls, microphone activation, external files, or GUI
automation are needed for this repair. Do not alter the running candidate1
payload. Package a new candidate only after the repair is integrated/reviewed.
