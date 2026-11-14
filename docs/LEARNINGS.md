# Lessons enforced by structure

This branch adds only the next scoped entry. The full preceding audit and Phase 1 inventory are in
[PR #85](https://github.com/vikas53953/bluey/pull/85); preserve both records when reconciling their additions.

| Date | What went wrong / source | Encoding |
| --- | --- | --- |
| 2026-11-14 | Text checks differed from the real consumer: [#23](https://github.com/vikas53953/bluey/pull/23#issuecomment-6842652780) erased alias hits; [#28](https://github.com/vikas53953/bluey/pull/28#issuecomment-6845758415) missed compiler-input diagnostic suppression. | BOO-LINT-002A uses Roslyn to reject source warning-disable directives in committed src C# blobs. Hosted red/green proof pending in this PR. This is only the pragma slice of the missing Phase 1 no-suppress check; configuration/attribute/nullable suppression coverage is still pending or owned by #28. |

Phase 1: #85 gate-manifest has exact-head QA PASS and review PASS WITH ISSUES at dd21010, still unmerged/owner
protection pending. This new syntax rule is in PR preparation, not merged/proven until hosted receipts are inspected.
Threading/catch analyzers and the rest of the general no-suppress policy remain missing; no Phase 1 completion claim.
