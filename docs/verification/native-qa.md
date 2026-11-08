# Native QA — candidate1 exploratory smoke

Date: 2026-11-07 (Asia/Calcutta)
Status: BLOCKED; independent receipt audit complete, no native behavior passed

## Candidate identity

- Source commit supplied by root: `d181791`
- Package: `artifacts/Boo-0.1.0-candidate1-win-x64.zip`
- Package SHA-256: `0209EECD180D02D5444EF6E175BA22CDD4C8A388859C61E7B17958C33E292204` (matches RELEASE)
- Manifest: `artifacts/candidate-1-manifest.json`
- Manifest SHA-256: `88A31389B0EB81CF3AC73AB1A9D1FB51E7208A825DEB34F21CF691B2A0E9DA40` (matches RELEASE)
- `artifacts/candidate/Boo.dll` SHA-256: `09B5C5213358A06A9AE94434069B1929860CD624F515258F8C741D524C89879` (matches RELEASE)
- `artifacts/candidate/Boo.exe` SHA-256 observed: `4D05C3139984BB720B29C9DA2FBE61F2A34FC3A9515C4950C3222BAC0DDA5A7B`

## Live inventory before GUI

PowerShell read-only process refresh at 2026-11-07:

- Boo PID 18904, path `artifacts/candidate/Boo.exe`, responsive, no normal main-window title (likely tray/notch-only state).
- Fixture PID 16996, path `artifacts/qa-fixture/Boo.QaWorkspace.exe`, title `Boo QA Workspace`, responsive.

## Blocker

The child tool catalog exposes no `mcp__node_repl__js` / `@oai/sky` route. I did not substitute terminal/UIAutomation automation because the required Computer Use guidance mandates Sky for Windows GUI actions. Therefore no screenshot, focus, typing, drawing, approval, Stop, accessibility, or quit result is claimed. Candidate1 native smoke remains `BLOCKED`, not accepted.

Next action: parent must restore/provide the Sky node-repl route, then refresh process/window inventory and run the bounded observe → one action → refresh smoke sequence. Stop further actions after the first actionable product failure.

## Forwarded native-driver receipt audit

Receipt: `docs/verification/native-driver.md`, timestamp `2026-11-07T05:13:20Z`.
The receipt is bound to the same candidate package, manifest, and `Boo.dll`
hashes above. Its Sky route was healthy for the fixture: it observed and
maximized fixture PID `16996` / window `244516272`, and saw the expected target,
edit, Apply edit, Undo, status, and scroll controls. It performed no Boo
request, screenshot, typing, authentication, provider turn, or consequential
action.

The receipt reports no candidate Boo window or tray item for responsive PID
`18904`. The source flags (`ShowInTaskbar=false`, `ShowActivated=false`,
`WS_EX_TOOLWINDOW`, `WS_EX_NOACTIVATE`, and `WDA_EXCLUDEFROMCAPTURE`) are source
observations only. They do not isolate which flag, if any, explains the absent
Sky target; that causal explanation remains an inference, not an experimental
finding. The receipt also correctly records that the supported Sky API has no
system-tray enumeration or tray-menu invocation capability.

## Case status after receipt audit

No native case is PASS. Fixture control visibility is deterministic support
evidence only and cannot prove Boo behavior.

| Cases | Status | Evidence boundary / missing proof |
| --- | --- | --- |
| NAT-001 | NOT TESTED | Process identity was observed, but no Boo title/tray/quit or post-quit inventory was exercised. |
| NAT-002–NAT-004 | BLOCKED | No targetable Boo window; no notch appearance, panel, focus, typing, display, or return-focus evidence. |
| NAT-005–NAT-007 | BLOCKED | Fixture controls were visible, but Boo produced no annotation/request; no click-through, edit/Undo-through-Boo, stale-target, or fail-closed evidence. |
| NAT-008–NAT-010 | NOT TESTED | No Boo capture/audio session, indicator, exclusion frame, Stop, lock/sleep, permission, device, or network-failure evidence. |
| NAT-011–NAT-013 | NOT TESTED | No human voice, typed turn, provider response, audio, transcript, or interrupt evidence. |
| NAT-014 | NOT TESTED | No settings/profile inspection or credential/privacy behavior was exercised; credentials remain unread. |
| NAT-015–NAT-018 | NOT TESTED | No research, source links, file action, approval, cancellation, race, protected-target, or hostile-content run. |
| NAT-019–NAT-022 | NOT TESTED | No focus lifecycle, nudge, lock/sleep boundary, history, or persistence run. |
| NAT-023–NAT-024 | NOT TESTED | No Boo accessibility tree, keyboard traversal, reduced-motion, help/about, or disclosure evidence. |
| PERF-001–PERF-002 | NOT TESTED | Zero latency/turn samples; no live provider or human-speaker evidence. |
| RELEASE-001 | NOT TESTED | No 15-minute exact-candidate soak, before/after hash, crash/error, process, handle, memory, or cleanup receipt. |
| PROCESS-001–PROCESS-002 | NOT TESTED | Candidate identity and independent receipt exist, but no baseline replay or case-by-case exact-candidate QA completion exists. |

## Actionable evidence gap

To advance candidate1, the driver needs an allowed, observable path to the
candidate's user-facing notch/panel and tray-owned recovery controls. The next
receipt must first refresh the live process/window inventory, state exactly
which Sky-returned object is being used, and capture the first visible Boo
state before any action. If the supported Sky boundary still cannot expose the
notch or tray, the affected rows remain BLOCKED; source flags must not be
relabelled as the experimentally isolated cause, and no native PASS may be
claimed from the fixture-only observation.

Candidate1 remains fixed to source `d181791a7d8a422794650d2189281891a08f3b57`.
The current root source refactor `7ef0e07` is a different revision and cannot
change this receipt's provenance or acceptance status.
