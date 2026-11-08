# Provider and native-audio build evidence

## Canonical integration and bounded live probes

Root integrated the owned provider/audio files and reran the canonical
integration suite: 22 passed, zero failed (`2026-11-07_10_30_03_net10.0.trx`).
Oracle revision 3 verifies 23 files, retaining all previous hashes.

Root's separate `tools/Boo.LiveProbe` used the protected credential store and
the production adapters with synthetic input. Responses returned a structured
ring proposal in 5142 ms; Decisions returned an on-task distribution in 4291
ms. These are one-shot connection/adapter checks, not drawing quality or
latency-distribution acceptance. Realtime initially produced a tool call and
no audio for an ordinary typed prompt. After explicit conversation/tool-use
instructions, it returned `Boo is ready.` plus 79,200 PCM bytes in 3730 ms.
No microphone was used and that probe did not play audio. Human speech,
matching audible native playback, and packaged acceptance remain open. The
later null-argument repair is covered by independent pre-fix RED and post-fix
transport tests; the earlier live probes are not evidence of its behavior.

## Scope

This receipt covers the provider/audio slice at worktree `boo-providers-audio`,
based on `b2b515b`. Only these production files were changed:

- `src/Boo.Providers/ResponsesClient.cs`
- `src/Boo.Providers/DecisionsClient.cs`
- `src/Boo.Providers/RealtimeClient.cs`
- `src/Boo.Windows/AudioSession.cs`

The frozen provider contracts and provider regression tests were read-only. The
implementation uses injected HTTP transport for Responses and Decisions,
official-origin-only bearer credentials, memory-only JPEG/audio payloads,
strict structured marks/actions and citations, pinned choice probabilities with
separate confidence, and the documented Realtime GA WebSocket event shapes.
Realtime input transcription uses the live-proven `gpt-4o-mini-transcribe`
model, and `ConnectAsync` waits for `session.updated` before becoming ready.
The session instructions keep ordinary conversation direct and spoken, reserve
`screen_help` for explicit teach/research/task requests, and report failed or
incomplete `response.done` events with sanitized readable status text.
Native audio uses NAudio's explicit-start `WaveInEvent` and buffered
`WaveOutEvent` at mono PCM16/24 kHz; stop clears playback and disposes capture.

## Verification

The frozen oracle was verified before implementation:

```text
python scripts/test_oracle.py
ORACLE_VERIFIED revision 3 23 files
```

The provider contract baseline was intentionally red before implementation:

```text
powershell.exe -ExecutionPolicy Bypass -File scripts/build.ps1 -Action test -Project tests/Boo.Integration.Tests -Filter "FullyQualifiedName~ProviderContractTests|FullyQualifiedName~ProviderRegressionTests|FullyQualifiedName~ProviderActionArgumentTests"
Failed: 2, Passed: 14, Total: 16
```

After implementation, the same pinned wrapper passed all provider cases:

```text
powershell.exe -ExecutionPolicy Bypass -File scripts/build.ps1 -Action test -Project tests/Boo.Integration.Tests -Filter "FullyQualifiedName~ProviderContractTests|FullyQualifiedName~ProviderRegressionTests|FullyQualifiedName~ProviderActionArgumentTests"
Passed! - Failed: 0, Passed: 16, Skipped: 0, Total: 16
```

The oracle was rechecked after implementation and remained verified. No paid
call, API key, live microphone, or desktop voice session was used by this
slice. The parent integration lane must run the complete suite and package
candidate before native QA.

The parent live Realtime probe independently passed the ordinary typed turn
with the exact requested spoken response, no tool call, and 79,200 PCM audio
bytes. That live result is parent-owned evidence; this worktree did not run the
probe.

The full solution test command was also run in this isolated baseline. It
remains red in unrelated lanes because their frozen stubs are still present:
`60 failed, 0 passed, 1 skipped` in `Boo.Core.Tests`, and `6 failed, 11
passed` in `Boo.Integration.Tests/StoragePrivacyTests`. The provider contract
cases remained green in that run.

## Remaining gaps

- L-20 live voice acceptance is unverified: no acoustic intelligibility,
  barge-in latency, device lifecycle, model entitlement, or packaged-build
  proof is claimed.
- Realtime socket lifecycle and screen-help routing are implemented, but were
  not exercised against a live connection in this slice.
- Audio device enumeration/capture/playback was not tested on the target
  Windows desktop; failures remain readable through the `Failed` event.
- The Realtime client now waits for `session.updated` before `ConnectAsync`
  returns and reports a sanitized configuration rejection; socket readiness and
  late-event behavior still need a live or transport-fake lifecycle test.
- Null-valued structured action arguments are omitted while meaningful empty
  strings remain present, preventing numeric and file-content alias failures.
