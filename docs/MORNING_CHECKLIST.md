# Boo morning checklist (one page)

For Vikas, first thing at the PC. Build: `lane/face` (PR #3), head `47b6d9f` or newer. Nothing on this list has been tried on a real Windows PC yet; CI on windows-latest is green. Do the items in order: each one unblocks the next, and the first failures are the ones that would stop the demos. Budget: about 20 minutes to item 9, then 25 minutes for item 10. Tick as you go and photograph anything odd with your phone (Boo hides itself from screenshots).

## Before anything

- Quit any old Boo (candidate15) from its tray icon. Boo allows one copy at a time.
- Have your OpenAI key ready. Type it only into Boo's **OpenAI key (voice + OpenAI text)** box.
- Keep the full [acceptance runbook](verification/2026-11-08-acceptance-runbook.md) open; the step numbers below point into it.

## Priority order

| # | Test (runbook step) | Pass looks like | If it fails |
|---|---|---|---|
| 1 | **Installer runs** (0.2, 0.5). Download `boo-installer-win-x64` from Actions > release for `lane/face`, run `Boo-win-Setup.exe`, accept SmartScreen **More info > Run anyway**. | No admin prompt. Boo in Start menu and Settings > Apps. Tray icon "Boo companion", no main window. | Fall back to the portable `boo-portable-win-x64` ZIP (extract fully, run `Boo.exe`). Note the SmartScreen text. |
| 2 | **Hotkeys register** (1.1). Press **Ctrl+Alt+B**. | Full window opens. Status line is "Ready", not "Shortcut busy". | Read the Settings status sentence: it names the busy key and the tray fallback. Close the app that owns it (screen recorders often do). |
| 3 | **Key saved** (1.2, 1.3). Pick Display and Microphone, paste the key, **Save settings**. | "Settings saved. Credential input was protected and cleared from the UI." Box empties. | If "No OpenAI key yet" persists, re-paste; check `%LOCALAPPDATA%\Boo\credential.bin` exists. |
| 4 | **Notch hover** (2.1 to 2.6). Move the mouse to the top-centre edge. | Eyes reveal in about a quarter second, follow the cursor, blink, retract about a second after you leave. Right-click on them = Stop. | Note display scaling and whether a second monitor is involved. Hover timing was never measured natively. |
| 5 | **Hold to talk** (3.1, 3.2). Hold **Ctrl+Alt+Space**, "What's two plus two?", release. Then a quick tap. | Spoken answer; no spaces typed into the app behind. Tap shows "Hold to talk". | If "Ctrl+Alt+Space is unavailable", use **Talk** or hold the eyes. If no audio at all, check the Windows mic indicator appeared while held. |
| 6 | **Stop keys and keyboard** (8.1 to 8.4). During a long answer press **Ctrl+Alt+F12**; repeat with **Ctrl+Alt+S**. | Speech stops at once, pointer and cards vanish, notch shows "Stopped" briefly. In the full window, **F6** cycles Ask, Focus and Settings with a visible focus ring. | If one is taken, the other still works; tray **Stop** always works. Do this before the focus session so you know the panic button. |
| 7 | **Demo 1, explain + pointer** (4.1). On a busy page: "Show me where the search box is." | Blue pointer flies from your mouse to the item, ring draws after it lands, marks clear on scroll or window switch. | Photograph where the ring landed versus the real element. Drawing accuracy on real DPI is the biggest unknown. |
| 8 | **Demo 2, edit + Undo** (6.1 to 6.6). Notepad with `Draft v1`, Ctrl+Alt+B, type `change the text to Final v2`, Approve, then `undo`, Approve. | Review action card shows Notepad as destination; field reads `Final v2` then `Draft v1` again. Then 7.1: `type hello into this window` with PowerShell in front is refused in red. | If the card shows the wrong window, Cancel and note which window it named. Never approve a card aimed at a shell. |
| 9 | **Demo 3, research** (5.x). "Research the best free tools for team retrospectives and make a shortlist." | Progress, then a shortlist with clickable links under **Sources**. | If it answers without sources, note whether you said "research" or "find" at the start. |
| 10 | **25-minute focus session** (10.x, 11.x). Enter a real task, 25 minutes, Start. Work normally. Do not lock the PC or press the stop keys. | A nudge only when you drift for a while (bubble + chirp, at most every 2 minutes). At 25:00: happy face, chirp, "Session complete. Nice work. It is saved in your history." Session appears in history with pause gaps marked. | If it never nudges, deliberately open YouTube for 2 minutes. If it nudges while you are clearly on task, note what was on screen. For a quick dry run first, tick **Developer Mode** (4x clock, checks every 10 seconds). |

## Only if time allows

- ~~Settings > Text AI: switch to Anthropic Claude or Google Gemini~~ — out of scope since 13 Nov 2026 (Boo uses only OpenAI, for chat and voice). Skip it.
- **Settings > Updates**: paste a fine-grained GitHub token (Contents: read), tick the daily check, press **Check for updates**. Expect "no newer release" until a `v*` tag exists.
- **Start with Windows** (9.1 to 9.3): tick it, sign out and in, Boo should return as tray icon and notch only.
- **Perf**: in a Developer Mode focus session, watch Boo's private bytes in Task Manager stay flat; CI budget is 160 MB working set.

## What to send back

One line per failed item: step number, what you saw, a phone photo if visual. Build commit from the run page. Never send `credential.bin`, a key, or a screenshot of the key boxes.
