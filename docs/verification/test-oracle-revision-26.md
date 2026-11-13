# Test-oracle revision 26: additive (2026-11-13)

Branch `phase2/notch-reducer` (agent-trust plan, Phase 2, PR 2.4). Made with `scripts/test_oracle.py --extend`:
new files only; every file frozen in revision 25 keeps its hash.

**Numbering:** open PR #40 (`docs/arch-followups`) also takes revision 26 on the same base. Whichever merges second
re-makes its revision with `--extend` on the new `lane/face` tip and renumbers this note (to 27 if #40 merges first).

## Added frozen files (exactly 1)

| File | What it is |
| --- | --- |
| `tests/Boo.Core.Tests/NotchLifecycleTests.cs` | Tests for the new pure reducer `src/Boo.Core/NotchLifecycle.cs` (plan section 3.2 C): at least one test per invariant of section 3.2 B (`INV-PRIV-1/2`, `INV-APPLY-1/2/3`, `INV-FOCUS-1`, `INV-TALK-1`, `INV-STOP-1`, `LIVE-1`, `LIVE-2`), the main transitions of `docs/architecture/notch-lifecycle.md` (R4, R7, R10/R11, F3/F4, F7, F16-F18, F22, G1), Stop from 14 starting states, and a seeded random-sequence test (FuzzKit, 3 seeds x 400 runs x up to 200 events) that checks every safety invariant after every step and that every run settles (Responding ends, the notch retracts) |

Changed frozen files: none. Removed: none.

## Tests named `..._gap_..._today`

The reducer models what the app does **today**. Where today's app does not meet an invariant as the plan words it,
the test pins today's behaviour and names the finding of `docs/architecture/notch-lifecycle.md` section 5, so a later
behaviour change has to edit a frozen test in a reviewed revision:

| Test | Finding |
| --- | --- |
| `INV_PRIV_2_gap_F5_capture_starts_from_hidden_without_any_approval_today` | F5: captures need no approval (the Capture face is shown in the same step instead) |
| `INV_APPLY_2_gap_F6_a_second_file_undo_reapplies_the_change_today` | F6: an approved file undo is journaled as a new edit |
| `INV_FOCUS_1_gap_F9_a_session_replaced_by_a_new_start_ends_with_no_event_today` | F9: a replaced session ends quietly |
| `INV_STOP_1_gap_F4_a_focus_session_keeps_the_notch_out_on_paused_after_stop_today` | F4: Stop pauses focus and Paused pins the notch |
| `INV_STOP_1_gap_the_capture_face_outlasts_stop_until_the_capture_operation_ends_today` | `INV-STOP-1` (b): the capture indicator clears when the operation ends |
| `LIVE_1_gap_F7_thinking_has_no_local_timeout_today` | F7: Thinking has no local timeout |
| `LIVE_2_hover_lasts_as_long_as_the_pointer_stays_today` | `LIVE-2`: Hover means the pointer is on the notch |

## Why this does not weaken anything

Purely additive: no existing test, assertion, timeout or `Skip` is touched, and the new tests contain no `Skip`.

## Red proof (local, not committed)

1. Planted "apply without approve" (a `Propose` applies at once): 6 tests failed, among them
   `INV_APPLY_1_apply_only_after_approve_of_the_same_proposal` (`Assert.Empty() Failure: Collection was not empty`) and
   all 3 random-sequence seeds (`INV-APPLY-1 broken seed=1 run=0 step=9 ...`). Reverted.
2. Planted "a new request keeps the old card": 5 tests failed, among them
   `INV_APPLY_3_a_new_request_drops_the_older_proposal` and all 3 seeds (`INV-APPLY-3 (card belongs to the current
   request) broken ...`). Reverted.
