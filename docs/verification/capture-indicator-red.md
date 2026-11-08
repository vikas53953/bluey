# Capture indicator tracker RED receipt

Date: 2026-11-07
Base: `1b9bfa58e26a6d674ead11943709cd3ef4375f81`

The independent test-author packet adds only
`tests/Boo.Core.Tests/CaptureActivityTests.cs`. The compile-time stub at
`src/Boo.Core/CaptureActivityTracker.cs` was left unchanged. The tests use the
public seam only: `ActiveDisplays`, `CreateOperation()`,
`CaptureActivityOperation.Start(DisplayInfo)`, and `Dispose()`.

## Focused run

```powershell
scripts/build.ps1 -Action test -Project tests/Boo.Core.Tests/Boo.Core.Tests.csproj -Configuration Release -Filter FullyQualifiedName~CaptureActivityTests
```

Result: **RED** — 9 failed, 2 passed, 0 skipped, 11 total.

TRX: `TestResults/vikasmit_VIKASMIT_2026-11-07_13_15_31_net10.0.trx`

The 9 expected behavioral failures against the no-op stub were:

- `Boo.Core.Tests.CaptureActivityTests.Start_activates_one_display_and_dispose_removes_that_activity`
- `Boo.Core.Tests.CaptureActivityTests.Multiple_operations_on_one_display_remain_active_until_all_complete`
- `Boo.Core.Tests.CaptureActivityTests.Distinct_displays_use_oldest_surviving_operation_order_and_metadata`
- `Boo.Core.Tests.CaptureActivityTests.Older_completion_does_not_hide_a_newer_operation_on_the_same_display`
- `Boo.Core.Tests.CaptureActivityTests.Previously_returned_snapshots_remain_stable_after_activity_changes`
- `Boo.Core.Tests.CaptureActivityTests.Null_display_is_rejected_without_consuming_the_operation`
- `Boo.Core.Tests.CaptureActivityTests.Repeated_start_is_rejected_without_replacing_the_original_display`
- `Boo.Core.Tests.CaptureActivityTests.Start_after_dispose_is_rejected`
- `Boo.Core.Tests.CaptureActivityTests.Independent_operations_can_start_and_finish_concurrently`

The two passing cases (`Newly_created_operation_is_inactive` and
`Dispose_is_idempotent`) are compatible with the stub and do not weaken the
RED result. The concurrent case uses asynchronous
`TaskCompletionSource` ready/start/release barriers and atomic counters with
`RunContinuationsAsynchronously`, bounded watchdog waits, and `finally`
release/drain cleanup; it uses no blocking gates, sleeps, or synchronization
object disposal while workers may still be running. The snapshot case also
attempts a public `IList<DisplayInfo>` mutation when available and requires a
read-only collection to reject it without changing tracker state.

## Public-seam boundary

These tests do not claim WPF visibility timing, native capture timing,
provider waits, shell text, or cancellation cleanup. Those remain integration
and packaged acceptance work owned outside this test-author packet. No reset or
clear-all API is assumed.

Release ownership: the test author owns this receipt and test file only. The
separate builder owns the Core tracker implementation; the root owns shell
integration and final freeze/review.

Root independently reproduced 9 failures and 2 passes before implementation:
`TestResults/vikasmit_VIKASMIT_2026-11-07_13_16_20_net10.0.trx`.
