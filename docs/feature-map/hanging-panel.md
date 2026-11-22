Section: Notch and presence
Replaces: N2

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| hanging-panel | **The hanging panel** (round 3, 22 Nov 2026, HeyClicky structure, Boo's eyes): Ctrl+Alt+B, the tray, or a click on the eyes opens one dark 420 × 330 sheet that hangs centred under the notch with exactly three views, Home (focus session card, session history), Chat (bubbles, Review-action and Sources cards, Talk, Ask Boo, "Open in a window") and Settings (640 × 520, sidebar + grouped page). Only the open view's controls are in the UI Automation tree; Ctrl+1/2/3 and F6 switch views, Esc and Close hide the panel, "Open in a window" makes it a movable, resizable 760 × 520 window. The old full-size window is gone. | automated live | `tests/Boo.Ui.Tests/BooSmokeTests.cs::OpenShortcut_CtrlAltB_OpensCompanionWindow` (ui smoke: open, Close, reopen); `tools/Boo.VerifyLive/LiveJourneyTests.cs::J0_NotchWakes_OnHotkey` (live journeys); the smoke steps that read Settings, Chat and Focus controls now reach them through `BooAppFixture.FindInView` (reviewed oracle revision in the same PR) |
