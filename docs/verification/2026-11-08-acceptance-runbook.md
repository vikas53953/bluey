---
title: Boo one-sitting acceptance runbook (Windows)
type: verification
date: 2026-11-08
status: NOT RUN
for: Vikas (owner). No WPF or developer knowledge needed.
time: about 15 minutes of checks, then one 25-minute focus session
---

# Boo acceptance runbook

This is the one run that decides whether Boo is accepted: the three demos plus one 25-minute focus session, all on one build, on your PC. Work top to bottom. For each step, do what the **Do** column says, compare with **You should see**, and tick PASS or FAIL. If a step fails, write one line about what you saw and carry on. Nothing later depends on an earlier PASS unless the step says so.

Everything here comes from the code on branch `lane/face` (PR #3). Where the code does not settle what you will see, the step says **(check)**: just write down what happened.

**Before you start**

- Have ready: your OpenAI key (you will type it into Boo only, never into a chat), a microphone and speakers, Notepad, a browser, and your phone to take photos.
- **Boo hides itself from screenshots** on purpose (Windows capture exclusion), so Snipping Tool shows a black or empty area where Boo is. Use your phone camera for photos of Boo.
- **Do not lock the PC (Win+L) or press Ctrl+Alt+F12 or Ctrl+Alt+S during the 25-minute session.** Both pause the session (by design) and you would have to press Resume.

Write down: Windows version (Settings > System > About), display scaling (Settings > System > Display > Scale), and the build's commit (step 0.3).

## 0. Get the build (about 3 minutes)

| # | Do | You should see | Result |
|---|----|----------------|--------|
| 0.1 | If an older Boo (for example candidate15) is running, right-click its tray icon (the generic app icon; tooltip "Boo companion") and choose **Quit**. | No Boo tray icon left. Boo allows only one copy at a time: a new copy started while an old one runs just exits silently. | ☐ PASS ☐ FAIL |
| 0.2 | **Installer (preferred):** On GitHub (signed in), **Actions** > **release** > newest green run for branch `lane/face` > artifact **boo-installer-win-x64**. Unzip it and run `Boo-win-Setup.exe` (per-user, no admin prompt, installs to `%LocalAppData%\Boo`, adds a Start menu entry, starts Boo). Then skip to 0.5. **Portable (fallback):** open repository `vikas53953/bluey` > **Pull requests** > **#3** ("Integration: eyes/notch + pointer + focus + research/Undo + packaging") > **Checks**, or go to **Actions** > **windows-ci** and pick the newest green run for branch `lane/face`. At the bottom of the run page, under **Artifacts**, download **boo-win-x64**. *Alternative:* **Actions** > **release** > newest green run for `lane/face` > artifact **boo-portable-win-x64** (contains `Boo-0.1.0-<commit>-win-x64.zip`, a `.sha256` file and a `READ-ME-FIRST.txt` inside). | A ZIP downloads (about 70 to 100 MB (check)). | ☐ PASS ☐ FAIL |
| 0.3 | Note the 7-character commit shown on the run page (at the time of writing the head of `lane/face` was `22ef666`). | Commit written down: ________ | ☐ done |
| 0.4 | Right-click the ZIP > **Extract All** into a normal folder, for example `Downloads\Boo`. For the portable artifact, extract the inner `Boo-...-win-x64.zip` too. **Do not run Boo.exe from inside the ZIP.** | A folder containing `Boo.exe` (many DLLs next to it is normal: it is self-contained, no .NET install needed). | ☐ PASS ☐ FAIL |
| 0.5 | Double-click `Boo.exe` (the installer starts Boo for you). If SmartScreen says "Windows protected your PC", choose **More info** > **Run anyway** (the build is not code-signed). | No main window opens (Boo starts in the background). A tray icon with tooltip "Boo companion" appears. | ☐ PASS ☐ FAIL |

## 1. First run: key and settings (about 2 minutes)

| # | Do | You should see | Result |
|---|----|----------------|--------|
| 1.1 | Press **Ctrl+Alt+B**. | The full Boo window opens with **Ask Boo**, **Conversation**, **Focus** and **Settings** sections. | ☐ PASS ☐ FAIL |
| 1.2 | In **Settings**, pick your screen in **Display** and your mic in **Microphone**. | The top line changes from "Screen sharing off · display not selected" to show the chosen display name. | ☐ PASS ☐ FAIL |
| 1.3 | Click in **OpenAI key (voice + OpenAI text)** (masked box) and type or paste your OpenAI key **there only**. Press **Save settings**. | Status line: "Settings saved. Credential input was protected and cleared from the UI." The box is empty again. The key is stored in `%LOCALAPPDATA%\Boo\credential.bin`, encrypted with Windows DPAPI for your Windows account. Never paste the key into chat, email or a file. | ☐ PASS ☐ FAIL |
| 1.4 | Optional: press Win+R, type `%LOCALAPPDATA%\Boo`, Enter. | Files `settings.json`, `appearance.json`, `credential.bin` (and `sessions.db` after a focus session). Do not send `credential.bin` to anyone. | ☐ PASS ☐ FAIL |

## 2. Notch and eyes (about 1.5 minutes)

Close the full window with **×** first (it hides; Boo keeps running). The notch is a small black "pocket" at the top centre of the chosen display with two white "button" eyes. When idle it retracts out of view.

| # | Do | You should see | Result |
|---|----|----------------|--------|
| 2.1 | Move the mouse to the very top edge, at the centre of the screen. | The notch slides down in about a quarter of a second: black pocket about 84 x 36 px at 100% scaling, curved shoulders into the screen edge, two white eyes with black pupils. **No text** on it. | ☐ PASS ☐ FAIL |
| 2.2 | With the notch showing, move the mouse slowly left, right and below it. | Both pupils follow the mouse smoothly (small movement, never leave the white). | ☐ PASS ☐ FAIL |
| 2.3 | Keep the mouse near the notch and watch for 20 seconds. | The eyes blink (a quick vertical squash) every 3 to 6 seconds; now and then a double blink. | ☐ PASS ☐ FAIL |
| 2.4 | Move the mouse away from the notch. | It stays for just under a second, then retracts quickly. Coming back before it retracts keeps it out. | ☐ PASS ☐ FAIL |
| 2.5 | Reveal the notch and **click** the eyes once (short click). | The full Boo window opens. | ☐ PASS ☐ FAIL |
| 2.6 | Reveal the notch and **right-click** it. | Acts as Stop: no menu (code: right-click on the pocket calls Stop). The tray icon's right-click menu has **Show Boo (Ctrl+Alt+B)**, **Stop (Ctrl+Alt+F12)**, **Settings**, **Quit** (the shortcut disappears from a label if another app owns that key). | ☐ PASS ☐ FAIL |

## 3. Hold-to-talk, both ways, and latency (about 3 minutes)

Open Notepad and click into it so it has the text cursor.

| # | Do | You should see | Result |
|---|----|----------------|--------|
| 3.1 | **Hold Ctrl+Alt+Space** (keep all three down), say "What's two plus two?", then let go. | While held: the notch shows the listening face and widens (about 172 px at 100%) with listening bars; Boo's Windows mic icon appears in the taskbar. **No spaces are typed into Notepad** and Notepad's window menu does not open. On release: one spoken answer (and the text in **Conversation** when the full window is open). | ☐ PASS ☐ FAIL |
| 3.2 | Tap Ctrl+Alt+Space quickly (under a fifth of a second). | Nothing is sent, no reply. In the full window, under the Talk button, the hint **"Hold to talk"** appears. | ☐ PASS ☐ FAIL |
| 3.3 | Reveal the notch, **press and hold the eyes** with the left mouse button (longer than 0.2 s), ask "What day is it today?", release. | Same as 3.1: listening face while held, one spoken answer after release. | ☐ PASS ☐ FAIL |
| 3.4 | While Boo is giving a long answer (ask "Tell me a short story"), hold Ctrl+Alt+Space again and ask something else. | Boo stops speaking as soon as you press, listens, then answers the new question. The old answer does not resume. | ☐ PASS ☐ FAIL |
| 3.5 | Latency: do 5 more quick hold-to-talk questions ("what's 3 plus 3", and so on), releasing as soon as you stop talking. Then open the full window (**Ctrl+Alt+B**) and read the line under **Settings** > **Microphone**. | A line like "Voice latency (end of speech → first audio): warm 5/20, median ___ ms, p95 ___ ms, target not met yet. Turns (c = cold): ...". "target not met yet" is expected with fewer than 20 warm turns. **PASS if the median is 1500 ms or less.** Write the median and p95 here: ______ | ☐ PASS ☐ FAIL |

The full latency target (20 warm turns, median ≤ 1500 ms, p95 ≤ 3000 ms, shown as "meets target") is optional today; it takes about 10 more minutes.

## 4. Demo 1: explain by pointing and drawing (about 1 minute)

| # | Do | You should see | Result |
|---|----|----------------|--------|
| 4.1 | Open a busy web page or app on the chosen display. Put the mouse in a corner. Hold Ctrl+Alt+Space and ask "Show me where the search box is" (or ask about any button on screen). Release. | Boo answers out loud. A **blue pointer** starts at your mouse position and flies to the item in about half a second; **after it lands**, a ring (or arrow) is drawn around the item. While Boo looks at the screen the notch shows the capture state (wider pocket) as a disclosure. | ☐ PASS ☐ FAIL |
| 4.2 | Check where it landed. | The pointer tip is on the item, not offset (an offset by your scaling factor, e.g. 1.25x or 1.5x, is a FAIL: photo please). | ☐ PASS ☐ FAIL |
| 4.3 | Ask again, and while the ring is showing, scroll the page. | Pointer and ring disappear immediately. | ☐ PASS ☐ FAIL |

## 5. Demo 2: instant voice conversation (about 1 minute)

| # | Do | You should see | Result |
|---|----|----------------|--------|
| 5.1 | Have a 3-turn conversation by holding Ctrl+Alt+Space each time, e.g. "I'm planning a firewall change window", then "What should I check first?", then "Make that shorter." | Each reply starts quickly and follows on from the previous turn (it remembers the context). Spoken and written text match. | ☐ PASS ☐ FAIL |

## 6. Demo 3: search, then do a task with Apply and Undo (about 3 minutes)

| # | Do | You should see | Result |
|---|----|----------------|--------|
| 6.1 | In the full window, type in **Ask Boo**: `research the best free tools for team retrospectives with sources` and press **Send**. | Progress, then a short answer with clickable links under **Sources**. Clicking a link opens your browser. | ☐ PASS ☐ FAIL |
| 6.2 | Open Notepad, type `Draft v1` in it and leave Notepad in front. **The field must already contain text: Boo's Undo cannot restore a field that was empty before its edit.** Press **Ctrl+Alt+B**. | The full window opens; Boo remembers Notepad as the target window. | ☐ PASS ☐ FAIL |
| 6.3 | In **Ask Boo** type `change the text to Final v2` and press **Send**. (Requests starting with type, edit, change, set, click, open, create or task are treated as actions.) | A **Review action** card shows **Requested target** and **Resolved destination** = "Notepad window (process NNNN)" (the real window, not the model's words). Nothing has changed in Notepad yet. **Cancel** has the keyboard focus, not Approve. | ☐ PASS ☐ FAIL |
| 6.4 | Press **Approve** (this applies the edit). | Notepad now shows `Final v2`. Boo reads the field back and reports what it actually contains. (check) Whether Notepad's text area is one Boo can address is not proven; if Boo says it cannot read or edit the field, write the exact message and FAIL this step. | ☐ PASS ☐ FAIL |
| 6.5 | Without touching Notepad, type `undo` in **Ask Boo** and press **Send** (saying "undo" with hold-to-talk also works). | No AI call: Boo says "Undo is ready: 1 step(s) to review before anything changes." and shows another **Review action** card starting "Undo: ". | ☐ PASS ☐ FAIL |
| 6.6 | Press **Approve**. | Notepad shows `Draft v1` again, and Boo confirms the restore after reading it back. | ☐ PASS ☐ FAIL |
| 6.7 | Type `undo` again and Send. | "Nothing to undo: Boo has no recorded edit." | ☐ PASS ☐ FAIL |

## 7. Security guard: no typing into shells (about 1 minute)

| # | Do | You should see | Result |
|---|----|----------------|--------|
| 7.1 | Open PowerShell (or Windows Terminal, or cmd) and leave it in front. Press **Ctrl+Alt+B**. Type in **Ask Boo**: `type hello into this window` and press **Send**. | Either a **Review action** card with the red message "Boo will not click or type into WindowsTerminal: input there can run commands or change system settings." (the name may be `powershell` or `cmd` instead) and **Approve greyed out**, or Boo declines in words (check). In both cases: **nothing is typed into the console.** | ☐ PASS ☐ FAIL |
| 7.2 | Press **Cancel** if a card is shown. | Card closes; the console is unchanged. | ☐ PASS ☐ FAIL |

Also blocked by the same guard: regedit, Task Manager, MMC consoles, installers, UAC and credential prompts, WSL/bash, PuTTY, and Boo's own windows.

## 8. Emergency stop: Ctrl+Alt+F12 and Ctrl+Alt+S (1 minute; do it before the focus session)

| # | Do | You should see | Result |
|---|----|----------------|--------|
| 8.1 | Ask Boo a question that gets a long spoken answer. While it is speaking, press **Ctrl+Alt+F12**. | Speech stops at once. Under Talk: "Microphone stopped. Screen activity is shown in the sharing indicator." Any pointer, ring or Review action card disappears. The notch goes back to its idle face. | ☐ PASS ☐ FAIL |
| 8.2 | Hold Ctrl+Alt+Space, and while still holding Ctrl+Alt, press **F12**. Then let go of everything. | Listening cancels immediately; nothing is sent when you let go; the taskbar mic icon goes away within a couple of seconds. | ☐ PASS ☐ FAIL |
| 8.3 | Ask for another long answer. While Boo is speaking, press **Ctrl+Alt+S** (Riley's Control+Option+S). | Same effect as 8.1, and the notch flashes a small **"Stopped"** bubble for about 1.6 seconds before returning to the idle face. | ☐ PASS ☐ FAIL |

If Ctrl+Alt+F12 or Ctrl+Alt+S is taken by another app, the notch shows **"Shortcut busy: use tray"** (or "Shortcuts busy" when more than one is taken) and Settings explains which key; use the tray menu **Stop** instead and note it.

## 9. Start with Windows (about 1 minute)

| # | Do | You should see | Result |
|---|----|----------------|--------|
| 9.1 | In the full window, next to **Talk**, check that **Start Boo with Windows** is **unticked** (default off). Tick it and press **Save settings**. | "Settings saved. Credential input was protected and cleared from the UI." If you ran Boo.exe from inside the ZIP or a temp folder you get "Settings saved, but Start with Windows was not turned on: ..." and the box unticks itself. That is correct (security fix); extract properly and retry. | ☐ PASS ☐ FAIL |
| 9.2 | In PowerShell run: `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v Boo` | `Boo    REG_SZ    "C:\...\Boo.exe" --background` (the full path in quotes, then `--background`). | ☐ PASS ☐ FAIL |
| 9.3 | Optional, at the very end: sign out and sign back in. | Boo starts in the background: notch and tray icon only, no main window stealing focus. | ☐ PASS ☐ FAIL |
| 9.4 | Untick it, **Save settings**, run the same `reg query`. | "ERROR: The system was unable to find the specified registry key or value." Your other startup apps are untouched. | ☐ PASS ☐ FAIL |

## 10. Focus quick check in Developer Mode, with chirp (about 2 minutes)

Developer Mode makes the focus clock run 4x and checks the screen every 10 seconds instead of 30, so a 6-minute session takes 90 real seconds.

| # | Do | You should see | Result |
|---|----|----------------|--------|
| 10.1 | Make sure **Mute chirps** (next to Talk) is unticked and your speakers are on. In **Focus**, tick **Developer Mode**. | Text below it: "Developer Mode on: the focus clock runs 4x and checks happen every 10 seconds." | ☐ PASS ☐ FAIL |
| 10.2 | Task: `Write the Q4 firewall migration plan`, duration `6`, press **Start**. Immediately open a YouTube video (or any clearly unrelated page) full screen on the chosen display and leave it there. | Focus status "Running; monitoring pauses on Stop, lock, sleep, or Pause." Timer counts down fast. | ☐ PASS ☐ FAIL |
| 10.3 | Wait (about 20 to 40 seconds). | A **nudge**: the notch grows into a bubble with a caption that types in, the eyes shake, a short rising **two-note chirp** plays, and the blue pointer may fly to the distracting tab text and ring it (only if the matching text is visible on screen). Focus status: "Nudge: return to the task when you are ready. Boo did not change your apps." Boo never closes or clicks anything. | ☐ PASS ☐ FAIL |
| 10.4 | Switch back to something on-task (for example Notepad) and wait until the timer reaches zero. | **Happy face**: eyes turn into "^" arcs with a small bounce for about 2.5 seconds, the same chirp plays once, and Focus status says "Session complete. Nice work. It is saved in your history." The session appears in **Session history**. | ☐ PASS ☐ FAIL |
| 10.5 | **Untick Developer Mode.** | "Developer Mode off: real-time clock, checks every 30 seconds." | ☐ PASS ☐ FAIL |

## 11. The 25-minute focus session (25 minutes)

| # | Do | You should see | Result |
|---|----|----------------|--------|
| 11.1 | Developer Mode **off**. Task: something real you are working on. Duration `25`. Press **Start**. Close the full window with × and just work. | Timer counts down in real time. Boo looks at the screen about every 30 seconds; the notch shows the capture disclosure briefly while it does. | ☐ PASS ☐ FAIL |
| 11.2 | Short detours (a minute on email or chat) should be fine. Around minute 10, go clearly off-task (a video or news site) and stay there 4 to 5 minutes. | No nudge for short detours. After about 2 to 4 minutes of sustained off-task activity: one nudge bubble with chirp (at most one nudge per 2 minutes). | ☐ PASS ☐ FAIL |
| 11.3 | Go back to work until the end. Do not lock the PC or press Ctrl+Alt+F12. | At 25:00: happy face (^ arcs, bounce), chirp, "Session complete. Nice work. It is saved in your history." | ☐ PASS ☐ FAIL |
| 11.4 | Open the full window and look at **Session history**. | The session is listed with its timeline (light = on task, red = off task, see the legend under it). | ☐ PASS ☐ FAIL |

## Result summary

Copy this into your reply:

```
Build commit: ______   Windows: ______   Scaling: ____%   Monitors: __
Steps failed (number + one line each):
Latency (step 3.5): median ____ ms, p95 ____ ms over __ warm turns
Focus 25 min: nudges seen __, happy face yes/no
Overall: ACCEPT / NOT YET
```

## What to send back if something fails

1. The **step number** and one line on what you saw instead.
2. A **phone photo** of the screen at that moment (screenshots do not show Boo).
3. Any status text Boo showed (the line under Talk, the status line, or the Focus status). For latency problems, copy the whole "Voice latency" line.
4. A **diagnostics ZIP**: download `scripts/Collect-BooDiagnostics.ps1` from branch `lane/face` on GitHub (open the file > **Download raw file**), then in PowerShell run:

   ```
   powershell -ExecutionPolicy Bypass -File "$HOME\Downloads\Collect-BooDiagnostics.ps1"
   ```

   It only reads; it writes one file, `Boo-diagnostics-<date-time>.zip`, to your Desktop. It contains Boo's `settings.json` and `appearance.json`, a list of file names and sizes in the data folder, Windows version, monitors with scaling, audio devices, mic privacy setting, the Boo Run key, the running Boo process and recent Boo crash entries from the Windows Application log. It **never** copies `credential.bin` (your encrypted key). Add `-IncludeFocusHistory` to also include `sessions.db` (your focus task names and check results) only if the failure is in the focus steps. Open the ZIP and look inside before sending it if you like.

**About logs:** Boo writes a redacted crash log (`crash-*.txt`, API keys stripped) under `%LOCALAPPDATA%\Boo\logs` only when something crashes or recovers; there is no routine activity log. Boo's data folder is `%LOCALAPPDATA%\Boo` (or the folder in the `BOO_DATA_DIR` environment variable, if you set one). So the photo, the status text and the diagnostics ZIP (which includes those crash logs) are the evidence.

Never send `credential.bin`, your API key, or a screenshot of the key boxes in Settings.
