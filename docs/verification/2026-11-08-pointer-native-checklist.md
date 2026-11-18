# Pointer lane native checklist (U3 pointer + drawing, U6 nudge pointer)

Status: NOT RUN. Windows-only. Everything below needs a real Windows 10/11 PC; nothing here was verified in the Linux build container. Record each result as Pass / Fail / Not run with the date, build number and a screenshot or short screen recording.

Core behaviour is frozen in `tests/Boo.Core.Tests/PointerFlightPlannerTests.cs`, `PointerAnnotationSequencerTests.cs` and `OcrNudgeTargetMatcherTests.cs`. This checklist only covers what those tests cannot see: real cursor position, real DPI, real monitors, real OCR, and the overlay on screen.

## Setup

- Packaged Boo build under test (write the build number here: ______).
- Fixture app: `tests/fixtures/native/Boo.QaWorkspace` (target button `TargetButton`, AutomationId `design-button`). Build and run it and keep it in the foreground.
- Displays: primary at 100% scaling. If you have a second monitor, set it to 150% and place it to the LEFT of the primary so it has a negative origin. If you have only one monitor, run the 150% rows by changing the primary's scaling and signing out/in if Windows asks.
- Turn on Settings > Developer Mode in Boo for the focus rows (10 s checks).
- Keep the Boo logs folder open so you can attach the pointer/annotation lines on failure.

## AE1 "Show me why this button is hard to find"

| # | Step | Expected | 100% | 150% | 2nd monitor |
|---|------|----------|------|------|-------------|
| P1 | Put the mouse cursor in the far corner from the button. Hold to talk, say "Show me why this button is hard to find." | Boo's blue pointer appears at the cursor position (or at the nearest edge of the fixture's monitor if the cursor is on another monitor) and flies to the button in about 0.3 to 0.6 s with a smooth ease in and out. | | | |
| P2 | Watch where it lands. | The pointer tip lands on the centre of `TargetButton`, not offset by the scaling factor (no 1.25x / 1.5x drift, no landing on the wrong monitor). | | | |
| P3 | Watch after landing. | Only after the pointer has landed, a ring is drawn around the button over about 0.62 s (stroke animation, not a pop-in). An arrow or label, if the model asked for one, also appears after landing. | | | |
| P4 | Wait while Boo explains. | Ring and pointer hold while Boo speaks, then clear by themselves. The button is still clickable through the ring (click-through overlay). | | | |
| P5 | Move the fixture window so the button is half off screen, ask again. | Pointer lands on the visible half of the button. If the button is fully off screen, Boo explains in words and draws nothing (no guessed mark, R3). | | | |
| P6 | Ask about a very wide target, e.g. "point at the title bar". | Pointer lands inside the bar near the side the cursor came from, not on a far-away centre. | | | |

## Marks clear on change

Start P1 again each time and do the action while the ring is showing (and once while the pointer is still flying).

| # | Action | Expected | Pass/Fail |
|---|--------|----------|-----------|
| C1 | Scroll the fixture's workspace with the mouse wheel. | Pointer and ring disappear immediately. | |
| C2 | Resize the fixture window. | Pointer and ring disappear immediately. | |
| C3 | Alt+Tab to another app (foreground window change). | Pointer and ring disappear immediately. | |
| C4 | Change display scaling or unplug/replug the second monitor. | Pointer and ring disappear; nothing is drawn on the wrong monitor afterwards. | |
| C5 | Press Stop (or Esc / the notch stop control) while the pointer is mid-flight. | The pointer vanishes at once; no ring is drawn afterwards, even a second later. | |
| C6 | Ask a second question while the first pointer is still flying. | The first pointer disappears; only the second answer's pointer flies and draws. A late reply to the first question never draws anything. | |

## U6 nudge pointer (Windows OCR)

| # | Step | Expected | Pass/Fail |
|---|------|----------|-----------|
| N1 | Start a focus session ("Write the report", Developer Mode on). Open a browser with a distracting tab, e.g. a YouTube tab, and keep it in front until a nudge fires. | The notch nudge bubble appears (caption types in, eyes shake 2x, chirp). | |
| N2 | Watch the pointer. | The blue pointer flies to the distracting tab's title text found by Windows OCR (Windows.Media.Ocr) and a ring is drawn around that text after landing. | |
| N3 | Check it did not point at Boo itself. | The pointer never targets the word inside Boo's own nudge bubble or the notch, even though the bubble contains the same word. | |
| N4 | Use a page where the model's phrase is not on screen (e.g. switch to a tab whose title does not match). | Nudge text only; no pointer and no ring. | |
| N5 | Repeat N1 at 150% scaling and with the browser on the second monitor. | Pointer lands on the tab text on the correct monitor. | |
| N6 | Scroll, Alt+Tab or press Stop during the nudge pointer. | Pointer and ring clear immediately. | |
| N7 | Check the screen capture of the next focus check (history timeline thumbnail, if shown). | The pointer/ring overlay is excluded from Boo's own captures. | |

## Notes for the tester

- A Pass needs a screenshot or recording at the moment of landing for P2, P3 and N2.
- If the pointer lands offset by exactly the scaling factor, attach the display list from the Boo log; that is a DPI-mapping defect, not a model mistake.
- Do not mark anything Pass from a build that only compiled in CI.
