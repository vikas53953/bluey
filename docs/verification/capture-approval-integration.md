# Capture and approval integration

The root integrated the two independent repair worktrees based on
`cf191e73a0ccd4ea7129791d907eb9189063e0ba`. The approved work source is
`docs/plans/2026-11-07-capture-approval-repairs.md`.

The capture service now serializes complete backend lifetimes and keeps the
gate through cancellation cleanup. A five-second first-frame deadline leaves
caller cancellation distinct. Owned handles and geometry are validated before
native capture. This does not forcibly interrupt GPU or COM calls.

Action previews now copy requests immutably, show full arguments and the
canonical file destination, and use the same resolver as file execution.
The WPF card renders selectable read-only values in a bounded scroll area,
keeps Cancel initially focused, and disables invalid approvals. Each chain step
uses the same preview builder. Stop, replacement, and cancellation clear the
preview data. File actions no longer depend on an unrelated foreground window;
window actions retain their target checks. Native file identity races remain
open and are not repaired by path revalidation.

The packaged candidate3 teaching check failed because no image reached the
provider. Its receipt is preserved in `native-teaching-candidate3.md`. The
controls now retain a separate capture failure message after a provider reply,
and expose actual status text to accessibility instead of fixed labels. These
are native-control changes covered by the prewritten capture and accessibility
acceptance cases; source inspection/build is not a native pass.

Root verification after integration:

- `ActionPreviewTests`: 1 passed in Debug; initial RED is retained in
  `action-preview-red.md`.
- `CaptureDeadlineTests`: 1 passed in Release;
  `TestResults/vikasmit_VIKASMIT_2026-11-07_11_36_52_net10.0.trx`.
- `Boo.App` Release build: zero warnings and errors after removing an
  unsupported non-WPF accessibility property identified by the compiler.
- Supplemental independent tests: 15 preview/resolver cases and nine capture
  lifecycle cases passed; both authors explicitly released ownership.
- Oracle revision 7 freezes their two new files, preserving every prior hash.
- Full Release suite: 118 passed, one unchanged host-dependent reparse fixture
  skip. Results are `TestResults/vikasmit_VIKASMIT_2026-11-07_11_42_15_net10.0.trx`
  and `TestResults/vikasmit_VIKASMIT_2026-11-07_11_42_17_net10.0.trx`.
- Independent source review and a new exact packaged diagnostic check are
  pending at this checkpoint.

This checkpoint does not establish working native capture, drawing, approval
UX, voice hardware, latency, or application acceptance.
