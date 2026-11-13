# verify-boo journeys: every notch state, one shared list

This is the **single shared list** cataloguing Boo's notch states and transitions, planned user journeys and explicit
coverage gaps. Some internal transitions cannot be driven through the UI. **Nothing here has been run because it is written down**:
a journey proves something only when a run of the published `Boo.exe` does it, with evidence (see `SKILL.md`).

Where the facts come from:

- The notch map: `docs/architecture/notch-lifecycle.md` (PR #27). Its row ids are used as they are: **R1–R11**
  (reveal), **F1–F24** (face), **H1–H8** (hold-to-talk), **Fo1–Fo8** (focus), **G1–G5** (request generations),
  **A1–A8** (approval card), **U1–U6** (undo), **P1–P4** (pointer and ring), and its invariants (`INV-…`, `LIVE-…`).
- The map's section 5 also numbers its **findings** F1–F9, which clash with the face rows F1–F24. In this file a
  finding is always written **§5-F** plus its number (for example §5-F4 "Stop leaves focus Paused"); findings now include §5-F10 (mixed microphone ownership). A plain **F4** is
  face row F4.
- The face names: `NotchFaceState` in `src/Boo.Core/NotchSpec.cs:15` (10 values).
- Existing journeys j0–j6: `tools/Boo.VerifyLive/LiveJourneyTests.cs` (`[Fact, Step(0)]` … `Step(6)`). Their ids do not
  change. New journeys start at **j7**.

## How evidence is read (the same for every journey)

| Evidence | What it is | What it can prove |
| --- | --- | --- |
| **Face timeline (pending dependency)** | PR #48 proposes `tools/Boo.VerifyLive/FaceTimeline.cs`; it is **absent from this head**. The planned observer reads UI Automation `ItemStatus` about every 25 ms and writes `jN-<name>-face-timeline.txt`, with action marks and bounded waits. These filenames and APIs describe required future evidence, not an available recorder. Existing j0–j6 observation logs remain available. | Once implemented and validated: observed face order and durations. Until then, timeline-dependent assertions are **UNVERIFIED**. A face shorter than the actual sampling interval may be missed; record the measured interval. |
| **UI Automation read** | A named control or property read through FlaUI: the panel's controls (for example `Send typed question`, `Activate microphone`, `Review action`, `Approve proposed action`, `Focus task`), the notch's `HelpText` (the "Stopped" flash, `src/Boo.App/NotchWindow.xaml.cs:315-339`), the nudge buttons `Back to it` and `5 min break` (`src/Boo.App/NotchWindow.xaml:101-120`). Written to `jN-*.txt`. | Panel state, card titles, status text, files written. |
| **Boo-window screenshot** | One image per visible Boo window, `<moment>.boo-N.png` + `<moment>.capture.txt` (never the desktop; `SKILL.md` "Reporting"). | Supporting evidence. The notch images are often black on black, so a face claim rests on the timeline, not the picture. |

Two things the evidence **cannot** show directly, said once here:

- The **reveal phase** (`RevealPhase`: Hidden, Revealing, Revealed, RetractPending, Retracting) is not published to UI
  Automation, only the face is. A reveal phase is proven **indirectly** from face-timeline timings (for example the
  face stays `Idle` for about 900 ms, then about 160 ms later reads `Hidden`). Say "inferred from timings" in reports.
- The **pointer phase** (`PointerPhase`) is not published either; only the drawn ring and pointer are visible (j3).

Shipped timings used below (from the map): reveal 240 ms, retract delay 900 ms, retract 160 ms (map §3.1); Responding
held 4 s (F4, F8); Nudge held 6 s (F16); Happy held 2.5 s (F18); hold-to-talk tap threshold 200 ms and cap 120 s
(H4, H5, H8); "Stopped" flash about 1.6 s (`src/Boo.App/NotchWindow.xaml.cs:44`); focus check every 30 s, every 10 s
in Developer Mode (Fo8).

**Live or mock-capable:** *live* = needs the real OpenAI key (cheapest models per the 13 Nov decision and PR #48:
`gpt-6-luna` chat and focus, `gpt-realtime-2.1-mini` voice). The current smoke supports `BOO_LIVE_MODEL` and
`BOO_LIVE_REALTIME_MODEL`; **there is no `BOO_LIVE_FOCUS_MODEL` consumer at this head**. Focus uses Boo's configured
focus model; record the actual settings and do not claim an environment override configured it. *mock-capable* = can also run against `tools/Boo.QaMockAi` (text and focus answers only;
it has **no voice**), or needs no AI at all. A mock run proves the UI and logic, never the AI.

**"Expect today's behaviour"** marks a step where the map's findings say Boo today differs from the plan. The journey
checks what Boo does **today** and names the finding; it is not a pass or fail of the ideal. When a later PR changes
that behaviour on purpose, that PR updates the step.

---

## 1. State coverage

Every notch face (`NotchFaceState`, 10 values) and every lifecycle state of the map, with the journeys that reach it.

### 1a. Notch faces (`src/Boo.Core/NotchSpec.cs:15`)

| Face | Reached by | How the evidence proves it | Live / mock-capable |
| --- | --- | --- | --- |
| `Hidden` | every journey (start and end) | face timeline first and last entry `Hidden` | mock-capable (no AI) |
| `Hover` | j1, j7 | face timeline `Hover` while the pointer is on the notch (F1) | mock-capable (no AI) |
| `Idle` | j1, j7, and the tail of every busy journey | face timeline `Idle` lasting about 900 ms + 160 ms before `Hidden` (F2, R5, R6) | mock-capable (no AI) |
| `Listening` | j9 (chord), j10 (eyes), j11 (panel Talk), j12c (Stop) | face timeline `Listening` between key/eye down and release; panel `ListeningStatus` text read (`Listening… release to send.`, `src/Boo.App/MainWindow.Talk.cs:262`) | **live only** (voice; the mock has no voice) and needs a microphone device on the runner, see j9 |
| `Thinking` | j8 (typed handed to voice), j9, j12a | face timeline `Thinking` until the first audio (F3 voice branch, F7) | live (voice) |
| `Responding` | j2, j3, j4, j8, j9, j12b | face timeline `Responding`, about 4 s after the last reply part (F4, F5, F8, F9) | live; j2's plain-text path is also mock-capable |
| `Capture` | j2, j3, j6, j12d, j14 | face timeline `Capture` around the AI call (F14, F15) | live; mock-capable for typed and focus checks |
| `Nudge` | j14 | face timeline `Nudge` about 6 s; nudge buttons `Back to it` / `5 min break` read by UI Automation | live (AI decides "off task", may need a retry); mock-capable (QaMockAi answers off-task, demo-ci step 4) |
| `Paused` | j12f, j12g, j13, j14 | face timeline `Paused` that stays while the session is paused; panel focus status read | mock-capable (focus controls need no AI) |
| `Happy` | j6, j13 | face timeline `Happy` about 2.5 s after the session ends (F18, Fo7) | mock-capable (End button); live in j6 |

### 1b. Lifecycle states of the map (not faces)

| Machine | State | Reached by | How the evidence proves it | Live / mock-capable |
| --- | --- | --- | --- | --- |
| Reveal (§3.1) | `Hidden` | all | face `Hidden` | mock-capable |
| Reveal | `Revealing` | j1, j7 | inferred: first non-Hidden face appears; j7 re-enters at known times | mock-capable |
| Reveal | `Revealed` | j1, j7, every pinned face | inferred: face non-Hidden and stable | mock-capable |
| Reveal | `RetractPending` | j1, j7 | inferred: `Idle` for about 900 ms after leave or unpin | mock-capable |
| Reveal | `Retracting` | j1, j7 | inferred: `Idle` → `Hidden` about 160 ms after the delay | mock-capable |
| Hold-to-talk (§3.3) | `Idle` | j9, j10 | `Listening` ends; panel status read | live |
| Hold-to-talk | `Listening` | j9, j10 | face `Listening` | live, mic needed |
| Focus (§3.4) | `Running` | j6, j13, j14 | panel focus status; periodic `Capture` in the timeline (Fo8) | mock-capable |
| Focus | `Paused` | j12f, j12g, j13, j14 | face `Paused` + panel status | mock-capable |
| Focus | `Ended` | j6, j13 | face `Happy` + session history entry (Fo7); for a quiet end (Fo2) **no** `Happy` | mock-capable |
| Approval (§3.6) | `NoProposal` | j5, j15 | `Review action` section not visible | mock-capable |
| Approval | `ApplyPending` | j5, j12e, j15, j16 | `Review action` visible, its title read (for example `Write file`) | live; mock-capable (demo-ci step 2) |
| Approval | `Executing` | j5, j15, j16 | Approve clicked → file or field changed (read back) | live; mock-capable |
| Undo (§2, §3.7) | `Applied` | j5, j16 | file written / field changed, then an `Undo:` card is offered | live; mock-capable |
| Undo | `Undone` | j5, j16 | file back to before; field journal cleared (U5) or new file edit journaled (U6) | live; mock-capable |
| Pointer (§3.8) | `Idle`, `Fly`, `Land`, `DrawRing`, `Hold`, `Clear` | j3, j12h | only the drawn ring/pointer is visible; phases are **not** published, so the walk is not proven step by step (map P3 note) | live; mock-capable (demo-ci step 1) |
| Plan state `Awake` | **no such state** (§5-F3) | j0 | face does not change when the panel opens (F22) | mock-capable |
| Plan state `Interrupt` | **no such state** (§2: Stop is a function) | j12 | flags cleared in one step; `Stopped` HelpText flash | mock-capable |
| Face input `UserHidden` | **never set by any caller** (§5-F2) | none | cannot be driven; reducer tests only | not drivable |

Count: **10 faces + 21 lifecycle states** (5 reveal, 2 hold-to-talk, 3 focus, 3 approval, 2 undo, 6 pointer), plus 3
names that are **not** states (the plan-only `Awake` and `Interrupt`, and the unused `UserHidden` input). Every face and every real state
is reached by at least one journey; `UserHidden` is not drivable.

---

## 2. The journeys

Common preconditions unless a row says otherwise: the published `Boo.exe`, single instance, on GitHub's
`windows-latest` (never a personal PC); the live fixture's saved settings; the selected display chosen; panel closed at
the start; a fresh `FaceTimeline` per journey. "Speech off" means `SpeechEnabled: false` (the live fixture's setting
today, `tools/Boo.LiveSmoke/LiveAppFixture.cs:123`); "speech on" is Boo's default (`src/Boo.Core/Contracts.cs:36`).

### Existing journeys (ids kept; expectations added)

| Id | Name | Preconditions | Steps | Expected face sequence | Expected effects | Covers | Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| j0 | Hotkey opens the panel | none (no AI) | Press Ctrl+Alt+B | `Hidden` stays `Hidden` (no notch flag changes) | panel window opens | F22, §5-F3 (`Awake` is a panel state, not a notch state) | panel found by UI Automation; face timeline (none today, add one) |
| j1 | Hover reveals and retracts | none (no AI) | Move the pointer to the top-centre strip; wait; move it away | `Hidden` → `Hover` → `Idle` (about 900 ms) → `Hidden` | notch slides in, then out | R1, R2, R3, R5, R6, F1, F2, `LIVE-2` | `j1-hover-face-timeline.txt` (PR #48), `j1-hover.txt`, `j1-hover-revealed.boo-N.png` |
| j2 | Typed chat, plain-text path | speech off; chat `gpt-6-luna` | Ctrl+Alt+B, type a question in `Type a question for Boo`, click `Send typed question` | `Hidden` → `Thinking` (brief, may be missed) → `Capture` (through the AI call, because Capture outranks Thinking, map §3.2) → `Responding` about 4 s → `Idle` → `Hidden` | real answer in the transcript | F3 (speech-off branch), F4, F5, F14, F15, R9, R11, G1, G4, `LIVE-1` (Responding ends) | `j2-chat-face-timeline.txt` and `facesDuringTurn=` in `j2-chat.txt` (PR #48), `j2-chat-answer.boo-N.png` |
| j3 | Screen explain: pointer and ring | speech off; chat model | Ask Boo to point at a named on-screen button | as j2 | pointer and ring drawn on the target, held about 10 s, then cleared | as j2, plus P3 | `j3-explain-ring.boo-N.png` (ring overlay window), add a face timeline |
| j4 | Research with sources | speech off; chat model | Ask a research question | record the faces of the turn (expected to end `Responding` → `Idle` → `Hidden` as j2; the map does not cite the research path's capture, so do not assert `Capture` here) | answer with "Open source …" links | F4, F5 | `j4-research.txt`, add a face timeline |
| j5 | Apply then Undo | speech off; chat model; Boo's file root | Ask Boo to write a named file; read the card; click `Approve proposed action`; type "undo"; approve the `Undo:` card | face as j2 for each typed turn; the approval card is a **panel** state, not a face (map §2) | file written only after Approve; undo removes it | A1, A5, A6, A8, U1, U3, `INV-APPLY-1` | `j5-apply-undo.txt`, `j5-*.png` |
| j6 | 25-minute focus on the Developer Mode clock | Developer Mode on; focus model `gpt-6-luna` | Start focus, 25 min; let it run to the end (about 375 real seconds) | repeated `Hidden` → `Capture` → `Idle` → `Hidden` (one per check) → `Happy` about 2.5 s → `Idle` → `Hidden` | session ends `Completed`; history entry | Fo1, Fo5, Fo7, Fo8, F14, F15, F18, R9, R11 | `j6-focus-face-timeline.txt` (PR #48), `j6-focus.txt`, `j6-focus-*.png` |

**Reading the expected faces.** The notch shows one face at a time, the highest of: `Capture` > `Listening` >
`Responding` > `Thinking` > `Nudge` > `Happy` > `Paused` > `Hover` > `Idle` > `Hidden`
(`src/Boo.Core/NotchFace.cs`, `FaceStateResolver`; it also has `UserHidden` just below `Listening`, which nothing
sets today, §5-F2). Every expected sequence below follows that order: while a higher face is on, a lower one (for
example `Hover` while `Paused`) does not show. Two consequences every journey here follows:
- **Voice and AI turns sit on top.** While a turn shows `Listening`, `Thinking` or `Responding`, a lower face that
  happens during it (`Happy` when a voice command ends a focus session, `Paused` when one pauses it) may never be
  visible. Such a result is proven from the panel (focus status, history), not from the face.
- **The pointer stays where the mouse left it.** After a click or hold on the notch or its eyes, the pointer is still
  on the notch, so the face reads `Hover` (above `Idle`) until a step moves the pointer away. Buttons on the notch are
  pressed through UI Automation where possible (no pointer), or the journey moves the pointer away before expecting
  `Idle` / `Hidden`.

A journey that seems to expect otherwise is a mistake in this list; report it, do not count it as a Boo failure.

### New journeys

#### j7 — Hover edge timings (no AI, real clock)

| Field | Content |
| --- | --- |
| Preconditions | none; reduced motion **off** (with reduced motion on, the reveal is shown at once, `INV-PRIV-1` row of the map) |
| Steps | a) Hover top centre, leave **during** the 240 ms reveal (about 100 ms in). b) Hover, leave, re-enter after about 400 ms (inside the 900 ms delay). c) Hover, leave, re-enter after about 950 ms (inside the 160 ms retract). d) Start a focus session so `Paused` pins the notch (Pause it), hover, then leave. |
| Expected faces | a) `Hover` → `Idle` → `Hidden`. `Idle` shows as soon as the pointer leaves (the notch is still sliding in); what R4 delays is the 900 ms countdown, which starts only when the 240 ms slide-in ends, so `Idle` lasts until about 240 + 900 ms after the hover began, then the 160 ms retract ends in `Hidden` (R4). b) `Hover` → `Idle` → `Hover` with no `Hidden` between (R7). c) `Hover` → `Idle` → `Hover` with no `Hidden` reading, because the face reads `Idle` until the phase is Hidden (F2); the notch slides back from where it was (R8). d) stays `Paused` throughout: `Paused` outranks `Hover` (face priority, `src/Boo.Core/NotchFace.cs`), so the hover makes no visible change, and the notch never reads `Idle`/`Hidden` while paused (R10). |
| Expected effects | none besides the notch moving |
| Covers | R2, R4, R5, R7, R8, R10, F1, F2, `LIVE-2` |
| Evidence | `j7-hover-edges-face-timeline.txt` with `Mark` at each pointer move; timings inferred (say so) |
| Live / mock | mock-capable (no AI) |

