# Response deadline repair: Release verification

Date: 2026-11-07 (Asia/Calcutta)

The implementation updates `src/Boo.Providers/ResponsesClient.cs` to read the
injected `HttpClient.Timeout` once and apply that finite budget to both
`SendAsync` and `ReadAsStringAsync` through one owned linked cancellation
source. Infinite timeout remains infinite. Caller cancellation is checked
before sending and after body reading and decoding; caller cancellation wins
over the owned deadline. Deadline cancellation and direct or nested timeout
exceptions normalize to the fixed `ResponseTimeoutException` message. Other
non-caller cancellation normalizes to the fixed interruption message. The
injected `HttpClient` is not mutated or disposed, and request payload,
selected model, schema, and credential handling remain unchanged.

`src/Boo.Providers/ResponseTimeoutException.cs` remains the public sealed
zero-argument timeout type with the fixed safe message.

## Red receipt used

`docs/verification/response-deadline-red.md` records the unchanged frozen
13-case pre-repair result: 8 failed and 5 passed. Its focused Release TRX
receipt was produced before this implementation and is not used as a pass
claim.

## Verification

Restore:

```powershell
.\scripts\build.ps1 restore
```

Focused frozen suite:

```powershell
.\scripts\build.ps1 test -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -Configuration Release -NoRestore -Filter 'FullyQualifiedName~ResponseDeadline'
```

Result: **0 failed, 13 passed, 0 skipped, 13 total**. TRX:
`TestResults/<user>_<machine>_2026-11-07_12_49_22_net10.0.trx`.

Existing provider contract/regression/action tests:

```powershell
.\scripts\build.ps1 test -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -Configuration Release -NoRestore -Filter 'FullyQualifiedName~Provider'
```

Result: **0 failed, 17 passed, 0 skipped, 17 total**. TRX:
`TestResults/<user>_<machine>_2026-11-07_12_49_31_net10.0.trx`.

Full Release build:

```powershell
.\scripts\build.ps1 build -Configuration Release -NoRestore
```

Result: succeeded with 0 warnings and 0 errors.

Full Release suite:

```powershell
.\scripts\build.ps1 test -Configuration Release -NoRestore
```

Result: **0 failed, 157 passed, 1 skipped, 158 total** across the two test
assemblies. TRX receipts:
`TestResults/<user>_<machine>_2026-11-07_12_49_54_net10.0.trx` and
`TestResults/<user>_<machine>_2026-11-07_12_49_54_net10.0[1].trx`.

Frozen oracle:

```powershell
python .\scripts\test_oracle.py
```

Result: `ORACLE_VERIFIED revision 10 33 files`.

The repository-wide format verification was also attempted with
`.\scripts\build.ps1 format`; it remains red on pre-existing whitespace
diagnostics in unrelated `src/Boo.Core` files (`AnnotationValidator.cs`,
`Contracts.cs`, and `RequestCoordinator.cs`). It did not modify the worktree
and is outside this provider-only repair.

No live provider, network, GUI, native, microphone, credential, or `.env`
activity was used. Packaging and native acceptance remain outside this repair.
