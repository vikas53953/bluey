# Control observation Core RED receipt

Date: 2026-11-07
Base: `bbbedee0cab8a43b8416413478a105260af85d54`

The independent test-author packet adds only
`tests/Boo.Core.Tests/ControlObservationTests.cs`. The public Core factory in
`src/Boo.Core/ControlObservation.cs` remains the supplied throwing scaffold;
no production code, existing tests, or contracts were changed.

## Public seam and coverage

The tests call `ControlObservation.Create(ScreenContext,
IEnumerable<ControlCandidate>)` and inspect only the returned `Context`,
`Controls`, and public `ObservedControl` properties. They cover:

- exact selector, name, role, value, bounds, capability, and runtime-ID copies;
- acceptance at the exact selector, name, role, and runtime-ID length limits;
- source-array/runtime-ID mutation after creation and a read-only published collection;
- value preservation through 1,024 characters and unavailable longer values;
- disabled, off-screen, password, selector/name/role, runtime-ID, and bounds eligibility;
- negative display origins, a target crossing the display boundary, and finite edge checks;
- input order, the 128-control publication limit, 512-candidate consumption, and
  duplicate suppression before publication using ordinal IDs;
- null top-level inputs and invalid context identity, geometry, scale, target,
  and frame dimensions.

Existing Core test patterns inspected before authoring were
`GeometryRegressionTests.cs`, `ActionPolicyTests.cs`, and
`ActionPreviewValidationTests.cs`, along with `Contracts.cs` and the supplied
factory scaffold. No private members, reflection, Windows APIs, provider
transport, GUI, credentials, or invented APIs are used.

## Focused run

```powershell
./scripts/build.ps1 -Action test -Configuration Release -Project tests/Boo.Core.Tests/Boo.Core.Tests.csproj -Filter FullyQualifiedName~ControlObservationTests
```

The Release project build succeeded, then the filtered test run was genuine
RED against the unchanged scaffold:

```text
Failed: 24, Passed: 0, Skipped: 0, Total: 24
System.NotImplementedException: Control observation implementation awaits independently authored tests.
```

TRX: `TestResults/<user>_<machine>_2026-11-07_14_04_27_net10.0.trx`

The duplicate/limit case uses a public `IEnumerable<ControlCandidate>` that
throws if the factory tries to enumerate beyond the first 512 candidates. The
public wording does not define whether a null element inside a non-null
sequence is an argument error or an invalid candidate, so the tests require
null top-level inputs and omit invalid null fields without inventing a null
element policy. Runtime-ID integer values are treated as opaque and copied
exactly; only the documented count bound is asserted.

Release ownership: the independent test author owns this test and receipt;
the separate Core builder owns the implementation. No implementation was
started here.
