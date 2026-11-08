# Windows control-observation build evidence

Date: 2026-11-07

Base: `6d38e9d1388622be062419044d805a02788457e0`

Owned files:

- `src/Boo.Windows/ControlObservationReader.cs`
- `docs/verification/control-windows-build.md`

## Implemented boundary

`ControlObservationReader` reads only the independently retained target from
`TargetObserver`. It rechecks `TargetObserver.IsCurrent` before creating the
UI Automation element and after the bounded walk. UI Automation runs on a
dedicated, windowless MTA thread; the returned data is copied into Core
candidates and no `AutomationElement` crosses the worker boundary.

The reader uses `AutomationElement.FromHandle` for the retained target, a
ControlView `TreeWalker`, and a cache request for ordinary properties. The walk
counts the root and every returned child/sibling, including elements later
omitted as invalid, and never visits more than 512 elements. Cancellation is
checked between native calls. Password and off-screen state are checked before
any `ValuePattern.Value` read. Invoke and value capabilities come from the
actual patterns; a read-only value pattern can provide an observed value but
cannot set it. Runtime IDs are copied as opaque comparison data.

Target loss, navigation failure, and UI Automation API unavailability return
the fixed `CaptureLimitationException` message without provider text. A single
disappearing element may be omitted. The worker is awaited through task
completion without a timeout or detached cleanup. A provider call that blocks
inside Windows remains an explicit native-lifetime limitation.

Review corrections applied before this receipt: `IsEnabled == true`,
`IsOffscreen == false`, and `IsPassword == false` are required before any
pattern or value access; unknown state fails closed. A root element loss now
returns the fixed unavailable error, while an individual child disappearance
may be omitted. All other native navigation failures return the fixed error;
they are not silently skipped. Pattern-availability cache entries were removed
because capabilities are determined by the actual patterns. The fixed
`CaptureLimitationException` is an `InvalidOperationException`, preserving the
existing WPF fallback classification. An unavailable name is omitted while a
genuine empty name remains preserved.

## Verification

The requested scoped Release build passed with zero warnings and errors:

```powershell
.\scripts\build.ps1 -Action build -Project src/Boo.Windows/Boo.Windows.csproj -Configuration Release
```

The frozen oracle and whitespace checks were run after the source change:

```powershell
python scripts/test_oracle.py
git diff --check
```

No Windows reader test was added in this lane: the approved handoff assigns
Core and provider tests to their independent authors, forbids test/oracle and
Contracts edits here, and reserves native GUI execution for the later Sky QA
lane. The intentional Core `ControlObservation.Create` throwing scaffold is
therefore a known integration prerequisite, not a Windows-reader build
failure.

## Native case and release ownership

The native typing case was specified in
`docs/verification/observed-control-work.md` before this implementation and
was not run here. It remains pending on the exact packaged candidate and the
existing Sky driver; no live API, credentials, desktop automation, or private
application evidence is claimed.

The root agent owns integration, Core/provider readiness, WPF wiring, complete
Release verification, source review, packaging, and native acceptance. This
lane owns only the Windows reader source and this evidence note.

Primary API references reviewed:

- [Microsoft UI Automation threading guidance](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading)
- [TreeWalker.GetFirstChild cache overload](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.treewalker.getfirstchild?view=windowsdesktop-10.0)
- [AutomationElement.GetRuntimeId](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.automationelement.getruntimeid?view=windowsdesktop-10.0)
