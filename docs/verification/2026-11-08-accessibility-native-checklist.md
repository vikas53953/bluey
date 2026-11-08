# Accessibility native checklist (8 Nov 2026, branch claude/project-thread-syqxnu)

What the accessibility pass built, what CI proves, and what still needs a human on a real Windows desktop.
Visual design is unchanged (face B, Nunito, dark cards, HeyClicky-style notch).

## Built

| Area | Change | Where |
|---|---|---|
| Screen-reader structure | Every card is a UIA **Group**: Screen sharing status, Ask Boo, Conversation, Review action, Focus, Session history, Settings | `Controls/BooSection.cs`, `MainWindow.xaml` |
| Headings | Boo = level 1; Ask Boo, Conversation, Review action, Focus, Settings = level 2; Session history, Appearance = level 3 | `MainWindow.xaml` (`AutomationProperties.HeadingLevel`) |
| Conversation | Transcript is a list (one Tab stop, Up/Down walk entries); each entry reads "Speaker: text" | `MainWindow.xaml` TranscriptList |
| Approval card | "Review action" text is an **assertive** live region; focus still lands on Cancel; Alt+A / Alt+C / Escape unchanged | `MainWindow.xaml` |
| Keyboard | **F6 / Shift+F6** cycle Ask -> Focus -> Settings; Tab order unchanged; Ctrl+Enter sends; Escape cancels or hides | `MainWindow.Accessibility.cs` |
| Focus visuals | 2 px accent ring on every focusable control, drawn only for keyboard focus; text boxes also tint their border | `App.xaml` BooFocusVisual |
| High contrast | All Boo brushes already map to SystemColors; the focus ring follows SystemColors.Highlight | `App.xaml.cs` ApplyThemeResources |
| Reduce animations | Windows "Animation effects" off, or Boo's Reduced motion, now also skips the pointer flight: marks appear at once | `AnnotationWindow.xaml.cs`, `Core/AccessibilityPreferences.cs` |
| Text scaling | Settings > Accessibility > Text size (100-225 %) scales the companion window content; min size grows but never past the work area; live updates via UISettings | `AccessibilitySettings.cs`, `MainWindow.Accessibility.cs` |
| Notch | HelpText names the keyboard routes (Ctrl+Alt+B / Ctrl+Alt+Space / Ctrl+Alt+S); nudge pills say where their keyboard equivalent is | `NotchWindow.xaml` |

## Proven in CI (windows-latest)

- UI smoke steps 11-14 (`tests/Boo.Ui.Tests/BooSmokeTests.Accessibility.cs`): groups and heading levels, Tab/F6/Shift+F6/Escape focus trail, transcript list with named items, no unnamed interactive control, notch HelpText.
- Core: `AccessibilityPreferencesTests` (23) for the text-scale clamp, scaled minimum and reduced-motion decision.

## Still needs a human (not provable on the CI desktop)

1. **Narrator / NVDA walk**: open with Ctrl+Alt+B, press H to jump headings (Boo, Ask Boo, Conversation, Focus, Settings), Narrator+F7 landmark list shows the groups. Send a question with no key; the transcript entry reads "You: ...".
2. **Approval card announcement**: type `change the text box to Final v2` (needs a key); Narrator should announce the Review action text without moving; focus lands on Cancel; Alt+A approves.
3. **High contrast** (Settings > Accessibility > Contrast themes, "Night sky"): panel, notch and disclosure chip follow the theme while Boo runs; focus ring is the theme highlight colour; switching back restores Boo's palette with no restart.
4. **Text size 150 % and 225 %**: panel content scales, both columns stay readable, no clipped buttons, window min size still fits a 1366x768 work area; changing the slider while Boo is open rescales live.
5. **Animation effects off**: notch reveal is instant, no nudge shake, and after "explain this screen" the pointer does not fly: ring/marks appear immediately and still clear on scroll/resize.
6. **Keyboard only**: unplug the mouse; F6 cycles regions; every control shows the accent ring; Escape hides; Ctrl+Alt+B returns; hold Ctrl+Alt+Space talks.

Known limits: the notch itself never takes keyboard focus (by design, it must not steal focus); its actions are reachable through the hotkeys and the companion window. Notch geometry does not follow text scaling (spec-driven), only the disclosure/caption text sizes are from the spec.