#### j8 — Typed question handed to voice (speech on)

| Field | Content |
| --- | --- |
| Preconditions | speech **on**, not muted (`Mute spoken replies` unchecked); realtime `gpt-realtime-2.1-mini`; no microphone needed (text is typed) |
| Steps | a) Ctrl+Alt+B, type a short question, `Send typed question`. b) Then type a question that needs the screen ("what is on my screen?") so the voice model may call `screen_help`. |
| Expected faces | a) `Hidden` → `Thinking` (a brief `Capture` may show first; it ends as soon as the text is sent, so `Thinking` stays visible while waiting, map §3.2) → `Responding` (refreshed on every audio part) → `Responding` held for the rest of playback (F9) → `Idle` → `Hidden`. b) `Thinking` → `Capture` (tool call) → `Responding` → `Idle` → `Hidden`. **Expect today's behaviour (§5-F7):** if the provider stays silent, `Thinking` has no local timeout. |
| Expected effects | spoken reply (audio not checked; the transcript is) |
| Covers | F3 (voice branch), F8, F9, F11, F14, F15, G1, `LIVE-1`, §5-F7 |
| Evidence | face timeline; transcript read; `ListeningStatus` read |
| Live / mock | **live only** (voice) |

#### j9 — Hold-to-talk with Ctrl+Alt+Space

