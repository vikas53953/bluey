# Observed-control Core build evidence

Date: 2026-11-07
Base: `6d38e9d1388622be062419044d805a02788457e0`

## Implementation

`ControlObservation.Create` now validates the selected display, target identity,
target/display geometry, computed finite edges, and positive frame dimensions.
It examines no more than 512 candidates, omits invalid candidates, removes all
ordinal duplicate IDs before publication, and publishes at most 128 controls in
input order. Eligible bounds must be finite, positive, and wholly contained in
both the display and target window. Selector identity, names, and roles retain
their exact source text; values longer than 1,024 characters are unavailable.

Published runtime IDs are copied into immutable arrays and the control list is
immutable. Null top-level inputs throw `ArgumentNullException`; invalid context
throws `ArgumentException`; invalid individual candidates are omitted. No
provider, Windows, WPF, test, oracle, or contract files were changed.

## Verification

Focused Release verification:

```powershell
./scripts/build.ps1 -Action test -Configuration Release -Project tests/Boo.Core.Tests/Boo.Core.Tests.csproj -Filter FullyQualifiedName~ControlObservationTests
```

Passed: 24, skipped: 0, failed: 0. TRX:
`TestResults/<user>_<machine>_2026-11-07_14_09_09_net10.0.trx`.

Core suite and oracle verification:

```powershell
./scripts/build.ps1 -Action test -Configuration Release -Project tests/Boo.Core.Tests/Boo.Core.Tests.csproj
python scripts/test_oracle.py
```

The full Core suite passed 115 tests with 1 environment-dependent skip and 0
failures. TRX:
`TestResults/<user>_<machine>_2026-11-07_14_09_21_net10.0.trx`.
The oracle reported `ORACLE_VERIFIED revision 12 35 files`.

Root integration used the builder's exact file bytes, then repeated the Core
suite: 115 passed, 1 unchanged host-dependent skip, 0 failed. Canonical TRX:
`TestResults/<user>_<machine>_2026-11-07_14_11_03_net10.0.trx`.
Oracle revision 12 verified unchanged. Provider and native integration are
separate work; this is not desktop acceptance.
