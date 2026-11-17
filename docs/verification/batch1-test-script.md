---
title: Boo Batch 1 (MVP) test script, for Vikas on his own PC
type: verification
date: 2026-11-18
status: DRAFT, not run
covers: typing, voice, screen pointer, Stop (incl. #185 right-click "Stopped" and #186 silent-reply recovery)
time: about 15 minutes
---

# Boo Batch 1 test script

Fourteen steps. Do each one, compare with **You should see**, tick PASS or FAIL. If a step fails, write one line about
what you saw and carry on. Every label and message below is copied from Boo's code on `lane/face` (plus #185 and #186,
which this batch includes).

**Before you start**

- Have ready: your OpenAI key (type it into Boo only, never into a chat), a microphone and speakers, a browser, your phone.
- Boo hides itself from screenshots on purpose, so use your **phone camera** for photos of Boo.
- Windows **Animation effects** must be on (Settings > Accessibility > Visual effects). If they are off, Boo skips the
  pointer on purpose. In Boo's own Settings, leave **Reduced motion** unticked for the same reason.
- If an older Boo is running, right-click its tray icon (tooltip "Boo companion") and choose **Quit** first. Boo runs one
  copy at a time.

| # | Do | You should see | Result |
|---|----|----------------|--------|
| 1 | **Install.** Open the build link on the card, download the artifact **boo-installer-win-x64**, unzip it, run `Boo-win-Setup.exe`. If SmartScreen warns, choose **More info** > **Run anyway**. | No window opens (Boo starts in the background). A tray icon appears with the tooltip **"Boo companion"**. | ☐ PASS ☐ FAIL |
| 2 | Press **Ctrl+Alt+B**. | Boo's window opens with **Ask Boo**, **Conversation**, **Focus** and **Settings**. | ☐ PASS ☐ FAIL |
| 3 | **Key and settings.** In **Settings**: pick your screen in **Display** and your mic in **Microphone**; set **Text AI provider** to **OpenAI**; paste your key into **OpenAI key (voice + OpenAI text)**; press **Save settings**. | Status line: **"Settings saved. Credential input was protected and cleared from the UI."** The key box is empty again. The top line reads **"Screen sharing off · \<your display\>"**. | ☐ PASS ☐ FAIL |
| 4 | **Notch.** Close the window with **×** (Boo keeps running). Move the mouse to the very top edge, centre of the screen. Then move it away. | A small black pocket slides down with two white eyes whose pupils follow the mouse. When you move away it slides back up after about a second. | ☐ PASS ☐ FAIL |
| 5 | **Typing.** Press **Ctrl+Alt+B**. In **Ask Boo** type `What is the capital of France?` and press **Send** (or Ctrl+Enter). | A real answer appears in **Conversation** within a few seconds, and it says **Paris**. | ☐ PASS ☐ FAIL |
| 6 | **Voice, hold to talk.** **Hold Ctrl+Alt+Space**, say "What is two plus three?", then let go. | While you hold: **"Listening… release to send."** under Talk, and the notch widens with listening bars. After you let go you **hear** one spoken answer that says **five**, and the same text appears in **Conversation**. | ☐ PASS ☐ FAIL |
| 7 | Tap Ctrl+Alt+Space very quickly (a fifth of a second). | Nothing is sent, no answer. Under Talk: **"Hold to talk"**. | ☐ PASS ☐ FAIL |
| 8 | **Voice, Talk button.** Press **Talk**. Say "I'm planning a firewall change window", wait for the answer, then say "What should I check first?". Then press **Stop talk**. | After Talk: the button reads **Stop talk** and the line under it says **"Listening… press Stop talk or the global Stop to interrupt."** The second answer follows on from the first (it remembers). After Stop talk: the button reads **Talk** and the line says **"Microphone idle. Activate Talk when you want to speak."** | ☐ PASS ☐ FAIL |
| 9 | **If Boo's voice goes quiet** (watch for this during steps 6, 8, 12 and 13; you cannot cause it on purpose): the answer text shows but you hear nothing. | Within a few seconds Boo **reads the answer aloud** itself, and the status line says **"OpenAI's voice reply came back silent; Boo read the answer aloud."** FAIL if you hear nothing and the status line says **"Spoken reply is unavailable; text remains visible."** If it never happened, tick **not seen**. | ☐ PASS ☐ FAIL ☐ not seen |
| 10 | **Pointer.** Open a web page with a search box on the chosen screen. Press **Ctrl+Alt+B**, type `Explain this page and point at the search box`, press **Send**, then straight away close Boo's window with **×** and click once on the web page's title bar. Wait. | A **blue pointer** flies to the search box, then a **peach-coloured ring** is drawn around it. The pointer tip is on the box, not off to one side. Re-open Boo (Ctrl+Alt+B): the status line says **"Screen explanation is shown. Marks are click-through and tied to this observation."** | ☐ PASS ☐ FAIL |
| 11 | Do step 10 again, and while the ring is showing, **scroll** the page. | Pointer and ring disappear at once. | ☐ PASS ☐ FAIL |
| 12 | **Stop by keyboard.** Hold Ctrl+Alt+Space, say "Tell me a short story", let go. While Boo is speaking press **Ctrl+Alt+F12**. | Speech stops at once. The notch shows **"Stopped"** for about 1.6 seconds. In Boo's window, under Talk: **"Microphone stopped. Screen activity is shown in the sharing indicator."** | ☐ PASS ☐ FAIL |
| 13 | **Stop by right-click on the notch** (new in this batch, #185). Ask for another story the same way. While Boo is speaking, move the mouse to the top centre so the notch shows, and **right-click** it. | No menu opens. Speech stops at once and the notch shows **"Stopped"** for about 1.6 seconds, the same as step 12. | ☐ PASS ☐ FAIL |
| 14 | **Stop from the tray and the window.** Ask for another story. While Boo speaks, right-click the tray icon and choose **Stop (Ctrl+Alt+F12)**. Then ask once more, open Boo's window and press its **Stop** button (top right). | Both times: speech stops at once and the notch shows **"Stopped"**. The tray menu lists **Show Boo (Ctrl+Alt+B)**, **Stop (Ctrl+Alt+F12)**, **Settings**, **Quit**. | ☐ PASS ☐ FAIL |

If a shortcut is taken by another app, the notch shows **"Shortcut busy: use tray"** and the tray menu label loses its
shortcut; use the tray **Stop** instead and note it.

## Result to send back

```
Build: ______   Windows: ______   Display scaling: ____%
Steps failed (number + one line each):
Step 9 (silent voice): seen / not seen
Overall: BATCH 1 OK / NOT YET
```

For any FAIL: the step number, one line on what you saw, a phone photo, and any status text Boo showed. Never send
your key or a photo of the key box.
