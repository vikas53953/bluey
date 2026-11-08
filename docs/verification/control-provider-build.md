# Observed-control provider build

Source was built from the assigned clean worktree at `bda7d7d72387e8129c29f9c0970944812ffcae02`.
Only `src/Boo.Providers/ResponsesClient.cs` and this verification record are
owned by this slice. Frozen tests and contracts were not changed.

## Behavior delivered

- Validates a supplied control observation against the captured frame's exact
  context and request generation before reading credentials or sending HTTP.
- Sends the bounded observed-control list as a separate untrusted `input_text`
  JSON object with only automation ID, name, role, value, physical desktop
  bounds, and capability fields. Runtime IDs and rejected candidates are not
  serialized.
- Instructs the model to use exact ordinal observed IDs, copy observed names,
  and require `canSetValue` for observed `type_text` proposals.
- Filters observed click/type proposals by exact ordinal ID/name, fills an
  omitted observed name, requires an observed settable ID for `type_text`, and
  retains coordinate-only clicks, other proposal order, and the old two-
  argument call contract.
- Existing endpoint, model, schema, timeout, cancellation, and transport
  behavior remains unchanged.

## Evidence

The independent pre-build focused run was genuine provider RED: 10 failures
and 1 legacy compatibility pass. After implementation:

```text
Pre-build TRX: TestResults/vikasmit_VIKASMIT_2026-11-07_14_13_52_net10.0.trx
```

```text
./scripts/build.ps1 -Action test -Configuration Release -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -Filter FullyQualifiedName~ControlObservationProviderTests
Passed: 11, Failed: 0, Skipped: 0
TRX: TestResults/vikasmit_VIKASMIT_2026-11-07_14_15_38_net10.0.trx
```

The full Release integration suite also passed:

```text
./scripts/build.ps1 -Action test -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -Configuration Release
Passed: 88, Failed: 0, Skipped: 0
TRX: TestResults/vikasmit_VIKASMIT_2026-11-07_14_15_54_net10.0.trx
```

The repository oracle passed:

```text
python scripts/test_oracle.py
ORACLE_VERIFIED revision 13 36 files
```

`git diff --check` was run after the implementation; no whitespace errors were
reported.

## Boundaries and ownership

This is fixture/transport evidence only. It does not prove live provider
behavior, Windows UI Automation reads, native click/type postconditions,
stale-control invalidation at commit time, or complete edit/Undo delivery.
The parent integration owner must inspect this diff, perform authoritative
current-main verification, and own the canonical release/commit decision.

Root inspected and integrated the source, clarified that observed bounds are
physical desktop pixels and coordinate-only clicks retain their existing
proposal fields, then reran the complete integration suite: 88 passed,
0 failed. Canonical TRX:
`TestResults/vikasmit_VIKASMIT_2026-11-07_14_18_34_net10.0.trx`.
Oracle revision 13 remained unchanged. These results are local transport tests,
not live provider or native desktop proof.
