# Test-oracle revision 22: demo Step 4 state-sampling race (2026-11-13)

One frozen file changes; no file is added or removed. The independent PR reviewer re-checks this note against the
diff.

## Why

`DemoTests.Step4_Focus_NudgeThenBackToIt_EndsHappy` failed twice on docs-only commits while its twin runs passed
(runs [43240958545](https://github.com/vikas53953/bluey/actions/runs/43240958545) on `1e5c754` and
[43296734862](https://github.com/vikas53953/bluey/actions/runs/43296734862) on `3593ba3`), at
`Assert.Contains("Nudge", seen)` with `seen` ending in `Capture`.

Investigated and independently judged a **test race, not an app bug** (PR #16 QA comment):
- The focus check keeps the notch in `Capture` for its whole screenshot round trip, and `Capture` outranks `Nudge`.
  When the capture ends, the state flips to `Nudge`. On the CI VM the caption is shown at once, so the "Back to it"
  pills appear in the next frame (`src/Boo.App/NotchWindow.xaml.cs` ApplyState and the caption-done code).
- Inside the app, `_state` and the published `ItemStatus` change together on the UI thread, and a state change hides
  the pills in the same step. The pills are shown only while the state is `Nudge`, once the caption has finished.
- The test read `ItemStatus` (`Capture`), then made a **separate** UI Automation call that found the button after the
  flip, and ended the wait without reading the state again, so `Nudge` was never recorded. The screenshot taken right
  after shows Boo correctly in Nudge with the caption and both buttons.

## What changed

| File | Tests before → after | Asserts before → after | What changed | Decision |
|---|---|---|---|---|
| `tests/Boo.Demo.Tests/DemoTests.cs` | 5 → 5 | 20 → 20 | The nudge wait now records the state again **after** the button search and ends only when a fresh read says `Nudge`. The "Back to it" button is then waited for (up to the existing UI timeout) instead of assumed present. Both Step 4 loops record states through one helper, `RecordState`. | ACCEPT: same assertions (`Assert.Contains("Nudge", seen)`, `Assert.NotNull(back)`, `sawHappy`, `ended`); the wait can no longer finish on a stale state sample. |

No `[Fact]`/`[Theory]` removed, no `Skip =` added, and no assertion loosened. The test is strictly harder to pass by
accident: it used to accept the button alone, but now needs the published state to be `Nudge`.

## Revision 23 (same PR, after the frozen-test review)

The reviewer accepted revision 22 and noted one leftover race: the button wait ignored its result and looked the button
up a second time, which could fail falsely if the 6 s nudge ended between the two lookups. Revision 23 keeps the
element the wait found (`back = FindBack()` inside the wait). Tests 5 → 5, asserts 20 → 20, nothing loosened.
ACCEPT.
