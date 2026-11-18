# Deferred approval focus integration

Candidate11 provided native RED before implementation: a confirmed new request
produced the exact immutable typing proposal, but actual keyboard focus stayed
on InputBox while Cancel and Approve were present. See the corrected-input
attempt in `native-typing-candidate11.md`; no edit was approved.

The independent builder deferred the existing focus request to Dispatcher
Loaded priority. The callback checks the current preview object, request lease,
generations, authority, active visible panel, visible preview, unchanged focus,
close state and approval state before bringing the preview into view and
focusing Cancel. It does not activate the window or approve an action.

Root inspected and integrated the actual worker file. Its SHA-256 matches the
independently reviewed worktree source:
`52D0705D75D3743FFB2E9CCBB5B97CBDFC31E5166C591331C930334407842E7D`.
The combined simplification pass found no safe changes. Fresh CE source review
found no actionable issue and inspected Stop, cancellation, replacement,
close/hide, background and chained-action paths. These are source findings,
not native focus proof.

Root's integrated Release build passed with zero warnings/errors. The full
suite passed 123 Core tests with one unchanged host-dependent reparse fixture
skip, and 88 integration tests. Exact evidence:
`TestResults/<user>_<machine>_2026-11-07_15_05_00_net10.0.trx` and
`TestResults/<user>_<machine>_2026-11-07_15_05_02_net10.0.trx`.
Oracle revision 14 retains all 37 frozen files unchanged; diff checks pass.
The project format check still exits 2 on the same untouched Core whitespace
in AnnotationValidator.cs, Contracts.cs and RequestCoordinator.cs. Current
evidence is `artifacts/format-preview-focus.log`; no new formatting failure
was introduced and the full check is still reported as failed.

Runtime focus has no suitable deterministic seam in the current suite. The
native regression case in `action-preview-focus-work.md` is required against
the next exact package. Until that case succeeds, layout timing remains a
repair hypothesis, and focus, approved typing/readback and edit/Undo acceptance
remain open. General target lifetime, click verification and voice checks are
still governed by `review-residuals.md`.
