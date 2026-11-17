Section: Notch and presence

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| stop-acknowledged-everywhere | **Every way to Stop shows "Stopped" on the notch**: the hotkey, a right-click on the notch, the tray menu's Stop and the panel's Stop button. The notch's UI Automation HelpText says "Stopped" within 1 s, then returns to its standing text | automated live (published Boo.exe on windows-latest, no key, no AI) | `tools/Boo.CrashReopen/StopAcknowledgmentTests.cs::EveryUserStop_ShowsStoppedOnTheNotch` (workflow `crash-reopen` on every PR); issue #103 |
