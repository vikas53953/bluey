# Full-window shell integration

The independent shell builder worked from
`2638356be7cc6d9de4538234cf24b759c1cfd21a` in an isolated worktree, using the
prewritten frozen native acceptance contract. Root inspected actual changes,
requested corrections within that contract, and integrated the final six App
files by exact bytes with the already tested Core/Storage implementation.

The resulting shell provides the eyes-only idle-hidden launcher, typed active
capture/microphone disclosures, saved colors, normal resizable desktop window,
separate conversation and settings/focus scrolling, explicit approval/save/reset
access keys, and high-contrast theme resources. No provider or desktop-action
execution implementation changed in this unit.

Evidence strategy: the earlier compact shell and visible wordmark were the
source/user-observed baseline; native expectations were frozen before the
builder started. No XAML string checks or simulated desktop tests are counted
as behavioral proof. Core/Storage behavior has independent executable RED/GREEN
evidence, while actual WPF rendering and interaction require packaged native QA.

Root checks after final integration:

- Full Release suite: **239 passed, 1 unchanged host-dependent reparse skip**.
- Core TRX: `TestResults/vikasmit_VIKASMIT_2026-11-07_19_44_53_net10.0.trx`.
- Integration TRX: `TestResults/vikasmit_VIKASMIT_2026-11-07_19_44_55_net10.0.trx`.
- Release app build: zero warnings/errors.
- Oracle revision 15: all 41 frozen files verified.
- `git diff --check`: passed.

Commands: `scripts/build.ps1 -Action test -Configuration Release -NoRestore`,
`scripts/build.ps1 -Action build -Configuration Release -Project src/Boo.App/Boo.App.csproj -NoRestore`,
and `python scripts/test_oracle.py`.

Independent source review, formatting status, packaging and native acceptance
are separate gates. These results do not establish hover hit testing, actual
window placement, high-contrast rendering, full-window usability, or task/voice
acceptance. Existing action and resource findings in `review-residuals.md` remain.
