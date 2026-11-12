# Realtime transcript repair: genuine RED

Date: 2026-11-07

The independent transcript test author added two focused suites before the
implementation builder was released:

- `tests/Boo.Integration.Tests/RealtimeTranscriptTests.cs` covers all six
  transcript delta/completion kinds, corrected completion replacement,
  interleaved item and content-index identity, completion without deltas,
  repeated finals, late deltas, empty-final clearing, audio-channel preference,
  invalid identity/text, reset, speaker mapping, whitespace deltas, independent
  same-speaker item accumulation, audio-before-final text suppression, and
  negative reducer content indexes.
- `tests/Boo.Integration.Tests/RealtimeTranscriptProtocolTests.cs` covers final
  `text`, audio/input `transcript`, nonnegative `content_index`, compatible
  absent index, safe rejection of null/string/fractional/negative/overflow
  indexes, and call-ID preference with an index present.

The existing frozen call-ID regression assertion remains byte-for-byte
unchanged; the new call-ID case is integration-specific because it checks
compatibility with the newly added content index.

## Commands

```powershell
./scripts/build.ps1 -Action restore -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj
./scripts/build.ps1 -Action test -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -Configuration Release -Filter 'FullyQualifiedName~RealtimeTranscript'
```

## Result

Restore completed successfully. Release compilation completed, then the
filtered run found 24 transcript tests: 22 failed and 2 passed. The reducer
failures were the expected initial `NotImplementedException` from
`Boo.Providers.RealtimeTranscript.Apply(RealtimeEvent)`. The protocol failures
showed the pre-repair parser not reading final output `text`, defaulting
`content_index` to zero, and accepting invalid provided indexes. The two passes
were compatibility cases already satisfied by the existing parser (absent
index and a zero-index audio transcript).

This is deterministic reducer/protocol RED evidence only. No live provider,
microphone, GUI, credentials, API request, or environment-file access was
used. The implementation builder still owns the production repair; this test
author owns no production code.

Root independently reproduced the final 24-case suite in the canonical
checkout before implementation: 22 failed, 2 passed, no skips. The retained
TRX is `TestResults/<user>_<machine>_2026-11-07_12_25_10_net10.0.trx`.
The author released ownership before root extended oracle revision 9; all
prior frozen hashes remain unchanged. The reducer stub is a compile-time
contract only, with no working behavior or application wiring yet.
