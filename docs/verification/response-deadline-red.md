# Response deadline repair: independent red receipt

This receipt covers the additive `ResponseDeadlineTests` integration tests on
the isolated test-author checkout at base `b9be600f3c22f8410758ae095ad200d722ae8838`.
The only pre-existing working-tree change was the root-owned compile-time
`ResponseTimeoutException` contract. No production source was changed here.

## Scope

The tests exercise `ResponsesClient.RespondAsync` through an in-memory
`HttpMessageHandler`, with a cooperative cancellable response body. They make
no network or provider calls. The watchdog cancels and drains the pending
operation in `finally`; a stalled body therefore produces a bounded red result
without leaving test work running.

Covered contract cases include finite send/body deadlines, direct and inner
`TimeoutException` normalization, caller cancellation before/during send and
body, caller precedence when the deadline is also canceled, non-caller
cancellation normalization, infinite timeout plus caller cancellation,
successful decoding, fixed safe messages, unchanged `HttpClient.Timeout`, and
the selected reasoning-model payload.

## Focused Release run

Restore completed with:

```powershell
.\scripts\build.ps1 restore
```

The focused run was:

```powershell
.\scripts\build.ps1 test -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -Filter 'FullyQualifiedName~ResponseDeadline' -Configuration Release
```

Result against the current pre-repair implementation: **8 failed, 5 passed,
0 skipped, 13 total**.

Expected red failures:

- `Finite_timeout_covers_a_stalled_send_and_normalizes_it` — actual
  `TaskCanceledException`, not `ResponseTimeoutException`.
- `Finite_timeout_covers_headers_followed_by_a_stalled_body` — the 3-second
  outer watchdog fired; its `finally` canceled and drained the cooperative
  body operation.
- `Direct_timeout_during_body_read_normalizes_to_the_fixed_public_message` —
  actual generic `InvalidOperationException` from the unreadable-body path,
  not the public sealed type.
- `Inner_timeout_during_body_read_normalizes_to_the_fixed_public_message` —
  actual generic `InvalidOperationException` from the unreadable-body path,
  not the public sealed type.
- `Direct_timeout_exception_normalizes_to_the_fixed_public_message` — actual
  `TimeoutException`, not the public sealed type.
- `Inner_timeout_exception_normalizes_to_the_fixed_public_message` — actual
  generic `InvalidOperationException`, not the public sealed type.
- `Noncaller_cancellation_without_timeout_becomes_a_fixed_interruption` —
  actual `OperationCanceledException`, not the fixed interruption error.
- `Caller_cancellation_before_send_remains_operation_canceled` — the current
  implementation reached the fixture handler and returned its sentinel
  `InvalidOperationException` instead of preserving caller cancellation.

Already passing on this base:

- caller cancellation during send;
- caller cancellation during body;
- caller precedence when the deadline is also canceled;
- infinite timeout honoring caller cancellation; and
- successful decoding with the unchanged timeout and reasoning-model payload.

These results are red-phase evidence only. They are not a production or
release acceptance result.

Root independently reproduced the final 13-case contract in the canonical
checkout: 8 failed, 5 passed, no skips. The stalled body reached its outer
watchdog and drained before the process completed. TRX:
`TestResults/<user>_<machine>_2026-11-07_12_43_48_net10.0.trx`.
The author released ownership before oracle revision 10 added this file;
every earlier frozen hash remains unchanged. Implementation has not started.