| Field | Content |
| --- | --- |
| Preconditions | speech on; realtime `gpt-realtime-2.1-mini`; **an audio input device on the runner** that plays a short recorded question. With no input device Boo never shows `Listening` (`src/Boo.App/MainWindow.Talk.cs:253-258`): report the journey **BLOCKED (no microphone on the runner)**, not failed. |
| Steps | a) Hold Ctrl+Alt+Space about 3 s while the question plays, release. b) **Tap**: press and release within 200 ms. c) Hold the chord, then press and hold the eyes, release the chord, then release the eyes. d) Hold the chord for more than 120 s (real clock). e) While a reply is playing, hold the chord again. |
| Expected faces | a) `Hidden` → `Listening` → `Thinking` (after release, once the server reports speech stopped/committed, F7) → `Responding` → `Idle` → `Hidden`. b) `Listening` for under 200 ms (may be missed by the timeline) → `Idle` → `Hidden`; hint "Hold to talk" (`src/Boo.Core/HoldToTalk.cs:51`). c) `Listening` stays through the first release (H3), turn sent on the last release. d) `Listening` until about 120 s, then the turn is sent as in a) (H8). e) the reply is interrupted and `Listening` starts again (H1 "Interrupt + Start"). |
| Expected effects | mic off immediately on release (`INV-TALK-1`); a turn shorter than 4,800 bytes of audio is not sent (map §3.3) |
| Covers | H1, H3, H4, H5, H8, F7, F8, F9, F12, F13, `INV-TALK-1`, §5-F8 (chord path) |
| Evidence | face timeline with `Mark("chord down")` / `Mark("chord up")`; `ListeningStatus` read |
| Live / mock | **live only**, mic needed |

