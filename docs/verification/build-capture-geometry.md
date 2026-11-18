# Capture, target, and geometry build receipt

Date: 2026-11-07  
Worktree: `%USERPROFILE%\.codex\worktrees\boo-capture-geometry\blue--fo`  
Base ref: `b2b515bf7023ac665a2c4f3d159452a6df5c0389`  
SDK: `.dotnet/dotnet.exe`, pinned by `global.json` to .NET `10.0.403`

## Scope delivered

- `Boo.Core.AnnotationValidator` now rejects stale, future, mismatched,
  empty-target, non-finite, out-of-frame, unknown-kind, and empty-mark
  proposals. It maps frame coordinates to physical display pixels using the
  captured frame-to-display ratios on each axis, so a downsampled WGC frame
  does not apply DPI twice. `DisplayGeometry.NotchBounds` converts DIP sizes
  with the selected display scale and returns physical pixels.
- `Boo.Windows.TargetObserver` enumerates Win32 monitors with effective DPI,
  installs a foreground WinEvent hook, seeds the current external foreground
  window, retains only non-Boo windows, and revalidates HWND, process identity,
  process name, physical bounds, selected display, foreground ownership, and
  protected/cloaked state. A Boo-owned foreground window is allowed to leave a
  retained external target current while the chat is open; an external switch
  invalidates it.
- `Boo.Windows.ScreenCapture` uses an explicit selected-monitor
  `GraphicsCaptureItem`, a free-threaded `Direct3D11CaptureFramePool`, one
  frame per call, Direct3D11 CPU readback, and in-memory WPF JPEG encoding.
  Cancellation unregisters the frame handler and all WGC/D3D resources are
  disposed. Every supplied Boo HWND must successfully accept
  `WDA_EXCLUDEFROMCAPTURE`; any failure raises `CaptureLimitationException`
  and no capture begins. There is no BitBlt fallback and no capture file.

The implementation follows Microsoft's desktop interop shape for
`IGraphicsCaptureItemInterop`, `Direct3D11CaptureFramePool`, and the
`CreateDirect3D11DeviceFromDXGIDevice` bridge:

- <https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/>
- <https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture>
- <https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/tree/master/dotnet/WPF/ScreenCapture>

No project manifest or package edit was needed: `Boo.Windows` already targets
`net10.0-windows10.0.19041.0`, enables WPF/WinForms and unsafe interop, and the
existing Boo application manifest declares PerMonitorV2 awareness.

## Test-first evidence

The pre-implementation focused run used the canonical wrapper:

```powershell
./scripts/build.ps1 test tests/Boo.Core.Tests -Filter 'FullyQualifiedName~AnnotationTests'
```

It discovered 17 tests and failed all 17 through the original
`NotImplementedException` stubs. The independent revision-2 geometry
regression run then reproduced the intended RED signal: 2 failed, 0 passed
because the original implementation incorrectly multiplied by DPI.

After the ratio repair, the frozen geometry/annotation set passed:

```powershell
./scripts/build.ps1 test tests/Boo.Core.Tests -Filter 'FullyQualifiedName~AnnotationTests|FullyQualifiedName~GeometryRegressionTests'
```

Result: 19 passed, 0 failed.

The oracle and compilation checks also passed:

```powershell
python scripts/test_oracle.py
./scripts/build.ps1 build src/Boo.Windows/Boo.Windows.csproj
./scripts/build.ps1 build Boo.sln
```

Results: `ORACLE_VERIFIED revision 2 22 files`; Boo.Windows build succeeded
with 0 warnings/0 errors; solution build succeeded with 0 warnings/0 errors.
The final Release solution build also succeeded with 0 warnings/0 errors, and
the focused suite was rerun afterward with the same 19/19 result.

## WinRT bridge smoke evidence

After the Release solution build, a narrow PowerShell reflection probe loaded
the packaged app output (which carries the Windows SDK projection assemblies)
and exercised only the ABI bridges:

The pre-fix raw `Marshal.GetObjectForIUnknown(... )` item cast reproduced the
host failure (`System.__ComObject` could not be cast to
`GraphicsCaptureItem`). The implementation now uses
`WinRT.MarshalInterface<T>.FromAbi`, and the final probe produced:

- `NativeCaptureDevice.Create()` returned an object satisfying the projected
  `Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice` interface; it was then
  disposed immediately.
- `CaptureInterop.CreateItemForMonitor()` returned a projected
  `Windows.Graphics.Capture.GraphicsCaptureItem` with a real display name and
  size (`Display 1`, `2880x1800` on this host).

The probe did not start a capture session, acquire a frame, upload a frame,
run desktop automation, or persist private screen data. This confirms the
modern C#/WinRT `MarshalInterface<T>.FromAbi` conversions, not native capture
acceptance.

## Native proof gaps

This lane did not run private-desktop capture, UI automation, or cloud/live
provider calls. Consequently the following remain unproven and must be run by
the root/native QA owner against the exact packaged candidate and fixture:

- actual WGC monitor capture and JPEG frame content;
- successful/failed `WDA_EXCLUDEFROMCAPTURE` behavior and recursive-overlay
  exclusion;
- foreground-hook behavior across Boo activation, target closure/reuse,
  protected windows, display moves, and external foreground switches;
- real multi-monitor physical origins and 100/150/200% DPI behavior;
- lock/sleep/disconnect cancellation and packaged-app lifecycle evidence.

The 19 deterministic tests and successful compilation are not native runtime
acceptance, release/package proof, or evidence of live provider correctness.

## Canonical integration

Root integrated the six owned source files and this receipt after reviewing
the actual diff and the modern WinRT ABI correction. The complete canonical
core suite passed 62 cases with one explicit host skip, recorded at
`2026-11-07_10_22_57_net10.0.trx`. The canonical Windows project built with zero
warnings/errors, and oracle revision 2 verified all 22 files. The native proof
gaps above remain open.
