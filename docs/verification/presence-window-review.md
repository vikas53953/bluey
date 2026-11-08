# Presence and full-window independent review

Reviewed production source: `013be73c6f2eb11efc032ce406087094c90a9560` against frozen-contract base `2638356be7cc6d9de4538234cf24b759c1cfd21a`.

Compound Engineering full review used four fresh reviewers (correctness, reliability, testing, maintainability), a separate merge pass, an independent validator, and a separate report pass. The report is **Not ready**. Five findings remain: history failure can abort shell startup; required native coverage is incomplete; shell-status fallback is unreachable; disconnected-monitor placement stays stale; unreadable appearance paths can silently produce defaults. The validator confirmed code reachability; it did not reproduce native fault incidence.

Raw review, metadata and complete stage-cost record are preserved locally under `artifacts/reviews/presence-window-013be73/`. Original run ID: `20261107-presence-3156c8fe`. No cross-model peer was selected for this bounded appearance/window change. Two maintainability suggestions were advisory, not an implementation rewrite mandate.

The approved repair is recorded in the main plan. The status repair follows the user's hidden-idle requirement: reveal error text on hover, not permanently at idle. The existing prewritten native checklist is not missing documentation; the missing proof is execution of its required cases against the packaged app. Those cases stay open where the supported driver cannot exercise them.

Candidate13 independently demonstrated opening the full-window accessibility surface and an approved synthetic field mutation with independent readback. That does not close the remaining hover, Apply/Undo, voice, display, accessibility or performance gates.

## Repair disposition

At `1493d18fb1133a645ee3f6ee0110ddaedf863453`, the four source defects (#1, #4, #6 and #7) were repaired after an independently authored RED regression and native fault checklist were frozen. The integrated suite passed 242 tests with one unchanged host-dependent skip, and a fresh reviewer confirmed the source repairs. See [integration evidence](shell-recovery-integration.md) and `artifacts/reviews/shell-repair-1493d18.json`.

Finding #2 remains open: exact packaged native acceptance is incomplete. The historical Not ready verdict above is retained; it is not a claim that the four repaired source defects remain present. Candidate14 has its own [native receipt](native-shell-candidate14.md), whose observations must not be broadened to unexercised cases.
