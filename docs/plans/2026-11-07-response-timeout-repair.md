# Response deadline and cancellation reporting repair

## Evidence and intended behavior

Candidate8 passed the former frame-readback failure and showed sharing active.
Its one screen-help request produced no reply and showed `Stopped before the
response completed` about 48 seconds after submission, before QA pressed Stop.
The exact receipt is `docs/verification/native-teaching-candidate8.md`.

The app configures a 45-second HttpClient timeout. ResponsesClient rethrows
every OperationCanceledException; MainWindow labels any such error as Stopped
even while the caller request is still current. The timing is consistent with
HTTP timeout, but the native exception cause was not captured and is not proven.
The source-confirmed defect is conflating transport cancellation with user Stop.

ResponsesClient also uses ResponseHeadersRead, whose HttpClient timeout stops
at headers. Microsoft documents that content reading needs a separate bound:
https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0

Keep the configured timeout value and selected models unchanged. No retries,
extra live calls, performance claims, arbitrary new limits, or credential work.

## Implementation contract

- Read the supplied HttpClient.Timeout once. ResponsesClient applies it to the complete
  asynchronous HTTP response operation, including body reading, through one
  owned linked cancellation source, calling CancelAfter only for finite values.
  Infinite timeout stays infinite; explicit caller cancellation remains
  supported. Do not mutate or dispose the shared HttpClient.
- Use the linked token for SendAsync and ReadAsStringAsync, check cancellation
  after body reading and again after decoding before returning, checking the
  caller token first. Dispose response/message/deadline resources on every path.
  Await cancellation-cooperative operations through cleanup; do not detach work
  with WhenAny or WaitAsync to manufacture an earlier return.
- Actual caller cancellation takes precedence and remains an
  OperationCanceledException. A non-caller cancellation caused by the owned
  deadline or a direct/inner TimeoutException becomes a public sealed
  `ResponseTimeoutException : TimeoutException`. Search the exception chain for
  TimeoutException before generic send/body wrappers; a TaskCanceledException
  by itself does not prove timeout. The caller token always takes precedence.
  Other non-caller cancellation becomes an InvalidOperationException with a
  fixed interrupted-request message. Do not echo provider bodies or secrets.
  The timeout exception's only public constructor takes no arguments and fixes
  Message to `OpenAI response timed out. Try again.`. The interrupted exception
  text is `OpenAI response was interrupted before completion. Try again.`.
- Root adds an explicit ResponseTimeoutException catch to the typed request
  handler and screen-help handler, showing the fixed timeout message above.
  SanitizedError also maps the sealed timeout exception to that fixed message.
  A remaining non-caller OperationCanceledException must never claim the user
  stopped the request: display the fixed interrupted message above. Other
  normalized provider failures retain the existing generic unavailable UI
  message. Preserve generation checks, including rechecking after the awaited
  CompleteToolAsync before writing StatusText, so old failures cannot overwrite
  a newer request's status. Keep genuine Stop behavior intact.
- Capture indicator ownership is a separate existing defect and is not
  repaired by scattered off writes in this change. Provider speed, native
  transcript/mark success, and the eventual teaching demo remain unproven.

## Test-first execution

Independent tests precede implementation, with an empty exception class added
only as a compile-time contract if needed. Use in-memory HttpMessageHandler
and cancellable response content; no paid calls. Prove: send deadline becomes
the timeout exception; headers followed by stalled body are bounded; explicit
caller cancellation before/during send or body remains cancellation; a
successful response within the budget still decodes; direct and inner timeout
causes normalize; non-caller cancellation without timeout becomes an
interruption; infinite timeout still honors caller cancellation; caller wins
when deadline and caller cancellation coincide; sentinel exception content
does not appear in messages; exact safe strings, HttpClient.Timeout, and model
payload remain as specified. Use a generous outer watchdog so a broken body
deadline produces a bounded RED result; its finally must cancel and drain the
operation so failure cannot leave work running in the test process.

Root observes RED, freezes only additive independent tests, and commits the
contract before a fresh isolated builder gets ownership of ResponsesClient.cs
and ResponseTimeoutException.cs. The transcript builder owns different files.
Root owns MainWindow wiring and integration. A separate reviewer checks the
assembled timeout and UI diff. Focused tests, frozen oracle, full Release build,
and final suite must pass before packaging. One later bounded native check can
test the improved outcome; do not call a timeout-message fix a latency fix.
