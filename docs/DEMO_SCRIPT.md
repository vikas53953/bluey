# Boo demo script (shot by shot)

A recording script for a 6 to 8 minute video of Boo on Windows, built to mirror Riley Brown's Bluey video "I Built My Most Useful App Ever (Full Guide)" (youtube.com/watch?v=8QPd6WD9Zzg). The video page itself could not be read from here, so the beat order follows the feature order in Riley's repository README and the parity table in `docs/verification/2026-11-08-u8-riley-parity.md`: wake the character, hold to talk, state a task, get watched, get nudged, finish with a happy face, then the extras (pointing, research, computer control, Undo, Ctrl+Option+S). Every "You see" line below is what the `lane/face` code does; nothing in it is assumed.

Riley's app lives on a phone propped under the Mac. Boo lives in a notch at the top of the Windows screen, so the camera work is different: screen capture plus one phone shot of the notch.

## Before you record

- **Boo hides itself from screen recordings.** Windows capture exclusion means OBS, Xbox Game Bar and Snipping Tool show a black or empty area where the notch, pointer and ring are. Two options:
  - **Option A (recommended for the video):** start Boo with the environment variable `BOO_QA_CAPTURABLE=1` set, so screen recorders can see it. In PowerShell: `$env:BOO_QA_CAPTURABLE=1; .\Boo.exe`. The status line will say "QA mode: Boo windows are capturable". Mention this once in the voice-over or cut the status line.
  - **Option B:** record the monitor with a phone or second camera for the notch shots and use screen capture for everything else.
- Set **Developer Mode** on in Focus for the focus beat (4x clock, 10 s checks, nudge after about 20 to 40 real seconds of off-task activity). Untick it on camera before the closing shot if you want to show the real-time setting.
- Open and arrange: Notepad with the text `Draft v1`, a busy web page (a signup page or a dashboard), a YouTube video tab (the distraction), PowerShell (for the security beat), and the Boo full window.
- Check **Mute chirps** is off and system volume is up so the nudge and session-end chirps are heard.
- Hide the key: the **OpenAI key (voice + OpenAI text)** box is masked and clears after save, but do not show the paste.
- Display scaling: 100% gives the cleanest pointer landing. 125% and 150% are built but not yet proven by hand.

Timings assume you speak at a normal pace. Total: about 7 minutes.

---

## Shot 1. Cold open: the eyes (0:00 to 0:20)

**Camera:** screen capture, desktop empty, mouse in the middle.

**Do:** Move the mouse to the top centre edge. The notch slides down. Move the mouse left and right; the pupils follow. Wait for a blink. Move the mouse away; it retracts after just under a second.

**You see:** a black pocket about 84 x 36 px with two white button eyes and no text. Pupils track the cursor. A blink is a quick vertical squash.

**Say:** "This is Boo. It lives in a little notch at the top of my screen, watches the mouse, blinks, and otherwise stays out of the way. It's my Windows version of Riley Brown's Bluey, except there's no phone: the whole thing is one app on this PC."

## Shot 2. Hold to talk (0:20 to 0:50)

**Camera:** screen capture; keep the full window closed so the notch does the acting.

**Do:** Hold **Ctrl+Alt+Space**, say "Hey Boo, what's two plus two?", release.

**You see:** while you hold, the notch widens to the listening face with sound bars and the Windows mic icon appears in the taskbar. On release the thinking face, then the responding face while Boo speaks.

**Say (after the answer):** "Riley holds the face on his phone to talk. Here you hold Ctrl+Alt+Space, or press and hold the eyes themselves." Reveal the notch, press and hold the eyes, ask "What day is it?", release. "Release to send. Nothing is listening until I hold."

## Shot 3. State a task, focus starts (0:50 to 1:20)

**Do:** Hold Ctrl+Alt+Space and say "I'm writing the Q4 firewall migration plan for the next six minutes." Release.

**You see / hear:** Boo replies "Focus started: Q4 firewall migration plan for 6 minutes." Press **Ctrl+Alt+B** to open the full window: Focus shows the task, the countdown, and "Running; monitoring pauses on Stop, lock, sleep, or Pause." If Developer Mode is on, the countdown runs 4x.

