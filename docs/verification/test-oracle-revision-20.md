# Test-oracle revision 20: reviewed rebaseline (2026-11-12)

Branch `fix/live-smoke-off-desktop`. Reason: Vikas's decision of 12 Nov 2026, 10:02 PM IST: the live AI smoke must
never run on his PC, and test screenshots must be of Boo's own windows, never the whole desktop.

## Changed frozen files (exactly 3, all test **harness** files, no test file)

| File | What changed | Decision |
| --- | --- | --- |
| `tests/Boo.Demo.Tests/BooAppFixture.cs` | `CaptureScreen` takes the whole desktop only when `FullDesktopCaptureAllowed()` is true: `RUNNER_ENVIRONMENT=github-hosted` and `BOO_CAPTURE_SCOPE` is not `boo-windows`. Otherwise new `CaptureBooWindows` saves each visible top-level Boo window (`name.png`, `name-1.png`, ...), or writes `name.no-window.txt` if none is visible. | ACCEPT |
| `tests/Boo.Ui.Tests/BooAppFixture.cs` | Same change, same code. | ACCEPT |
| `tests/Boo.Perf.Tests/PerfAppFixture.cs` | Same change (keeps its `using var image` disposal). | ACCEPT |

## Why this does not weaken any test

- No `[Fact]`/`[Theory]`, assertion, timeout or `Skip` changed. Test attribute counts are unchanged.
- In CI (`windows-ci`, `demo-ci`, both on `windows-latest`, so `RUNNER_ENVIRONMENT=github-hosted`) and without
  `BOO_CAPTURE_SCOPE`, the code path is exactly the old one: `Capture.Screen()`.
- `CaptureScreen` was already evidence only: every caller ignores the image, and the method already swallowed
  capture errors into `.error.txt`. No test reads pixels from a `CaptureScreen` image. Tests that check pixels use
  `CaptureRectangle` (unchanged) on a rectangle around a Boo element.
- Behaviour changes only off GitHub's VMs (for example a local run on the owner's PC) and in the live smoke, which sets
  `BOO_CAPTURE_SCOPE=boo-windows`: those now get Boo's windows instead of the whole desktop.

## Known limit

`Capture.Element` copies the screen pixels inside a Boo window's rectangle. If another window overlapped Boo at that
moment, its pixels inside that rectangle would appear. `CaptureRectangle` is unchanged and can still capture any
rectangle a test names (today always one around a Boo element). Both are far narrower than the whole desktop.

Added to the oracle: none. Removed: none.
