# Sprint 1 MVP: Vikas's own test of the notch status words on his PC

Plain-English script, 22 Nov 2026. Build under test: the installer from the `package` job on the head of the notch
status words PR (download `boo-installer-win-x64` from the run linked in the PR's evidence table). Boo must be the only
copy running; quit any older Boo from the tray first. Tick each line; a line you cannot tick is a bug, not a maybe.

Expect about 5 minutes. Keep Notepad open before you start.

| # | Do | You should see | OK? |
| --- | --- | --- | --- |
| 1 | Start Boo. Hover the top strip, then move away. | Only the eyes, no words. The eyes go back to sleep. | |
| 2 | Press Ctrl+Alt+B. On Home type task "Read the notch clock", minutes 5, leave Developer Mode off, click Start now. | The card shows Running and a clock. Beside the eyes on the notch: nothing yet (the panel is open). | |
| 3 | Click the X to close the panel. | The eyes stay out and the time left appears right of them, for example `04:52`, counting down once a second. The ink widens a little to make room; it does not wobble as the digits change. | |
| 4 | Type a line in Notepad. | The letters land in Notepad; the clock on the notch keeps ticking and never takes the keyboard. | |
| 5 | Rest the pointer on the eyes until the panel opens. | The time beside the eyes disappears the moment the panel is up; the Home card shows the same clock. | |
| 6 | Click Pause on the card, then close the panel. | No time beside the eyes while paused (the eyes look sleepy). Reopen, click Resume, close: the time is back. | |
| 7 | Open the panel, go to Chat, ask "What is the capital of Australia?" and watch the eyes. | "Thinking…" beside the eyes while the answer runs; gone when the answer is in. | |
| 8 | In Chat, press and hold Talk (or Ctrl+Alt+Space) for two seconds, then let go. | "Listening" beside the eyes while you hold; gone when you let go. | |
| 9 | Home: click End. | The happy face for a couple of seconds, then the eyes go back to sleep with no words. | |
| 10 | Right-click the tray icon, Quit. | Boo closes. | |

## When something fails

Write the line number and what you saw instead (a phone photo of the notch is enough). The notch's word is readable by
screen readers as the `NotchStatusWord` text, so "the word is there but unreadable" is also a bug.
