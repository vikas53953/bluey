# Appearance feedback repair

The independent native regression was frozen at `7a55e8f3521fa29dfdc29e2cd8bd0a2fcb3c4232` before a fresh builder changed the two feedback branches in `MainWindow.xaml.cs`. Candidate14's retained accessibility states demonstrate the previous success message remaining after validation rejected a later save. They are preserved in `artifacts/native-candidate14-appearance-state.txt`.

The repair updates the general status when validation rejects appearance values and reports a successful reset only after its save succeeds. Existing detailed validation, last-good-file preservation and successful-save clearing remain intact. The diff is five inserted lines and one removed line; no tests or storage/provider/action logic changed.

Root verification on the integrated source:

- Release tests: 242 passed, one unchanged host-dependent skip. Core TRX `TestResults/<user>_<machine>_2026-11-07_20_44_42_net10.0.trx`; integration TRX `TestResults/<user>_<machine>_2026-11-07_20_44_45_net10.0.trx`.
- App Release build: zero warnings/errors.
- Oracle revision 17: 44 files verified, preserving every earlier frozen hash.
- Scoped formatting of MainWindow and `git diff --check`: passed. The previously recorded repository-wide formatting failures remain.
- A fresh independent reviewer found no defect in the bounded diff or its callbacks. It performed no native run.

The test strategy for these WPF messages is the prewritten native contract `acceptance-appearance-feedback.md`; source-text assertions or a new production abstraction would not prove the actual UI feedback. Exact packaged execution of all three cases is required separately and remains pending at this integration receipt.

## Packaged follow-through

Candidate15 was built from `114e4999fb077312a4bd83acfa71b3ef74b3eb8f`. The sole supported native driver subsequently exercised all three frozen cases; they passed, and root inspected the retained state/file evidence. See [native results](native-appearance-candidate15.md). The application was then opened in the normal profile and left idle, without a provider request, microphone use or screen capture.

The bounded source review receipt and its complete stage record are preserved at `artifacts/reviews/appearance-feedback-114e499/`. Its Ready to merge source verdict does not certify the broader application's still-open native gates. Simplification was skipped for the six-line code diff under LFG's small-change rule; no new durable lesson was needed beyond the frozen regression. No browser routes changed. No remote is configured, so delivery remains local.