#### j10 — The eyes: tap and hold

| Field | Content |
| --- | --- |
| Preconditions | as j9 for the hold parts; no AI for the tap |
| Steps | a) Hover top centre; click the eyes quickly (under 200 ms). b) Press and hold the eyes about 3 s, release, keep the pointer on the eyes through the reply and until Hover returns, then move it away. c) Press and hold the eyes, then make another window take the mouse (Alt+Tab, or bring another window to the front) so the eyes lose mouse capture. Dragging alone does **not** do it: the eyes keep the mouse while the button is held (`src/Boo.App/NotchWindow.xaml.cs`). After capture loss move the pointer away immediately. If the driver cannot switch windows while the mouse button is held, this step is **manual**; that switching windows releases the eyes at all is **UNVERIFIED** (the notch window never takes focus). |
| Expected faces | a) `Hover`, no `Listening`; the panel opens (same as Ctrl+Alt+B). b) `Hover` → `Listening` (after 200 ms) → `Thinking` → `Responding` → `Hover` (pointer stays on the eyes) → after pointer leave: `Idle` → `Hidden`. c) capture loss counts as eye release (H7); after immediate pointer leave the voice-turn faces take priority, then `Idle` → `Hidden`. No post-reply Hover is required in c). |
| Expected effects | a) panel open. b) and c) a voice turn, as j9 |
| Covers | F23, F24, H2, H4, H5, H7 |
| Evidence | face timeline; panel found by UI Automation for a) |
| Live / mock | a) mock-capable (no AI); b), c) live only, mic needed |

#### j11 — Panel Talk button (the second talk path)

| Field | Content |
| --- | --- |
| Preconditions | speech on; realtime model; mic as j9 |
| Steps | a) Ctrl+Alt+B, click `Activate microphone` (Talk); speak (recorded question); click it again (now "Stop talk"). b) In a Talk session, ask "pause my focus" while a focus session runs. |
| Expected faces | a) `Hidden` → `Listening` → `Idle`/`Hidden` after Stop talk (F13). b) **Expect today's behaviour:** `focus_pause` is refused in a non-push-to-talk voice session while a session is live (Fo3 note; `src/Boo.Core/VoiceCommands.cs:193-194`), so the face stays as it was (no `Paused`). |
| Expected effects | `ListeningStatus` reads "Listening… press Stop talk or the global Stop to interrupt." then "Microphone idle. Activate Talk when you want to speak." (`StopListening`, `src/Boo.App/MainWindow.xaml.cs:949`) |
| Covers | §5-F8 (Talk button does not use `HoldToTalkController`), F12, F13, G3, Fo3 (voice refusal) |
| Evidence | face timeline; `TalkButton` content and `ListeningStatus` read |
| Live / mock | **live only**, mic needed |

#### j12 — Stop from every busy state

Stop runs the same `StopAll` from every source (F21). Each sub-step uses a **different source** so all drivable sources
are hit. After a hotkey Stop (and any Stop while no panel exists) the notch's `HelpText` flashes "Stopped" for about
1.6 s.

