# Observed controls in requested tasks

The Core snapshot, provider handoff, Windows reader and WPF integration are
implemented locally. Native acceptance is pending on a package of this source.
The full edit-and-Undo demo is not complete.

## Independent tests and integration

The independently authored Core suite recorded 24 RED failures before its
builder started. The independent provider suite subsequently recorded 10
behavioral RED failures and one compatibility pass after Core was working,
before its provider builder started. Both suites are frozen in oracle
revision 13; all prior frozen hashes are unchanged.

Root integrated the builders' actual source files, inspected their changes,
and independently reran the complete Release suite after cleanup:

- Core: 115 passed, one unchanged host-dependent reparse-fixture skip.
- Integration: 88 passed, no failures.
- Release solution build: zero warnings and errors.
- Oracle: revision 13, 36 files verified unchanged.

Canonical TRX files:
`TestResults/vikasmit_VIKASMIT_2026-11-07_14_28_14_net10.0.trx` and
`TestResults/vikasmit_VIKASMIT_2026-11-07_14_28_16_net10.0.trx`.

The project-wide format check still exits 2 on untouched Core whitespace,
including frozen Contracts.cs. See `artifacts/format-control-observation.log`.
This is a recorded failed check, not a formatting pass. Frozen files were not
rewritten to clear it.

## Native wiring and review

Explicit typed tasks and authorized realtime task screen-help calls pass actual
observed controls to Responses. Ordinary teaching, research, focus checks and
the realtime text-send path do not add control-reading work. Cancellation,
lease, reader/source identity, display, context and retained-target checks run
before the subsequent provider request. Reader failure supplies an empty
observation with a visible limitation; it cannot enable guessed UIA typing.
Pending observations follow preview/queue lifetime and are checked against the
approval's generation and selected window.

Root review corrected password/off-screen filtering before pattern/value access,
null-name coercion and navigation/root-loss handling before native integration.
Fresh reviewers then inspected Core/provider and native/WPF paths independently
and found no remaining material issue within this bounded plan. A final check
after cleanup likewise found no new issue. These are source reviews, not native
privacy or usability proof.

Cached scalar property reads use the element cache; actual provider calls retain
cancellation checks. This distinction was checked against
[Microsoft's API contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.automationelement.getcachedpropertyvalue?view=windowsdesktop-10.0)
and the [WPF implementation](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/UIAutomation/UIAutomationClient/System/Windows/Automation/AutomationElement.cs).

## Required work still open

Run the prewritten native typing case from `observed-control-work.md` against
the exact package, inspect the immutable proposal and actual field readback,
then restore the disposable fixture. Password/off-screen no-value-read behavior
has source review but no direct native test result yet.

Fresh control identity validation at commit, verified click postconditions,
same-connection voice authority, general target lifetime and native operation
bounds remain required. In particular, each observation starts an MTA worker;
repeated requests against a blocked UIA provider can accumulate workers. A
separate test-first lifetime change must bound that concurrency without detaching
unfinished native work or pretending a provider call was interrupted.
