# Native read lifetime coordination

The bounded follow-up in the observed-control plan received independent
PLAN_APPROVED review. At the test checkpoint, the native reader still started
uncoordinated workers and the public Core seam was an unwired scaffold.

Independent test-author review corrected a counter and removed an unintended
FIFO-order requirement before freezing. Root then independently repeated the
focused Release suite against the scaffold: seven behavioral failures and one
synchronous-throw compatibility pass. Canonical RED evidence:
`TestResults/vikasmit_VIKASMIT_2026-11-07_14_48_37_net10.0.trx` and
`artifacts/read-lifetime-red.log`.

The test author changed only the new test and its receipt. Existing frozen
tests and Contracts.cs remain unchanged. Native interruption and packaged
acceptance are not implied by these tests.

## Integrated implementation and checks

Root inspected and integrated the separate builder's two source files. The
reader shares one coordinator across instances and starts its windowless MTA
worker only after admission. The coordinator awaits the full callback task,
checks cancellation before invocation and after successful completion, and
releases admission in finally. It does not detach an unfinished native call.

Root independently ran the Release solution build with zero warnings/errors,
then the full suite: 123 Core passes and one unchanged host-dependent reparse
fixture skip, plus 88 integration passes. Canonical full-suite evidence:
`TestResults/vikasmit_VIKASMIT_2026-11-07_14_54_36_net10.0.trx` and
`TestResults/vikasmit_VIKASMIT_2026-11-07_14_54_38_net10.0.trx`.

After comment-only cleanup, all eight focused cases passed again:
`TestResults/vikasmit_VIKASMIT_2026-11-07_14_59_00_net10.0.trx`.
Oracle revision 14 verifies all 37 frozen files unchanged; diff checks pass.
The full format check still exits 2 only for pre-existing whitespace in
AnnotationValidator.cs, Contracts.cs and RequestCoordinator.cs, recorded in
`artifacts/format-read-lifetime.log`. It is not a formatting pass.

Fresh independent CE source review completed with no actionable findings;
review run `20261107-093034-9049` used the lite path and checked actual current
Core/Windows wiring and the frozen proof test. Its verdict applies to this
unit's source, not product delivery. A blocked Windows provider call remains
uninterruptible; this change bounds admitted native reads while it is held.
The Windows wiring has not yet received packaged native or blocked-provider
failure-injection proof. General action-execution lifetime remains separate.