| Sub-step | Busy state and how to reach it | Stop source | Expected faces | Covers |
| --- | --- | --- | --- | --- |
| j12a | `Thinking`: j8 a) step, before the first audio | Ctrl+Alt+F12 | `Thinking` → `Idle` → `Hidden`; "Stopped" flash | F21, F6 (late reply dropped), G2, G4 |
| j12b | `Responding`: j8 a) during playback | Ctrl+Alt+S | `Responding` → `Idle` → `Hidden`; audio stops; "Stopped" flash | F21, G2 |
| j12c | `Listening`: hold Ctrl+Alt+Space (j9) | Ctrl+Alt+F12 while still holding, then release the chord | `Listening` → `Idle` → `Hidden`; the later release does nothing (H6); mic off | F21, H6, F13, `INV-TALK-1` |
| j12d | `Capture`: j2 step (speech off) during the AI call | panel `Stop Boo` button | `Capture` → (may stay `Capture` briefly until the in-flight capture is disposed, **expect today's behaviour**, `INV-STOP-1` (b), duration UNVERIFIED) → `Idle` → `Hidden` | F21, F15, `INV-STOP-1` |
| j12e | `ApplyPending`: j5 card showing | right-click on the notch | card gone (`Review action` hidden); face unchanged by the card | F21, A3 |
| j12f | focus **running** (j13 Start) | tray Stop (if the tray icon cannot be reached by UI Automation on the runner, drive this step **manual** and say so) | **Expect today's behaviour (§5-F4):** the session is **paused**, face goes to `Paused` and stays pinned (not quiet) | F21, F19, Fo3, §5-F4, `INV-STOP-1` (a) |
| j12g | focus **paused** (j13 Pause) | Ctrl+Alt+F12 | stays `Paused` (§5-F4) | F21, §5-F4 |
| j12h | ring showing (j3) | Ctrl+Alt+F12 | ring and pointer windows close | P4, F21 |

Evidence: face timeline with `Mark("stop: <source>")`; notch `HelpText` read for "Stopped"; panel status reads; for
j12h the ring overlay window is gone in the next `capture.txt`. Live/mock: j12a–c live only (voice); j12d, j12e, j12h
live or mock-capable; j12f, j12g mock-capable (no AI). The existing ui smoke steps 7 and 9 (`tests/Boo.Ui.Tests`)
already press Ctrl+Alt+F12 and Ctrl+Alt+S on a running session; they do not read the face.

#### j13 — Focus: pause, resume, end, restart

| Field | Content |
| --- | --- |
| Preconditions | Developer Mode on; focus model `gpt-6-luna` (or the mock, or no key: the ui smoke step 6 drives these buttons with no key) |
| Steps | a) Enter a task in `Focus task`, minutes 25, `Start focus session`. b) `Pause focus session`. c) `Resume focus session`. d) `Pause` again, then `End focus session` from Paused. e) Start again, then `Start` once more while it runs (no End). |
| Expected faces | a) `Hidden` (plus `Capture` at each check, Fo8). b) `Paused`, pinned. c) `Paused` → `Idle` → `Hidden`. d) `Paused` → `Happy` about 2.5 s → `Idle` → `Hidden`. e) **Expect today's behaviour (§5-F9):** the old session ends quietly, **no** `Happy`, no end event (Fo2). |
| Expected effects | history shows one entry per ended session; the replaced session in e) has no end event |
| Covers | Fo1, Fo2, Fo3, Fo4, Fo6, Fo7, Fo8, F18, F19, F20, R9, R11, `INV-FOCUS-1`, §5-F9 |
| Evidence | face timeline; focus status and `Focus history timeline` read |
| Live / mock | mock-capable; live with the focus model |

#### j14 — Focus nudge (off task)

| Field | Content |
| --- | --- |
| Preconditions | Developer Mode on. Live: focus model `gpt-6-luna`, a task clearly unrelated to what Boo's test screen shows (the AI decides; retry once if no nudge, and say so). Mock: `tools/Boo.QaMockAi` answers only the first three checks of its whole run off-task, then always on-task, so one mock run gives one nudge: steps b) and c) each need a **fresh mock process** (and a fresh session). Live and mock: a new nudge needs at least **120 focus-seconds** after the previous one (`NudgePolicy`, `src/Boo.Core/FocusSession.cs`). |
| Steps | a) Start focus; wait for the nudge; press `Back to it` through UI Automation (no pointer; with a mouse click, move the pointer away afterwards). b) Wait at least 120 focus-seconds for another nudge (fresh mock process in mock mode); let it time out (no click). c) Same wait (fresh mock process in mock mode); click `5 min break`. |
| Expected faces | a) `Capture` → `Nudge` (caption shown) → `Back to it` → `Idle` → `Hidden` (with a mouse click: `Hover` until the pointer moves away). b) `Nudge` about 6 s → `Idle` → `Hidden`. c) `Nudge` → `Paused` (break pauses the session). |
| Expected effects | nudge pointer/ring may show (8 s hold, P3); session paused after c) |
| Covers | F16, F17, Fo3 (5 min break), Fo8, F14, F15, P3 |
| Evidence | face timeline; nudge buttons and caption read through UI Automation |
| Live / mock | live (AI-dependent); mock-capable: a) certain in one mock run, b) and c) certain only with a fresh mock process each |

#### j15 — Approval card edges

