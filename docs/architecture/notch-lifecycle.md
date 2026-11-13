# Notch lifecycle: how Boo behaves today

Agent-trust plan, Phase 2, PR 2.1 (`docs/plans/agent-trust-plan.md` §3.2 A). This is a map of the states and events of
the notch and the features that drive it, **as the code on `lane/face` at `af089dd` behaves**, written by reading the
code. It changes no behaviour. Later Phase 2 PRs (the TLA+ model, the `NotchLifecycle` reducer, the property tests)
start from this map, so any change to it must be agreed **before** behaviour moves.

How to read the labels:

- **code**: confirmed by reading the cited lines. Every transition cites `file:line` (paths from the repo root).
- **live**: also seen in the real-key live run [43339557570](https://github.com/vikas53953/bluey/actions/runs/43339557570)
  (PR #13 QA head `3168e40`, journeys j0–j6, artifact `live-ai-evidence`). Line numbers are from `af089dd`; the
  lifecycle code the run exercised did not change between the two heads (only docs and CI files did).
- **UNVERIFIED**: could not be confirmed from code alone, or depends on timing/threads no run has shown. Never guessed.

---

## 1. The big picture (one paragraph)

There is **no single state machine** today. The notch face is a **pure function of flags**, recomputed whenever a flag
changes:

1. The main window (`MainWindow`) owns app flags: `Thinking`, `Responding`, `NudgeShowing`, `FocusPaused`, `Happy`
   (`SetFace`, `src/Boo.App/MainWindow.xaml.cs:152-173`).
2. The notch window (`NotchWindow`) owns `hovered`, `captureActive` and `microphoneActive`
   (`src/Boo.App/NotchWindow.xaml.cs:348-359`, `:1367-1379`).
3. `FaceStateResolver.Resolve` picks one of 10 face states by fixed priority (`src/Boo.Core/NotchFace.cs:38-53`).
4. Any non-Hidden result **pins** the notch out; a reveal timeline (`NotchRevealTimeline`, `src/Boo.Core/NotchFace.cs:294-406`)
   slides it in and out (`src/Boo.App/NotchWindow.xaml.cs:499-522`).

Around that sit small, separate machines with their own state: hold-to-talk, focus sessions, request generations,
the approval card, the two undo journals and the pointer/ring sequencer. Section 3 has one diagram and table for each.

```mermaid
flowchart LR
  subgraph MainWindow["MainWindow (panel)"]
    Chat["Send / Reply / voice events"] --> Flags["SetFace flags:<br/>Thinking, Responding,<br/>NudgeShowing, FocusPaused, Happy"]
    Focus["Focus session"] --> Flags
    Stop["StopAll"] --> Flags
    Mic["mic on/off"]
    Cap["capture start/end"]
  end
  subgraph NotchWindow
    Hover["mouse enter/leave"] --> R
    Flags -->|"App.OnFaceInputsChanged → UpdateFace"| R["Recompute"]
    Mic -->|SetMicrophoneActivity| R
    Cap -->|SetCaptureActivity| R
    R --> Resolver["FaceStateResolver<br/>(priority)"]
    Resolver -->|"not Hidden ⇒ pinned"| Timeline["NotchRevealTimeline<br/>(RevealPhase)"]
    Timeline --> Resolver
    Resolver --> Face["NotchFaceState<br/>(10 values, read by UI Automation)"]
  end
```

Wiring: `src/Boo.App/App.xaml.cs:72-85` (panel created at startup, notch created, `FaceInputsChanged`,
`EyesHeldChanged`, `NudgeActionChosen`), `src/Boo.App/App.xaml.cs:140-168` (capture/mic callbacks, `UpdateFace`).

## 2. The plan's states mapped to today's code

The plan (§3.2 A) names 11 abstract states. Today they are not one variable; this is where each one lives.

| Plan state | Today | Where |
| --- | --- | --- |
| `Hidden` | `NotchFaceState.Hidden` with `RevealPhase.Hidden` | `src/Boo.Core/NotchFace.cs:52`, `:353-355` |
| `Hover` (peek) | `NotchFaceState.Hover` = pointer is over the notch and no higher flag | `src/Boo.Core/NotchFace.cs:50`; `src/Boo.App/NotchWindow.xaml.cs:1367-1372` |
| `Awake` (idle, panel ready) | **No such notch state.** `NotchFaceState.Idle` only means "revealed, pointer not on it"; in practice it is the short tail on the way out (retract delay). Whether the panel is open does not change the notch face (one exception: if the selected display is gone, opening the panel runs Stop, F21). | `src/Boo.Core/NotchFace.cs:51`; `src/Boo.App/App.xaml.cs:103-138` (opening the panel calls `RefreshSelectedDisplay`, `src/Boo.App/MainWindow.xaml.cs:600`, which re-places the notch, refreshes the capture indicator, and runs `StopAll` when the selected display is gone, `:612-614`). Note: the journey harness comment "The open panel keeps the face awake" (`tools/Boo.VerifyLive/LiveJourneyTests.cs:79`) is not supported by the code; no `SetFace` call depends on the panel |
| `Chat` (thinking → responding) | `Thinking` flag, then `Responding` flag held as a timed "moment" | `src/Boo.App/MainWindow.xaml.cs:743`, `:953`, `:2258`, `:2281`, `:2301-2302` |
| `Listening` | `microphoneActive` in the notch (the app-level `FaceInputs.Listening` flag is never set by any caller) | `src/Boo.App/NotchWindow.xaml.cs:355-359`, `:505` |
| `Capture` | `captureActive` in the notch, from the capture tracker (the app-level `FaceInputs.CaptureActive` is never set) | `src/Boo.App/MainWindow.xaml.cs:702-711`; `src/Boo.App/NotchWindow.xaml.cs:348-353`, `:504` |
| `Interrupt` (Stop) | **No state.** `StopAll` is a function that resets flags in one call; the notch only flashes a "Stopped" screen-reader status for a hotkey Stop, or for any Stop while no panel exists | `src/Boo.App/MainWindow.xaml.cs:265-313`; `src/Boo.App/NotchWindow.xaml.cs:318-334`, `:1650-1656`; `src/Boo.App/App.xaml.cs:194-198` |
| `Focus` (running / paused / nudge / ended) | `FocusStatus` `Running`/`Paused`/`Ended` in Core; on the notch `Paused`, `Nudge` (6 s moment), `Happy` (2.5 s moment). A running session with no nudge shows nothing on the notch except its periodic `Capture`. | `src/Boo.Core/Contracts.cs:11`; `src/Boo.App/MainWindow.xaml.cs:1546-1550`; `src/Boo.App/MainWindow.Focus.cs:43` |
| `ApplyPending` | The approval card in the **panel** (`ActionPreview` visible, `_pendingPreview` set). No notch state. | `src/Boo.App/MainWindow.xaml.cs:1118-1133` |
| `Applied` | An undo journal holds the edit | `src/Boo.App/MainWindow.Edits.cs:243-245`, `:307`, `:504-533` |
| `Undone` | The field journal is cleared after a verified undo; the file journal records the undo as a new edit (see finding F6) | `src/Boo.Core/UndoJournal.cs:197-213`; `src/Boo.App/MainWindow.Edits.cs:504-533` |

## 3. The machines, one by one

### 3.1 Reveal timeline (`RevealPhase`, 5 values)

`src/Boo.Core/NotchFace.cs:285` (enum), `:294-406` (logic). Shipped timings: reveal 240 ms, retract delay 900 ms,
retract 160 ms (`docs/design/boo-notch-spec.json:255-259`). "Pinned" = the face would be non-Hidden even without the
pointer (`src/Boo.App/NotchWindow.xaml.cs:510-512`).

```mermaid
stateDiagram-v2
  [*] --> Hidden
  Hidden --> Revealing: PointerEnter / Pin
  Revealing --> Revealed: 240 ms
  Revealed --> RetractPending: PointerLeave (not pinned) / Unpin (pointer outside)
  Revealing --> RetractPending: left during reveal, reveal finishes first
  RetractPending --> Revealed: PointerEnter / Pin (retract cancelled)
  RetractPending --> Retracting: 900 ms
  Retracting --> Revealing: PointerEnter / Pin (from current progress)
  Retracting --> Hidden: 160 ms
```

| # | From → To | Event | Cite | Label |
| --- | --- | --- | --- | --- |
| R1 | Hidden → Revealing | Pointer enters the notch strip | `src/Boo.App/NotchWindow.xaml.cs:1367-1371` → `src/Boo.Core/NotchFace.cs:311-315`, `:380-382`, `:392`, `:347` | code, live (j1 `revealed=True`; the harness reads the face state, not the reveal phase) |
| R2 | Revealing → Revealed | 240 ms pass | `src/Boo.Core/NotchFace.cs:347`, `:395-396` | code |
| R3 | Revealed → RetractPending | Pointer leaves and nothing pins | `src/Boo.App/NotchWindow.xaml.cs:1374-1378` → `src/Boo.Core/NotchFace.cs:317-324`, `:351` | code, live (j1 `retracted=True`; face state, the phase is inferred) |
| R4 | Revealing → RetractPending | Pointer left mid-reveal: the reveal finishes, then the 900 ms delay starts from whichever is later (leave or reveal end) | `src/Boo.Core/NotchFace.cs:349`, `:398-403` | code |
| R5 | RetractPending → Retracting | 900 ms pass | `src/Boo.Core/NotchFace.cs:350-352` | code |
| R6 | Retracting → Hidden | 160 ms pass | `src/Boo.Core/NotchFace.cs:352-353` | code, live (j1 `retracted=True`) |
| R7 | RetractPending → Revealed | Pointer re-enters (or a pin arrives): retract cancelled, reveal kept | `src/Boo.Core/NotchFace.cs:383-392` (comment `:389`) | code |
| R8 | Retracting → Revealing | Pointer re-enters (or pin): reveal restarts from the current progress | `src/Boo.Core/NotchFace.cs:387-388` | code |
| R9 | any → Revealing/Revealed | Pin: a face flag above Hidden appears (capture, mic, thinking, nudge, …) | `src/Boo.App/NotchWindow.xaml.cs:510-512` → `src/Boo.Core/NotchFace.cs:327-334` | code, live (j6 `Hidden>Capture`) |
| R10 | Revealed → (stays) | Pointer leaves while pinned: no retract | `src/Boo.Core/NotchFace.cs:320` | code |
| R11 | Revealed → RetractPending | Unpin while the pointer is outside: the 900 ms delay starts at the unpin | `src/Boo.Core/NotchFace.cs:335-339` | code, live (j6 `Capture>Idle>Hidden`) |

Notes (code): the internal mode never goes back to `Mode.Hidden`; after a retract it stays `Leaving` and `PhaseAt`
reports `Hidden` (`src/Boo.Core/NotchFace.cs:348-353`); a later `Show` from that point restarts the reveal from 0
(`:384-386`). Phase changes caused only by time are picked up by the render loop, which recomputes the face when the
phase and face disagree about Hidden (`src/Boo.App/NotchWindow.xaml.cs:639-659`); the loop keeps running while the
phase is not Hidden (`src/Boo.Core/RenderBudget.cs:12-13`, `src/Boo.App/NotchWindow.xaml.cs:669-674`).

### 3.2 Face state (`NotchFaceState`, 10 values)

`src/Boo.Core/NotchSpec.cs:15` (enum). Priority, highest first (`src/Boo.Core/NotchFace.cs:22-53`):
**Capture > Listening > (UserHidden ⇒ Hidden) > Responding > Thinking > Nudge > Happy > Paused > Hover > Idle > Hidden.**
The face is recomputed on every flag change (`src/Boo.App/NotchWindow.xaml.cs:499-522`) and published to UI Automation
(`src/Boo.App/NotchWindow.xaml.cs:524-529`, `:1269`). `UserHidden` exists in the resolver but **no caller sets it**
(searched `src/`, only `src/Boo.Core/NotchFace.cs:18`, `:44`).

**Capture lasts through the AI call, not only the screenshot.** On a typed question answered on the plain-text path, a voice `screen_help` tool and a focus
check, the capture operation is disposed in a `finally` after the AI answers (`src/Boo.App/MainWindow.xaml.cs:2029`,
`:822`, `:845`; `:2394`, `:2432-2434`; `:1528`, `:1582`). Because Capture outranks Thinking, the Thinking face is not
seen during those calls; the notch shows Capture until the answer (or the cancel) arrives. If no frame is taken, the
operation ends at once (`:752-755`). When a typed question is handed to the voice session instead (speech on), the
`finally` runs as soon as the text is sent (`:797-801`, `:843-845`), so Capture ends before the reply and Thinking stays
visible while waiting (`:846`).

```mermaid
stateDiagram-v2
  [*] --> Hidden
  Hidden --> Hover: PointerEnter
  Hover --> Idle: PointerLeave
  Idle --> Hidden: retract (900 + 160 ms)
  Hidden --> Thinking: Send / voice turn committed / tool call
  Thinking --> Responding: Reply / audio delta
  Thinking --> Idle: ReplyFailed / cancelled / Stop
  Responding --> Idle: moment expires (4 s, or playback tail) / Stop
  Hidden --> Listening: mic on (TalkDown, eyes held, Talk button)
  Listening --> Thinking: mic off, then the server reports speech stopped / committed
  Listening --> Idle: mic off (Cancel / Stop)
  Hidden --> Capture: capture starts (Send, voice tool, focus check)
  Capture --> Idle: capture ends
  Hidden --> Nudge: focus check says drifting
  Nudge --> Idle: 6 s / Back to it
  Nudge --> Paused: 5 min break
  Hidden --> Paused: focus paused (Pause, Stop, sleep, disconnect)
  Paused --> Idle: Resume / Start
  Paused --> Happy: End
  Hidden --> Happy: focus session ends
  Happy --> Idle: 2.5 s
```

"→ Idle" above means "the flag clears"; the face then falls to the next flag by priority, or to Idle → Hidden through
the reveal timeline when nothing else is set. Every arrow is in the table:

| # | Event | Flag change | Cite | Label |
| --- | --- | --- | --- | --- |
| F1 | Pointer enters | `hovered = true` → `Hover` (if nothing higher) | `src/Boo.App/NotchWindow.xaml.cs:1367-1371`; `src/Boo.Core/NotchFace.cs:50` | code, live (j1 `state='Hover'`) |
| F2 | Pointer leaves | `hovered = false` → `Idle` while the timeline is not Hidden, then `Hidden` | `src/Boo.App/NotchWindow.xaml.cs:1374-1378`, `:515`, `:654-659`; `src/Boo.Core/NotchFace.cs:51-52` | code, live (j1 `retracted=True`) |
| F3 | Send (typed question) | `Thinking = true`, `Responding = false`. Every Send first drops any old voice session (`BeginRequest`). With speech on (the default, `src/Boo.Core/Contracts.cs:36`) and not muted, Send then opens a new voice session and hands the text to it; the voice rows F7-F10b take over. The plain-text path (F4) runs when speech is off or muted, or when that connect fails with `InvalidOperationException` / `HttpRequestException`; that includes the 15 s connect timeout and classified auth/quota/server failures, which are `ProviderException`, a kind of `InvalidOperationException`. Only a cancel, or a failure sending the text after connect, ends in F6 | `src/Boo.App/MainWindow.xaml.cs:728-743`, `:766-801` (filter `:779`), `:836`, `:1964-1974`; `src/Boo.Core/ProviderFailure.cs:32`; `src/Boo.Providers/RealtimeClient.cs:358-372`, `:384-390` | code, live for the speech-off branch only (j2) |
| F4 | Reply (typed, plain-text path) | `Thinking = false`, `Responding = true` held 4 s | `src/Boo.App/MainWindow.xaml.cs:950-953` | code, live (j2 reply `'Paris'`) |
| F5 | Responding moment expires | `Responding = false` by the moment timer | `src/Boo.App/MainWindow.xaml.cs:175-202`; `src/Boo.Core/FaceMomentSchedule.cs:43-50` | code |
| F6 | Reply failed / cancelled / stale (typed) | `Thinking = false` only if the request is still current **and the text was not handed to the voice session** (`!handedToRealtime`); a newer request or Stop owns the face otherwise | `src/Boo.App/MainWindow.xaml.cs:829-850` (condition `:846`) | code |
| F7 | Voice: speech started / stopped / committed | `Thinking = false` on started, `true` on stopped/committed | `src/Boo.App/MainWindow.xaml.cs:2252-2259` | code |
| F8 | Voice: audio delta | `Responding = true`, 4 s moment refreshed on every delta | `src/Boo.App/MainWindow.xaml.cs:2278-2281` | code |
| F9 | Voice: `response.done` | `Thinking = tools in flight > 0`; `Responding` held for the remaining playback (`FaceMoments.AfterReplyDone`), only if Responding is already on (otherwise `SetFace` drops the hold) | `src/Boo.App/MainWindow.xaml.cs:2297-2302`, `:161-165`; `src/Boo.Core/FaceMoments.cs:22-30` | code |
| F10 | Voice: disconnect / error | `StopListening` clears `Thinking` and `Responding`; a running focus session is paused (`FocusPaused = true`) | `src/Boo.App/MainWindow.xaml.cs:2304-2315`, `:945-947` | code |
| F10b | Voice tool `go_to_sleep` | after the spoken reply finishes (or an 8 s fallback) the voice session ends through `StopListening`: Thinking and Responding cleared, mic off; focus untouched | `src/Boo.App/MainWindow.Voice.cs:64-66`, `:76-81`, `:84-99`, `:104`, `:124-137` | code |
| F11 | Voice tool call (`screen_help`) | `Thinking = true`; its answer goes through `RenderReply` (`Thinking = false`, `Responding = true`, as F4); later reply events also clear it; every voice undo resets it in its `finally` if the request is still current (`src/Boo.App/MainWindow.Edits.cs:178`) | `src/Boo.App/MainWindow.xaml.cs:2364-2366`, `:2405`; `src/Boo.App/MainWindow.Edits.cs:174-182` | code |
| F12 | Microphone on | notch `microphoneActive = true` → `Listening` | `src/Boo.App/MainWindow.Talk.cs:260-261`; `src/Boo.App/MainWindow.xaml.cs:884-885`; `src/Boo.App/NotchWindow.xaml.cs:355-359`, `:505`; `src/Boo.Core/NotchFace.cs:43` | code |
| F13 | Microphone off | `microphoneActive = false` | `src/Boo.App/MainWindow.Talk.cs:396-397`; `src/Boo.App/MainWindow.xaml.cs:297`, `:895`, `:926` | code |
| F14 | Capture starts | tracker gets an active display → `SetCaptureActivity(true)` → `Capture` | `src/Boo.App/MainWindow.xaml.cs:2029-2030` (typed / voice tool), `:1598-1599` (focus check), `:702-711`; `src/Boo.App/NotchWindow.xaml.cs:348-353`, `:504`; `src/Boo.Core/NotchFace.cs:42` | code, live (j6 `Hidden>Capture` ×N) |
| F15 | Capture ends | operation disposed → indicator refresh | `src/Boo.App/MainWindow.xaml.cs:713-717`, `:845`, `:1582`; `src/Boo.Core/CaptureActivityTracker.cs:57-72` | code, live (j6 `Capture>Idle`) |
| F16 | Focus check says drifting | `NudgeShowing = true` held 6 s, caption set | `src/Boo.App/MainWindow.xaml.cs:1541-1550` | code (j6 `nudges=0`, so not live) |
| F17 | Nudge "Back to it" / "5 min break" | `NudgeShowing = false`; break also pauses focus | `src/Boo.App/NotchWindow.xaml.cs:1027`, `:1033` → `src/Boo.App/App.xaml.cs:84` → `src/Boo.App/MainWindow.xaml.cs:1384-1392` | code |
| F18 | Focus session ends (completed or End) | `FocusPaused = false`, `Happy = true` held 2.5 s | `src/Boo.App/MainWindow.Focus.cs:22-46` | code, live (j6 `>Happy`, `sawHappy=True`) |
| F19 | Focus paused (Pause, Stop, sleep gap, tick error, provider disconnect) | `FocusPaused = true` | `src/Boo.App/MainWindow.xaml.cs:1375`, `:312`, `:1457`, `:1487`, `:2314` | code |
| F20 | Focus Start / Resume | `FocusPaused = false` (Start also `Happy = false`) | `src/Boo.App/MainWindow.xaml.cs:1353`, `:1414` | code |
| F21 | **Stop** (Ctrl+Alt+F12, Ctrl+Alt+S, tray Stop, panel Stop, right-click on the notch, lock/logoff, sleep, update restart, quit; and internally: selected display gone, annotation window cannot be hidden from capture, colour chooser opened while active, crash handlers) | replaces all app flags with `FocusPaused = (focus is paused)`: clears Thinking, Responding, Nudge, Happy and the caption; mic off; capture indicator refreshed | `src/Boo.App/MainWindow.xaml.cs:265-313`; sources: `src/Boo.App/NotchWindow.xaml.cs:1650-1656`, `:1381-1385`; `src/Boo.App/App.xaml.cs:188-199`, `:210`, `:225-229`, `:237-241`; `src/Boo.App/MainWindow.xaml.cs:1915`; update restart `src/Boo.App/MainWindow.Updates.cs:220`; quit `src/Boo.App/MainWindow.xaml.cs:315-318`; internal `:612-614`, `:1029`, `:1886`; crash handlers `src/Boo.App/App.xaml.cs:310`, `:315`, `:348`, `:355` | code |
| F22 | Hotkey Ctrl+Alt+B | opens the panel; **no notch flag changes** (except Stop when the selected display is gone, see the `Awake` row in section 2) | `src/Boo.App/NotchWindow.xaml.cs:1643-1649` → `src/Boo.App/App.xaml.cs:75`, `:103-138` | code; live for the panel (j0); the notch face is not logged, but the notch capture `j0-after-hotkey.boo-0.png` is all black, consistent with a collapsed notch but thin evidence |
| F23 | Click on the eyes (< 200 ms) | opens the panel (same as F22) | `src/Boo.App/NotchWindow.xaml.cs:1322-1335`, `:1307-1311` | code |
| F24 | Press and hold the eyes (≥ 200 ms) | `EyesHeldChanged(true)` → hold-to-talk (§3.3) | `src/Boo.App/NotchWindow.xaml.cs:1313-1320`, `:1351-1365`; `src/Boo.Core/NotchFaceGeometry.cs:58`; `src/Boo.App/App.xaml.cs:83` | code |

### 3.3 Hold-to-talk (`HoldToTalkController`)

Pure Core machine, time from an injected `TimeProvider` (`src/Boo.Core/HoldToTalk.cs:43-72`). Two hold sources (the
Ctrl+Alt+Space chord from a low-level keyboard hook, and holding the eyes); listening ends when the **last** holder
lets go. The panel's **Talk** button is a separate toggle path (`src/Boo.App/MainWindow.xaml.cs:853-908`) that does
not use this controller.

```mermaid
stateDiagram-v2
  [*] --> Idle
  Idle --> Listening: TalkDown (chord) / EyesPressed  [StartListening, or Interrupt+Start if a reply is playing]
  Listening --> Listening: one holder released, other still held
  Listening --> Idle: last release ≥ 200 ms [StopAndSend]
  Listening --> Idle: last release < 200 ms [Cancel + "Hold to talk"]
  Listening --> Idle: Stop / lock / sleep [Cancel; later releases ignored]
  Listening --> Idle: held > 120 s [StopAndSend]
```

| # | From → To | Event | Command | Cite | Label |
| --- | --- | --- | --- | --- | --- |
| H1 | Idle → Listening | fresh Ctrl+Alt+Space | `StartListening` (or `Interrupt`, `StartListening` while a reply plays) | `src/Boo.Core/HoldToTalk.cs:81-98`, `:176-187` | code |
| H2 | Idle → Listening | eyes held ≥ 200 ms | same | `src/Boo.Core/HoldToTalk.cs:124-133`; glue `src/Boo.App/MainWindow.Talk.cs:35-44` | code |
| H3 | Listening → Listening | one source released, the other still held | none | `src/Boo.Core/HoldToTalk.cs:120`, `:144` | code |
| H4 | Listening → Idle | last release after ≥ 200 ms | `StopAndSend` | `src/Boo.Core/HoldToTalk.cs:105-121`, `:136-145`, `:189-200` | code |
| H5 | Listening → Idle | last release before 200 ms | `Cancel`, hint "Hold to talk" | `src/Boo.Core/HoldToTalk.cs:197-198` | code |
| H6 | Listening → Idle | Stop, session lock, suspend | `Cancel`; both holds cleared so later releases do nothing | `src/Boo.Core/HoldToTalk.cs:153-160`, `:202-213`; glue `src/Boo.App/MainWindow.Talk.cs:86`, `:113-127`; `src/Boo.App/MainWindow.xaml.cs:267` | code |
| H7 | Listening → (as H3/H4/H5) | the eyes lose mouse capture | the notch raises `EyesHeldChanged(false)`, i.e. an eye release. `HoldToTalkController.FocusLost` (`src/Boo.Core/HoldToTalk.cs:151`) is reachable only through `MainWindow.TalkFocusLost` (`src/Boo.App/MainWindow.Talk.cs:47`), which **nothing calls**: focus loss itself is not wired | `src/Boo.App/NotchWindow.xaml.cs:1342-1349` | code |
| H8 | Listening → Idle | held longer than 120 s (250 ms ticker) | `StopAndSend` | `src/Boo.Core/HoldToTalk.cs:163-174`; `src/Boo.App/MainWindow.Talk.cs:57-58`, `:196-208` | code |

What the shell does with each command (`src/Boo.App/MainWindow.Talk.cs:170-189`): `StartListening` starts a new
request generation unless it can reuse the open voice session, opens the microphone **before** connecting and only
then lights the mic indicator (`:225-276`); `StopAndSend` turns the mic off first, then commits the turn if at least
4,800 bytes were captured (`:299-330`); `Cancel` turns the mic off and discards the audio (`:352-368`). A newer hold
that supersedes one still connecting only discards the old turn's audio (`:278-284`). A mic or realtime failure ends
the hold and the voice session (`:405-421`).

### 3.4 Focus session (`FocusSessionController` / `FocusSession`)

`FocusStatus { Running, Paused, Ended }` (`src/Boo.Core/Contracts.cs:11`).

```mermaid
stateDiagram-v2
  [*] --> Running: FocusStart
  Running --> Paused: Pause / Stop / sleep gap / tick error / provider disconnect / 5 min break
  Paused --> Running: Resume (only)
  Running --> Ended: Tick reaches duration [SessionEnded(Completed)]
  Running --> Ended: End [SessionEnded(EndedByUser)]
  Paused --> Ended: End [SessionEnded(EndedByUser)]
  Running --> Ended: FocusStart again (old session EndQuietly, no event)
  Paused --> Ended: FocusStart again (old session EndQuietly, no event)
  Ended --> [*]
```

| # | From → To | Event | Cite | Label |
| --- | --- | --- | --- | --- |
| Fo1 | none → Running | Start: task not blank and minutes > 0 are checked; the duration is then clamped to 1–240 min. Also reachable by the voice tool `focus_start` | `src/Boo.App/MainWindow.xaml.cs:1315-1354` (checks `:1317`, `:1322`; clamp `:1342`); `src/Boo.Core/FocusSession.cs:22-25`; voice `src/Boo.App/MainWindow.Voice.cs:45-54` | code, live (j6 `minutes=25`) |
| Fo2 | Running/Paused → Ended (quiet) | Start while a session is live: old one ends with **no** end event | `src/Boo.App/MainWindow.xaml.cs:1333-1341`; `src/Boo.Core/FocusSessionController.cs:152-159` | code |
| Fo3 | Running → Paused | Pause button; Stop; clock gap (sleep); tick exception; provider disconnect; nudge "5 min break" | `src/Boo.App/MainWindow.xaml.cs:1361-1376`, `:303-310`, `:1448-1458`, `:1481-1487`, `:2308-2314`, `:1387-1389`; voice `focus_pause` `src/Boo.App/MainWindow.Voice.cs:55-57` (refused while a session is live in any voice session that is not push-to-talk, i.e. Talk button or a typed question handed to voice, `src/Boo.Core/VoiceCommands.cs:193-194`, `src/Boo.App/MainWindow.Voice.cs:37`); `src/Boo.Core/FocusSession.cs:62-70`; `src/Boo.Core/FocusSessionController.cs:128-132` | code |
| Fo4 | Paused → Running | Resume only, by button or voice `focus_resume` (unlock and reconnect never resume) | `src/Boo.App/MainWindow.xaml.cs:1394-1414`; `src/Boo.App/MainWindow.Voice.cs:58-60`; `src/Boo.Core/FocusSession.cs:72-76` | code |
| Fo5 | Running → Ended | Tick reaches the planned duration (4× clock in Developer Mode) | `src/Boo.App/MainWindow.xaml.cs:1460`; `src/Boo.Core/FocusSessionController.cs:80-92`; `src/Boo.Core/FocusSession.cs:49-60` | code, live (j6 `Completed`) |
| Fo6 | Running/Paused → Ended | End button or voice `focus_end` (voice refused while a session is live in any non-push-to-talk voice session, `src/Boo.Core/VoiceCommands.cs:193-194`, `src/Boo.App/MainWindow.Voice.cs:37`) | `src/Boo.App/MainWindow.xaml.cs:1422-1431`; `src/Boo.App/MainWindow.Voice.cs:61-63`; `src/Boo.Core/FocusSessionController.cs:137-144` | code |
| Fo7 | (Ended) | `SessionEnded` raised at most once → Happy face, chirp, save | `src/Boo.Core/FocusSessionController.cs:161-168`; `src/Boo.App/MainWindow.Focus.cs:22-46` | code, live (j6 `sawHappy=True`) |
| Fo8 | Running → Running | Check due (every 30 s, 10 s in Developer Mode, paused time not counted) → capture → classify → maybe Nudge | `src/Boo.App/MainWindow.xaml.cs:1497-1558`; `src/Boo.Core/FocusSessionController.cs:74`, `:98-111` | code, live (j6: 35 `Hidden>Capture>Idle` cycles in `states=`) |

### 3.5 Request generations (`RequestCoordinator`)

Every user request (typed Send, Talk, a new hold) gets a new **generation**; anything from an older generation is
dropped.

| # | Event | Effect | Cite | Label |
| --- | --- | --- | --- | --- |
| G1 | New request (`BeginRequest`) | stops listening, drops the old voice session, cancels the old token, new generation, clears annotations and the approval card | `src/Boo.App/MainWindow.xaml.cs:1962-1988`; `src/Boo.Core/RequestCoordinator.cs:11-22` | code |
| G2 | Stop | no generation is current until the next Begin | `src/Boo.App/MainWindow.xaml.cs:268-274`; `src/Boo.Core/RequestCoordinator.cs:24-36` | code |
| G3 | Voice session ends (`StopListening` with a voice session) | same as Stop for the coordinator, plus generation bump, approval card cleared | `src/Boo.App/MainWindow.xaml.cs:927-936` | code |
| G4 | Any async result arrives | applied only if `IsCurrent(generation)` | `src/Boo.App/MainWindow.xaml.cs:1990-1997`; `src/Boo.Core/RequestCoordinator.cs:38-44`, `:79-81` | code |
| G5 | A desktop action commits | the side effect runs inside `TryCommit` under the coordinator's lock, so Stop and a commit cannot interleave | `src/Boo.Windows/DesktopTools.cs:130`, `:165`, `:223`, `:287`, `:332`, `:384`, `:467`; `src/Boo.Core/RequestCoordinator.cs:46-62`; wiring `src/Boo.App/MainWindow.xaml.cs:2209` | code |

### 3.6 Approval card (Propose → Approve / Reject)

```mermaid
stateDiagram-v2
  [*] --> NoProposal
  NoProposal --> ApplyPending: Propose (reply with actions to an explicit task) / Undo planned
  ApplyPending --> NoProposal: Reject (Cancel, Escape) / Stop / new request
  ApplyPending --> Executing: Approve (still current, same target)
  ApplyPending --> NoProposal: Approve but expired or denied
  Executing --> ApplyPending: success and more steps queued [Next step]
  Executing --> NoProposal: done / failed / stopped
```

| # | From → To | Event | Cite | Label |
| --- | --- | --- | --- | --- |
| A1 | NoProposal → ApplyPending | Reply carries actions **and** the user's text was an explicit task; actions in other replies are dropped with a message | `src/Boo.App/MainWindow.xaml.cs:966-995`, `:1118-1133` | code, live (j5 `preview='Write file'`) |
| A2 | ApplyPending → NoProposal | Cancel button, Escape | `src/Boo.App/MainWindow.xaml.cs:1288-1313`, `:1937-1944` | code |
| A3 | ApplyPending → NoProposal | Stop or any new request | `src/Boo.App/MainWindow.xaml.cs:301`, `:1982` | code |
| A4 | ApplyPending → NoProposal | Approve, but the request is no longer current, the policy denies, or the target window changed | `src/Boo.App/MainWindow.xaml.cs:1177-1222` | code |
| A5 | ApplyPending → Executing | Approve: an approval ticket is made for **this exact request** (digest of name, target, arguments, generation) and re-checked before every effect | `src/Boo.App/MainWindow.xaml.cs:1173`, `:1197`, `:1225`; `src/Boo.Core/ActionPolicy.cs:42-45`, `:62-82`; `src/Boo.Windows/DesktopTools.cs:968-971` | code, live (j5 `written='BOO-LIVE-VERIFY-OK'`) |
| A6 | Executing → Applied | verified success → journal the edit (file: `FileUndoJournal`; field: `UndoJournal`) | `src/Boo.App/MainWindow.xaml.cs:1226-1231`; `src/Boo.App/MainWindow.Edits.cs:243-245`, `:307`, `:504-533` | code, live (j5 `written=...` then the undo card) |
| A7 | Executing → ApplyPending | success and more steps queued | `src/Boo.App/MainWindow.xaml.cs:1246-1250` | code |
| A8 | Executing → NoProposal | done, failed or stopped (`finally`) | `src/Boo.App/MainWindow.xaml.cs:1252-1285` | code |

### 3.7 Undo (`UndoJournal` for fields, `FileUndoJournal` for files)

| # | Event | Effect | Cite | Label |
| --- | --- | --- | --- | --- |
| U1 | The user types or says "undo" | the request is classified as Undo; the **file** journal wins if it holds something, else the field journal | `src/Boo.App/MainWindow.xaml.cs:760-764`, `:2267-2275`; `src/Boo.App/MainWindow.Edits.cs:71-85` | code, live (j5 typed "undo" → `Undo: Delete file`) |
| U2 | Nothing recorded | message, no card (the app checks `HasPendingUndo` first, so the journals' own "nothing" plans are not reached from here) | `src/Boo.App/MainWindow.Edits.cs:74`, `:81-84` | code |
| U3 | File undo planned | the reverse step (write the old text, or delete a file Boo created) is shown as an `Undo:` card; blocked if the file changed since | `src/Boo.App/MainWindow.Edits.cs:455-478`; `src/Boo.Core/FileUndoJournal.cs:67-106` | code, live (j5 `undoPreview='Undo: Delete file'`) |
| U4 | Field undo planned | plan refuses on a newer change, an ambiguous Undo control, or an empty "before" value | `src/Boo.App/MainWindow.Edits.cs:93-123`; `src/Boo.Core/UndoJournal.cs:87-195` | code (demo-ci, mock: `02-undo-*`) |
| U5 | Undo approved (field) | after read-back the journal is cleared, so a second undo has nothing to do | `src/Boo.App/MainWindow.Edits.cs:227-233`, `:327-357`; `src/Boo.Core/UndoJournal.cs:197-213` | code |
| U6 | Undo approved (file) | the undo is itself a file write/delete, so it is **journaled as a new edit**; a second "undo" would plan to reverse the undo | `src/Boo.App/MainWindow.xaml.cs:1224-1231`; `src/Boo.App/MainWindow.Edits.cs:485-533` | code; behaviour of a second undo UNVERIFIED (no run does it) |

### 3.8 Pointer and ring (`PointerAnnotationSequencer`, `PointerPhase`, 6 values)

`src/Boo.Core/PointerGuidance.cs:217-219` (enums), `:245-363` (logic), glue in `src/Boo.App/AnnotationWindow.xaml.cs`.

```mermaid
stateDiagram-v2
  [*] --> Idle
  Idle --> Fly: Begin (flight > 0 ms)
  Idle --> Land: Begin (no flight)
  Fly --> Land
  Land --> DrawRing: 120 ms
  DrawRing --> Hold: stroke drawn
  Hold --> Idle: 10 s (explain ring) / 8 s (nudge ring); the last frame says Clear, reason Completed
  Fly --> Idle: Clear(reason)
  Land --> Idle: Clear(reason)
  DrawRing --> Idle: Clear(reason)
  Hold --> Idle: Clear(reason)
```

| # | Event | Cite | Label |
| --- | --- | --- | --- |
| P1 | Begin with an older or equal generation is refused | `src/Boo.Core/PointerGuidance.cs:274-277` | code |
| P2 | Begin while one is showing clears it first (`TargetChanged`) | `src/Boo.Core/PointerGuidance.cs:279-282` | code |
| P3 | Tick walks Fly → Land → DrawRing → Hold → Idle (`Completed`); `Clear` is only the phase of the returned last frame, `Phase` itself goes straight to Idle (`:333-352`); hold 10 s for the explain ring, 8 s default for the nudge ring | `src/Boo.Core/PointerGuidance.cs:292-335`; `src/Boo.App/AnnotationWindow.xaml.cs:66`, `:100` | code; live shows only the ring and pointer drawn on the target (j3), not the phase walk or hold time |
| P4 | Clear with any reason → Idle; the window closes with `Stop`; scroll, resize, foreground and display changes clear it | `src/Boo.Core/PointerGuidance.cs:338-354`; `src/Boo.App/AnnotationWindow.xaml.cs:87`, `:223`, `:281-298`; Stop/new request close the windows `src/Boo.App/MainWindow.xaml.cs:2590-2596`, `:300`, `:1981` | code |

## 4. The plan's invariants against today's code

| Id | Plain words | Today | Where |
| --- | --- | --- | --- |
| `INV-PRIV-1` | Mic or capture on ⇒ notch visible | **Enforced in the notch window, not by `PresenceController`.** Capture and Listening beat every other flag, including `UserHidden`, and pin the timeline out. The reveal takes 240 ms (Revealing) unless reduced motion. `PresenceController` (the class the plan names) is **not used by the app** (only by Core and tests). | `src/Boo.Core/NotchFace.cs:41-43`; `src/Boo.App/NotchWindow.xaml.cs:504-512`, reduced motion / wake-up shown at once `:683`, `:692-695`; `src/Boo.Core/NotchFace.cs:327-334`; unused: `src/Boo.Core/PresenceController.cs:3` |
| `INV-PRIV-2` | No capture starts while Hidden without approval | **Not enforced as written.** Captures need no approval: they start on a typed Send, a voice tool call, or a focus check. For a typed Send and a voice tool call, `Thinking` is set first (`:743`, `:2366`) and already pins the notch out before the capture starts; for a focus check, starting the capture is what reveals the notch (`:1598`). In every path the indicator is set, then the code yields once to the UI before grabbing the frame. Whether the notch is already on screen at the moment of the grab is UNVERIFIED (timing). | `src/Boo.App/MainWindow.xaml.cs:2029-2037`, `:1598-1606` |
| `INV-APPLY-1` | Apply only after Approve of the same proposal | **Enforced.** The ticket is a digest of the exact request (name, target, arguments, generation) and is re-checked before each effect. There is no separate proposal id; the digest plays that role. | `src/Boo.Core/ActionPolicy.cs:62-82`; `src/Boo.App/MainWindow.xaml.cs:1173`, `:1197`, `:1225`; `src/Boo.Windows/DesktopTools.cs:968-971` |
| `INV-APPLY-2` | Undo only what was applied, only once; restores the before-state | **Fields: enforced** (plan needs a recorded edit, refuses newer changes, clears on verified undo). **Files: "only once" is not enforced**: the approved undo is journaled as a new edit, so a second undo reverses the undo (finding F6). | `src/Boo.Core/UndoJournal.cs:87-213`; `src/Boo.Core/FileUndoJournal.cs:31-59`; `src/Boo.App/MainWindow.Edits.cs:504-533` |
| `INV-APPLY-3` | A new request never applies an older proposal | **Enforced** three ways: a new request clears the card; policy denies a request whose generation differs from the authority; commits run inside `TryCommit` for the current generation. | `src/Boo.App/MainWindow.xaml.cs:1982`; `src/Boo.Core/ActionPolicy.cs:42-45`; `src/Boo.Core/RequestCoordinator.cs:46-62` |
| `INV-FOCUS-1` | Exactly one end event per session | **Partly: at most one is enforced.** `_endRaised` stops a second event. But a session replaced by a new Start ends with `EndQuietly` and emits **none**, and a session still open when Boo quits is only paused by `StopAll`, never ended (finding F9). | `src/Boo.Core/FocusSessionController.cs:152-168`; `src/Boo.App/MainWindow.xaml.cs:1338-1339`, `:315-318` |
| `INV-TALK-1` | After TalkUp or Stop, mic off | **Enforced in the glue.** `StopAndSend` and `Cancel` both stop the mic before anything else; Stop cancels the hold, stops audio and clears the indicator. The mic is opened before the first `await` in `StartPushToTalk`, so a release queued behind it still finds it open and closes it (code reading; ordering on the UI thread). | `src/Boo.App/MainWindow.Talk.cs:253-261`, `:299-303`, `:352-356`; `src/Boo.App/MainWindow.xaml.cs:267`, `:280`, `:297` |
| `INV-STOP-1` | Stop from anywhere ⇒ quiet (Awake or Hidden), mic and capture off | **Partly.** Mic off: yes. Chat flags cleared: yes. But (a) **a running focus session is paused by Stop (and an already-paused one stays paused), and `Paused` pins the notch out**, so the notch stays visible showing Paused, not quiet; (b) capture: Stop cancels the work, but the capture indicator clears only when each in-flight operation is disposed, which happens after the AI call it wraps is cancelled (section 3.2), so `Capture` can outlast Stop briefly (UNVERIFIED how long). | `src/Boo.App/MainWindow.xaml.cs:265-313`, `:303-312`, `:713-717`, `:845`, `:1582`; `src/Boo.Core/NotchFace.cs:49` |
| `LIVE-1` | Chat always ends ("never stuck on Responding") | **Responding: enforced** by the moment timer (4 s, or the playback tail capped by `FaceMoments`). **Thinking: no local timeout.** Typed requests on the plain-text path end through the 45 s HTTP timeout and `finally`. But with speech on (the default) and a voice session connected, a typed question is handed to the voice session and `finally` leaves Thinking set (`:846`). A voice turn's Thinking (and such a hand-off) ends only on a reply event, error, disconnect, `go_to_sleep`, Stop or a new request. The voice client's 15 s connect timeout and 10 s keep-alive catch a dead connection, not a connected provider that stays silent; then Thinking stays (UNVERIFIED: no run has a silent provider). | `src/Boo.App/MainWindow.xaml.cs:175-202`, `:843-849`, `:2258`, `:766-801`, `:846`, `:2297-2307`, `:2123`; `src/Boo.Core/FaceMoments.cs:22-30`; `src/Boo.Providers/RealtimeClient.cs:292-294`, `:339-346` |
| `LIVE-2` | Hover with no more input returns to Hidden | **Only after the pointer leaves.** Hover means "pointer is on the notch", so it lasts as long as the pointer stays. After PointerLeave: Idle for 900 ms, retract 160 ms, Hidden, driven by the render loop. | `src/Boo.App/NotchWindow.xaml.cs:1374-1378`, `:654-659`; `src/Boo.Core/NotchFace.cs:317-353`; live (j1 `retracted=True`; its `retractMs=713` is shorter than 900 + 160 ms because the harness starts its clock only after the animated mouse move away returns, i.e. after the pointer already left) |

## 5. Findings to settle before the model (no code changed here)

These are facts from reading the code that a TLA+ model or reducer must either copy or change on purpose. Each needs a
decision in its own PR, not here.

1. **F1 `PresenceController` is unused by the app.** The privacy rule lives in `FaceStateResolver` + the reveal timeline
   instead (`src/Boo.App/NotchWindow.xaml.cs:504-512`). The model should mirror the code path that runs.
2. **F2 `UserHidden`, `FaceInputs.Listening` and `FaceInputs.CaptureActive` are never set by the app.** Listening and
   Capture reach the face only through the notch's own mic/capture flags (`src/Boo.App/NotchWindow.xaml.cs:504-505`).
3. **F3 There is no "Awake" notch state.** The panel being open does not change the notch; `Idle` is only the retract
   tail. The plan's statechart should either drop `Awake` or say it is a panel state.
4. **F4 Stop is not a state and does not always end quiet**: with a running or paused focus session, Stop leaves the notch pinned
   on `Paused` (`INV-STOP-1` above). Either the invariant allows `Paused`, or Stop's behaviour changes.
5. **F5 Captures need no approval**; for focus checks the notch is revealed by the capture starting, and for a typed or voice request by the Thinking face set just before it (`INV-PRIV-2` above). The invariant as
   worded does not match the product; it needs rewording (for example "capture never runs without the Capture face
   being set first") or a behaviour change.
6. **F6 File undo toggles.** An approved file undo is journaled as a new edit, so "undo" twice re-applies the original
   edit. Field undo does not do this. The plan's property "double undo is a no-op" would fail for files today.
7. **F7 Thinking has no local timeout for voice turns, nor for typed text handed to the voice session** (speech on, the default; `LIVE-1` above).
8. **F8 Two talk paths.** The panel Talk button opens the mic without `HoldToTalkController`; a model of "Listening"
   must include both (`src/Boo.App/MainWindow.xaml.cs:853-908`). The voice session can also end itself (`go_to_sleep`, F10b) and drive focus (`focus_start/pause/resume/end`, `src/Boo.App/MainWindow.Voice.cs:43-66`); the model's event list needs these too.
9. **F9 A focus session can end with no end event**: a session replaced by a new Start ends quietly, and one still open at quit is only paused (`INV-FOCUS-1` above). The model must either allow zero end events for these or the behaviour changes.

## 6. Cross-check against a live run

Real-key live run [43339557570](https://github.com/vikas53953/bluey/actions/runs/43339557570) (PR #13 QA head
`3168e40`, artifact `live-ai-evidence`, `journeys/`). The notch face state is read through UI Automation; the notch screenshots are black on black, so the cross-check rests mainly on those logs, with the panel screenshots (j2, j5) and the ring (j3) checked by eye.

| Journey | What it shows | Transitions it confirms |
| --- | --- | --- |
| j0 `j0-hotkey.txt`, `j0-after-hotkey.*.png` | Ctrl+Alt+B opens the panel; the notch capture shows it still collapsed | F22 |
| j1 `j1-hover.txt`, `j1-hover-revealed.boo-0.png` | `revealed=True state='Hover'`, then `retracted=True` | R1, R3, R6, F1, F2, LIVE-2 |
| j2 `j2-chat.txt`, `j2-chat-answer.*.png` | typed question, real reply `'Paris'` | F3, F4 (reply arrives; the Thinking/Responding faces themselves are not logged) |
| j3 `j3-explain-ring.*.png` | pointer and ring drawn on the target button (the notch state is not logged) | P3 |
| j5 `j5-apply-undo.txt`, `j5-*.png` | `Write file` card → written → `Undo: Delete file` card → file gone | A1, A5, A6, U1, U3 (a second undo is not run, so U6 stays UNVERIFIED) |
| j6 `j6-focus.txt`, `j6-focus-*.png` | `states=Hidden>Capture>Idle>Hidden>…>Happy` (35 capture cycles), `Completed`, `sawHappy=True` | Fo1, Fo5, Fo7, Fo8, F14, F15, F18, R9, R11 |

Not covered by any run: Listening (voice is UNVERIFIED by choice, `docs/DECISIONS.md` 12 Nov), Nudge (j6 `nudges=0`),
Paused, Stop, a second undo, a silent provider, and the typed-question hand-off to voice (the live fixture saves `SpeechEnabled: false`, `tools/Boo.LiveSmoke/LiveAppFixture.cs:123`, so j2 used the plain-text path). Video of runs arrives with PR 2.8.
