# Test-oracle revision 20: reviewed rebaseline (2026-11-12)

Branch `fix/live-smoke-off-desktop` (PR #18). Reason: Vikas's decision of 12 Nov 2026, 10:02 PM IST: the live AI smoke
must never run on his PC, and test screenshots must be of Boo's own windows, never the whole desktop.

## Changed frozen files (exactly 3, all test **harness** files, no test file)

`tests/Boo.Demo.Tests/BooAppFixture.cs`, `tests/Boo.Ui.Tests/BooAppFixture.cs`, `tests/Boo.Perf.Tests/PerfAppFixture.cs`
get the same change:

| Method | Before | After |
| --- | --- | --- |
| `CaptureScreen(name)` | always `Capture.Screen()` (the whole desktop) | Whole desktop only when `FullDesktopCaptureAllowed()`: `RUNNER_ENVIRONMENT=github-hosted` **and** `BOO_CAPTURE_SCOPE` empty. Otherwise `CaptureBooWindows`: each visible top-level Boo window is drawn from **its own content** with `PrintWindow(PW_RENDERFULLCONTENT)` (not copied from the screen) to `name.boo-0.png`, `name.boo-1.png`, ..., plus `name.capture.txt` listing what was captured. **Nothing is written at `name.png`** in that mode. |
| `CaptureRectangle(name, rect)` | `Capture.Rectangle(rect)` anywhere | Throws unless `RUNNER_ENVIRONMENT=github-hosted`: a rectangle copies whatever is on the real screen there. |

Decision: ACCEPT.

## Why this does not weaken any test

- No `[Fact]`/`[Theory]`, assertion, timeout or `Skip` changed. Test attribute counts are unchanged.
- In CI (`windows-ci`, `demo-ci` on `windows-latest`, so `RUNNER_ENVIRONMENT=github-hosted`, and no
  `BOO_CAPTURE_SCOPE`) both methods run exactly the old code (`Capture.Screen()`, `Capture.Rectangle(rect)`).
- **One test reads pixels from a `CaptureScreen` file:** `DemoTests` step 1 passes the `01-explain-ring-fullscreen`
  path to `ProbeRing`, which reads it by screen coordinates. On CI it is unchanged. Off CI no file is written at that
  path, so the probe fails honestly instead of reading a crop with the wrong origin. Pixel tests that use
  `CaptureRectangle` (Demo nudge/happy, Ui notch box, Perf notch bounds) now fail off CI with a plain message. That is
  intended: the UI, demo and perf suites run only on GitHub's VMs (docs/DECISIONS.md, "Never capture the owner's real
  screen").
- The live smoke (`tools/Boo.LiveSmoke`) sets `BOO_CAPTURE_SCOPE=boo-windows` itself and never uses
  `CaptureRectangle`, so all its evidence comes from `CaptureBooWindows` on any machine.

## Review history

The first version of this revision (commit `8112020`) captured Boo windows with `Capture.Element`, which copies
screen pixels inside the window's rectangle. The independent review found that Boo's transparent, full-screen
"screen explanation" overlay (`src/Boo.App/AnnotationWindow.xaml.cs`) is a top-level Boo window, so that capture would
have been a whole-display picture. It also found the note's claim "no test reads pixels from CaptureScreen" was false
(see above). Both are fixed here: `PrintWindow` draws only the window's own content, so the overlay shows only its
marks, and the claim is corrected. The workflow `live-ai-smoke.yml` fails the run if any evidence PNG did not come
from this renderer (`*.boo-<n>.png`).

A second fresh review (head `fa630a8`) passed the capture fixes and found a bypass in `scripts/ci/check_runners.py`
(a quoted key with an escape sequence that YAML reads as `runs-on`), fixed in the next commit by checking each job on its own with plain-word keys only.
It also asked for `BOO_CAPTURE_SCOPE` to be honoured when it holds only spaces (`IsNullOrEmpty`, not
`IsNullOrWhiteSpace`), done here.

**Now tested, not only reasoned:** `tools/Boo.CaptureProof` (run by `demo-ci` after the demos) opens the overlay over a
full-screen magenta decoy window and fails if the window-only screenshots miss the overlay or contain a single decoy
pixel; a copied screen corner first proves the decoy was visible. Before that test existed: in the live runs so far only the notch and the main panel were open at capture time. That
`PrintWindow` draws the transparent explanation overlay without the desktop beneath is reasoned from how Windows
composes layered windows (the window's own surface never contains other windows), not yet seen in a run. The workflow
step fails blank, unreadable or non-renderer images, but it cannot tell desktop pixels inside a render, so this point
stays reasoned until someone looks at an overlay capture from a GitHub VM run.

Added to the oracle: none. Removed: none.