| Field | Content |
| --- | --- |
| Preconditions | speech off; chat model; Boo's file root |
| Steps | a) Ask Boo to write a file; on the card click `Cancel proposed action`. b) Same, press Escape. c) Same, then type a new question before approving. d) Ask for a two-step task (for example "create file A, then file B") and approve each step. e) Ask a plain question whose reply happens to carry actions (not an explicit task). |
| Expected | a), b) card gone, no file (A2). c) card gone, the new request runs (A3, G1); approving the old card is impossible (`INV-APPLY-3`). d) after the first Approve a `Next step` card appears (A7), then done (A8). e) no card; a message says the actions were dropped (A1, negative case). Faces as j2 for each typed turn. |
| Covers | A1, A2, A3, A5, A7, A8, G1, `INV-APPLY-1`, `INV-APPLY-3` |
| Evidence | `Review action` visibility and title; file system read back |
| Live / mock | live (d and e depend on the AI's reply); mock-capable for a)–c) |

#### j16 — Second undo (files and fields)

| Field | Content |
| --- | --- |
| Preconditions | as j5; for the field part, the demo target window with a text field (as demo-ci `02-undo`) |
| Steps | a) j5 to the end (file created, then removed by an approved undo). b) Type "undo" again and read the card. c) Approve it. d) Field: have Boo fill a field, approve, "undo", approve, then "undo" again. |
| Expected | b), c) **Expect today's behaviour (§5-F6):** the approved file undo was journaled as a new edit, so the second undo offers a card that reverses the undo, and approving it **re-creates the file** (U6). d) the field journal was cleared after the verified undo (U5), so the second "undo" shows a "nothing to undo" message and no card (U2). |
| Covers | U1, U2, U3, U5, U6, A5, A6, `INV-APPLY-2`, §5-F6. U4 refusal branches are separate manual gaps below, not covered by successful undo. |
| Evidence | card titles read; file existence and field value read back after each step |
| Live / mock | live; mock-capable (demo-ci step 2 covers the field undo once) |

**U4 refusal coverage (manual, UNVERIFIED):** On a fresh field edit for each case: (1) after Boo's approved fill,
change the field yourself before asking Undo; (2) use a target exposing ambiguous Undo controls; (3) use a field
whose original value was empty and whose safe native Undo control is unavailable. Ask Undo in each case and require
an explanatory refusal, no approval card and an unchanged target value. Record the original/current field values,
raw target controls and refusal text plus screenshots. Cases (2) and (3) need an appropriate target fixture, absent
from this journey's current demo setup; they are **not automated coverage**. Successful undo or "nothing recorded"
after journal clearance proves none of these refusal branches.

#### j17 — Voice ends itself and voice drives focus

| Field | Content |
| --- | --- |
| Preconditions | speech on; realtime model; mic as j9 for push-to-talk |
| Steps | a) Hold-to-talk: "start a 25 minute focus session on writing". b) Hold-to-talk: "pause my focus", then "resume", then "end my focus". c) Hold-to-talk or typed: "go to sleep". |
| Expected faces | a) session starts (Fo1 voice). b) each spoken turn reads `Listening` → `Thinking` → `Responding` (on top of everything else). Between turns: `Paused` after "pause my focus" (pinned), then `Idle` → `Hidden` after "resume". After "end my focus" the session ends during that turn's `Thinking`/`Responding`, so its 2.5 s `Happy` is usually hidden; prove the end from the panel's focus status and history (`Ended`), not from the face (push-to-talk sessions may change focus, unlike j11 b). c) the spoken reply finishes (or an 8 s fallback), then the voice session ends: `Thinking`/`Responding` cleared, mic off, focus untouched (F10b). |
| Covers | Fo1, Fo3, Fo4, Fo6 (voice), F10b, G3, §5-F8 (voice ends itself, drives focus) |
| Evidence | face timeline; focus status read |
| Live / mock | **live only** (voice, AI-dependent tool choice) |

#### j18 — Newer request wins

| Field | Content |
| --- | --- |
| Preconditions | speech off; chat model |
| Steps | a) Send a question, and before it answers send a second one. b) Separately run j3 until the ring is visible; keep a before screenshot, then send a new question and observe that the old annotation clears. |
| Expected | a) only the second answer appears; the first's late result is dropped; `Thinking` is owned by the newer request (F6). b) the old annotation clears at BeginRequest (P4); this does not exercise PointerGuidance.Begin replacing an existing annotation (P2). |
| Covers | G1, G4, F6; P4 new-request clearing in b). P1/P2 are internal coverage gaps below. |
| Evidence | a) transcript read (one answer), face observations; b) before/after overlay screenshots and timestamped new-request event. Transcript/face evidence alone cannot prove ring clearing. |
| Live / mock | live; mock-capable |

---

#### j19 — Pause after End must not invent a paused session (#70)

