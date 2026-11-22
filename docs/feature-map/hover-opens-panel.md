Section: Notch and presence

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| hover-opens-panel | **Hover opens the panel** (round 3): resting the pointer on the notch for 350 ms drops the panel without taking the keyboard from the app in front; when the pointer has been off both the notch and the panel for 400 ms the panel folds away and the notch retracts as before. A panel opened on purpose (hotkey, tray, click) stays up. A click elsewhere closes a hover-opened panel. | automated live | `tools/Boo.VerifyLive/LiveJourneyTests.cs::J7_HoverOpensThePanel_AndItFoldsWhenThePointerLeaves` (live journeys); `tests/Boo.Ui.Tests/BooSmokeTests.U8.cs::Notch_HoverStrip_RevealsAndRetracts` (ui smoke: the notch still rests in Hidden after the pointer leaves, with the hover-opened panel folded) |
