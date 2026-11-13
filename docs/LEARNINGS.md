# Lessons enforced by structure

Audit date: 2026-11-13. Base: `lane/face` at `b7e3e806aa5c1d60283684704a860aebd9c1467a`.
Sources are repository PRs/issues, not attributed quotations from builders. The organizing principle is poteto's
"encode lessons in structure": after a repeat, prefer types/architecture, then compiler rules, then executable gates.

**manual:** this is a source/review audit. Linked run results below are reported by the source authors unless explicitly
marked inspected. No screenshots or run artifacts were downloaded in this audit; no new live PASS is claimed.
No local build, test or desktop interaction was performed. All new validation must use GitHub `windows-latest`.

## Recurring mistakes (distinct incidents, not repeated reports of one bug)

| Date | What went wrong; sources | Strongest encoding and current status |
| --- | --- | --- |
| 2026-11-14 | A green pipeline omitted meaningful test coverage: [#10](https://github.com/vikas53953/bluey/pull/10) closed empty/skipped-test vacuity; [#34](https://github.com/vikas53953/bluey/pull/34) later extended the static gate to tools test projects. | BOO-CI-001, this PR, preserves three blocking oracle/source/result gate invocations and its own checker steps via parsed workflow mappings. This prevents removal/softening of declared gates, not incomplete semantics inside a checker. **mock:** hosted [gate-removal red](https://github.com/vikas53953/bluey/actions/runs/43484855090/job/132090475848) and [corrected green](https://github.com/vikas53953/bluey/actions/runs/43487998741/job/132101463235), logs inspected. Owner-required check remains pending. |
| 2026-11-13 | A check judged different inputs from the shipped program. [#28 response-file bypass](https://github.com/vikas53953/bluey/pull/28#issuecomment-6845758415) and [#32 Release/publish/OS references](https://github.com/vikas53953/bluey/pull/32#issuecomment-6846320947) both passed CI. | Compiler/build boundary: #37 merged, records actual Release/publish facts; #28 compiler replay + banned APIs and #32 reference allowlist remain in PR. Do not add another source-regex gate. **UNVERIFIED:** current bypass resistance; both PRs lack GitHub red proofs in their latest audit snapshot. |
| 2026-11-13 | Green tests did not exercise important behavior. [#47: three compiling protocol mutants survived](https://github.com/vikas53953/bluey/pull/47#issuecomment-6854904318); [#68 mutation/test improvements](https://github.com/vikas53953/bluey/pull/68#issuecomment-6859316959) cover the native capture/playback boundary separately. | Executable boundary tests, then selected compiling mutants. #47 merged additional protocol cases; #68 in PR adds native audio boundary tests and a hosted journey. BOO-BUG-001 (#58) requires red-before/green-on-head for fixes; BOO-TEST-002 (#61) protects existing tests. **PENDING:** there is no general automated mutation requirement; a selected kill rate is not exhaustive coverage. |
| 2026-11-13 | UI status/face followed an event rather than the resulting domain state: [#70 Pause after End](https://github.com/vikas53953/bluey/issues/70), [#71 Stop leaves Thinking](https://github.com/vikas53953/bluey/issues/71). | Prefer one typed state-to-presentation projection; cancellation must project a terminal status even after generation invalidation, and invalid focus transitions must not project Paused. **PENDING architecture prerequisite:** wire the Core reducer (#43) into WPF and cover both flows; #43 expressly has no app wiring. Existing red regression PRs #75/#76 own the checks; do not duplicate them. Follow-up ownership: product/state integration, tracked by #70/#71. No green product proof yet. |
| 2026-11-13 | Evidence was broader than what was observed: [#47 provider-client voice is not native voice](https://github.com/vikas53953/bluey/pull/47#issuecomment-6854744665); [#39 an empty MP4 could pass, and desktop video did not prove every journey](https://github.com/vikas53953/bluey/pull/39#issuecomment-6859216523). | Validate evidence through its actual consumer: native published-app journey (#68), decode/duration/frame checks (#39), plus BOO-EVID-001 (#34, merged) for visible labels/links. **PENDING:** no static check proves an honest human interpretation of screenshots or a provider reply's native wiring. |
| 2026-11-13 | Home-made text interpretation missed the real consumer's semantics: [#23 alias rewriting erased ratchet hits](https://github.com/vikas53953/bluey/pull/23#issuecomment-6842652780); [#28 compiler configuration hid a banned call](https://github.com/vikas53953/bluey/pull/28#issuecomment-6845758415). | Keep the ratchet monotonic (#21/#23 merged), use compiler symbols/canaries and actual compiler inputs (#28), and use GitHub-rendered Markdown for evidence (#34 merged). Text scans remain bounded approximations, not semantic proof. |

## Review of existing quality PRs (do not duplicate)

These are audit observations, not independent PASS verdicts. A linked green run is not a fresh artifact inspection.

| PR / rule | Audit conclusion | Reported red / green evidence |
| --- | --- | --- |
| [#28](https://github.com/vikas53953/bluey/pull/28), banned APIs | Correct boundary is actual compiler inputs and replay. Latest QA notes an unresolved `PublishDepsFilePath` probe where judges exited 0; the note does not establish whether a forbidden call was actually planted or an overwrite happened. Reproduce on hosted Windows before deciding. Merge order: #28 before #32. | **manual** local red only; **mock** [Windows green](https://github.com/vikas53953/bluey/actions/runs/43468497615). GitHub red proof **PENDING**. |
| [#32](https://github.com/vikas53953/bluey/pull/32), architecture refs | Uses shipped build facts instead of independent Linux project evaluation. Needs hosted red fixtures for Release/RID/publish/solution/Windows conditions and the forbidden reference; sync after #28's analyzer allowance. | **manual** local red only; **mock** [Windows green](https://github.com/vikas53953/bluey/actions/runs/43468068207). GitHub red proof **PENDING**. |
| [#51](https://github.com/vikas53953/bluey/pull/51), BOO-CMT-003 | Existing comment-lint owner; a textual policy check, not a C# behavior guarantee. Do not replace it with another lint. | **mock** [red](https://github.com/vikas53953/bluey/actions/runs/43423630833), [green](https://github.com/vikas53953/bluey/actions/runs/43464788991). Existing lint job uses Linux; not new Windows validation by this audit. |
| [#58](https://github.com/vikas53953/bluey/pull/58), BOO-BUG-001 | Owns regression evidence for fixes. Its linked green job establishes run success, not by itself that the named regression executed; review the detector and exact test receipt. | **mock** [red](https://github.com/vikas53953/bluey/actions/runs/43435149732/job/131918194715), [green](https://github.com/vikas53953/bluey/actions/runs/43462402944). Existing job uses Linux. |
| [#61](https://github.com/vikas53953/bluey/pull/61), BOO-TEST-002 | Owns test weakening. Sign-off text proves presence, not reviewer identity; assertion-shaped counts cannot prove preserved behavior. Base-copy checking protects the script, not arbitrary workflow edits. | **mock** [red](https://github.com/vikas53953/bluey/actions/runs/43437212800), [green](https://github.com/vikas53953/bluey/actions/runs/43464650296). Existing job uses Linux. |
| [#60](https://github.com/vikas53953/bluey/pull/60), async-void cleanup | Lowers BOO-ASYNC-003 from seven to zero; shared UiAction helper preserves exception routing rather than adding a broad catch. Existing ratchet owns the rule. Update-button behavior remains UNVERIFIED. | **mock** [Windows green](https://github.com/vikas53953/bluey/actions/runs/43464804216). No new rule, so no new red proof claimed. |

## Issues / PRs #66-#78: complete candidate disposition

Numbers in this range include PRs. Two failed journeys of the same defect do not count as two independent mistakes.

| Source | Disposition / enforcing check or pending work |
| --- | --- |
| [#66](https://github.com/vikas53953/bluey/issues/66) | Single omitted-folder-context incident, two failing file journeys in #54. Typed request context is the preferred seam; corrected live causal proof pending. |
| [#67](https://github.com/vikas53953/bluey/issues/67) | Uncertain UIA timeout before the hotkey. Not evidence of a hotkey defect; preserve the failure and add diagnostics without relaxing limits. |
| [#68](https://github.com/vikas53953/bluey/pull/68) | Native voice QA PR, relevant to behavioral coverage/evidence recurrences above; hosted synthetic audio does not prove physical devices. |
| [#69](https://github.com/vikas53953/bluey/pull/69) | Explorer harness/source of issues; do not count the harness as another product defect. |
| [#70](https://github.com/vikas53953/bluey/issues/70) | Repeated state/presentation class; existing regression #75; architecture integration pending. |
| [#71](https://github.com/vikas53953/bluey/issues/71) | Repeated state/presentation class; existing regression #76; cancellation projection pending. |
| [#72](https://github.com/vikas53953/bluey/pull/72) | Throwaway red proof for explorer regressions, not another independent incident. |
| [#73](https://github.com/vikas53953/bluey/issues/73) | Single transcript identity incident. Prefer reference/explicit identity over value-equal UI collection rows; existing regression #77. |
| [#74](https://github.com/vikas53953/bluey/pull/74) | Throwaway red proof for #39 video validation, not another incident. |
| [#75](https://github.com/vikas53953/bluey/pull/75) | Red regression for #70; not a second occurrence of #70. |
| [#76](https://github.com/vikas53953/bluey/pull/76) | Red regression for #71; not a second occurrence of #71. |
| [#77](https://github.com/vikas53953/bluey/pull/77) | Red regression for #73; not a second occurrence of #73. |
| [#78](https://github.com/vikas53953/bluey/issues/78) | First panel Show blocks face observation. Shares an observation boundary with #67, but a shared root cause is UNVERIFIED. #54's recorder-health regression remains red; profile before choosing a fix. |

## Phase 1 static-analysis progress

Snapshot from GitHub PR metadata and the checked-out plan, 2026-11-13. "Merged" means merged into the audited
`lane/face` history, not proof of branch protection, current artifact quality, or full-app acceptance.

| Plan item | State | Enforcement / remaining work |
| --- | --- | --- |
| 1.1 ratchet | Merged #21, follow-up #23 | `scripts/quality/ratchet.py`; baseline may only decrease. |
| 1.2 architecture refs | In PR #32 | Actual build facts; hosted red proofs pending. |
| 1.3 banned APIs | In PR #28 | RS0030, canary, replay, exact baselines; hosted red proofs pending. |
| 1.4 threading/catch analyzers | Missing | No root `.editorconfig` or corresponding analyzer rollout in audited base; add compiler analyzers with exact baselines separately. |
| 1.5 no-suppress / gate manifest | Partial, this PR | BOO-CI-001 introduces a bounded gate manifest for three windows-ci gates and its own checker steps. General no-suppress remains missing. #28's RS0030-specific defenses are not that general rule. |
| 1.6 comment lint | In PR #51 | BOO-CMT-003; no duplicate PR. |
| 1.7 key scan everywhere | Merged #26 + #46 | `scripts/quality/key_scan.py`; upload contracts and Linux filename follow-ups. |
| 1.8 hosted-runner guard | Merged #18 | `scripts/ci/check_runners.py` and live capture guard. Existing workflow allowlist also permits Ubuntu; new runs for this task must be Windows. |
| 1.9 fixture capture guards | Merged #18 | Frozen oracle revision 24 history; no frozen edits in this audit. |
| 1.10 evidence / tools test scan | Merged #34 | BOO-EVID-001 and BOO-TEST-001. |
| 1.11 architecture tests | Merged #31; follow-ups in #40 | BOO-ARCH-001..006; frozen tests unchanged here. |
| 1.12 trust metrics | Merged #36 | Metrics are measurements, not acceptance evidence. |
| C1 async-void cleanup | In PR #60 | BOO-ASYNC-003 seven to zero. |
| Added regression / test protection | In PR #58 / #61 | BOO-BUG-001 / BOO-TEST-002. |

**2026-11-14 addition:** BOO-CI-001 ([rule](quality/gate-manifest.md)) encodes the repeated incomplete-test-gate class
from #10 and #34 as a semantic workflow contract. Hosted red/green proof is recorded in [PR #85](https://github.com/vikas53953/bluey/pull/85); logs were inspected on 2026-11-14. The new rule is
**mock/static proven** at code head `1ff39e2` (17 CLI cases plus five real-git bootstrap cases). All three required checks passed at that SHA. Branch protection for the new check is owner-pending.

**PENDING:** the uncovered state/presentation recurrence
still needs its architecture change and green hosted regressions. Phase 1 is incomplete; missing 1.4/1.5 must not be
described as merged. Keep new rule PRs separate, one rule each, with a failing planted pattern and green real head on
GitHub `windows-latest`. Never substitute local proof, a green unrelated job, or these source links for inspected evidence.

2026-11-14, independent review follow-up (**manual / mock**): a partial base contract could silently skip its judge
when the manifest remained but the checker was missing. The workflow now rejects either partial state and an unavailable
base commit. Five hosted real-git fixtures execute the actual workflow bootstrap, and all pass on `1ff39e2`.
The [older bootstrap red](https://github.com/vikas53953/bluey/actions/runs/43487946714/job/132101291751) stops at the
script-only case for lacking the required BOO-CI-001 diagnostic; it does not demonstrate the old manifest-only silent
skip at runtime. No fresh independent PASS or full-app acceptance follows from these static receipts.