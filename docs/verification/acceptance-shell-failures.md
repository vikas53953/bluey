# Shell failure recovery acceptance checklist

This checklist is prewritten before the shell repair. It is a native acceptance
contract, not a claim that these cases have been exercised. The cases require a
real supported Windows observation against the exact packaged candidate; unit or
source-text checks cannot certify WPF window state, monitor routing, capture
disclosure, or the supported input driver.

## Evidence boundary

For every executed case, record all of the following before calling it a pass:

- Candidate identity: package name, candidate number, absolute executable path,
  file SHA-256, and the source/main commit SHA.
- Environment: Windows version, selected monitor IDs, scale, and the supported
  native driver and version used for observation.
- Raw evidence: timestamped screenshots or accessibility observations before,
  during, and after the fault, plus the relevant application log/error text.
- Cleanup and recovery: confirm the fixture/database/monitor state was restored
  and that no automatic resume, stale capture, or stale display metadata
  remained.

If the supported driver cannot trigger or observe a case, mark it **UNVERIFIED**
with the driver limitation. Do not disable capture protection, use an alternate
GUI automation path, or infer native acceptance from a build/test result.

## Native fault cases

| ID | Fault and trigger | Required observable outcome | Status |
| --- | --- | --- | --- |
| SHELL-001 | Start with a corrupt optional `sessions.db`, then open the launcher and full window. | Both remain usable; history is empty or unavailable with one fixed bounded readable history error; the database is not reset, deleted, or silently replaced. | UNVERIFIED — prewritten; not executed in this test-author lane |
| SHELL-002 | Start with a newer-schema optional `sessions.db` that the build cannot read. | Launcher and full window remain usable; the same bounded history error is shown; original database bytes remain unchanged and no downgrade/reset occurs. | UNVERIFIED — prewritten; not executed in this test-author lane |
| SHELL-003 | Make the optional `sessions.db` unreadable through a supported native file-state fixture, then open the launcher and full window. | Startup does not crash or hang; history is unavailable with a bounded error, and the database is not deleted or rewritten. | UNVERIFIED — prewritten; not executed in this test-author lane |
| SHELL-004 | While capture, voice, or a pending action is active, remove the selected monitor. | Capture, voice, and pending actions stop before display rebuilding; the notch moves to a live primary fallback, synchronizes the selected display/indicator, and does not auto-resume or retain missing-display geometry. | UNVERIFIED — prewritten; not executed in this test-author lane |
| SHELL-005 | Trigger a hotkey registration or capture-exclusion failure, then hover/reveal and open the full experience. | A bounded shell-status error is visible in the revealed/opened experience; the idle notch remains retracted and does not expose the error as an always-visible body. | UNVERIFIED — prewritten; not executed in this test-author lane |
| SHELL-006 | Cause a shell-status/registration error while capture or microphone disclosure is active. | The active capture or microphone disclosure takes precedence over the shell-status fallback until the active work stops; the error remains available after stop without stale disclosure. | UNVERIFIED — prewritten; not executed in this test-author lane |
| SHELL-007 | Cause history refresh/save to fail while Stop is unwinding active work. | Stop completes without throwing; capture/voice/disclosure state still clears, and the bounded history error is reported without a second exception or database reset. | UNVERIFIED — prewritten; not executed in this test-author lane |

These native cases remain open until the root runs them on a uniquely identified
packaged candidate and records direct supported-driver evidence. The executable
storage tests in `AppearanceReadFailureTests` cover only public temporary-path
behavior and do not establish this native acceptance.
