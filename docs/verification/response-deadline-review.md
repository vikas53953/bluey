# Response deadline integration review

The independent Advisor requested precise timeout classification, safe message
contracts, post-body/decode checks, resource ownership, and post-await UI
generation checks. Root revised the plan; the Advisor returned PLAN_APPROVED
before independent test authoring and implementation.

The author and root each observed 8 failures and 5 passes before the builder
started. The 13-case contract is frozen in oracle revision 10.

## Initial UI review

A separate reviewer found no blocking defect in the scoped MainWindow wiring.
Typed timeout and non-caller interruption messages match the contract. Genuine
Stop and newer generations keep their existing state. Screen-help sends safe
timeout feedback; post-await status writes recheck generation, connection, and
source identity. The shared error formatter recognizes the sealed timeout
exception. Its generic cancellation mapping remains for other paths; these
two response paths handle non-caller cancellation explicitly.

## Root integrated checks

After integrating the separate builder's provider changes, root ran the full
Release suite: 157 passed, one unchanged host-dependent fixture skip. Oracle
revision 10 verified 33 frozen files unchanged; diff check passed. TRX evidence:
`TestResults/<user>_<machine>_2026-11-07_12_52_54_net10.0.trx` and
`TestResults/<user>_<machine>_2026-11-07_12_52_55_net10.0.trx`.

## Final assembled review

The reviewer found no deadline, cleanup, caller-precedence, body/decode, or
stale-generation defect. It requested a clearer UI for the already-normalized
interruption error. Although the plan allowed the generic unavailable fallback,
root added an exact type/message whitelist for the known fixed interruption
string. Arbitrary exception text still receives a generic fallback. A separate
recheck found no remaining scoped findings, and the final Release build passed
with zero warnings/errors.

Wrapped-only cancellation is a deliberate boundary of this repair: a transport
error containing an inner cancellation without a timeout cause retains generic
network/body failure handling. No actual producer requiring a broader policy
was demonstrated. Direct cancellation and direct/inner timeout causes follow
the tested policy. This boundary must not be mislabeled as tested normalization
of every possible exception graph.

Native verification remains pending. This repair must not be reported as a
provider-speed or teaching-success improvement.