| Field | Content |
| --- | --- |
| Preconditions | No key needed; speech off; pointer away from the notch |
| Steps | Start a 25-minute focus session; Pause; End; record the Ended status and history. After Happy settles, invoke Pause again, then Resume and End. |
| Required result | The session stays Ended, history is unchanged, and no Paused face is pinned by a click on an ended session. A disabled Pause control is acceptable. |
| Known failure | Issue #70: Pause after End writes a false Paused status and pins Paused. This is a **failure**, never an accepted baseline. |
| Covers | Fo3, Fo6, Fo7; extends j13 with terminal-state controls |
| Evidence | Before/after focus status, history and enabled controls; face timeline; Boo-window screenshots. Existing explorer proof: [run 43468157115](https://github.com/vikas53953/bluey/actions/runs/43468157115), `x06-focus.txt`. Re-run on the tested head; old proof is not current-head proof. |
| Live / mock | No AI required; current-head execution **UNVERIFIED** |

#### j20 — Stop clears the panel's busy status (#71)

| Field | Content |
| --- | --- |
| Preconditions | Real OpenAI; speech off; panel open; pointer away from notch |
| Steps | Send a question; confirm it is pending; press Ctrl+Alt+F12 before completion. Read status and transcript immediately and for at least five seconds. Repeat for other pending request types when reachable, reporting each independently. If the reply finishes first, report that substep UNVERIFIED; do not retry silently. |
| Required result | The status stops claiming work is pending, cancelled work adds no late answer or action, and the face returns according to active focus/hover state. A resting face alone is insufficient. |
| Known failure | Issue #71: the face rests but status remains Thinking about the selected context. Count this as FAIL. |
| Covers | F21, G2, G4; extends j12 with panel-status assertions |
| Evidence | Timestamped Stop, status/transcript reads and face timeline plus Boo-window screenshots. Existing explorer failure: [run 43468157115](https://github.com/vikas53953/bluey/actions/runs/43468157115), `l04-stop-mid-reply-FAILED.txt`; current-head execution **UNVERIFIED**. |
| Live / mock | Real-provider execution is live; a held local proxy is a separately labelled mock regression proof, never real-provider evidence |

#### j21 — Duplicate conversation rows stay individually accessible (#73)

| Field | Content |
| --- | --- |
| Preconditions | Fresh disposable runner profile, no key saved; panel open |
| Steps | Send the identical question twice; wait for each no-key result. Read every Conversation item through UI Automation, preserving duplicate items and their order. Inspect the screenshot too. |
| Required result | Both visible You rows have separate accessible items with the same text. Scrolling to the newest row reaches the second item. Never deduplicate the UI Automation results before comparing counts. |
| Known failure | Issue #73: two visible equal rows expose only one automation item. Count this as FAIL. |
| Covers | Conversation screen accessibility and newest-row scrolling; complements j2 and j18 |
| Evidence | Ordered raw automation items, item identities/counts, scroll observation and panel screenshot. Existing explorer proof: [run 43469967627](https://github.com/vikas53953/bluey/actions/runs/43469967627), `b01-wrong-key.txt` and its capture index; current-head execution **UNVERIFIED**. |
| Live / mock | No AI required; repeat with real-provider duplicate answers only as separate live coverage |

These bug journeys add required outcomes; they do not claim the bugs are fixed or that this document executed them.

#### j22 — Mixed chord and panel Talk must preserve microphone privacy

| Field | Content |
| --- | --- |
| Preconditions | Disposable hosted Windows VM only; real OpenAI; working audio input as j9. With no input device, BLOCKED. Bound each variant to one attempt, then global Stop in cleanup. |
| Steps | Hold Ctrl+Alt+Space; click panel Talk twice without releasing the chord. Wait until the second connection completes and Talk reads Stop talk, then release the chord. Separately repeat with Send then Talk during the hold, and with the 120-second hold timeout instead of release. Keep pointer away from the notch when observing the result. |
| Required privacy outcome | Whenever any microphone is recording, the Listening indicator stays visible (or Capture if that higher-priority activity is active). Releasing the hold or global Stop must stop the relevant recording; a hidden face must never coexist with active recording. Verify device/stream lifetime independently of the face and button label. |
| Known failure | Map §5-F10, code reading only: the Talk-button microphone can survive release/timeout while StopPushToTalkMic clears the shared indicator. This is a required FAIL if reproduced, never accepted current behavior. No live reproduction is claimed here. |
| Evidence | Timestamped chord/Talk/connect/release events, face observations and Boo-window screenshots; independent audio-device/stream lifetime evidence. A Stop talk label alone does not prove recording. If lifetime cannot be observed, mark the privacy assertion UNVERIFIED rather than pass. Global Stop and confirm cleanup after each variant. |
| Covers | §5-F10, INV-PRIV-1, INV-TALK-1; H4/H8 and mixed ownership beyond j9/j11 |
| Live / mock | Live only, microphone-dependent; execution and independent stream observer are **UNVERIFIED** |

## 3. Transition coverage

Every row of the map, and the journey(s) that drive it. **manual** = a person must do it on a throwaway VM (or it
needs a device change the runner cannot make); **not drivable yet** = no user action reaches it today, with the reason.

| Rows | Journeys | Notes |
| --- | --- | --- |
| R1, R2, R3, R5, R6 | j1, j7 | R2, R5 phase timings are inferred from face timings |
| R4, R7, R8, R10 | j7 | |
| R9, R11 | j2, j6, j13 | |
| F1, F2 | j1, j7 | |
| F3 (speech-off branch) | j2 | |
| F3 (voice hand-off branch) | j8 | |
| F4, F5 | j2, j3, j4 | |
| F6 | j12a, j18 | |
| F7, F8, F9 | j8, j9 | |
| F10 (voice disconnect / error) | **manual** | needs the network or the provider to drop mid-session; the runner cannot cut it on cue |
| F10b | j17 | |
| F11 | j8 b) | AI-dependent tool call |
| F12, F13 | j9, j10, j11, j12c | mic device needed |
| F14, F15 | j2, j6, j8 b), j14 | |
| F16, F17 | j14 | |
| F18 | j6, j13 | |
| F19 (Pause, Stop, 5 min break) | j12f, j13, j14 | |
| F19 (sleep gap, tick error, provider disconnect) | **manual** / **not drivable yet** | sleep: the VM cannot suspend on cue; tick error: no user action throws in the tick; disconnect: as F10 |
| F20 | j13 | |
| F21 (Ctrl+Alt+F12, Ctrl+Alt+S, tray Stop, panel Stop, right-click notch) | j12 | |
| F21 (lock / logoff, sleep, update restart, quit, crash handlers) | **manual** | lock/sleep need the session or power state changed; update restart needs a published update; quit is covered by §5-F9 note in j13 (manual check that a paused session stays unended); crash handlers need a crash |
| F21 (internal: selected display gone, annotation window cannot be hidden from capture, colour chooser opened while active) | **manual** | display gone needs a monitor unplugged (one display on the runner); the capture-exclusion failure is an OS condition; the colour-chooser case is not scripted yet |
| F22 | j0 | |
| F23, F24 | j10 | |
| H1, H3, H4, H5, H8 | j9 | |
| H2, H7 | j10 | |
| H6 (Stop) | j12c | |
| H6 (session lock, suspend) | **manual** | as F21 lock/sleep |
| Fo1 | j6, j13, j17 | |
| Fo2 | j13 e) | |
| Fo3 (Pause, Stop, 5 min break, voice) | j12f, j13, j14, j17 (j11 b: refusal) | |
| Fo3 (sleep gap, tick error, provider disconnect) | **manual** / **not drivable yet** | as F19 |
| Fo4, Fo6 | j13, j17 | |
| Fo5, Fo7, Fo8 | j6 (Fo7 also j13) | |
| G1, G4 | j2, j15, j18 | |
| G2 | j12 | |
| G3 | j11, j17 | |
| G5 | **not drivable yet** | the commit-under-lock ordering is internal; no user action can show Stop and a commit interleaving; reducer/unit tests only |
| A1, A2, A3, A5, A6, A7, A8 | j5, j12e, j15, j16 | A7 depends on the AI proposing two steps |
| A4 (expired / policy denies / target window changed) | **manual** (the "policy denies" part may be drivable by asking for a write outside Boo's file folder; UNVERIFIED) | Stop clears the card first (A3), so "expired" is not reachable by a click; a changed target window needs a second app moved in between; not scripted yet |
| U1, U3 | j5, j16 | |
| U2, U5, U6 | j16 | |
| U4 | **manual / UNVERIFIED** | j16's separate refusal checklist: newer field change, ambiguous Undo controls, empty prior value; the latter two need additional target fixtures. Successful/repeated undo is not refusal coverage. |
| P1, P2 | **not drivable through the current UI / UNVERIFIED** | P1 needs an older/equal generation passed to PointerGuidance.Begin; P2 needs Begin while an annotation already exists. App BeginRequest clears annotations first (MainWindow.xaml.cs:1986); j18 cannot prove either core branch. Require targeted Core tests as component evidence, not E2E proof. |
| P3 | j3, j14 | the phase walk itself is not published (only the drawn result) |
| P4 (Stop, new request) | j12h, j18 | |
| P4 (scroll, resize, foreground, display change) | **manual** | not scripted yet; display change needs a second monitor |

Invariants and findings:

| Item | Journeys | Today |
| --- | --- | --- |
| `INV-PRIV-1` | j9, j10, j11, j22 (Listening), j2, j6 (Capture) | Required: visible privacy indicator while a device is active. §5-F10 documents a code-reading gap; j22 must test it. Not an unconditional current-behavior claim. |
| `INV-PRIV-2` | j6, j14 (focus check reveals the notch) | whether the notch is on screen at the moment of the grab is UNVERIFIED (timing); §5-F5 expect today's behaviour |
| `INV-APPLY-1`, `-2`, `-3` | j5, j15, j16 | `-2` fails for files today (§5-F6) |
| `INV-FOCUS-1` | j13 | §5-F9 |
| `INV-TALK-1` | j9, j12c, j22 | §5-F10: mixed Talk/chord microphone can survive release; required failure if reproduced |
| `INV-STOP-1` | j12 | §5-F4, and Capture may outlast Stop (j12d) |
| `LIVE-1` | j2 (Responding ends), j8 (Thinking with voice) | a silent connected provider is **not drivable yet** (needs a provider that never answers; the mock has no voice) |
| `LIVE-2` | j1, j7 | |
| §5-F1 (`PresenceController` unused), §5-F2 (`UserHidden` never set) | none | **not drivable**: no caller sets them; reducer/unit tests only |
| §5-F3 (no Awake) | j0 | |
| §5-F4 | j12f, j12g | |
| §5-F5 | j6, j14 | |
| §5-F6 | j16 | |
| §5-F7 | j8 | silent-provider part not drivable yet |
| §5-F8 | j9, j10, j11, j17 | |
| §5-F9 | j13 e); quit part manual | |
| §5-F10 | j22 | Microphone privacy gap, code reading only; live execution UNVERIFIED |

