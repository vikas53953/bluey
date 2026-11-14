# BOO-CI-001: declared gates must remain blocking

2026-11-14. Source: [#10](https://github.com/vikas53953/bluey/pull/10) closed an empty-test green build;
[#34](https://github.com/vikas53953/bluey/pull/34) later closed omitted tools test coverage. Both show why a green
pipeline is insufficient when its test gates are missing or incomplete. Phase 1 item 1.5 calls for a gate manifest.

`gate_manifest.py` parses workflow YAML, rejects duplicate mapping keys, and compares three declared steps in
`windows-ci/build-test-publish` to `gate-manifest.json`: frozen oracle, static test gate, TRX result gate.
Each must appear exactly once with the entire expected step mapping. Conditional/soft-failing steps, command changes,
missing steps, job conditions, job soft failure, alternate runners and run defaults fail with BOO-CI-001 and the fix.
Unrelated jobs are not constrained. Its own four named checker steps and Windows job are declared too.

The new workflow runs on `windows-latest`. On PRs it runs the base SHA's checker and manifest against head workflows
first, then the head checker. No base contract exists on the introducing PR, so only its new contract runs then.
Self-tests invoke the CLI in temporary directories against semantic workflow fixtures, including removing/disabling
the checker job. No product, frozen test or oracle file is modified.

## Trust boundary and intentional changes

**Owner action pending:** require `gate-manifest` in branch protection. An optional workflow cannot force GitHub to
run itself after deletion. Requiring its job prevents that missing-check bypass. This PR does not change protection.
Trusted checks also rely on the pinned toolchain/actions and their execution environment. This is a bounded workflow
contract, not proof against hostile actions, forged TRX, shell/tool substitution, or changes to the tests/checks themselves.
It does not claim that all CI checks exist or that a named test passed. Existing oracle/test guards remain necessary.

For a legitimate policy change, submit a separate owner-reviewed policy PR explaining the changed contract and its
red/green proof. The old contract still judges that PR: existing commands cannot simply be relaxed by changing both
workflow and manifest. Retiring/changing an existing command needs an owner-controlled trusted-contract transition;
do not bypass the base judge. Additional unrelated workflow steps do not need a contract change.

No local test result counts here. Hosted self-test and planted-removal runs must be linked in the PR body. A failed
build or unrelated failure is not red proof; the named manifest step must report BOO-CI-001 for the planted removal.

## Contract retention (Codex BLOCK on 093703e)

The trusted base invocation also passes `--head-manifest docs/quality/gate-manifest.json`. The trusted checker
validates both contracts and refuses any missing job or missing/changed existing step mapping. New job/step
contracts are allowed; reordering is harmless. Duplicate identities and empty contracts fail closed.
This closes the two-PR route where one PR deletes a manifest entry and the next deletes its gates.
There is no policy-waiver/sign-off bypass implemented here. Retiring an old contract is deliberately blocked
until an owner-controlled trusted-policy transition is designed and reviewed separately.

Hosted fixtures cover shrinking a job, shrinking one step from a still-nonempty job, changed commands, softened
step mappings, duplicates, reordering, additions, and executing the actual trusted workflow bootstrap with a
committed base contract and a shrunk head manifest. Existing test/oracle files remain identical to lane/face.
