# Sprint 1 MVP: Vikas's own test of the hanging panel on his PC

Plain-English script, 22 Nov 2026. Build under test: the installer from the `package` job on the head of PR #189
(download `boo-installer-win-x64` from the run linked in the PR's evidence table). Boo must be the only copy running;
quit any older Boo from the tray first. Tick each line; a line you cannot tick is a bug, not a maybe.

Expect about 15 minutes. Keep Notepad and one YouTube tab open before you start.

## 1. The panel opens four ways (hanging panel)

| # | Do | You should see | OK? |
| --- | --- | --- | --- |
| 1.1 | Start Boo. | Only the eyes at the top centre; no window. Hover the top strip: the eyes wake. | |
| 1.2 | Press Ctrl+Alt+B. | A dark panel about 420 x 330 hangs under the eyes with a Home view: greeting, a focus card, "History" and "Ask Boo" buttons. The eyes stay visible over its top bar. | |
| 1.3 | Click the X (Close) in the panel's top right. | The panel folds; the eyes stay. | |
| 1.4 | Right-click the tray icon, choose Show Boo. | The panel opens on the view it had. | |
| 1.5 | Close it, then click the eyes once. | The panel opens. | |
| 1.6 | Close it. Rest the pointer on the eyes and keep still. | After about a third of a second the panel opens by itself. The app you were in keeps the keyboard (type a letter in Notepad: it lands in Notepad). | |
| 1.7 | Move the pointer down through the panel and out of its bottom edge. | The panel folds as soon as the pointer leaves it. The eyes go back to sleep about a second later. | |
| 1.8 | Hover again so it opens, then press Ctrl+Alt+B while it is open. | The panel stays up now even when the pointer leaves (a deliberate open keeps it). | |
| 1.9 | With the panel open and focused, press Esc. | The panel folds. | |

## 2. Three views and "Open in a window"

| # | Do | You should see | OK? |
| --- | --- | --- | --- |
| 2.1 | Open the panel. Click Chat in the top bar. | The Chat view: an empty conversation, the "Ask Boo…" box, Send, Talk and an "open in a window" button. | |
| 2.2 | Click the gear (Settings). | The panel grows to about 640 x 520; a left column General / Voice / Shortcuts / Appearance / Task folder / AI and keys / Updates and the settings page on the right. Clicking a column item scrolls to that group. | |
| 2.3 | Press Ctrl+1, Ctrl+2, Ctrl+3. | Home, Chat, Settings in that order. F6 cycles them too. | |
| 2.4 | In Chat, click "Open in a window". | The same content becomes a window about 760 x 520 that you can drag by its top bar and resize from its edges. | |
| 2.5 | Click the same button again. | It docks back under the eyes at 420 x 330. | |

## 3. Ask Boo with your real key (Chat)

| # | Do | You should see | OK? |
| --- | --- | --- | --- |
| 3.1 | Settings > AI and keys: provider OpenAI, paste your key, Save settings. | "Saved" in the status line; the key box is masked and clears. | |
| 3.2 | Chat: type "What is the capital of Australia?" and press Send (or Ctrl+Enter). | Your bubble on the right, Boo's answer on the left within a few seconds; the eyes show thinking while it runs. | |
| 3.3 | Ask "Give me three sources on how WPF renders text" and wait. | An answer with a Sources card of links underneath. | |
| 3.4 | Ask a long question ("Explain TCP congestion control in detail") and click Stop (the square in the top bar) while it is streaming. | The answer stops where it was; the status line says Stopped; the eyes go calm. | |

## 4. Focus session from Home

| # | Do | You should see | OK? |
| --- | --- | --- | --- |
| 4.1 | Home: task "Write the Monday summary", minutes 5, tick Developer Mode, click Start now. | The card shows Running and a counting clock; the eyes show the focus face. | |
| 4.2 | Switch to the YouTube tab and stay there about 30 seconds. | Boo nudges you (the eyes change, a chirp, a nudge message) and offers to get back to it. | |
| 4.3 | Click Pause, then Resume. | The clock freezes on Pause and continues on Resume. | |
| 4.4 | Press Ctrl+Alt+S. | "Paused by Stop; Resume is explicit." Nothing runs until you click Resume. | |
| 4.5 | Click End. | Ended; the happy face shows for a couple of seconds; the session is in History. | |

## 5. Notch status words (only if PR 2 is in the build)

| # | Do | You should see | OK? |
| --- | --- | --- | --- |
| 5.1 | Ask a question in Chat and watch the eyes. | "Thinking" beside the eyes while it runs, gone when the answer is in. | |
| 5.2 | Start a focus session and close the panel. | The remaining time beside the eyes, ticking. | |

## When something fails

Note the step number and what you saw instead, and take a screenshot (Win+Shift+S) of the eyes and the panel. Post
them in the project thread. Each failed step becomes a regression test before the next sprint starts.
