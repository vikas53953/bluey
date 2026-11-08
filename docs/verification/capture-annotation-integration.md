# Capture activity and annotation outcome integration

Root integrated the independent Core builder after the independently authored
tests failed on the no-op contract. The frozen tests remain unchanged in
oracle revision 11 (34 files). Root's Release suite passed 168 tests, with one
unchanged host-dependent reparse-fixture skip:

- Core: 91 passes, one skip,
  `TestResults/vikasmit_VIKASMIT_2026-11-07_13_19_32_net10.0.trx`.
- Integration: 77 passes,
  `TestResults/vikasmit_VIKASMIT_2026-11-07_13_19_36_net10.0.trx`.

Each typed request, realtime screen-help invocation, and focus check owns its
capture activity. The indicator starts before native capture invocation,
receives a dispatcher rendering opportunity, and retains that operation until
its awaited work and cleanup end. No-frame paths end activity before text-only
provider work. Every completion reads the tracker rather than writing a shared
boolean, and the notch's Stop caller preserves the panel's activity status.

Annotation outcomes have a separate accessible message. Reply rendering clears
the previous overlay/outcome before handling the new result. Validation
reasons, no-mark replies, and exclusion failures survive generic action status;
exclusion failure is reported after StopAll cleanup. The existing validation
and actual drawing code are unchanged. The prewritten UI cases are in
`docs/plans/2026-11-07-annotation-outcome-repair.md`.

Fresh independent integration review against `2638403` found no material new
issue in the final Core tracker, three capture callers, App-to-notch Stop
chain, and annotation outcome paths. It inspected source only; it did not
certify visual timing or native behavior. Earlier candidate9 evidence predates
these changes and is not evidence for this integration.

Remaining acceptance: exact packaged capture timing/overlap/cancellation,
annotation outcome observation, exclusion-failure injection, and actual ring
geometry. Same-connection voice tool ordering and target freshness remain
separate open findings; this repair does not establish turn-bound authority or
invalidate every stale target transition.

After behavior-preserving cleanup, root reran the full Release suite: 168
passes and the same skip, with TRX files ending `13_24_30_net10.0.trx` and
`13_24_31_net10.0.trx`. Oracle revision 11 is unchanged. Whole-repository
format verification still fails on pre-existing whitespace in untouched Core
files, including frozen Contracts; see `artifacts/format-revision11.log`.
The final Release build passed with zero warnings/errors. Independent review
rechecked the final snapshot and timeout-handler cleanup and reported no new
material findings. Candidate10 subsequently retained an accessible marks-shown
outcome through reply completion and cleared it on Stop. The sampled capture
indicator states were both off; QA did not observe the active interval. See
`native-teaching-candidate10.md`; native geometry/timing acceptance remains open.