**Say:** "That's the same flow as Bluey: say what you're working on, Boo starts a focus session. No confirmation, no form. Every 30 seconds it takes one screenshot of this display and asks the model whether what it sees relates to my task. In Developer Mode, which I've turned on for the video, that's every 10 seconds and the clock runs four times faster."

**Tip:** the minutes default to 30 if you don't say a number. Six keeps the shot short.

## Shot 4. Work, then get distracted (1:20 to 2:20)

**Do:** Close the full window with ×. Type a few lines into Notepad about a firewall plan for 15 seconds. Then open the YouTube tab full screen and leave it.

**You see:** every check the notch briefly shows the capture face (a wider pocket). After roughly 20 to 40 real seconds of clear off-task evidence a nudge arrives: the notch grows into a bubble, a short caption types in character by character, the eyes shake, a two-note chirp plays, and if the distracting text is readable on screen the blue pointer flies to it and rings it. Two pills appear: **Back to it** and **5 min break**.

**Say (over the wait):** "Short detours are fine. Boo wants to see sustained off-task activity, at least two checks in a row, before it says anything, and it never nudges more than once every two minutes. And it never touches my apps: no closing tabs, no blocking."

**Say (on the nudge):** "There it is. A nudge is a sentence, a chirp, and a pointer at what distracted me. Nothing else."

**Do:** Click **Back to it**. Go back to Notepad.

## Shot 5. Session ends: happy face (2:20 to 3:00)

**Do:** Keep typing in Notepad until the countdown reaches zero (about 90 real seconds in total for a 6-minute Developer Mode session).

**You see / hear:** the eyes turn into "^" arcs with a small bounce for about 2.5 seconds, one chirp, and the Focus status "Session complete. Nice work. It is saved in your history." Open the full window and scroll to **Session history**: a timeline bar, light for on task, red for off task, light grey for uncertain, a peach tick for pauses, dark grey for not observed.

**Say:** "Happy face at the end, and the session is saved with a timeline. These are periodic estimates, not a time audit."

**Do:** Untick **Developer Mode** on camera. "Developer Mode off: real-time clock, checks every 30 seconds."

## Shot 6. Point at things on screen (3:00 to 3:45)

**Do:** Open the busy web page. Put the mouse in a corner. Hold Ctrl+Alt+Space: "Show me where the search box is." Release.

**You see:** Boo explains out loud and in text, a blue pointer starts at your mouse and flies to the element in about half a second, then a ring or arrow is drawn around it. Scroll the page: pointer and ring vanish immediately.

**Say:** "Riley's Bluey points with a blue cursor. Boo does that and then draws a ring, HeyClicky-style. The marks are click-through and disappear the moment I scroll."

**Tip:** if Boo answers without drawing, the status line says why ("Boo explained the screen without a verified mark"). Ask again rather than cutting around it.

## Shot 7. Research with sources (3:45 to 4:30)

**Do:** Open the full window. In **Ask Boo** type `research the best free tools for team retrospectives with sources` and press **Send**.

**You see:** progress, then a short answer with clickable links under **Sources**. Click one; the browser opens.

**Say:** "Research cards, like Bluey's. Anything that starts with research, find, search or compare, or says 'with sources', goes to the web and comes back with the links it relied on."

## Shot 8. Computer control with approval (4:30 to 5:30)

**Do:** Click into Notepad (it already contains `Draft v1`), then press **Ctrl+Alt+B**. In **Ask Boo** type `change the text to Final v2`, press **Send**.

