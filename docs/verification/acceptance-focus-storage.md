# Focus and Storage Acceptance Contract

This contract freezes the focus-policy and local-persistence behavior tested by
`FocusPolicyTests` and `StoragePrivacyTests`. Expected values come from the
product plan and the pinned Bluey reference (`Shared/FocusSession.swift`), not
from an implementation copy. These cases exercise public APIs only.

## Focus cases

| Case | Trigger | Expected observable outcome | Evidence |
| --- | --- | --- | --- |
| FOCUS-001 | Observe fresh off-task checks with probability `0.96` every 30 seconds from elapsed `0` through `120` seconds | No nudge occurs before the lower bound; the check at exactly 120 seconds nudges. | xUnit unit test |
| FOCUS-002 | Observe fresh off-task checks with probability `0.50` every 30 seconds through `210`, then at `239` and `240` seconds | No nudge occurs at 239 seconds; the check at exactly 240 seconds nudges. | xUnit unit test |
| FOCUS-003 | Use probability `0.96`, confidence `0`, and otherwise fresh 30-second checks from 0 through 120 seconds | Confidence is diagnostic only; the check at the 120-second threshold still nudges. | xUnit unit test |
| FOCUS-004 | Submit 30-second-cadence checks aged `20` seconds; separately submit a check aged greater than 20 seconds | Age 20 is accepted; an age above 20 is rejected and resets the evidence run. | xUnit unit test |
| FOCUS-005 | Submit uncertain, probability-invalid, out-of-order, and over-gap observations between valid off-task checks | Each invalid/uncertain, non-increasing elapsed time, or gap greater than 45 seconds clears the accumulated run; no stale run can nudge. | xUnit unit test |
| FOCUS-006 | Nudge at 120 seconds, then start a new high-strength run at 121 seconds with checks no more than 30 seconds apart and complete it at 240/241 seconds | A second nudge is suppressed before 120 seconds have elapsed since the previous nudge and allowed at the 120-second cooldown boundary. | xUnit unit test |
| FOCUS-007 | Create a normal session and a developer-mode session; advance each by one real check interval | Normal mode reports a 30-second check interval and 1:1 elapsed time; developer mode reports a 10-second check interval, advances the focus clock at 4x, and preserves real active elapsed time separately. | xUnit unit test |
| FOCUS-008 | Construct sessions below 60 seconds and above 14,400 seconds | Duration is clamped to the inclusive range 60–14,400 seconds. | xUnit unit test |
| FOCUS-009 | Pause with a reason, attempt to advance, explicitly resume, advance, then end and attempt resume | Pausing stops monitoring and time advancement; only explicit resume restarts a paused session; ending stops monitoring permanently and cannot be resumed. | xUnit unit test |
| FOCUS-010 | Add an on-task observation, an uncertain observation, and pause a session | The snapshot preserves the observations and represents a pause as a paused check; it does not invent an exact audit interval. | xUnit unit test |

The native lock/sleep/unlock adapter is outside this public core seam. Its
acceptance case remains: lock, sleep, disconnect, pause, or end must stop
monitoring, and unlock/reconnect alone must not call the explicit `Resume` API.
That behavior requires a native lifecycle test against the adapter and is not
claimed by these pure unit tests.

## Storage cases

| Case | Trigger | Expected observable outcome | Evidence |
| --- | --- | --- | --- |
| STORAGE-001 | Save a completed snapshot with observations to a temporary SQLite database, create a new `SessionStore` for the same path, and list | The snapshot survives restart with identity, timing, status, developer flags, and observation values intact. | xUnit integration test |
| STORAGE-002 | Save a snapshot, save a changed snapshot with the same ID, then list | The existing record is updated rather than duplicated. | xUnit integration test |
| STORAGE-003 | Save two snapshots, delete one ID, then list | Only the requested history entry is removed; the other remains unchanged. | xUnit integration test |
| STORAGE-004 | Save `AppSettings` containing display, speech/chirp, motion, offset, microphone, file-root, and provider values; reopen `SettingsStore` | All settings fields, including nested provider settings, round-trip without raw capture/audio data. | xUnit integration test |
| STORAGE-005 | Save a synthetic credential string, inspect the on-disk credential file, reopen and load | The value round-trips through the public API; the plaintext synthetic value is absent from the stored bytes (Windows DPAPI at-rest protection). | xUnit integration test |
| STORAGE-006 | Delete a saved credential and load again | No credential is returned after deletion. | xUnit integration test |
| STORAGE-007 | Replace the credential file with malformed bytes and call `Load` | Corruption is handled as a safe missing/invalid credential result, without leaking the malformed value or crashing the caller. This case is enabled only if the root freezes that recovery contract. | Pending contract decision |

## Explicit non-claims

These cases do not prove live provider access, screen/audio capture, Windows
lock/sleep notifications, full application history UI, or credential validity
with a real provider. Raw frames and microphone audio are never supplied to
the storage tests. Test paths are disposable temporary files and use only
synthetic credentials.
