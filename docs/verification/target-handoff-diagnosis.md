# External-window handoff diagnosis

CE Debug is investigating the native candidate4 failure. Source revision:
`b76d6532893761161907d82d721363fee4ddc880`; exact package and observed values are
in `native-teaching-candidate4.md`. No tracker or remote is configured.

The request reached `MainWindow.CaptureForRequestAsync`, but
`TargetObserver.Snapshot` returned null before `ScreenCapture` ran. The visible
error was `No stable external window was selected. Bring the window forward,
then reopen Boo and retry.` The provider consequently received no image.

This rules out WGC/frame acquisition as the site of this particular failure;
native capture is not yet reached or accepted. The null result alone does not
distinguish a missing retained identity, a display mismatch, or changed HWND,
process, geometry, or protection state. The source uses Forms display inventory
in the shell and Win32 monitor inventory in the observer; equality is still an
unverified link. Foreground transitions and bounds are competing explanations.

The next diagnostic package adds a snapshot overload returning a readable
limitation and passes that reason to the existing capture-detail control.
It preserves all target selection and rejection conditions. Messages contain
fixed reasons and OS error codes, without titles, screen content, or secrets.
No screenshot or audio is stored. The existing native teaching reproduction is
the check for this manual Windows boundary; no new mock is counted as native
proof. Target selection is not claimed fixed at this checkpoint.

Pre-diagnostic state: feature branch `feat/boo-windows`, production tree clean;
only independent review/native receipt documents were untracked. The diagnostic
owns `TargetObserver.cs`, its MainWindow call site, and this receipt. The app
Release build passed with zero warnings/errors after the diagnostic change.

## Confirmed reopen/resize repair

Candidate5, after accessibility-verified input, reported that the selected
window moved/resized/changed identity. `App.OpenPanel` called
`PrimeExternalTarget` only inside the panel-creation branch. Foreground
observation retained the fixture bounds at activation; a later maximize did
not produce a new foreground event. Reopening the existing panel therefore
reused old bounds, which `TargetObserver.IsCurrentWindow` correctly rejected.

The narrow fix moves priming outside the creation branch, before settings,
Show, or Activate. Priming uses the already selected display when present;
the initial primary-display fallback remains unchanged. All identity and
protection checks remain. A fresh independent source review found no defect in
this narrow change and explicitly did not accept secondary-display startup.

Candidate6 repeated the original caller chain: open controls, Escape-close,
foreground fixture, change observed bounds from889x675 to1440x852 with the
observed Maximize control, reopen existing controls, verify exact input, submit
once. The old target rejection did not recur. The next error was a later native
capture `InvalidCastException` with HRESULT0x80004002. The handoff now reaches
the capture service; drawing and native capture are still not accepted.

This Windows/WinEvent caller chain has no existing non-GUI public test seam.
The prewritten native stale/fresh-target acceptance case and candidate5 RED to
candidate6 later-stage observation are the regression evidence; a fake is not
substituted for it. Full Release tests after the fix:120 passed, one unchanged
skip (`11_58_36` and `11_58_37` TRX receipts); full build zero warnings/errors;
oracle8 verified30 files. Candidate6 manifest binds base commit50636a5 plus
the exact source diff and42 source-file hashes, all rechecked matching before
the source commit. Its binary/package hashes remain unchanged.