**You see:** a **Review action** card with **Requested target** (the model's words) and **Resolved destination** "Notepad window (process NNNN)", the real window. Nothing has changed yet. Keyboard focus sits on **Cancel**.

**Say:** "This is the part Bluey calls computer control. Boo proposes, I approve. The card shows the real window it's about to type into, not what the model thinks it is."

**Do:** Press **Approve**. Notepad shows `Final v2`. Boo reads the field back and reports "Verified outcome: …".

**Say:** "And it reads the field back before it says done."

**Tip:** Notepad's text area has not yet been proven addressable by Boo's UI Automation reader. If Boo says it cannot read or edit the field, switch to WordPad or any app with a plain text box, and keep the shot.

## Shot 9. Undo (5:30 to 6:00)

**Do:** Without touching Notepad, type `undo` in Ask Boo and press **Send**.

**You see:** "Undo is ready: 1 step(s) to review before anything changes." and a second Review action card starting "Undo: ". No AI call is made. Press **Approve**. Notepad shows `Draft v1` again and Boo confirms after reading it back. Type `undo` once more: "Nothing to undo: Boo has no recorded edit."

**Say:** "Undo is planned locally from what Boo itself changed, it goes through the same approval card, and it refuses if I've typed in the field since. One honest limit: it can't restore a field that was empty before the edit, which is why Notepad already had text in it."

## Shot 10. The guard rails (6:00 to 6:30)

**Do:** Bring PowerShell forward, press **Ctrl+Alt+B**, type `type hello into this window`, press **Send**.

**You see:** either a Review action card with the red line "Boo will not click or type into powershell: input there can run commands or change system settings." and **Approve** greyed out, or Boo declines in words. Nothing is typed into the console. Press **Cancel**.

**Say:** "Boo won't type into terminals, registry editors, Task Manager, installers or password prompts, even if I approve. Typed text into a shell is a command, so it's just off the table."

## Shot 11. Stop everything (6:30 to 6:50)

**Do:** Ask for a long answer ("Tell me a short story"). While Boo is speaking, press **Ctrl+Alt+S**.

**You see:** speech stops at once, any pointer or card disappears, the notch returns to its idle face, and the status under Talk reads "Microphone stopped. Screen activity is shown in the sharing indicator." If a focus session were running it would now be paused with "Paused by Stop; Resume is explicit."

**Say:** "Riley's panic button is Control+Option+S. Boo has Ctrl+Alt+S, and Ctrl+Alt+F12 does the same thing. Right-clicking the eyes or the tray Stop works too. It kills capture, audio and any pending action, and pauses a focus session."

## Shot 12. Privacy and close (6:50 to 7:15)

**Camera:** full window, Settings visible with the masked credential box empty.

**Say:** "Privacy, briefly. The key is encrypted with Windows DPAPI and only ever goes to api.openai.com. Screenshots and audio stay in memory and never touch disk. The notch shows a capture face every time Boo looks, Windows shows the mic icon every time it listens, and Boo excludes its own windows from every screenshot. One app, one PC, no phone."

**Do:** Hold Ctrl+Alt+Space: "Bye, Boo." Boo says "Going quiet." and the status reads "Boo went quiet. Activate Talk or hold Boo's eyes to speak again." Move the mouse away; the notch retracts.

**Say:** "That's Boo."

---

## If a shot goes wrong

| Problem | What to do on camera |
| --- | --- |
| No nudge arrives in shot 4 | Make the distraction unmistakable (full-screen video with a visible title). Nudges need two consecutive off-task checks; in Developer Mode that is about 20 s at minimum, 20 to 40 s typical. |
| Pointer lands off the target | Note your scaling. 100% is the proven case; 125/150% are built but unverified. Retake at 100%. |
| Boo cannot read Notepad's text area | Use another app with a plain text box. The Review action and Undo flow are the point, not Notepad. |
| "Capture paused: Boo could not confirm UI exclusion." | Windows refused display-affinity exclusion. Restart Boo. If you started it with `BOO_QA_CAPTURABLE=1`, exclusion is intentionally off and this message should not appear. |
| Hotkey message in the status line ("… is used by another app") | Close the app that owns it (often a screen recorder's own hotkeys) or use the tray fallbacks it names. |
| Voice latency feels slow | The first turn is cold. Do two warm-up questions before recording shot 2. |

## Mapping to Riley's video beats

| Riley's beat | Boo shot |
| --- | --- |
| Googly eyes on the phone follow the mouse | 1 |
| Hold the face to speak | 2 |
| "I'm making a Codex video for 30 minutes" starts focus | 3 |
| Screen checked every 30 s, Decisions classify | 3, 4 |
| Nudge bubble and chirp | 4 |
| Session end, timeline on the phone | 5 |
| Developer Mode 4x / 10 s | 3, 5 |
| Blue cursor points at things | 6 |
| Research cards | 7 |
| Computer control on request | 8, 9 |
| Control+Option+S stops it all | 11 |
| Screen sharing and privacy | 12 |
| iPhone pairing, Codex controller, meeting notes | dropped on purpose |
