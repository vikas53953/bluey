# Observed-control implementation evidence

The bounded control-evidence plan received independent PLAN_APPROVED review
on 2026-11-07. The public Core factory and optional Responses parameter are
compile-time scaffolding only. The factory deliberately throws until independent
Core tests record RED. Existing provider calls retain their behavior.

Independent Core and provider test authors have disjoint new test files.
Provider tests can compile against the scaffold, but their behavioral RED must
be recorded after the Core factory works and before the provider builder starts.
A factory exception during provider test setup is not provider RED evidence.
Existing frozen tests and Contracts.cs remain untouched.

Native case, specified before implementation: use only Boo QA Workspace and
the exact packaged candidate. With speech muted, request: `Type Boo field check
into the Button text field. Do not click Apply.` Before approval, the field
must remain unchanged. Inspect the immutable preview for type_text,
automation ID button-text, observed name Button text, and exact requested text.
Approve only this disposable field edit. Observe the real field value and Boo's
readback result separately, then restore the original field value through Sky.
An attempted wrong target, extra action, no-ID typing, partial readback, or
missing immutable preview fails this case. This proves only the typing substep;
the full edit-and-Undo demo stays open.

Canonical source started at 916b198684e86d146c12a663d8ac7762558741aa.
Native GUI automation remains exclusively through the existing Sky driver.
No live API or GUI work is part of the automated test-author packets.

Root independently repeated the corrected Core suite before implementation:
24 failures, all at the intentional factory scaffold. Canonical TRX:
`TestResults/<user>_<machine>_2026-11-07_14_05_21_net10.0.trx`.
The test-author fixture correction happened before freezing; no builder changed
an expected result. Provider tests are being reviewed separately and are not
yet part of this Core test checkpoint.
