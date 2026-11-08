# Shell failure recovery builder receipt

Date: 2026-11-07  
Worktree: `C:\Users\vikasmit\.codex\worktrees\boo-shell-recovery-build\blue--fo`  
Base: `e7047727fbdd59c854b375792797559a213db3cd`

## Scope delivered

The bounded shell repair changes only `src/Boo.App/MainWindow.xaml.cs` and
`src/Boo.App/NotchWindow.xaml.cs`:

- history storage is initialized separately from settings and fails closed on
  the targeted data/I/O/access/SQLite failures; history refresh and focus-save
  failures leave an empty list with one fixed readable message and never reset,
  delete, or recreate the database;
- a removed selected monitor stops active work before rebuilding the live
  display list, selects the live primary fallback, synchronizes settings and
  the selector/indicator, and notifies the notch; persistence is best effort;
- display refresh and notch positioning have bounded re-entry guards for
  synchronous display/DPI callbacks and stop-triggered indicator updates;
- the notch discards stale display metadata when the requested monitor is no
  longer live, rebuilds bounds from the live screen, and then uses the existing
  actual-window-DPI correction;
- shell-status text is shown only in a revealed notch with no active capture or
  microphone caption, and the measured disclosure height includes the text.

No tests, contracts, oracle/provider/action files, credentials, or commits were
changed. The native shell-fault checklist remains unverified; source/build
evidence does not certify native WPF, monitor, disclosure, or driver behavior.

## Verification

| Check | Result |
| --- | --- |
| `./scripts/build.ps1 -Action build -Configuration Release` | PASS; zero warnings/errors |
| `python scripts/test_oracle.py` | PASS; `ORACLE_VERIFIED revision 16 43 files` |
| `git diff --check` | PASS |

The default `scripts/build.ps1` test action was also attempted. It stopped at
the independent storage-lane `AppearanceReadFailureTests` directory-path case
(98 passed, 1 failed, 0 shell failures observed); that failure is outside this
two-file shell unit and is not relabeled as a shell pass.

## Root-directed follow-up correction

The bounded history message is exactly `Focus history is unavailable.`.
`LoadHistory` also treats corrupt/out-of-range `ArgumentException` and
`OverflowException` values from history rendering as unavailable history. The
constructor and save filters remain limited to the originally targeted
data/I/O/access/SQLite failures.

After this correction, the explicit Release build again completed with zero
warnings/errors; oracle revision 16 and `git diff --check` remained clean.

