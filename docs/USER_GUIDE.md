# Boo user guide

Boo is a small Windows companion that lives in a black "pocket" at the top centre of your screen. Two white eyes watch the mouse and blink. Hold a key and talk to it, ask it to explain what is on screen, let it research something with sources, hand it a small task on your PC (always with your approval), or tell it what you are working on and let it keep you on task for 25 minutes.

This guide describes the build on branch `lane/face` (pull request #3). Every statement here was checked against that code. Where a behaviour has not yet been tried on a real PC, the text says so.

Contents

1. [Install](#1-install)
2. [First run and your OpenAI key](#2-first-run-and-your-openai-key)
3. [The notch, the eyes and the full window](#3-the-notch-the-eyes-and-the-full-window)
4. [Hotkeys](#4-hotkeys)
5. [Talking to Boo](#5-talking-to-boo)
6. [Voice commands](#6-voice-commands)
7. [Explain what is on screen](#7-explain-what-is-on-screen)
8. [Research with sources](#8-research-with-sources)
9. [Tasks, approvals and Undo](#9-tasks-approvals-and-undo)
10. [Focus sessions and Developer Mode](#10-focus-sessions-and-developer-mode)
11. [Settings](#11-settings)
12. [Privacy and security](#12-privacy-and-security)
13. [Troubleshooting, message by message](#13-troubleshooting-message-by-message)
14. [FAQ](#14-faq)

---

## 1. Install

Boo ships two ways from the same build: a per-user **installer** (recommended) and a **portable ZIP**. Both come from the `release` workflow. Until the integration PR is merged, pick the newest green run for the branch you are testing (`lane/face`, PR #3); after the merge, the newest green run on `main`.

**Installer.** On GitHub open **Actions > release** in `vikas53953/bluey`, pick the run, and download the **boo-installer-win-x64** artifact. Unzip it and run `Boo-win-Setup.exe`. It installs for the current user only (no admin prompt) into `%LocalAppData%\Boo`, adds **Boo** to the Start menu and to **Settings > Apps** for uninstall, and starts Boo. `Boo-win-Setup.exe.sha256` holds the hash (`Get-FileHash Boo-win-Setup.exe`). Only installed copies can update themselves (see section 11, **Updates**).

**Portable ZIP.**

1. Download the **boo-portable-win-x64** artifact instead. A tagged version (`v*`) is also attached under **Releases**. The windows-ci workflow uploads a plain **boo-win-x64** artifact too.
2. Extract the download. Inside the portable artifact is `Boo-<version>-<commit>-win-x64.zip`, a matching `.sha256` file and a `READ-ME-FIRST.txt`. Extract that inner ZIP to a normal folder, for example `Downloads\Boo`.
3. Optional: check the hash in PowerShell with `Get-FileHash Boo-*.zip` and compare it with the `.sha256` file.
4. Double-click `Boo.exe`. The build is self-contained (no .NET install needed).

Neither build is code-signed, so SmartScreen may warn: choose **More info > Run anyway**.

What to expect: no window opens. Boo starts in the background with a tray icon (tooltip "Boo companion") and the notch at the top centre of the display.

Two rules worth knowing:

- **Extract the whole archive.** Boo refuses to turn on "Start with Windows" when it runs from a temporary folder such as the one Windows uses for files opened straight from a ZIP.
- **One copy at a time.** A second `Boo.exe` started while the first one runs exits silently.

## 2. First run and your OpenAI key

Boo uses your own OpenAI account for voice, screen explanations, research and focus checks. Without a key, typing and the notch still work, but every AI request answers "No OpenAI key yet. Add it in Settings, then try again."

1. Press **Ctrl+Alt+B** to open the full window.
2. In **Settings**, click the **OpenAI key (voice + OpenAI text)** box and paste your key there. Press **Save settings**.
3. Expect the status line "Settings saved. Credential input was protected and cleared from the UI." The box empties itself.
4. In **Settings** also pick the **Display** Boo may look at and your **Microphone**. The top status line changes from "Screen sharing off · display not selected" to the display name.

Where the key goes: `%LOCALAPPDATA%\Boo\credential.bin`, encrypted with Windows Data Protection (DPAPI) for your Windows account only. It is never written to settings, logs or the package, and it is only ever sent as a Bearer header to `https://api.openai.com`. Paste it into Boo only, never into a chat, an email or a file.

Default models (changeable only by editing `settings.json`, there is no UI for them yet):

| Use | Model |
| --- | --- |
| Screen explanation, research, task planning | `gpt-6.1-sol` |
| Voice conversation | `gpt-realtime-2.1`, voice `marin` |
| Focus on-task / off-task checks | `gpt-6-luna` |

## 3. The notch, the eyes and the full window

The notch is a HeyClicky-style pocket, about 84 x 36 px at 100% scaling, black by default with two white "button" eyes. There is deliberately no text on it.

| You do | Boo does |
| --- | --- |
| Move the mouse to the very top edge at the centre of the chosen display | The notch slides down in about a quarter of a second |
| Move the mouse around near it | Pupils follow the cursor; the eyes blink every 3 to 6 seconds |
| Move the mouse away | It waits just under a second, then retracts |
| **Click** the eyes (a short click) | The full window opens |
| **Press and hold** the eyes for longer than 0.2 s | Boo listens; release to send (see section 5) |
| **Right-click** the notch | Same as Stop: capture, audio and pending work stop at once |

Faces you will see: idle, listening (pocket widens with sound bars), thinking, responding, capturing the screen (a wider pocket as a disclosure), nudge (a caption types in and the eyes shake), paused, and happy (eyes become "^" arcs with a small bounce for about 2.5 seconds when a focus session ends).

The **full window** has four areas: **Ask Boo** (type a question or request, press **Send**), **Conversation** (replies, sources and action cards), **Focus**, and **Settings**. Closing it with × hides it; Boo keeps running. The tray menu has **Show Boo (Ctrl+Alt+B)**, **Stop (Ctrl+Alt+F12)**, **Settings** and **Quit**. **Quit** is the only way to exit.

## 4. Hotkeys

| Hotkey | What it does | If another app owns it |
| --- | --- | --- |
| **Ctrl+Alt+B** | Opens (or brings forward) the full window | Use the tray icon's **Show Boo** |
| **Ctrl+Alt+Space** (hold) | Hold to talk. Release sends your words. A tap under 0.2 s sends nothing and shows "Hold to talk". A hold is sent automatically after 120 s | Use **Talk** in the full window, or hold the eyes |
| **Ctrl+Alt+S** | Pause everything: cancels capture, audio and pending work and pauses a running focus session (Riley's Ctrl+Option+S) | Ctrl+Alt+F12 still stops Boo |
| **Ctrl+Alt+F12** | Emergency stop, identical effect to Ctrl+Alt+S. Also cancels a hold in progress so nothing is sent | Use the tray **Stop** or right-click the eyes |

Inside the full window: **Alt+A** approves and **Alt+C** cancels an action card, **Alt+S** saves settings, **Alt+R** resets appearance, **Ctrl+Enter** in **Ask Boo** sends, and **Esc** cancels a pending action card or, with none showing, hides the window. **Tab** moves between controls in the usual Windows order; every control has a screen-reader name. There is no F6 pane-cycling key.

Boo registers the hotkeys at start and tells the truth about them. If one fails, a single sentence appears in Settings, in the status line and on the notch ("Shortcut busy: use tray" or "Shortcut busy: use Talk"), and tray labels drop the dead shortcut. Registration is retried after you unlock the PC or change displays.

Two things Boo does not do: it never uses **Alt+Space** (that is the Windows window-menu key), and it does not type a space into the app you are in while you hold Ctrl+Alt+Space.

Known quirk: on keyboard layouts where AltGr+S types a letter (Polish "ś" for example), Ctrl+Alt+S is taken by Boo while it runs.

## 5. Talking to Boo

There are three ways to speak.

1. **Hold Ctrl+Alt+Space**, talk, release. This is push-to-talk: the mic is open only while you hold.
2. **Hold the eyes** with the left mouse button, talk, release. Same behaviour.
3. Press **Talk** in the full window. This opens the microphone continuously until you press **Stop talk** or Stop. Use it when a hotkey is unavailable.

Boo answers out loud and writes the same words in **Conversation**. If Boo is still speaking when you start a new hold, it stops talking immediately and listens.

Replies are conversational by default. Boo only looks at the screen, researches or proposes an action when you ask for it in words (see sections 7 to 9).

You can **type instead**: put your words in **Ask Boo** and press **Send**. Typing works even when the microphone is unavailable.

Latency diagnostics appear under **Settings > Microphone** after a few hold-to-talk turns: "Voice latency (end of speech → first audio): warm n/20, median … ms, p95 … ms". The target is 20 warm turns with a median of 1.5 s or less and a p95 of 3 s or less.

## 6. Voice commands

While talking, Boo recognises six focus and sleep commands. They go straight to the focus controls without any confirmation prompt. The code behind this was tested, but a live microphone round trip has not yet been tried on a PC.

| Say something like | Boo does | Boo says |
| --- | --- | --- |
| "I'm writing the intro for 30 minutes", "Let me work on the firewall plan" | Starts a focus session with that task. Minutes default to 30, clamped between 1 and 240; the task text is capped at 120 characters | "Focus started: <task> for 30 minutes." |
| "Pause focus" | Pauses a running session | "Focus paused." (or "There is no running focus session to pause.") |
| "Resume" | Resumes a paused session | "Focus resumed." (or "There is no paused focus session to resume.") |
| "End the session", "Stop focusing" | Ends the session | "Focus ended." (or "There is no focus session to end.") |
| "How's my focus going?" | Reports task and minutes left | "Focusing on <task>, 18 minutes left." |
| "Bye", "Be quiet" | Closes the voice session after the reply; a focus session keeps running | "Going quiet." then "Boo went quiet. Activate Talk or hold Boo's eyes to speak again." |

If you ask to start focusing while a session is already running, Boo replies "A focus session is already going (<task>). End it first."

Typed words are also classified. Boo decides what to do from the first words of your request, so you can predict it:

| Your text starts with (or contains) | Kind |
| --- | --- |
| exactly `undo`, `undo that`, `revert`, `put it back`, `change it back` (optionally with "please") | Undo, handled locally |
| `task`, `open`, `create`, `type`, `click`, `edit`, `change`, `set`, `apply` (optionally with "please") | Action, needs approval |
| `find`, `search`, `research`, `look up`, `latest`, `compare`, or contains "with sources", "with links", "cite sources" | Research |
| anything else | Explain the screen or just talk |

## 7. Explain what is on screen

1. Pick a **Display** in Settings first, or Boo says "Choose a display in Settings before asking about the screen."
2. Open the page or app you want explained. Hold Ctrl+Alt+Space and ask, for example, "Show me where the search box is" or "Why is the sign-up button hard to find?" Typing in **Ask Boo** works too.
3. Boo takes one screenshot of that display (its own windows are excluded), explains out loud and in text, and a **blue pointer** flies from your mouse position to the element in about half a second. After it lands a ring or arrow is drawn.
4. The marks are click-through and disappear as soon as you scroll, resize or switch windows.

Boo only draws marks it can verify against the captured frame. If it cannot, it still answers and says why, for example "Boo explained the screen without a verified mark" or "Boo explained the screen without drawing: the display changed. Ask again to see marks."

Pointer landing accuracy at 125% and 150% scaling and on a second monitor is built but not yet proven by hand.

## 8. Research with sources

Type or say something that starts with `research`, `find`, `search`, `look up` or `compare`, or add "with sources": for example `research the best free tools for team retrospectives with sources`.

Boo shows progress, then a short written answer with clickable links under **Sources**. Clicking a link opens your browser. Only absolute HTTP or HTTPS links are shown. If the model returns no sources, the answer is marked "No sources were returned. Treat this answer as unverified."

## 9. Tasks, approvals and Undo

Boo can do a small set of things on your PC, and only when you asked for them in words that classify as an action (section 6). Nothing happens until you press **Approve**.

The catalogue is closed: `search`, `inspect`, `open_url`, `open_app`, `click`, `type_text`, `write_file`, `delete_file`. Typed text is limited to 1,024 visible characters; a written file to 4 MB. File actions only work inside the **Task folder** you choose in Settings and never follow links, junctions or paths outside it.

### Approving an action

1. Bring the target app forward (for example Notepad with some text in it), then press **Ctrl+Alt+B**. Boo remembers the last external window as its target.
2. Type `change the text to Final v2` and press **Send**.
3. A **Review action** card shows the **Requested target** (what the model asked for) and the **Resolved destination**, which is the real window, for example "Notepad window (process 1234)". **Cancel** has the keyboard focus, not Approve.
4. Press **Approve** (or Alt+A). Boo applies the change, reads the field back and reports what it actually contains ("Verified outcome: …"). If it cannot verify, it says so ("The action committed, but Boo could not verify its result.").

If the window changes before you approve, the approval expires: "Approval expired because the target changed; no action was committed." Ask again.

### What Boo refuses

- Typing or clicking into command shells and admin tools: cmd, PowerShell, Windows Terminal, conhost, WSL, bash, mintty, PuTTY, regedit, MMC, Task Manager, installers, UAC and credential prompts. The card shows the reason in red and Approve is disabled.
- Its own windows ("Boo will not act on its own windows.").
- Text with hidden control, bidi or zero-width characters.
- URLs with user-info (`https://bank.example@evil.test`) or non-HTTP schemes.
- Overwriting an existing file without a separate explicit overwrite approval.

### Undo

Type or say exactly `undo` (also `revert` or `put it back`). Boo handles this **locally, without calling the AI**:

1. It re-reads the window it edited.
2. It shows "Undo is ready: 1 step(s) to review before anything changes." and another **Review action** card starting "Undo: ".
3. Only after **Approve** does it restore the original value, then reads it back to confirm.

Undo is deliberately cautious. It refuses when:

- there is no recorded edit: "Nothing to undo: Boo has no recorded edit."
- **the field was empty before Boo's edit**: "<field> was empty before the edit; Boo cannot restore an empty value." Type something in the field first if you want to demo Undo.
- the field changed since Boo's edit: "<field> changed since Boo edited it; Undo would overwrite a newer change."
- the edited window is no longer the target, or its controls cannot be read: "Boo needs a current view of the edited window before it can undo. Nothing was changed."
- more than one Undo control is visible: "More than one Undo control is visible; Boo will not guess."

Undo restores the fields Boo set. It is not a general undo for your app.

## 10. Focus sessions and Developer Mode

A focus session is a timer plus a gentle observer. Boo looks at the chosen display about every 30 seconds, asks the AI whether what it sees relates to your declared task, and nudges you only after sustained off-task activity. Boo never clicks, closes or blocks your apps.

### Start a session

In **Focus**, type what you are working on, set the minutes (1 to 240) and press **Start**. Or just say it to Boo (section 6). Status becomes "Running; monitoring pauses on Stop, lock, sleep, or Pause."

### While it runs

- Short detours are fine. A nudge needs at least two consecutive off-task checks spanning 2 to 4 minutes (the clearer the evidence, the sooner), and never more than one nudge every 2 minutes.
- A **nudge** is a bubble on the notch with a short kind caption (at most 12 words), a shake of the eyes, a two-note chirp (880 to 1320 Hz, unless chirps are muted), and when the distracting text is visible on screen, the blue pointer flies to it and rings it. The bubble offers two pills: **Back to it** dismisses the nudge; **5 min break** pauses the session ("On a 5 minute break. Press Resume when you are back."). Resume is always explicit.
- **Pause** stops screenshots; **Resume** continues; **End** finishes early.
- Ctrl+Alt+S, Ctrl+Alt+F12, locking the PC and sleep all pause the session ("Paused by Stop; Resume is explicit."). Boo also detects a gap of more than 30 seconds with no clock ticks and pauses with "Paused after sleep. Press Resume to continue."
- The running session is saved every 30 seconds. If Boo crashes, the next start says "Your last focus session was interrupted when Boo closed. It is saved as paused in history."

### When it ends

The happy face appears for about 2.5 seconds, the chirp plays once, and the status reads "Session complete. Nice work. It is saved in your history." **Session history** lists each session as a timeline bar: light = on task, red = off task, light grey = uncertain, peach tick = pause, dark grey = not observed. These are periodic estimates, not an exact time audit.

### Developer Mode

The **Developer Mode** checkbox in Focus is for testing. On: "the focus clock runs 4x and checks happen every 10 seconds", and a nudge can arrive after about 20 to 40 real seconds of clear off-task activity. A 6-minute session finishes in about 90 real seconds. A session that used Developer Mode is flagged in history. Turn it off before a real session: "Developer Mode off: real-time clock, checks every 30 seconds."

## 11. Settings

| Setting | Default | Notes |
| --- | --- | --- |
| OpenAI key (voice + OpenAI text) | empty | Your OpenAI key, DPAPI-encrypted in `credential.bin`, cleared from the box after save |
| Text AI provider | OpenAI | **Anthropic Claude** or **Google Gemini** can answer explain, research, nudges and focus checks instead. Voice always uses OpenAI Realtime and the OpenAI key |
| Anthropic Claude key, Google Gemini key | empty | One masked box each; stored in `credential.anthropic.bin` and `credential.gemini.bin` |
| Claude / Gemini model, focus model | blank = default | Blank uses the built-in default ids; type a model id to override |
| Updates: Check GitHub Releases for updates once a day | off | Installed copies only. Needs a GitHub fine-grained token (**Contents: read** on the private repo), stored DPAPI-protected in `update-token.bin`. **Check for updates** checks now; **Install and restart** is the only step that changes files. **Include pre-releases** also offers suffixed tags |
| Display | none | The one display Boo may capture. Required for explain and focus |
| Microphone | default device | Selected device for voice |
| Task folder | none | The only folder file actions may touch |
| Mute speech | off | Replies stay as text only |
| Mute chirps | off | Silences nudge and session-end chirps |
| Reduced motion | off | Calmer notch animation |
| Start Boo with Windows | off | Adds an HKCU Run entry `"…\Boo.exe" --background`. Refused when Boo runs from a temp folder |
| Appearance: body colour, eye colour, notch offset | black, white, 0 | Colours must have at least 3:1 contrast or the save is rejected; **Reset appearance** restores black and white |

Settings are stored in `%LOCALAPPDATA%\Boo\settings.json` and `appearance.json`; focus history in `sessions.db`. Set the `BOO_DATA_DIR` environment variable to move the data folder.

## 12. Privacy and security

This summarises the 8 November security review in `docs/security/2026-11-08-security-review.md`.

**What leaves your PC.** Only requests to `https://api.openai.com` (the origin is pinned), plus `https://api.anthropic.com` or `https://generativelanguage.googleapis.com` when you select that text provider, and `https://github.com` only if you opt in to update checks: your spoken audio while you hold to talk, one screenshot of the chosen display per explain request or focus check, OCR text for nudge pointing, and your typed requests. Responses calls are sent with `store: false`.

**What stays local.** Screenshots and microphone audio are held in memory only and never written to disk. Your keys are DPAPI-encrypted in `credential.bin`, `credential.anthropic.bin`, `credential.gemini.bin` and (for updates) `update-token.bin`. Focus task names and check results live in `sessions.db`. Crash reports go to `%LOCALAPPDATA%\Boo\logs` with keys, Bearer tokens and your profile path redacted.

**Visible disclosure.** The notch shows the capture face while Boo looks at the screen, Windows shows its mic indicator while Boo listens, and the full window's top line says "Screen sharing on · <display>". Boo's own windows are excluded from capture; if Windows cannot guarantee that, capture pauses ("Capture paused: Boo could not confirm UI exclusion.").

**Prompt injection.** Text on screen, OCR, control names and the model's own output are treated as untrusted data. Actions are offered only when *your* words classify as a task, every action needs your click, click and type targets are bound to real UI Automation controls observed at that moment, and the approval card shows the real destination window, not the model's description.

**Residual risks, unchanged.** Any program running as your Windows user could decrypt `credential.bin` (normal for desktop apps). `open_url` is policy-level read-only, so read the URL on the card before approving. Boo cannot tell the Win+R Run dialog apart from File Explorer. The build is not code-signed.

**Screenshots of Boo are black.** Because Boo excludes itself from capture, Snipping Tool shows an empty area where the notch is. Use a phone camera for photos, or set `BOO_QA_CAPTURABLE=1` before starting Boo for testing only (the status line then warns "QA mode: Boo windows are capturable").

## 13. Troubleshooting, message by message

Every message below is the exact text Boo shows. They are grouped by where they appear.

### Setup and key

| Message | Meaning and fix |
| --- | --- |
| No OpenAI key yet. Add it in Settings, then try again. | Paste the key into **OpenAI key (voice + OpenAI text)** and **Save settings**. |
| OpenAI rejected the API key. Paste a valid key in Settings. | HTTP 401. The key is wrong, revoked or from another account. |
| OpenAI denied access for this key or model. Check your OpenAI project permissions. | HTTP 403. The project does not allow the model or endpoint. |
| The configured OpenAI model is not available for this key. Check the models in Settings. | The model name in `settings.json` is not enabled for your account. |
| OpenAI quota is used up. Add credit or raise the limit in your OpenAI billing settings. | Billing hard limit reached. |
| OpenAI is limiting requests. Wait a moment and try again; if it repeats, check your OpenAI billing. | HTTP 429 rate limit. |
| OpenAI is busy with too many requests. Wait a moment and try again. | Server-side overload. Retry. |
| OpenAI is having trouble right now. Try again in a moment. | HTTP 5xx. |
| OpenAI could not accept this request. Try again; if it repeats, check the models in Settings. | HTTP 400. Usually a model mismatch. |
| Can't reach OpenAI. Check your internet connection and try again. | Offline or DNS failure. |
| A secure connection to OpenAI failed. Check your network, proxy, or system clock. | TLS failure, often a proxy or wrong clock. |
| OpenAI took too long to answer. Try again. / OpenAI response timed out. Try again. | Request deadline passed. |
| OpenAI response was interrupted before completion. Try again. | The stream ended early. |
| Couldn't connect to OpenAI voice in time. Check your internet connection and try again. | Realtime connect exceeded its 15 s timeout. |
| Settings are corrupt or unreadable. Delete the settings file and configure Boo again. | Delete `%LOCALAPPDATA%\Boo\settings.json`. Boo starts with defaults meanwhile ("Saved settings could not be read; using safe defaults."). |
| Credential data is corrupt or unreadable. Delete it and configure Boo again. | Delete `credential.bin` and paste the key again. |
| Saved appearance is corrupt or invalid; using default colors until it is replaced. | Reset appearance and save. |
| Appearance was not saved. Check the color details. | Colours need `#RRGGBB` form and at least 3:1 contrast. |
| Settings saved, but Start with Windows was not turned on: Boo is running from a temporary folder… | Extract the ZIP to a real folder, start Boo from there, tick the box again. |

### Shortcuts

| Message | Meaning and fix |
| --- | --- |
| Ctrl+Alt+B is used by another app. Open Boo from the tray icon. | Another program registered the combo. Free it or use the tray. |
| Ctrl+Alt+F12 is used by another app. Stop Boo from the tray icon or by right-clicking Boo's eyes. | Same for the stop key. |
| Ctrl+Alt+Space is unavailable. Use Talk or hold Boo's eyes to speak. | Windows refused the keyboard hook. Hold the eyes or press Talk. |
| Ctrl+Alt+S is used by another app; Ctrl+Alt+F12 still stops Boo. | The pause alias is taken. F12 still works. |
| Shortcut busy: use tray / Shortcuts busy: use Talk | Notch-sized version of the above. |
| Hold to talk | You tapped Ctrl+Alt+Space for under 0.2 s. Hold it while you speak. |

### Microphone and voice

| Message | Meaning and fix |
| --- | --- |
| No microphone found. Connect one and try again; typing still works. | No input device. |
| Microphone access is off. Turn on Settings > Privacy & security > Microphone > Let desktop apps access your microphone. | Windows privacy switch. |
| Microphone is blocked or in use. Close other apps using it, or turn on the privacy setting. | Another app holds the mic exclusively. |
| The selected microphone isn't available. Plug it in or choose another in Settings. | Saved device index no longer exists. |
| The microphone was disconnected. Reconnect it and try again. | Unplugged mid-session. |
| This microphone can't record the audio Boo needs. Choose another in Settings. | Device cannot provide 16-bit PCM. |
| Microphone unavailable. You can still type a request. | Any of the above during a request. |
| Realtime connection stopped; typing remains available. Activate Talk to retry. | Socket closed or keep-alive detected a dead connection. |
| The voice session could not continue. Activate Talk to retry. / The voice turn could not be sent; typing remains available. | Realtime error mid-turn. Press Talk or hold again. |
| Voice turn was not sent. Hold to talk to retry. | Release happened before audio could be committed. |
| Boo went quiet. Activate Talk or hold Boo's eyes to speak again. | You said bye. Not an error. |
| Spoken reply is unavailable; text remains visible. | Audio playback failed or speech is muted. |

### Screen, pointer and capture

| Message | Meaning and fix |
| --- | --- |
| Choose a display in Settings before asking about the screen. | No display selected. |
| Capture paused: Boo could not confirm UI exclusion. / Capture paused: Windows could not exclude Boo's annotation window. No mark was shown. | Windows refused display-affinity exclusion for Boo's windows, so Boo will not capture (it would see itself). Retry; if persistent, note your Windows version. |
| Screen capture failed (…, error 0x…). No frame was sent. | Graphics capture failed. Changing display or scaling and retrying usually helps. |
| Boo explained the screen without a verified mark. / Boo explained the screen but could not draw a verified mark. | The model's mark did not validate against the frame. Ask again. |
| Boo explained the screen without drawing: the display changed. Ask again to see marks. | You changed display or resolution mid-request. |
| Screen controls could not be read. Boo can explain the image; typing actions are unavailable. | UI Automation could not read the target. Explanations work; typing actions need a readable app. |
| The selected window changed. Ask again to use its current screen controls. | You switched windows between request and reply. |
| No stable external window was selected. Bring the window forward, then reopen Boo and retry. | Boo had no target app. Click the app, then Ctrl+Alt+B. |

### Actions and Undo

| Message | Meaning and fix |
| --- | --- |
| Boo will not click or type into <process>: input there can run commands or change system settings. | Shell or admin-tool guard. By design. |
| Boo will not act on its own windows. | Self-target guard. |
| The text contains hidden control characters; Boo only types visible text, tabs and line breaks. | The proposed text had control, bidi or zero-width characters. |
| Text exceeds Boo's bounded 1024-character input limit. | Shorten the text. |
| A user-selected file root is required for this file action. / The selected file root does not exist. | Choose a **Task folder** in Settings. |
| The file path escapes the selected root. / The file path contains a reparse point. | Boo only writes inside the Task folder and never through links. |
| The target file exists; an explicit overwrite approval is required. | Ask again saying you want to overwrite. |
| Approval expired because the target changed; no action was committed. / This approval expired; request a fresh action. | The window, controls or generation changed. Ask again. |
| Boo suggested an action, but no explicit task request authorized it. | Your words did not start with an action verb. Rephrase, for example "change …". |
| Nothing to undo: Boo has no recorded edit. | Nothing to restore. |
| <field> was empty before the edit; Boo cannot restore an empty value. | Undo cannot restore an empty field. Put text in before the edit next time. |
| <field> changed since Boo edited it; Undo would overwrite a newer change. | You or the app changed the field. Boo will not overwrite it. |
| Boo could not read the window's controls, so it cannot plan an Undo. Nothing was changed. | Bring the edited window forward and try again. |

### Focus

| Message | Meaning and fix |
| --- | --- |
| Name the focus task first. / Enter a positive duration in minutes. | Fill both fields. Duration is 1 to 240. |
| Boo could not start focus. | Usually a missing display or key. Check the status line. |
| Paused by Stop; Resume is explicit. | You pressed Ctrl+Alt+S or Ctrl+Alt+F12. Press Resume. |
| Paused. Resume is explicit; unlock/reconnect will not resume it. | PC was locked or slept. Press Resume. |
| Paused after sleep. Press Resume to continue. | A 30 s gap with no ticks was detected. |
| Focus observation unavailable; this interval is unknown. | One check failed (capture or provider). Shown as dark grey in history. |
| Nudge: return to the task when you are ready. Boo did not change your apps. | A nudge fired. Not an error. |
| Focus history is unavailable. / Focus history is corrupt or unreadable. Delete the history database and try again. | Delete `sessions.db` (you lose past sessions). |
| Focus history uses a newer schema. Update Boo before opening this history. | You started an older Boo against a newer database. |
| Your last focus session was interrupted when Boo closed. It is saved as paused in history. | Recovery after a crash. Start a new session. |

### Crashes

| Message | Meaning and fix |
| --- | --- |
| Boo recovered from a problem. Details are in the logs folder. | A UI error was caught and Boo kept running (up to 5 per minute). Look in `%LOCALAPPDATA%\Boo\logs`. |
| Boo hit a problem and stopped what it was doing. Details are in the logs folder. | Same, after a previous crash was recorded. Capture, audio and pending work were stopped first. |
| QA mode: Boo windows are capturable (BOO_QA_CAPTURABLE=1). | You set the test variable. Unset it for normal use. |

To send a diagnostics bundle, run `scripts/Collect-BooDiagnostics.ps1` from the repository. It never copies `credential.bin`.

## 14. FAQ

**Does Boo need the cloud?** Yes, for anything it says or decides: OpenAI handles voice, explanations, research and focus checks. The notch, hotkeys, approvals and Undo planning run locally.

**Does it listen all the time?** No. The microphone opens only while you hold Ctrl+Alt+Space or the eyes, or between Talk and Stop talk. Windows shows its mic indicator whenever it is open.

**Does it watch my screen all the time?** No. One screenshot per explain request, and one every 30 seconds during a focus session only. The notch shows the capture face each time.

**Can Boo do things on my PC without asking?** No. Every click, keystroke, file write or URL open goes through a Review action card and your Approve. It will not type into terminals or admin tools even if you approve.

**Why is Undo refusing?** Most often because the field was empty before Boo's edit, or because you typed in it afterwards. Undo only restores values Boo itself set.

**Why does my screenshot of Boo show a black box?** Boo excludes its windows from capture so the AI never sees itself. Use a phone camera for photos.

**Where is my data?** `%LOCALAPPDATA%\Boo`: `settings.json`, `appearance.json`, `credential.bin` (encrypted key), `sessions.db` (focus history), `logs\` (redacted crash reports).

**How do I quit?** Tray icon > **Quit**. Closing the window only hides it.

**Can I use another AI provider?** Not yet. OpenAI is the only provider wired in; the provider sits behind an interface so others can be added later.

**Can I change the colours?** Yes, in **Appearance**: body and eye colours as `#RRGGBB`, at least 3:1 contrast, plus a vertical notch offset. **Reset appearance** returns to black and white.

**What has not been tried on a real PC yet?** Live voice round trip and latency, pointer accuracy at 125/150% scaling and on a second monitor, the audible chirp, a full 25-minute native focus session, and the complete Apply-then-Undo demo. The acceptance runbook in `docs/verification/2026-11-08-acceptance-runbook.md` is the checklist for proving them.
