# Presence and appearance RED baseline

Contract author: independent presence test lane. Baseline checkout:
`cf147c5fbd9f658aeb997c9f297ff78db8449f42`.

The three production files in this baseline are public-interface stubs only;
behavior is intentionally not implemented. The tests below exercise public
seams and real temporary files. They do not inspect source text, XAML, or
private state.

## Added contract

- `Boo.Core.PresenceController`: pointer, capture, and microphone activity
  setters plus read-only `IsRevealed`.
- `Boo.Core.NotchAppearance`: default black/white colors and `Validate()`.
- `Boo.Storage.AppearanceStore`: load/save of appearance settings.
- Native acceptance cases: `docs/verification/acceptance-presence-window.md`.

## Expected RED command

```powershell
& .\scripts\build.ps1 test -Project tests/Boo.Core.Tests/Boo.Core.Tests.csproj -Configuration Release -Filter "FullyQualifiedName~PresenceControllerTests|FullyQualifiedName~NotchAppearanceTests"
& .\scripts\build.ps1 test -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -Configuration Release -Filter "FullyQualifiedName~AppearanceStoreTests"
```

Expected result against the stubs: test discovery/build succeeds, but the
focused tests fail because each not-yet-implemented public operation throws
`NotImplementedException`. This is the required RED state; a missing SDK,
broken discovery, or compile failure is not RED evidence.

## Run receipt

Run date: 2026-11-07 (Asia/Calcutta), Windows x64, Release configuration.

| Command | Exit code | Result | TRX |
| --- | ---: | --- | --- |
| `& .\scripts\build.ps1 test -Project tests/Boo.Core.Tests/Boo.Core.Tests.csproj -Configuration Release -Filter "FullyQualifiedName~PresenceControllerTests\|FullyQualifiedName~NotchAppearanceTests"` | 1 | Build/discovery succeeded; 21 failed, 0 passed. Failures are the expected `NotImplementedException` from the public stubs. | `TestResults/vikasmit_VIKASMIT_2026-11-07_19_16_21_net10.0.trx` |
| `& .\scripts\build.ps1 test -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -Configuration Release -Filter "FullyQualifiedName~AppearanceStoreTests"` | 1 | Build/discovery succeeded; 7 failed, 0 passed. Failures are the expected `NotImplementedException` from the public storage stubs. | `TestResults/vikasmit_VIKASMIT_2026-11-07_19_15_40_net10.0.trx` |

Root integrated the independently authored files by exact bytes and reran the
combined focused Release filter against the nonfunctional stubs in the canonical
checkout. Root also observed 21 Core failures and 7 Storage failures, all expected
stub failures, with discovery and compilation succeeding. Root TRX files:
`TestResults/vikasmit_VIKASMIT_2026-11-07_19_17_00_net10.0.trx` and
`TestResults/vikasmit_VIKASMIT_2026-11-07_19_17_03_net10.0.trx`.

The receipt is bound to baseline SHA `cf147c5fbd9f658aeb997c9f297ff78db8449f42`
and must not be reused after production behavior changes. `TestResults/` is an
ignored local output directory; the TRX files are retained locally for root
inspection and are not part of the source change.
