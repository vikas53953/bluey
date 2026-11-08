# Realtime transcript repair build evidence

Date: 2026-11-07

## Scope

This provider-only repair makes Realtime transcript events safe to apply to
independent conversation rows. The parser preserves the existing function-call
ID preference, reads final output text from `text`, reads audio and input
transcripts from `transcript`, and accepts an optional nonnegative integer
`content_index` (missing remains compatible with index zero). Invalid provided
indexes are rejected without exposing provider payloads.

`RealtimeTranscript` accumulates deltas by item, content index, and channel,
replaces accumulated text with authoritative completion text, ignores late
deltas, and supports completion without a preceding delta. Input speech is
reported as `You`; assistant audio and text are reported as `Boo`. Audio and
text channels are finalized independently, with audio taking precedence for an
assistant content item. `Reset()` clears only reducer session state.

Only these production files are owned by this slice:

- `src/Boo.Providers/RealtimeClient.cs`
- `src/Boo.Providers/RealtimeTranscript.cs`
- `docs/verification/realtime-transcript-build.md`

WPF/MainWindow integration, row-index mapping, connection reset wiring, and
all live provider, microphone, GUI, credential, and API behavior remain owned
by the parent integration lane.

## Test-first evidence

The independent test author’s unchanged RED receipt is
`docs/verification/realtime-transcript-red.md`. It records the pre-repair
focused run with 24 transcript/protocol cases: 22 failed and 2 passed. The
reducer failures were the intentional stub’s `NotImplementedException`; the
protocol failures covered final output `text`, content-index parsing, and
invalid provided indexes. No production implementation was used as RED
evidence.

## Verification

Restore and focused Release tests were run with the pinned wrapper:

```powershell
./scripts/build.ps1 -Action restore -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj
./scripts/build.ps1 -Action test -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -Configuration Release -Filter 'FullyQualifiedName~RealtimeTranscript'
```

Result:

```text
Passed!  - Failed: 0, Passed: 24, Skipped: 0, Total: 24
```

The Release compilation produced the provider, Windows, and integration-test
assemblies without warnings or errors. The test cases and frozen contracts
were used unchanged.

The frozen test oracle was checked after implementation:

```powershell
python scripts/test_oracle.py
```

Result:

```text
ORACLE_VERIFIED revision 9 32 files
```

No live provider, microphone, GUI, credential, API, or environment-file access
was used. Audible quality, real asynchronous event ordering, and native row
replacement remain unverified until parent integration and independent QA.

Ownership of this provider slice is released to the parent for integration.