Count: the map has **75 rows** (R 11, F 25 including F10b, H 8, Fo 8, G 5, A 8, U 6, P 4). Every row is catalogued;
that is not a claim that every row has an executable E2E journey. U4 refusal cases remain manual/UNVERIFIED.
Rows or events that **no** journey can drive from the UI today: **P1/P2**, **F10**, the sleep /
tick-error / disconnect events of **F19** and **Fo3**, the lock / sleep / update / quit / crash / internal sources of
**F21**, the lock / suspend events of **H6**, **G5**, **A4**, and the scroll / resize / foreground / display events of
**P4**: these are **manual** or **not drivable yet** as listed above, and need reducer tests (PR 2.11) to be covered at
all. Voice journeys (j8–j11, j12a–c, j17) need the realtime model, and the ones that listen need an audio input device
on the runner; without one they are **BLOCKED**, never green.

---

## 4. How to reuse this list

- **Refer to journeys by id** (`j0` … `j22`, sub-steps like `j12f`). Ids are stable: a journey that is dropped keeps its
  id retired; a new journey takes the next free number. Change a step here, in the same PR, when the behaviour it
  checks changes on purpose.
- **Full-app QA suite** (branch `qa/full-app-suite`, PR 2.12 "Phase 2 gate"): run every journey; report one evidence row
  per journey id with its label (live / mock / manual / UNVERIFIED / BLOCKED) and run link.
- **PR #45** (plan rows 2.11 "every notch state tested" and 2.12): the state table (section 1) and the transition table
  (section 3) are the checklist for "no notch state left UNVERIFIED". `docs/FEATURE_MAP.md` rows can cite a journey id.
- **PR #48** (pending `FaceTimeline.cs`): each journey's face sequence is what its planned `jN-*-face-timeline.txt`
  should show once the recorder lands; action marks and bounded face waits are requirements, not current APIs. A face shorter than
  one reading (about 25–35 ms) can be missed, so a missing very short face (for example the tap in j9 b) is not a
  failure on its own.
- **Steps marked "expect today's behaviour"** check today's code and name the finding. They are not a verdict on the
  plan's ideal.
- This file claims nothing was run. Evidence lives in each run's artifacts and PR body, never here.
